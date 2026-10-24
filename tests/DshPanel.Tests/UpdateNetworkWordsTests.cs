using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DshPanel.Shell;
using DshPanel.Update;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СЕТЕВАЯ ОШИБКА — СЛОВАМИ ЧЕЛОВЕКА, А НЕ ТЕХСТРОКОЙ (u2, DEF-007).
///
/// До 2.4.0 отказ сети попадал в строку раздела «Обновление» как есть: `HttpRequestException:
/// The remote name could not be resolved` (`UpdateClient.cs:227`). Человек читал имя типа
/// исключения — то есть не читал ничего. Правило банка (`70-writing.md` §1, `60-states.md` §3):
/// причина + что сделать; технический след — только в журнал.
///
/// **Что доказывается прогоном и что может упасть.**
///
/// 1. **Человеку — слова:** <see cref="UpdateRelease.Error"/> равен словарной строке
///    `UpdateNetworkError` и НЕ содержит ни имени типа исключения, ни адреса запроса.
///    Мутация «вернуть техстроку» (`Fail($"{error.GetType().Name}: {error.Message}", now)`)
///    роняет и то, и другое.
/// 2. **Журналу — след:** техническая подробность не выброшена, она в
///    <see cref="UpdateRelease.Technical"/> и в строке журнала (`UpdateNetworkErrorLogFormat`).
///    Иначе «нет связи» стало бы неразбираемым: DNS это было, TLS или таймаут.
/// 3. **Чужой отказ не переодевается:** HTTP-код по-прежнему называется своим форматом
///    (`UpdateHttpFailedFormat`), а не «нет связи»: разные причины — разные слова.
///
/// ⚠️ Живого GitHub здесь нет и быть не может: подставляется обработчик, который бросает ровно
/// то исключение, какое приходит от сети. Ни одного запроса наружу.
/// </summary>
public class UpdateNetworkWordsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 3, 0, 0, TimeSpan.Zero);

    /// <summary>Обработчик-обрыв: сеть не ответила вовсе — то же, что видит панель без связи.</summary>
    private sealed class Broken : HttpMessageHandler
    {
        private readonly Exception _error;

        public Broken(Exception error) => _error = error;

        // ⚠️ Панель ходит в сеть СИНХРОННО (`HttpClient.Send`, так устроен `HttpUpdateClient`),
        // а базовый обработчик синхронный путь не поддерживает вовсе (`NotSupportedException`).
        // Поэтому обрыв подставляется в оба пути: иначе проверка мерила бы не отказ сети,
        // а «синхронный вызов не поддержан» — и зеленела бы на исправном коде и на сломанном.
        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken) => throw _error;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => throw _error;
    }

    /// <summary>Обработчик-ответ: сервер ответил, но отказом.</summary>
    private sealed class Answering : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public Answering(HttpStatusCode status) => _status = status;

        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(_status);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status));
    }

    private static UpdateRelease Check(HttpMessageHandler handler) =>
        new HttpUpdateClient(new HttpClient(handler)).Latest("ru", Now);

    /// <summary>
    /// СЕТЬ НЕ ОТВЕТИЛА — ЧЕЛОВЕК ЧИТАЕТ «НЕТ СВЯЗИ», А НЕ ИМЯ ИСКЛЮЧЕНИЯ.
    ///
    /// Исключение нарочно с адресом и текстом, как настоящее: если код снова начнёт отдавать
    /// техстроку, проверка увидит и «Exception», и адрес.
    /// </summary>
    [Fact]
    public void Отказ_сети_назван_словами_и_не_содержит_техстроки()
    {
        var result = Check(new Broken(new HttpRequestException(
            "api.github.com: The remote name could not be resolved")));

        Assert.False(result.Ok);

        // СЛОВА ЧЕЛОВЕКУ: ровно словарная строка «что случилось + что сделать».
        Assert.Equal(PanelStrings.UpdateNetworkError, result.Error);
        Assert.Contains("связи", result.Error, StringComparison.OrdinalIgnoreCase);

        // НИ СЛЕДА ТЕХСТРОКИ в том, что читает человек.
        Assert.DoesNotContain("Exception", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api.github.com", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resolved", result.Error, StringComparison.OrdinalIgnoreCase);

        // СЛЕД ЖУРНАЛУ: тип и сообщение сохранены — по ним разбирают, что порвалось.
        Assert.Contains("HttpRequestException", result.Technical, StringComparison.Ordinal);
        Assert.Contains("resolved", result.Technical, StringComparison.Ordinal);
    }

    /// <summary>
    /// ЖУРНАЛ НАЗЫВАЕТ СЛЕД, А НЕ ТОЛЬКО «НЕТ СВЯЗИ»: строка журнала собирается форматом
    /// `UpdateNetworkErrorLogFormat` и содержит техническую подробность. Без неё разбор
    /// «почему панель не проверила обновление» неотличим от гадания.
    /// </summary>
    [Fact]
    public void Журнал_называет_технический_след_отказа_сети()
    {
        var result = Check(new Broken(new HttpRequestException("connection refused")));

        var journal = UpdateController.FailureJournal(result);
        var expected = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.UpdateNetworkErrorLogFormat, result.Technical);

        Assert.Equal(expected, journal);
        Assert.Contains("HttpRequestException", journal, StringComparison.Ordinal);
        Assert.Contains("connection refused", journal, StringComparison.Ordinal);
    }

    /// <summary>
    /// ЧУЖОЙ ОТКАЗ НЕ ПЕРЕОДЕВАЕТСЯ В «НЕТ СВЯЗИ»: HTTP-код называется своим форматом, и следа
    /// у него нет (технической подробности здесь неоткуда взяться). Журнал получает ту же
    /// человеческую причину — другой у этого отказа нет.
    /// </summary>
    [Fact]
    public void Отказ_по_коду_называется_своим_форматом_а_не_нетом_связи()
    {
        var result = Check(new Answering(HttpStatusCode.InternalServerError));

        Assert.False(result.Ok);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateHttpFailedFormat, 500),
            result.Error);
        Assert.NotEqual(PanelStrings.UpdateNetworkError, result.Error);
        Assert.Equal(string.Empty, result.Technical);
        Assert.Equal(result.Error, UpdateController.FailureJournal(result));
    }

    /// <summary>
    /// УДАЧНАЯ ПРОВЕРКА ЖУРНАЛУ ДАЁТ ВЕРСИЮ, А НЕ ПРИЧИНУ: у отказа и удачи — одна строка
    /// журнала (`UpdateLogFormat`), и подставлять в неё пустоту значило бы терять факт.
    /// </summary>
    [Fact]
    public void Удачная_проверка_в_журнале_называет_версию()
    {
        var ok = UpdateRelease.NotRequested() with { Ok = true, Latest = "2.4.0" };

        Assert.Equal("2.4.0", UpdateController.FailureJournal(ok));
    }
}
