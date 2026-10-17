using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ФАЙЛ ПОДСКАЗОК О ВЕРСИЯХ ДВИЖКА (этап E2, шаг 5).
///
/// Решение владельца: подсказка лежит **файлом в публичном репозитории**, а не в панели, и
/// обновляется **одним коммитом без выпуска панели**. Из этого следуют два свойства, и оба
/// здесь проверяются прогоном:
///
/// 1. **Недоступный файл ничего не роняет** — «не проверялась» это честный ответ, а не поломка;
/// 2. **Файл — подсказка, а не источник правды**: ни одна строка из него не становится вердиктом
///    об умениях. Умения панель проверяет сама — пробой.
///
/// ⚠️ **Заметки живут на трёх языках В ФАЙЛЕ.** Перевод новой заметки не может ждать нового
/// выпуска панели: он приходит вместе с заметкой. Проверяется и выбор языка, и запасной путь.
/// </summary>
public sealed class EngineHintsTests
{
    private const string Real = """
        {
          "versions": [
            {
              "version": "0.1.5-rc.2",
              "verified": true,
              "notes": {
                "ru": ["Панель проверена целиком: 8 умений из 8."],
                "en": ["The panel was verified in full: 8 skills of 8."],
                "zh": ["面板已完整验证：8 项能力中的 8 项。"]
              },
              "troubles": {
                "ru": ["Ответ на вопрос агента проверен только на открытие канала."],
                "en": ["The agent question answer is verified only for opening the channel."],
                "zh": ["代理提问的应答仅验证了通道打开。"]
              }
            },
            {
              "version": "0.1.7-rc.2",
              "verified": false,
              "notes": { "ru": ["Ещё не проверялась."] }
            }
          ]
        }
        """;

    private static EngineHints Parsed() => EngineHints.Parse(Real);

    // --- РАЗБОР ФАЙЛА ---------------------------------------------------------------------

    /// <summary>Настоящий файл разбирается: версия, отметка «проверена» и три языка заметок.</summary>
    [Fact]
    public void Настоящий_файл_разбирается()
    {
        var hints = Parsed();

        Assert.True(hints.Any);
        Assert.Equal(2, hints.Versions.Count);

        var mine = hints.Find("0.1.5-rc.2");

        Assert.NotNull(mine);
        Assert.True(mine!.Verified);
        Assert.Equal(3, mine.Notes.Count);
        Assert.Equal(3, mine.Troubles.Count);

        // И строка заметки — ровно то, что лежит в файле, без обрамления и выдумок.
        Assert.Equal(
            "Панель проверена целиком: 8 умений из 8.",
            Assert.Single(EngineHints.NotesFor(mine, "ru")));
    }

    /// <summary>
    /// БИТЫЙ ФАЙЛ — ЭТО «ПОДСКАЗОК НЕТ», А НЕ ПАДЕНИЕ. Файл правят руками в публичном
    /// репозитории, поэтому сломать его может кто угодно и когда угодно; панель, которая от этого
    /// не запускается, — это панель, которую человек больше не запустит.
    /// </summary>
    [Theory]
    [InlineData("{ это не json")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>файл переехал</html>")]
    [InlineData("[]")]
    [InlineData("{\"versions\":{}}")]
    [InlineData("{\"versions\":[]}")]
    [InlineData("{\"versions\":[{\"verified\":true}]}")]
    [InlineData("{\"versions\":[{\"version\":\"\"}]}")]
    [InlineData("{\"versions\":[\"0.1.5-rc.2\"]}")]
    public void Битый_файл_это_отсутствие_подсказок_а_не_падение(string json)
    {
        var hints = EngineHints.Parse(json);

        Assert.False(hints.Any);
        Assert.Empty(hints.Versions);
    }

    /// <summary>
    /// КРИВАЯ ЗАМЕТКА НЕ УБИВАЕТ ЗАПИСЬ О ВЕРСИИ: версия остаётся известной, а строк заметок нет.
    /// Это тоже терпимость, и она намеренная: сломать одну заметку в публичном файле не должно
    /// значить «про эту версию панель ничего не знает».
    /// </summary>
    [Theory]
    [InlineData("{\"versions\":[{\"version\":\"0.1.5-rc.2\",\"notes\":[1,2]}]}")]
    [InlineData("{\"versions\":[{\"version\":\"0.1.5-rc.2\",\"notes\":\"строка вместо списка\"}]}")]
    [InlineData("{\"versions\":[{\"version\":\"0.1.5-rc.2\",\"notes\":{\"ru\":{}}}]}")]
    [InlineData("{\"versions\":[{\"version\":\"0.1.5-rc.2\",\"troubles\":7}]}")]
    public void Кривая_заметка_не_убивает_запись_о_версии(string json)
    {
        var hints = EngineHints.Parse(json);
        var hint = hints.Find("0.1.5-rc.2");

        Assert.NotNull(hint);
        Assert.Empty(EngineHints.NotesFor(hint, "ru"));
        Assert.Empty(EngineHints.TroublesFor(hint, "ru"));
    }

    /// <summary>Пустое место в заметках не становится строкой: строки «—» в отчёте быть не должно.</summary>
    [Fact]
    public void Пустые_заметки_не_становятся_строками()
    {
        var hints = EngineHints.Parse(
            "{\"versions\":[{\"version\":\"1.0.0\",\"notes\":{\"ru\":[\"\",\"  \"]},\"troubles\":{\"ru\":[]}}]}");

        var hint = hints.Find("1.0.0");

        Assert.NotNull(hint);
        Assert.Empty(EngineHints.NotesFor(hint, "ru"));
        Assert.Empty(EngineHints.TroublesFor(hint, "ru"));
    }

    // --- СХЕМА ФАЙЛА ----------------------------------------------------------------------

    /// <summary>
    /// ЧУЖАЯ СХЕМА НЕ УГАДЫВАЕТСЯ. Файл правят одним коммитом без выпуска панели, поэтому завтра
    /// в нём могут появиться поля, смысла которых эта панель не знает: тогда честный ответ —
    /// «подсказок нет», а не догадка по знакомым именам. Поля `format` в первой редакции не было —
    /// такой файл панель читает как свой.
    /// </summary>
    [Theory]
    [InlineData("{\"format\":1,\"versions\":[{\"version\":\"1.0.0\"}]}", true)]
    [InlineData("{\"versions\":[{\"version\":\"1.0.0\"}]}", true)]
    [InlineData("{\"format\":2,\"versions\":[{\"version\":\"1.0.0\"}]}", false)]
    [InlineData("{\"format\":0,\"versions\":[{\"version\":\"1.0.0\"}]}", false)]
    [InlineData("{\"format\":\"1\",\"versions\":[{\"version\":\"1.0.0\"}]}", false)]
    [InlineData("{\"format\":1.5,\"versions\":[{\"version\":\"1.0.0\"}]}", false)]
    [InlineData("{\"format\":null,\"versions\":[{\"version\":\"1.0.0\"}]}", false)]
    public void Чужая_схема_файла_не_угадывается(string json, bool ours)
    {
        var hints = EngineHints.Parse(json);

        Assert.Equal(ours, hints.Any);
    }

    /// <summary>Схема объявлена и разбор её знает: номер живёт ОДНИМ числом в коде.</summary>
    [Fact]
    public void Номер_схемы_один_и_наша_схема_читается()
    {
        Assert.Equal(1, EngineHints.Format);
        Assert.True(EngineHints.Parse($"{{\"format\":{EngineHints.Format},\"versions\":[{{\"version\":\"1.0.0\"}}]}}").Any);
    }

    // --- ВЕРСИЯ ---------------------------------------------------------------------------

    /// <summary>
    /// ВЕРСИЯ СВЕРЯЕТСЯ ПРАВИЛОМ ПАНЕЛИ (<see cref="DshPanel.Update.UpdateDecisions.Numeric"/>):
    /// ведущая `v` и хвост сборки `+хеш` отбрасываются — это и есть «числовая часть».
    /// </summary>
    [Theory]
    [InlineData("0.1.5-rc.2")]
    [InlineData("v0.1.5-rc.2")]
    [InlineData("0.1.5-rc.2+481fb6b")]
    [InlineData(" 0.1.5-rc.2 ")]
    public void Версия_сверяется_правилом_панели(string version)
    {
        Assert.NotNull(Parsed().Find(version));
    }

    /// <summary>
    /// ⚠️ ПРЕДРЕЛИЗНЫЙ ХВОСТ ЗНАЧИМ, и это осознанно: запись про `0.1.5-rc.2` НЕ выдаётся за
    /// финальный `0.1.5`. Иначе подсказка про предрелиз молча приклеилась бы к финальному выпуску —
    /// то есть панель сказала бы «проверена» про версию, которую не проверяла.
    /// </summary>
    [Theory]
    [InlineData("0.1.5")]
    [InlineData("0.1.5+сборка")]
    [InlineData("0.1.5-rc.3")]
    public void Предрелиз_не_выдаётся_за_финальный_выпуск(string version)
    {
        Assert.Null(Parsed().Find(version));
    }

    /// <summary>
    /// ЧУЖАЯ ВЕРСИЯ НЕ ВЫДАЁТСЯ ЗА НАШУ: нет записи про эту версию — нет ни заметок, ни бед.
    /// Это честное «про эту версию мы ничего не знаем».
    /// </summary>
    [Theory]
    [InlineData("9.9.9")]
    [InlineData("0.1.6")]
    [InlineData("0.1.50")]
    [InlineData("")]
    [InlineData(null)]
    public void Чужая_версия_не_выдаётся_за_нашу(string? version)
    {
        var hints = Parsed();
        var hint = hints.Find(version);

        Assert.Null(hint);
        Assert.Empty(EngineHints.NotesFor(hint, "ru"));
        Assert.Empty(EngineHints.TroublesFor(hint, "ru"));
    }

    /// <summary>Список проверенных версий называет только те, где стоит «проверена».</summary>
    [Fact]
    public void Список_проверенных_версий_называет_только_проверенные()
    {
        var list = Parsed().VerifiedList();

        Assert.Contains("0.1.5-rc.2", list, StringComparison.Ordinal);
        Assert.DoesNotContain("0.1.7-rc.2", list, StringComparison.Ordinal);
    }

    /// <summary>Подсказок нет — и списка нет: пустая строка честнее выдуманного номера.</summary>
    [Fact]
    public void Без_подсказок_список_версий_пуст()
    {
        Assert.Equal(string.Empty, EngineHints.Empty.VerifiedList());
        Assert.Equal(string.Empty, EngineHints.Parse("{}").VerifiedList());
    }

    // --- ЯЗЫК -----------------------------------------------------------------------------

    /// <summary>
    /// ЗАМЕТКА ПРИХОДИТ НА ЯЗЫКЕ ПАНЕЛИ; нет перевода — берём русский (язык разработки), потом
    /// любой: пустая подсказка хуже неточной, но выдумывать перевод панель не станет.
    /// </summary>
    [Theory]
    [InlineData("ru", "Панель проверена")]
    [InlineData("en", "verified in full")]
    [InlineData("zh", "面板已完整验证")]
    [InlineData("RU", "Панель проверена")]
    [InlineData("de", "Панель проверена")]
    [InlineData("", "Панель проверена")]
    public void Заметка_приходит_на_языке_панели(string language, string expected)
    {
        var notes = EngineHints.NotesFor(Parsed().Find("0.1.5-rc.2"), language);

        Assert.Contains(expected, Assert.Single(notes), StringComparison.Ordinal);
    }

    /// <summary>
    /// ПЕРЕВОДА НЕТ НИ НА ОДНОМ ОЖИДАЕМОМ ЯЗЫКЕ — берём тот, что есть, а не пустое место:
    /// строка «известна беда» без самой беды бесполезна.
    /// </summary>
    [Fact]
    public void Единственный_чужой_язык_всё_равно_отдаётся()
    {
        var hints = EngineHints.Parse("{\"versions\":[{\"version\":\"1.0.0\",\"notes\":{\"fr\":[\"Note en français.\"]}}]}");

        Assert.Equal("Note en français.", Assert.Single(EngineHints.NotesFor(hints.Find("1.0.0"), "ru")));
    }

    /// <summary>Бед у версии нет — и строки нет: выдуманной беды в отчёте быть не должно.</summary>
    [Fact]
    public void Без_бед_строки_бед_нет()
    {
        var hints = EngineHints.Parse("{\"versions\":[{\"version\":\"1.0.0\",\"notes\":{\"ru\":[\"Заметка.\"]}}]}");

        Assert.Empty(EngineHints.TroublesFor(hints.Find("1.0.0"), "ru"));
        Assert.Single(EngineHints.NotesFor(hints.Find("1.0.0"), "ru"));
    }

    // --- ЧТЕНИЕ ИЗ СЕТИ -------------------------------------------------------------------

    /// <summary>
    /// НЕДОСТУПНЫЙ ФАЙЛ — ЭТО ОТВЕТ, А НАРУЖУ НИ ОДНОГО ИСКЛЮЧЕНИЯ. Проверка обновлений уже
    /// научена этому же (битый JSON там — отказ), и подсказка не имеет права быть строже.
    /// </summary>
    [Fact]
    public void Недоступный_файл_не_бросает()
    {
        var broken = new HttpEngineHintsClient("https://example.invalid/engine-hints.json", new HttpClient(new Broken()));

        var answer = broken.Read();

        Assert.False(answer.Ok);
        Assert.Equal(string.Empty, answer.Json);
        Assert.Contains("HttpRequestException", answer.Detail, StringComparison.Ordinal);
    }

    /// <summary>Отказ сервера (403 без User-Agent — обычное дело на GitHub) — тоже отказ, а не падение.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void Отказ_сервера_называется_кодом(HttpStatusCode code)
    {
        var answer = new HttpEngineHintsClient(
            "https://example.invalid/engine-hints.json",
            new HttpClient(new Status(code))).Read();

        Assert.False(answer.Ok);
        Assert.Contains(((int)code).ToString(System.Globalization.CultureInfo.InvariantCulture), answer.Detail, StringComparison.Ordinal);
    }

    /// <summary>Адрес не задан — в сеть не ходим вовсе и говорим почему.</summary>
    [Fact]
    public void Без_адреса_в_сеть_не_ходим()
    {
        var handler = new Status(HttpStatusCode.OK, Real);

        var answer = new HttpEngineHintsClient("  ", new HttpClient(handler)).Read();

        Assert.False(answer.Ok);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>
    /// ФАЙЛ ПРИШЁЛ — И ОН ЖЕ УХОДИТ В РАЗБОР: клиент отдаёт текст, а не своё мнение о нём.
    /// Так живость читается одним прогоном, а не двумя разными разборами.
    /// </summary>
    [Fact]
    public void Прочитанный_файл_разбирается_тем_же_разбором()
    {
        var handler = new Status(HttpStatusCode.OK, Real);

        var answer = new HttpEngineHintsClient("https://example.invalid/engine-hints.json", new HttpClient(handler)).Read();

        Assert.True(answer.Ok);
        Assert.Equal(1, handler.Calls);

        // И User-Agent уходит: без него GitHub отвечает 403 (проверено на живой сети в обновлении).
        Assert.Contains(ProductLinks.UserAgent, handler.UserAgents);

        Assert.True(EngineHints.Parse(answer.Json).Any);
    }

    // --- ОТЧЁТ ----------------------------------------------------------------------------

    private static IReadOnlyList<SkillVerdict> Verdicts() =>
        EngineSkills.Measure(EngineSkillProbe.None(AppPaths.Under(@"C:\Проба\панель")));

    private static EnvironmentReport Report(EngineHints? hints) => EnvironmentReport.Build(
        engine: null,
        dshVersion: "0.1.5-rc.2",
        nodeVersion: string.Empty,
        npmVersion: string.Empty,
        pnpmVersion: string.Empty,
        paths: AppPaths.Under(@"C:\Проба\панель"),
        workDir: @"C:\Проба\работа",
        workDirConfigured: true,
        serverPort: 3081,
        portState: PanelStrings.EnvPortFree,
        skills: Verdicts(),
        hints: hints);

    private static EnvironmentGroup Skills(EnvironmentReport report) =>
        Assert.Single(report.Groups, g => g.Title == Loc.T(SkillKeys.Title));

    /// <summary>
    /// ПОДСКАЗКИ ПРО НАШУ ВЕРСИЮ ПОПАДАЮТ В ТУ ЖЕ ТАБЛИЦУ, что и умения: новых окон не заведено,
    /// и заметка о версии читается там, где человек и так смотрит умения.
    ///
    /// ⚠️ В файле нарочно **две** записи, и проверенная — ЧУЖАЯ нам версия: так видно, что список
    /// проверенных приходит ИЗ ФАЙЛА, а не совпадает случайно с нашей же версией движка.
    /// </summary>
    [Fact]
    public void Заметки_про_нашу_версию_попадают_в_таблицу()
    {
        var hints = EngineHints.Parse(
            """
            {"versions":[
              {"version":"0.1.4","verified":true},
              {"version":"0.1.5-rc.2","verified":false,
               "notes":{"ru":["Заметка про нашу версию."]},
               "troubles":{"ru":["Беда про нашу версию."]}}]}
            """);

        var group = Skills(Report(hints));

        // Умения, сводка, строка версий, заметка и беда — пять сверх умений.
        Assert.Equal(Verdicts().Count + 4, group.Lines.Count);

        var notes = EngineHints.NotesFor(hints.Find("0.1.5-rc.2"), Loc.Language);
        var troubles = EngineHints.TroublesFor(hints.Find("0.1.5-rc.2"), Loc.Language);

        Assert.Contains(group.Lines, line => line.Text().Contains(PanelStrings.EnvSkillNotes, StringComparison.Ordinal));
        Assert.Contains(group.Lines, line => line.Text().Contains(PanelStrings.EnvSkillTroubles, StringComparison.Ordinal));
        Assert.Contains(group.Lines, line => line.Text().Contains(notes.Single(), StringComparison.Ordinal));
        Assert.Contains(group.Lines, line => line.Text().Contains(troubles.Single(), StringComparison.Ordinal));

        // Список проверенных версий — из файла: 0.1.4 взять больше негде.
        Assert.Contains(group.Lines, line => line.Text().Contains("0.1.4", StringComparison.Ordinal));
    }

    /// <summary>
    /// НЕТ ПОДСКАЗОК ПРО НАШУ ВЕРСИЮ — НЕТ И СТРОК О НЕЙ. Пустых строк и строк-заголовков без
    /// содержимого в отчёте быть не должно: человек читает их как поломку.
    /// </summary>
    [Fact]
    public void Без_подсказок_про_нашу_версию_строк_о_них_нет()
    {
        var group = Skills(Report(EngineHints.Parse("{\"versions\":[{\"version\":\"0.1.7-rc.2\",\"verified\":true}]}")));

        Assert.Equal(Verdicts().Count + 2, group.Lines.Count);
        Assert.DoesNotContain(group.Lines, line => line.Text().Contains(PanelStrings.EnvSkillNotes, StringComparison.Ordinal));
        Assert.DoesNotContain(group.Lines, line => line.Text().Contains(PanelStrings.EnvSkillTroubles, StringComparison.Ordinal));
    }

    /// <summary>
    /// ФАЙЛА НЕТ ВОВСЕ — ТАБЛИЦА УМЕНИЙ ОСТАЁТСЯ И ГОВОРИТ «НЕ ПРОВЕРЯЛАСЬ». Это главное правило
    /// шага: подсказка не имеет права ни убрать таблицу, ни превратить «не проверялась» в беду.
    /// </summary>
    [Fact]
    public void Без_файла_таблица_умений_на_месте()
    {
        var group = Skills(Report(null));

        Assert.Equal(Verdicts().Count + 2, group.Lines.Count);
        Assert.Contains(group.Lines, line => line.Text().Contains(Loc.T(SkillKeys.NotChecked), StringComparison.Ordinal));
        Assert.DoesNotContain(group.Lines, line => line.Text().Contains(PanelStrings.EnvSkillNotes, StringComparison.Ordinal));
    }

    /// <summary>
    /// ПОДСКАЗКА — НЕ ВЕРДИКТ: из файла с «проверена: true» ни одно умение «проверенным» не
    /// становится. Иначе панель поверила бы чужому файлу на слово там, где обязана спрашивать
    /// движок сама — а файл правит кто угодно и когда угодно.
    /// </summary>
    [Fact]
    public void Подсказка_не_подменяет_собственную_пробу()
    {
        var verdicts = Verdicts();

        Assert.Equal(0, EngineSkills.Confirmed(verdicts));
        Assert.Equal(3, EngineSkills.Unchecked(verdicts));

        // И в отчёте с подсказками «проверена» не появляется ни у одного умения. Строка умения
        // начинается с его имени (`{имя}: …`), поэтому ищем именно её: заметка о версии тоже
        // содержит слово «проверена», и по одному слову строки не различить.
        var group = Skills(Report(Parsed()));
        var names = EngineSkills.All.Select(skill => Loc.T(SkillKeys.Name(skill))).ToList();
        var skillLines = group.Lines
            .Where(line => names.Any(name => line.Text().StartsWith(name + ":", StringComparison.Ordinal)))
            .ToList();

        Assert.Equal(names.Count, skillLines.Count);
        Assert.DoesNotContain(skillLines, line => line.Text().Contains(Loc.T(SkillKeys.Checked), StringComparison.Ordinal));
    }

    // --- НАШ СОБСТВЕННЫЙ ФАЙЛ -------------------------------------------------------------

    /// <summary>
    /// ФАЙЛ, КОТОРЫЙ МЫ ПУБЛИКУЕМ, ОБЯЗАН РАЗБИРАТЬСЯ, ОБЪЯВЛЯТЬ СХЕМУ И ГОВОРИТЬ НА ТРЁХ ЯЗЫКАХ.
    /// Он живёт в дереве репозитория (`engine-hints.json`, корень) и уходит в публичный репозиторий;
    /// правят его одним коммитом БЕЗ выпуска панели, поэтому поймать в нём опечатку больше нечем —
    /// только этой проверкой до коммита.
    ///
    /// ⚠️ Проверяются ТРИ свойства, и все три — обещания владельцу: файл объявляет **версию схемы**
    /// (`format`), говорит про ту версию движка, на которой панель мерилась
    /// (`EngineRpcSurface.MeasuredOnVersion`), и у КАЖДОЙ заметки есть ru, en и zh: заметка
    /// на одном языке читалась бы двумя третями людей как пустое место.
    /// </summary>
    [Fact]
    public void Наш_собственный_файл_подсказок_разбирается_и_трёхъязычен()
    {
        var path = Path.Combine(RepositoryRoot(), "engine-hints.json");

        Assert.True(File.Exists(path), $"нет файла подсказок: {path}");

        var text = File.ReadAllText(path);
        var hints = EngineHints.Parse(text);

        Assert.True(hints.Any, "файл подсказок не разобрался или пуст");
        Assert.Contains($"\"format\": {EngineHints.Format}", text, StringComparison.Ordinal);

        var mine = hints.Find(EngineRpcSurface.MeasuredOnVersion);

        Assert.True(mine is not null, $"в файле нет записи про версию {EngineRpcSurface.MeasuredOnVersion}");
        Assert.True(mine!.Verified, "наша версия движка не отмечена проверенной");

        // ⚠️ ТРИ ЯЗЫКА — У КАЖДОЙ ЗАПИСИ, а не только у нашей: файл читают люди на трёх языках,
        // и запись про чужую (непроверенную) версию — такая же подсказка, как про свою.
        foreach (var hint in hints.Versions)
        {
            foreach (var languages in new[] { hint.Notes, hint.Troubles })
            {
                foreach (var language in new[] { "ru", "en", "zh" })
                {
                    Assert.True(
                        languages.TryGetValue(language, out var lines) && lines.Count > 0,
                        $"у версии «{hint.Version}» нет строк на языке «{language}»");
                }
            }
        }

        // Непроверенная версия обязана быть названа непроверенной: файл — не реклама.
        Assert.All(hints.Versions, hint => Assert.Equal(hint.Version == EngineRpcSurface.MeasuredOnVersion, hint.Verified));
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

        Assert.Fail($"не нашёл корень репозитория: вверх от «{AppContext.BaseDirectory}» нет файла DshPanel.slnx");

        return string.Empty;
    }

    // --- ПОДСТАВНОЕ СОЕДИНЕНИЕ ------------------------------------------------------------

    /// <summary>Подставной обработчик: отдаёт заданный код и тело, считая запросы.</summary>
    private sealed class Status : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;

        public Status(HttpStatusCode code, string body = "")
        {
            _code = code;
            _body = body;
        }

        public int Calls { get; private set; }

        public List<string> UserAgents { get; } = new();

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            if (request.Headers.TryGetValues("User-Agent", out var agents)) UserAgents.AddRange(agents);

            return new HttpResponseMessage(_code)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    /// <summary>Подставной обработчик, который обрывает связь: так выглядит ошибка сети.</summary>
    private sealed class Broken : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("сеть недоступна");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("сеть недоступна");
    }
}
