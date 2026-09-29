using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Shell;
using DshPanel.Update;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЧЕЛОВЕЧЕСКИЕ СЛОВА ПРИЧИНЫ ЖИВУТ В СЛОВАРЯХ, А НЕ В ДВИЖКЕ — проверка, сторожащая дефект
/// языка, найденный и названный 29.09.2026.
///
/// **Что было.** У пяти разных причин распаковки и у трёх неудач скачивания стоял ОДИН ключ отказа
/// (<c>UpdateArchiveUnreadable</c>, <c>DownloadFailed</c>), поэтому различать случаи приходилось
/// словами — а слова эти движок писал сам, русскими литералами, в
/// <see cref="UpdatePreparation.Detail"/>. Detail же подставляется в строку, которую читает
/// человек (<c>Views\UpdateWindow.axaml.cs</c>, <c>Views\SettingsWindow.axaml.cs</c>): на английском
/// и китайском внутри английской и китайской фразы стоял русский текст — «Could not download the
/// release file: файл сумм: …».
///
/// Три требования, и каждое обязано уметь падать:
///
/// 1. **У каждого значения отказа СВОЙ ключ.** Один ключ на пятерых — это ровно дорога назад:
///    различать случаи снова станет нечем, кроме слов в Detail.
/// 2. **Строка каждого отказа на <c>en</c> и <c>zh</c> — без кириллицы, на <c>ru</c> — с ней.**
///    Читается СЛОВАРЬ языка явно, а не только <see cref="Loc.TIn"/>: ⚠️ <c>TIn</c> на ПУСТОЕ
///    значение молча отдаёт английский, и «в китайском нет кириллицы» зеленело бы на пустом
///    китайском переводе. Пустой перевод — пропажа, а не перевод.
/// 3. **Отказ добывается НАСТОЯЩИМ путём** — подготовкой и распаковкой на своих байтах. Только
///    так проверка стережёт движок, а не выдумку проверки: дефект жил именно в значениях, которые
///    движок подставлял сам. Отказ, до которого настоящего пути нет, обязан быть НАЗВАН
///    исключением (<see cref="EnvironmentMessage"/>) — молча пропустить новый отказ нельзя.
/// </summary>
public class UpdateRefusalLanguageTests
{
    private static readonly string[] Languages = { "ru", "en", "zh" };

    private static readonly Regex Cyrillic = new("[А-Яа-яЁё]", RegexOptions.CultureInvariant);

    /// <summary>Момент свой и вымышленный: от системных часов отчёт зависеть не должен.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Отказы, у которых подставляемое значение — сообщение СРЕДЫ (.NET), а не наше значение:
    /// на русской Windows оно русское, и перевести его нечем — иначе причина исчезнет вовсе
    /// (решение названо в <c>UpdateInstall.Prepare</c> у <c>WriteFailed</c> и в
    /// <c>UpdateStaging.Unpack</c> у <c>ArchiveBroken</c>).
    ///
    /// ⚠️ Список ЗАКРЫТ и проверяется: третий такой отказ обязан уронить прогон, а не проскользнуть
    /// в него тихо. И у этих двух наружу идёт ОБРАМЛЕНИЕ из словаря — оно проверено требованием 2,
    /// как и у всех остальных.
    /// </summary>
    private static readonly UpdateRefusal[] EnvironmentMessage =
    {
        UpdateRefusal.ArchiveBroken,
        UpdateRefusal.WriteFailed,
    };

    // ------------------------------------------------------------------ требование 1

    /// <summary>
    /// У КАЖДОГО ОТКАЗА — СВОЙ КЛЮЧ. Два значения с одним ключом значат, что различать их снова
    /// будет Detail — то есть слова причины, написанные движком на одном языке из трёх.
    /// </summary>
    [Fact]
    public void У_каждого_отказа_свой_ключ()
    {
        var named = new Dictionary<string, UpdateRefusal>(StringComparer.Ordinal);

        foreach (var refusal in Enum.GetValues<UpdateRefusal>())
        {
            var key = UpdateRefusals.Key(refusal);

            if (key.Length == 0)
            {
                Assert.Equal(UpdateRefusal.None, refusal);
                continue;
            }

            Assert.False(
                named.ContainsKey(key),
                $"отказы «{named.GetValueOrDefault(key)}» и «{refusal}» названы ОДНИМ ключом «{key}»: " +
                "различать их снова придётся словами в Detail, а Detail читает человек");

            Assert.True(UpdateRefusalLines.Knows(key), $"панель не знает ключа отказа «{key}»");

            named[key] = refusal;
        }

        Assert.True(named.Count > 20, $"ключей отказа всего {named.Count} — похоже, перечисление прочитано не то");
    }

    // ------------------------------------------------------------------ требование 2

    /// <summary>
    /// СТРОКА ОТКАЗА — НА ЯЗЫКЕ ПАНЕЛИ. Словарь читается ЯВНО: пустое значение в языке считается
    /// пропажей, даже если <see cref="Loc.TIn"/> отдал английскую запаску (на этом уже попались).
    /// Кириллица на <c>en</c> и <c>zh</c> — тот самый дефект: русское слово внутри чужой фразы.
    /// </summary>
    [Fact]
    public void Строка_отказа_на_трёх_языках_и_без_русских_слов_в_чужом()
    {
        foreach (var refusal in Enum.GetValues<UpdateRefusal>())
        {
            var key = UpdateRefusals.Key(refusal);
            if (key.Length == 0) continue;

            foreach (var language in Languages)
            {
                var dictionary = Loc.Dictionary(language);

                Assert.True(
                    dictionary.TryGetValue(key, out var own) && own.Length > 0,
                    $"{language}: у отказа «{key}» нет своей строки — пустая строка это пропажа, а не перевод");

                // Запаска от Loc.TIn не должна подменять отсутствующий перевод: сверяем с словарём.
                Assert.Equal(own, Loc.TIn(language, key));

                if (language == "ru")
                {
                    Assert.True(Cyrillic.IsMatch(own), $"ru: строка отказа «{key}» не по-русски: «{own}»");
                }
                else
                {
                    Assert.False(Cyrillic.IsMatch(own), $"{language}: в строке отказа «{key}» русские слова: «{own}»");
                }
            }
        }
    }

    /// <summary>
    /// ПОДСТАНОВКА — ПО ПРАВИЛУ ПАНЕЛИ, А НЕ ПРОВЕРКИ, и число подстановок совпадает с именем
    /// ключа: у ключа с <c>Format</c> в имени значения есть куда ставить, у остальных — нечего
    /// ставить вовсе. Разойдись это — «форматная» строка показала бы человеку фигурные скобки.
    /// </summary>
    [Fact]
    public void У_ключа_с_Format_есть_куда_ставить_значения()
    {
        foreach (var refusal in Enum.GetValues<UpdateRefusal>())
        {
            var key = UpdateRefusals.Key(refusal);
            if (key.Length == 0) continue;

            var formats = key.Contains("Format", StringComparison.Ordinal);

            foreach (var language in Languages)
            {
                var places = LanguageDecisions.Placeholders(Loc.Dictionary(language)[key]);

                if (formats)
                {
                    Assert.True(
                        places.Count > 0,
                        $"{language}: у ключа «{key}» с Format в имени нет ни одной подстановки — значения некуда ставить");
                }
                else
                {
                    Assert.Empty(places);
                }
            }
        }
    }

    /// <summary>
    /// ЗНАЧЕНИЯ (а не слова) ДОХОДЯТ ДО СТРОКИ ЧЕЛОВЕКА — и на трёх языках сразу: строка собирается
    /// <see cref="UpdateRefusalLines.Fill"/>, тем же правилом, каким её собирает окно.
    /// </summary>
    [Fact]
    public void Значения_отказа_подставляются_в_строку_человека()
    {
        var line = UpdateRefusalLines.Line(
            UpdatePreparation.Refuse(UpdateRefusal.ArchiveSumMismatch, "aaa", "bbb"));

        Assert.Contains("aaa", line, StringComparison.Ordinal);
        Assert.Contains("bbb", line, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", line, StringComparison.Ordinal);
        Assert.DoesNotContain("{1}", line, StringComparison.Ordinal);

        // Отказ без Format значений не показывает: его слова живут в словаре.
        Assert.Equal(
            PanelStrings.UpdateNoArchive,
            UpdateRefusalLines.Line(UpdatePreparation.Refuse(UpdateRefusal.NoArchive, "aaa", "bbb")));
    }

    // ------------------------------------------------------------------ требование 3

    /// <summary>
    /// НА НАСТОЯЩЕМ ПУТИ В ЧУЖОМ ЯЗЫКЕ НЕТ РУССКИХ СЛОВ. Строки берутся не из головы проверки,
    /// а из отказов, добытых подготовкой и распаковкой: имена файлов, хеши, числа, ожидаемое
    /// и полученное. Именно здесь падало бы возвращение русского литерала в Detail.
    /// </summary>
    [Fact]
    public void На_настоящем_пути_в_чужом_языке_нет_русских_слов()
    {
        foreach (var (name, preparation) in RealRefusals())
        {
            if (EnvironmentMessage.Contains(preparation.Refusal)) continue;

            var key = preparation.RefusalKey;
            Assert.NotEqual(string.Empty, key);

            foreach (var language in Languages)
            {
                var line = Line(language, preparation);

                if (language == "ru")
                {
                    Assert.True(
                        Cyrillic.IsMatch(line),
                        $"{name}: русская строка отказа «{key}» осталась без русских слов: «{line}»");
                }
                else
                {
                    Assert.False(
                        Cyrillic.IsMatch(line),
                        $"{name}: на «{language}» внутри строки отказа «{key}» стоят русские слова: «{line}»");
                }
            }
        }
    }

    /// <summary>
    /// ЗНАЧЕНИЕ ОТКАЗА НЕ ТЕРЯЕТСЯ ПО ДОРОГЕ: у «форматного» отказа то, что движок назвал
    /// (имя файла, хеш, версия), обязано стоять в строке человека. Мутация «ключ потерял Format» —
    /// значения перестали подставляться — падает ровно здесь.
    /// </summary>
    [Fact]
    public void Значения_отказа_доходят_до_строки()
    {
        foreach (var (name, preparation) in RealRefusals())
        {
            if (EnvironmentMessage.Contains(preparation.Refusal)) continue;

            var key = preparation.RefusalKey;
            if (!key.Contains("Format", StringComparison.Ordinal)) continue;

            var line = Line("ru", preparation);

            if (preparation.Detail.Length > 0)
            {
                Assert.Contains(preparation.Detail, line, StringComparison.Ordinal);
            }

            if (preparation.DetailMore.Length > 0)
            {
                Assert.Contains(preparation.DetailMore, line, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("{0}", line, StringComparison.Ordinal);
            Assert.DoesNotContain("{1}", line, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(line), $"{name}: строка отказа «{key}» пуста");
        }
    }

    /// <summary>
    /// КАЖДЫЙ ОТКАЗ ЛИБО ПРОВЕРЕН НАСТОЯЩИМ ПУТЁМ, ЛИБО НАЗВАН ИСКЛЮЧЕНИЕМ. Новый отказ без случая
    /// в <see cref="RealRefusals"/> падает здесь: иначе он тихо войдёт в дерево, и никто не спросит,
    /// откуда возьмутся его слова в чужом языке.
    /// </summary>
    [Fact]
    public void Каждый_отказ_проверен_настоящим_путём_или_назван_исключением()
    {
        var covered = RealRefusals()
            .Select(pair => pair.Preparation.Refusal)
            .ToHashSet();

        foreach (var refusal in Enum.GetValues<UpdateRefusal>())
        {
            if (UpdateRefusals.Key(refusal).Length == 0) continue;

            Assert.True(
                covered.Contains(refusal) || EnvironmentMessage.Contains(refusal),
                $"отказ «{refusal}» не добыт ни одним настоящим путём: заведи ему случай в RefusalsFromRealPaths " +
                "или назови его исключением в EnvironmentMessage — тихо пропустить новый отказ проверка не даёт");
        }
    }

    // ------------------------------------------------------------------ настоящие пути

    /// <summary>
    /// ОТКАЗЫ, ДОБЫТЫЕ ДВИЖКОМ: у каждого свой стенд, свои байты и своя причина. Значения здесь
    /// только наши — имена файлов, хеши, версии, числа; сообщение среды подставляют ровно два
    /// отказа (<see cref="EnvironmentMessage"/>), и это названо вслух.
    /// </summary>
    private static List<(string Name, UpdatePreparation Preparation)> RealRefusals()
    {
        var found = new List<(string, UpdatePreparation)>();

        using (var stand = new Stand())
        {
            found.Add(("нет права готовить", Prepare(stand, Ready(stand), allowed: false)));
        }

        using (var stand = new Stand())
        {
            // Пустой тег: числовая часть версии не разбирается ни во что.
            found.Add(("номер выпуска не разобран", Prepare(stand, Ready(stand), release: "")));
        }

        using (var stand = new Stand())
        {
            found.Add(("выпуск не новее", Prepare(stand, Ready(stand), release: "v2.0.0", current: "2.0.0")));
        }

        using (var stand = new Stand())
        {
            found.Add(("в выпуске нет архива", Prepare(stand, new ReleaseAssets(string.Empty, string.Empty, SumsUrl))));
        }

        using (var stand = new Stand())
        {
            found.Add(("в выпуске нет сумм", Prepare(stand, new ReleaseAssets(ArchiveUrl, string.Empty, string.Empty))));
        }

        using (var stand = new Stand())
        {
            found.Add(("не названа папка панели", Prepare(stand, Ready(stand), target: "\\")));
        }

        using (var stand = new Stand())
        {
            var assets = Ready(stand);
            stand.Download.FailUrl = SumsUrl;

            found.Add(("файл сумм не скачался", Prepare(stand, assets)));
        }

        using (var stand = new Stand())
        {
            stand.Download.Add(SumsUrl, Encoding.UTF8.GetBytes("# одни комментарии" + Environment.NewLine));
            stand.Download.Add(ArchiveUrl, PanelArchive());

            found.Add(("файл сумм пуст", Prepare(stand, new ReleaseAssets(ArchiveUrl, string.Empty, SumsUrl))));
        }

        using (var stand = new Stand())
        {
            var assets = Ready(stand);
            stand.Download.FailUrl = ArchiveUrl;

            found.Add(("архив не скачался", Prepare(stand, assets)));
        }

        using (var stand = new Stand())
        {
            var zip = PanelArchive();
            stand.Download.Add(SumsUrl, Encoding.UTF8.GetBytes(SumsText((UpdateAssets.SetupName, Encoding.UTF8.GetBytes("установщик")))));
            stand.Download.Add(ArchiveUrl, zip);

            found.Add(("нет строки для архива", Prepare(stand, new ReleaseAssets(ArchiveUrl, string.Empty, SumsUrl))));
        }

        using (var stand = new Stand())
        {
            stand.Download.Add(SumsUrl, Encoding.UTF8.GetBytes(SumsText((UpdateAssets.ArchiveName, PanelArchive("9.9.9")))));
            stand.Download.Add(ArchiveUrl, PanelArchive("2.1.0"));

            found.Add(("сумма архива не сошлась", Prepare(stand, new ReleaseAssets(ArchiveUrl, string.Empty, SumsUrl))));
        }

        using (var stand = new Stand())
        {
            found.Add(("архив не читается", Prepare(stand, Ready(stand, Encoding.UTF8.GetBytes("это не архив")))));
        }

        using (var stand = new Stand())
        {
            found.Add(("в архиве нет файлов", Prepare(stand, Ready(stand, EmptyZip()))));
        }

        using (var stand = new Stand())
        {
            var zip = Zip((UpdateStaging.PanelExeName, "панель"), ("../мимо.txt", "мимо"));

            found.Add(("записи мимо каталога распаковки", Prepare(stand, Ready(stand, zip))));
        }

        using (var stand = new Stand())
        {
            var zip = Zip(("readme.txt", "это не панель"));

            found.Add(("в архиве нет панели", Prepare(stand, Ready(stand, zip))));
        }

        using (var stand = new Stand())
        {
            found.Add(("версия внутри архива не та", Prepare(stand, Ready(stand), versionOf: _ => "2.0.9")));
        }

        using (var stand = new Stand())
        {
            var zip = PanelArchive();
            var setup = Encoding.UTF8.GetBytes("установщик");
            stand.Download.Add(SumsUrl, Encoding.UTF8.GetBytes(SumsText((UpdateAssets.ArchiveName, zip), (UpdateAssets.SetupName, setup))));
            stand.Download.Add(ArchiveUrl, zip);
            stand.Download.FailUrl = SetupUrl;

            found.Add(("установщик не скачался", Prepare(stand, new ReleaseAssets(ArchiveUrl, SetupUrl, SumsUrl))));
        }

        using (var stand = new Stand())
        {
            var zip = PanelArchive();
            stand.Download.Add(SumsUrl, Encoding.UTF8.GetBytes(SumsText((UpdateAssets.ArchiveName, zip))));
            stand.Download.Add(ArchiveUrl, zip);
            stand.Download.Add(SetupUrl, Encoding.UTF8.GetBytes("установщик"));

            found.Add(("нет строки для установщика", Prepare(stand, new ReleaseAssets(ArchiveUrl, SetupUrl, SumsUrl))));
        }

        using (var stand = new Stand())
        {
            var zip = PanelArchive();
            var promised = Encoding.UTF8.GetBytes("обещанный установщик");
            stand.Download.Add(SumsUrl, Encoding.UTF8.GetBytes(SumsText((UpdateAssets.ArchiveName, zip), (UpdateAssets.SetupName, promised))));
            stand.Download.Add(ArchiveUrl, zip);
            stand.Download.Add(SetupUrl, Encoding.UTF8.GetBytes("подменённый установщик"));

            found.Add(("сумма установщика не сошлась", Prepare(stand, new ReleaseAssets(ArchiveUrl, SetupUrl, SumsUrl))));
        }

        using (var stand = new Stand())
        {
            var missing = Path.Combine(stand.Root, "Programs", "Нет такой папки") + "\\";

            found.Add(("страховочной копии нет", Prepare(stand, Ready(stand), target: missing)));
        }

        // --- защита самой распаковки: до этих случаев подготовка не доводит, но ключи у них свои,
        // --- и язык у этих ключей обязан быть свой тоже
        using (var stand = new Stand())
        {
            var archive = Path.Combine(stand.Root, "архив.zip");
            File.WriteAllBytes(archive, PanelArchive());

            var occupied = Path.Combine(stand.Root, "занято-файлом");
            File.WriteAllText(occupied, "я файл, а не папка");

            found.Add(("архива нет", FromUnpack(UpdateStaging.Unpack(Path.Combine(stand.Root, "нет.zip"), Path.Combine(stand.Root, "куда")))));
            found.Add(("папка распаковки не названа", FromUnpack(UpdateStaging.Unpack(archive, "   "))));
            found.Add(("архив не разобрался", FromUnpack(UpdateStaging.Unpack(archive, occupied))));
        }

        return found;
    }

    /// <summary>
    /// Отказ распаковки глазами движка подготовки: ровно так его забирает <c>UpdateInstall.Prepare</c>
    /// (<c>Fail(unpack.Refusal, unpack.Detail, unpack.More)</c>). Отдельного ответа здесь не
    /// выдумывается — берутся те же случай и значения.
    /// </summary>
    private static UpdatePreparation FromUnpack(UpdateUnpack unpack) =>
        UpdatePreparation.Refuse(unpack.Refusal, unpack.Detail, unpack.More);

    /// <summary>Строка человека на ЯВНОМ языке — тем же правилом подстановки, что у окна.</summary>
    private static string Line(string language, UpdatePreparation preparation)
    {
        var key = preparation.RefusalKey;

        return UpdateRefusalLines.Fill(
            key,
            Loc.TIn(language, key),
            preparation.Detail,
            preparation.DetailMore);
    }

    // ------------------------------------------------------------------ оснастка

    private const string SumsUrl = "https://example.invalid/SHA256SUMS.txt";
    private const string ArchiveUrl = "https://example.invalid/DshPanel.zip";
    private const string SetupUrl = "https://example.invalid/dsh-panel-setup.exe";

    /// <summary>
    /// Временный стенд: свой корень панели, своя папка «установленной» панели и своя подстановка
    /// вместо сети. Ни одного чужого каталога, ни одного живого запроса.
    /// </summary>
    private sealed class Stand : IDisposable
    {
        public Stand()
        {
            Root = Path.Combine(Path.GetTempPath(), "dsh-panel-refusal-language-tests", Guid.NewGuid().ToString("N"));

            Target = Path.Combine(Root, "Programs", "DSH Panel");
            Paths = AppPaths.Under(Path.Combine(Root, "run"));

            Directory.CreateDirectory(Target);
            File.WriteAllText(Path.Combine(Target, UpdateStaging.PanelExeName), "прежняя панель");
        }

        public string Root { get; }

        public string Target { get; }

        public AppPaths Paths { get; }

        public FakeDownload Download { get; } = new();

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);

                var parent = Path.GetDirectoryName(Root);
                if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent);
            }
            catch
            {
                // Родитель занят другой проверкой — это не ошибка.
            }
        }
    }

    /// <summary>Подстановка вместо сети: отдаёт свои байты и умеет отказать на названном адресе.</summary>
    private sealed class FakeDownload : IUpdateDownload
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Адрес, на котором подстановка обязана отказать (пусто — не отказывает).</summary>
        public string FailUrl { get; set; } = string.Empty;

        public FakeDownload Add(string url, byte[] bytes)
        {
            _files[url] = bytes;
            return this;
        }

        public void Save(string url, string path, Action<long, long>? progress)
        {
            if (FailUrl.Length > 0 && string.Equals(url, FailUrl, StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("HTTP 500");
            }

            if (!_files.TryGetValue(url, out var bytes))
            {
                throw new InvalidOperationException("подстановки для адреса нет: " + url);
            }

            File.WriteAllBytes(path, bytes);
        }
    }

    /// <summary>Запрос на подготовку: папка панели уходит с хвостовым «\», как у человека.</summary>
    private static UpdatePreparation Prepare(
        Stand stand,
        ReleaseAssets assets,
        bool allowed = true,
        string release = "v2.1.0",
        string current = "2.0.0",
        string? target = null,
        Func<string, string>? versionOf = null) =>
        UpdateInstall.Prepare(
            new UpdateRequest(
                stand.Paths,
                current,
                target ?? stand.Target + "\\",
                release,
                assets,
                allowed,
                IgnoreNewer: false,
                StartArgument: string.Empty,
                MutexName: string.Empty,
                At: Now),
            stand.Download,
            versionOf ?? (_ => "2.1.0"));

    /// <summary>Скачанные файлы подстановки: суммы со своим счётом, архив и, если нужно, установщик.</summary>
    private static ReleaseAssets Ready(Stand stand, byte[]? archive = null)
    {
        var zip = archive ?? PanelArchive();

        stand.Download.Add(SumsUrl, Encoding.UTF8.GetBytes(SumsText((UpdateAssets.ArchiveName, zip))));
        stand.Download.Add(ArchiveUrl, zip);

        return new ReleaseAssets(ArchiveUrl, string.Empty, SumsUrl);
    }

    /// <summary>Архив панели, каким его кладёт сборка: сама панель и что-то рядом.</summary>
    private static byte[] PanelArchive(string version = "2.1.0") =>
        Zip(
            (UpdateStaging.PanelExeName, "новая панель " + version),
            ("DshPanel.dll", "новая библиотека"));

    /// <summary>Архив без единой записи: читается, но распаковывать из него нечего.</summary>
    private static byte[] EmptyZip()
    {
        using var memory = new MemoryStream();

        using (new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Записей нет намеренно.
        }

        return memory.ToArray();
    }

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

    /// <summary>Файл сумм в том виде, в каком его публикует выпуск: «хэш  имя файла».</summary>
    private static string SumsText(params (string Name, byte[] Bytes)[] files) =>
        string.Join(
            Environment.NewLine,
            files.Select(file =>
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file.Bytes)).ToLowerInvariant()
                + "  " + file.Name));
}
