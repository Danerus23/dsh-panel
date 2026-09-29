using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Restore;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СТОРОЖ ЧУЖОЙ РАСКЛАДКИ В НАКАТЕ — отдельно от <c>RestoreTests</c>, потому что здесь проверяется
/// не «накат делает своё дело», а один конкретный случай, найденный на живом прогоне у владельца.
///
/// Случай. В папке копий лежали две НАСТОЯЩИЕ копии, снятые панелью v1 (<c>dsh-tray</c>) 24.09
/// и 25.09.2026: полные, 31 215 и 31 181 файл. Владелец выбрал копию, и план наката сказал
/// «к накату: 3 файл(ов) в 6 группах» — то есть три файла ключей и НИЧЕГО больше. Причина:
/// сторож в <see cref="RestoreEngine.Plan"/> требовал, чтобы ПУСТЫМИ были ВСЕ группы описи
/// (<c>groups.All(group => group.Files == 0)</c>), а у копии v1 одна группа совпадает по имени —
/// каталоги ключей и там и там зовутся <c>keys/&lt;номер&gt;-&lt;слаг&gt;</c> (v1:
/// <c>..\dsh-tray\BackupService.cs</c>, «<c>"keys/" + name</c>»). Эта группа давала 3 файла,
/// условие «все по нулю» не срабатывало — и сторож молчал.
///
/// Чем это опасно: при согласии «вернуть ключи» группа ключей становится выбранной, план
/// делается годным, накат раскладывает три файла и отчитывается об успехе, а ~31 000 файлов
/// (сессии, история, настройки, проекты) из архива не раскладываются вовсе. Ложный успех
/// в инструменте резервных копий — ровно то, ради чего сторож и написан.
///
/// Стенд — свой временный каталог: опись и записи строятся здесь, каталогов владельца проверка
/// не касается ни одной строкой.
/// </summary>
public class RestoreOldPanelTests
{
    // --- стенд ---------------------------------------------------------------

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "dsh old-panel " + Guid.NewGuid().ToString("N")[..8]);

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Временный каталог в %TEMP%: не убрался — не провал проверки.
        }
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Архив, собранный вручную: записи данных плюс опись — там, где её кладёт и сама панель
    /// (<see cref="ZipLayout.ManifestEntry"/>, первой записью архива).
    /// </summary>
    private static string MakeArchive(
        string root, string name, BackupManifest manifest, IEnumerable<string> entries)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var entry in entries) AddText(zip, entry, "проба\n");
            AddText(zip, ZipLayout.ManifestEntry, JsonSerializer.Serialize(manifest, BackupManifest.WriteOptions));
        }

        return path;
    }

    /// <summary>
    /// Опись копии ПРЕЖНЕЙ панели — ровно такая, какую писал v1
    /// (<c>..\dsh-tray\BackupService.cs</c>, «<c>manifest.Paths["dshHome"] = …</c>» и соседние):
    /// ключи — имена ПОНЯТИЙ, и поля «вид группы» (<see cref="BackupManifest.Kinds"/>) у неё нет
    /// НИ ОДНОГО. Это важная часть случая, а не украшение.
    ///
    /// А записи в архиве v1 звались именами папок: <c>dsh-home</c>, <c>appdata/DshPanel</c>,
    /// <c>engine/npm</c>, <c>engine/nodejs</c>, <c>installer</c> — и только каталоги ключей
    /// совпадали с описью (<c>keys/&lt;номер&gt;-&lt;имя&gt;</c>).
    /// </summary>
    private static BackupManifest OldPanelManifest(params string[] groups)
    {
        var recorded = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dshHome"] = @"C:\Users\Прежний\.dsh",
            ["appData"] = @"C:\Users\Прежний\AppData\Roaming\DshPanel",
            ["app"] = @"C:\Users\Прежний\AppData\Local\DshPanel",
            ["npmRoot"] = @"C:\Users\Прежний\AppData\Roaming\npm\node_modules",
            ["nodeDir"] = @"C:\Program Files\nodejs",
            ["keys/1-ssh"] = @"C:\Users\Прежний\.ssh",
        };

        var manifest = new BackupManifest
        {
            CreatedAt = "2026-09-25 21:00:00",
            App = "DshTray",
            AppVersion = "1.21.0",
            Machine = "ПРЕЖНЯЯ-МАШИНА",
            User = "прежний",
            WithEngine = true,
            WithSessions = true,
        };

        foreach (var group in groups) manifest.Paths[group] = recorded[group];

        return manifest;
    }

    /// <summary>Как выглядит копия v1 в архиве: имена папок, а не имена понятий из описи.</summary>
    private static readonly string[] OldPanelEntries =
    {
        "dsh-home/sessions/probe.txt",
        "engine/npm/dsh.cmd",
        "keys/1-ssh/id_probe",
    };

    // --- доказательство дефекта ----------------------------------------------

    /// <summary>
    /// ЖИВОЙ СЛУЧАЙ ЦЕЛИКОМ: опись v1 (имена понятий, ни одного вида группы) и её же архив,
    /// в котором совпала ровно ОДНА группа — каталог ключей. Такой архив обязан быть отвергнут
    /// ЦЕЛИКОМ, и причина обязана быть про копию прежней панели.
    ///
    /// Согласий проверяется ДВА: без ключей и с ними. Второе и есть опасность — именно оно делает
    /// группу ключей выбранной, план годным («3 файл(ов)»), а накат — «успешным» при тридцати
    /// одной тысяче неразложенных файлов.
    /// </summary>
    [Fact]
    public void Копия_прежней_панели_с_совпавшей_группой_ключей_не_накатывается()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);

            var archive = MakeArchive(
                root, "old-panel.zip",
                OldPanelManifest("dshHome", "appData", "app", "npmRoot", "nodeDir", "keys/1-ssh"),
                OldPanelEntries);

            foreach (var consent in new[] { new RestoreOptions(), new RestoreOptions(WithKeys: true) })
            {
                var plan = RestoreEngine.Plan(archive, paths, null, consent);

                Assert.False(plan.Ok, "план принял копию прежней панели: " + plan.Summary());
                Assert.Equal(PanelStrings.RestoreOldPanelArchive, plan.Error);

                // Причина называет себя словами, а не только ключом словаря.
                Assert.Contains("копия прежней панели", plan.Error);
                Assert.Contains("потерять данные", plan.Error);

                // И плана на «3 файла» нет вовсе: групп нет, значит и числа нет.
                Assert.Empty(plan.Groups);
                Assert.Equal(0, plan.Files);
                Assert.Empty(plan.Selected);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Отказ плана — не «отписка»: накат по такому плану не начинается, и три файла ключей
    /// на диск не ложатся. Иначе копия прежней панели возвращалась бы «успешно», а её данные —
    /// нет.
    /// </summary>
    [Fact]
    public void По_отказанному_плану_чужой_копии_ничего_не_раскладывается()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);

            var archive = MakeArchive(
                root, "old-panel.zip", OldPanelManifest("dshHome", "npmRoot", "keys/1-ssh"),
                OldPanelEntries);

            // Согласие на ключи — то самое, при котором дефект и выходил наружу.
            var options = new RestoreOptions(WithKeys: true, SafetyCopy: false);
            var plan = RestoreEngine.Plan(archive, paths, null, options);

            Assert.False(plan.Ok, plan.Summary());

            var run = RestoreEngine.Run(plan, options, paths);

            Assert.False(run.Ok, run.Summary());
            Assert.Contains("восстановление не начато", run.Error);
            Assert.Equal(0, run.Files);

            Assert.False(Directory.Exists(paths.SshDir), "ключи из чужой раскладки всё-таки легли");
            Assert.False(Directory.Exists(paths.DshHome), "данные из чужой раскладки всё-таки легли");
            Assert.Empty(run.Restored);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- положительный контроль ----------------------------------------------

    /// <summary>
    /// ПОЛОЖИТЕЛЬНЫЙ КОНТРОЛЬ: та же раскладка, но записанная 2.0 (имена групп — имена папок,
    /// и у каждой группы записан её вид), планом ПРИНИМАЕТСЯ, и все три группы в ней видны.
    ///
    /// Без него нельзя утверждать, что проверка ловит именно чужую раскладку: она могла бы
    /// отказывать всему подряд — и тогда доказательство выше ничего не стоит.
    /// </summary>
    [Fact]
    public void Раскладка_своей_панели_принимается()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);

            var manifest = new BackupManifest
            {
                CreatedAt = "2026-09-26 12:00:00",
                Machine = "ДРУГАЯ-МАШИНА",
                User = "Кто-то-Другой",
                Paths =
                {
                    ["dsh-home"] = @"C:\Users\Прежний\.dsh",
                    ["engine/npm"] = @"C:\Users\Прежний\AppData\Roaming\npm",
                    ["keys/1-ssh"] = @"C:\Users\Прежний\.ssh",
                },
                Kinds =
                {
                    ["dsh-home"] = BackupRootKinds.DshHome,
                    ["engine/npm"] = BackupRootKinds.EnginePackages,
                    ["keys/1-ssh"] = BackupRootKinds.KeyRing,
                },
            };

            var archive = MakeArchive(root, "own-panel.zip", manifest, OldPanelEntries);

            var plan = RestoreEngine.Plan(
                archive, paths, null, new RestoreOptions(WithEngine: true, WithKeys: true));

            Assert.True(plan.Ok, plan.Error);
            Assert.Equal(3, plan.Groups.Count);
            Assert.Equal(3, plan.Files);
            Assert.All(plan.Groups, group => Assert.Equal(1, group.Files));

            // Домашний каталог движка раскладывается всегда — и на СВОЁ место, а не по чужому пути.
            Assert.Equal(paths.DshHome, plan.Groups.Single(group => group.Prefix == "dsh-home").Target);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- копия прежней панели в ОБЩЕЙ папке копий (после развода имён 26.09.2026) --------------

    /// <summary>
    /// КОПИЯ ПРЕЖНЕЙ ПАНЕЛИ В ОБЩЕЙ ПАПКЕ — и накат по ней решается СОДЕРЖИМЫМ, а не именем.
    ///
    /// Папка копий у 1.x и 2.0 одна (решение владельца 26.09.2026), разводятся только имена:
    /// у 2.0 свой префикс (<c>dsh2-…</c>). Значит у сторожа Д1 меняется ровно одно обстоятельство —
    /// имя файла, — и проверка сторожит, что от этого не поехало НИЧЕГО:
    ///
    /// 1. **архив прежней панели в СПИСКЕ не показывается** (решение владельца 28.09.2026:
    ///    *«Удали у меня просто эти строки от старой версии панели»*), но **на диске остаётся целым
    ///    и читаемым** — панель перестала его ПОКАЗЫВАТЬ, а не удалила. Это и закрыло прежний
    ///    вопрос «чем возвращать архивы 1.x»: возвращать нечего, потому что предлагать нечего;
    /// 2. **прежний отказ Д1 на месте**: раскладка v1 не понята, план не годен и называет причину.
    ///    Сторож смотрит в ОПИСЬ и АРХИВ, а не в имя файла, — поэтому чужая фамилия файла его
    ///    не обходит и не ломает;
    /// 3. **имя само по себе накат не запрещает**: файл с именем панели 1.x, но с ПОНЯТНОЙ раскладкой
    ///    раскладывается как обычно. Это и есть «решает содержимое».
    ///
    /// Без третьей половины проверка доказывала бы только запреты, а вторая половина без первой
    /// ничего не стоила бы: отказ получался бы и у копии, которую человек не может выбрать.
    /// </summary>
    [Fact]
    public void Копия_панели_1_x_из_общей_папки_скрыта_а_файл_цел_и_решается_содержимым()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);
            var folder = Path.Combine(root, "Копии");
            Directory.CreateDirectory(folder);

            // Настоящая копия прежней панели: её опись (имена понятий, ни одного вида группы),
            // её раскладка (имена папок), её имя файла.
            const string foreignName = "dsh-backup-2026-09-25-210000-full.zip";

            var foreign = MakeArchive(
                folder, foreignName,
                OldPanelManifest("dshHome", "appData", "app", "npmRoot", "nodeDir", "keys/1-ssh"),
                OldPanelEntries);

            // 1. В списке её НЕТ, а на диске она ЕСТЬ и читается — одно без другого ничего не стоит.
            Assert.Empty(BackupFolder.List(folder).Entries);

            Assert.True(File.Exists(foreign), "список удалил чужой архив: он имеет право только не показывать его");
            Assert.Equal(foreignName, Path.GetFileName(foreign));

            var entry = BackupFolder.Inspect(foreign);
            Assert.True(entry.Readable, entry.Error);

            // 2. Сторож Д1 по-прежнему отказывает — по содержимому, а не по имени файла.
            var refused = RestoreEngine.Plan(foreign, paths, null, new RestoreOptions(WithKeys: true));

            Assert.False(refused.Ok, "план принял чужую раскладку: " + refused.Summary());
            Assert.Equal(PanelStrings.RestoreOldPanelArchive, refused.Error);
            Assert.Equal(0, refused.Files);

            // 3. А имя панели 1.x само по себе накат не отменяет: та же фамилия файла с ПОНЯТНОЙ
            //    описью (имена групп — имена папок, у каждой записан её вид) раскладывается.
            var understood = new BackupManifest
            {
                CreatedAt = "2026-09-25 22:00:00",
                Machine = "ПРЕЖНЯЯ-МАШИНА",
                User = "прежний",
                Paths =
                {
                    ["dsh-home"] = @"C:\Users\Прежний\.dsh",
                    ["engine/npm"] = @"C:\Users\Прежний\AppData\Roaming\npm",
                    ["keys/1-ssh"] = @"C:\Users\Прежний\.ssh",
                },
                Kinds =
                {
                    ["dsh-home"] = BackupRootKinds.DshHome,
                    ["engine/npm"] = BackupRootKinds.EnginePackages,
                    ["keys/1-ssh"] = BackupRootKinds.KeyRing,
                },
            };

            var named = MakeArchive(
                folder, "dsh-backup-2026-09-25-220000-full.zip", understood, OldPanelEntries);

            var plan = RestoreEngine.Plan(
                named, paths, null, new RestoreOptions(WithEngine: true, WithKeys: true));

            Assert.True(plan.Ok, plan.Error);
            Assert.Equal(3, plan.Files);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- вторая половина сторожа: данных нет вовсе ---------------------------

    /// <summary>
    /// АРХИВ БЕЗ ДАННЫХ ВОВСЕ: в нём только опись, а группа ключей в описи есть и по имени
    /// совпала бы с архивом. Отказ обязателен и здесь: «план на 0 файл(ов)» — не «принято»,
    /// а накат такого архива не начинается вовсе.
    ///
    /// Эта проверка стережёт ВТОРУЮ половину сторожа — прежнее условие «все группы по нулю».
    /// Новое правило про непонятые записи её не заменяет: непонятых записей тут нет ни одной,
    /// а раскладывать нечего.
    /// </summary>
    [Fact]
    public void Копия_без_данных_вовсе_не_накатывается()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);

            var archive = MakeArchive(
                root, "only-manifest.zip",
                OldPanelManifest("dshHome", "npmRoot", "keys/1-ssh"),
                Array.Empty<string>());

            var plan = RestoreEngine.Plan(archive, paths, null, new RestoreOptions(WithKeys: true));

            Assert.False(plan.Ok, "план признал годным архив, в котором нет ни одной записи данных: " + plan.Summary());
            Assert.Equal(PanelStrings.RestoreOldPanelArchive, plan.Error);
            Assert.Equal(0, plan.Files);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- граница правила: что чужой раскладкой НЕ считается ------------------

    /// <summary>
    /// ГРАНИЦА ПРАВИЛА, и она не «на всякий случай». Запись, которой нет места в описи, у архива
    /// с СОВПАВШЕЙ раскладкой — не чужая копия: такие записи уже называются в отчёте наката
    /// («записей БЕЗ ГРУППЫ в описи») и раскладку не отменяют (правило Д5, проверка
    /// <c>RestoreEmptyMachineTests.Невыбранная_группа_не_называется_записью_без_группы</c>).
    ///
    /// Чужая раскладка — это когда опись обещает корни, которых в архиве НЕТ ВОВСЕ, а в архиве
    /// лежат корни, которых нет в описи: опись и архив говорят о РАЗНЫХ наборах корней.
    /// Здесь все группы описи в архиве есть — значит раскладка понята, и лишняя запись не повод
    /// отказывать.
    /// </summary>
    [Fact]
    public void Посторонняя_запись_при_понятой_раскладке_накат_не_отменяет()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);

            var manifest = new BackupManifest
            {
                CreatedAt = "2026-09-26 12:00:00",
                Machine = "ДРУГАЯ-МАШИНА",
                User = "Кто-то-Другой",
                Paths =
                {
                    ["dsh-home"] = @"C:\Users\Прежний\.dsh",
                    ["engine/npm"] = @"C:\Users\Прежний\AppData\Roaming\npm",
                },
                Kinds =
                {
                    ["dsh-home"] = BackupRootKinds.DshHome,
                    ["engine/npm"] = BackupRootKinds.EnginePackages,
                },
            };

            var archive = MakeArchive(root, "with-extra.zip", manifest, new[]
            {
                "dsh-home/sessions/probe.txt",
                "engine/npm/dsh.cmd",

                // Запись, которой в описи нет вовсе: она попадает в отчёт наката, но не отменяет его.
                "scratch/junk.txt",
            });

            var plan = RestoreEngine.Plan(archive, paths, null, new RestoreOptions(WithEngine: true));

            Assert.True(plan.Ok, plan.Error);
            Assert.Equal(2, plan.Files);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// ВТОРАЯ ГРАНИЦА ПРАВИЛА: ПУСТАЯ группа в описи — ещё не чужая раскладка. Группа бывает
    /// законно пустой (каталог ключей был пуст в момент копии, каталог нашёлся, а файлов в нём
    /// нет) — и отказывать из-за одного этого значит ломать СВОЮ копию.
    ///
    /// Признак чужой раскладки — не пустота группы, а записи, которым НЕТ МЕСТА в описи.
    /// Здесь все записи архива описи понятны, и накат обязан состояться.
    /// </summary>
    [Fact]
    public void Пустая_группа_при_понятой_раскладке_накат_не_отменяет()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);

            var manifest = new BackupManifest
            {
                CreatedAt = "2026-09-26 12:00:00",
                Machine = "ДРУГАЯ-МАШИНА",
                User = "Кто-то-Другой",
                Paths =
                {
                    ["dsh-home"] = @"C:\Users\Прежний\.dsh",
                    ["keys/1-ssh"] = @"C:\Users\Прежний\.ssh",
                },
                Kinds =
                {
                    ["dsh-home"] = BackupRootKinds.DshHome,
                    ["keys/1-ssh"] = BackupRootKinds.KeyRing,
                },
            };

            // Записей ключей в архиве НЕТ вовсе: каталог в копию попал, а файлов в нём не было.
            var archive = MakeArchive(
                root, "empty-group.zip", manifest, new[] { "dsh-home/sessions/probe.txt" });

            var plan = RestoreEngine.Plan(archive, paths, null, new RestoreOptions(WithKeys: true));

            Assert.True(plan.Ok, plan.Error);

            // Пустая группа остаётся в плане и называет своё число — ноль, а не пропадает.
            Assert.Equal(0, plan.Groups.Single(group => group.Prefix == "keys/1-ssh").Files);
            Assert.Equal(1, plan.Files);
        }
        finally
        {
            Cleanup(root);
        }
    }
}
