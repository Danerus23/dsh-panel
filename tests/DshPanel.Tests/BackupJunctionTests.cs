using System;
using System.Collections.Generic;
using System.Globalization;
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
/// НАХОДКА В6 (ответ системы про ссылки терялся) и ДЕФЕКТ Д6 (отказ копии без имени файла).
///
/// Общее у них одно, и ради этого они лежат вместе: **причина была в руках, а человеку её
/// не говорили**. У В6 <c>CreateJunction</c> читал и выбрасывал ответ Windows — на exFAT она
/// отвечает «Для завершения операции требуются локальные тома NTFS» (измерено на настоящем
/// носителе), — и на каждую из 482 ссылок отчёт писал одинаковое «создать не удалось».
/// У Д6 исключение на запертом файле летело в общий <c>catch</c>, и отчёт говорил «копия не снята»
/// без виновника: человек не знал, что закрыть, чтобы копия снялась.
///
/// Стенд — временный каталог в <c>%TEMP%</c>, ни одного касания каталогов владельца.
/// </summary>
public class BackupJunctionTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "dsh junction " + Guid.NewGuid().ToString("N")[..8]);

    private static void WriteFile(string path, string text)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllText(path, text);
    }

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

    /// <summary>Сколько раз подстрока встречается в отчёте: по этому считается «пожар строк».</summary>
    private static int Count(string text, string part)
    {
        var count = 0;
        var at = 0;

        while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += part.Length;
        }

        return count;
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    // --- В6: причина создания ссылки -----------------------------------------

    /// <summary>
    /// Ответ системы возвращается, а не выбрасывается: на живой машине ссылка создаётся, и вместе
    /// с «получилось» приходит то, что ответила Windows.
    ///
    /// ⚠️ **Проверка независима от ЯЗЫКА СИСТЕМЫ, и это её главное свойство** (правка 30.09.2026).
    /// Прежняя редакция требовала в ответе русского слова «соединение» — и падала на английской
    /// Windows, которая отвечает «Junction created for …». Закреплением культуры это НЕ лечится:
    /// язык ответа <c>mklink</c> задаёт сама Windows, а не культура .NET, поэтому на английском
    /// раннере ответ остаётся английским при любой культуре прогона.
    ///
    /// Проверяется поэтому то, что панель ДЕЙСТВИТЕЛЬНО обещает человеку (находка В6: «причина
    /// была в руках, а человеку её не говорили»), а не редакция ответа:
    ///
    /// 1. **ответ получен** — он не пуст;
    /// 2. **в нём есть отклик <c>mklink</c>** — стрелка <c>&lt;===&gt;&gt;</c>. Она в выводе обеих
    ///    Windows дословно: русская пишет «ссылка … &lt;&lt;===&gt;&gt; … цель», английская —
    ///    «Junction created for … &lt;&lt;===&gt;&gt; …». Это след САМОЙ программы, а не перевод;
    /// 3. **в нём названы ОБА пути** — и ссылка, и цель (<see cref="AnswerNamesPaths"/>).
    ///
    /// ⚠️ **Чего эта проверка не утверждает и не может утверждать здесь:** что кириллица в ответе
    /// читается. Ответ приходит байтами консоли, а восстанавливается кодовой страницей, взятой
    /// у КУЛЬТУРЫ .NET (<c>RestoreEngine.ReadAnswer</c>), а не у языка Windows: русская культура
    /// на английской Windows законно даёт «кракозябры» вместо букв. Это названо в отчёте как
    /// находка в <c>src</c> (чужая поверхность) — чинить её отсюда нельзя. Поэтому пути для
    /// сравнения взяты ЛАТИНСКИЕ: байты ниже 128 не перетолковывает ни одна кодовая страница,
    /// и след пути находится при любом языке системы.
    /// </summary>
    [Fact]
    public void Создание_ссылки_отдаёт_ответ_системы()
    {
        // ⚠️ **Корни у ссылки и у цели РАЗНЫЕ, и это часть правки, а не украшение** (нашла мутация
        // 30.09.2026). При общем корне путь цели СОДЕРЖИТ путь ссылки, и проверка «в ответе названа
        // цель» проходила бы зелёной даже тогда, когда цель из ответа пропала целиком.
        var root = NewRoot();
        var linkRoot = NewRoot();

        try
        {
            // Пути ЛАТИНСКИЕ, и это тоже не украшение: след латинского пути не перетолковывает
            // ни одна кодовая страница, поэтому сверка не зависит ни от языка системы, ни от того,
            // какой кодовой страницей восстановлен ответ консоли.
            var target = Path.Combine(root, "target folder");
            var link = Path.Combine(linkRoot, "link folder");
            Directory.CreateDirectory(target);

            // Родитель ссылки существует, а сама ссылка — ещё нет: так её и создаёт панель.
            Directory.CreateDirectory(linkRoot);

            // Условие, без которого проверка ослепла бы снова: ни один путь не содержится в другом.
            // Оно проверяется здесь же, потому что корни временные и совпадение неочевидно.
            Assert.DoesNotContain(Path.GetFileName(linkRoot), target, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.GetFileName(root), link, StringComparison.Ordinal);

            var made = RestoreEngine.CreateJunction(link, target);

            Assert.True(made.Ok, "junction не создался — проверять нечего");
            Assert.True(Directory.Exists(link));
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);

            // 1. Ответ системы получен и он не пуст: причина не выброшена (находка В6).
            Assert.False(string.IsNullOrWhiteSpace(made.Answer), "ответ системы не вернулся вовсе");

            // 2. Это ОТКЛИК mklink, а не подставленная панелью строка: стрелка есть в выводе любой
            //    Windows («ссылка … <<===>> … цель», «Junction created for … <<===>> …»).
            //    Без неё «ответ» мог бы оказаться чем угодно — лишь бы непустым.
            Assert.Contains("<===>", made.Answer, StringComparison.Ordinal);

            // 3. И в ответе названы ОБА пути — куда легла ссылка и куда она смотрит.
            //
            // ⚠️ **Каждый путь утверждается САМ ЗА СЕБЯ, и это не дублирование** (нашла мутация
            // 30.09.2026): проверка «оба пути» ОДНОЙ свёрткой оказалась ДЫРЯВОЙ — сведение её
            // к первому пути НЕ роняло проверку, потому что путь ссылки оказался префиксом пути
            // цели, и одного первого хватало. Дыра закрыта дважды: пути утверждаются по отдельности
            // И корни у них разные.
            Assert.True(
                AnswerNamesPaths(made.Answer, link, target),
                $"в ответе системы не названы пути «{link}» → «{target}»: {made.Answer}");

            // Ссылка названа дважды, и оба раза не зря: эта сверка держит саму ссылку, а следующая
            // (за цель) роняет потерю цели, когда ссылка уцелела.
            Assert.True(
                NamesPath(made.Answer, link),
                $"в ответе системы не названа САМА ССЫЛКА «{link}»: {made.Answer}");

            // А эта сверка держит цель — и она НЕ тавтология предыдущих: корни у путей разные,
            // и путь цели не содержит пути ссылки (за этим следит утверждение выше по тексту).
            Assert.True(
                NamesPath(made.Answer, target),
                $"в ответе системы не названа ЦЕЛЬ ссылки «{target}»: {made.Answer}");

            Directory.Delete(link);
        }
        finally
        {
            Cleanup(root);
            Cleanup(linkRoot);
        }
    }

    /// <summary>
    /// Названы ли в ответе системы оба пути — БЕЗ опоры на язык системы и на кодировку консоли.
    ///
    /// <see cref="RestoreEngine"/> читает вывод <c>cmd</c> как Latin1 и перетолковывает его кодовой
    /// страницей консоли. Байты ниже 128 при этом не меняются НИКОГДА, а байты кириллицы — меняются
    /// (и законно: кодовая страница берётся у культуры .NET, а не у языка Windows). Поэтому след
    /// латинского пути ищется побайтово.
    ///
    /// ⚠️ Свёртка эта — удобство вызова, а не сама проверка: утверждается КАЖДЫЙ путь отдельно
    /// (<see cref="Создание_ссылки_отдаёт_ответ_системы"/>), иначе потеря цели проходила бы зелёной,
    /// когда путь ссылки — префикс пути цели. Это и нашла мутация 30.09.2026.
    /// </summary>
    private static bool AnswerNamesPaths(string answer, string link, string target) =>
        NamesPath(answer, link) && NamesPath(answer, target);

    private static bool NamesPath(string answer, string path)
    {
        if (path.Length == 0) return true;

        if (answer.Contains(path, StringComparison.Ordinal)) return true;

        return answer.Contains(Encoding.Latin1.GetString(Encoding.Latin1.GetBytes(path)), StringComparison.Ordinal);
    }

    /// <summary>
    /// Ссылка не создалась — и это ВИДНО по исходу, а не только по отсутствию каталога: исход
    /// несёт ответ Windows. Цель на диске отсутствует — ветка, доступная на любом носителе.
    /// </summary>
    [Fact]
    public void Несозданная_ссылка_несёт_ответ_и_отсутствие_каталога()
    {
        var root = NewRoot();
        try
        {
            var link = Path.Combine(root, "ссылка");
            var made = RestoreEngine.CreateJunction(link, Path.Combine(root, "нет-такого"));

            Assert.False(made.Ok);
            Assert.False(Directory.Exists(link));
            Assert.NotEqual(string.Empty, made.Answer);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Том НЕ NTFS: причина обязана называть и файловую систему, и что с этим делать. Случай
    /// измерен на настоящем носителе (VHDX, отформатирован в гостя как exFAT) — там Windows
    /// отвечает «Для завершения операции требуются локальные тома NTFS».
    /// </summary>
    [Fact]
    public void Отказ_на_не_NTFS_называет_файловую_систему_и_что_делать()
    {
        var refusal = RestoreEngine.JunctionRefusal(new RestoreEngine.JunctionAttempt(
            false,
            "Для завершения операции требуются локальные тома NTFS.",
            "exFAT",
            NotNtfs: true));

        Assert.Contains("exFAT", refusal, StringComparison.Ordinal);
        Assert.Contains("NTFS", refusal, StringComparison.Ordinal);
        Assert.Contains("тома NTFS", refusal, StringComparison.Ordinal);   // это ответ Windows, дословно

        var advice = PanelStrings.JunctionAdvice;
        Assert.Contains("NTFS", advice, StringComparison.Ordinal);
        Assert.Contains("exFAT", advice, StringComparison.Ordinal);
    }

    /// <summary>Том NTFS, а ссылка всё равно не легла: называем ровно то, что ответила система.</summary>
    [Fact]
    public void Отказ_на_NTFS_называет_ответ_Windows()
    {
        var refusal = RestoreEngine.JunctionRefusal(new RestoreEngine.JunctionAttempt(
            false, "Отказано в доступе.", "NTFS", NotNtfs: false));

        Assert.Contains("Отказано в доступе", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("файловая система", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("exFAT", refusal, StringComparison.Ordinal);
    }

    /// <summary>Ответа нет вовсе — и об этом говорим словами, а не пустой строкой.</summary>
    [Fact]
    public void Молчание_системы_тоже_названо()
    {
        var refusal = RestoreEngine.JunctionRefusal(new RestoreEngine.JunctionAttempt(
            false, string.Empty, string.Empty, NotNtfs: false));

        Assert.Equal(PanelStrings.JunctionNoAnswer, refusal);
    }

    // --- В6: отчёт сводит отказы ссылок в одну строку ------------------------

    /// <summary>
    /// exFAT, воспроизведённый точно: ссылки НЕ создаются, и вот что на это отвечает система.
    ///
    /// Измерено на настоящем носителе (VHDX в гостя, exFAT): <c>mklink /J</c> даёт код 1
    /// и ответ «Для завершения операции требуются локальные тома NTFS.». Восемь ссылок здесь —
    /// миниатюра того же случая, где их было 482; отказы подставлены СВОИМ источником
    /// (шов <c>junction</c> у <see cref="RestoreEngine.Run"/>, как у проб сервера и часов), потому
    /// что exFAT-тома в проверке нет, а поведение проверить надо.
    ///
    /// Проверяется ровно то, что сломано и что дороже формулировок:
    ///   * причина названа ОДИН раз и ЧИСЛОМ, а не 482 одинаковыми строками;
    ///   * накат с нелёгшими ссылками НЕ выдаётся за полный успех (это и есть беда В6:
    ///     отчёт называл 482 отказа и заканчивался словами «накат сделан»);
    ///   * данные при этом ЛЕГЛИ — «неполный» не значит «ничего не сделано».
    /// </summary>
    [Fact]
    public void Ссылки_не_легли_накат_неполный_и_данные_на_месте()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);
            WriteFile(Path.Combine(paths.DshHome, "sessions", "keep.txt"), "K\n");

            var manifest = new BackupManifest
            {
                CreatedAt = "2026-09-26 12:00:00",
                Machine = "ДРУГАЯ-МАШИНА",
                User = "Кто-то-Другой",
                Paths = { ["dsh-home"] = paths.DshHome },
                Kinds = { ["dsh-home"] = BackupRootKinds.DshHome },
            };

            for (var index = 1; index <= 8; index++)
            {
                manifest.Links.Add(new BackupLink
                {
                    Path = $"dsh-home/profiles/node_modules/link-{index}",
                    Target = Path.Combine(paths.DshHome, "global-packages", "link-" + index),
                });

                // Цели существуют: значит ссылку БУДУТ создавать, а не пропустят «цели нет».
                Directory.CreateDirectory(Path.Combine(paths.DshHome, "global-packages", "link-" + index));
            }

            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, "copy.zip");

            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                AddText(zip, "dsh-home/sessions/keep.txt", "K\n");
                AddText(zip, ZipLayout.ManifestEntry,
                    JsonSerializer.Serialize(manifest, BackupManifest.WriteOptions));
            }

            var plan = RestoreEngine.Plan(archive, paths, workingDirectory: null);
            Assert.True(plan.Ok, plan.Error);

            // Носитель, где ссылки невозможны: Windows отвечает кодом 1 и вот этим текстом.
            var result = RestoreEngine.Run(
                plan,
                new RestoreOptions(),
                paths,
                engine: null,
                progress: null,
                junction: (_, _) => new RestoreEngine.JunctionAttempt(
                    false,
                    "Для завершения операции требуются локальные тома NTFS.",
                    "exFAT",
                    NotNtfs: true));

            var report = result.Summary();

            // 1. Число названо, и причина — один раз: файловую систему и ответ Windows.
            Assert.Contains("ссылки не легли: 8", report, StringComparison.Ordinal);
            Assert.Contains("exFAT", report, StringComparison.Ordinal);
            Assert.Contains("тома NTFS", report, StringComparison.Ordinal);

            // Одинаковых строк на каждую ссылку больше нет: причина сведена в одну строку отчёта.
            Assert.Equal(1, Count(report, "создать не удалось"));
            Assert.Equal(8, result.LinksNotCreated);

            // 2. Полного успеха нет: накат неполный, и это видно и в итоге, и в коде возврата.
            Assert.False(result.Ok, "накат без ссылок не должен выглядеть полным успехом");
            Assert.Contains("НЕПОЛНОЕ", result.Summary(), StringComparison.Ordinal);
            Assert.Contains("ссылок создать не удалось: 8 из 8", result.Summary(), StringComparison.Ordinal);

            // 3. Данные легли — «неполный» не значит «ничего не сделано»: итог называет число
            //    легших файлов, а сам файл на месте.
            Assert.True(File.Exists(Path.Combine(paths.DshHome, "sessions", "keep.txt")));
            Assert.Equal(1, result.Files);
            Assert.Contains("1 файл(ов) легло", result.Summary(), StringComparison.Ordinal);

            // Строка группы сверяется ЦЕЛИКОМ и с ОЖИДАЕМЫМ именем группы.
            //
            // Прежняя проверка (`line.Contains("dsh-home")`) была СЛЕПОЙ: та же подстрока есть
            // и в ПУТИ группы, поэтому потеря имени группы в строке проходила зелёной (мутация
            // `RestoreGroupLandsFormat`: «{0}» → «»). И одного сравнения с ожидаемой строкой мало:
            // она строится из ТОГО ЖЕ ключа, и мутация ключа сдвинула бы обе стороны, — поэтому
            // рядом стоит проверка, что строка группу НАЗЫВАЕТ.
            var lands = string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreGroupLandsFormat,
                "dsh-home", DisplayMask.Path(paths.DshHome), 1);

            var line = Assert.Single(result.Restored);
            Assert.Equal(lands, line);
            Assert.Contains("«dsh-home»", line, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Ссылке некуда указывать (цели на этой машине нет) — это не отказ создания, а пропуск
    /// с причиной, и накат от него НЕ становится неполным: класть мёртвую ссылку нельзя.
    /// </summary>
    [Fact]
    public void Ссылка_без_цели_называется_и_не_делает_накат_неполным()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);
            WriteFile(Path.Combine(paths.DshHome, "sessions", "keep.txt"), "K\n");

            var manifest = new BackupManifest
            {
                CreatedAt = "2026-09-26 12:00:00",
                Machine = "ДРУГАЯ-МАШИНА",
                User = "Кто-то-Другой",
                Paths = { ["dsh-home"] = paths.DshHome },
                Kinds = { ["dsh-home"] = BackupRootKinds.DshHome },
            };

            manifest.Links.Add(new BackupLink
            {
                Path = "dsh-home/profiles/node_modules/link-1",
                Target = @"C:\Чужой-каталог\совсем\другого\человека\link-1",
            });

            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, "copy.zip");

            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                AddText(zip, "dsh-home/sessions/keep.txt", "K\n");
                AddText(zip, ZipLayout.ManifestEntry,
                    JsonSerializer.Serialize(manifest, BackupManifest.WriteOptions));
            }

            var plan = RestoreEngine.Plan(archive, paths, workingDirectory: null);
            Assert.True(plan.Ok, plan.Error);

            var result = RestoreEngine.Run(plan, new RestoreOptions(), paths);

            Assert.True(result.Ok, result.Error);
            Assert.Equal(0, result.LinksNotCreated);

            // Причина пропуска — ЦЕЛИКОМ, а не по подстроке, и отдельно по двум своим обещаниям:
            // назвать цель и сказать, что её здесь НЕТ. Второе и третье — про сам текст ключа:
            // сравнение с ожидаемой строкой одной мутации ключа не поймало бы (обе стороны
            // строятся из него же), а без этих обещаний причина человеку ничего не объясняет.
            var missing = string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestoreLinkTargetMissingFormat,
                "profiles/node_modules/link-1",
                DisplayMask.Path(@"C:\Чужой-каталог\совсем\другого\человека\link-1"));

            Assert.Contains(missing, result.Skipped);
            Assert.Contains("на этой машине нет", missing, StringComparison.Ordinal);
            Assert.Contains(@"C:\Чужой-каталог", missing, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- Д6: отказ копии называет файл ---------------------------------------

    /// <summary>
    /// Файл не прочитан — отказ называет ФАЙЛ и ПРИЧИНУ. Проверка проходит через настоящее
    /// состояние дефекта: файл в копируемом дереве открыт монопольно и не читается ничем, а копия
    /// снимается СВОИМИ силами (7-Zip отключён пустым путём) — то есть через тот код, где
    /// исключение и терялось.
    ///
    /// Отдельная тонкость: сам исход тоже назван честно, потому что занятость файла держит
    /// САМ ПРОЦЕСС проверки. Если бы .NET читал такой файл вопреки блокировке (а он этого умеет
    /// не делать), проверка сказала бы об этом, а не позеленела молча.
    /// </summary>
    [Fact]
    public void Отказ_копии_называет_файл_и_причину()
    {
        var root = NewRoot();
        try
        {
            var paths = AppPaths.Under(root);
            var locked = Path.Combine(paths.DshHome, "sessions", "запертый.txt");
            WriteFile(locked, "не отдамся\n");

            var plan = BackupPlanner.Full(paths, workingDirectory: null, engine: null);
            Assert.True(plan.Ok, plan.Error);

            var archive = Path.Combine(root, "copy.zip");
            Assert.False(File.Exists(archive));

            using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

            var result = BackupEngine.Run(
                plan,
                new BackupRequest(archive, SevenZipPath: "", Verify: false),
                paths);

            Assert.False(result.Ok, "копия с нечитаемым файлом не должна считаться снятой");

            // «копия не снята» без виновника — ровно то, чего быть не должно.
            Assert.Contains("запертый.txt", result.Error, StringComparison.Ordinal);
            Assert.Contains("НЕ ПРОЧИТАН", result.Error, StringComparison.Ordinal);
            Assert.Contains("IOException", result.Error, StringComparison.Ordinal);

            // И это в отчёте, а не только в поле ошибки: отчёт — то, что читает человек.
            Assert.Contains("запертый.txt", result.Summary(), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }
}
