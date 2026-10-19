using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЖИВАЯ ПРОБА УМЕНИЙ: «ТОТ ЖЕ СЕРВЕР ИЛИ НОВЫЙ» РЕШАЕТСЯ ПРОЦЕССОМ (задача F-2, слово владельца
/// 20.10.2026).
///
/// **От какого случая.** После перезапуска сервера карточка показывала старую живую пробу как
/// «проверено … смотрено 23:15» — это про ДРУГОЙ сервер (0.1.5), а стоял уже 0.2.1-rc.2.
/// Прежний ключ был «порт + ДЛИНЫ cookie и ссылки входа»: у перезапущенного сервера те же порт
/// и те же длины — ключ совпадал, и проба заново не шла. Комментарий рядом обещал «порт + номер
/// процесса», код считал длины: проверка ниже и есть та, которой не хватало.
///
/// **Что проверяется, а чего здесь нет.** Здесь нет ни сети, ни движка: дверь измерения подставная
/// (<see cref="Stand.Measure"/>), и она отвечает заранее заданными фактами. Проверяется РЕШЕНИЕ —
/// повторить пробу или нет — и то, что факты и момент пробы при повторе обновляются, а при отказе
/// старые не выдаются за новые. Подсказки о версиях (файл) в пробе не участвуют ВООБЩЕ: факты
/// приходят только из двери измерения.
///
/// **Мутации, которые обязаны упасть:**
/// * «вернуть ключ по длинам» → <see cref="После_перезапуска_сервера_проба_идёт_заново"/> красная
///   (ключ совпадёт, проба не повторится);
/// * «убрать сравнение процесса» (ключ = порт) → та же проверка красная;
/// * «молчать, когда процесс не назван» → <see cref="Процесс_не_назван_говорим_словами_и_ключ_по_ссылке"/>
///   красная (нет строки в журнале);
/// * «подменить факты пробы подсказками/умолчанием» → обе красные (версия и момент перестанут
///   приходить из двери измерения).
/// </summary>
public sealed class LiveSkillProbeTests
{
    private static readonly AppPaths Paths = AppPaths.Under(@"C:\Проба\панель");

    /// <summary>Подставной стенд: цель, ответ двери измерения, журнал и счётчик проб.</summary>
    private sealed class Stand
    {
        private readonly object _gate = new();
        private readonly List<SkillTarget> _calls = new();
        private readonly List<string> _logs = new();

        public Stand()
        {
            Probe = new LiveSkillProbe(Paths, () => Target, () => @"C:\Проба\панель\engine\0.2.1-rc.2\bin.js", Log, Measure);
        }

        public LiveSkillProbe Probe { get; }

        /// <summary>Куда смотрит проба прямо сейчас (проверка меняет это «перезапуском сервера»).</summary>
        public SkillTarget? Target { get; set; }

        /// <summary>Что ответит дверь измерения. <c>null</c> — проба не дошла до вердиктов.</summary>
        public SkillFacts? Answer { get; set; } = new() { Probed = true, EngineVersion = "0.1.5-rc.2" };

        public int Calls
        {
            get
            {
                lock (_gate) return _calls.Count;
            }
        }

        public List<string> Logs
        {
            get
            {
                lock (_gate) return _logs.ToList();
            }
        }

        private void Log(string line)
        {
            lock (_gate) _logs.Add(line);
        }

        private SkillFacts? Measure(SkillTarget target, string? bin)
        {
            lock (_gate)
            {
                _calls.Add(target);
                return Answer;
            }
        }

        /// <summary>
        /// Дождаться, что проба прошла `count` раз И ЗАКОНЧИЛАСЬ. Одного счётчика мало: заказ,
        /// пришедший во время пробы, отбрасывается (по делу), и проверка, ждущая только счётчик,
        /// ловила бы гонку — второй заказ попадал в ещё идущую первую пробу.
        /// </summary>
        public bool WaitCalls(int count) =>
            SpinWait.SpinUntil(() => Calls >= count && !Probe.Running, TimeSpan.FromSeconds(10));
    }

    private static SkillTarget Target(int port, string link, int pid, long ticks) =>
        new($"http://127.0.0.1:{port}", string.Empty, port, link, pid, ticks);

    /// <summary>
    /// ПЕРЕЗАПУСК СЕРВЕРА — ПРОБА ИДЁТ ЗАНОВО, даже когда порт и ссылка ТЕ ЖЕ САМЫЕ (у перезапуска
    /// меняется процесс, а не длины). Факты и момент пробы — новые; старая проба не выдаётся за новую.
    ///
    /// **Мутации, которые обязаны упасть:** «ключ по длинам» (как было) и «ключ = порт».
    /// </summary>
    [Fact]
    public void После_перезапуска_сервера_проба_идёт_заново()
    {
        var stand = new Stand();

        // ССЫЛКА И ПОРТ ОДНИ И ТЕ ЖЕ у обоих «серверов» — нарочно: ключом обязан быть процесс.
        var link = "http://127.0.0.1:3097/?token=aaa";
        var first = Target(3097, link, pid: 100, ticks: 1_000);

        stand.Target = first;
        stand.Answer = new SkillFacts { Probed = true, EngineVersion = "0.1.5-rc.2" };

        stand.Probe.Ask();
        Assert.True(stand.WaitCalls(1), "проба не прошла вовсе");

        var firstMoment = stand.Probe.Facts.MeasuredAt;
        Assert.NotNull(firstMoment);
        Assert.Equal("0.1.5-rc.2", stand.Probe.Facts.EngineVersion);

        // ТОТ ЖЕ сервер: перерисовка окна зовёт заказ каждую секунду — второй пробы быть не должно.
        stand.Probe.Ask();
        Thread.Sleep(150);
        Assert.Equal(1, stand.Calls);

        // ПЕРЕЗАПУСК: тот же порт, та же ссылка (даже токен тот же) — другой процесс.
        stand.Target = first with { Pid = 200, StartTicks = 2_000 };
        stand.Answer = new SkillFacts { Probed = true, EngineVersion = "0.2.1-rc.2" };

        stand.Probe.Ask();
        Assert.True(
            stand.WaitCalls(2),
            "после перезапуска сервера проба НЕ повторилась — ключ не увидел новый процесс");

        Assert.Equal("0.2.1-rc.2", stand.Probe.Facts.EngineVersion);
        Assert.True(
            stand.Probe.Facts.MeasuredAt >= firstMoment,
            $"момент пробы не обновился: было {firstMoment:O}, стало {stand.Probe.Facts.MeasuredAt:O}");
    }

    /// <summary>
    /// ТОТ ЖЕ НОМЕР ПРОЦЕССА С ДРУГИМ ВРЕМЕНЕМ СТАРТА — ЭТО НОВЫЙ СЕРВЕР. Номера процессов
    /// переиспользуются (урок v1: панель уже гасила чужой процесс по совпадению номера), поэтому
    /// ключ обязан быть ПАРОЙ «PID + время старта», а не одним номером.
    ///
    /// **Мутация, которая обязана упасть:** «ключ = только PID» (время старта выброшено).
    /// </summary>
    [Fact]
    public void Тот_же_номер_процесса_с_другим_временем_старта_это_новый_сервер()
    {
        var stand = new Stand();
        var link = "http://127.0.0.1:3097/?token=aaa";
        var first = Target(3097, link, pid: 100, ticks: 1_000);

        stand.Target = first;
        stand.Probe.Ask();
        Assert.True(stand.WaitCalls(1));

        // Номер тот же, время старта другое — номер достался другой программе.
        stand.Target = first with { StartTicks = 2_000 };
        stand.Probe.Ask();

        Assert.True(
            stand.WaitCalls(2),
            "ключ из одного PID: переиспользованный номер не отличить от прежнего сервера");
    }

    /// <summary>
    /// ПРОЦЕСС НЕ НАЗВАН — ЭТО НАЗЫВАЕТСЯ СЛОВАМИ, а ключом становится САМА ССЫЛКА ВХОДА: та же
    /// длина, другой токен — проба повторяется (длины ключом быть не могут, на них панель и попалась).
    ///
    /// **Мутации, которые обязаны упасть:** «молчать, когда процесс не назван» (нет строки в журнале)
    /// и «вернуть ключ по длинам» (второй пробы не будет — длины-то равны).
    /// </summary>
    [Fact]
    public void Процесс_не_назван_говорим_словами_и_ключ_по_ссылке()
    {
        var stand = new Stand();

        stand.Target = Target(3097, "http://127.0.0.1:3097/?token=aaa", pid: 0, ticks: 0);
        stand.Probe.Ask();
        Assert.True(stand.WaitCalls(1));

        // СКАЗАНО СЛОВАМИ — и ровно один раз на ключ, а не каждую перерисовку окна.
        Assert.Contains(PanelStrings.EnvSkillsProbeNoProcessLog, stand.Logs);
        stand.Probe.Ask();
        Thread.Sleep(150);
        Assert.Equal(1, stand.Logs.Count(line => line == PanelStrings.EnvSkillsProbeNoProcessLog));
        Assert.Equal(1, stand.Calls);

        // «ПЕРЕЗАПУСК БЕЗ ЗАПИСИ»: ДЛИНА ССЫЛКИ ТА ЖЕ (token=bbb вместо token=aaa), токен другой.
        stand.Target = Target(3097, "http://127.0.0.1:3097/?token=bbb", pid: 0, ticks: 0);
        stand.Probe.Ask();

        Assert.True(
            stand.WaitCalls(2),
            "ключ по ссылке не изменился при том же порте и той же длине — проба не повторилась");
    }

    /// <summary>
    /// ОТКАЗ ПРОБЫ НЕ ВЫДАЁТ СТАРЫЕ ФАКТЫ ЗА НОВЫЕ: факты и их момент остаются прежними, в журнале
    /// сказано, что проба не дошла до вердиктов. Подсказок о версиях здесь нет вовсе — подменить
    /// факты нечем, и это тоже проверяется: версия приходит только из двери измерения.
    /// </summary>
    [Fact]
    public void Отказ_пробы_не_выдаёт_старые_факты_за_новые()
    {
        var stand = new Stand();

        var first = Target(3097, "http://127.0.0.1:3097/?token=aaa", pid: 100, ticks: 1_000);
        stand.Target = first;
        stand.Answer = new SkillFacts { Probed = true, EngineVersion = "0.1.5-rc.2" };

        stand.Probe.Ask();
        Assert.True(stand.WaitCalls(1));

        var moment = stand.Probe.Facts.MeasuredAt;

        // Перезапуск, но проба до вердиктов не дошла.
        stand.Target = first with { Pid = 200, StartTicks = 2_000 };
        stand.Answer = null;

        stand.Probe.Ask();
        Assert.True(stand.WaitCalls(2));

        // Старые факты не стёрты и не подменены; момент пробы — ПРЕЖНИЙ, а не «сейчас».
        Assert.Equal("0.1.5-rc.2", stand.Probe.Facts.EngineVersion);
        Assert.Equal(moment, stand.Probe.Facts.MeasuredAt);
        Assert.Contains(PanelStrings.EnvSkillsProbeNoFactsLog, stand.Logs);

        // А новая удачная проба их заменяет — и версия снова из двери измерения.
        stand.Answer = new SkillFacts { Probed = true, EngineVersion = "0.2.1-rc.2" };
        stand.Target = first with { Pid = 300, StartTicks = 3_000 };

        stand.Probe.Ask();
        Assert.True(stand.WaitCalls(3));

        Assert.Equal("0.2.1-rc.2", stand.Probe.Facts.EngineVersion);
        Assert.True(stand.Probe.Facts.MeasuredAt >= moment);
    }
}
