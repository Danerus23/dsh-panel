using System;
using System.IO;
using DshPanel.Localization;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// НАДПИСЬ О ЗАПУСКЕ СЕРВЕРА ГОВОРИТ О ПЕРВОМ ЗАПУСКЕ ТОЛЬКО КОГДА ОН ПЕРВЫЙ (t15, 18.10.2026).
///
/// **От какого случая (слово владельца).** Панель на каждом подъёме писала «первый запуск
/// достраивает профиль, это до минуты», а профиль DSH не пересоздавался (`profiles`, `sessions`,
/// `skills` лежали с 26.09): человек читал обещание минуты ожидания там, где ждать было нечего.
///
/// **Как проверяется.** Признак «первый запуск» — это КАТАЛОГИ профиля в доме движка
/// (<see cref="EngineFirstRun.Markers"/>), а не настройка; надпись выбирается РЕШЕНИЕМ
/// (<see cref="EngineFirstRun.StartingLabel"/>), а не окном. Поэтому проверка не показывает окон
/// и не требует живого профиля: временные каталоги плюс явный признак-параметр.
///
/// **Мутации, которые обязаны упасть:** «надпись всегда обещает первый запуск» (ZH6) ·
/// «надпись никогда не обещает» (ZH7) · «признак не смотрит каталоги» (ZH8) ·
/// «подробность «ещё поднимается» не зависит от признака» (ZH9).
/// </summary>
public sealed class EngineFirstRunTests
{
    [Fact]
    public void Признак_первого_запуска_смотрит_три_каталога_профиля()
    {
        var home = TempRoots.For("engine-first-run");

        try
        {
            Directory.CreateDirectory(home);

            // Дом есть, каталогов профиля нет — это честно первый запуск: строить нечего.
            Assert.True(EngineFirstRun.Looks(home), "пустой дом движка обязан считаться первым запуском");

            // Хоть один каталог профиля — профиль уже строился, и обещать «первый запуск» нечего.
            foreach (var marker in EngineFirstRun.Markers)
            {
                var path = Path.Combine(home, marker);
                Directory.CreateDirectory(path);

                Assert.False(
                    EngineFirstRun.Looks(home),
                    $"каталог «{marker}» есть, а признак всё ещё говорит «первый запуск»");

                Directory.Delete(path);
            }

            // Дома нет вовсе — первый запуск (всё появится).
            Assert.True(EngineFirstRun.Looks(Path.Combine(home, "no-such-home")));

            // Мерить нечем — НЕ «первый запуск»: обещать нечего.
            Assert.False(EngineFirstRun.Looks(null));
            Assert.False(EngineFirstRun.Looks("   "));
        }
        finally
        {
            TempRoots.Remove(home);
        }
    }

    [Fact]
    public void Надпись_о_запуске_обещает_первый_запуск_только_когда_он_первый()
    {
        // Ложный признак — надпись БЕЗ обещания первого запуска.
        Assert.Equal(PanelStrings.ServerStarting, EngineFirstRun.StartingLabel(firstRunLooks: false));
        Assert.NotEqual(PanelStrings.ServerStartingFirstRun, EngineFirstRun.StartingLabel(firstRunLooks: false));

        // Правда — с обещанием.
        Assert.Equal(PanelStrings.ServerStartingFirstRun, EngineFirstRun.StartingLabel(firstRunLooks: true));
        Assert.NotEqual(PanelStrings.ServerStarting, EngineFirstRun.StartingLabel(firstRunLooks: true));

        // Слова этого отличия — из русского словаря явно: обещание живёт только в «первом» ключе.
        Assert.DoesNotContain(
            "первый запуск",
            Loc.TIn("ru", nameof(PanelStrings.ServerStarting)),
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "первый запуск",
            Loc.TIn("ru", nameof(PanelStrings.ServerStartingFirstRun)),
            StringComparison.OrdinalIgnoreCase);

        // ПОДРОБНОСТЬ состояния — тоже по признаку, и порт в обеих строках на месте.
        var plain = EngineFirstRun.StillStartingDetail(firstRunLooks: false, 3081);
        var firstRun = EngineFirstRun.StillStartingDetail(firstRunLooks: true, 3081);

        Assert.NotEqual(plain, firstRun);
        Assert.Contains("3081", plain, StringComparison.Ordinal);
        Assert.Contains("3081", firstRun, StringComparison.Ordinal);
        Assert.DoesNotContain("первый запуск", plain, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("первый запуск", firstRun, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Оба_случая_различаются_на_всех_трёх_языках()
    {
        foreach (var language in new[] { "ru", "en", "zh" })
        {
            var plain = Loc.TIn(language, nameof(PanelStrings.ServerStarting));
            var firstRun = Loc.TIn(language, nameof(PanelStrings.ServerStartingFirstRun));

            Assert.False(string.IsNullOrWhiteSpace(plain), $"«{language}»: пустая надпись о запуске");
            Assert.False(string.IsNullOrWhiteSpace(firstRun), $"«{language}»: пустая надпись о первом запуске");
            Assert.NotEqual(plain, firstRun);

            var plainStill = Loc.TIn(language, nameof(PanelStrings.SrvEngineStillStartingFormat), 3081);
            var firstRunStill = Loc.TIn(language, nameof(PanelStrings.SrvEngineStillStartingFirstRunFormat), 3081);

            Assert.NotEqual(plainStill, firstRunStill);
        }
    }

    /// <summary>
    /// ПОДСТАНОВКА СЕРВЕРА В ПРОВЕРКАХ НЕ ОБЕЩАЕТ ПЕРВЫЙ ЗАПУСК: у неё нет каталога движка,
    /// и «нечем мерить» обязано давать честную надпись без обещания. Это не удобство, а то же
    /// правило, что у <c>IServerControl.Known</c>: значение по умолчанию выбирает ЧЕСТНЫЙ ответ.
    /// </summary>
    [Fact]
    public void Подстановка_сервера_не_обещает_первый_запуск()
    {
        IServerControl stub = new StubServer();

        Assert.False(stub.FirstRunLooks);
        Assert.Equal(PanelStrings.ServerStarting, EngineFirstRun.StartingLabel(stub.FirstRunLooks));
    }

    /// <summary>
    /// НАДПИСЬ БЕРУТ ОБА МЕСТА ЗАПУСКА, И ОБА — РЕШЕНИЕМ, А НЕ ЛИТЕРАЛОМ (t15, 18.10.2026).
    ///
    /// **Зачем отдельно.** Ложное обещание жило в ДВУХ местах: `App.axaml.cs` (автоподъём при
    /// старте панели) и `Views\MainWindow.axaml.cs` (кнопка «Запустить»). Решение проверено выше,
    /// но «решение есть» не значит «его позвали»: вернуть литерал можно тихо, и ни один прогон
    /// не покраснеет. Это ровно тот класс, что стерегут проверки-швы по исходнику
    /// (`PhonePageTests.Крючок_устройства…`, `TreeDeletionTests.Удаление_дерева_живёт_одним_местом`).
    ///
    /// ⚠️ **Чего проверка НЕ доказывает:** что надпись ВИДНА в окне. Это доказывает поведение,
    /// а не текст исходника; здесь стерегутся только точки вызова — само поведение проверено
    /// решением (<see cref="EngineFirstRun.StartingLabel"/>) и глазами на кадре.
    ///
    /// Мутации, которые обязаны упасть: «вернуть литерал в `App.axaml.cs`» (ZH10) ·
    /// «вернуть литерал в `MainWindow.axaml.cs`» (ZH11).
    /// </summary>
    [Fact]
    public void Надпись_о_запуске_берут_оба_места_запуска()
    {
        var root = RepositoryRoot();

        var app = File.ReadAllText(Path.Combine(root, "src", "DshPanel", "App.axaml.cs"));
        var window = File.ReadAllText(Path.Combine(root, "src", "DshPanel", "Views", "MainWindow.axaml.cs"));

        Assert.Equal(1, Occurrences(app, "EngineFirstRun.StartingLabel("));
        Assert.Equal(1, Occurrences(window, "EngineFirstRun.StartingLabel("));

        // Старая проводка не вернулась: безусловный литерал в этих двух местах запрещён.
        Assert.DoesNotContain("BeginServerStart(PanelStrings.ServerStarting)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("PanelStrings.ServerStarting)", window, StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>Корень репозитория — вверх от каталога сборки по файлу <c>DshPanel.slnx</c>.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DshPanel.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        Assert.Fail($"не нашёл корень репозитория: вверх от «{AppContext.BaseDirectory}» нет DshPanel.slnx");
        return string.Empty;
    }

    private sealed class StubServer : IServerControl

    {
        public ServerState State => ServerState.Stopped(0, string.Empty);

        public ServerOwner Owner => ServerOwner.None;

        public bool ConsentRemembered => false;

        public string EntryLink => string.Empty;

        public ServerState Refresh() => State;

        public ServerState Start(TimeSpan timeout) => State;

        public ServerState Stop(bool confirmed) => State;

        public ServerState Restart(TimeSpan timeout, bool confirmed) => State;

        public ServerState RecoverOwnServer(TimeSpan timeout, bool confirmed) => State;

        public DiscoveryResult Scan() => DiscoveryResult.Nothing;

        public ServerState Adopt(FoundServer found) => State;

        public ServerState Detach() => State;
    }
}
