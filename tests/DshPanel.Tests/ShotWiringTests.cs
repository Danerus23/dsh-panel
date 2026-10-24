using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DshPanel.Agents;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Peak;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОБВЯЗКА СЪЁМКИ: ОБРАЗЦОВЫЕ ДАННЫЕ ДОЕЗЖАЮТ ДО САМОГО ОКНА КАДРА.
///
/// **От каких случаев класс заведён (мутации AJ4 и AJ9, `docs\HISTORY.md`).** Обе подменяли
/// ИСПОЛЬЗОВАНИЕ решения о данных — одна снимала образцовую дверь из одного места сборки, вторая
/// переворачивала выбор двери, — и **обе выжили**: проверки читали РАЗБОР съёмки и каталог
/// значений, а не собранное окно. Пока сборка окна жила строками внутри `Program.Shot`, дотянуться
/// до неё проверкой было нельзя: съёмка поднимает платформу. Теперь окно собирает один член
/// (<see cref="Program.ShotWindowWithDoors"/>), и этот класс строит ИМ ЖЕ каждое имя кадра
/// и читает то, что в окне оказалось.
///
/// **Что именно проверяется:**
/// * до окна дошла КАЖДАЯ обещанная образцовая дверь — по имени, а не «в кадре что-то есть»
///   (урок AJ4: кадр оставался непустым за счёт соседней двери);
/// * образцовые ЗНАЧЕНИЯ видны в окне текстом — значит дверь не просто передана, а прочитана;
/// * кадр «два найденных сервера» образцовых значений НЕ получает: он снимает настоящую модель
///   с подставленной пробой процессов (AJ9 переворачивал ровно этот выбор).
///
/// ⚠️ **Глазом сюда не смотрят.** Проверка читает строки и имена; «похоже ли это на правду»
/// читает человек по кадру.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class ShotWiringTests
{
    /// <summary>
    /// Сервер окна — ТОТ ЖЕ, ЧТО У СЪЁМКИ: прогон проверки не занимает порт владельца,
    /// а кадру «два найденных сервера» даётся ТА ЖЕ подставленная проба процессов, что и в съёмке
    /// (`Шот`). Без неё этот кадр нашёл бы на машине что попало — или ничего.
    /// </summary>
    private static ServerController ShotServer(string kind, AppPaths paths) =>
        new(
            paths.DshHome,
            () => paths.DataDir,
            _ => { },
            mayOccupyOwnerPort: false,
            probe: kind == StartModes.ShotMainTwoServers
                ? TwoServersProbe(paths)
                : ServerProbe.System(),
            ownEngineDir: () => paths.EngineDir);

    /// <summary>
    /// ПОДСТАВНАЯ ПРОБА ДВУХ СЕРВЕРОВ — ТА ЖЕ, ЧТО В СЪЁМКЕ (`Program.TwoServersProbe`).
    ///
    /// ⚠️ **Почему копия, а не вызов съёмочного члена.** Он закрыт (`private`), и открывать его
    /// ради проверки значило бы менять продукт под проверку. Здесь повторены ровно те факты,
    /// которые называет съёмка: порты 3080 и 3099, номера процессов 19804 и 22222 и путь движка
    /// ЭТОГО прогона. Разойдись они — разойдётся и кадр, но не молча: проверка ниже сверяет
    /// находки по портам.
    /// </summary>
    private static ServerProbe TwoServersProbe(AppPaths paths)
    {
        const int OwnPort = 3080;
        const int ForeignPort = 3099;
        const int OwnPid = 19804;
        const int ForeignPid = 22222;

        var ownBin = OwnEngine.BinPath(paths.EngineDir, EngineRpcSurface.MeasuredOnVersion);

        return new ServerProbe(
            ListenerPid: port => port == OwnPort ? OwnPid : port == ForeignPort ? ForeignPid : 0,
            Listeners: () => new[]
            {
                new PortTable.Listener(OwnPort, OwnPid),
                new PortTable.Listener(ForeignPort, ForeignPid),
            },
            CommandLine: pid => pid == OwnPid
                ? $"\"node.exe\" {ownBin} web --no-open --port {OwnPort}"
                : "\"node.exe\" C:\\Users\\Public\\AppData\\Roaming\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js"
                  + " web --no-open --port " + ForeignPort,
            ProcessName: _ => "node",
            StartTicks: _ => 63_000_000_000_000_000,
            AnswersFingerprint: _ => true);
    }

    /// <summary>
    /// КАЖДОМУ ИМЕНИ КАДРА — СВОЙ НАБОР ДВЕРЕЙ, И НИ ОДНОЙ ЛИШНЕЙ.
    ///
    /// ⚠️ **Пустой список — тоже ответ, и он назван:** у кадров-диалогов данных нет вовсе
    /// (`confirm-stop`, `confirm-engine`, `backup-stop`, `unsaved-changes`), у `about`, `issue`
    /// и `pricing-history` — тоже (история цен приходит не дверью, а чистым разбором).
    /// Кадр «два найденных сервера» — единственный, кто снимает НАСТОЯЩУЮ модель.
    ///
    /// Мутации, которые обязаны упасть здесь: «образцовый сервер больше не доезжает до главного
    /// окна» (AJ4) · «образцовые настройки не доезжают до окна настроек» · «кадру с находками
    /// выдали образцовые данные» · «дверь доехала туда, где её быть не должно».
    /// </summary>
    [AvaloniaFact]
    public void Образцовые_двери_доезжают_до_каждого_окна()
    {
        foreach (var (kind, expected) in DoorPlan())
        {
            var built = Build(kind);

            Assert.True(
                built.Doors.OrderBy(x => x, StringComparer.Ordinal)
                    .SequenceEqual(expected.OrderBy(x => x, StringComparer.Ordinal)),
                $"кадр «{(kind.Length == 0 ? "(без имени)" : kind)}»: до окна доехали двери "
                + $"[{string.Join(", ", built.Doors)}], а обещаны [{string.Join(", ", expected)}]");
        }
    }

    /// <summary>
    /// ОБРАЗЦОВЫЕ ЗНАЧЕНИЯ ВИДНЫ В ОКНЕ ТЕКСТОМ, А НЕ ТОЛЬКО ПЕРЕДАНЫ ДВЕРЬЮ.
    ///
    /// **От какого случая.** Дверь можно передать и не прочитать: окно, которому не сказали
    /// показать раздел, остаётся с пустым местом — и «дверь доехала» было бы правдой о коде
    /// и ложью о кадре. Поэтому здесь ищутся САМИ ЛИТЕРАЛЫ (<see cref="ShotSamples"/>) в текстах
    /// показанного окна.
    ///
    /// ⚠️ **У КАЖДОГО РАЗДЕЛА — СВОЁ значение, и это не придирка:** одно и то же значение в двух
    /// разделах зеленело бы и на съёмке не того раздела. Раздел «Баланс и тариф» деньгами не занят
    /// (он про расписание и порог), поэтому у него признак — название раздела, а не число.
    ///
    /// Мутации, которые обязаны упасть здесь: «окно собрано без образцовых настроек» ·
    /// «список копий оставлен настоящим» · «баланс на кадре снова не читается» ·
    /// «кадр снимает не тот раздел».
    /// </summary>
    [AvaloniaFact]
    public void Образцовые_значения_видны_в_окне()
    {
        // ГЛАВНОЕ ОКНО снимается БЕЗ ИМЕНИ: списка имён оно не содержит вовсе, и это устройство
        // разбора, а не оплошность проверки. Здесь видны и сервер (порт), и баланс (число).
        var panel = ShowAndRead(string.Empty, ShotSamples.Port.ToString(CultureInfo.InvariantCulture));

        Assert.Contains(Money(), panel, StringComparison.Ordinal);
        Assert.Contains(ShotSamples.Port.ToString(CultureInfo.InvariantCulture), panel, StringComparison.Ordinal);

        // ⚠️ И СЕРВЕР ЗДЕСЬ ОБРАЗЦОВЫЙ, А НЕ НАСТОЯЩИЙ — отдельное утверждение (урок обхода
        // ворот `upd-build\gates-f3.ps1`, мутация AJ4). Порт в тексте есть и у НАСТОЯЩЕГО
        // контроллера прогона — он честно отвечает «свой сервер панели: работает», — поэтому
        // «порт в тексте есть» зеленело и на кадре БЕЗ образцовой двери. Мутация AJ4 снимала
        // дверь, а `doors.Add("server")` оставляла: список дверей сходился, текст сходился,
        // а кадр показывал НАСТОЯЩИЙ сервер прогона.
        //
        // Отличает их СЛОВО состояния, а не порт: у образцовой двери сервер — «свой, работает»,
        // у настоящего контроллера прогона он не запущен и говорит другое. Спрашивается слово
        // образца — из словаря, а не литералом.
        Assert.Contains(Loc.T(PanelStrings.ServerRunning), panel, StringComparison.Ordinal);

        // РАЗДЕЛ «ОБЩЕЕ»: рабочий порт и рабочая папка — образцовые, а не умолчания.
        var general = ShowAndRead(StartModes.ShotSettingsGeneral, ShotSamples.Port.ToString(CultureInfo.InvariantCulture));

        Assert.Contains(ShotSamples.Port.ToString(CultureInfo.InvariantCulture), general, StringComparison.Ordinal);
        Assert.Contains(ShotSamples.WorkDir, general, StringComparison.Ordinal);

        // РАЗДЕЛ «РЕЗЕРВНОЕ КОПИРОВАНИЕ»: папка копий — образцовая.
        Assert.Contains(
            ShotSamples.BackupFolder,
            ShowAndRead(StartModes.ShotSettingsBackup, ShotSamples.BackupFolder),
            StringComparison.Ordinal);

        // РАЗДЕЛ «БАЛАНС И ТАРИФ»: признак — название раздела (денег в нём нет по устройству).
        Assert.Contains(
            Loc.T(PanelStrings.SettingsBalanceSection),
            ShowAndRead(StartModes.ShotSettingsBalance, Loc.T(PanelStrings.SettingsBalanceSection)),
            StringComparison.Ordinal);

        // РАЗДЕЛ «ОБНОВЛЕНИЕ»: видна проверенная версия движка — без образцовой двери здесь
        // стояло бы «проверенной версии нет», то есть слова о том, чего панель НЕ СМОГЛА.
        Assert.Contains(
            ShotSamples.EngineVersion,
            ShowAndRead(StartModes.ShotSettingsUpdate, ShotSamples.EngineVersion),
            StringComparison.Ordinal);

        // РАЗДЕЛ «ТЕЛЕФОН»: адрес — образцовый, и он виден СРАЗУ (код сопряжения окно показывает
        // только по кнопке, поэтому признаком раздела служит адрес, а не код).
        Assert.Contains(
            ShotSamples.PhoneAddress,
            ShowAndRead(StartModes.ShotSettingsPhone, ShotSamples.PhoneAddress),
            StringComparison.Ordinal);

        // КАДР «settings» (старое имя) снимает самый длинный раздел — «Резервное копирование»:
        // его значение обязано быть видно и здесь.
        Assert.Contains(
            ShotSamples.BackupFolder,
            ShowAndRead(StartModes.ShotSettings, ShotSamples.BackupFolder),
            StringComparison.Ordinal);

        // ОКНО КОПИЙ: три образцовые строки ЛЕГЛИ В ТАБЛИЦУ, а не «список пуст».
        //
        // ⚠️ Читается САМО ОКНО (`EntriesTable`), а не текст дерева: строки таблицы — свои органы,
        // и «есть ли строки» здесь видно числом. Текст дерева у этого окна показывает только
        // заголовок и папку — и проверка «строк нет» зеленела бы на неотрисованном списке.
        var backup = (DshPanel.Views.BackupWindow)Build(StartModes.ShotBackup).Window;

        backup.Show();

        Assert.True(
            backup.ListedCount == ShotSamples.Backups().Count,
            $"в окне копий строк {backup.ListedCount}, а образцовых копий {ShotSamples.Backups().Count}");

        backup.Close();

        // ОКНО «ПИКИ И ТАРИФЫ»: таблица ПИКОВ заполнена образцовыми данными — дни и часы видны.
        //
        // ⚠️ **Признак — день недели, а не цена и не баланс.** Цены приходят из разбора прайса
        // (`ShotPricingWithHistory`), а не дверью; число денег живёт в главном окне и в разделе
        // «Баланс и тариф». Пики же приходят ИМЕННО дверью баланса: пропади она — и таблица дней
        // осталась бы одной шапкой.
        // ⚠️ **КЛАСС ДЕФЕКТА (t24, 18.10.2026): проверка зависела от ЧАСОВОГО ПОЯСА машины.**
        // Здесь стоял литерал «04:00–07:00, 09:00–13:00», и он верен ТОЛЬКО в UTC+3: окно
        // показывает МЕСТНОЕ время, а пояс у машины свой — на раннере GitHub (UTC) проверка
        // падала ВСЕГДА (в CI она до этого не прогонялась ни разу).
        //
        // Теперь момент и смещение — ПАРАМЕТРЫ (решение владельца, инъекция): два прогона на
        // ОДНОМ И ТОМ ЖЕ мгновении (02:00Z), смещения явные 0 и +180, пояс машины не участвует
        // НИ В ОДНОМ ожидании. Смещение передаётся функцией на момент UTC, а не числом: за
        // границей перевода часов оно другое (см. `PeakDecisions.State`).
        foreach (var run in new[]
        {
            (Moment: new DateTimeOffset(2026, 9, 21, 2, 0, 0, TimeSpan.Zero), Offset: 0),
            (Moment: new DateTimeOffset(2026, 9, 21, 5, 0, 0, TimeSpan.FromHours(3)), Offset: 180),
        })
        {
            Func<DateTime, int> offsetAtUtc = _ => run.Offset;

            // (а) строка понедельника в таблице — ровно та, что даёт таблица при этом смещении;
            var monday = PeakTable.Rows(AgentCatalog.DeepSeek, run.Offset)[0].PeakWindows;
            var state = PeakDecisions.State(AgentCatalog.DeepSeek, run.Moment, offsetAtUtc);

            // (в) заголовок называет пояс: «UTC+00:00» / «UTC+03:00».
            var heading = PeakTable.Heading(run.Offset);

            // (б) строки состояния называют час переключения с ТЕМ ЖЕ смещением.
            var next = state.NextSwitchUtc!.Value;
            var clock = PeakDecisions.LocalClock(next, run.Offset);
            var nowLine = PeakTable.NowLine(state);

            Assert.Contains(clock, nowLine, StringComparison.Ordinal);

            var text = ShowAndRead(StartModes.ShotPeaks, monday, run.Moment, offsetAtUtc);

            Assert.Contains(monday, text, StringComparison.Ordinal);
            Assert.Contains(heading, text, StringComparison.Ordinal);
            Assert.Contains(PeakTable.NowText(state), text, StringComparison.Ordinal);
            Assert.Contains(nowLine, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// КАДР ДОКУМЕНТАЦИИ НАЗЫВАЕТ ДАТЫ ВЫПУСКА, А НЕ ВЫДУМАННЫЕ (t13, 18.10.2026).
    ///
    /// **От какого случая.** Владелец увидел на витрине «Выпущен: 27.09.2026» и «проверено
    /// 28.09.2026», а выпуск 2.1.0 вышел **17.10.2026**. Прежняя проверка кадров считала ЦВЕТА
    /// («кадр не белый лист»), и кадр с неверной датой был для неё так же зелен, как с верной:
    /// число цветов про СМЫСЛ текста не говорит ничего.
    ///
    /// **Что проверяется.** Текст СОБРАННОГО окна кадра «Обновление»: в нём обе даты — 17.10.2026
    /// (выпуск) и 18.10.2026 (проверка) — и НЕТ прежних 27.09.2026 / 28.09.2026.
    ///
    /// ⚠️ Даты сверяются ЛИТЕРАЛАМИ, а не константами образцовой двери: проверка о том, что видит
    /// ЧЕЛОВЕК, и «ожидание = источник» осталось бы зелёным после правки самого источника —
    /// то есть не поймало бы ровно тот дефект, ради которого заведено.
    ///
    /// **Мутации, которые обязаны упасть:** вернуть `"2026-09-27T10:00:00Z"` в `ShotUpdateControl`
    /// (дата выпуска снова 27.09) · вернуть 28.09.2026 датой проверки в `ShotUpdateControl.For()`.
    /// </summary>
    [AvaloniaFact]
    public void Кадр_обновления_называет_даты_выпуска_2_4_0()
    {
        var text = ShowAndRead(StartModes.ShotSettingsUpdate, "24.10.2026");

        // U16 (2.4.0): витрина называет СВОЙ выпуск — 2.4.0, дата 24.10.2026, проверка та же.
        Assert.Contains("24.10.2026", text, StringComparison.Ordinal);
        Assert.Contains("2.4.0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("27.09.2026", text, StringComparison.Ordinal);
        Assert.DoesNotContain("28.09.2026", text, StringComparison.Ordinal);
        Assert.DoesNotContain("18.10.2026", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// КАДРЫ ОБНОВЛЕНИЯ ПОКАЗЫВАЮТ «ЕСТЬ НОВЕЕ», А НЕ «ОБНОВЛЯТЬСЯ НЕ НУЖНО» (DEF-009, 2.4.1).
    ///
    /// **От какого случая.** На кадре стояла установленная версия самой панели, и после выпуска
    /// 2.4.0 она совпала с образцовым выпуском: над готовой сборкой вставали «У вас последняя
    /// версия 2.4.0 — обновляться не нужно» и «Обновляться некуда: выпуск не новее установленной
    /// панели». Кадр противоречил сам себе и учил неправде. Теперь на кадре установлена **2.3.2**
    /// (выпущена 22.10.2026), а выпуск — **2.4.0** (выпущен 24.10.2026): обе версии настоящие,
    /// и доступным назван только существующий выпуск: 2.4.1 на кадре как «доступный» не называется.
    ///
    /// ⚠️ Проверяются ОБА кадра — и «до подготовки», и «подготовлено»: противоречие жило не в одном.
    /// Смотрится ТЕКСТ СОБРАННОГО ОКНА, а не образцовая дверь: «обновляться не нужно» — это фраза,
    /// которую произносит окно, и ищется она теми же словами, какими её увидит человек.
    ///
    /// Мутации, которые обязаны упасть: вернуть установленную версию 2.4.0 (равна выпуску) ·
    /// заставить дверь отвечать «новее нет» при разных версиях.
    /// </summary>
    [AvaloniaFact]
    public void Кадры_обновления_показывают_новее_а_не_обновляться_не_нужно()
    {
        foreach (var kind in new[] { StartModes.ShotSettingsUpdate, StartModes.ShotSettingsUpdatePrepared })
        {
            // «2.3.2» — обязательная строка кадра: без неё мерить «есть новее» не на чем.
            var text = ShowAndRead(kind, "2.3.2");

            // ЕСТЬ НОВЕЕ: установлено 2.3.2, и решение называет выпуск v2.4.0.
            Assert.Contains(
                string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateCurrentFormat, "2.3.2"),
                text,
                StringComparison.Ordinal);

            Assert.Contains(
                string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateAvailableFormat, "v2.4.0"),
                text,
                StringComparison.Ordinal);

            // И НИ ОДНОЙ ФРАЗЫ «ОБНОВЛЯТЬСЯ НЕ НУЖНО» — ни решения, ни подсказки под кнопками.
            Assert.DoesNotContain(
                string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateWindowLatestFormat, "2.4.0"),
                text,
                StringComparison.Ordinal);

            Assert.DoesNotContain(PanelStrings.UpdateAlreadyLatest, text, StringComparison.Ordinal);

        // ДОСТУПНЫМ КАДР НАЗЫВАЕТ ТОЛЬКО СУЩЕСТВУЮЩИЙ ВЫПУСК: образцовый 2.4.0 (проверено выше).
        // ⚠️ Запрет литерала «2.4.1» целиком был НЕВЕРЕН и уронил выпускной прогон 24.10.2026: с выпуском
        // 2.4.1 это СОБСТВЕННАЯ версия панели, и она законно видна в окне («Версия: 2.4.1»). Запрещать
        // надо не слово, а утверждение — «доступен выпуск, которого нет».
        Assert.DoesNotContain(
            string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateAvailableFormat, "v2.4.1"),
            text,
            StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// КАДР «ПОДГОТОВЛЕНО» (DEF-009, 2.4.1): КАРТОЧКА ЗАМЕНЫ ЦЕЛИКОМ, ПУТЬ МАСКОЙ, ПОДГОТОВКА
    /// БОЛЬШЕ НЕ ДОСТУПНА, ХОД ПУСТ.
    ///
    /// **От какого случая.** У состояния «подготовлено» своего кадра не было: `settings-update`
    /// снимает раздел ДО подготовки, и карточку замены — единственную дверь, подменяющую файлы
    /// панели, — нельзя было увидеть глазами. Кадр собирается ТЕМ ЖЕ членом, что у съёмки
    /// (<see cref="Program.ShotWindowWithDoors"/>), и показывает состояние, которое произвёл ТОТ ЖЕ
    /// шов, каким идёт человек (`SettingsWindow.PrepareNow`), а не подставленное поле.
    ///
    /// ⚠️ **Меряется словом и числом, а не «кадр непустой»:**
    /// * карточка замены ВИДИМА и ЦЕЛИКОМ в видимой области прокрутки (верх/низ против `Viewport`) —
    ///   без прокрутки, потому что человек её не трогал;
    /// * кнопка замены на месте и доступна (иначе кадр показывает карточку без двери);
    /// * строка сборки называет путь ПОД МАСКОЙ `~` и версию образцового выпуска ЛИТЕРАЛАМИ,
    ///   а имени пользователя в ней нет (красная линия 7);
    /// * кнопка подготовки ВЫКЛЮЧЕНА: готовить больше нечего;
    /// * строка хода пуста, полоса прогресса скрыта — та же пара, что у окна после успеха.
    ///
    /// Мутации, которые обязаны упасть здесь: «дверь подготовленного кадра снова отказывает»
    /// (карточки нет) · «кадр снимает состояние ДО подготовки» · «путь показан без маски».
    /// </summary>
    [AvaloniaFact]
    public void Кадр_подготовленного_обновления_показывает_карточку_замены()
    {
        var window = Build(StartModes.ShotSettingsUpdatePrepared).Window;

        window.Show();
        PanelTestStand.Settle();

        try
        {
            var settings = (DshPanel.Views.SettingsWindow)window;

            var card = settings.FindControl<Border>("UpdateReplaceCard")!;
            var replace = settings.FindControl<Button>("UpdateReplaceButton")!;
            var prepare = settings.FindControl<Button>("UpdatePrepareButton")!;
            var staged = settings.FindControl<TextBlock>("UpdatePreparedText")!;

            Assert.True(card.IsVisible, "карточки замены на кадре нет — кадр снял состояние ДО подготовки");
            Assert.True(replace.IsVisible, "кнопки замены на кадре нет — карточка показана без двери");
            Assert.True(replace.IsEnabled, "кнопка замены на кадре недоступна");

            // ПУТЬ — МАСКОЙ И С ВЕРСИЕЙ ОБРАЗЦОВОГО ВЫПУСКА, литералами: проверка о том, что видит
            // человек. «Users» в пути значило бы, что маска не легла и в витрину уехало имя.
            var text = staged.Text ?? string.Empty;

            Assert.Contains(@"~\AppData\Local\DshPanel2\state\update\2.4.0", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Users", text, StringComparison.OrdinalIgnoreCase);

            // ПОДГОТОВКА БОЛЬШЕ НЕ ДОСТУПНА, ХОД ПУСТ — как у окна после успешной подготовки.
            Assert.False(prepare.IsEnabled, "кнопка подготовки на кадре доступна, хотя сборка уже готова");
            Assert.Equal(string.Empty, settings.FindControl<TextBlock>("UpdateProgressText")!.Text ?? string.Empty);
            Assert.False(settings.FindControl<ProgressBar>("UpdateProgressBar")!.IsVisible);

            // КАРТОЧКА ЦЕЛИКОМ БЕЗ ПРОКРУТКИ: человек колесо не трогал.
            var scroll = settings.FindControl<ScrollViewer>("ContentScroll")!;

            scroll.Offset = new Vector(0, 0);
            PanelTestStand.Settle();

            var at = card.TranslatePoint(new Point(0, 0), scroll) ?? default;

            Assert.True(
                at.Y >= 0 && at.Y + card.Bounds.Height <= scroll.Viewport.Height,
                $"карточка замены не видна целиком: верх {at.Y:0.#}, низ {at.Y + card.Bounds.Height:0.#} "
                + $"при видимой области {scroll.Viewport.Height:0.#}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// КАДР «ПИКИ И ТАРИФЫ» ПОКАЗЫВАЕТ ЦЕНЫ, А НЕ ОТКАЗ ПРОГОНА (t13, 18.10.2026).
    ///
    /// **От какого случая.** Блок «Стоимость» на витрине был ПУСТ и объяснял: «{PriceLocked}».
    /// Человек читает это как «панель ничего не умеет», а не как «здесь был прогон без сети».
    /// Лечение — образцовая дверь цен (<see cref="ShotDrawers.Pricing"/>), а не правка окна.
    ///
    /// **Что проверяется.** Текст собранного окна: пары образцовых цен (0,25 вне пика / 0,50 в пик —
    /// ровно те, что в образце истории цен), дата проверки цен 18.10.2026 и ОТСУТСТВИЕ строк
    /// «цены не читались» / «ещё не спрашивали».
    ///
    /// **Мутации, которые обязаны упасть:** вернуть в ветвь кадра «пики» настоящий контроллер цен
    /// (пустой «Стоимость» + строка про прогон) · убрать строки из `ShotSamples.PricingSnapshotSample`.
    /// </summary>
    [AvaloniaFact]
    public void Кадр_пиков_показывает_цены_а_не_отказ_прогона()
    {
        var text = ShowAndRead(StartModes.ShotPeaks, "$0.3");

        // U16 (2.4.0): числа — со страницы цен 24.10.2026 (deepseek-flash, фикстура pricing-en.html).
        Assert.Contains("$0.15", text, StringComparison.Ordinal);
        Assert.Contains("$0.3", text, StringComparison.Ordinal);
        Assert.Contains("24.10.2026", text, StringComparison.Ordinal);
        Assert.DoesNotContain(PanelStrings.PriceLocked, text, StringComparison.Ordinal);
        Assert.DoesNotContain(PanelStrings.PriceNeverRead, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// КАДР «ДВА НАЙДЕННЫХ СЕРВЕРА» ОБРАЗЦОВЫХ ЗНАЧЕНИЙ НЕ ПОЛУЧАЕТ.
    ///
    /// **Зачем отдельно.** Это единственный кадр с НАСТОЯЩЕЙ моделью окна и подставленной пробой
    /// процессов: его дело — пометки «наш движок (версия)» и «чужой», и образцовые двери заменяют
    /// ему дело помехой. Мутация AJ9 переворачивала ровно этот выбор, и падать было нечему.
    ///
    /// ⚠️ Проверка ищет ЗНАЧЕНИЯ каталога, а не «пусто/непусто»: кадр обязан быть непустым —
    /// он просто не имеет права быть образцовым. Что он непуст, доказывают ДВЕ НАХОДКИ, пришедшие
    /// в модель окна: без них «нет образцового» зеленело бы на окне без содержания вовсе.
    /// </summary>
    [AvaloniaFact]
    public void Кадр_двух_находок_образцовых_значений_не_получает()
    {
        var built = Build(StartModes.ShotMainTwoServers);

        Assert.Empty(built.Doors);

        var main = (DshPanel.Views.MainWindow)built.Window;

        main.Show();
        main.RefreshNow();
        PanelTestStand.Settle();

        try
        {
            // ДВЕ НАХОДКИ — и это проба процессов, которую этому кадру подставляют нарочно:
            // пометки «наш движок (версия)» и «чужой» и есть его содержание.
            // ДВЕ НАХОДКИ — и это проба процессов, которую этому кадру подставляют нарочно:
            // пометки «наш движок (версия)» и «чужой» и есть его содержание. Одна находка значила бы,
            // что кадр обещает два сервера, а показывает один.
            Assert.True(
                main.Discovery.Found.Count >= 2,
                $"в кадре двух находок найдено {main.Discovery.Found.Count} — кадр снял бы не то, что обещает имя: "
                + string.Join(" ; ", main.Discovery.Found.Select(f => f.Port + "/" + f.Pid + "/" + f.OwnVersion)));

            // И ОБЕ ОНИ — СВОИ-ИЛИ-ЧУЖИЕ, А НЕ ОБРАЗЦОВЫЕ: у находок свой порт, а не тот, что в каталоге
            // образцовых значений (иначе «кадр с образцовыми дверями» прошёл бы по ошибке).
            Assert.Contains(main.Discovery.Found, found => found.Port == 3099);
            Assert.Contains(main.Discovery.Found, found => found.Port == 3080);

            var panel = Read(main);

            foreach (var value in SampleValues())
            {
                Assert.DoesNotContain(value, panel, StringComparison.Ordinal);
            }
        }
        finally
        {
            main.Close();
        }
    }

    /// <summary>
    /// РЕШЕНИЕ О ДАННЫХ СПРАШИВАЕТСЯ, А НЕ ЗАШИТО В СБОРКУ.
    ///
    /// **Зачем.** Список дверей — свойство СБОРКИ. Если решение о данных перестанет до неё доезжать
    /// (мутация «все кадры настоящие»), список опустеет у ВСЕХ имён сразу, и проверка на одно имя
    /// этого не заметит: она сверит пустое с пустым.
    /// </summary>
    [AvaloniaFact]
    public void Решение_о_данных_доезжает_до_сборки()
    {
        var sampled = 0;

        foreach (var kind in StartModes.ShotWindows)
        {
            var doors = Build(kind).Doors;

            if (kind == StartModes.ShotMainTwoServers)
            {
                Assert.Empty(doors);

                continue;
            }

            if (doors.Count > 0) sampled++;
        }

        Assert.True(
            sampled >= 5,
            $"образцовые двери доехали всего до {sampled} кадров — сборка кадра перестала спрашивать решение");
    }

    /// <summary>
    /// ЧТО ОБЕЩАНО КАКОМУ ИМЕНИ: имя кадра → двери, которые обязаны до него доехать.
    ///
    /// ⚠️ Список ДУБЛИРУЕТ знание сборки — и это нарочно: сведи его с одной стороной, и проверка
    /// мерила бы свою выдумку. Он читается глазами при каждом новом имени кадра.
    /// </summary>
    private static IReadOnlyList<(string Kind, string[] Doors)> DoorPlan() => new[]
    {
        // ЕДИНСТВЕННЫЙ кадр с настоящей моделью: проба процессов подставлена, данные — нет.
        (StartModes.ShotMainTwoServers, Array.Empty<string>()),

        // Кадры без образцовых данных: диалоги и окна, которым двери не нужны вовсе.
        (StartModes.ShotIssue, Array.Empty<string>()),
        (StartModes.ShotPricingHistory, Array.Empty<string>()),
        (StartModes.ShotConfirmStop, Array.Empty<string>()),
        (StartModes.ShotConfirmEngine, Array.Empty<string>()),
        (StartModes.ShotBackupStop, Array.Empty<string>()),
        (StartModes.ShotUnsavedChanges, Array.Empty<string>()),
        (StartModes.ShotBackupClose, Array.Empty<string>()),
        (StartModes.ShotFirstRun, new[] { "first-run" }),

        // Кадры с образцовыми данными (у окна первого запуска своя дверь фактов — это она).
        (StartModes.ShotBackup, new[] { "backup" }),

        // ⚠️ «ПИКИ И ТАРИФЫ» — ДВЕ ДВЕРИ, А НЕ ОДНА (t13, 18.10.2026): «balance» и «pricing».
        // Вторая появилась потому, что без неё блок «Стоимость» на кадре был ПУСТ и объяснял
        // «цены не читались: это прогон проверки» — то есть витрина показывала нашу механику.
        (StartModes.ShotPeaks, new[] { "balance", "pricing" }),
        (StartModes.ShotSettings, AllSettingsDoors()),
        (StartModes.ShotSettingsGeneral, AllSettingsDoors()),
        (StartModes.ShotSettingsBackup, AllSettingsDoors()),
        (StartModes.ShotSettingsBalance, AllSettingsDoors()),
        (StartModes.ShotSettingsUpdate, AllSettingsDoors()),

        // ⚠️ «ПОДГОТОВЛЕНО» (DEF-009, 2.4.1) — те же двери, что у прочих кадров окна настроек:
        // двери — про ДАННЫЕ, а не про состояние раздела. Само состояние проверяет отдельная
        // проверка ниже (карточка замены, путь маской, подготовка недоступна).
        (StartModes.ShotSettingsUpdatePrepared, AllSettingsDoors()),
        (StartModes.ShotSettingsServer, AllSettingsDoors()),
        (StartModes.ShotSettingsPhone, AllSettingsDoors()),        (StartModes.ShotSettingsAbout, AllSettingsDoors()),
    };

    /// <summary>Двери окна настроек: три довода сборки и две, поставленные после вязки.</summary>
    private static string[] AllSettingsDoors() => new[] { "settings", "phone", "server", "balance", "update" };

    /// <summary>
    /// СОБРАТЬ КАДР ТЕМ ЖЕ ЧЛЕНОМ, ЧТО И СЪЁМКА, ПОД СВОИМ ВРЕМЕННЫМ КОРНЕМ.
    ///
    /// ⚠️ **Разница со съёмкой ровно одна и она названа:** владелец автозапуска не передаётся
    /// (`autostart: null`) — в съёмке он берётся из контекста прогона, а у проверки контекста нет
    /// и быть не должно. Автозапуск к дверям данных не относится: он показывает одну галочку.
    /// </summary>
    private static (Window Window, IReadOnlyList<string> Doors) Build(
        string kind,
        DateTimeOffset? peaksAt = null,
        Func<DateTime, int>? peaksOffsetAtUtc = null)
    {
        var paths = AppPaths.Under(PanelTestStand.TempDir());

        return Program.ShotWindowWithDoors(kind, paths, ShotServer(kind, paths), null, peaksAt, peaksOffsetAtUtc);
    }

    /// <summary>
    /// ПОКАЗАТЬ КАДР И ПРОЧИТАТЬ ВЕСЬ ЕГО ТЕКСТ — одной строкой.
    ///
    /// Берутся и <see cref="TextBlock"/>, и <see cref="TextBox"/>: число, вбитое в поле, человек
    /// видит так же, как подпись, и «в кадре его нет» было бы неправдой.
    ///
    /// ⚠️ **ОКНО ПОКАЗЫВАЕТСЯ РОВНО ОДИН РАЗ, и это выяснено прогоном.** Повторный `Show()` на том
    /// же окне сносит его содержимое: список копий после второго показа читался как одно слово
    /// «Копии», хотя строки в нём были. Поэтому строитель и читатель — ОДИН шаг, и окно после
    /// чтения закрывается: второго шанса прочитать то же окно у проверки нет.
    ///
    /// ⚠️ **Текст читается ДО ПОЛНОТЫ, а не одним заходом.** У окон панели есть анимация появления,
    /// и сразу после `Show()` в дереве только край: первое чтение главного окна давало одно слово
    /// «DSH», и проверка «нет образцового» зеленела бы на неготовом окне. Поэтому окно досчитывается,
    /// ПОКА ОБЕЩАННОЕ НЕ ПОЯВИТСЯ (и не больше двадцати шагов) — тогда «в окне этого нет» сказано
    /// о ПОКАЗАННОМ окне, а не о недосчитанном.
    ///
    /// ⚠️ <paramref name="mustFind"/> — строка, которой в тексте быть обязано: пустое окно
    /// не должно проходить проверку «образцового в нём нет».
    /// </summary>
    private static string ShowAndRead(
        string kind,
        string mustFind,
        DateTimeOffset? peaksAt = null,
        Func<DateTime, int>? peaksOffsetAtUtc = null)
    {
        var window = Build(kind, peaksAt, peaksOffsetAtUtc).Window;

        window.Show();

        // ⚠️ И ОДИН ОСМОТР МОДЕЛИ, как в съёмке: список найденных серверов наполняется
        // не от `Show()`, а ходом разведки — без него кадр «два найденных сервера» показывал бы
        // пустое место, и «образцового в нём нет» зеленело бы на окне без содержания вовсе.
        if (window is DshPanel.Views.MainWindow main) main.RefreshNow();

        try
        {
            var text = Read(window);

            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (mustFind.Length == 0 || text.Contains(mustFind, StringComparison.Ordinal)) break;

                PanelTestStand.Settle();

                text = Read(window);
            }

            if (mustFind.Length > 0)
            {
                Assert.True(
                    text.Contains(mustFind, StringComparison.Ordinal),
                    $"кадр «{(kind.Length == 0 ? "(без имени)" : kind)}» ничего не показал: "
                    + $"в нём нет даже «{mustFind}» — мерить нечего");
            }

            return text;
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Одно чтение текста окна — без досчитывания: досчитывает вызывающий.</summary>
    private static string Read(Window window)
    {
        var parts = new List<string>();

        foreach (var block in window.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!string.IsNullOrEmpty(block.Text)) parts.Add(block.Text);
        }

        foreach (var box in window.GetVisualDescendants().OfType<TextBox>())
        {
            if (!string.IsNullOrEmpty(box.Text)) parts.Add(box.Text);
        }

        return string.Join("\n", parts);
    }

    /// <summary>Число баланса на кадре — так, как его показывает окно.</summary>
    private static string Money() => ShotSamples.Balance.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// ОБРАЗЦОВЫЕ ЗНАЧЕНИЯ КАТАЛОГА — то, чего в кадре двух находок быть не должно.
    ///
    /// ⚠️ Версий движка и Node здесь НЕТ нарочно: версия движка настоящая (её панель и ставит),
    /// и она в этом кадре быть обязана — «наш движок (0.1.5-rc.2)» и есть его содержание.
    /// </summary>
    private static IEnumerable<string> SampleValues()
    {
        yield return Money();
        yield return ShotSamples.PhoneCode;
        yield return ShotSamples.PhoneAddress;
        yield return ShotSamples.NpmVersion;

        foreach (var entry in ShotSamples.Backups())
        {
            yield return Path.GetFileName(entry.Path);
        }

        foreach (var device in ShotSamples.Phone().Devices)
        {
            if (device.Name.Length > 0) yield return device.Name;
        }
    }
    /// <summary>
    /// КАДР НЕ ЗАВИСИТ ОТ АВТОЗАПУСКА НА МАШИНЕ (A3, находка C1 — условие заказчика).
    ///
    /// **От какого случая.** Прежде ветвь съёмки читала НАСТОЯЩУЮ запись `HKCU\...\Run` (только
    /// на чтение): у владельца кадр показывал «автозапуск включён» с его путём, на чистой машине —
    /// «выключен». Один и тот же кадр выходил разным, то есть не воспроизводимым, а в текст кадра
    /// попадал путь пользователя.
    ///
    /// **Как это доказывается здесь.** Кадр собирается ТЕМ ЖЕ членом, что у съёмки
    /// (<see cref="Program.ShotWindowWithDoors"/>), и текст читается из собранного окна:
    /// * с образцовой дверью съёмки (`ShotDrawers.Autostart`) — в тексте ОБРАЗЦОВЫЙ путь;
    /// * с дверью, которая изображает ДРУГУЮ машину (записи нет) — текст ДРУГОЙ, образцового пути
    ///   в нём нет.
    /// Значит текст кадра берётся У ДВЕРИ, а не из машины; а то, что съёмка даёт именно образцовую
    /// дверь, стережёт ворота по исходникам (`ShotSampleTests`: в разборе съёмки нет ни одного
    /// обращения к хранилищу автозапуска).
    ///
    /// **Мутации, которые обязаны упасть:** «вернуть чтение реестра в съёмке» (CD3 — падают ворота
    /// по исходникам) · «окно перестало читать дверь автозапуска» (здесь: образцового пути в тексте нет).
    /// </summary>
    [AvaloniaFact]
    public void Кадр_не_зависит_от_автозапуска_на_машине()
    {
        var sampled = ShowAndReadWithAutostart(ShotDrawers.Autostart(), ShotSamples.AutostartPath);

        Assert.Contains(ShotSamples.AutostartPath, sampled, StringComparison.Ordinal);

        var other = ShowAndReadWithAutostart(
            new AutostartProbe(new DshPanel.Autostart.AutostartState(
                DshPanel.Autostart.AutostartWhere.None, string.Empty, string.Empty)),
            PanelStrings.AutostartOff);

        Assert.DoesNotContain(ShotSamples.AutostartPath, other, StringComparison.Ordinal);
        Assert.NotEqual(sampled, other);
    }

    /// <summary>
    /// ДВЕРЬ АВТОЗАПУСКА ДЛЯ ПРОВЕРКИ: то же состояние, что показала бы другая машина, — и ни одного
    /// обращения к реестру (это подставная дверь, а не настоящий контроллер).
    /// </summary>
    private sealed class AutostartProbe : DshPanel.Autostart.IAutostartControl
    {
        public AutostartProbe(DshPanel.Autostart.AutostartState state) => State = state;

        public DshPanel.Autostart.AutostartState State { get; }

        public bool Writable => false;

        public void Refresh()
        {
            // Перечитывать нечего: состояние задано проверкой.
        }

        public bool Set(bool enabled) => false;
    }

    /// <summary>
    /// Собрать кадр раздела «Общее» с НАЗВАННОЙ дверью автозапуска, показать, прочитать текст и закрыть.
    /// Подпись автозапуска живёт именно в этом разделе — там же, где её видит человек.
    /// </summary>
    private static string ShowAndReadWithAutostart(DshPanel.Autostart.IAutostartControl autostart, string mustFind)
    {
        var paths = AppPaths.Under(PanelTestStand.TempDir());
        var kind = StartModes.ShotSettingsGeneral;

        var window = Program.ShotWindowWithDoors(kind, paths, ShotServer(kind, paths), autostart).Window;

        window.Show();

        try
        {
            var text = Read(window);

            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (text.Contains(mustFind, StringComparison.Ordinal)) break;

                PanelTestStand.Settle();
                text = Read(window);
            }

            Assert.True(
                text.Contains(mustFind, StringComparison.Ordinal),
                $"кадр «{kind}» ничего не сказал про автозапуск: в нём нет даже «{mustFind}» — мерить нечего");

            return text;
        }
        finally
        {
            window.Close();
        }
    }
}
