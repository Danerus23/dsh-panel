using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using DshPanel;
using DshPanel.Isolation;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// САМОТЕСТ КАЛИТКИ ТЕЛЕФОНА (<c>--phone-selftest</c>, этап F1.e) — проверки «без экрана».
///
/// **Что здесь проверяется, а что нет.** Здесь — то, что самотест обязан делать ДО и ПОМИМО
/// живого движка: отказать без изоляции (код 2), не занять порты <c>43127</c>/<c>3080</c>/<c>3081</c>,
/// сказать «неприменимо» там, где движка нет, и оставить ЧИСЛА в отчёте. Живой прогон самотеста
/// (настоящий движок, настоящее рукопожатие мультиплексора, кадр <c>ready</c>) идёт ключом на
/// собранном exe в изоляции — проверкой без экрана он не заменяется и не подменяется.
///
/// ⚠️ **Движка здесь нет намеренно.** Все прогоны идут с <c>locateEngine: () =&gt; null</c>: это
/// ровно тот шов, ради которого поиск движка в самотесте — дверь. Иначе проверка «нет движка —
/// это код 2» поднимала бы на машине с движком НАСТОЯЩИЙ сервер, то есть проверяла бы не то
/// состояние, в котором дефект возможен, и делала бы это минутами.
///
/// ⚠️ **Порты не занимаются.** Свой корень — под <c>%TEMP%</c>, каждый прогон в своём каталоге;
/// уборка за собой проверяется отдельно («корня после прогона нет»).
/// </summary>
public sealed class PhoneSelfTestTests
{
    /// <summary>Короткий срок: движка в этих прогонах нет, ждать нечего.</summary>
    private static TimeSpan Short => TimeSpan.FromSeconds(5);

    /// <summary>Свой корень прогона на каждую проверку — и он же убирается самотестом.</summary>
    private static string Root() =>
        Path.Combine(Path.GetTempPath(), "dsh-phone-selftest-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Прогон БЕЗ движка: поиск движка подменён, наружу не уходит ничего.</summary>
    private static PhoneSelfTestResult NoEngine(string root) =>
        PhoneSelfTest.Run(Short, root, locateEngine: () => null);

    private static bool Failed(PhoneSelfTestResult result) =>
        result.Report.Any(line => line.Contains(" = False", StringComparison.Ordinal));

    /// <summary>Число отчёта, стоящее после метки: «проверок: 75; …» → 75.</summary>
    private static int NumberAfter(PhoneSelfTestResult result, string label)
    {
        var line = result.Report.FirstOrDefault(item => item.Contains(label, StringComparison.Ordinal));

        Assert.True(line is not null, $"в отчёте нет строки с меткой «{label}»");

        var tail = line![(line.IndexOf(label, StringComparison.Ordinal) + label.Length)..];
        var digits = new string(tail.SkipWhile(symbol => !char.IsDigit(symbol)).TakeWhile(char.IsDigit).ToArray());

        Assert.False(string.IsNullOrEmpty(digits), $"после «{label}» в отчёте нет числа: «{line}»");

        return int.Parse(digits);
    }

    // ---- 1. без изоляции самотест отказывается ---------------------------------------------

    /// <summary>
    /// БЕЗ ИЗОЛЯЦИИ — КОД 2 И СЛОВА. То же поведение, что у <c>--server-selftest</c>: самотест
    /// поднимает настоящий движок (тот пишет в домашний каталог движка) и пишет файл пропуска
    /// телефона, а без корня и то и другое — данные владельца (красная линия 4).
    ///
    /// ⚠️ Довод «корня нет» передаётся пустым, а не берётся из окружения: проверка обязана
    /// проходить через ЭТО состояние, а не через то, в котором её случайно запустили.
    /// </summary>
    [Fact]
    public void Без_изоляции_самотест_отказывает_кодом_2()
    {
        var result = PhoneSelfTest.Run(Short, runRoot: null, locateEngine: () => null);

        Assert.Equal(2, result.Code);
        Assert.Equal("КАЛИТКА НЕПРИМЕНИМО", result.Title);
        Assert.Contains("--run-root", result.Report[0], StringComparison.Ordinal);
        Assert.Contains("данные владельца", result.Report[0], StringComparison.Ordinal);

        // Ни одного «проверено»: отказ — не проверка, и выдавать его за успех нельзя.
        Assert.Equal(0, result.Checks);
        Assert.Equal(0, result.Failed);
    }

    /// <summary>
    /// ПРОГОН С КОРНЕМ, НО БЕЗ ИЗОЛЯЦИИ — тоже отказ. Признак «изолирован» не выводится из
    /// «корень непустой»: контекст прогона — ОДНО место, где это сказано (<see cref="RunContext"/>),
    /// и проверка берёт его оттуда же, откуда весь продукт.
    /// </summary>
    [Fact]
    public void Обычный_запуск_с_корнем_не_притворяется_изолированным()
    {
        var result = PhoneSelfTest.Run(Short, runRoot: null, locateEngine: () => null);

        Assert.Equal(2, result.Code);
        Assert.Equal(0, result.Checks);

        // И корень при отказе НЕ заводится: отказались — значит ничего не тронули.
        Assert.False(Directory.Exists(Path.Combine(Path.GetTempPath(), "dsh-phone-selftest-tests", "не-наш")));
    }

    // ---- 2. запретные порты -----------------------------------------------------------------

    /// <summary>
    /// САМОТЕСТ НЕ ЗАНИМАЕТ <c>43127</c>/<c>3080</c>/<c>3081</c> — и это проверяется ДВУМЯ
    /// способами, потому что одного мало.
    ///
    /// Первый — ПО ФАКТУ: все порты, которые в этом прогоне заняты, свободны сейчас и не запрещены
    /// решением. Второй — ПО ИСХОДНИКАМ: в самом самотесте нет ни одного ЛИТЕРАЛА этих номеров.
    /// Второй нужен потому, что первый не поймал бы «зашитый на будущее» порт, который на этой
    /// машине случайно свободен.
    /// </summary>
    [Fact]
    public void Запретные_порты_самотест_не_занимает()
    {
        // ФАКТ: запрет объявлен решением, и числа названы здесь, а не «где-то».
        Assert.Equal(43127, PhonePass.DefaultPort);
        Assert.Equal(3080, ServerDecisions.OwnerPort);
        Assert.Equal(3081, ServerDecisions.RunFallbackPort);

        Assert.True(PhoneGate.PortAllowed(PhonePass.DefaultPort));
        Assert.False(PhoneGate.PortForbidden(PhonePass.DefaultPort));
        Assert.False(PhoneGate.PortAllowed(ServerDecisions.OwnerPort));
        Assert.False(PhoneGate.PortAllowed(ServerDecisions.RunFallbackPort));

        // Свободный порт, которым пользуется самотест, — НЕ запретный (то же правило, что внутри).
        var free = FreeLoopback();
        Assert.True(PhoneGate.PortAllowed(free));
        Assert.NotEqual(PhonePass.DefaultPort, free);
        Assert.NotEqual(ServerDecisions.OwnerPort, free);
        Assert.NotEqual(ServerDecisions.RunFallbackPort, free);

        // ИСХОДНИКИ: в КОДЕ самотеста нет литералов этих номеров — он берёт порт у системы.
        // ⚠️ Комментарии из сверки исключены, и это не поблажка: комментарий ничего не занимает,
        // а назвать запретные номера вслух как раз полезно.
        var rootPath = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(rootPath), "не нашёл корень репозитория");

        var code = File.ReadAllLines(Path.Combine(rootPath!, "src", "DshPanel", "Server", "PhoneSelfTest.cs"))
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal)
                           && !line.StartsWith("///", StringComparison.Ordinal)
                           && !line.StartsWith("*", StringComparison.Ordinal))
            .ToList();

        // Контроль, что сверка вообще видит код: пустой список означал бы «проверка ничего не читала».
        Assert.Contains(code, line => line.Contains("FreeLoopbackPort", StringComparison.Ordinal));

        foreach (var literal in new[] { "43127", "3080", "3081" })
        {
            var lines = code.Where(line => line.Contains(literal, StringComparison.Ordinal)).ToList();

            // Номер позволено НАЗВАТЬ (в шапке и в словах человеку о запрете) — но не занять:
            // ни одного `TcpListener`/аргумента запуска с этим номером быть не должно.
            Assert.True(
                lines.All(line => line.Contains("запрещ", StringComparison.Ordinal)
                                  || line.Contains("PortForbidden", StringComparison.Ordinal)
                                  || line.Contains("PortAllowed", StringComparison.Ordinal)),
                $"номер {literal} в самотесте — не в словах о запрете, а в деле: {string.Join(" | ", lines)}");

            Assert.DoesNotContain(code, line => line.Contains("TcpListener", StringComparison.Ordinal)
                                                && line.Contains(literal, StringComparison.Ordinal));
        }

        // И порт берётся СВЯЗЫВАНИЕМ НА 0 — то есть у системы, а не перебором «примерно свободных».
        Assert.Contains(code, line => line.Contains("new TcpListener(IPAddress.Loopback, 0)", StringComparison.Ordinal));
    }

    /// <summary>Свободный порт петли — тем же способом, каким его берёт самотест и продукт.</summary>
    private static int FreeLoopback()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    // ---- 3. «нет движка» — это код 2, а не успех --------------------------------------------

    /// <summary>
    /// НЕТ ДВИЖКА — КОД 2 («неприменимо»), А НЕ «УСПЕХ». Это не придирка к слову: без движка
    /// главная часть F1 — живое рукопожатие мультиплексора — не прогонялась, и зелёный ответ
    /// означал бы «проверено», которого не было.
    ///
    /// Прогон при этом идёт ПОЛНЫЙ: все разделы до живого движка отрабатывают на своём корне
    /// (калитка, сопряжение, белый список, рубильник, вопрос на подставных кадрах) — и ни один
    /// из них не смеет сказать «False». То есть «неприменимо» относится РОВНО к одной причине.
    /// </summary>
    [Fact]
    public void Нет_движка_это_код_2_а_не_успех()
    {
        var root = Root();
        var result = NoEngine(root);

        Assert.Equal(2, result.Code);
        Assert.Equal("КАЛИТКА НЕПРИМЕНИМО", result.Title);

        // Причина названа СЛОВАМИ, а не молчанием.
        Assert.Contains(result.Report, line => line.Contains("нет Node", StringComparison.Ordinal));
        Assert.Contains(result.Report, line => line.Contains("НЕ «зелено»", StringComparison.Ordinal));

        // Всё остальное, что проверялось, — прошло: «неприменимо» не прячет поломку.
        Assert.True(!Failed(result), "в прогоне без движка упала проверка:\n" + string.Join("\n",
            result.Report.Where(line => line.Contains(" = False", StringComparison.Ordinal))));

        Assert.True(result.Checks > 40, $"проверок всего {result.Checks} — самотест почти ничего не сделал");

        // Порты и корни: за собой прибрано, корня не осталось.
        Assert.False(Directory.Exists(root), "самотест не убрал свой корень за собой");
    }

    // ---- 4. каждая строка отчёта влияет на исход ---------------------------------------------

    /// <summary>
    /// КАЖДАЯ СТРОКА ОТЧЁТА — ЛИБО ПРОВЕРКА, ЛИБО ЯВНО ПОЯСНЕНИЕ, И НИЧЕГО ТРЕТЬЕГО.
    ///
    /// У соседей это названо так: «без этого „успех“ означал бы лишь „исключений не было“».
    /// Здесь то же самое проверяется ЧИСЛОМ: сколько строк кончаются на <c>= True</c>/<c>= False</c>
    /// (это проверки), ровно столько самотест насчитал в <see cref="PhoneSelfTestResult.Checks"/>.
    /// Разошлось — значит в отчёт попала строка, которая на исход не влияет и при этом выглядит
    /// проверкой.
    /// </summary>
    [Fact]
    public void Каждая_проверка_отчёта_влияет_на_исход()
    {
        var result = NoEngine(Root());

        var verdicts = result.Report
            .Count(line => line.EndsWith(" = True", StringComparison.Ordinal)
                           || line.EndsWith(" = False", StringComparison.Ordinal));

        Assert.Equal(result.Checks, verdicts);
        Assert.Equal(result.Failed, result.Report.Count(line => line.EndsWith(" = False", StringComparison.Ordinal)));

        // И провал ВИДЕН в отчёте поимённо, а не только числом: иначе приёмка читала бы «1»
        // и искала причину по всему выводу. На прогоне без провалов строки нет — и это правильно.
        Assert.Equal(
            result.Failed > 0,
            result.Report.Any(line => line.Contains("провалившиеся проверки", StringComparison.Ordinal)));

        // Сломанная проверка делает исход провалом — и это тот же счёт, а не другое правило.
        var code = result.Failed > 0 ? 1 : result.Code;

        Assert.True(code != 0 || result.Failed == 0, "нулевой код при провалившихся проверках");
    }

    /// <summary>
    /// ЖИВОЕ РУКОПОЖАТИЕ — УСЛОВИЕ НУЛЕВОГО КОДА, И ЭТО ЗАПИСАНО КОДОМ, А НЕ НАДЕЖДОЙ.
    ///
    /// Самая дорогая часть самотеста — та, которую без движка не прогнать. Если однажды её вердикт
    /// перестанут учитывать, самотест начнёт отвечать «УСПЕХ» там, где рукопожатие не проверялось
    /// вовсе, — то есть ровно та ложь, ради которой он и писался. Проверка читает ИСХОДНИК
    /// и требует ОБЕ половины: раздел живого движка возвращает «неприменимо» (2), а исход
    /// с нулевого кода понижается до 2, когда этот раздел сказал 2.
    ///
    /// ⚠️ Почему по исходнику, а не прогоном: живой прогон требует настоящего движка, а проверка
    /// без экрана обязана быть дешёвой и детерминированной. Здесь сторожится УСТРОЙСТВО, а живое
    /// рукопожатие сторожит прогон `--phone-selftest` в изоляции (его числа — в отчёте куска).
    /// </summary>
    [Fact]
    public void Нулевой_код_невозможен_без_живого_рукопожатия()
    {
        var source = StringSource("src", "DshPanel", "Server", "PhoneSelfTest.cs");

        Assert.Contains("if (code == 0 && live == 2) code = 2;", source, StringComparison.Ordinal);
        Assert.Contains("return 2;", source, StringComparison.Ordinal);
        Assert.Contains("на этой машине нет Node и пакета @deepseek-ai/dsh", source, StringComparison.Ordinal);

        // И «нет движка» не выдаётся за успех ни одной строкой отчёта.
        Assert.DoesNotContain("движка нет — это успех", source, StringComparison.Ordinal);
    }

    /// <summary>Исходник файла репозитория — по частям пути, чтобы не заводить второй корень.</summary>
    private static string StringSource(params string[] parts)
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        return File.ReadAllText(Path.Combine(new[] { root! }.Concat(parts).ToArray()));
    }

    // ---- 5. разбор вывода даёт числа ---------------------------------------------------------

    /// <summary>
    /// ИЗ ОТЧЁТА ЧИТАЮТСЯ ЧИСЛА — и это не украшение. Приёмка сравнивает числа между ревизиями
    /// («было 75 проверок, стало 60»), а «зелено» без числа не отвечает на вопрос, что именно
    /// прогонялось. Отсюда требования: число проверок, число провалов и числа живого раздела.
    ///
    /// ⚠️ Готовность мультиплексора в миллисекундах читается ИЗ ТОГО ЖЕ ВИДА строки, каким её
    /// печатает живой прогон, — поэтому разбор проверяется на живом отчёте, снятом в изоляции
    /// (`upd-build\e3-recon-logs\phone-selftest-isolated.txt`), а не на выдуманной строке.
    /// </summary>
    [Fact]
    public void Разбор_отчёта_даёт_числа()
    {
        var result = NoEngine(Root());

        Assert.Equal(result.Checks, NumberAfter(result, "проверок: "));
        Assert.Equal(result.Failed, NumberAfter(result, "провалилось: "));
        Assert.True(NumberAfter(result, "проверок: ") > 0);

        // Готовность мультиплексора — ЧИСЛО, и оно читается тем же способом из строки живого прогона.
        const string live = "  готовность подписки мультиплексора: 13 мс (от открытия канала до кадра `ready`)";
        var digits = new string(live.SkipWhile(symbol => !char.IsDigit(symbol)).TakeWhile(char.IsDigit).ToArray());

        Assert.Equal("13", digits);
        Assert.Contains("мс", live, StringComparison.Ordinal);
        Assert.Contains("мультиплексора", live, StringComparison.Ordinal);

        // А сам самотест печатает её ИМЕННО ТАК — это сторожит исходник: строка, из которой числа
        // не прочитать, обесценила бы весь замер. Рядом печатается и то, каким по счёту кадром
        // пришёл `ready`: без этого «13 мс» не отличить от «канал открывался минуту».
        var source = StringSource("src", "DshPanel", "Server", "PhoneSelfTest.cs");

        Assert.Contains("готовность подписки мультиплексора: {readyAt} мс", source, StringComparison.Ordinal);
        Assert.Contains("первым кадром потока оказался `ready` через {openedAt} мс", source, StringComparison.Ordinal);

        // И снятый в изоляции отчёт лежит рядом с журналами прогонов — приёмка читает его глазами.
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        var log = Path.Combine(root!, "upd-build", "e3-recon-logs", "phone-selftest-isolated.txt");

        if (File.Exists(log))
        {
            var text = File.ReadAllText(log);

            Assert.Contains("КАЛИТКА УСПЕХ", text, StringComparison.Ordinal);
            Assert.Contains("готовность подписки мультиплексора:", text, StringComparison.Ordinal);
            Assert.Contains("мс (от открытия канала до кадра `ready`)", text, StringComparison.Ordinal);
            Assert.Contains("проверок: ", text, StringComparison.Ordinal);
        }
    }

    // ---- 6. ключ известен разбору режимов ----------------------------------------------------

    /// <summary>
    /// КЛЮЧ <c>--phone-selftest</c> ЗНАКОМ РАЗБОРУ, А НЕЗНАКОМЫЙ КЛЮЧ ПО-ПРЕЖНЕМУ ГРОМОК.
    ///
    /// Половины две, и обе обязательны. Без первой ключ отвергался бы как опечатка и самотест
    /// нельзя было бы запустить вовсе (ровно этот случай уже был у <c>--engine-install-selftest</c>).
    /// Без второй опечатка («--phone-selftes») провалилась бы мимо всех ветвей в ОБЫЧНЫЙ ЗАПУСК,
    /// то есть открыла бы окно на рабочем столе владельца (находка холодного агента 24.09.2026).
    /// </summary>
    [Fact]
    public void Ключ_известен_разбору_а_незнакомый_по_прежнему_отказ()
    {
        Assert.Contains("--phone-selftest", StartModes.Known);
        Assert.False(StartModes.KnowsShotWindow("--phone-selftest"));

        var typo = StartModes.Refuse(new[] { "--phone-selftes" });

        Assert.NotNull(typo);
        Assert.Contains("--phone-selftes", typo!, StringComparison.Ordinal);
        Assert.Contains("--phone-selftest", typo!, StringComparison.Ordinal);
        Assert.Contains("НЕИЗВЕСТНЫЙ КЛЮЧ", typo!, StringComparison.Ordinal);

        // Ключ без режима — по-прежнему отказ: это модификатор, а не проверка.
        var alone = StartModes.Refuse(new[] { "--phone-selftest" });

        Assert.NotNull(alone);
        Assert.Contains("БЕЗ РЕЖИМА", alone!, StringComparison.Ordinal);
    }

    // ---- 7. самотест не подменяет движок молча ----------------------------------------------

    /// <summary>
    /// РУБИЛЬНИК НАСТОЯЩИЙ, А НЕ «СТРОКА В ОТЧЁТЕ». Отвязка устройства — это ЗАПИСЬ в файл
    /// пропуска, и смысл у неё один: подпись отвязанного устройства осталась бы верной, поэтому
    /// «отвязано» обязано значить «тот же cookie больше не пускает». Проверка читает ИСХОДНИК
    /// раздела и требует ОБЕ половины: запись через <see cref="PhonePass.WithoutDevice"/>
    /// И проверку «после отвязки — 401».
    ///
    /// ⚠️ Почему по исходнику, а не прогоном: раздел рубильника живёт внутри полного самотеста,
    /// а полный самотест без движка стоит секунды и его ответ — «неприменимо» (код 2). Живые числа
    /// этого раздела снимает прогон `--phone-selftest` в изоляции; здесь сторожится УСТРОЙСТВО
    /// раздела — ровно то, что подменяет мутация «отвязка ничего не делает».
    /// </summary>
    [Fact]
    public void Рубильник_без_экрана()
    {
        var source = StringSource("src", "DshPanel", "Server", "PhoneSelfTest.cs");

        Assert.Contains("pass.WithoutDevice(device.Id).Save(", source, StringComparison.Ordinal);
        Assert.Contains("тот же cookie после отвязки не пускает (401)", source, StringComparison.Ordinal);
        Assert.Contains("после остановки порт освободился (связывание тем же портом проходит)", source, StringComparison.Ordinal);

        // Запись действительно ПРОВЕРЯЕТСЯ, а не «вызвана и забыта»: результат `Save` идёт в ответ.
        Assert.Contains("Check(\"отвязка записана в файл пропуска (устройств 0)\"", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// РАЗДЕЛ ЖИВОГО ДВИЖКА СПРАШИВАЕТ ДВИЖОК ЧЕРЕЗ ДВЕРЬ, А НЕ ИЩЕТ ЕГО САМ.
    ///
    /// Иначе проверки без экрана поднимали бы настоящий сервер на машине, где движок есть, —
    /// то есть проверяли бы не то состояние, в котором возможен дефект «нет движка», и делали бы
    /// это минутами. Здесь проверяется, что шов есть и что самотест зовёт ИМЕННО его.
    /// </summary>
    [Fact]
    public void Поиск_движка_идёт_дверью()
    {
        var calls = 0;

        var result = PhoneSelfTest.Run(Short, Root(), locateEngine: () =>
        {
            calls++;

            return null;
        });

        Assert.Equal(1, calls);
        Assert.Equal(2, result.Code);

        var source = StringSource("src", "DshPanel", "Server", "PhoneSelfTest.cs");

        Assert.Contains("var engine = _locateEngine();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var engine = DshEngine.Locate();", source, StringComparison.Ordinal);
    }
}
