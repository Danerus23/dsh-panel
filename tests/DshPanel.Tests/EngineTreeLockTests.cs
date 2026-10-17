using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЗАМОК ПРОВЕРЕННОГО ДВИЖКА (этап E3, шаг E3.1).
///
/// Решение владельца 10.10.2026: движок ставится **точными версиями и суммами** («как решит npm» —
/// нельзя: замер показал 232 расхождения у ТОЙ ЖЕ версии движка). Замок снимается замером
/// (`upd-build\make-engine-lock.ps1`) из СВОЕЙ изолированной установки и входит в сборку
/// **встроенным ресурсом** — тогда панель умеет поставить проверенное дерево, а в сеть идёт только
/// npm и только за вложениями из замка.
///
/// ⚠️ **РЕДАКЦИЯ 2 — ПОСЛЕ ЗАМЕРА 10.10.2026.** Первая редакция была нашим дайджестом (путь, имя,
/// версия, сумма, адрес) и для установки **не годилась**: в ней нет поля `dependencies`, и `npm ci`
/// поставил по ней **1 пакет вместо 588** (`upd-build\probe-install-by-lock.ps1`). Поэтому внутри
/// теперь лежит **замок npm целиком**, а оболочка добавляет только своё. Здесь проверяется САМ ФАЙЛ
/// и его разбор; установку проверяет `EngineInstallerTests`, а живьём — прогон в ВМ.
/// </summary>
public class EngineTreeLockTests
{
    private const string Engine = "@deepseek-ai/dsh";
    private const string Probe = "9.9.9-probe";

    /// <summary>
    /// ФАЙЛ, КОТОРЫЙ ЛЕЖИТ В ДЕРЕВЕ, РАЗБИРАЕТСЯ И НЕ ПУСТ. Проверка идёт по **файлу из корня
    /// репозитория**, а не по встроенному ресурсу: так видно, что именно уедет в сборку, и что
    /// правка файла не осталась «на словах».
    /// </summary>
    [Fact]
    public void Замок_из_дерева_разбирается_и_полон()
    {
        var path = Path.Combine(RepositoryRoot(), "engine-lock.json");

        Assert.True(File.Exists(path), "нет файла замка: " + path);

        var text = File.ReadAllText(path);
        var parsed = EngineTreeLock.Parse(text);

        Assert.True(parsed.Any, "замок не разобрался или пуст");
        Assert.Contains($"\"format\": {EngineTreeLock.Format}", text, StringComparison.Ordinal);

        // ⚠️ СУММА ЕСТЬ У КАЖДОГО ПАКЕТА: замок без сумм — это список версий, а не замок,
        // и «ставить по замку» с ним значило бы обещать проверку, которой нет.
        Assert.True(parsed.FullySummed, "в замке есть пакеты без контрольной суммы");

        // И объявленное число записей совпадает с настоящим: обрезанный файл выглядел бы
        // «замком из двухсот пакетов», и панель поставила бы половину дерева.
        Assert.True(parsed.Complete, $"замок объявляет {parsed.Declared} пакетов, а записей {parsed.Entries.Count}");
        Assert.Equal(588, parsed.Entries.Count);

        // ⚠️ ЗАМОК ПРО ТУ ЖЕ ВЕРСИЮ, НА КОТОРОЙ ПАНЕЛЬ МЕРИЛАСЬ. Иначе он «проверенный» только
        // по названию: проба умений снята на одной версии, а ставить панель будет другую.
        Assert.Equal(EngineRpcSurface.MeasuredOnVersion, parsed.EngineVersion);
        Assert.Equal(Engine, parsed.EngineName);

        // Ядро движка названо в замке отдельной записью — по нему установщик и находит движок.
        var engine = parsed.Find(Engine);

        Assert.NotNull(engine);
        Assert.Equal(parsed.EngineVersion, engine!.Version);
        Assert.True(engine.HasSum);
    }

    /// <summary>
    /// ЗАМОК БЕЗ ОБЪЯВЛЕННОЙ РЕДАКЦИИ НЕ ЧИТАЕТСЯ. Это не придирка: старая редакция (дайджест) как
    /// раз и не объявляла ничего, кроме своих полей, — а по ней `npm ci` ставит ОДИН пакет вместо 588.
    /// Принять её значило бы «замок есть», и установка молча собрала бы не то дерево.
    /// </summary>
    [Fact]
    public void Замок_без_объявленной_редакции_не_читается()
    {
        var valid = Mini("\"node_modules/a\":{\"version\":\"1.0.0\",\"integrity\":\"sha512-A\"}", declared: 2);

        Assert.True(EngineTreeLock.Parse(valid).Any, "годный замок не разобрался — проверять нечего");

        var headless = valid.Replace("\"format\":2,", string.Empty, StringComparison.Ordinal);

        Assert.DoesNotContain("\"format\"", headless, StringComparison.Ordinal);
        Assert.False(EngineTreeLock.Parse(headless).Any);
    }

    /// <summary>
    /// ЗАМОК npm ЛЕЖИТ ВНУТРИ ЦЕЛИКОМ, И ПАНЕЛЬ ПИШЕТ ЕГО КАК ЕСТЬ. Это и есть причина редакции 2:
    /// `npm ci` ставит дерево по СВОЕМУ замку, и если панель что-то из него выбросит или перепишет,
    /// дерево у человека окажется не тем, что проверяли. Здесь проверяется, что выброшенного нет:
    /// корневые зависимости на месте, версия схемы npm на месте, счёт записей тот же.
    /// </summary>
    [Fact]
    public void Панель_отдаёт_npm_его_замок_целиком()
    {
        var parsed = EngineTreeLock.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "engine-lock.json")));

        Assert.True(parsed.Any);

        using var npm = JsonDocument.Parse(parsed.NpmLockJson);
        var root = npm.RootElement;

        Assert.Equal(3, root.GetProperty("lockfileVersion").GetInt32());
        Assert.Equal("dsh-panel-engine", root.GetProperty("name").GetString());

        var packages = root.GetProperty("packages");
        var paths = packages.EnumerateObject()
            .Where(item => item.Name.Contains("node_modules/", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(parsed.Entries.Count, paths.Count);

        // Корневая запись — единственный источник `package.json`: два места разойтись не могут.
        var rootEntry = packages.GetProperty(string.Empty);
        var dependencies = rootEntry.GetProperty("dependencies");

        Assert.Equal(parsed.Dependencies.Count, dependencies.EnumerateObject().Count());
        Assert.Equal(parsed.EngineVersion, dependencies.GetProperty(Engine).GetString());
        Assert.DoesNotContain("^", parsed.PackageJson, StringComparison.Ordinal);

        using var product = JsonDocument.Parse(parsed.PackageJson);

        Assert.Equal(rootEntry.GetProperty("name").GetString(), product.RootElement.GetProperty("name").GetString());
        Assert.Equal(rootEntry.GetProperty("version").GetString(), product.RootElement.GetProperty("version").GetString());
        Assert.Equal(
            parsed.EngineVersion,
            product.RootElement.GetProperty("dependencies").GetProperty(Engine).GetString());

        // А поля, без которых npm собирает ДРУГОЕ дерево, на месте у записей: замер показал,
        // что без `dependencies` ставится один пакет вместо 588.
        Assert.Contains("\"dependencies\"", parsed.NpmLockJson, StringComparison.Ordinal);
        Assert.Contains("\"peerDependencies\"", parsed.NpmLockJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// ВСТРОЕННЫЙ РЕСУРС — ТОТ ЖЕ ЗАМОК. Файл в дереве и файл в сборке обязаны совпадать: панель
    /// ставит движок по встроенному, а проверяют глазами и правят — тот, что в дереве.
    /// </summary>
    [Fact]
    public void Встроенный_замок_совпадает_с_файлом_из_дерева()
    {
        var path = Path.Combine(RepositoryRoot(), "engine-lock.json");

        var fromFile = EngineTreeLock.Parse(File.ReadAllText(path));
        var embedded = EngineTreeLock.Load();

        Assert.True(embedded.Any, "встроенный замок не прочитался — в сборку он не попал");
        Assert.Equal(fromFile.EngineVersion, embedded.EngineVersion);
        Assert.Equal(fromFile.Entries.Count, embedded.Entries.Count);
        Assert.Equal(fromFile.Declared, embedded.Declared);
        Assert.Equal(fromFile.NpmLockJson.Length, embedded.NpmLockJson.Length);
    }

    /// <summary>
    /// ЧУЖАЯ СХЕМА НЕ УГАДЫВАЕТСЯ, а битый файл — это «замка нет», а не падение: замок обновляют
    /// отдельно от выпуска панели, и сломать его может кто угодно и когда угодно. Отдельно —
    /// ПЕРВАЯ РЕДАКЦИЯ: она выглядит знакомо, но по ней npm поставит один пакет, поэтому «замка нет»
    /// честнее, чем «замок есть».
    /// </summary>
    [Theory]
    [InlineData("{ это не json")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>файл переехал</html>")]
    [InlineData("[]")]
    [InlineData("{\"entries\":{}}")]
    [InlineData("{\"entries\":[]}")]
    [InlineData("{\"format\":1,\"engine\":{\"name\":\"@deepseek-ai/dsh\",\"version\":\"1.0.0\"},\"entries\":[{\"name\":\"a\",\"version\":\"1.0.0\"}]}")]
    [InlineData("{\"format\":3,\"lock\":{\"packages\":{}}}")]
    [InlineData("{\"format\":\"2\",\"lock\":{\"packages\":{}}}")]
    [InlineData("{\"format\":2,\"engine\":{\"name\":\"@deepseek-ai/dsh\",\"version\":\"1.0.0\"}}")]
    [InlineData("{\"format\":2,\"engine\":{\"name\":\"@deepseek-ai/dsh\",\"version\":\"1.0.0\"},\"lock\":{\"packages\":[]}}")]
    [InlineData("{\"format\":2,\"engine\":{\"name\":\"@deepseek-ai/dsh\",\"version\":\"1.0.0\"},\"lock\":{\"packages\":{\"node_modules/a\":{\"version\":\"1.0.0\"}}}}")]
    public void Битый_или_чужой_замок_значит_замка_нет(string json)
    {
        var parsed = EngineTreeLock.Parse(json);

        Assert.False(parsed.Any);
        Assert.Empty(parsed.Entries);
        Assert.False(parsed.FullySummed);
    }

    /// <summary>
    /// ДВЕ ПРАВДЫ ОБ ОДНОМ ДЕРЕВЕ — ЭТО «ЗАМКА НЕТ». Версия движка объявлена в оболочке И лежит
    /// в записи самого движка внутри замка npm; разошлись — значит файл правили по частям, и
    /// доверять ему нельзя. Имени движка нет вовсе — то же самое.
    /// </summary>
    [Theory]
    [InlineData("\"1.0.0\"", "\"2.0.0\"")]  // оболочка и запись разошлись
    [InlineData("\"1.0.0\"", "")]           // записи движка в замке нет
    public void Версия_движка_из_оболочки_сверяется_с_замком(string told, string inside)
    {
        var engine = inside.Length == 0
            ? string.Empty
            : $"\"node_modules/@deepseek-ai/dsh\":{{\"version\":{inside},\"integrity\":\"sha512-A\"}},";

        var json =
            "{\"format\":2,\"engine\":{\"name\":\"@deepseek-ai/dsh\",\"version\":" + told + "},\"packages\":1," +
            "\"lock\":{\"lockfileVersion\":3,\"packages\":{\"\":{\"dependencies\":{\"@deepseek-ai/dsh\":" + told + "}}," +
            engine +
            "\"node_modules/a\":{\"version\":\"1.0.0\",\"integrity\":\"sha512-A\"}}}}";

        Assert.False(EngineTreeLock.Parse(json).Any);
    }

    /// <summary>
    /// ИМЯ БЕРЁТСЯ ИЗ ПУТИ (у пакетов со областью оно состоит из двух частей и разрезать его
    /// по слэшу нельзя), а запись без версии пропускается: она не описывает пакет.
    /// </summary>
    [Fact]
    public void Имя_берётся_из_пути_а_неполные_записи_пропускаются()
    {
        // Объявлено 4 записи: движок, пакет со областью, вложенный и «c» — но у «c» нет версии,
        // поэтому пакетом она не считается, и замок перестаёт быть целым.
        var parsed = Parse(
            "\"node_modules/@scope/name\":{\"version\":\"1.2.3\",\"integrity\":\"sha512-A\"}," +
            "\"node_modules/a/node_modules/b\":{\"version\":\"4.5.6\",\"integrity\":\"sha512-B\"}," +
            "\"node_modules/c\":{\"integrity\":\"sha512-C\"}",
            declared: 4);

        Assert.Equal(3, parsed.Entries.Count);
        Assert.Equal("1.2.3", parsed.Find("@scope/name")!.Version);
        Assert.Equal("4.5.6", parsed.Find("b")!.Version);
        Assert.Null(parsed.Find("c"));

        // Запись без версии в замке есть, но пакетом не считается — и «целым» замок уже не назвать.
        Assert.False(parsed.Complete);
    }

    /// <summary>Пакет ищется по ИМЕНИ, а не «берётся первый»: замок на 588 пакетов иначе бесполезен.</summary>
    [Fact]
    public void Пакет_ищется_по_имени()
    {
        var parsed = Parse(
            "\"node_modules/a\":{\"version\":\"1.0.0\",\"integrity\":\"sha512-A\"}," +
            "\"node_modules/b\":{\"version\":\"2.0.0\",\"integrity\":\"sha512-B\"}",
            declared: 2);

        Assert.Equal("2.0.0", parsed.Find("b")!.Version);
        Assert.Equal("1.0.0", parsed.Find(" a ")!.Version);
        Assert.Null(parsed.Find("нет-такого"));
        Assert.Null(parsed.Find(null));
    }

    // ---- два предохранителя замка (требование владельца 10.10.2026) --------------------------

    /// <summary>
    /// (1) ВСЕ АДРЕСА В ЗАМКЕ ВЕДУТ ТОЛЬКО НА ОФИЦИАЛЬНЫЙ РЕЕСТР.
    ///
    /// Замок — это то, что панель поставит у человека. Пакет, взятый «с другого хоста», означал бы,
    /// что ставится не то, что проверяли: это отказ, а не замечание. Замер 10.10.2026: в снятом
    /// замке **все 588** адресов ведут на `registry.npmjs.org`, других хостов нет вовсе.
    /// </summary>
    [Fact]
    public void Все_адреса_в_замке_ведут_на_официальный_реестр()
    {
        var parsed = EngineTreeLock.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "engine-lock.json")));

        Assert.True(parsed.Any);
        Assert.True(parsed.AllFromRegistry, "в замке есть пакет не с официального реестра");
        Assert.Empty(parsed.ForeignSources);

        // И у КАЖДОЙ записи адрес вообще есть: «пусто» не значит «официальный».
        Assert.All(parsed.Entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Resolved)));
        Assert.All(parsed.Entries, entry => Assert.True(entry.FromOfficialRegistry));

        // А чужой хост — это не «мелочь»: он виден и назван.
        var swapped = Parse(
            "\"node_modules/a\":{\"version\":\"1.0.0\",\"integrity\":\"sha512-A\",\"resolved\":\"https://registry.npmjs.org/a/-/a-1.0.0.tgz\"}," +
            "\"node_modules/b\":{\"version\":\"2.0.0\",\"integrity\":\"sha512-B\",\"resolved\":\"https://злой-зеркало.example/b/-/b-2.0.0.tgz\"}",
            declared: 2);

        Assert.True(swapped.Any);
        Assert.False(swapped.AllFromRegistry);
        Assert.Equal("b", Assert.Single(swapped.ForeignSources).Name);

        // Запись без адреса — тоже «не с официального реестра».
        var headless = Parse("\"node_modules/c\":{\"version\":\"3.0.0\",\"integrity\":\"sha512-C\"}", declared: 1);

        Assert.False(headless.AllFromRegistry);
        Assert.Equal("c", Assert.Single(headless.ForeignSources).Name);
    }

    /// <summary>
    /// (2) СУММА САМОГО ЗАМКА ЗАКРЕПЛЕНА В СБОРКЕ. Слова владельца: *«сумма самого замка закреплена
    /// в сборке, чтобы „обновление данных“ не подсунуло другой набор»*.
    ///
    /// Проверка считает сумму ФАЙЛА из дерева и требует, чтобы она совпала с закреплённой строкой:
    /// так правка замка без правки суммы не проходит, а `Load()` отказывается читать подменённый
    /// встроенный ресурс.
    /// </summary>
    [Fact]
    public void Сумма_замка_закреплена_в_сборке()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "engine-lock.json"));

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

        Assert.Equal(EngineTreeLock.PinnedSum, hash, ignoreCase: true);
        Assert.True(EngineTreeLock.MatchesPinned(text), "закреплённая сумма не совпала с файлом замка");

        // Подменённый набор сумму не проходит — и это НЕ «мелкое расхождение», а другой замок.
        Assert.False(EngineTreeLock.MatchesPinned(text.Replace("0.1.5-rc.2", "0.1.5-rc.9", StringComparison.Ordinal)));
        Assert.False(EngineTreeLock.MatchesPinned(string.Empty));
        Assert.False(EngineTreeLock.MatchesPinned(null));

        // И встроенный замок читается — то есть проверку своей суммы он прошёл.
        Assert.True(EngineTreeLock.Load().Any, "встроенный замок не прошёл проверку закреплённой суммы");
    }

    // ---- что этой машине нужно, а что нет (замер 10.10.2026) ----------------------------------

    /// <summary>
    /// НУЖЕН ЛИ ПАКЕТ ЭТОЙ МАШИНЕ — ПРАВИЛО npm, И ОНО ПРОВЕРЯЕТСЯ ЗДЕСЬ.
    ///
    /// Замер: из 588 путей на Windows появляются 519. 68 необязательных пакетов просят
    /// `darwin`/`linux` или `musl`/`glibc`, а один помечен `extraneous` и не ставится вовсе.
    /// Без этого правила честная установка выглядела бы 69 расхождениями, и настоящая пропажа
    /// утонула бы среди них. Отрицания (`!win32`) и `any` — тоже правило npm, и они проверяются.
    /// </summary>
    [Theory]
    [InlineData("win32", "x64", "", true)]          // обычный пакет Windows
    [InlineData("darwin", "arm64", "", false)]      // не наша система
    [InlineData("!win32", "", "", false)]           // прямо запрещено нам
    [InlineData("win32", "arm64", "", false)]       // наша система, чужая разрядность
    [InlineData("", "", "musl", false)]             // нужна чужая libc
    [InlineData("any", "any", "", true)]            // годится везде
    public void Пригодность_пакета_считается_правилом_npm(string os, string cpu, string libc, bool needed)
    {
        var entry = new EngineTreeLockEntry(
            "node_modules/a", "a", "1.0.0", "sha512-A", "https://registry.npmjs.org/a/-/a-1.0.0.tgz",
            Optional: false, Extraneous: false,
            Os: os.Length == 0 ? Array.Empty<string>() : new[] { os },
            Cpu: cpu.Length == 0 ? Array.Empty<string>() : new[] { cpu },
            Libc: libc.Length == 0 ? Array.Empty<string>() : new[] { libc });

        Assert.Equal(needed, entry.NeededHere("win32", "x64", string.Empty));
    }

    /// <summary>Необязательный и «лишний по мнению npm» пакеты этой машине не нужны — и это НЕ пропажа.</summary>
    [Fact]
    public void Необязательные_пакеты_этой_машине_не_нужны()
    {
        var optional = new EngineTreeLockEntry(
            "node_modules/opt", "opt", "1.0.0", "sha512-A", "https://registry.npmjs.org/opt/-/opt-1.0.0.tgz",
            Optional: true, Extraneous: false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        var extraneous = new EngineTreeLockEntry(
            "node_modules/extra", "extra", "1.0.0", "sha512-A", "https://registry.npmjs.org/extra/-/extra-1.0.0.tgz",
            Optional: false, Extraneous: true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        // А обычный пакет — НУЖЕН: иначе «не нужны» превратилось бы в «не нужен никто», и сверка
        // перестала бы замечать пропажу целого дерева.
        var plain = new EngineTreeLockEntry(
            "node_modules/plain", "plain", "1.0.0", "sha512-A", "https://registry.npmjs.org/plain/-/plain-1.0.0.tgz",
            Optional: false, Extraneous: false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        Assert.False(optional.NeededHere("win32", "x64", string.Empty));
        Assert.False(extraneous.NeededHere("win32", "x64", string.Empty));
        Assert.True(plain.NeededHere("win32", "x64", string.Empty));
    }

    // ---- сверка установленного дерева с замком -----------------------------------------------

    /// <summary>
    /// ДЕРЕВО СОШЛОСЬ С ЗАМКОМ — и тогда это именно «сошлось», а не «промолчало»: сверка была.
    /// </summary>
    [Fact]
    public void Дерево_сошлось_с_замком()
    {
        var expected = Parse(
            "\"node_modules/a\":{\"version\":\"1.0.0\",\"integrity\":\"sha512-A\"}," +
            "\"node_modules/b\":{\"version\":\"2.0.0\",\"integrity\":\"sha512-B\"}",
            declared: 3);

        var report = EngineTreeCheck.Against(
            expected,
            new[] { EnginePath, "node_modules/a", "node_modules/b" },
            Versions(("node_modules/a", "1.0.0"), ("node_modules/b", "2.0.0")));

        Assert.True(report.Checked);
        Assert.True(report.Ok);
        Assert.Empty(report.Problems);
    }

    /// <summary>
    /// ТРИ РАСХОЖДЕНИЯ И КАЖДОЕ НАЗВАНО: пакета нет · версия другая · в дереве лишнее.
    /// «Состав сошёлся» без имён бесполезно: человеку надо знать, ЧТО именно ставить заново.
    /// </summary>
    [Fact]
    public void Расхождения_называются_поимённо()
    {
        var expected = Parse(
            "\"node_modules/a\":{\"version\":\"1.0.0\",\"integrity\":\"sha512-A\"}," +
            "\"node_modules/b\":{\"version\":\"2.0.0\",\"integrity\":\"sha512-B\"}," +
            "\"node_modules/c\":{\"version\":\"3.0.0\",\"integrity\":\"sha512-C\"}",
            declared: 4);

        var report = EngineTreeCheck.Against(
            expected,
            new[] { EnginePath, "node_modules/a", "node_modules/b", "node_modules/лишний" },
            Versions(
                ("node_modules/a", "1.0.0"),
                ("node_modules/b", "2.5.0"),      // версия другая
                ("node_modules/лишний", "9.9.9"))); // в замке его нет («c» в дереве нет вовсе)

        Assert.True(report.Checked);
        Assert.False(report.Ok);

        Assert.Equal("c", Assert.Single(report.Missing).Name);
        Assert.Equal("node_modules/c", Assert.Single(report.Missing).Actual);

        var version = Assert.Single(report.Versions);
        Assert.Equal("b", version.Name);
        Assert.Equal("2.0.0", version.Expected);
        Assert.Equal("2.5.0", version.Actual);

        Assert.Equal("node_modules/лишний", Assert.Single(report.Extra).Name);
    }

    /// <summary>
    /// ВЕРСИЮ НЕ ПРОЧИТАТЬ — ЭТО НЕ «СОВПАЛО». Молчание не подтверждение: пакет, у которого
    /// не удалось спросить версию, считается расхождением и называется.
    /// </summary>
    [Fact]
    public void Непрочитанная_версия_это_расхождение()
    {
        var expected = Parse("\"node_modules/a\":{\"version\":\"1.0.0\",\"integrity\":\"sha512-A\"}", declared: 2);

        var report = EngineTreeCheck.Against(
            expected,
            new[] { EnginePath, "node_modules/a" },
            path => string.Equals(path, EnginePath, StringComparison.OrdinalIgnoreCase) ? Probe : null);

        Assert.True(report.Checked);
        Assert.False(report.Ok);

        var problem = Assert.Single(report.Versions);
        Assert.Equal("a", problem.Name);
        Assert.Equal("1.0.0", problem.Expected);

        // «Что нашли» — путь пакета: версию прочитать не удалось, и это названо, а не спрятано.
        Assert.Equal("node_modules/a", problem.Actual);
    }

    /// <summary>
    /// СВЕРКА ИДЁТ ПО ПУТЯМ, А НЕ ПО ИМЕНАМ. Один и тот же пакет npm кладёт в разные места
    /// (`node_modules/a/node_modules/x` и `node_modules/x`): сверка по именам объявила бы законный
    /// вложенный пакет лишним и пропустила бы пропажу второго экземпляра.
    /// </summary>
    [Fact]
    public void Сверка_идёт_по_путям_а_не_по_именам()
    {
        var expected = Parse(
            "\"node_modules/a\":{\"version\":\"1.0.0\",\"integrity\":\"sha512-A\"}," +
            "\"node_modules/a/node_modules/x\":{\"version\":\"2.0.0\",\"integrity\":\"sha512-X\"}," +
            "\"node_modules/x\":{\"version\":\"3.0.0\",\"integrity\":\"sha512-Y\"}",
            declared: 4);

        var report = EngineTreeCheck.Against(
            expected,
            new[] { EnginePath, "node_modules/a", "node_modules/a/node_modules/x" },
            Versions(("node_modules/a", "1.0.0"), ("node_modules/a/node_modules/x", "2.0.0")));

        var missing = Assert.Single(report.Missing);
        Assert.Equal("x", missing.Name);
        Assert.Equal("node_modules/x", missing.Actual);
        Assert.Empty(report.Extra);
    }

    /// <summary>
    /// НЕОБЯЗАТЕЛЬНЫЕ НЕ СЧИТАЮТСЯ ПРОПАЖЕЙ, И ЭТО НАЗВАНО ЧИСЛОМ. Иначе честная установка
    /// на 588 путях выглядела бы 69 расхождениями (замер 10.10.2026), и настоящая пропажа
    /// потерялась бы среди них.
    /// </summary>
    [Fact]
    public void Необязательные_не_считаются_пропажей()
    {
        var expected = Parse(
            "\"node_modules/a\":{\"version\":\"1.0.0\",\"integrity\":\"sha512-A\"}," +
            "\"node_modules/darwin-only\":{\"version\":\"1.0.0\",\"integrity\":\"sha512-D\",\"optional\":true,\"os\":[\"darwin\"]}," +
            "\"node_modules/extra-one\":{\"version\":\"1.0.0\",\"integrity\":\"sha512-E\",\"extraneous\":true}",
            declared: 4);

        var report = EngineTreeCheck.Against(
            expected,
            new[] { EnginePath, "node_modules/a" },
            Versions(("node_modules/a", "1.0.0")));

        Assert.True(report.Ok);
        Assert.Equal(2, report.Skipped);
    }

    /// <summary>
    /// ПУСТОЙ ЗАМОК — «СВЕРЯТЬ НЕ С ЧЕМ», а не «всё сошлось». Иначе «замка нет» читалось бы как
    /// зелёная сверка, и панель ставила бы что угодно, называя это проверенным.
    /// </summary>
    [Fact]
    public void Без_замка_сверка_не_считается_пройденной()
    {
        var report = EngineTreeCheck.Against(EngineTreeLock.Empty, new[] { "node_modules/a" }, _ => "1.0.0");

        Assert.False(report.Checked);
        Assert.False(report.Ok);
        Assert.NotEmpty(report.Problems);
    }

    // ---- сборка замка для проверок -----------------------------------------------------------

    /// <summary>Путь движка: он есть в КАЖДОМ настоящем замке — иначе ставить нечего.</summary>
    private const string EnginePath = "node_modules/@deepseek-ai/dsh";

    /// <summary>Версия по пути: движок отвечает версией пробы, остальные — из названного списка.</summary>
    private static Func<string, string?> Versions(params (string Path, string Version)[] items) =>
        path => string.Equals(path, EnginePath, StringComparison.OrdinalIgnoreCase)
            ? Probe
            : items
                .Where(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Version)
                .FirstOrDefault();

    /// <summary>Замок из подставных записей: оболочка та же, что у настоящего файла.</summary>
    private static EngineTreeLock Parse(string entries, int declared) => EngineTreeLock.Parse(Mini(entries, declared));

    /// <summary>Текст подставного замка — тем же устройством, что у настоящего файла.</summary>
    private static string Mini(string entries, int declared) =>
        "{\"format\":2,\"engine\":{\"name\":\"" + Engine + "\",\"version\":\"" + Probe + "\"}," +
            "\"product\":{\"name\":\"dsh-panel-engine\",\"version\":\"0.0.0\"}," +
            "\"packages\":" + declared + "," +
            "\"lock\":{\"name\":\"dsh-panel-engine\",\"version\":\"0.0.0\",\"lockfileVersion\":3,\"requires\":true," +
            "\"packages\":{\"\":{\"name\":\"dsh-panel-engine\",\"version\":\"0.0.0\",\"dependencies\":{\"" + Engine + "\":\"" + Probe + "\"}}," +
            "\"node_modules/" + Engine + "\":{\"version\":\"" + Probe + "\",\"integrity\":\"sha512-ENGINE\"," +
            "\"resolved\":\"https://registry.npmjs.org/" + Engine + "/-/dsh-" + Probe + ".tgz\"}," +
            entries + "}}}";

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
}
