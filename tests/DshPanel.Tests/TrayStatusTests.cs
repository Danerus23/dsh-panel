using System;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЧЕТЫРЕ строки состояния в меню значка (запрос владельца 27.09.2026: «в трее при нажатии правой
/// кнопкой мыши не хватает состояния»; четвёртая — обновление — встала в тот же ряд третьим шагом
/// работы обновления).
///
/// Проверка перебирает СОСТОЯНИЯ, а не смотрит один раз: у сервера их пять, и они говорят
/// человеку разное. Строка «Сервер: работает» вместо «Сервер: не подключён» — это не мелочь,
/// а неправда в меню, которое человек читает, не открывая окна.
/// </summary>
public class TrayStatusTests
{
    private static TrayStatusLines Build(
        bool connected = true,
        ServerPresence presence = ServerPresence.Stopped,
        ServerOwner owner = ServerOwner.None,
        int port = 3080,
        string agent = "DeepSeek",
        string balance = "12,34 $",
        bool inPeak = false) =>
        TrayStatus.Build(
            connected,
            presence,
            owner,
            port,
            agent,
            balance,
            inPeak,
            new TrayStatusLine("обновление: последняя версия", TrayTone.Good));

    /// <summary>
    /// «Панель сервера не знает» и «сервер не запущен» — РАЗНЫЕ утверждения, и различить их
    /// одним перечислением нельзя: умолчание <see cref="ServerPresence"/> — как раз «остановлен».
    /// Поэтому у сборщика есть отдельный признак «подключён», и эта проверка сторожит его.
    /// </summary>
    [Fact]
    public void Сервера_нет_вовсе_и_сервер_не_запущен_названы_по_разному()
    {
        var unbound = Build(connected: false);
        var stopped = Build(connected: true, presence: ServerPresence.Stopped);

        Assert.Equal(PanelStrings.TrayStatusServerUnbound, unbound.Server.Text);
        Assert.Equal(PanelStrings.TrayStatusServerStopped, stopped.Server.Text);
        Assert.NotEqual(unbound.Server.Text, stopped.Server.Text);
    }

    /// <summary>Работающий сервер называется вместе с портом: без порта строка бесполезна.</summary>
    [Fact]
    public void Работающий_сервер_назван_вместе_с_портом()
    {
        var own = Build(presence: ServerPresence.Running, owner: ServerOwner.Panel, port: 3099);

        Assert.Contains("3099", own.Server.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", own.Server.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Чужой работающий DSH назван чужим (владелец ничем его не поднимал): это ровно тот случай,
    /// ради которого делалось встраивание, и «работает» без оговорки читалось бы как «работает наш».
    /// </summary>
    [Fact]
    public void Чужой_работающий_сервер_назван_чужим()
    {
        var own = Build(presence: ServerPresence.Running, owner: ServerOwner.Panel);
        var adopted = Build(presence: ServerPresence.Running, owner: ServerOwner.Adopted);
        var foreign = Build(presence: ServerPresence.Running, owner: ServerOwner.None);

        Assert.Equal(own.Server.Text, adopted.Server.Text);
        Assert.NotEqual(own.Server.Text, foreign.Server.Text);
        Assert.Contains("3080", foreign.Server.Text, StringComparison.Ordinal);
    }

    /// <summary>Порт, занятый не-DSH, — третий случай, и он тоже назван словами.</summary>
    [Fact]
    public void Порт_занятый_чужой_программой_назван_прямо()
    {
        var busy = Build(presence: ServerPresence.BusyByOther);

        Assert.Equal(PanelStrings.TrayStatusServerBusy, busy.Server.Text);
        Assert.NotEqual(Build(presence: ServerPresence.Stopped).Server.Text, busy.Server.Text);
        Assert.NotEqual(Build(presence: ServerPresence.Running).Server.Text, busy.Server.Text);
    }

    /// <summary>
    /// Сумма баланса доходит до строки, а её отсутствие — словами, не пустым местом.
    /// Строка «Агент: DeepSeek · баланс: » выглядит как сломанная панель.
    /// </summary>
    [Fact]
    public void Баланс_и_его_отсутствие_видны_по_разному()
    {
        var withMoney = Build(balance: "12,34 $");
        var without = Build(balance: "   ");

        Assert.Contains("12,34 $", withMoney.Agent.Text, StringComparison.Ordinal);
        Assert.Contains(PanelStrings.TrayStatusNoBalance, without.Agent.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(PanelStrings.TrayStatusNoBalance, withMoney.Agent.Text, StringComparison.Ordinal);
    }

    /// <summary>Имя агента — то, что владелец назвал словами «активный агент, который отслеживается».</summary>
    [Fact]
    public void Имя_агента_и_его_отсутствие_видны_по_разному()
    {
        var named = Build(agent: "DeepSeek");
        var unnamed = Build(agent: "");

        Assert.Contains("DeepSeek", named.Agent.Text, StringComparison.Ordinal);
        Assert.Contains(PanelStrings.TrayStatusNoAgent, unnamed.Agent.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", unnamed.Agent.Text, StringComparison.Ordinal);
    }

    /// <summary>Тариф — готовой строкой словаря; пик и вне пика называются по-разному.</summary>
    [Fact]
    public void Тариф_берётся_готовой_строкой()
    {
        Assert.Equal(PanelStrings.PeakInPeak, Build(inPeak: true).Peak.Text);
        Assert.Equal(PanelStrings.PeakOffPeak, Build(inPeak: false).Peak.Text);
    }

    // ---------------------------------------------------------------- тон (цвет) строк

    /// <summary>
    /// ЦВЕТ СТРОКИ СЕРВЕРА — решение владельца 27.09.2026: работает — зелёный, не работает
    /// (остановлен или порт занят чужой программой) — красный, панель не знает — серый.
    ///
    /// Перебираются ВСЕ состояния перечисления, и у каждого назван свой ответ: проверка «тон
    /// где-то есть» зеленела бы и при перепутанных цветах.
    /// </summary>
    [Fact]
    public void Тон_строки_сервера_назван_для_каждого_состояния()
    {
        // Сервера нет вовсе — серый: это «не знаю», а не «сломалось».
        Assert.Equal(TrayTone.Neutral, Build(connected: false, presence: ServerPresence.Running).Server.Tone);

        Assert.Equal(TrayTone.Good, Build(presence: ServerPresence.Running, owner: ServerOwner.Panel).Server.Tone);
        Assert.Equal(TrayTone.Good, Build(presence: ServerPresence.Running, owner: ServerOwner.Adopted).Server.Tone);

        // Чужой работающий DSH — ЗЕЛЁНЫЙ: он работает, а чей он, сказано словами. Красный здесь
        // значил бы «DSH не работает», то есть был бы неправдой.
        Assert.Equal(TrayTone.Good, Build(presence: ServerPresence.Running, owner: ServerOwner.None).Server.Tone);

        Assert.Equal(TrayTone.Bad, Build(presence: ServerPresence.Stopped).Server.Tone);
        Assert.Equal(TrayTone.Bad, Build(presence: ServerPresence.BusyByOther).Server.Tone);

        // Состояний ровно три, и все три названы выше: добавится четвёртое — упадёт эта строка,
        // а не молча останется без цвета.
        Assert.Equal(3, Enum.GetValues<ServerPresence>().Length);
    }

    /// <summary>
    /// Цвет строки АГЕНТА: баланс есть — зелёный, нет баланса или нет агента — янтарный.
    /// Янтарный здесь «внимание», а не «авария»: панель не сломана, она просто не знает суммы.
    /// </summary>
    [Fact]
    public void Тон_строки_агента_янтарный_без_баланса_и_без_агента()
    {
        Assert.Equal(TrayTone.Good, Build(agent: "DeepSeek", balance: "12,34 $").Agent.Tone);
        Assert.Equal(TrayTone.Warning, Build(agent: "DeepSeek", balance: "   ").Agent.Tone);
        Assert.Equal(TrayTone.Warning, Build(agent: "", balance: "12,34 $").Agent.Tone);
        Assert.Equal(TrayTone.Warning, Build(agent: "", balance: "").Agent.Tone);
    }

    /// <summary>Цвет строки тарифа: пик — янтарный (дороже), вне пика — зелёный.</summary>
    [Fact]
    public void Тон_строки_тарифа_янтарный_в_пик()
    {
        Assert.Equal(TrayTone.Warning, Build(inPeak: true).Peak.Tone);
        Assert.Equal(TrayTone.Good, Build(inPeak: false).Peak.Tone);
    }

    /// <summary>
    /// Огонёк на значке — тот же язык, что строка сервера, но проверяется СВОИМИ словами:
    /// человек смотрит на рисунок, а не на меню, и обещание у значка своё.
    /// Сегодня ответы совпадают — и это совпадение проверяется перебором, а не подразумевается.
    /// </summary>
    [Fact]
    public void Тон_огонька_на_значке_идёт_за_состоянием_сервера()
    {
        Assert.Equal(TrayTone.Neutral, TrayStatus.IconTone(connected: false, presence: ServerPresence.Stopped));
        Assert.Equal(TrayTone.Good, TrayStatus.IconTone(connected: true, presence: ServerPresence.Running));
        Assert.Equal(TrayTone.Bad, TrayStatus.IconTone(connected: true, presence: ServerPresence.Stopped));
        Assert.Equal(TrayTone.Bad, TrayStatus.IconTone(connected: true, presence: ServerPresence.BusyByOther));

        // И то же самое по всем состояниям: значок не расходится со строкой сервера.
        foreach (var connected in new[] { true, false })
        {
            foreach (var presence in Enum.GetValues<ServerPresence>())
            {
                Assert.Equal(
                    TrayStatus.ServerTone(connected, presence),
                    TrayStatus.IconTone(connected, presence));
            }
        }
    }

    /// <summary>
    /// В ЛЮБОМ состоянии все три строки непусты и без неподставленных мест: «{0}» в меню
    /// человек прочитал бы как поломку перевода, а пустая строка в шапке — как «панель
    /// не знает, что показать». Перебор идёт по ВСЕМ сочетаниям, а не по трём выбранным.
    ///
    /// Тем же перебором сторожится и тон: он обязан быть ОДНИМ ИЗ четырёх, а не случайным числом
    /// (перечисление с дырой — это цвет, которого нет в палитре).
    /// </summary>
    [Fact]
    public void В_любом_состоянии_три_строки_непусты_и_без_подстановок()
    {
        var checkedStates = 0;
        var tones = new List<(TrayTone Server, TrayTone Agent, TrayTone Peak)>();

        foreach (var connected in new[] { true, false })
        {
            foreach (var presence in Enum.GetValues<ServerPresence>())
            {
                foreach (var owner in Enum.GetValues<ServerOwner>())
                {
                    var lines = Build(connected, presence, owner, 3080);
                    checkedStates++;

                    foreach (var line in new[] { lines.Server, lines.Agent, lines.Peak })
                    {
                        Assert.False(string.IsNullOrWhiteSpace(line.Text), $"пустая строка при {presence}/{owner}");
                        Assert.DoesNotContain("{0}", line.Text, StringComparison.Ordinal);
                        Assert.DoesNotContain("{1}", line.Text, StringComparison.Ordinal);
                        Assert.True(Enum.IsDefined(line.Tone), $"тон {line.Tone} не из палитры");
                    }

                    tones.Add((lines.Server.Tone, lines.Agent.Tone, lines.Peak.Tone));
                }
            }
        }

        Assert.Equal(2 * Enum.GetValues<ServerPresence>().Length * Enum.GetValues<ServerOwner>().Length, checkedStates);

        // Тона НЕ одинаковы во всех состояниях: будь тон константой, проверка выше зеленела бы,
        // а цвет в меню не менялся бы вовсе — ровно та беда, ради которой всё и делалось.
        Assert.True(tones.Distinct().Count() > 1, "тон строк не зависит от состояния — цвет ничего не значит");
    }
}
