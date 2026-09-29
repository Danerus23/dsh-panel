using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Pricing;
using DshPanel.Settings;
using DshPanel.Shell;

namespace DshPanel.Update;

/// <summary>
/// ПРОБА ОБНОВЛЕНИЯ — то, что видно только на собранном exe и на настоящем файле настроек:
/// суточный гейт по настоящей отметке, запись следов через владельца настроек, «шарик один раз
/// на версию» и полное отсутствие сети. С третьего шага — ещё и ВЕСЬ путь установки на
/// подставной загрузке: скачивание, сверка суммы, распаковка, страховочная копия, сценарий
/// замены и чтение итога при следующем запуске.
///
/// ⚠️ **Сети здесь нет вовсе, и это не «стараемся».** Уговоры <see cref="IUpdateClient"/>
/// и <see cref="IUpdateDownload"/> подставляются: проба даёт готовый ответ GitHub и готовые
/// БАЙТЫ выпуска, а живой запрос делается только обычным запуском панели человеком. Прогон
/// проверки не имеет права ходить в интернет от его имени.
///
/// ⚠️ **Файлов панели проба не заменяет и сценарий замены не запускает.** Сценарий только
/// СОЧИНЯЕТСЯ (<see cref="UpdateScript"/>), а «папка панели», которую он заменил бы, — своя
/// временная папка пробы; проверяется обратное: после подготовки файлы панели НЕ тронуты.
///
/// ⚠️ **Файлов владельца проба не касается.** Свои настройки она заводит под своим временным
/// корнем (`%TEMP%\dsh-panel-update-selftest\&lt;guid&gt;`) и убирает за собой вместе с пустым
/// родительским каталогом. Расположение файла печатается, но времянка в отчёте — не личные данные.
///
/// Каждая строка отчёта ВЛИЯЕТ на исход, и в конце печатается ЧИСЛО: сколько утверждений
/// проверено и сколько из них прошло. Иначе «успех» означал бы лишь «исключений не было».
/// </summary>
public static class UpdateSelfTest
{
    /// <summary>Адреса подстановки: вымышленные и ведут в никуда — живого запроса не будет.</summary>
    private const string ArchiveUrl = "https://example.invalid/DshPanel.zip";

    private const string SumsUrl = "https://example.invalid/SHA256SUMS.txt";

    /// <summary>Подстановка вместо сети: считает вызовы и отдаёт заранее записанный ответ.</summary>
    private sealed class FakeClient : IUpdateClient
    {
        private readonly Func<UpdateRelease> _answer;

        public FakeClient(Func<UpdateRelease> answer) => _answer = answer;

        public int Calls { get; private set; }

        public List<string> Languages { get; } = new();

        public UpdateRelease Latest(string language, DateTimeOffset now)
        {
            Calls++;
            Languages.Add(language);

            return _answer();
        }
    }

    /// <summary>
    /// Подстановка вместо скачивания: отдаёт заранее известные БАЙТЫ и зовёт ход подготовки.
    /// Ни одного живого запроса: адреса здесь вымышленные.
    /// </summary>
    private sealed class FakeDownload : IUpdateDownload
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public FakeDownload Add(string url, byte[] bytes)
        {
            _files[url] = bytes;
            return this;
        }

        public void Save(string url, string path, Action<long, long>? progress)
        {
            if (!_files.TryGetValue(url, out var bytes))
                throw new InvalidOperationException("подстановки для адреса нет: " + url);

            progress?.Invoke(0, bytes.Length);
            File.WriteAllBytes(path, bytes);
            progress?.Invoke(bytes.Length, bytes.Length);
        }
    }

    public static int Run()
    {
        var report = new List<string>();
        var total = 0;
        var passed = 0;

        void Check(string what, bool ok)
        {
            total++;
            if (ok) passed++;
            report.Add($"{what} = {ok}");
        }

        // Момент у пробы свой и вымышленный: ни системных часов, ни даты на машине
        // проверка не касается — иначе отчёт менялся бы от запуска к запуску.
        var now = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var current = "2.0.0";

        var parent = Path.Combine(Path.GetTempPath(), "dsh-panel-update-selftest");
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));

        try
        {
            // ⚠️ ЖДАТЬ ПРИХОДИТСЯ, и это не слабость пробы, а свойство живого пути: запрос уходит
        // в ФОН (`Task.Run`), иначе сеть на нитке интерфейса заморозила бы окно панели. «Сейчас
        // ноль вызовов» без ожидания ничего не доказывало бы — вызов мог случиться через
        // миллисекунду. Тот же приём у проверок страницы цен и баланса.
        bool Wait(Func<bool> done)
        {
            var waited = 0;

            while (waited < 5000)
            {
                if (done()) return true;

                Thread.Sleep(10);
                waited += 10;
            }

            return false;
        }

        // --- 1. решения: версия, гейт, шарик, заметки -------------------------

            Check("числовая часть версии отбрасывает хеш сборки", UpdateDecisions.Numeric("2.0.0+481fb6b") == "2.0.0");
            Check("числовая часть версии отбрасывает букву тега", UpdateDecisions.Numeric(" v2.10.0 ") == "2.10.0");

            Check("2.10.0 новее 2.9.9 (сравнение по числам, а не по строкам)", UpdateDecisions.IsNewer("2.10.0", "2.9.9"));
            Check("2.9.9 НЕ новее 2.10.0", !UpdateDecisions.IsNewer("2.9.9", "2.10.0"));
            Check("та же версия с хешем сборки новее не считается", !UpdateDecisions.IsNewer("2.0.0+abc", "2.0.0+def"));
            Check("финальный выпуск новее предрелиза", UpdateDecisions.IsNewer("2.0.0", "2.0.0-rc.1"));

            Check("отметки нет — проверять пора", UpdateDecisions.ShouldCheck(now, string.Empty, isolatedRun: false));
            Check("час назад — рано", !UpdateDecisions.ShouldCheck(now, PricingDecisions.Stamp(now.AddHours(-1)), isolatedRun: false));
            Check("25 часов назад — пора", UpdateDecisions.ShouldCheck(now, PricingDecisions.Stamp(now.AddHours(-25)), isolatedRun: false));

            // Часы переведены назад: отметка в будущем. Проверять нельзя — иначе суточный гейт
            // обходится переводом часов туда-обратно.
            Check(
                "часы переведены назад — не проверяем",
                !UpdateDecisions.ShouldCheck(now, PricingDecisions.Stamp(now.AddHours(3)), isolatedRun: false));

            Check(
                "изолированный прогон по расписанию не проверяет ничего",
                !UpdateDecisions.ShouldCheck(now, string.Empty, isolatedRun: true));

            Check("первый раз о версии говорим", UpdateDecisions.ShouldAnnounce("2.1.0", string.Empty, current, string.Empty));
            Check(
                "повторно о той же версии не говорим",
                !UpdateDecisions.ShouldAnnounce("2.1.0", string.Empty, current, "2.1.0"));
            Check(
                "о пропущенной версии не говорим",
                !UpdateDecisions.ShouldAnnounce("2.1.0", "2.1.0", current, string.Empty));
            Check(
                "о версии, которая не новее пропущенной, и подавно молчим",
                !UpdateDecisions.ShouldAnnounce("2.1.0", "2.2.0", current, string.Empty));
            Check(
                "о более новой, чем пропущенная, говорим",
                UpdateDecisions.ShouldAnnounce("2.3.0", "2.1.0", current, string.Empty));
            Check(
                "о версии не новее установленной не говорим",
                !UpdateDecisions.ShouldAnnounce("2.0.0", string.Empty, current, string.Empty));

            const string notes =
                "## RU\n\nРусские заметки\n\n## EN\n\nEnglish notes\n\n## ZH\n\n中文说明";

            Check("заметки на русском берутся из блока RU", UpdateDecisions.PickNotes(notes, "ru") == "Русские заметки");
            Check("заметки на английском берутся из блока EN", UpdateDecisions.PickNotes(notes, "en") == "English notes");
            Check("заметки на китайском берутся из блока ZH", UpdateDecisions.PickNotes(notes, "zh") == "中文说明");
            Check(
                "чужой язык панели получает английский блок",
                UpdateDecisions.PickNotes(notes, "de") == "English notes");
            Check(
                "тело без блоков показывается целиком",
                UpdateDecisions.PickNotes("просто текст выпуска", "ru") == "просто текст выпуска");
            Check("пустое тело заметок — пустая строка", UpdateDecisions.PickNotes(string.Empty, "ru").Length == 0);

            // Машинные метки — ТОТ ЖЕ разбор, что заголовки, и это не запас на будущее: выпуск
            // 1.21.0 пришёл именно с ними, и панель, знавшая только заголовки, показывала русскому
            // человеку английский текст (замечание владельца 28.09.2026).
            const string marked =
                "## English\n\n<!-- dsh-notes:en -->\nEN TEXT\n<!-- /dsh-notes:en -->\n\n" +
                "## Русский\n\n<!-- dsh-notes:ru -->\nРУССКИЙ ТЕКСТ\n<!-- /dsh-notes:ru -->";

            Check("заметки с метками берутся на языке панели", UpdateDecisions.PickNotes(marked, "ru") == "РУССКИЙ ТЕКСТ");
            Check("метки сильнее заголовков того же языка", UpdateDecisions.PickNotes(marked, "en") == "EN TEXT");
            Check(
                "незакрытая метка блоком не считается",
                UpdateDecisions.PickNotes("<!-- dsh-notes:ru -->\nОБОРВАНО", "ru") == "<!-- dsh-notes:ru -->\nОБОРВАНО");

            // --- 2. ответ GitHub: разбор и отказы ---------------------------------

            using (var document = System.Text.Json.JsonDocument.Parse(
                "{\"tag_name\":\"v2.1.0\",\"html_url\":\"https://github.com/example/releases/tag/v2.1.0\"," +
                "\"published_at\":\"2026-09-27T10:00:00Z\",\"body\":\"## RU\\n\\nНовое\\n\\n## EN\\n\\nNew\"}"))
            {
                var parsed = HttpUpdateClient.Parse(document.RootElement, "ru", now);

                Check("ответ GitHub разобран", parsed.Ok);
                Check("тег выпуска прочитан", parsed.Latest == "v2.1.0");
                Check("страница выпуска прочитана", parsed.PageUrl.Length > 0);
                Check("дата выпуска прочитана", parsed.Published.Length > 0);
                Check("заметки выбраны по языку панели", parsed.Notes == "Новое");
                Check("сырое тело выпуска сохранено", parsed.NotesRaw.Contains("## EN", StringComparison.Ordinal));
            }

            var bad = HttpUpdateClient.Parse(
                System.Text.Json.JsonDocument.Parse("{\"tag_name\":\"\"}").RootElement, "ru", now);
            Check("ответ без тега выпуска — названный отказ", !bad.Ok && bad.Error.Length > 0);

            // --- 3. контроллер: подстановка вместо сети и следы в настройках ------

            var isolated = RunContext.Create(
                RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, root }, _ => null));
            var paths = isolated.Paths;

            report.Add($"файл настроек пробы = {paths.SettingsFile}");

            var store = new SettingsStore(paths.SettingsFile);
            var settings = new SettingsController(
                store, paths,
                allowed: true,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,
                applyTheme: _ => { },
                log: _ => { });

            var client = new FakeClient(() => new UpdateRelease(
                true, string.Empty, "v2.1.0", ProductLinks.ReleasesPageUrl, "2026-09-27T10:00:00Z",
                UpdateDecisions.PickNotes(notes, "ru"), notes, now));

            var announcements = new List<string>();

            using (var controller = new UpdateController(
                client,
                () => settings.Settings,
                () => "ru",
                () => current,
                allowed: true,
                clock: () => now,
                dispatch: action => action(),
                log: _ => { },
                remember: traces => settings.SetUpdateChecked(traces),
                rememberCheckedAt: stamp => settings.SetUpdateCheckedAt(stamp)))
            {
                Check("до проверки состояние — «ещё не проверяли»", controller.SourceText == PanelStrings.UpdateNeverChecked);

                // К шарику подписываемся ПОСЛЕ первой проверки: она ещё не автоматическая
                // (её позвал человек), а «один раз на версию» проверяется ниже повторной просьбой.
                controller.Check();
                Wait(() => client.Calls == 1 && !controller.Busy);

                Check("подстановка вместо сети позвана ровно один раз", client.Calls == 1);
                Check("язык панели дошёл до запроса", client.Languages.Count == 1 && client.Languages[0] == "ru");
                Check("проверка закончилась", !controller.Busy);
                Check("выпуск новее установленной панели", controller.Newer);
                Check("состояние называет версию выпуска", controller.StatusText.Contains("2.1.0", StringComparison.Ordinal));
                Check("заметки на языке панели", controller.Notes == "Русские заметки");

                Check(
                    "отметка проверки записана в настройки",
                    PricingDecisions.TryParse(settings.Settings.UpdateCheckedAt, out var recorded) && recorded == now);

                Check("версия выпуска записана в настройки", settings.Settings.UpdateLatest == "v2.1.0");
                Check("страница выпуска записана в настройки", settings.Settings.UpdatePageUrl.Length > 0);
                Check("сырое тело выпуска записано в настройки", settings.Settings.UpdateNotesRaw.Contains("## ZH", StringComparison.Ordinal));

                using (var again = new UpdateController(
                    client,
                    () => settings.Settings,
                    () => "ru",
                    () => current,
                    allowed: true,
                    clock: () => now,
                    dispatch: action => action(),
                    log: _ => { },
                    remember: traces => settings.SetUpdateChecked(traces),
                    rememberCheckedAt: stamp => settings.SetUpdateCheckedAt(stamp)))
                {
                    again.Announce += announcements.Add;
                    again.Tick(now);

                    // Суточный гейт спрашивается ДО фоновой работы, поэтому ожидание здесь —
                    // на то, чтобы вызов ПОЯВИЛСЯ, если он вообще собирался появиться.
                    Wait(() => client.Calls > 1);

                    Check("второй такт в те же сутки в сеть не идёт", client.Calls == 1);
                    Check("о той же версии шарика нет — о ней уже говорили", announcements.Count == 0);
                }

                controller.Announce += announcements.Add;
                controller.Check();
                Wait(() => !controller.Busy);

                Check("повторная просьба человека о той же версии шарика не даёт", announcements.Count == 0);
            }

            // Пропущенная версия: человек ответил — о ней не напоминаем.
            settings.Settings.UpdateSkippedVersion = "v2.1.0";
            settings.Save(settings.Settings);

            using (var skipped = new UpdateController(
                client,
                () => settings.Settings,
                () => "ru",
                () => current,
                allowed: true,
                clock: () => now,
                dispatch: action => action(),
                log: _ => { },
                remember: traces => settings.SetUpdateChecked(traces),
                rememberCheckedAt: stamp => settings.SetUpdateCheckedAt(stamp)))
            {
                var told = new List<string>();
                skipped.Announce += told.Add;

                skipped.Check();
                Wait(() => !skipped.Busy);

                Check("пропущенная версия распознана", skipped.Skipped);
                Check("о пропущенной версии шарика нет", told.Count == 0);

                using (var afterSkip = new UpdateController(
                    client,
                    () => settings.Settings,
                    () => "ru",
                    () => current,
                    allowed: true,
                    clock: () => now,
                    dispatch: action => action(),
                    log: _ => { },
                    remember: traces => settings.SetUpdateChecked(traces),
                    rememberCheckedAt: stamp => settings.SetUpdateCheckedAt(stamp)))
                {
                    afterSkip.Check();
                    Wait(() => !afterSkip.Busy);

                    Check("пропуск переживает перечитывание настроек", afterSkip.Skipped);
                }
            }

            // --- 4. отказ GitHub не летит наружу исключением ----------------------

            var failing = new FakeClient(() => UpdateRelease.Failed(PanelStrings.UpdateRateLimited, now));

            using (var refused = new UpdateController(
                failing,
                () => PanelSettings.Default,
                () => "ru",
                () => current,
                allowed: true,
                clock: () => now,
                dispatch: action => action(),
                log: _ => { },
                remember: _ => { },
                rememberCheckedAt: _ => { }))
            {
                refused.Check();
                Wait(() => !refused.Busy);

                Check("отказ GitHub не бросил исключения наружу", !refused.Busy);
                Check("отказ назван человеку словами", !refused.Result.Ok && refused.Result.Error.Length > 0);
                Check("отказ виден в строке состояния", refused.SourceText.Contains(PanelStrings.UpdateRateLimited, StringComparison.Ordinal));
            }

            // --- 5. прогон без права в сеть не ходит ВООБЩЕ ----------------------

            var silent = new FakeClient(() => UpdateRelease.Failed("не должны были позвать", now));
            var log = new List<string>();

            using (var locked = new UpdateController(
                silent,
                () => PanelSettings.Default,
                () => "ru",
                () => current,
                allowed: false,
                clock: () => now,
                dispatch: action => action(),
                log: log.Add,
                remember: _ => { },
                rememberCheckedAt: _ => { }))
            {
                locked.Start();
                locked.Tick(now);
                locked.Check();

                Check("прогон без права в сеть не ходил", silent.Calls == 0);
                Check("молчание прогона без права сказано словами", log.Contains(PanelStrings.PanelLogUpdateLocked));
                Check("строка состояния прогона без права названа", locked.SourceText == PanelStrings.UpdateLocked);
            }

            // --- 6. адрес выпусков назван и ведёт в публичный репозиторий ---------

            Check(
                "адрес выпусков ведёт в публичный репозиторий",
                ProductLinks.ReleasesApiUrl == "https://api.github.com/repos/Danerus23/dsh-panel/releases/latest");
            Check("человеческая страница выпусков отлична от адреса API",
                !string.Equals(ProductLinks.ReleasesPageUrl, ProductLinks.ReleasesApiUrl, StringComparison.Ordinal));
            Check("имя для GitHub задано (без него GitHub отвечает 403)", ProductLinks.UserAgent.Length > 0);

            // --- 6а. РЫЧАГ ПОДМЕНЫ АДРЕСА ВЫПУСКА --------------------------------
            //
            // Без этого рычага лабораторный прогон самообновления невозможен: он идёт на копии
            // панели с подставным выпуском на местной заглушке, и без подмены панель пошла бы
            // в настоящий GitHub. Проверяются ТРИ случая, и каждый — своим ответом: переменной
            // нет, переменная пуста, переменная задана. ⚠️ Сети здесь по-прежнему нет вовсе:
            // проверяется только то, КУДА панель пошла бы.
            {
                var was = Environment.GetEnvironmentVariable(ProductLinks.UpdateApiVariable);

                try
                {
                    Environment.SetEnvironmentVariable(ProductLinks.UpdateApiVariable, null);
                    Check("без переменной адрес выпусков — умолчание",
                        ProductLinks.UpdateApi == ProductLinks.ReleasesApiUrl);

                    Environment.SetEnvironmentVariable(ProductLinks.UpdateApiVariable, "   ");
                    Check("пустая переменная — это «не задана», а не «адреса нет»",
                        ProductLinks.UpdateApi == ProductLinks.ReleasesApiUrl);

                    Environment.SetEnvironmentVariable(
                        ProductLinks.UpdateApiVariable, "http://127.0.0.1:3099/releases/latest");
                    Check("заданная переменная подменяет адрес выпусков",
                        ProductLinks.UpdateApi == "http://127.0.0.1:3099/releases/latest");
                }
                finally
                {
                    Environment.SetEnvironmentVariable(ProductLinks.UpdateApiVariable, was);
                }
            }

            // --- 7. ВЕСЬ ПУТЬ УСТАНОВКИ на подставной загрузке -------------------
            //
            // Здесь прогоняется то, что делает кнопка «Скачать и подготовить»: скачивание,
            // сверка суммы, распаковка, сверка версии, страховочная копия и сценарий замены.
            // Всё — на СВОИХ байтах и под своим корнем: ни одного живого запроса, ни одной
            // чужой папки. И проверяется ОБРАТНОЕ тому, что делает сценарий: файлы панели
            // после подготовки остаются прежними.

            var archive = Zip(
                (UpdateStaging.PanelExeName, "новая панель 2.1.0"),
                ("DshPanel.dll", "новая библиотека"));

            var download = new FakeDownload()
                .Add(ArchiveUrl, archive)
                .Add(SumsUrl, Encoding.UTF8.GetBytes(SumsText((UpdateAssets.ArchiveName, archive))));

            var installed = Path.Combine(root, "Programs", "DSH Panel");
            Directory.CreateDirectory(installed);
            File.WriteAllText(Path.Combine(installed, UpdateStaging.PanelExeName), "прежняя панель");
            File.WriteAllText(Path.Combine(installed, "DshPanel.dll"), "прежняя библиотека");

            var assets = new ReleaseAssets(ArchiveUrl, string.Empty, SumsUrl);
            var ticks = new List<UpdateProgress>();

            UpdateRequest Request(bool allowed) => new(
                paths,
                current,
                installed,
                "v2.1.0",
                assets,
                allowed,
                IgnoreNewer: false,
                StartArgument: string.Empty,
                MutexName: string.Empty,
                At: now);

            // Версия внутри архива сверяется ОТДЕЛЬНЫМ разбором: свои байты свойств файла
            // не несут, а проверяется здесь не чтение свойств (оно проверено в тестах),
            // а весь остальной путь. Проверка суммы при этом настоящая — она считается с файла.
            var preparation = UpdateInstall.Prepare(Request(true), download, _ => "2.1.0", ticks.Add);

            Check("подготовка обновления прошла на подставной загрузке", preparation.Ok);
            Check("сценарий замены записан", File.Exists(preparation.ScriptPath));
            Check("шапка журнала замены записана", File.Exists(preparation.LogPath));
            Check("страховочная копия прежней панели снята", Directory.Exists(preparation.BackupFolder));
            Check("ход назвал этапы подготовки", ticks.Count > 0 && ticks.Any(tick => tick.Stage == UpdateStage.Backup));
            Check("ход назвал проценты, когда размер файла известен", ticks.Any(tick => tick.Percent == 100));

            // ГЛАВНОЕ УТВЕРЖДЕНИЕ ЭТОГО ШАГА: подготовка НЕ подменяет панель. Замена файлов —
            // отдельное нажатие человека и отдельный сценарий; до него папка панели не тронута.
            Check(
                "подготовка НЕ заменяет файлы панели",
                File.ReadAllText(Path.Combine(installed, UpdateStaging.PanelExeName)) == "прежняя панель");

            // ИТОГ ЧИТАЕТСЯ ПРИ СЛЕДУЮЩЕМ ЗАПУСКЕ: сборка новее установленной панели, а замена
            // не состоялась (сценарий не запускали) — человеку об этом говорят, и ровно один раз.
            var outcome = UpdateOutcomes.StartupNotice(paths, current, allowed: true);

            Check("итог прошлого обновления прочитан", outcome is not null);
            Check(
                "незаменившееся обновление названо неприменившимся",
                outcome?.Verdict == UpdateVerdict.NotApplied);
            Check("причина итога названа ключом", UpdateReasonLines.Knows(outcome?.ReasonKey));
            Check("причина итога сказана словами", UpdateReasonLines.Text(outcome?.ReasonKey).Length > 0);
            Check(
                "о неприменившемся обновлении говорят ОДИН раз",
                UpdateOutcomes.StartupNotice(paths, current, allowed: true) is null);

            // ПРАВО: без него движок отказывает ДО того, как что-то создаст.
            var withoutRight = UpdateInstall.Prepare(Request(false), download, _ => "2.1.0");

            Check(
                "без права человека обновление не готовится",
                !withoutRight.Ok && withoutRight.Refusal == UpdateRefusal.NotAllowed);

            // СТРОКИ ДЛЯ ЧЕЛОВЕКА — по ВСЕМ значениям, которые может вернуть ядро. Забудь строку
            // для нового отказа (итога, этапа) — эта проверка падает, а не молчит.
            Check(
                "у каждого отказа ядра есть строка в словаре",
                Enum.GetValues<UpdateRefusal>().All(refusal =>
                {
                    var key = UpdateRefusals.Key(refusal);
                    return key.Length == 0 || UpdateRefusalLines.Knows(key);
                }));

            Check(
                "у каждого итога замены есть строка в словаре",
                Enum.GetValues<UpdateResultKind>().All(kind => UpdateReasonLines.Knows(UpdateOutcomes.ReasonKeyFor(kind))));

            Check(
                "у каждого этапа подготовки есть подпись",
                Enum.GetValues<UpdateStage>().All(stage => UpdateStageLines.Knows(stage)));

            // Правило подстановки взято у ядра: «Format» в имени ключа — сюда идёт подробность.
            Check(
                "подробность отказа подставляется в строку",
                UpdateRefusalLines
                    .Line(UpdatePreparation.Refuse(UpdateRefusal.SumMismatch, "ожидалось одно, получено другое"))
                    .Contains("ожидалось одно", StringComparison.Ordinal));

            Check(
                "отказ без Format показывается как есть",
                UpdateRefusalLines.Line(UpdatePreparation.Refuse(UpdateRefusal.NoArchive, "мелочь"))
                == PanelStrings.UpdateNoArchive);

            report.Add($"утверждений проверено = {total}, прошло = {passed}");
        }
        catch (Exception ex)
        {
            total++;
            report.Add($"ОШИБКА: {ex.GetType().Name} — {ex.Message} = False");
        }
        finally
        {
            var gone = true;
            var why = string.Empty;

            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                try { if (Directory.Exists(parent)) Directory.Delete(parent); } catch { }
            }
            catch (Exception ex)
            {
                gone = false;
                why = $" ({ex.GetType().Name}: {ex.Message})";
            }

            total++;
            if (gone) passed++;

            report.Add(
                $"уборка: временный каталог пробы убран = {gone}{why}, " +
                $"пустого каталога проб нет = {!Directory.Exists(root)}");
        }

        var failed = passed != total;

        foreach (var line in report) Console.WriteLine("ОБНОВЛЕНИЕ| " + line);
        Console.WriteLine($"ОБНОВЛЕНИЕ: утверждений {total}, прошло {passed}, провалено {total - passed}");
        Console.WriteLine(failed ? "ОБНОВЛЕНИЕ ПРОВАЛ" : "ОБНОВЛЕНИЕ УСПЕХ");
        return failed ? 1 : 0;
    }

    /// <summary>Архив панели, каким его кладёт сборка: сама панель и что-то рядом.</summary>
    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var memory = new MemoryStream();

        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(text);
            }
        }

        return memory.ToArray();
    }

    /// <summary>
    /// Файл сумм в том виде, в каком его публикует выпуск: «хэш  имя файла». Хэш считается СВОИМ
    /// счётом, а не <see cref="UpdateSums.Sha256"/>: иначе проба сверяла бы панель с нею же,
    /// и неверный хэш в файле сумм остался бы незамеченным.
    /// </summary>
    private static string SumsText(params (string Name, byte[] Bytes)[] files) =>
        string.Join(
            Environment.NewLine,
            files.Select(file =>
                Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant() + "  " + file.Name));
}
