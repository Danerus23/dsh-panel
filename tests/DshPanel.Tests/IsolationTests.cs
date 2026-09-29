using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DshPanel.Isolation;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки механизма изоляции.
///
/// Смысл набора — не «изоляция работает», а «изоляция не может разойтись молча».
/// Поэтому здесь три вещи: перебор ВСЕХ сочетаний у чистых предикатов (а не по одному
/// примеру на предикат), проверка того, что ни один путь не выходит из-под корня, и
/// проверка громких отказов на корень, которым объявляют настоящие каталоги панели.
///
/// Все проверки чистые: ни одна не запускает приложение и ничего не читает у владельца.
/// </summary>
public class IsolationTests
{
    private static Func<string, string?> NoEnv => _ => null;

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "dsh-panel-iso", Guid.NewGuid().ToString("N"));

    // ---------------------------------------------------------------- предикаты

    /// <summary>
    /// Виды сообщений перебираются ВСЕ, а не один: в v1 правило было общим, но проверка,
    /// обходившая один вид, не заметила бы, что новый вид его обошёл.
    /// </summary>
    [Fact]
    public void Все_виды_сообщений_молчат_в_изоляции_и_говорят_у_владельца()
    {
        var kinds = Enum.GetValues<NoticeKind>();

        // Число стережёт появление нового вида: добавили — тест обязан быть перечитан.
        // 26.09.2026 добавлен девятый вид — `BackupLiveCopy` («копия снята на ходу»): решение
        // владельца о том, что сервер ради копии не гасится, требует предупредить человека.
        // 29.09.2026 добавлены ещё два, и оба перечитаны здесь:
        //   * `PricingChanged` — требование владельца 28.09.2026 «шарик с предупреждением, что пики
        //     или тарифы изменились» (история цен, `docs\DESIGN.md` п. 26);
        //   * `PanelStarted` — требование владельца 29.09.2026: обычный запуск не показывает окна,
        //     и человек не понимает, работает панель или нет. Это ПЕРВЫЙ вид, показываемый при
        //     запуске без просьбы человека, и от общего предохранителя он не освобождён.
        Assert.Equal(11, kinds.Length);

        foreach (var kind in kinds)
        {
            Assert.False(IsolationRules.ShouldNotify(kind, isolatedRun: true), $"{kind}: в изоляции обязан молчать");
            Assert.True(IsolationRules.ShouldNotify(kind, isolatedRun: false), $"{kind}: у владельца обязан говорить");
            Assert.False(IsolationRules.DoorDecision(kind, isolated: true), $"{kind}: дверь в изоляции обязана молчать");
            Assert.True(IsolationRules.DoorDecision(kind, isolated: false), $"{kind}: дверь у владельца обязана говорить");
        }
    }

    /// <summary>
    /// Таблица ожиданий записана ЯВНО, а не формулой. Формула повторяла бы код и не заметила бы,
    /// что её поменяли; таблица — заметит.
    /// </summary>
    [Fact]
    public void Мастер_первой_настройки_перебирает_все_восемь_сочетаний()
    {
        var cases = new (bool Onboarded, bool Requested, bool Isolated, bool Expected)[]
        {
            (false, false, false, true),
            (false, false, true,  false),
            (false, true,  false, true),
            (false, true,  true,  true),
            (true,  false, false, false),
            (true,  false, true,  false),
            (true,  true,  false, true),
            (true,  true,  true,  true),
        };

        Assert.Equal(8, cases.Length);
        foreach (var c in cases)
        {
            Assert.Equal(
                c.Expected,
                IsolationRules.ShouldAutoShowOnboarding(c.Onboarded, c.Requested, c.Isolated));
        }
    }

    [Fact]
    public void Изоляция_глушит_всё_автоматическое_и_не_трогает_осознанное()
    {
        Assert.False(IsolationRules.ShouldAutoOpenBrowser(true));
        Assert.False(IsolationRules.ShouldAutoStartServer(true));
        Assert.False(IsolationRules.ShouldShowPanelOnSignal(true));
        Assert.False(IsolationRules.ShouldRunScheduledWork(true));

        Assert.True(IsolationRules.ShouldAutoOpenBrowser(false));
        Assert.True(IsolationRules.ShouldAutoStartServer(false));
        Assert.True(IsolationRules.ShouldShowPanelOnSignal(false));
        Assert.True(IsolationRules.ShouldRunScheduledWork(false));
    }

    [Fact]
    public void Окно_панели_при_старте_перебирает_все_четыре_сочетания()
    {
        Assert.True(IsolationRules.ShouldShowWindowOnStart(onboarded: true, isolated: false));
        Assert.False(IsolationRules.ShouldShowWindowOnStart(onboarded: true, isolated: true));
        Assert.False(IsolationRules.ShouldShowWindowOnStart(onboarded: false, isolated: false));
        Assert.False(IsolationRules.ShouldShowWindowOnStart(onboarded: false, isolated: true));
    }

    /// <summary>
    /// ШАРИК «ПАНЕЛЬ ЗАПУЩЕНА» (требование владельца 29.09.2026). Перебираются ВСЕ четыре
    /// сочетания: вопрос здесь не «показать ли вообще», а в КАКОЙ ИЗ ДВУХ бед он уместен —
    /// окно показано (человек и так видит панель) или это прогон проверки.
    ///
    /// ⚠️ Проверка обязана уметь падать: мутация «убрать условие окна» роняет ровно её.
    /// </summary>
    [Fact]
    public void Шарик_о_запуске_молчит_при_показанном_окне_и_в_изоляции()
    {
        Assert.True(IsolationRules.ShouldAnnouncePanelStarted(windowShown: false, isolated: false));
        Assert.False(IsolationRules.ShouldAnnouncePanelStarted(windowShown: true, isolated: false));
        Assert.False(IsolationRules.ShouldAnnouncePanelStarted(windowShown: false, isolated: true));
        Assert.False(IsolationRules.ShouldAnnouncePanelStarted(windowShown: true, isolated: true));
    }

    // ---------------------------------------------------------------- права

    /// <summary>
    /// ПРАВО ЗАНЯТЬ ПОРТ ВЛАДЕЛЬЦА (решение владельца 26.09.2026: 2.0 встаёт на место 1.x).
    /// Перебираются ВСЕ четыре сочетания, а не одно: без этого «право только у обычного запуска»
    /// держалось бы словом, а проверка — не может упасть.
    /// </summary>
    [Fact]
    public void Порт_владельца_занимает_только_обычный_запуск_человеком()
    {
        var cases = new (bool Isolated, bool Human, bool Expected)[]
        {
            (false, true,  true),    // обычный запуск человеком — можно
            (false, false, false),   // прогон проверки без корня — нельзя
            (true,  true,  false),   // изолированный прогон — нельзя (и человек его не запускал)
            (true,  false, false),   // изолированный прогон проверки — нельзя
        };

        Assert.Equal(4, cases.Length);

        foreach (var (isolatedRun, human, expected) in cases)
        {
            var context = RunContext.Create(
                isolatedRun
                    ? RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, TempRoot() }, NoEnv)
                    : RunRequest.Owner());

            Assert.Equal(expected, RunRights.MayOccupyOwnerPort(context, human));
        }
    }

    /// <summary>
    /// ПРАВО ПОДНЯТЬ СЕРВЕР САМОМУ — то же правило, но это ДРУГОЕ решение, и проверка у него своя:
    /// прогон проверки (<c>--shell-selftest</c> идёт ОБЫЧНЫМ путём панели) не поднимает НИЧЕГО.
    /// Именно на этой проверке ловится снятое условие «только человек»: у обвязки в `App` иначе
    /// зубов нет — на машине владельца до запуска дело не доходит, потому что рядом всегда
    /// работает его живой DSH и замок от второго движка честно срабатывает.
    /// </summary>
    [Fact]
    public void Сервер_поднимает_сам_только_обычный_запуск_человеком()
    {
        var cases = new (bool Isolated, bool Human, bool Expected)[]
        {
            (false, true,  true),
            (false, false, false),
            (true,  true,  false),
            (true,  false, false),
        };

        Assert.Equal(4, cases.Length);

        foreach (var (isolatedRun, human, expected) in cases)
        {
            var context = RunContext.Create(
                isolatedRun
                    ? RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, TempRoot() }, NoEnv)
                    : RunRequest.Owner());

            Assert.Equal(expected, RunRights.MayAutoStartServer(context, human));
        }
    }

    /// <summary>
    /// Права РАЗНЫЕ по смыслу, хотя тела совпадают сегодня, и это не повод свести их в одну
    /// функцию. Проверка сторожит именно РАЗДЕЛЬНОСТЬ: у каждого права свой предикат, и завтра
    /// у любого из них появится своё третье условие, которое не должно поехать в чужое.
    ///
    /// ⚠️ Здесь НЕ проверяется, что одно право не подменяет другое вызовом: для проверки этого
    /// пришлось бы ломать код. Проверяется то, что видно снаружи: у каждого права своя точка входа
    /// и своё имя, и оба они есть.
    /// </summary>
    [Fact]
    public void Права_порта_и_подъёма_существуют_раздельно()
    {
        var owner = RunContext.Create(RunRequest.Owner());

        Assert.True(RunRights.MayOccupyOwnerPort(owner, humanLaunch: true));
        Assert.True(RunRights.MayAutoStartServer(owner, humanLaunch: true));
        Assert.False(RunRights.MayOccupyOwnerPort(owner, humanLaunch: false));
        Assert.False(RunRights.MayAutoStartServer(owner, humanLaunch: false));

        // И оба они про ЛЮДЕЙ, а не про машинное окружение: у изолированного прогона нет ни одного.
        var isolated = RunContext.Create(
            RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, TempRoot() }, NoEnv));

        Assert.False(RunRights.MayOccupyOwnerPort(isolated, humanLaunch: true));
        Assert.False(RunRights.MayAutoStartServer(isolated, humanLaunch: true));
    }

    // ---------------------------------------------------------------- пути

    /// <summary>
    /// Вот она, та самая проверка. В v1 прогон подменил данные и состояние, но НЕ домашний
    /// каталог движка — и прочитал настоящий профиль владельца, показав его баланс в журнале.
    /// Здесь перебирается СПИСОК путей, а не запомненные свойства: новое свойство, забытое
    /// в подмене, обязано уронить эту проверку.
    /// </summary>
    [Fact]
    public void В_изолированном_прогоне_ни_один_путь_не_выходит_из_под_корня()
    {
        var root = TempRoot();
        var paths = AppPaths.Under(root);
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = canonical + Path.DirectorySeparatorChar;

        Assert.Equal(canonical, paths.Root);
        Assert.NotEmpty(paths.AllPaths);

        foreach (var path in paths.AllPaths)
        {
            // Корень «внутри» самого себя не лежит — он ему равен. Допустимы оба случая.
            var allowed = string.Equals(
                    Path.TrimEndingDirectorySeparator(path), canonical, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

            Assert.True(allowed, $"путь «{path}» вышел из-под корня «{root}» — изоляция дырявая");
        }

        // Число стережёт ПОПОЛНЕНИЕ и ПРОПАЖУ: путь, вычеркнутый из перечня, выпадает из перебора
        // выше, и проверка осталась бы зелёной, не проверив ровно тот путь, который убрали
        // (а путь-свойство, наоборот, дописанный в перечень, обязан быть перечитан и здесь).
        // Четырнадцать — сегодняшнее число: `Root`, `StateDir`, `DataDir`, `BackupsDir`, `LogFile`,
        // `SettingsFile`, `EngineDir`, `AutostartFile`, `DshHome`, `CredentialsPath`, `SshDir`,
        // `EntryLinkFile`, `OwnServerFile`, `PricingHistoryFile`. ⚠️ Два предпоследних добавлены
        // 27.09.2026 вместе с ссылкой входа и записью «этот сервер наш»: оба — секреты или почти
        // секреты, и оба обязаны подменяться в изолированном прогоне, иначе он прочитал бы файлы
        // владельца. Последний добавлен 29.09.2026 вместе с историей цен: она тоже файл состояния
        // под корнем панели, и прогон без права не должен ни читать чужую, ни писать свою.
        // Изменил перечень — перечитай и этот список, и `docs/ISOLATION.md` §3.
        Assert.Equal(14, paths.AllPaths.Count);
    }

    /// <summary>
    /// Обещание `docs/ISOLATION.md` §3, сделанное настоящим: свойство-путь, которого нет
    /// в <see cref="AppPaths.AllPaths"/>, роняет проверку. До 26.09.2026 это было только словом —
    /// перечень в проверке выше лишь ПЕРЕБИРАЛСЯ, а свойства в нём не сверялись, и добавленное
    /// свойство вне корня (например <c>EscapedDir</c>) не роняло ровно ничего.
    ///
    /// ОТБОР СВОЙСТВ — правило, а не сегодняшний список имён:
    ///
    ///   * берутся СВОЙСТВА, а не поля и не константы: <c>ProductFolder</c> и
    ///     <c>CredentialsFileName</c> — имена, а не пути, и полями они и объявлены;
    ///   * только у самого <see cref="AppPaths"/>, без вложенного <c>AppPaths.V1</c>: каталоги v1 —
    ///     ЧУЖИЕ, панель их только читает, из корня 2.0 они не выводятся и подменяться не должны —
    ///     значит и в перечне путей 2.0 им места нет. Поэтому <c>V1.StateDir</c>, <c>V1.DataDir</c>,
    ///     <c>V1.SettingsFile</c> и <c>V1.LegacyDataDir</c> (абсолютные пути!) под правило
    ///     НЕ попадают — и это единственное исключение, и оно про тип-владелец, а не про имя;
    ///   * возвращающие <c>string</c>: путь в перечне — строка, свойство другого типа дополнить
    ///     перечень не может;
    ///   * чьё ЗНАЧЕНИЕ — абсолютный путь (<see cref="Path.IsPathRooted"/>). Это ответ на вопрос
    ///     «а если свойство не путь»: признак, флаг, имя или путь относительный путём не является
    ///     и в перечне путей числиться не обязан. Такие свойства (сегодня их нет, но появятся)
    ///     проверкой путей не покрываются — сознательно и явно, а не по забывчивости;
    ///   * исключений ПО ИМЕНИ нет ни одного, и <see cref="AppPaths.Root"/> — не исключение:
    ///     он такой же путь, как остальные, и первым стоит в перечне. Вывести его из правила
    ///     значило бы оставить без присмотра тот самый путь, из которого растёт вся изоляция.
    ///
    /// Отбор идёт по ЗНАЧЕНИЮ, а не по имени свойства: назови автор новое свойство как угодно
    /// (<c>EscapedDir</c>, <c>TempDir</c>, <c>CacheDir</c>) — оно попадёт под правило, если его
    /// значение абсолютный путь. Поэтому и «слепое» правило (отключённый фильтр, отбор по числу)
    /// роняет эту проверку: без найденных свойств сверять нечего.
    /// </summary>
    [Fact]
    public void Каждое_свойство_пути_AppPaths_входит_в_AllPaths()
    {
        var paths = AppPaths.Under(TempRoot());

        var declared = typeof(AppPaths)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
            .Select(p => (Name: p.Name, Value: p.GetValue(paths) as string))
            .Where(p => !string.IsNullOrWhiteSpace(p.Value) && Path.IsPathRooted(p.Value!))
            .ToList();

        // Пустой набор — признак ослепшего отбора (сломан фильтр), а не чистого кода:
        // без этой строки слепое правило прошло бы «зелёным», ничего не сверив.
        Assert.NotEmpty(declared);

        foreach (var property in declared)
        {
            Assert.True(
                paths.AllPaths.Contains(property.Value!),
                $"свойство «{property.Name}» — путь «{property.Value}», но его нет в AppPaths.AllPaths: "
                    + "изолированный прогон пройдёт мимо него, а подмена его не подменит");
        }

        // И в обратную сторону: строка в перечне, за которой не стоит ни одного свойства-пути, —
        // это путь, подменяемый наугад. Число не задано, оно сверяется с найденным.
        Assert.Equal(declared.Count, paths.AllPaths.Count);
    }

    /// <summary>
    /// Если бы домашний каталог движка лежал внутри корня панели, изоляция получалась бы
    /// сама собой и ошибку v1 было бы не заметить. Он лежит вне — значит подмена обязательна.
    /// </summary>
    [Fact]
    public void Домашний_каталог_движка_у_владельца_лежит_вне_корня_панели()
    {
        var owner = AppPaths.ForOwner();
        var prefix = Path.TrimEndingDirectorySeparator(owner.Root) + Path.DirectorySeparatorChar;

        Assert.False(owner.DshHome.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith(".dsh", owner.DshHome);
    }

    [Fact]
    public void Каталоги_двух_версий_не_совпадают()
    {
        // Решение владельца: 2.0 встаёт РЯДОМ, а не поверх. Разойтись они обязаны каталогами,
        // иначе перенос настроек и откат на v1 станут невозможны.
        Assert.NotEqual(AppPaths.V1.ProductFolder, AppPaths.ProductFolder);
        Assert.False(string.Equals(
            AppPaths.V1.StateDir, AppPaths.ForOwner().Root, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- разбор просьбы

    [Fact]
    public void Без_корня_прогон_обычный()
    {
        var request = RunRequest.Parse(Array.Empty<string>(), NoEnv);

        Assert.False(request.IsIsolated);
        Assert.Null(request.Root);
        Assert.Empty(request.Facts);
    }

    [Fact]
    public void Корень_берётся_и_из_ключа_и_из_переменной()
    {
        var root = TempRoot();
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        var bySwitch = RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, root }, NoEnv);
        Assert.True(bySwitch.IsIsolated);
        Assert.Equal(full, bySwitch.Root);

        var byEnv = RunRequest.Parse(
            Array.Empty<string>(),
            name => name == RunRequest.RootVariable ? root : null);
        Assert.True(byEnv.IsIsolated);
        Assert.Equal(full, byEnv.Root);
    }

    [Fact]
    public void Один_и_тот_же_корень_из_двух_источников_не_спор()
    {
        var root = TempRoot();
        var request = RunRequest.Parse(
            new[] { RunRequest.RootSwitchArgument, root },
            name => name == RunRequest.RootVariable ? root : null);

        Assert.True(request.IsIsolated);
    }

    /// <summary>
    /// Перечень фактов — только на чтение. В v1 это записано прямо: за перечнем стоит вся
    /// защита рабочей машины, и менять его на ходу нельзя.
    /// </summary>
    [Fact]
    public void Перечень_фактов_изоляции_править_нельзя()
    {
        var request = RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, TempRoot() }, NoEnv);
        var mutable = Assert.IsAssignableFrom<IList<string>>(request.Facts);

        Assert.NotEmpty(mutable);
        Assert.Throws<NotSupportedException>(() => mutable[0] = "подмена");
    }

    /// <summary>
    /// Перечень фактов обязан называть ИСТОЧНИКИ, которые и правда были. Найдено ревизией
    /// 26.09.2026: перечень был одной зашитой строкой «DSH_PANEL_RUN_ROOT», поэтому прогон,
    /// изолированный КЛЮЧОМ, в отчёте называл переменную окружения, которой в запуске не было.
    ///
    /// Проверка сравнивает перечень не с ожидаемым списком, а с тем, чем корень задан в САМОМ
    /// вызове: подмени механизм набора фактов (или добавь третий способ изоляции, не вписав его
    /// в перечень) — и она упадёт, а не останется зелёной.
    /// </summary>
    [Fact]
    public void Перечень_фактов_называет_именно_те_источники_которыми_задан_корень()
    {
        var root = TempRoot();
        var byEnv = (Func<string, string?>)(name => name == RunRequest.RootVariable ? root : null);

        void Сверить(string[] args, Func<string, string?> env, string чем)
        {
            var request = RunRequest.Parse(args, env);

            var ожидаемые = new List<string>();
            if (Array.Exists(args, a => string.Equals(a, RunRequest.RootSwitchArgument, StringComparison.OrdinalIgnoreCase)))
                ожидаемые.Add(RunRequest.RootSwitchFact);
            if (!string.IsNullOrWhiteSpace(env(RunRequest.RootVariable)))
                ожидаемые.Add(RunRequest.RootVariable);

            Assert.True(ожидаемые.Count > 0, "проба бессмысленна: корень не задан ничем — " + чем);
            Assert.Equal(ожидаемые, request.Facts);
        }

        Сверить(new[] { RunRequest.RootSwitchArgument, root }, NoEnv, "корень задан ключом");
        Сверить(Array.Empty<string>(), byEnv, "корень задан переменной окружения");
        Сверить(new[] { RunRequest.RootSwitchArgument, root }, byEnv, "корень задан обоими источниками");
    }

    // ---------------------------------------------------------------- отказы

    [Fact]
    public void Ключ_без_пути_и_двойной_ключ_отвергаются()
    {
        Assert.Throws<ArgumentException>(() =>
            RunRequest.Parse(new[] { RunRequest.RootSwitchArgument }, NoEnv));

        Assert.Throws<ArgumentException>(() =>
            RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, RunRequest.RootSwitchArgument, TempRoot() }, NoEnv));

        Assert.Throws<ArgumentException>(() =>
            RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, TempRoot(), RunRequest.RootSwitchArgument, TempRoot() }, NoEnv));
    }

    [Fact]
    public void Корень_заданный_двумя_способами_по_разному_отвергается()
    {
        Assert.Throws<ArgumentException>(() => RunRequest.Parse(
            new[] { RunRequest.RootSwitchArgument, TempRoot() },
            name => name == RunRequest.RootVariable ? TempRoot() : null));
    }

    [Fact]
    public void Относительный_корень_отвергается()
    {
        Assert.Throws<ArgumentException>(() =>
            RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, "изолированный" }, NoEnv));
    }

    /// <summary>
    /// Главный отказ. Корнем нельзя объявить настоящие каталоги панели — иначе прогон
    /// называется изолированным, а пишет владельцу в его же данные. Ровно так в v1
    /// «изолированный» накат погасил рабочий сервер владельца.
    /// </summary>
    [Fact]
    public void Настоящий_корень_панели_корнем_объявить_нельзя()
    {
        var ownerRoot = AppPaths.ForOwner().Root;

        Assert.Throws<ArgumentException>(() =>
            RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, ownerRoot }, NoEnv));

        Assert.Throws<ArgumentException>(() =>
            RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, Path.Combine(ownerRoot, "подмена") }, NoEnv));
    }

    [Fact]
    public void Корень_содержащий_настоящий_отвергается()
    {
        // Каталог над корнем панели: настоящие данные лежали бы ВНУТРИ объявленного изолированным.
        var above = Path.GetDirectoryName(Path.GetDirectoryName(AppPaths.ForOwner().Root))!;

        Assert.Throws<ArgumentException>(() =>
            RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, above }, NoEnv));
    }

    // ---------------------------------------------------------------- контекст

    [Fact]
    public void Контекст_собирается_по_просьбе()
    {
        var owner = RunContext.Create(RunRequest.Owner());
        Assert.False(owner.IsIsolated);
        Assert.Equal(AppPaths.ForOwner().Root, owner.Paths.Root);

        var root = TempRoot();
        var isolated = RunContext.Create(
            RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, root }, NoEnv));

        Assert.True(isolated.IsIsolated);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            isolated.Paths.Root,
            ignoreCase: true);
        Assert.NotEmpty(isolated.IsolationFacts);
    }

    /// <summary>
    /// Ключ корня убирается из списка ВМЕСТЕ со своим значением: иначе режим, стоящий после него,
    /// не опознаётся. Дефект был настоящий и нашёлся на собранном exe, а не здесь:
    /// «корень, затем проба изоляции» падал отказом «не сказано, что делать».
    /// </summary>
    [Fact]
    public void Ключ_корня_убирается_из_списка_вместе_со_значением()
    {
        var root = TempRoot();

        Assert.Equal(
            new[] { "--isolation-selftest" },
            RunRequest.WithoutRootSwitch(new[] { RunRequest.RootSwitchArgument, root, "--isolation-selftest" }));

        Assert.Equal(
            new[] { "--shot", "кадр.png" },
            RunRequest.WithoutRootSwitch(new[] { "--shot", "кадр.png", RunRequest.RootSwitchArgument, root }));

        Assert.Equal(
            new[] { "--tray-selftest", "6" },
            RunRequest.WithoutRootSwitch(new[] { "--tray-selftest", "6" }));
    }
}
