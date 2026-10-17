using System;
using System.Collections.Generic;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ССЫЛКА ВХОДА: КОГДА ФАЙЛ ЧИТАЕТСЯ И КОГДА — ПОВТОРНО (цикл V-02 §r4, 17.10.2026).
///
/// **От какого случая.** Защёлка в <see cref="ServerController"/> читала `state\entry-link.txt`
/// **один раз за прогон** и закрывалась, даже если в тот миг файла не было (или он был про другой
/// порт, или чтение не удалось — файл занят). После этого ссылка не появлялась до перезапуска
/// панели: калитка честно спрашивала «есть ли вход» каждую секунду и столько же раз получала
/// «нет». Наружу это те же слова, что владелец видел 14.10.2026, — «связи с сервером DSH нет»
/// при живом своём сервере.
///
/// **Чем это замерено** (подставные файлы, без движка, сети и портов): до правки ссылка,
/// появившаяся после первого чтения, не доезжала **ни разу за 96 обращений и 3 секунды**;
/// в памяти ссылка про другой порт не давала прочитать файл заново **никогда**; а второй
/// спрашивающий (калитка и окно спрашивают ссылку одновременно) получал пустой ответ
/// **в 1 заходе из 30** — редкий флейк сторожевой проверки «вход, появившийся после старта,
/// доезжает до калитки без перезапуска».
///
/// ⚠️ **Чего эти проверки НЕ видят:** самой гонки (запись ссылки под замком) — её окно около
/// микросекунды, и проверка на неё была бы флакующей; поэтому гонка закрыта по построению, а её
/// замер назван в отчёте. Здесь проверяется то, что падает **всегда**: повтор чтения, перечитывание
/// при смене порта и обещание «не читать файл на каждое обращение».
/// </summary>
public sealed class EntryLinkCacheTests
{
    private const int Port = 3097;
    private const int Pid = 4242;
    private const long Ticks = 638_000_000_000_000_000;
    private const string Link = "http://127.0.0.1:3097/?token=0123456789abcdef";
    private const string LinkOfOtherPort = "http://127.0.0.1:3098/?token=old-port";

    /// <summary>
    /// ССЫЛКА, ПОЯВИВШАЯСЯ ПОСЛЕ ПЕРВОГО ЧТЕНИЯ, ДОЕЗЖАЕТ ДО СПРАШИВАЮЩЕГО.
    ///
    /// Мутация, которая обязана упасть здесь: вернуть защёлку «прочитано один раз за прогон»
    /// (второго чтения нет) → ответ остаётся пустым навсегда.
    /// </summary>
    [Fact]
    public void Ссылка_появившаяся_после_первого_чтения_доезжает_до_спрашивающего()
    {
        var files = new Files();
        using var controller = Make(files, entryLinkRetry: TimeSpan.Zero);
        controller.Refresh();

        // Файла ещё нет: движок ссылку не напечатал. Это «нет ссылки», а не «нет входа никогда».
        Assert.Equal(string.Empty, controller.EntryLink);
        Assert.Equal(1, files.Reads);

        // Движок напечатал ссылку — панель записала файл. Панель при этом НЕ перезапускалась.
        files.Entry = Link;

        Assert.Equal(Link, controller.EntryLink);
        Assert.Equal(Link, controller.EntryLink);
        Assert.Equal(2, files.Reads);
    }

    /// <summary>
    /// ССЫЛКА, ЛЕЖАЩАЯ В ПАМЯТИ ПРО ДРУГОЙ ПОРТ, НЕ ЗАПИРАЕТ ФАЙЛ НАВСЕГДА.
    ///
    /// Так бывает: в файле осталась ссылка прежнего запуска (другой порт). Она не годится — но
    /// и запереть чтение она не имеет права: настоящая ссылка появится в том же файле следом.
    /// Мутация: считать «прочитано один раз» и после чтения про чужой порт → ответ пуст навсегда.
    /// </summary>
    [Fact]
    public void Ссылка_про_другой_порт_не_запирает_чтение_файла()
    {
        var files = new Files { Entry = LinkOfOtherPort };
        using var controller = Make(files, entryLinkRetry: TimeSpan.Zero);
        controller.Refresh();

        Assert.Equal(string.Empty, controller.EntryLink);   // про 3098 — чужой порт, не годится
        Assert.Equal(1, files.Reads);

        files.Entry = Link;                                  // настоящая ссылка, порт 3097

        Assert.Equal(Link, controller.EntryLink);
        Assert.Equal(2, files.Reads);
    }

    /// <summary>
    /// ФАЙЛ НЕ ЧИТАЕТСЯ НА КАЖДОЕ ОБРАЩЕНИЕ, КОГДА ССЫЛКА УЖЕ ИЗВЕСТНА.
    ///
    /// Ссылку спрашивают каждую секунду (окно перерисовывается), поэтому «читать всегда» — это
    /// работа ввода-вывода на ровном месте. Мутация: снять проверку «ссылка уже есть» → чтений
    /// станет столько же, сколько обращений.
    /// </summary>
    [Fact]
    public void Файл_не_читается_на_каждое_обращение_когда_ссылка_известна()
    {
        var files = new Files { Entry = Link };
        using var controller = Make(files, entryLinkRetry: TimeSpan.FromHours(1));
        controller.Refresh();

        for (var i = 0; i < 100; i++) Assert.Equal(Link, controller.EntryLink);

        Assert.Equal(1, files.Reads);
    }

    /// <summary>
    /// ПОКА ССЫЛКИ НЕТ, ФАЙЛ НЕ ДОЛБИТСЯ НА КАЖДОЕ ОБРАЩЕНИЕ: повтор — по такту, а не по вызову.
    ///
    /// Это цена исправления: повтор чтения мог бы стать «читаем на каждый вызов». Такт назван
    /// параметром <c>entryLinkRetry</c> (по умолчанию 500 мс); здесь он час — тогда любое второе
    /// чтение видно счётом. Мутация: убрать такт (читать всегда) → чтений будет 50 вместо 1.
    /// </summary>
    [Fact]
    public void Пока_ссылки_нет_файл_читается_по_такту_а_не_на_каждый_вызов()
    {
        var files = new Files();
        using var controller = Make(files, entryLinkRetry: TimeSpan.FromHours(1));
        controller.Refresh();

        for (var i = 0; i < 50; i++) Assert.Equal(string.Empty, controller.EntryLink);

        Assert.Equal(1, files.Reads);
    }

    // ---------------------------------------------------------------- подставное окружение

    /// <summary>
    /// Файлы состояния подставные (как в <c>OwnServerTests</c> и <c>PhoneGateTests</c>): контроллер
    /// не знает путей, поэтому ни `entry-link.txt`, ни `own-server.txt` владельца эта проверка
    /// не читает и не пишет.
    /// </summary>
    private sealed class Files
    {
        public string Entry = string.Empty;
        public string Own = new OwnServerRecord(Port, Pid, Ticks).Format();
        public int Reads;

        public ServerStateFiles AsStateFiles() => new(
            ReadEntryLink: () => { Reads++; return Entry; },
            WriteEntryLink: url => Entry = url,
            ClearEntryLink: () => Entry = string.Empty,
            ReadOwnServer: () => Own,
            WriteOwnServer: text => Own = text,
            ClearOwnServer: () => Own = string.Empty);
    }

    /// <summary>
    /// Контроллер с УЖЕ УЗНАННЫМ своим сервером: запись `own-server.txt` и слушатель на её порту
    /// дают `Owner == Panel` — без этого ссылка не спрашивается вовсе (и это правильно: у чужого
    /// сервера своей ссылки не бывает).
    /// </summary>
    private static ServerController Make(Files files, TimeSpan entryLinkRetry)
    {
        var probe = new ServerProbe(
            ListenerPid: _ => Pid,
            Listeners: () => new[] { new PortTable.Listener(Port, Pid) },
            CommandLine: _ => string.Empty,
            ProcessName: _ => "node",
            StartTicks: _ => Ticks,
            AnswersFingerprint: _ => true);

        return new ServerController(
            @"C:\Temp\entry-link-cache-home",
            () => @"C:\Temp",
            _ => { },
            mayOccupyOwnerPort: false,
            locateEngine: () => null,
            killer: new SilentKiller(),
            probe: probe,
            stopWait: TimeSpan.Zero,
            scanInterval: TimeSpan.Zero,
            files: files.AsStateFiles(),
            ownEngineDir: () => @"C:\Temp\entry-link-cache-home\engine",
            entryLinkRetry: entryLinkRetry);
    }

    /// <summary>Убийца, который ничего не убивает: в этих проверках гасить нечего.</summary>
    private sealed class SilentKiller : IProcessKiller
    {
        public bool Kill(int pid, long startedAtTicks, out string error)
        {
            error = string.Empty;
            return true;
        }
    }
}
