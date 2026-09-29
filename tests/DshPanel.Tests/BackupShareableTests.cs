using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Settings;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// «КОПИЯ ДЛЯ ПЕРЕДАЧИ» — решение владельца 26.09.2026: копией делятся с другим человеком, поэтому
/// ключ модели и секрет подписи cookie входа (<c>~/.dsh/.credentials.yaml</c>) в такой архив
/// <b>не кладутся вовсе</b>. Своя копия при этом снимается как раньше — со всем, включая ключ.
///
/// Проверки идут на НАСТОЯЩЕМ архиве в изолированной площадке (<see cref="AppPaths.Under"/>):
/// ни <c>~/.dsh</c>, ни ключи владельца этой проверкой не читаются и не пишутся.
///
/// ⚠️ Главное, что здесь доказывается, — **исключение файла работает на ОБОИХ способах записи**:
/// у своего обхода правило пропуска, у быстрого пути через 7-Zip — ключ <c>-xr!</c>. Способ,
/// которым копия снята, называется в отчёте (<see cref="ZipWriterKind"/>), и проверка требует
/// именно того способа, о котором говорит.
/// </summary>
public class BackupShareableTests
{
    // --- план: что исключается (чистое решение, без диска) --------------------

    /// <summary>
    /// Исключение — про ФАЙЛ, а не про группу: пропуск получает КАЖДЫЙ корень, внутри которого
    /// файл лежит. В изолированном прогоне домашний каталог движка лежит внутри корня панели,
    /// и правило «только у домашнего каталога» пропустило бы ключ второй группой — это и нашла
    /// эта проверка (в `docs\HISTORY.md` про такой случай уже была запись: файл едет двумя
    /// группами, когда корни пересекаются).
    ///
    /// Рабочей папке, которая этих файлов не содержит, правило не достаётся: там имя файла ничего
    /// не значит, и пропускать одноимённый файл из чужих проектов было бы потерей.
    ///
    /// ⚠️ Файлов ДВА, и второй добавлен 27.09.2026 вместе со ссылкой входа: в ней ТОКЕН, и «копия
    /// для передачи» уезжает другому человеку. Оба файла лежат под корнем панели, поэтому корень
    /// панели обязан пропускать оба.
    /// </summary>
    [Fact]
    public void План_для_передачи_исключает_файлы_во_всех_корнях_где_они_лежат()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            Directory.CreateDirectory(paths.DshHome);
            Directory.CreateDirectory(paths.SshDir);

            var work = Path.Combine(root, "projects");
            Directory.CreateDirectory(work);

            var plan = BackupPlanner.Full(
                paths, work, engine: null, withEngine: false, shareable: true);

            var home = plan.Roots.Single(item => item.Kind == BackupRootKind.DshHome);
            var panel = plan.Roots.Single(item => item.Kind == BackupRootKind.Panel);
            var projects = plan.Roots.Single(item => item.Kind == BackupRootKind.WorkingFolder);

            Assert.Equal(new[] { AppPaths.CredentialsFileName }, home.Source.SkipFileNames);

            // Панель СОДЕРЖИТ и домашний каталог движка, и файл ссылки входа — без этого ключ уехал
            // бы второй группой, а ссылка входа с токеном уехала бы к другому человеку.
            Assert.Equal(
                new[] { AppPaths.CredentialsFileName, AppPaths.EntryLinkFileName },
                panel.Source.SkipFileNames);

            // Рабочая папка ни того, ни другого не содержит — правило к ней не применяется.
            Assert.Null(projects.Source.SkipFileNames);

            Assert.Contains(PanelStrings.BackupShareableNote, plan.Notes);
            Assert.Contains(PanelStrings.BackupEntryLinkNote, plan.Notes);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>Своя копия пропуска файла ключей НЕ получает: она снимается со всем, как раньше.</summary>
    [Fact]
    public void План_своей_копии_ничего_не_исключает()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            Directory.CreateDirectory(paths.DshHome);

            var plan = BackupPlanner.Full(paths, null, engine: null, withEngine: false, shareable: false);

            foreach (var item in plan.Roots) Assert.Null(item.Source.SkipFileNames);
            Assert.DoesNotContain(PanelStrings.BackupShareableNote, plan.Notes);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- настоящий архив: свой обход -----------------------------------------

    /// <summary>
    /// Своя копия: файл ключей доступа В АРХИВЕ, и опись это утверждает. Это половина пары —
    /// без неё «в копии для передачи файла нет» доказывало бы лишь то, что его нет нигде.
    /// </summary>
    [Fact]
    public void Своя_копия_кладёт_файл_ключей_и_опись_это_говорит()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            BuildSubject(paths);

            var archive = Path.Combine(paths.BackupsDir, "own.zip");
            var run = Copy(paths, archive, shareable: false, sevenZip: "");

            Assert.True(run.Ok, run.Error);

            var names = Names(archive);
            Assert.Contains(names, name => name == HomeGroup(paths) + "/" + AppPaths.CredentialsFileName);
            Assert.True(Manifest(archive)!.WithCredentials, "опись должна утверждать, что ключи в архиве есть");
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Копия для передачи своим обходом: файла ключей в архиве НЕТ, а остальное из домашнего
    /// каталога движка на месте — иначе «файла нет» одинаково верно для пустого архива.
    /// </summary>
    [Fact]
    public void Копия_для_передачи_своим_обходом_без_файла_ключей()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            BuildSubject(paths);

            var archive = Path.Combine(paths.BackupsDir, "share.zip");
            var run = Copy(paths, archive, shareable: true, sevenZip: "");

            Assert.True(run.Ok, run.Error);
            Assert.Equal(ZipWriterKind.BuiltIn, run.Writer);

            var names = Names(archive);

            Assert.DoesNotContain(names, name => name.EndsWith(AppPaths.CredentialsFileName, StringComparison.OrdinalIgnoreCase));

            // Домашний каталог движка в архиве всё-таки есть: сессия и настройки движка на месте.
            Assert.Contains(names, name => name == HomeGroup(paths) + "/sessions/proj/s/session.v3.jsonl.zstd");
            Assert.Contains(names, name => name == HomeGroup(paths) + "/settings.yaml");

            var manifest = Manifest(archive)!;
            Assert.False(manifest.WithCredentials, "опись не должна утверждать ключи, которых в архиве нет");
            Assert.True(manifest.TotalFiles > 0);

            // Группа домашнего каталога на месте: накат должен знать, куда раскладывать.
            Assert.True(manifest.Kinds.ContainsKey(HomeGroup(paths)));
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>В отчёте о снятии копии сказано словами, что файл ключей не положен.</summary>
    [Fact]
    public void Отчёт_копии_для_передачи_называет_исключение()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            BuildSubject(paths);

            var archive = Path.Combine(paths.BackupsDir, "share.zip");
            var run = Copy(paths, archive, shareable: true, sevenZip: "");

            Assert.True(run.Ok, run.Error);
            Assert.Contains("не положен", run.Summary());
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- настоящий архив: быстрый путь через 7-Zip ----------------------------

    /// <summary>
    /// Тот же запрет — НА БЫСТРОМ ПУТИ. Здесь исключение делает не наш обход, а 7-Zip своим
    /// ключом <c>-xr!</c>, поэтому проверка обязана идти отдельно: правило, работающее на одном
    /// способе записи и молчащее на другом, — это копия с ключом, о которой никто не знает.
    ///
    /// 7-Zip на машине может и не быть (это законно: копия снимается и без него). Тогда проверка
    /// ТРЕБУЕТ, чтобы работал свой обход, и называет это — а не проходит молча «как бы через 7-Zip».
    /// </summary>
    [Fact]
    public void Копия_для_передачи_через_7зайп_тоже_без_файла_ключей()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            BuildSubject(paths);

            var found = SevenZip.Find();
            var sevenZip = SevenZip.Works(found) ? found : null;

            var archive = Path.Combine(paths.BackupsDir, "share-7z.zip");
            var run = Copy(paths, archive, shareable: true, sevenZip: sevenZip ?? "");

            Assert.True(run.Ok, run.Error);

            var names = Names(archive);

            Assert.DoesNotContain(names, name => name.EndsWith(AppPaths.CredentialsFileName, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(names, name => name == HomeGroup(paths) + "/settings.yaml");
            Assert.False(Manifest(archive)!.WithCredentials);

            if (sevenZip is null)
            {
                // 7-Zip нет: проверка это НАЗЫВАЕТ и требует, чтобы сработал свой обход.
                Assert.Equal(ZipWriterKind.BuiltIn, run.Writer);
            }
            else
            {
                // 7-Zip есть: проверено именно на нём, и это требует отчёт о способе записи.
                Assert.Equal(ZipWriterKind.SevenZip, run.Writer);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- настройки: поле осталось МЁРТВЫМ ------------------------------------

    /// <summary>
    /// СТАРОЕ ПОЛЕ НАСТРОЕК БОЛЬШЕ НИЧЕГО НЕ РЕШАЕТ — и при этом не объявляется незнакомым.
    ///
    /// Решение владельца 27.09.2026 (п. 11 <c>docs\DESIGN.md</c>): передача — разовое действие
    /// на одну копию, из настроек она убрана. Но ключ <c>backupShareable</c> У ЧЕЛОВЕКА В ФАЙЛЕ УЖЕ
    /// ЛЕЖИТ (его писала прежняя сборка), а список известных ключей панель берёт у самого типа
    /// настроек: убери свойство — и человеку объявили бы незнакомым ключ его собственного файла.
    ///
    /// Поэтому проверяются РАЗОМ три вещи, и каждая — про правду человеку:
    ///
    /// 1. файл с <c>"backupShareable": true</c> читается БЕЗ замечания о незнакомых ключах;
    /// 2. значение в памяти при этом <c>false</c> — дверь настроек (<c>SettingsStore.Clean</c>)
    ///    приводит его к нулю и на чтении, и на записи, то есть первое же сохранение стирает поле
    ///    окончательно (и это цель, а не потеря);
    /// 3. скопировать передаваемую копию ИЗ ФАЙЛА НАСТРОЕК нельзя: контроллер поверх этих
    ///    настроек снимает СВОЮ копию — файл ключей доступа в архиве есть.
    /// </summary>
    [Fact]
    public void Мёртвое_поле_старых_настроек_не_делает_копию_передаваемой()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            BuildSubject(paths);

            // Так файл выглядит у человека, который включал «копию для передачи» в прежней сборке.
            Directory.CreateDirectory(paths.Root);
            File.WriteAllText(
                paths.SettingsFile,
                "{\n  \"backupShareable\": true,\n  \"backupWithKeys\": false\n}\n");

            var store = new SettingsStore(paths.SettingsFile);
            var load = store.Load();

            Assert.True(load.Ok, load.Problem);
            Assert.Equal(string.Empty, load.Problem);
            Assert.False(load.Settings.BackupShareable, "мёртвое поле обязано читаться как false");

            var settings = Settings(paths, store);
            var controller = Controller(paths, settings, store);

            var archive = Path.Combine(paths.BackupsDir, "from-dead-setting.zip");
            var run = controller.CreateCopy(archive, serverRunning: false, shareable: false);

            Assert.True(run.Ok, run.Error);
            Assert.Contains(
                Names(archive),
                name => name.EndsWith(AppPaths.CredentialsFileName, StringComparison.OrdinalIgnoreCase));

            // И сохранение настроек стирает поле окончательно: в файле его больше нет вовсе.
            Assert.True(store.Save(load.Settings));
            Assert.DoesNotContain(
                "\"backupShareable\": true",
                File.ReadAllText(paths.SettingsFile),
                StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- контроллер: решение приходит ПАРАМЕТРОМ ------------------------------

    /// <summary>
    /// РЕШЕНИЕ О ПЕРЕДАЧЕ ПРИХОДИТ ПАРАМЕТРОМ, а не берётся из настроек: у копии оно одно на вызов.
    /// Проверяется СКВОЗНЫМ путём контроллера — и обе половины пары: с <c>shareable: true</c> файла
    /// ключей в архиве нет, с <c>false</c> — есть.
    ///
    /// ⚠️ Прежде эта проверка называлась «решение читается из настроек и живёт в файле» и была
    /// переписана: настройки перестали быть местом правды для передачи (п. 11), и утверждать
    /// прежнее значило бы закреплять отменённое решение.
    /// </summary>
    [Fact]
    public void Контроллер_берёт_решение_передачи_параметром_а_не_из_настроек()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            BuildSubject(paths);

            var store = new SettingsStore(paths.SettingsFile);
            var settings = Settings(paths, store);
            var controller = Controller(paths, settings, store);

            var shared = Path.Combine(paths.BackupsDir, "share-parameter.zip");
            var sharedRun = controller.CreateCopy(shared, serverRunning: false, shareable: true);

            Assert.True(sharedRun.Ok, sharedRun.Error);
            Assert.DoesNotContain(
                Names(shared),
                name => name.EndsWith(AppPaths.CredentialsFileName, StringComparison.OrdinalIgnoreCase));

            var own = Path.Combine(paths.BackupsDir, "own-parameter.zip");
            var ownRun = controller.CreateCopy(own, serverRunning: false, shareable: false);

            Assert.True(ownRun.Ok, ownRun.Error);
            Assert.Contains(
                Names(own),
                name => name.EndsWith(AppPaths.CredentialsFileName, StringComparison.OrdinalIgnoreCase));

            // И в настройках от этого не появилось ничего: у передачи второго места правды нет.
            Assert.False(store.Load().Settings.BackupShareable);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// РАСПИСАНИЕ ПЕРЕДАВАЕМЫХ КОПИЙ НЕ СНИМАЕТ НИКОГДА. Ночная копия снимается сама, когда
    /// человека нет, и решение о передаче унаследовать ей неоткуда: контроллер передаёт
    /// <c>shareable: false</c> жёстко.
    ///
    /// ⚠️ Проверка идёт ЖИВЫМ путём: такт часов, настоящая копия в фоне, чтение архива. Прежнее
    /// поле настроек при этом стоит <c>true</c> — и это проверяется ОТДЕЛЬНО, потому что именно
    /// оно и было бедой, названной владельцем: настройка наследовалась ночными копиями.
    /// </summary>
    [Fact]
    public void Ночная_копия_не_бывает_передаваемой()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            BuildSubject(paths);

            var store = new SettingsStore(paths.SettingsFile);
            var settings = Settings(paths, store);

            // Даже если в файле лежит старое «да» — ночная копия обязана остаться своей.
            store.Save(new PanelSettings
            {
                BackupFolder = paths.BackupsDir,
                BackupScheduleEnabled = true,
                BackupEveryHours = 24,
                BackupShareable = true,
            });

            var controller = Controller(paths, settings, store);

            var due = controller.Tick(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            Assert.True(due.Take, "расписание не сработало: " + due.Reason);

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && Directory.GetFiles(paths.BackupsDir, "dsh2-backup-*.zip").Length == 0)
            {
                System.Threading.Thread.Sleep(50);
            }

            var archive = Directory.GetFiles(paths.BackupsDir, "dsh2-backup-*.zip").Single();

            Assert.Contains(
                Names(archive),
                name => name.EndsWith(AppPaths.CredentialsFileName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- запрет сочетания: приватные ключи + передача -------------------------

    /// <summary>
    /// СОЧЕТАНИЕ «ПРИВАТНЫЕ КЛЮЧИ + КОПИЯ ДЛЯ ПЕРЕДАЧИ» ЗАПРЕЩЕНО, и запрет живёт в ОДНОМ месте
    /// домена (<see cref="BackupPlanner.Full"/>). Решение владельца 29.09.2026 (п. 12): вместе они
    /// дают архив с приватными ключами человека и без ключа модели — ровно тот, который передавать
    /// нельзя, а он назван «для передачи».
    ///
    /// Проверяются три вещи: отказ называется СЛОВАМИ, копия при этом НЕ снимается (файла нет),
    /// и та же причина выдаётся дверью, которой пользуются окно и режим без окна.
    /// </summary>
    [Fact]
    public void Сочетание_ключей_и_передачи_отвергается_до_всякой_работы()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            BuildSubject(paths);

            Directory.CreateDirectory(paths.SshDir);
            File.WriteAllText(Path.Combine(paths.SshDir, "id_test"), "ключ");

            Assert.Equal(
                PanelStrings.BackupShareableWithKeysRefused,
                BackupPlanner.ShareableWithKeysRefusal(withKeys: true, shareable: true));

            Assert.Equal(string.Empty, BackupPlanner.ShareableWithKeysRefusal(withKeys: true, shareable: false));
            Assert.Equal(string.Empty, BackupPlanner.ShareableWithKeysRefusal(withKeys: false, shareable: true));

            var plan = BackupPlanner.Full(
                paths, null, engine: null, withEngine: false, withKeys: true, shareable: true);

            Assert.False(plan.Ok, "запрещённое сочетание прошло в план");
            Assert.Equal(PanelStrings.BackupShareableWithKeysRefused, plan.Error);
            Assert.Empty(plan.Roots);

            var archive = Path.Combine(paths.BackupsDir, "refused.zip");
            var run = BackupEngine.Run(
                plan,
                new BackupRequest(archive, SevenZipPath: "", Verify: false),
                paths);

            Assert.False(run.Ok);
            Assert.Contains(PanelStrings.BackupShareableWithKeysRefused, run.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(archive), "отказ обязан быть ДО первой записи, а файл архива появился");

            // Тот же запрет и через контроллер: обойти его окном нельзя.
            var store = new SettingsStore(paths.SettingsFile);
            var settings = Settings(paths, store);
            var controller = Controller(paths, settings, store);

            // Разрешение на ключи включается через ВЛАДЕЛЬЦА настроек и ПОСЛЕ сборки контроллера:
            // стенд пишет свои настройки при сборке (папка копий), а прямое `store.Save` не увидели
            // бы ни владелец (у него своя копия настроек в памяти), ни контроллер за ним —
            // на этом проверка и попалась.
            Assert.True(settings.Save(new PanelSettings
            {
                BackupFolder = paths.BackupsDir,
                BackupWithKeys = true,
            }));

            var viaController = controller.CreateCopy(archive, serverRunning: false, shareable: true);

            Assert.False(viaController.Ok);
            Assert.Contains(
                PanelStrings.BackupShareableWithKeysRefused, viaController.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(archive));
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- ссылка входа: тот же запрет, что у ключа ------------------------------

    /// <summary>
    /// ССЫЛКА ВХОДА в «копии для передачи» не уезжает, а в СВОЕЙ копии остаётся. Пара из двух
    /// половин, как и у ключа: без второй «ссылки нет» доказывало бы лишь то, что её нет нигде.
    ///
    /// ⚠️ В ссылке ТОКЕН входа в панель, и «копия для передачи» уезжает ДРУГОМУ человеку
    /// (красная линия 7). Файл лежит под КОРНЕМ ПАНЕЛИ, поэтому исключение обязано работать
    /// у корня панели, а не только у домашнего каталога движка.
    /// </summary>
    [Fact]
    public void Копия_для_передачи_не_берёт_ссылку_входа_а_своя_берёт()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            BuildSubject(paths);

            Directory.CreateDirectory(paths.StateDir);
            File.WriteAllText(paths.EntryLinkFile, "http://127.0.0.1:3080/?token=SECRET-TOKEN\n");

            var store = new SettingsStore(paths.SettingsFile);
            var settings = Settings(paths, store);
            var controller = Controller(paths, settings, store);

            var shared = Path.Combine(paths.BackupsDir, "share-link.zip");
            var sharedRun = controller.CreateCopy(shared, serverRunning: false, shareable: true);

            Assert.True(sharedRun.Ok, sharedRun.Error);
            Assert.DoesNotContain(
                Names(shared),
                name => name.EndsWith(AppPaths.EntryLinkFileName, StringComparison.OrdinalIgnoreCase));

            // Замечание плана называет пропуск словами: пропуск без слова выглядит тихой потерей.
            Assert.Contains(PanelStrings.BackupEntryLinkNote, sharedRun.Summary());

            var own = Path.Combine(paths.BackupsDir, "own-link.zip");
            var ownRun = controller.CreateCopy(own, serverRunning: false, shareable: false);

            Assert.True(ownRun.Ok, ownRun.Error);
            Assert.Contains(
                Names(own),
                name => name.EndsWith(AppPaths.EntryLinkFileName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- площадка -------------------------------------------------------------

    /// <summary>Домашний каталог движка, папка панели и ключи доступа — то, из чего состоит копия.</summary>
    private static void BuildSubject(AppPaths paths)
    {
        Directory.CreateDirectory(Path.Combine(paths.DshHome, "sessions", "proj", "s"));
        File.WriteAllText(
            Path.Combine(paths.DshHome, "sessions", "proj", "s", "session.v3.jsonl.zstd"), "сессия");

        File.WriteAllText(Path.Combine(paths.DshHome, "settings.yaml"), "model: deepseek-flash\n");
        File.WriteAllText(paths.CredentialsPath, "важный ключ доступа\n");

        Directory.CreateDirectory(paths.BackupsDir);
    }

    private static string HomeGroup(AppPaths paths) => ZipLayout.Leaf(paths.DshHome);

    /// <summary>
    /// Настоящий контроллер настроек в изолированной площадке: он и есть владелец решения.
    /// Движка нет, папка прежней панели не читается — проверке здесь нечего у владельца брать.
    /// </summary>
    private static SettingsController Settings(AppPaths paths, SettingsStore store) =>
        new(store,
            paths,
            allowed: true,
            canReadOwnerEnvironment: false,
            locateEngine: () => null,
            server: () => null,
            applyTheme: _ => { },
            log: _ => { });

    /// <summary>
    /// Контроллер копий с ТЕМ ЖЕ владельцем настроек, что и окно настроек, — так проверяется
    /// настоящий путь значения, а не только домен под ним. Папка копий задаётся явно: умолчание —
    /// «Документы\DeepSeekHarness-Backups», и проверка не должна писать владельцу.
    /// </summary>
    private static BackupController Controller(
        AppPaths paths, SettingsController settings, SettingsStore store)
    {
        settings.Save(new PanelSettings { BackupFolder = paths.BackupsDir });

        return new BackupController(
            paths,
            () => settings.Settings,
            allowed: true,
            locateEngine: () => null,
            clock: () => new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero),
            log: _ => { },
            settingsOwner: settings);
    }

    private static BackupRunResult Copy(AppPaths paths, string archive, bool shareable, string sevenZip)
    {
        var plan = BackupPlanner.Full(
            paths, null, engine: null, withEngine: false, shareable: shareable);

        return BackupEngine.Run(
            plan,
            new BackupRequest(archive, SevenZipPath: sevenZip, Verify: false),
            paths);
    }

    private static string[] Names(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        return zip.Entries.Select(entry => entry.FullName).ToArray();
    }

    private static BackupManifest? Manifest(string archive) => ZipReader.Read(archive).Manifest;

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-shareable-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Временный каталог в %TEMP%: не убрался — не провал проверки.
        }
    }
}
