using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using DshPanel.Shell;

namespace DshPanel.Update;

/// <summary>
/// ЧТО ПАНЕЛЬ УЗНАЛА О ПОСЛЕДНЕМ ВЫПУСКЕ. Запись, а не класс: состояние показывается в окне
/// и печатается в отчёте проверки целиком.
///
/// <see cref="Notes"/> — УЖЕ ВЫБРАННЫЙ блок на языке панели (см.
/// <see cref="UpdateDecisions.PickNotes"/>), а <see cref="NotesRaw"/> — тело выпуска как оно
/// пришло. Сырое тело хранится рядом намеренно: **язык панели меняется**, и заметки обязаны
/// пересобраться на новом языке, а не ждать следующей проверки (так же устроена панель 1.x).
/// </summary>
public sealed record UpdateRelease(
    bool Ok,
    string Error,
    string Latest,
    string PageUrl,
    string Published,
    string Notes,
    string NotesRaw,
    DateTimeOffset CheckedAt)
{
    /// <summary>
    /// ВЛОЖЕНИЯ ВЫПУСКА — архив панели, установщик и файл контрольных сумм. Нужны кнопке
    /// «Скачать и подготовить»: без адресов готовить нечего.
    ///
    /// ⚠️ Отдельным свойством, а не ещё одним полем записи, и это осознанно: вложения читает
    /// ТОЛЬКО разбор ответа GitHub (<see cref="HttpUpdateClient.Parse"/> — одна строка
    /// <see cref="UpdateAssets.Pick"/>), а все остальные места собирают запись про версию
    /// и заметки. Поле в записи заставило бы каждое такое место называть вложения, которых
    /// у него нет, — и однажды там оказался бы чужой адрес.
    ///
    /// Следы в настройках вложения НЕ хранят: адрес вложения живёт один выпуск, и после
    /// перезапуска панели его узнают новой проверкой. Поэтому окно и говорит «проверьте ещё раз»
    /// вместо того, чтобы готовить замену вслепую.
    /// </summary>
    public ReleaseAssets Assets { get; init; } = ReleaseAssets.None;

    /// <summary>Выпусков ещё не спрашивали (или спрашивать нельзя в этом прогоне).</summary>
    public static UpdateRelease NotRequested() =>
        new(false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, default);

    /// <summary>Спросили — и не получилось. Причина называется словами, а не кодом.</summary>
    public static UpdateRelease Failed(string error, DateTimeOffset at) =>
        new(false, error, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, at);
}

/// <summary>
/// СЛЕДЫ ПРОВЕРКИ ОБНОВЛЕНИЯ одной записью — то, что панель узнала о последнем выпуске
/// и что обязано пережить перезапуск.
///
/// **Почему записью, а не пятью аргументами.** У этих полей один смысл — «что вышло из последней
/// проверки», — и записаться они обязаны ВМЕСТЕ: записанные по одному, они однажды разошлись бы,
/// и человек увидел бы «доступна версия 2.1.0» с заметками от 2.0.0.
///
/// Живёт здесь, а не в настройках, потому что рождается в проверке: настройки — лишь место,
/// где след лежит (<see cref="Settings.PanelSettings.UpdateLatest"/> и соседи).
/// </summary>
public readonly record struct UpdateTraces(
    string Latest,
    string Published,
    string PageUrl,
    string Notes,
    string NotesRaw);

/// <summary>
/// ОТКУДА ПАНЕЛЬ УЗНАЁТ О ВЫПУСКАХ. Отдельный уговор — чтобы окно и решения проверялись
/// БЕЗ СЕТИ: проверка подставляет свой ответ, а живой запрос делается только обычным запуском
/// панели человеком.
///
/// ⚠️ **Сети в проверках не бывает вовсе.** Это то же правило, по которому устроены баланс
/// и страница цен: прогон проверки не имеет права ходить в интернет от имени владельца.
/// </summary>
public interface IUpdateClient
{
    UpdateRelease Latest(string language, DateTimeOffset now);
}

/// <summary>
/// ВЫПУСКИ ПО HTTP — одна дверь в сеть за выпусками панели.
///
/// Устроено как <c>Pricing\HttpPricingClient</c> и по той же причине: живой путь отделён
/// уговором (<see cref="IUpdateClient"/>), поэтому проверки подставляют готовый ответ
/// и в сеть не ходят вовсе, а панель ходит ровно там, где имеет право.
///
/// **Три правила GitHub, и все три — не украшение.**
///
/// 1. **<c>User-Agent</c> обязателен.** Без него GitHub отвечает 403 на любой запрос, и панель
///    показывала бы отказ вместо выпуска.
/// 2. **<c>Accept: application/vnd.github+json</c>** — иначе ответ приходит в разметке,
///    и разбор JSON падает.
/// 3. **403 и 429 — это лимит, а не поломка.** Без токена GitHub даёт 60 запросов в час
///    с одного адреса; сказать человеку «HTTP 403» значит не сказать ничего. Причина называется
///    словами (<see cref="PanelStrings.UpdateRateLimited"/>), и это подсказка «повторите позже».
///
/// ⚠️ **Наружу не летит ни одно исключение.** Ошибка сети, отказ GitHub, отсутствие выпуска
/// и битый JSON дают <c>Ok = false</c> с ЧЕЛОВЕЧЕСКОЙ причиной: панель, у которой проверка
/// обновлений валит приложение, — это панель, которую человек больше не запустит.
///
/// ⚠️ Ключа здесь нет вовсе: выпуски публичного репозитория читают без входа, и панель
/// не отправляет в GitHub ни ключа модели, ни ссылки входа.
/// </summary>
public sealed class HttpUpdateClient : IUpdateClient
{
    private readonly HttpClient _http;

    public HttpUpdateClient(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    public UpdateRelease Latest(string language, DateTimeOffset now)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ProductLinks.UpdateApi);
            request.Headers.TryAddWithoutValidation("User-Agent", ProductLinks.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = _http.Send(request);

            // 404 — это не «сломалось», а «выпуска нет»: репозиторий молод, и первый выпуск
            // появится позже. Сказать об этом надо словами, а не кодом.
            if (response.StatusCode == HttpStatusCode.NotFound) return Fail(PanelStrings.UpdateNoRelease, now);

            // 403 и 429 — лимит GitHub на число запросов с адреса (60 в час без токена).
            // Иначе человек видит «HTTP 403» и не понимает, что делать.
            if (response.StatusCode == HttpStatusCode.Forbidden
                || (int)response.StatusCode == 429)
            {
                return Fail(PanelStrings.UpdateRateLimited, now);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Fail(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        PanelStrings.UpdateHttpFailedFormat,
                        (int)response.StatusCode),
                    now);
            }

            using var stream = response.Content.ReadAsStream();
            using var document = JsonDocument.Parse(stream);

            return Parse(document.RootElement, language, now);
        }
        catch (JsonException)
        {
            // Битый JSON приходит и от посредника в сети, и от самого GitHub. Причина названа
            // человеку так, как он может её понять: «ответ не разобрался».
            return Fail(PanelStrings.UpdateBadJson, now);
        }
        catch (Exception error)
        {
            return Fail($"{error.GetType().Name}: {error.Message}", now);
        }
    }

    private static UpdateRelease Fail(string error, DateTimeOffset now) =>
        UpdateRelease.Failed(error, now);

    /// <summary>
    /// Разбор ответа GitHub. Отдельной чистой функцией, а не внутри запроса, — чтобы проверки
    /// гоняли ИМЕННО разбор, подставляя записанный ответ, и ни один живой запрос для этого
    /// не требовался.
    ///
    /// Ни одного обязательного поля здесь нет: чего в ответе нет — то пусто, и это не ошибка.
    /// Единственное, без чего ответ бессмыслен, — тег выпуска: без него сравнивать нечего,
    /// и это честный отказ, а не «выпуск версии ""».
    /// </summary>
    public static UpdateRelease Parse(JsonElement root, string language, DateTimeOffset now)
    {
        var tag = Text(root, "tag_name");
        if (tag.Length == 0) return UpdateRelease.Failed(PanelStrings.UpdateNoTag, now);

        var body = Text(root, "body");

        return new UpdateRelease(
            true,
            string.Empty,
            tag,
            Text(root, "html_url"),
            Text(root, "published_at"),
            UpdateDecisions.PickNotes(body, language),
            body,
            now)
        {
            // Вложения выбираются ТЕМ ЖЕ разбором, что проверен отдельно (`UpdateAssets.Parse`
            // и `Pick`): «кнопка подготовки получает адреса» — это одна строка здесь, а не
            // второй разбор ответа в окне.
            Assets = UpdateAssets.Pick(UpdateAssets.Parse(root)),
        };
    }

    private static string Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim()
            : string.Empty;
}
