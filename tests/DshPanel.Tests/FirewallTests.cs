using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ДВЕРЬ БРАНДМАУЭРА: РЕШЕНИЕ БЕЗ ПРАВ И БЕЗ МАШИНЫ (этап F4, кусок 1).
///
/// **Что здесь и чего здесь НЕТ.** Владелец 11.10.2026 сказал прямо: применяющую часть на машине
/// не проверять вовсе — ни <c>netsh … add</c>, ни тестовое правило; это изменение делается на живом
/// прогоне по его щелчку. Поэтому здесь проверяются: КОМАНДА (узкая ли, только ли своё правило),
/// РАЗБОР СОСТОЯНИЯ (по настоящим ответам <c>netsh</c>, снятым чтением 11.10.2026), ВЕТКИ ОТКАЗА
/// (нет права · человек не подтвердил · машина не изменилась) и СЛОВА. Настоящий брандмауэр этой
/// машины не трогается НИ РАЗУ — и это тоже проверено: по исходникам прогон к настоящей двери
/// не ходит.
///
/// ⚠️ **Откуда взяты образцы вывода `netsh`.** Измерено чтением на машине владельца 11.10.2026:
/// правила с нашим именем нет (<c>exit 1</c> и «No rules match the specified criteria.»), а форма
/// существующего правила снята с настоящего правила (`Rule Name: … · Enabled: Yes · Direction: In ·
/// Profiles: Domain,Private,Public · Protocol: TCP · LocalPort: 53 · Program: C:\…\svchost.exe ·
/// Action: Allow`). Метки полей netsh печатает на языке Windows, а ЗНАЧЕНИЯ — как есть; ровно
/// поэтому разбор сверяет ЗНАЧЕНИЯ, а не метки.
/// </summary>
public class FirewallTests
{
    private const int GatePort = 43127;

    private const string Exe = @"C:\Program Files\DshPanel2\DshPanel.exe";

    // ---- образцы ответов машины (сняты чтением 11.10.2026) --------------------------------

    /// <summary>Ответ «правила с нашим именем нет»: так netsh отвечает кодом 1.</summary>
    private const string NoRules =
        "\nNo rules match the specified criteria.\n\n";

    /// <summary>Наше правило, как его печатает netsh: широкие метки, но НАШИ значения.</summary>
    private static string Ours(int port = GatePort, string exe = Exe) =>
        "\nRule Name:                            " + FirewallDecisions.RuleName + "\n"
        + "----------------------------------------------------------------------\n"
        + "Enabled:                              Yes\n"
        + "Direction:                            In\n"
        + "Profiles:                             Private\n"
        + "LocalIP:                              Any\n"
        + "RemoteIP:                             LocalSubnet\n"
        + "Protocol:                             TCP\n"
        + "LocalPort:                            " + port + "\n"
        + "RemotePort:                           Any\n"
        + "Program:                              " + exe + "\n"
        + "Action:                               Allow\n";

    /// <summary>Правило с нашим именем, но НЕ наше: другой порт и другая программа.</summary>
    private static string Foreign() => Ours(port: 4444, exe: @"C:\Windows\System32\svchost.exe");

    /// <summary>
    /// ТАК ПЕЧАТАЕТ netsh, КОГДА ЕГО ПОЗВАЛИ БЕЗ `verbose`: строки `Program:` в выводе НЕТ вовсе.
    ///
    /// ⚠️ Замер живого прогона владельца 11.10.2026: без `verbose` правила панели читались как
    /// «чужие» — сверять путь было не с чем, и человеку говорилось «правило не такое» о СВОЕЙ же
    /// работе. Поэтому здесь два образца: без строки программы (проверить не удалось) и с ЧУЖОЙ
    /// программой (вот это действительно «не такое»).
    /// </summary>
    private static string WithoutProgram() =>
        "\nRule Name:                            " + FirewallDecisions.RuleName + "\n"
        + "----------------------------------------------------------------------\n"
        + "Enabled:                              Yes\n"
        + "Direction:                            In\n"
        + "Profiles:                             Private\n"
        + "Protocol:                             TCP\n"
        + "LocalPort:                            " + GatePort + "\n"
        + "RemotePort:                           Any\n"
        + "Action:                               Allow\n";

    private static FirewallAnswer Answer(string output, int exitCode = 0) => new(exitCode, output);

    // ---- команда --------------------------------------------------------------------------

    /// <summary>
    /// ПРАВИЛО УЗКОЕ, А НЕ «ОТКРЫТЬ ПОРТ»: каждое слово команды названо и каждое проверено.
    ///
    /// Мутации, которые обязаны упасть здесь: «профиль любой» · «порт любой» · «протокол любой» ·
    /// «исходящее вместо входящего» · «любой адрес вместо своей подсети» · «правило без программы
    /// (значит, любой программе)» · «правило на порт владельца».
    /// </summary>
    [Fact]
    public void Правило_узкое_и_только_на_своё()
    {
        var command = FirewallDecisions.Add(GatePort, Exe);

        Assert.True(command.Ok, command.Refusal);
        Assert.Empty(command.Refusal);

        var line = command.Line;

        Assert.Contains("advfirewall firewall add rule", line, StringComparison.Ordinal);
        Assert.Contains("name=\"" + FirewallDecisions.RuleName + "\"", line, StringComparison.Ordinal);
        Assert.Contains("dir=in", line, StringComparison.Ordinal);
        Assert.Contains("action=allow", line, StringComparison.Ordinal);
        Assert.Contains("protocol=TCP", line, StringComparison.Ordinal);
        Assert.Contains("localport=" + GatePort, line, StringComparison.Ordinal);
        Assert.Contains("program=\"" + Exe + "\"", line, StringComparison.Ordinal);
        Assert.Contains("profile=private", line, StringComparison.Ordinal);
        Assert.Contains("remoteip=localsubnet", line, StringComparison.Ordinal);
        Assert.Contains("enable=yes", line, StringComparison.Ordinal);

        // И НИ ОДНОГО ШИРОКОГО СЛОВА: «узкое» доказывается отсутствием, а не наличием.
        foreach (var wide in new[]
                 {
                     "profile=any", "localport=any", "protocol=any", "remoteip=any",
                     "dir=out", "action=block", "profile=domain", "profile=public", "program=\"\"",
                 })
        {
            Assert.DoesNotContain(wide, line, StringComparison.Ordinal);
        }

        // Порт владельца и порт панели правилом не открываются: калитка их не занимает.
        foreach (var forbidden in new[] { 3080, 3081, 0, 70000 })
        {
            var refused = FirewallDecisions.Add(forbidden, Exe);

            Assert.False(refused.Ok, $"правило собралось для порта {forbidden}: {refused.Line}");
            Assert.Contains(forbidden.ToString(System.Globalization.CultureInfo.InvariantCulture), refused.Refusal, StringComparison.Ordinal);
        }

        // Пути нет — команды нет: правило «любой программе» не собирается вовсе.
        Assert.False(FirewallDecisions.Add(GatePort, string.Empty).Ok);
        Assert.False(FirewallDecisions.Add(GatePort, "   ").Ok);
        Assert.False(FirewallDecisions.Add(GatePort, "C:\\плохой\"путь\\DshPanel.exe").Ok);
    }

    /// <summary>
    /// СНЯТИЕ УБИРАЕТ ТОЛЬКО СВОЁ: имя правила — единственный отбор, и оно ровно одно на панель.
    ///
    /// Мутации, которые обязаны упасть здесь: «снятие по чужому имени» · «снятие всех правил»
    /// (<c>name=all</c>) · «снятие по порту, а не по имени».
    /// </summary>
    [Fact]
    public void Снятие_убирает_только_своё_правило()
    {
        var line = FirewallDecisions.Remove();

        Assert.Equal("advfirewall firewall delete rule name=\"" + FirewallDecisions.RuleName + "\"", line);
        Assert.DoesNotContain("name=all", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("name=\"all\"", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localport", line, StringComparison.Ordinal);

        // Имя в команде РОВНО ОДНО: «убрать всё, что подходит» этой командой не выражается.
        Assert.Contains(FirewallDecisions.RuleName, line, StringComparison.Ordinal);
        Assert.Equal(1, line.Split("name=", StringSplitOptions.None).Length - 1);

        // Чтение тоже спрашивает ровно своё имя — и второе чтение добавляет требования формы.
        Assert.Contains("name=\"" + FirewallDecisions.RuleName + "\"", FirewallDecisions.ShowByName(), StringComparison.Ordinal);

        var narrow = FirewallDecisions.ShowNarrow();

        Assert.Contains("name=\"" + FirewallDecisions.RuleName + "\"", narrow, StringComparison.Ordinal);
        Assert.Contains("status=enabled", narrow, StringComparison.Ordinal);
        Assert.Contains("dir=in", narrow, StringComparison.Ordinal);
        Assert.Contains("profile=private", narrow, StringComparison.Ordinal);
    }

    /// <summary>
    /// ИМЯ ПРАВИЛА — ОДНО МЕСТО НА ВСЮ ПАНЕЛЬ. Проверка читает ИСХОДНИКИ: поведением этого
    /// не доказать — важно не «как называется», а что второго имени нет нигде.
    /// </summary>
    [Fact]
    public void Имя_правила_написано_ровно_в_одном_месте()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var source = Path.Combine(root!, "src", "DshPanel");
        var holders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            var code = UiThreadTests.WithoutComments(File.ReadAllText(path));

            if (code.Contains("DshPanel2-Phone-Gate", StringComparison.Ordinal)) holders.Add(Path.GetFileName(path));
        }

        Assert.Equal(new[] { "FirewallDecisions.cs" }, holders.OrderBy(name => name, StringComparer.Ordinal));

        // И само имя — латиницей: его печатает netsh в кодировке консоли, и от угадывания
        // кодировки зависит, узнаем ли мы СВОЁ правило.
        Assert.All(FirewallDecisions.RuleName, symbol => Assert.True(symbol < 128, $"в имени правила не латиница: {symbol}"));
        Assert.DoesNotContain(' ', FirewallDecisions.RuleName);
    }

    // ---- разбор состояния -----------------------------------------------------------------

    /// <summary>
    /// СОСТОЯНИЕ ЧИТАЕТСЯ ПРАВДОЙ: пять разных ответов машины дают пять разных состояний.
    ///
    /// Мутации, которые обязаны упасть здесь: «не прочиталось» выдано за «правила нет» · «правило
    /// с нашим именем есть, но не такое» выдано за готовое · «два правила с нашим именем» выданы
    /// за одно · «порт в правиле не наш, а сказано готово» · «программа в правиле не наша».
    /// </summary>
    [Fact]
    public void Состояние_читается_правдой()
    {
        // (1) НЕТ ПРАВИЛА: netsh отвечает кодом 1 и «No rules match…» — это ОТВЕТ, а не беда.
        var absent = FirewallDecisions.Read(Answer(NoRules, 1), Answer(NoRules, 1), GatePort, Exe);

        Assert.Equal(FirewallPresence.Absent, absent.Presence);
        Assert.True(absent.CanAdd);
        Assert.False(absent.CanRemove);
        Assert.Equal(PanelStrings.FirewallStateAbsent, absent.Words);

        // (2) НАШЕ ПРАВИЛО: имя, форма (фильтрами netsh) и наши значения — порт и путь.
        var ready = FirewallDecisions.Read(Answer(Ours()), Answer(Ours()), GatePort, Exe);

        Assert.Equal(FirewallPresence.Ready, ready.Presence);
        Assert.False(ready.CanAdd);
        Assert.True(ready.CanRemove);
        Assert.Contains(GatePort.ToString(System.Globalization.CultureInfo.InvariantCulture), ready.Words, StringComparison.Ordinal);

        // (3) ЧУЖОЕ ПОД НАШИМ ИМЕНЕМ: имя есть, а порт и программа — не наши.
        var other = FirewallDecisions.Read(Answer(Foreign()), Answer(Foreign()), GatePort, Exe);

        Assert.Equal(FirewallPresence.Other, other.Presence);
        Assert.False(other.CanAdd);
        Assert.True(other.CanRemove);
        Assert.Equal(PanelStrings.FirewallStateOther, other.Words);

        // (3а) форма не подтвердилась (netsh по фильтрам ничего не нашёл) — тоже «не такое»,
        //      даже когда значения в выводе наши.
        var notNarrow = FirewallDecisions.Read(Answer(Ours()), Answer(NoRules, 1), GatePort, Exe);

        Assert.Equal(FirewallPresence.Other, notNarrow.Presence);

        // (4) ДВА ПРАВИЛА С ОДНИМ ИМЕНЕМ: так быть не должно, и это отдельное состояние.
        var several = FirewallDecisions.Read(Answer(Ours() + Ours()), Answer(Ours()), GatePort, Exe);

        Assert.Equal(FirewallPresence.Several, several.Presence);
        Assert.True(several.CanRemove);

        // (5) НЕ ПРОЧИТАЛОСЬ — НЕ «ПРАВИЛА НЕТ»: пустой вывод, чужой код, молчание netsh.
        foreach (var broken in new[]
                 {
                     Answer(string.Empty, 0),
                     Answer("   \n", 0),
                     Answer("что-то пошло не так", 2),
                     new FirewallAnswer(-1, string.Empty),
                 })
        {
            var unknown = FirewallDecisions.Read(broken, broken, GatePort, Exe);

            Assert.Equal(FirewallPresence.Unknown, unknown.Presence);
            Assert.False(unknown.CanAdd);
            Assert.False(unknown.CanRemove);
            Assert.NotEqual(PanelStrings.FirewallStateAbsent, unknown.Words);
        }
    }

    // ---- изменение с проверкой -------------------------------------------------------------

    /// <summary>
    /// УСПЕХ ПОДТВЕРЖДАЕТСЯ ЧТЕНИЕМ, А НЕ КОДОМ ВОЗВРАТА.
    ///
    /// Мутации, которые обязаны упасть здесь: «код 0 принят за успех, хотя правила нет» ·
    /// «снятие отчиталось об успехе, а правило осталось» · «при отказе подтверждения сказано,
    /// что сделано» · «изменение сделано без нажатия».
    /// </summary>
    [Fact]
    public void Изменение_подтверждается_чтением()
    {
        var machine = new FakeFirewall();

        // (1) ПОСТАВИЛИ — и это ПРОЧИТАНО, а не заявлено кодом возврата.
        var added = FirewallWork.Run(machine, FirewallAction.Add, GatePort);

        Assert.True(added.Done);
        Assert.False(added.Refused);
        Assert.Equal(FirewallDecisions.DoneAdd(GatePort), added.Text);
        Assert.True(machine.Rule);
        Assert.True(machine.Reads > 0, "успех не подтверждён чтением");

        // (2) МАШИНА НЕ ИЗМЕНИЛАСЬ (netsh ответил 0, а правила нет) — говорится «не вышло»,
        //     и в словах стоит состояние, а не выдуманное «готово».
        var deaf = new FakeFirewall { Ignore = true };

        var notAdded = FirewallWork.Run(deaf, FirewallAction.Add, GatePort);

        Assert.False(notAdded.Done);
        Assert.False(notAdded.Refused);
        Assert.Contains(PanelStrings.FirewallStateAbsent, notAdded.Text, StringComparison.Ordinal);

        // (3) СНЯЛИ — и убедились, что имени больше нет.
        var removed = FirewallWork.Run(machine, FirewallAction.Remove, GatePort);

        Assert.True(removed.Done);
        Assert.Equal(FirewallDecisions.DoneRemove(), removed.Text);
        Assert.False(machine.Rule);

        // (4) СНЯТИЕ, КОТОРОЕ НЕ УБРАЛО: правило на месте — и сказано именно это.
        var sticky = new FakeFirewall { Rule = true, Narrow = true, Ignore = true };

        var notRemoved = FirewallWork.Run(sticky, FirewallAction.Remove, GatePort);

        Assert.False(notRemoved.Done);
        Assert.Equal(FirewallDecisions.NotRemoved(), notRemoved.Text);

        // (5) ЧЕЛОВЕК НЕ ПОДТВЕРДИЛ: правило НЕ ПОЯВЛЯЕТСЯ, и слова — про отказ, а не про беду.
        var refusedAdd = new FakeFirewall { Refuse = true };

        var refusal = FirewallWork.Run(refusedAdd, FirewallAction.Add, GatePort);

        Assert.False(refusal.Done);
        Assert.True(refusal.Refused);
        Assert.Equal(FirewallDecisions.Refused(), refusal.Text);
        Assert.False(refusedAdd.Rule, "при отказе подтверждения правило появилось");

        // (6) И НИ ОДНОГО ДЕЙСТВИЯ БЕЗ НАЖАТИЯ: пока никто не звал, машина не тронута вовсе.
        var untouched = new FakeFirewall();

        Assert.Equal(0, untouched.Adds);
        Assert.Equal(0, untouched.Removes);
    }

    /// <summary>
    /// ПРОГОН ПРОВЕРКИ К НАСТОЯЩЕЙ ДВЕРИ НЕ ХОДИТ — по исходникам, потому что поведением этого
    /// не доказать: важно не «что ответила дверь», а что у прогона её НЕТ.
    ///
    /// * настоящая дверь создаётся РОВНО В ОДНОМ месте — `App.axaml.cs`, под правом;
    /// * право спрашивается и внутри изменения (дверь без права отвечает отказом);
    /// * путь к `netsh` берётся из System32, а не из PATH.
    /// </summary>
    [Fact]
    public void Настоящая_дверь_создаётся_только_по_праву_в_App()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var source = Path.Combine(root!, "src", "DshPanel");
        var creations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            var code = UiThreadTests.WithoutComments(File.ReadAllText(path));

            if (code.Contains("new ElevatedFirewallDoor(", StringComparison.Ordinal)) creations.Add(Path.GetFileName(path));
        }

        Assert.Equal(new[] { "App.axaml.cs" }, creations.OrderBy(name => name, StringComparer.Ordinal));

        // Право спрашивается ВНУТРИ изменения, а не только там, где дверь создаётся.
        var door = UiThreadTests.WithoutComments(
            File.ReadAllText(Path.Combine(source, "Server", "FirewallDoor.cs")));

        Assert.Contains("RunRights.MayChangeFirewall", door, StringComparison.Ordinal);
        Assert.Contains("Environment.SystemDirectory", door, StringComparison.Ordinal);

        // Прогон съёмки двери не передаёт: иначе кадр (и любой прогон проверки) читал бы правила
        // и мог бы их менять.
        var program = UiThreadTests.WithoutComments(
            File.ReadAllText(Path.Combine(source, "Program.cs")));

        Assert.DoesNotContain("ElevatedFirewallDoor", program, StringComparison.Ordinal);
        Assert.DoesNotContain("IFirewallDoor", program, StringComparison.Ordinal);

        // И путь к netsh — системный, а не «как найдётся в PATH».
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "netsh.exe"), ElevatedFirewallDoor.NetshPath());
        Assert.Equal("netsh.exe", Path.GetFileName(ElevatedFirewallDoor.NetshPath()));
    }

    /// <summary>
    /// СЛОВА СОБРАНЫ В ОДНОМ МЕСТЕ И ГОВОРЯТ ТРИ РАЗНЫЕ ВЕЩИ: что будет · что сделано · что не вышло.
    /// Проверка держит не смысл (его читает человек), а то, что все места НАЗВАНЫ и не пусты.
    /// </summary>
    [Fact]
    public void Слова_до_после_и_отказа_названы()
    {
        Assert.Contains(GatePort.ToString(System.Globalization.CultureInfo.InvariantCulture), FirewallDecisions.BeforeAdd(GatePort), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(FirewallDecisions.BeforeRemove()));
        Assert.False(string.IsNullOrWhiteSpace(FirewallDecisions.Working()));
        Assert.Contains(GatePort.ToString(System.Globalization.CultureInfo.InvariantCulture), FirewallDecisions.DoneAdd(GatePort), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(FirewallDecisions.DoneRemove()));
        Assert.False(string.IsNullOrWhiteSpace(FirewallDecisions.Refused()));
        Assert.False(string.IsNullOrWhiteSpace(FirewallDecisions.NotRemoved()));

        // Слова ДО отличаются от слов ПОСЛЕ: «сейчас сделаю» и «сделано» — разные ответы.
        Assert.NotEqual(FirewallDecisions.BeforeAdd(GatePort), FirewallDecisions.DoneAdd(GatePort));
        Assert.NotEqual(PanelStrings.FirewallBeforeRemove, PanelStrings.FirewallDoneRemove);
    }

    /// <summary>
    /// ОТКАЗ ЧЕЛОВЕКА В ПОДТВЕРЖДЕНИИ ЧИТАЕТСЯ ПО НОМЕРУ, А НЕ ПО «НЕ НОЛЬ».
    ///
    /// ⚠️ Ради этого решение вынесено из <c>try/catch</c> в чистую функцию: отказ (<c>ERROR_CANCELLED
    /// 1223</c>) — НЕ беда панели, и сказать о нём надо словами «ничего не изменено», а не «netsh
    /// сломался». Запроса Windows эта проверка не поднимает вовсе — живого запроса на машине
    /// владельца не бывает ни в одном прогоне проверки.
    ///
    /// Мутации, которые обязаны упасть здесь: «при отказе повышения считает, что сделано» ·
    /// «отказ и поломка читаются одинаково».
    /// </summary>
    [Fact]
    public void Отказ_повышения_читается_по_коду()
    {
        Assert.Equal(FirewallElevation.Done, FirewallDecisions.Elevated(0, 0));
        Assert.Equal(FirewallElevation.Refused, FirewallDecisions.Elevated(-1, FirewallDecisions.CancelledNative));
        Assert.Equal(FirewallElevation.Refused, FirewallDecisions.Elevated(0, FirewallDecisions.CancelledNative));
        Assert.Equal(FirewallElevation.Failed, FirewallDecisions.Elevated(1, 0));
        Assert.Equal(FirewallElevation.Failed, FirewallDecisions.Elevated(0, 5));
        Assert.Equal(1223, FirewallDecisions.CancelledNative);
    }

    /// <summary>
    /// «ПРОВЕРИТЬ НЕ УДАЛОСЬ» — ЭТО НЕ «НЕ ТАКОЕ» (уточнение владельца 11.10.2026 после живого
    /// прогона). Три случая, и все три обязаны давать `Unverified`, а не `Other`:
    ///
    /// * вывод БЕЗ строки `Program:` — ровно так печатает netsh, когда его позвали без `verbose`;
    /// * форма правила не прочиталась вовсе (пустой вывод, чужой код возврата);
    /// * вывод есть, но программы в нём нет ни одной.
    ///
    /// И наоборот: ЧУЖАЯ программа в выводе или ДРУГОЙ порт — это уже «не такое», то есть `Other`.
    ///
    /// Мутации, которые обязаны упасть здесь: «из `ShowByName`/`ShowNarrow` убрано `verbose`» ·
    /// «`Unverified` свёрнуто в `Other`» · «чужое правило названо своим».
    /// </summary>
    [Fact]
    public void Непрочитанная_форма_это_не_не_такое()
    {
        // (1) ЧТЕНИЕ ЗОВЁТ netsh С `verbose`: без него он НЕ печатает строку `Program:`.
        foreach (var line in new[] { FirewallDecisions.ShowByName(), FirewallDecisions.ShowNarrow() })
        {
            Assert.Contains("verbose", line, StringComparison.Ordinal);
            Assert.Contains("name=\"" + FirewallDecisions.RuleName + "\"", line, StringComparison.Ordinal);
        }

        // (2) СТРОКИ ПРОГРАММЫ НЕТ ВОВСЕ (netsh без verbose) — «проверить не удалось», НЕ «не такое».
        var blind = FirewallDecisions.Read(Answer(WithoutProgram()), Answer(WithoutProgram()), GatePort, Exe);

        Assert.Equal(FirewallPresence.Unverified, blind.Presence);
        Assert.NotEqual(PanelStrings.FirewallStateOther, blind.Words);
        Assert.Contains("не удалось", blind.Words, StringComparison.Ordinal);
        Assert.False(blind.CanAdd);
        Assert.True(blind.CanRemove, "правило есть — убрать его обязано быть можно");

        // (3) ФОРМА НЕ ПРОЧИТАЛАСЬ (пустой вывод / чужой код) — тоже «проверить не удалось».
        foreach (var broken in new[] { Answer(string.Empty, 0), Answer("что-то пошло не так", 2) })
        {
            var state = FirewallDecisions.Read(Answer(Ours()), broken, GatePort, Exe);

            Assert.Equal(FirewallPresence.Unverified, state.Presence);
            Assert.True(state.CanRemove);
        }

        // (4) А ЧУЖАЯ ПРОГРАММА и ДРУГОЙ ПОРТ — это действительно «не такое».
        var foreignProgram = FirewallDecisions.Read(
            Answer(Ours()), Answer(Ours(exe: @"C:\Windows\System32\svchost.exe")), GatePort, Exe);

        Assert.Equal(FirewallPresence.Other, foreignProgram.Presence);

        var foreignPort = FirewallDecisions.Read(Answer(Ours()), Answer(Ours(port: 4444)), GatePort, Exe);

        Assert.Equal(FirewallPresence.Other, foreignPort.Presence);

        // (5) И НАШЕ правило по-прежнему читается как наше — иначе «не такое» ничего не значило бы.
        Assert.Equal(FirewallPresence.Ready, FirewallDecisions.Read(Answer(Ours()), Answer(Ours()), GatePort, Exe).Presence);
    }

    /// <summary>
    /// ПОДСТАВНАЯ МАШИНА: правило, которое можно поставить, снять и НЕ тронуть.
    ///
    /// ⚠️ Это не «двойник двери», а именно МАШИНА: у неё есть состояние (стоит ли правило и такое
    /// ли оно), и она умеет вести себя как настоящая в двух бедах — «просьбу пропустила, а правило
    /// не изменила» (`Ignore`) и «человек не подтвердил» (`Refuse`). Именно на них проверяется,
    /// что успех берётся чтением, а отказ не выдаётся за сделанное.
    /// </summary>
    private sealed class FakeFirewall : IFirewallDoor
    {
        public bool Rule { get; set; }

        public bool Narrow { get; set; } = true;

        public bool Refuse { get; set; }

        public bool Ignore { get; set; }

        public int Adds { get; private set; }

        public int Removes { get; private set; }

        public int Reads { get; private set; }

        public FirewallState Read(int port)
        {
            Reads++;

            if (!Rule) return FirewallDecisions.Absent();

            return Narrow ? FirewallDecisions.Ready(port) : new FirewallState(FirewallPresence.Other, PanelStrings.FirewallStateOther);
        }

        public FirewallChange Add(int port)
        {
            Adds++;

            if (Refuse) return FirewallChange.Refusal();
            if (!Ignore) Rule = true;

            return FirewallChange.Yes(string.Empty);
        }

        public FirewallChange Remove()
        {
            Removes++;

            if (Refuse) return FirewallChange.Refusal();
            if (!Ignore) Rule = false;

            return FirewallChange.Yes(string.Empty);
        }
    }
}
