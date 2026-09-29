using System;
using System.Linq;
using DshPanel.Backup;
using DshPanel.Headless;
using DshPanel.Isolation;
using DshPanel.Restore;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// РЕЖИМЫ БЕЗ ОКНА — разбор и решения `--backup &lt;папка&gt;` и `--restore &lt;архив&gt;`.
///
/// Эти проверки появились вместе с самими режимами и сторожат ровно то, ради чего они сделаны:
/// копию и накат можно запустить скриптом и **только в изоляции** (красная линия 5), состав копии
/// берётся из настроек, а согласия наката по умолчанию ВЫКЛЮЧЕНЫ. Ни диска, ни сети, ни окна:
/// точка входа (`Program.Main`) тестам недоступна — поэтому решения обязаны жить вне неё, и это
/// и есть причина, по которой они вынесены в <see cref="HeadlessDecisions"/>.
/// </summary>
public class HeadlessModesTests
{
    private const string Archive = @"C:\Temp\Копии\dsh2-backup-2026-09-26-120000-full.zip";
    private const string Folder = @"C:\Temp\Копии";

    // --- право прогона --------------------------------------------------------

    /// <summary>
    /// Право снимать копию и раскладывать её даёт ИЗОЛЯЦИЯ (или человек, открывший панель).
    /// Обычный прогон без корня — отказ, и это не «осторожность»: без корня каталоги берутся
    /// у владельца, и копия легла бы в его же данные.
    /// </summary>
    [Fact]
    public void Право_на_копию_и_накат_даёт_изоляция_или_человек()
    {
        var owner = RunContext.Create(RunRequest.Owner());
        var isolated = RunContext.Create(RunRequest.Parse(
            new[] { "--run-root", @"C:\Temp\dsh-headless-probe" }, _ => null));

        Assert.False(HeadlessDecisions.HasRight(owner, humanLaunch: false));
        Assert.True(HeadlessDecisions.HasRight(isolated, humanLaunch: false));
        Assert.True(HeadlessDecisions.HasRight(owner, humanLaunch: true));

        // Отказ обязан называть и причину, и выход: иначе его нечем выполнить.
        var refusal = HeadlessDecisions.IsolationRefusal(HeadlessMode.Backup);
        Assert.Contains("ОТКАЗ", refusal, StringComparison.Ordinal);
        Assert.Contains(RunRequest.RootSwitchArgument, refusal, StringComparison.Ordinal);
        Assert.Contains(RunRequest.RootVariable, refusal, StringComparison.Ordinal);
        Assert.Contains("изолированном", refusal, StringComparison.Ordinal);
    }

    // --- разбор аргументов ----------------------------------------------------

    /// <summary>
    /// Ключ без обязательного пути отвергается ВНЯТНО — как «--shot» без имени файла, а не как
    /// «неизвестный ключ»: ключ известный, аргумента нет. Текст отказа обязан быть ОДИН на всю
    /// панель, поэтому разбор режима и общий рубеж отказов отвечают одинаково.
    /// </summary>
    [Fact]
    public void Ключ_без_пути_отвергается_внятно_и_одинаково()
    {
        foreach (var (key, word) in new[] { ("--backup", "папки"), ("--restore", "архива") })
        {
            var parsed = HeadlessDecisions.Parse(new[] { key });

            Assert.NotNull(parsed);
            Assert.False(parsed!.Ok);
            Assert.Contains(key, parsed.Refusal, StringComparison.Ordinal);
            Assert.Contains(word, parsed.Refusal, StringComparison.Ordinal);
            Assert.Contains("полный путь", parsed.Refusal, StringComparison.Ordinal);

            // Тот же вопрос с другого рубежа — тот же ответ (иначе строка отказа разошлась бы).
            Assert.Equal(parsed.Refusal, StartModes.Refuse(new[] { key }));
        }

        // Следом идёт другой ключ — это тоже «пути нет», и сказать надо именно так.
        var swallowed = HeadlessDecisions.Parse(new[] { "--restore", "--with-engine" });
        Assert.NotNull(swallowed);
        Assert.False(swallowed!.Ok);
        Assert.Contains("--with-engine", swallowed.Refusal, StringComparison.Ordinal);

        // А вот сложившийся режим общий рубеж отвергать не имеет права.
        Assert.Null(StartModes.Refuse(new[] { "--restore", Archive }));
        Assert.Null(StartModes.Refuse(new[] { "--backup", Folder }));
    }

    /// <summary>
    /// Относительный путь отвергается, и причина не «так аккуратнее»: у <c>[IO.File]</c> рабочий
    /// каталог ПРОЦЕССА, а не каталог PowerShell, и «..\копии» означало бы не то, что написано
    /// в строке. Грабля области, стоившая уже не одного прогона.
    /// </summary>
    [Fact]
    public void Относительный_путь_отвергается_а_полный_принимается()
    {
        var relative = HeadlessDecisions.Parse(new[] { "--backup", @"..\копии" });

        Assert.NotNull(relative);
        Assert.False(relative!.Ok);
        Assert.Contains("ПРОЦЕССА", relative.Refusal, StringComparison.Ordinal);
        Assert.Contains("абсолютным", relative.Refusal, StringComparison.Ordinal);

        Assert.True(HeadlessDecisions.Parse(new[] { "--backup", Folder })!.Ok);
        Assert.True(HeadlessDecisions.Parse(new[] { "--restore", Archive })!.Ok);
    }

    /// <summary>
    /// Состав копии — ИЗ НАСТРОЕК, и это единственное место правды: ключей «положи ключи» у копии
    /// нет вовсе, и попытка их передать — отказ, а не молчание. У наката те же имена значат другое:
    /// это согласия, и они работают.
    ///
    /// ⚠️ Исключение одно — <c>--shareable</c> (решение владельца 27.09.2026, п. 11): «копия для
    /// передачи» настройкой быть перестала, поэтому у копии есть СВОЙ ключ на одну копию. Он же
    /// у наката — отказ: передают копию, а не восстановление.
    /// </summary>
    [Fact]
    public void Состав_копии_берётся_из_настроек_а_разовое_решение_ключом()
    {
        foreach (var key in new[] { "--with-engine", "--with-panel", "--with-keys" })
        {
            var toCopy = HeadlessDecisions.Parse(new[] { "--backup", Folder, key });

            Assert.NotNull(toCopy);
            Assert.False(toCopy!.Ok);
            Assert.Contains(key, toCopy.Refusal, StringComparison.Ordinal);
            Assert.Contains("настроек", toCopy.Refusal, StringComparison.Ordinal);

            // Тот же ключ у наката — законное согласие.
            Assert.True(HeadlessDecisions.Parse(new[] { "--restore", Archive, key })!.Ok);
        }

        // Разовое решение — наоборот: у КОПИИ законно, у наката отказ.
        var shareable = HeadlessDecisions.Parse(
            new[] { "--backup", Folder, HeadlessDecisions.ShareableKey });

        Assert.NotNull(shareable);
        Assert.True(shareable!.Ok);
        Assert.True(shareable.Shareable);
        Assert.False(shareable.WithKeys, "передача не имеет права тащить за собой согласие на ключи");

        // Без ключа — своя копия: умолчание осталось прежним.
        var own = HeadlessDecisions.Parse(new[] { "--backup", Folder });
        Assert.False(own!.Shareable);

        var atRestore = HeadlessDecisions.Parse(
            new[] { "--restore", Archive, HeadlessDecisions.ShareableKey });

        Assert.NotNull(atRestore);
        Assert.False(atRestore!.Ok);
        Assert.Contains(HeadlessDecisions.ShareableKey, atRestore.Refusal, StringComparison.Ordinal);
        Assert.Contains("накат", atRestore.Refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// ЗАПРЕТ СОЧЕТАНИЯ НАЗЫВАЕТСЯ В ОТЧЁТЕ ДО НАЧАЛА РАБОТЫ. Отказ живёт в одном месте домена
    /// (<c>BackupPlanner.ShareableWithKeysRefusal</c>), и режим без окна берёт его оттуда же: иначе
    /// человек ждал бы копию, которой не будет, и не знал бы почему.
    /// </summary>
    [Fact]
    public void Запрет_сочетания_виден_в_отчёте_до_работы()
    {
        var refused = HeadlessDecisions.CompositionLines(withKeys: true, shareable: true, keyDirectories: 1);

        Assert.Contains(refused, line => line.Contains("ОТКАЗ", StringComparison.Ordinal));
        Assert.Contains(
            refused,
            line => line.Contains(PanelStrings.BackupShareableWithKeysRefused, StringComparison.Ordinal));

        // И у законных сочетаний этой строки нет вовсе: «отказ» в отчёте обязан что-то значить.
        foreach (var (withKeys, shareable) in new[] { (true, false), (false, true), (false, false) })
        {
            var lines = HeadlessDecisions.CompositionLines(withKeys, shareable, keyDirectories: 0);
            Assert.DoesNotContain(lines, line => line.Contains("ОТКАЗ", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// СОГЛАСИЯ НАКАТА ПО УМОЛЧАНИЮ ВЫКЛЮЧЕНЫ — как в <see cref="RestoreOptions"/> и в окне копий.
    /// Иначе ключ возвращал бы движок и ключи человека, ничего у него не спросив.
    /// </summary>
    [Fact]
    public void Согласия_наката_по_умолчанию_выключены()
    {
        var bare = HeadlessDecisions.Parse(new[] { "--restore", Archive });

        Assert.NotNull(bare);
        Assert.True(bare!.Ok);
        Assert.False(bare.WithEngine, "движок и Node по умолчанию НЕ возвращаются");
        Assert.False(bare.WithPanel, "настройки панели по умолчанию НЕ возвращаются");
        Assert.False(bare.WithKeys, "ключи по умолчанию НЕ возвращаются");

        var all = HeadlessDecisions.Parse(
            new[] { "--restore", Archive, "--with-engine", "--with-panel", "--with-keys" });

        Assert.NotNull(all);
        Assert.True(all!.WithEngine && all.WithPanel && all.WithKeys);
        Assert.Equal(HeadlessMode.Restore, all.Mode);
        Assert.Equal(Archive, all.Path);

        // Копия — другой режим, и путь у неё свой.
        var copy = HeadlessDecisions.Parse(new[] { "--backup", Folder });
        Assert.NotNull(copy);
        Assert.Equal(HeadlessMode.Backup, copy!.Mode);
        Assert.Equal(Folder, copy.Path);
    }

    /// <summary>Лишний аргумент — отказ: «--with-my-key» у наката означал бы чужое намерение.</summary>
    [Fact]
    public void Лишний_аргумент_отвергается()
    {
        var extra = HeadlessDecisions.Parse(new[] { "--restore", Archive, "--with-my-key" });

        Assert.NotNull(extra);
        Assert.False(extra!.Ok);
        Assert.Contains("--with-my-key", extra.Refusal, StringComparison.Ordinal);

        // Не наш ключ — не наша забота: разбирать нечего.
        Assert.Null(HeadlessDecisions.Parse(new[] { "--shot", "кадр.png" }));
        Assert.Null(HeadlessDecisions.Parse(Array.Empty<string>()));
    }

    // --- имя архива -----------------------------------------------------------

    /// <summary>
    /// Имя архива — ПРАВИЛО v1 и ПОЛНАЯ копия (с движком): человек ищет копию глазами в проводнике
    /// и уносит её на флешке. Папку назвал вызывающий, и хвостовой разделитель её не ломает.
    /// </summary>
    [Fact]
    public void Имя_архива_берётся_правилом_v1_и_говорит_полная()
    {
        var now = new DateTimeOffset(2026, 9, 26, 1, 35, 0, TimeSpan.FromHours(3));

        var expected = System.IO.Path.Combine(Folder, BackupNaming.ArchiveName(now, withEngine: true));

        Assert.Equal(expected, HeadlessDecisions.ArchivePath(Folder, now));
        Assert.Equal(expected, HeadlessDecisions.ArchivePath(Folder + @"\", now));
        Assert.EndsWith("-full.zip", HeadlessDecisions.ArchivePath(Folder, now), StringComparison.Ordinal);
        Assert.Contains("2026-09-26-013500", HeadlessDecisions.ArchivePath(Folder, now), StringComparison.Ordinal);
    }

    // --- сервер: слова, а не код ---------------------------------------------

    /// <summary>
    /// Работающий сервер ЗАПРЕЩАЕТ накат — и только он. «Посмотреть не удалось» это НЕ «работает»:
    /// на этом отказ не строим, но и молчать нельзя — говорим словами.
    /// </summary>
    [Fact]
    public void Накат_отказывает_только_на_работающем_сервере()
    {
        Assert.True(HeadlessDecisions.BlocksRestore(ServerAnswer.Working));
        Assert.False(HeadlessDecisions.BlocksRestore(ServerAnswer.Idle));
        Assert.False(HeadlessDecisions.BlocksRestore(ServerAnswer.Unknown));

        // «Мог работать» — так решается оговорка «копия снята на ходу»: пропустить её дороже,
        // чем сказать лишний раз (находка В2).
        Assert.False(HeadlessDecisions.ServerMayRun(ServerAnswer.Idle));
        Assert.True(HeadlessDecisions.ServerMayRun(ServerAnswer.Working));
        Assert.True(HeadlessDecisions.ServerMayRun(ServerAnswer.Unknown));
    }

    /// <summary>
    /// Слова о сервере — единственное, что человек по этому поводу прочитает. Молчащий сервер
    /// не имеет права называться «на ходу», а работающий — «остановленным».
    /// </summary>
    [Fact]
    public void Слова_о_сервере_честные()
    {
        var live = HeadlessDecisions.ServerLine(ServerAnswer.Working, 3081, HeadlessMode.Backup);
        Assert.Contains("3081", live, StringComparison.Ordinal);
        Assert.Contains("НА ХОДУ", live, StringComparison.Ordinal);

        var idle = HeadlessDecisions.ServerLine(ServerAnswer.Idle, 3081, HeadlessMode.Backup);
        Assert.Contains("не отвечает", idle, StringComparison.Ordinal);
        Assert.Contains("остановленном", idle, StringComparison.Ordinal);
        Assert.DoesNotContain("НА ХОДУ", idle, StringComparison.Ordinal);

        var unknown = HeadlessDecisions.ServerLine(ServerAnswer.Unknown, 3081, HeadlessMode.Backup);
        Assert.Contains("не удалось", unknown, StringComparison.Ordinal);

        // У наката та же правда, но сказанная про накат: поверх живого движка он не делается.
        var blocking = HeadlessDecisions.ServerLine(ServerAnswer.Working, 3081, HeadlessMode.Restore);
        Assert.Contains("накат поверх живого движка не делается", blocking, StringComparison.Ordinal);
    }

    // --- строки отчёта --------------------------------------------------------

    /// <summary>
    /// Состав копии и согласия наката называются СЛОВАМИ: разрешение на ключи делает ключом доступа
    /// сам архив, а «копия для передачи» убирает из него файл ключей. Промолчать об этом значило бы
    /// принять решение о данных человека за него.
    /// </summary>
    [Fact]
    public void Состав_и_согласия_названы_словами()
    {
        var keys = HeadlessDecisions.CompositionLines(withKeys: true, shareable: false, keyDirectories: 2);
        Assert.Contains(keys, line => line.Contains("КЛАДУТСЯ", StringComparison.Ordinal));
        Assert.Contains(keys, line => line.Contains("2", StringComparison.Ordinal));

        var shared = HeadlessDecisions.CompositionLines(withKeys: false, shareable: true, keyDirectories: 0);
        Assert.Contains(shared, line => line.Contains("НЕ кладётся", StringComparison.Ordinal));

        var own = HeadlessDecisions.CompositionLines(withKeys: false, shareable: false, keyDirectories: 0);
        Assert.Contains(own, line => line.Contains("НЕ кладутся", StringComparison.Ordinal));
        Assert.Contains(own, line => line.Contains("в архив кладётся", StringComparison.Ordinal));

        // Передача — РАЗОВОЕ действие этого запуска, и это сказано в отчёте: иначе человек считал бы,
        // что она живёт в настройках, как было прежде.
        Assert.Contains(own, line => line.Contains(HeadlessDecisions.ShareableKey, StringComparison.Ordinal));

        var consents = HeadlessDecisions.ConsentLines(withEngine: false, withPanel: false, withKeys: false);
        foreach (var key in new[] { "--with-engine", "--with-panel", "--with-keys" })
            Assert.Contains(consents, line => line.Contains(key, StringComparison.Ordinal));

        Assert.Contains(consents, line => line.Contains("ВСЕГДА", StringComparison.Ordinal));
    }

    /// <summary>
    /// Строки отчёта начинаются со своего заголовка, а последняя строка — по коду возврата:
    /// «успех», «провал» и «отказ» — три РАЗНЫХ ответа, и скрипт читает именно код.
    /// </summary>
    [Fact]
    public void Отчёт_начинается_с_заголовка_а_кончается_по_коду()
    {
        Assert.StartsWith("КОПИЯ| ", HeadlessDecisions.Line(HeadlessMode.Backup, "факт"), StringComparison.Ordinal);
        Assert.StartsWith("НАКАТ| ", HeadlessDecisions.Line(HeadlessMode.Restore, "факт"), StringComparison.Ordinal);

        Assert.Equal("КОПИЯ УСПЕХ", HeadlessDecisions.Verdict(HeadlessMode.Backup, 0));
        Assert.Equal("НАКАТ ПРОВАЛ", HeadlessDecisions.Verdict(HeadlessMode.Restore, 1));
        Assert.Equal("НАКАТ ОТКАЗ", HeadlessDecisions.Verdict(HeadlessMode.Restore, 2));
    }

    /// <summary>
    /// НАХОДКА В5: план обязан назвать версию движка, которой снята копия, и сравнить её с нынешней —
    /// иначе человек узнает о расхождении только тогда, когда сессии не откроются. Правило одно
    /// на всю панель: им пользуются и окно копий, и режим наката без окна.
    /// </summary>
    [Fact]
    public void Строка_версии_движка_называет_расхождение()
    {
        var differs = RestoreEngine.VersionNote("0.1.5-rc.2", "0.1.7-rc.2");
        Assert.Contains("0.1.5-rc.2", differs, StringComparison.Ordinal);
        Assert.Contains("0.1.7-rc.2", differs, StringComparison.Ordinal);
        Assert.Contains("может не открыть", differs, StringComparison.Ordinal);

        var same = RestoreEngine.VersionNote("0.1.5-rc.2", "0.1.5-rc.2");
        Assert.Contains("той же версии", same, StringComparison.Ordinal);

        Assert.Contains("не записана", RestoreEngine.VersionNote(string.Empty, "0.1.5-rc.2"), StringComparison.Ordinal);
    }
}
