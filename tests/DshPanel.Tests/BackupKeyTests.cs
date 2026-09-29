using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Restore;
using DshPanel.Settings;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// КЛЮЧИ В КОПИИ — решение владельца 26.09.2026: «ключи снова входят в копию, как в v1», то есть
/// по отдельному разрешению, выключенному по умолчанию.
///
/// Проверки идут на НАСТОЯЩЕЙ копии в изолированной площадке (<see cref="AppPaths.Under"/>):
/// каталог ключей там свой, и настоящие <c>~/.ssh</c> и <c>C:\AndroidKeys</c> владельца не
/// трогаются ни одной строкой этой проверки.
///
/// Что здесь доказывается, по пунктам решения: имя группы и slug сделаны как в v1; без разрешения
/// ключей в копии нет; с разрешением — есть, и лежат они группой <c>keys/&lt;номер&gt;-&lt;имя&gt;</c>;
/// накат без согласия ключи не трогает; каталог SSH возвращается в ТЕКУЩИЙ <c>.ssh</c>; незнакомый
/// чужой каталог не восстанавливается и это названо.
/// </summary>
public class BackupKeyTests
{
    // --- имя группы и slug (правила v1) --------------------------------------

    [Fact]
    public void Slug_оставляет_латиницу_цифры_и_дефис()
    {
        Assert.Equal("ssh", BackupPlanner.Slug(".ssh".TrimStart('.')));
        Assert.Equal("android-keys", BackupPlanner.Slug("Android Keys"));
        Assert.Equal("keys-2026", BackupPlanner.Slug("keys_2026"));
        Assert.Equal("a-b-c", BackupPlanner.Slug("a...b___c"));
    }

    [Fact]
    public void Slug_обрезает_до_двадцати_знаков()
    {
        var slug = BackupPlanner.Slug("abcdefghijklmnopqrstuvwxyz");

        Assert.Equal(20, slug.Length);
        Assert.Equal("abcdefghijklmnopqrst", slug);
    }

    /// <summary>
    /// Кириллица в имени каталога slug не даёт: остаётся пустая строка, и группа зовётся просто
    /// номером. Это поведение v1, и оно верное — имя группы должно быть читаемым в любой кодировке.
    /// </summary>
    [Fact]
    public void Slug_кириллицу_не_пропускает()
    {
        Assert.Equal(string.Empty, BackupPlanner.Slug("Копии"));
        Assert.Equal("keys", BackupPlanner.Slug("Копии-keys"));
    }

    /// <summary>
    /// Номера групп: <c>keys/1-…</c>, <c>keys/2-…</c> — и они ОБЯЗАНЫ различаться, даже если
    /// каталоги называются одинаково. В v1 на этом потеряли часть ключей: одноимённые файлы
    /// (<c>id_rsa</c>, <c>known_hosts</c>) смешивались под одним именем группы.
    /// </summary>
    [Fact]
    public void Имена_групп_ключей_нумеруются_и_не_повторяются()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            var first = Path.Combine(root, "one", "keys");
            var second = Path.Combine(root, "two", "keys");
            Directory.CreateDirectory(paths.SshDir);
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);

            var roots = BackupPlanner.KeyRoots(paths, new[] { first, second });

            Assert.Equal(3, roots.Count); // каталог SSH площадки плюс два названных
            Assert.Equal("keys/1-ssh", roots[0].Prefix);
            Assert.Equal("keys/2-keys", roots[1].Prefix);
            Assert.Equal("keys/3-keys", roots[2].Prefix);
            Assert.Equal(roots.Count, roots.Select(item => item.Prefix).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>Каталог, которого нет, в состав не берётся, а НАЗВАННЫЙ человеком — называется словами.</summary>
    [Fact]
    public void Несуществующий_каталог_не_взят_и_назван()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            var missing = Path.Combine(root, "нет-такого-каталога");
            var notes = new List<string>();

            var found = BackupPlanner.KeyDirectories(paths, new[] { missing }, notes);

            Assert.Empty(found);
            Assert.Single(notes);
            Assert.Contains("не найден", notes[0]);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- разрешение: по умолчанию ключей в копии НЕТ --------------------------

    /// <summary>
    /// Разрешение выключено — ключей в копии нет, и об этом сказано словами. Молчать нельзя:
    /// человек иначе решит, что «унёс всё».
    /// </summary>
    [Fact]
    public void Без_разрешения_ключей_в_копии_нет()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            WriteKey(paths, "id_test");

            var plan = BackupPlanner.Full(paths, null, engine: null, withEngine: false, withKeys: false);

            Assert.DoesNotContain(plan.Roots, root => root.Kind == BackupRootKind.KeyRing);
            Assert.Contains(PanelStrings.BackupKeysNotIncluded, plan.Summary());
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Разрешение включено — каталог SSH площадки попал в план группой <c>keys/1-ssh</c>,
    /// и в замечаниях названо, чем от этого становится архив.
    /// </summary>
    [Fact]
    public void С_разрешением_каталог_ssh_попадает_в_план()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            WriteKey(paths, "id_test");

            var plan = BackupPlanner.Full(paths, null, engine: null, withEngine: false, withKeys: true);

            Assert.Single(plan.Roots, root => root.Kind == BackupRootKind.KeyRing);
            var keyRoot = plan.Roots.Single(root => root.Kind == BackupRootKind.KeyRing);
            Assert.Equal("keys/1-ssh", keyRoot.Prefix);
            Assert.Equal(paths.SshDir, keyRoot.Directory);
            Assert.Contains("ключи в копии", plan.Summary());
            Assert.Contains("ключом доступа", plan.Summary());
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// НАСТОЯЩАЯ копия: файл ключа лежит в архиве под группой ключей. Это и есть ответ на вопрос
    /// «а ключи точно поехали»: проверяется по записям архива, а не по намерению.
    /// </summary>
    [Fact]
    public void Файл_ключа_лежит_в_архиве_под_группой_ключей()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            WriteKey(paths, "id_test");
            Directory.CreateDirectory(paths.DshHome);

            var archive = Path.Combine(paths.BackupsDir, "copy.zip");
            var plan = BackupPlanner.Full(paths, null, engine: null, withEngine: false, withKeys: true);

            var run = BackupEngine.Run(
                plan,
                new BackupRequest(archive, SevenZipPath: "", Verify: false),
                paths);

            Assert.True(run.Ok, run.Error);

            var names = ZipFile.OpenRead(archive).Entries.Select(entry => entry.FullName).ToArray();

            Assert.Contains(names, name => name == "keys/1-ssh/id_test");
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- накат: три согласия и своё правило цели ------------------------------

    /// <summary>
    /// Без согласия ключи не трогаются: группа не выбрана, причина названа, и в отчёте о накате
    /// про них сказано отдельной строкой.
    /// </summary>
    [Fact]
    public void Накат_без_согласия_ключи_не_трогает()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            var archive = WriteKeyArchive(paths, "keys/1-ssh", "\\\\чужая-машина\\ssh", machine: "ДРУГОЙ-ПК");

            var plan = RestoreEngine.Plan(archive, paths, null, new RestoreOptions(WithKeys: false));

            Assert.Single(plan.Groups, item => item.Prefix == "keys/1-ssh");
            var group = plan.Groups.Single(item => item.Prefix == "keys/1-ssh");
            Assert.False(group.Selected);
            Assert.Equal(PanelStrings.RestoreKeysNeedConsent, group.Note);
            Assert.Contains("ключи в копии есть, но не возвращаются", plan.Summary());

            // И на диске ничего не появляется: накат без согласия и не начинается для этой группы.
            var run = RestoreEngine.Run(plan, new RestoreOptions(SafetyCopy: false), paths);

            Assert.DoesNotContain("keys/1-ssh", run.Restored);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// ЧУЖАЯ копия (в описи другое имя машины) и путь вне профиля: каталог SSH возвращается
    /// в ТЕКУЩИЙ <c>.ssh</c> площадки, а не туда, откуда взят. Это правило v1 дословно.
    /// </summary>
    [Fact]
    public void Чужая_копия_возвращает_ssh_в_текущий_каталог()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            var archive = WriteKeyArchive(paths, "keys/1-ssh", @"\\ЧУЖАЯ-МАШИНА\Users\someone\.ssh", machine: "ДРУГОЙ-ПК");

            var options = new RestoreOptions(WithKeys: true, SafetyCopy: false);
            var plan = RestoreEngine.Plan(archive, paths, null, options);

            Assert.Single(plan.Groups, item => item.Prefix == "keys/1-ssh");
            var group = plan.Groups.Single(item => item.Prefix == "keys/1-ssh");
            Assert.True(group.Selected);
            Assert.Equal(paths.SshDir, group.Target);
            Assert.Equal(PanelStrings.RestoreKeysToCurrentSsh, group.Note);

            var run = RestoreEngine.Run(plan, options, paths);

            Assert.True(run.Ok, run.Error);
            Assert.True(File.Exists(Path.Combine(paths.SshDir, "id_test")),
                "ключ обязан лечь в текущий каталог .ssh площадки");
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Незнакомый чужой каталог ключей НЕ восстанавливается — и это названо словами: писать
    /// чужие ключи в чужой профиль нельзя, а «молча пропустить» человек воспримет как потерю.
    /// </summary>
    [Fact]
    public void Незнакомый_чужой_каталог_не_восстанавливается()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            var archive = WriteKeyArchive(
                paths, "keys/9-чужое", @"\\ЧУЖАЯ-МАШИНА\Keys\секретное", machine: "ДРУГОЙ-ПК");

            var plan = RestoreEngine.Plan(archive, paths, null, new RestoreOptions(WithKeys: true));

            var group = Assert.Single(plan.Groups);
            Assert.False(group.Selected);
            Assert.Contains("не восстановлен", group.Note);
            Assert.Contains("ЧУЖАЯ-МАШИНА", group.Note);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Д8: после закрытия каталога ключей ВЛАДЕЛЕЦ обязан читать файлы внутри него. Проверка идёт
    /// ровно через то состояние, в котором дефект возможен и в котором он жил: каталог с УЖЕ
    /// лежащими файлами, к которому применяется то же правило, что ставит накат.
    ///
    /// Прежняя редакция правила ставила права каталогу БЕЗ наследования и с
    /// <c>preserveInheritance: false</c> — Windows снимал у детей всё унаследованное, а новых
    /// наследуемых правил у каталога не было, и файлы оставались с ПУСТЫМ списком доступа.
    /// Найдено прогоном на живом носителе: следующий накат падал на предохранительной копии
    /// («Access to the path … is denied»), а каталог при этом выглядел закрытым — проверка
    /// смотрела каталог, а страдал файл.
    /// </summary>
    [Fact]
    public void Владелец_читает_файлы_внутри_закрытого_каталога_ключей()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            var nested = Path.Combine(paths.SshDir, "вложенный");
            var key = Path.Combine(nested, "id_test");
            var note = Path.Combine(paths.SshDir, "known_hosts");

            Directory.CreateDirectory(nested);
            File.WriteAllText(key, "PRIVATE KEY\n", new UTF8Encoding(false));
            File.WriteAllText(note, "host key\n", new UTF8Encoding(false));

            var tightened = BackupEngine.RestrictToOwner(paths.SshDir);
            Assert.True(tightened.Ok, tightened.Reason);

            // Каталог закрыт и правила НАСЛЕДУЮТСЯ: иначе у детей доступа не будет вовсе.
            var security = new DirectoryInfo(paths.SshDir).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected,
                "у закрытого каталога ключей наследование прав обязано быть снято");

            // Перебор ЯВНЫЙ, без лямбд: разбор списка доступа — средство Windows, и в лямбде
            // разборщик платформы его не видит, а сборка обязана быть без предупреждений.
            var inherited = false;
            foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                         .OfType<FileSystemAccessRule>())
            {
                var flags = rule.InheritanceFlags;
                if ((flags & InheritanceFlags.ContainerInherit) != 0 && (flags & InheritanceFlags.ObjectInherit) != 0)
                {
                    inherited = true;
                    break;
                }
            }

            Assert.True(inherited, "правила каталога ключей не наследуются детьми — у файлов не будет доступа");

            // Вот он, дефект: ФАЙЛ внутри каталога. Владелец обязан его прочитать.
            Assert.Equal("PRIVATE KEY\n", File.ReadAllText(key, Encoding.UTF8));
            Assert.Equal("host key\n", File.ReadAllText(note, Encoding.UTF8));

            // И у файла есть доступ — не пустой список: читается он ровно поэтому.
            var keyRules = new FileInfo(key).GetAccessControl()
                .GetAccessRules(true, true, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .ToArray();

            Assert.NotEmpty(keyRules);
            foreach (var rule in keyRules)
                Assert.NotEqual(AccessControlType.Deny, rule.AccessControlType);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Д8: каталог закрывается ВМЕСТЕ С СОДЕРЖИМЫМ, и вот почему это не украшение.
    ///
    /// Файл внутри каталога ключей может приехать с ЛЮБЫМ списком доступа, а снятие наследования
    /// у родителя действует только вперёд: уже существующее правило ребёнка оно не тронет. Здесь
    /// файл заведомо испорчен — запрет на чтение владельцу, — и после закрытия каталога владелец
    /// обязан его прочитать: закрытие каталога закрывает и содержимое.
    ///
    /// ⚠️ Проверка обязана проходить ИМЕННО через испорченное состояние. На честном файле она
    /// бессмысленна: унаследованные правила родителя Windows применяет к детям сам, и «закрыть
    /// содержимое» оказывается неотличимо от «закрыть только каталог» — мутация на этом и выжила
    /// (первый прогон мутаций 26.09.2026 показал ровно это: Д8b не уронила ничего).
    /// </summary>
    [Fact]
    public void Закрытие_каталога_ключей_чинит_и_испорченные_права_файла()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = NewRoot();
        var key = string.Empty;

        try
        {
            var paths = AppPaths.Under(root);
            key = Path.Combine(paths.SshDir, "id_test");

            Directory.CreateDirectory(paths.SshDir);
            File.WriteAllText(key, "PRIVATE KEY\n", new UTF8Encoding(false));

            // Портим: владельцу — запрет на чтение. Так выглядит файл, приехавший с чужого
            // носителя (или оставшийся от прежнего наката).
            var me = WindowsIdentity.GetCurrent().User!;
            var spoiled = new FileSecurity();
            spoiled.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            spoiled.AddAccessRule(new FileSystemAccessRule(
                me, FileSystemRights.ReadData, AccessControlType.Deny));
            new FileInfo(key).SetAccessControl(spoiled);

            Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(key, Encoding.UTF8));

            var tightened = BackupEngine.RestrictToOwner(paths.SshDir);
            Assert.True(tightened.Ok, tightened.Reason);

            // Запрет снят, владелец читает свой ключ.
            Assert.Equal("PRIVATE KEY\n", File.ReadAllText(key, Encoding.UTF8));
        }
        finally
        {
            // Уборка обязана сработать даже при провале: у файла с запретом владелец его не удалит,
            // поэтому сначала возвращаем доступ, потом убираем дерево.
            try
            {
                if (key.Length > 0 && File.Exists(key)) BackupEngine.RestrictToOwner(key);
            }
            catch
            {
                // Не смогли вернуть доступ — дерево всё равно попробуем убрать.
            }

            Cleanup(root);
        }
    }

    /// <summary>
    /// Д8: посторонние доступа НЕ получили. «Закрыть на владельца» — это два правила, владелец
    /// и SYSTEM, и никакой третьей учётной записи в списке быть не должно — ни у каталога, ни
    /// у файла внутри него.
    /// </summary>
    [Fact]
    public void У_закрытого_каталога_ключей_нет_третьих_учётных_записей()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            var key = Path.Combine(paths.SshDir, "id_test");

            Directory.CreateDirectory(paths.SshDir);
            File.WriteAllText(key, "PRIVATE KEY\n", new UTF8Encoding(false));

            var tightened = BackupEngine.RestrictToOwner(paths.SshDir);
            Assert.True(tightened.Ok, tightened.Reason);

            var me = WindowsIdentity.GetCurrent().User!;
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            void OnlyOurs(AuthorizationRuleCollection rules, string what)
            {
                foreach (var rule in rules.OfType<FileSystemAccessRule>())
                {
                    Assert.True(
                        rule.IdentityReference is SecurityIdentifier sid && (sid == me || sid == system),
                        $"{what}: доступ достался посторонней учётной записи «{rule.IdentityReference}»");
                }
            }

            OnlyOurs(new DirectoryInfo(paths.SshDir).GetAccessControl()
                .GetAccessRules(true, true, typeof(SecurityIdentifier)), "каталог ключей");
            OnlyOurs(new FileInfo(key).GetAccessControl()
                .GetAccessRules(true, true, typeof(SecurityIdentifier)), "файл ключа");
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Д8: причина отказа тоже называется словами. Прежний ответ был одним <c>false</c>, и отчёт
    /// говорил «ключи не закрыты» без единого слова о том, что случилось.
    /// </summary>
    [Fact]
    public void Отказ_закрытия_прав_называет_причину()
    {
        var missing = Path.Combine(Path.GetTempPath(), "dsh-key-нет-такого-" + Guid.NewGuid().ToString("N")[..8]);

        var refused = BackupEngine.RestrictToOwner(missing);

        Assert.False(refused.Ok);
        Assert.NotEqual(string.Empty, refused.Reason);
        Assert.Contains("нет ни файла, ни каталога", refused.Reason);

        var empty = BackupEngine.RestrictToOwner("   ");
        Assert.False(empty.Ok);
        Assert.Equal("путь не задан", empty.Reason);
    }

    /// <summary>
    /// Разрешение выключено, а ключи в копии есть — говорим об этом в описи наката: иначе человек
    /// решит, что вернул всё.
    /// </summary>
    [Fact]
    public void Накат_называет_что_ключи_в_копии_есть_но_не_возвращаются()
    {
        var root = NewRoot();

        try
        {
            var paths = AppPaths.Under(root);
            var archive = WriteKeyArchive(paths, "keys/1-ssh", @"\\ЧУЖАЯ-МАШИНА\.ssh", machine: "ДРУГОЙ-ПК");

            var plan = RestoreEngine.Plan(archive, paths, null, new RestoreOptions(WithKeys: false));

            Assert.Contains("ключи в копии есть, но не возвращаются", plan.Summary());
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- предложение каталогов ------------------------------------------------

    /// <summary>
    /// Найденное предлагается кнопкой и не предлагается дважды: уже взятый в настройки каталог
    /// в список кнопок не попадает. Существование подставляется — проверка не ходит по диску.
    /// </summary>
    [Fact]
    public void Найденный_каталог_предлагается_один_раз()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(@"C:\", KeyDirSuggestions.AndroidKeysFolder),
            Path.Combine(@"C:\", "Other"),
        };

        var suggestions = KeyDirSuggestions.Find(path => found.Contains(path), already: null);

        Assert.Single(
            suggestions,
            item => item.Path.EndsWith(KeyDirSuggestions.AndroidKeysFolder, StringComparison.OrdinalIgnoreCase));

        var android = suggestions.Single(
            item => item.Path.EndsWith(KeyDirSuggestions.AndroidKeysFolder, StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(android.Source));

        var again = KeyDirSuggestions.Find(
            path => found.Contains(path),
            already: new[] { Path.Combine(@"C:\", KeyDirSuggestions.AndroidKeysFolder) });

        Assert.DoesNotContain(
            again, item => item.Path.EndsWith(KeyDirSuggestions.AndroidKeysFolder, StringComparison.OrdinalIgnoreCase));
    }

    // --- площадка -------------------------------------------------------------

    private static void WriteKey(AppPaths paths, string name)
    {
        Directory.CreateDirectory(paths.SshDir);
        File.WriteAllText(Path.Combine(paths.SshDir, name), "PRIVATE KEY\n");
    }

    /// <summary>
    /// Архив с одной группой ключей и описью, как её пишет панель. Собирается руками нарочно:
    /// так проверка задаёт РОВНО те пути и имена машин, которые ей нужны (чужая копия, чужой
    /// каталог), и не зависит от того, на какой машине её запустили.
    /// </summary>
    private static string WriteKeyArchive(AppPaths paths, string group, string recorded, string machine)
    {
        Directory.CreateDirectory(paths.BackupsDir);
        var archive = Path.Combine(paths.BackupsDir, "keys-copy.zip");

        var manifest = new BackupManifest
        {
            CreatedAt = "2026-09-26 12:00:00",
            Machine = machine,
            User = "кто-то",
            Paths = { [group] = recorded },
            Kinds = { [group] = BackupRootKinds.KeyRing },
        };

        using var zip = new ZipArchive(File.Open(archive, FileMode.Create), ZipArchiveMode.Create);

        var manifestEntry = zip.CreateEntry(ZipLayout.ManifestEntry);
        using (var stream = manifestEntry.Open())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(JsonSerializer.Serialize(manifest, BackupManifest.WriteOptions));

        var keyEntry = zip.CreateEntry(group + "/id_test");
        using (var stream = keyEntry.Open())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write("PRIVATE KEY\n");

        return archive;
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-key-tests-" + Guid.NewGuid().ToString("N"));
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
