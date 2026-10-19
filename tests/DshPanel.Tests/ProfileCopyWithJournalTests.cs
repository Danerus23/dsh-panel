using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using DshPanel.Backup;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// КОПИЯ ПРОФИЛЯ, КОГДА ЖУРНАЛ СЕССИЙ УЖЕ В НОВОМ ФОРМАТЕ (блок D плана 2.2.0).
///
/// **Зачем отдельная проверка.** Предохранительная копия перед подменой версии движка снимается
/// ЦЕЛИКОМ (`ZipWriter.Write(… Everything: true)`) — и это ровно то, что спасает человека, если
/// новая версия окажется хуже. Замер `l2` показал, что после первого же открытия сессии под 0.2.x
/// рядом со старым журналом появляется `session.v4.jsonl.zstd`, а новая работа пишется ТОЛЬКО в v4.
/// Значит копия обязана класть в архив и старые, и новые файлы, и **возвращать их байт в байт** —
/// иначе «право откатиться» окажется правом откатиться к половине работы.
///
/// **Почему проверка идёт НАСТОЯЩИМ 7-Zip, а не «на словах».** Пишет архив 7-Zip, а не панель
/// (решение владельца 25.09.2026), поэтому единственное доказательство — настоящий архив настоящим
/// инструментом. Так же устроены `BackupTests`/`BackupEngineTests`.
///
/// ⚠️ **Никаких данных человека:** раскладка собирается в своём корне (<see cref="TempRoots"/>),
/// содержимое — свои байты, уборка — <see cref="TempRoots.Remove"/>.
/// </summary>
public class ProfileCopyWithJournalTests
{
    /// <summary>Файлы журнала в той раскладке, в какой их ждёт движок.</summary>
    private static readonly (string Folder, string File)[] Journal =
    {
        ("только-старый", "session.v3.jsonl.zstd"),
        ("только-новый", "session.v4.jsonl.zstd"),
        ("смешанная", "session.v3.jsonl.zstd"),
        ("смешанная", "session.v4.jsonl.zstd"),
        ("незаконченная", "session.v4.jsonl.zstd.a1b2c3.tmp"),
    };

    /// <summary>Профиль движка: пять файлов журнала в четырёх папках сессий плюс обычные файлы дома.</summary>
    private static string Profile(string root)
    {
        var home = Path.Combine(root, "dsh-home");

        foreach (var (folder, file) in Journal)
        {
            var dir = Path.Combine(home, "sessions", "проект", folder);

            Directory.CreateDirectory(dir);

            // Байты НЕсжимаемые и разные: так у файла есть честный вес, а сверка ловит подмену.
            var bytes = new byte[16 * 1024 + folder.Length * 97 + file.Length];

            RandomNumberGenerator.Fill(bytes);
            File.WriteAllBytes(Path.Combine(dir, file), bytes);
        }

        File.WriteAllText(Path.Combine(home, "settings.yaml"), "language: ru\n");
        File.WriteAllText(Path.Combine(home, ".credentials.yaml"), "placeholder: true\n");

        return home;
    }

    /// <summary>Сумма каждого файла дома — по ней и сравнивается «вернулось то же».</summary>
    private static Dictionary<string, string> Sums(string home) =>
        Directory
            .GetFiles(home, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => Path.GetRelativePath(home, file).Replace('\\', '/'),
                file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
                StringComparer.Ordinal);

    /// <summary>
    /// Имена записей архива — прямыми косыми чертами, как требует формат. Ведущая косая снимается:
    /// предохранительная копия снимается с ПУСТЫМ префиксом (`EngineUpdateRealDoors` передаёт
    /// `new BackupSource(DshHome, string.Empty, Everything: true)`), а zip кладёт такую запись как
    /// `/sessions/…`. Это свойство настоящего архива, и проверка смотрит на ФАЙЛ, а не на префикс.
    /// </summary>
    private static string[] Entries(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);

        return zip.Entries
            .Where(entry => entry.Length > 0 || !entry.FullName.EndsWith('/'))
            .Select(entry => entry.FullName.TrimStart('/'))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// ГЛАВНОЕ: КОПИЯ КЛАДЁТ ВСЕ ПЯТЬ ФАЙЛОВ ЖУРНАЛА, И НАКАТ ВОЗВРАЩАЕТ ИХ БАЙТ В БАЙТ.
    ///
    /// Случаи названы по отдельности, потому что каждый — своя беда:
    /// * `v3` — то, что было до переезда (обязано оставаться целым: замер `l2` — старый файл
    ///   не переписывается);
    /// * `v4` — новая работа, которую без копии не вернуть никак;
    /// * **смешанная** — так выглядит ЛЕНИВАЯ миграция: оба файла рядом, и потерять один из них
    ///   значит потерять либо историю, либо продолжение;
    /// * **незаконченная** (`.tmp`) — тоже файл дома, и копия обязана быть копией, а не «умной
    ///   выборкой»: что там лежит, решает не панель.
    ///
    /// Мутации, которые обязаны упасть здесь: **«снимать копию с выборкой по имени `session.v3`»**
    /// и **«распаковывать без сверки содержимого»**.
    /// </summary>
    [Fact]
    public void Копия_профиля_берёт_журнал_обоих_форматов_и_возвращает_байт_в_байт()
    {
        var root = TempRoots.For("profile-copy-v4");
        Directory.CreateDirectory(root);

        try
        {
            var home = Profile(root);
            var before = Sums(home);

            // ТА ЖЕ ДВЕРЬ, ЧЕМ СНИМАЕТСЯ ПРЕДОХРАНИТЕЛЬНАЯ КОПИЯ ПЕРЕД ПОДМЕНОЙ ВЕРСИИ
            // (`EngineUpdateRealDoors`: дом движка целиком, `Everything: true`).
            var archive = Path.Combine(root, "dsh2-before-engine-2026-10-18-120000.zip");
            var written = ZipWriter.Write(
                archive,
                new[] { new BackupSource(home, string.Empty, Everything: true) },
                SevenZip.Find());

            Assert.True(written.Ok, written.Error);

            var entries = Entries(archive);

            foreach (var (folder, file) in Journal)
            {
                var wanted = "sessions/проект/" + folder + "/" + file;

                Assert.True(
                    entries.Contains(wanted, StringComparer.Ordinal),
                    $"в копии нет {wanted}; в архиве: {string.Join(", ", entries)}");
            }

            Assert.Contains("settings.yaml", entries, StringComparer.Ordinal);
            Assert.Contains(".credentials.yaml", entries, StringComparer.Ordinal);

            // НАКАТ: распаковка ТЕМ ЖЕ порядком, каким распаковывает панель, — и сверка КАЖДОГО
            // файла дома (не «файл на месте», а «тот же файл»).
            //
            // ⚠️ `ZipFile.ExtractToDirectory` здесь не годится и это не обход правила: он отказывает
            // на записи с ведущей косой чертой («resulted in a file outside the destination»), а
            // предохранительная копия кладёт записи именно так (префикс пустой). Панель распаковывает
            // сама (`RestoreEngine` → `ZipReader.Normalize`/`IsSafe`), поэтому и проверка идёт тем же
            // путём: нормализовать имя, отбросить каталоги, написать файл.
            var unpacked = Path.Combine(root, "unpacked");
            Directory.CreateDirectory(unpacked);

            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (var entry in zip.Entries)
                {
                    var name = ZipReader.Normalize(entry.FullName).TrimStart('/');

                    if (ZipReader.IsDirectory(name) || name.Length == 0) continue;

                    var target = Path.Combine(unpacked, name.Replace('/', Path.DirectorySeparatorChar));

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                    using var source = entry.Open();
                    using var file = File.Create(target);

                    source.CopyTo(file);
                }
            }

            var after = Sums(unpacked);

            Assert.Equal(before.Count, after.Count);

            foreach (var (name, sum) in before)
            {
                Assert.True(after.TryGetValue(name, out var restored), $"после наката нет файла {name}");
                Assert.Equal(sum, restored);
            }
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }

    /// <summary>
    /// РЕШЕНИЕ О КОПИИ НЕ ЗАВИСИТ ОТ ФОРМАТА ЖУРНАЛА: имя из предохранительного семейства, которое
    /// ротация НЕ ТРОГАЕТ, и путь — в папке копий панели.
    ///
    /// ⚠️ Семейство проверяется **ЛИТЕРАЛОМ**, а не константой продукта, и это не придирка: первая
    /// редакция читала `EngineProfileCopy.Prefix`, то есть проверяла продукт им же самим, — и мутация
    /// «сменить префикс на убираемый» на ней **выжила** (18.10.2026). Литерал — это договор с ротацией
    /// (`BackupNaming.EnginePrefix`), и он записан здесь дважды намеренно: разойдись они — упадёт
    /// проверка, а не «человек однажды останется без предохранительной копии».
    ///
    /// Мутации, которые обязаны упасть здесь: **«сменить префикс»** и **«сделать вид файла обычным»**.
    /// </summary>
    [Fact]
    public void Копия_перед_подменой_версии_зовётся_своим_семейством()
    {
        var plan = EngineProfileCopy.Decide(
            "C:\\дом-движка", "C:\\панель\\backups", new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

        Assert.True(plan.Any, plan.RefusalKey);
        Assert.StartsWith("dsh2-before-engine-", Path.GetFileName(plan.Target), StringComparison.Ordinal);
        Assert.Equal("C:\\панель\\backups", plan.Folder);
        Assert.EndsWith(".zip", plan.Target, StringComparison.Ordinal);

        // ОДНО ИМЯ ОДНИМ МЕСТОМ: константа продукта и договор с ротацией — это одно и то же.
        Assert.Equal("dsh2-before-engine-", BackupNaming.EnginePrefix);
        Assert.Equal(BackupNaming.EnginePrefix, EngineProfileCopy.Prefix);

        // И ВИД ФАЙЛА — ПРЕДОХРАНИТЕЛЬНЫЙ: такой копии ротация не касается. ⚠️ Спрашивается ИМЯ,
        // а не путь: `BackupNaming.Kind` сверяет префикс имени (`dsh2-…`), и на полном пути он честно
        // ответил бы `Other` — путь с `C:\` ни с каким нашим префиксом не начинается.
        Assert.Equal(BackupFileKind.Safety, EngineProfileCopy.Kind(Path.GetFileName(plan.Target)));
        Assert.Equal(BackupFileKind.Other, EngineProfileCopy.Kind(plan.Target));

        // Дома нет — отказ словами, а не пустая копия.
        Assert.False(EngineProfileCopy.Decide(string.Empty, "C:\\панель\\backups", DateTimeOffset.UnixEpoch).Any);
        Assert.False(EngineProfileCopy.Decide("C:\\дом-движка", string.Empty, DateTimeOffset.UnixEpoch).Any);
    }
}
