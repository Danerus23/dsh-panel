using System;
using DshPanel.Isolation;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПРАВА ПРОГОНА — проверки, живущие рядом с правом (этап F1).
///
/// Полный перебор прав и путей — в <see cref="IsolationTests"/>; здесь то, что принадлежит
/// телефону и живёт своим файлом, чтобы этап F не правил проверки соседних этапов.
///
/// **Зачем отдельная проверка на каждое сочетание.** Право «калитку открывает только обычный
/// запуск человеком и только если человек её включил» держится ТРЕМЯ условиями, и любое
/// из них можно снять одной правкой. Проверка, перебирающая все восемь сочетаний, роняет
/// такую правку сразу и называет, какое именно условие пропало.
/// </summary>
public class RunRightsTests
{
    private static Func<string, string?> NoEnv => _ => null;

    private static string TempRoot() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-panel-phone-rights", Guid.NewGuid().ToString("N"));

    private static RunContext Isolated() =>
        RunContext.Create(RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, TempRoot() }, NoEnv));

    private static RunContext Owner() => RunContext.Create(RunRequest.Owner());

    /// <summary>
    /// КАЛИТКУ ТЕЛЕФОНА ОТКРЫВАЕТ ТОЛЬКО ОБЫЧНЫЙ ЗАПУСК ЧЕЛОВЕКОМ — И ТОЛЬКО КОГДА ЧЕЛОВЕК ЕЁ ВКЛЮЧИЛ.
    ///
    /// Перебираются ВСЕ восемь сочетаний «изолированно × человек × включено», а не одно:
    /// два условия из трёх (`!IsIsolated` и `humanLaunch`) совпадают с телом
    /// <see cref="RunRights.MayAutoStartServer"/>, и снятое любое из них проверка обязана увидеть.
    /// ⚠️ Снятое условие «только человек» иначе не ловится ничем: на машине владельца до запуска
    /// дело не доходит, потому что прогон проверки открывает калитку лишь на время прогона, —
    /// а «на время» здесь и есть дверь наружу.
    /// </summary>
    [Fact]
    public void Калитку_телефона_открывает_только_включённый_запуск_человеком()
    {
        var cases = new (bool Isolated, bool Human, bool Enabled, bool Expected)[]
        {
            (false, true,  true,  true),    // обычный запуск человеком и телефон включён — можно
            (false, true,  false, false),   // человек её НЕ включал — нельзя
            (false, false, true,  false),   // прогон проверки — нельзя, хотя телефон включён
            (false, false, false, false),   // прогон проверки без разрешения — нельзя
            (true,  true,  true,  false),   // изолированный прогон — нельзя (и человек его не запускал)
            (true,  true,  false, false),   // изолированный, телефон выключен — нельзя
            (true,  false, true,  false),   // изолированный прогон проверки — нельзя
            (true,  false, false, false),   // изолированный прогон проверки без разрешения — нельзя
        };

        Assert.Equal(8, cases.Length);

        foreach (var (isolated, human, enabled, expected) in cases)
        {
            var context = isolated ? Isolated() : Owner();

            Assert.Equal(
                expected,
                RunRights.MayOpenPhoneGate(context, humanLaunch: human, enabled: enabled));
        }
    }

    /// <summary>
    /// ВЫКЛЮЧЕННАЯ КАЛИТКА НЕ ОТКРЫВАЕТСЯ НИКОГДА — даже у обычного запуска человеком. Это
    /// ОТДЕЛЬНОЕ условие, а не следствие первого: без него «человек запустил панель» и «человек
    /// разрешил телефон» слились бы в одно, и порт открылся бы у всякого, кто просто открыл панель.
    /// </summary>
    [Fact]
    public void Выключенный_телефон_не_открывает_калитку_даже_у_человека()
    {
        Assert.False(RunRights.MayOpenPhoneGate(Owner(), humanLaunch: true, enabled: false));
        Assert.True(RunRights.MayOpenPhoneGate(Owner(), humanLaunch: true, enabled: true));
    }

    /// <summary>
    /// ПРАВО ЭТО — СВОЁ, И ОНО ОСТАЁТСЯ НА МЕСТЕ: у него своя точка входа и своё имя, и оно
    /// не подменяется соседним. Проверяется то, что видно снаружи (ломать код ради проверки
    /// «одно право не зовёт другое» пришлось бы правкой самих исходников).
    ///
    /// ⚠️ И это не формальность: у каждого из «человеческих» прав завтра появится своё третье
    /// условие — у телефона, например, «человек разрешил И порт свободен», — и сведённые в одну
    /// функцию они поехали бы вместе молча.
    /// </summary>
    [Fact]
    public void Право_калитки_существует_отдельно_от_прочих()
    {
        var owner = Owner();

        Assert.True(RunRights.MayOpenPhoneGate(owner, humanLaunch: true, enabled: true));
        Assert.True(RunRights.MayAutoStartServer(owner, humanLaunch: true));
        Assert.True(RunRights.MayOccupyOwnerPort(owner, humanLaunch: true));
        Assert.True(RunRights.MayOpenBrowser(owner, humanLaunch: true));

        // И все четыре — про ЛЮДЕЙ: в изоляции у них у всех пусто.
        var isolated = Isolated();

        Assert.False(RunRights.MayOpenPhoneGate(isolated, humanLaunch: true, enabled: true));
        Assert.False(RunRights.MayAutoStartServer(isolated, humanLaunch: true));
        Assert.False(RunRights.MayOccupyOwnerPort(isolated, humanLaunch: true));
        Assert.False(RunRights.MayOpenBrowser(isolated, humanLaunch: true));
    }

    /// <summary>
    /// У ПРАВА НЕТ УМОЛЧАНИЯ «ПУСТЬ БУДЕТ»: без ответа на вопрос «человек её включил?» право
    /// не спрашивается вовсе — третьего довода нет в подписи, и вызов без него не собирается.
    /// Проверка сторожит именно это: значение по умолчанию у третьего довода появилось бы
    /// по неосторожности и сделало бы выключенную калитку открытой.
    /// </summary>
    [Fact]
    public void У_права_калитки_нет_умолчания_на_включённость()
    {
        var method = typeof(RunRights).GetMethod(nameof(RunRights.MayOpenPhoneGate));

        Assert.NotNull(method);

        var parameters = method!.GetParameters();

        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(bool), parameters[2].ParameterType);
        Assert.False(parameters[2].HasDefaultValue, "у «включено» появилось умолчание — калитка откроется сама");
    }

    /// <summary>
    /// ВЛАДЕТЬ КАЛИТКОЙ И ОТКРЫВАТЬ ЕЁ — ДВА РАЗНЫХ ПРАВА (уточнение владельца 11.10.2026).
    ///
    /// **Зачем разделено.** Прежде право было одно, и выключенный телефон означал «калитки нет
    /// вовсе»: щелчок рубильника поднимать было нечего, а код сопряжения — выпустить нечем. Теперь
    /// ВЛАДЕНИЕ отвечает на «этот прогон вообще имеет дело с калиткой» (и от рубильника НЕ зависит),
    /// а ОТКРЫТИЕ — на «ей позволено слушать» (владение ПЛЮС включённый телефон).
    ///
    /// Перебираются все восемь сочетаний: снятое «не изолирован» у владения означало бы, что прогон
    /// проверки держит калитку, а снятое «человек её включил» у открытия — открытую дверь
    /// без согласия.
    /// </summary>
    [Fact]
    public void Владение_калиткой_и_её_открытие_разведены()
    {
        foreach (var isolated in new[] { false, true })
        {
            foreach (var human in new[] { false, true })
            {
                foreach (var enabled in new[] { false, true })
                {
                    var context = isolated ? Isolated() : Owner();

                    // ВЛАДЕНИЕ: только обычный запуск человеком — и НИКОГДА не от рубильника.
                    Assert.Equal(
                        human && !isolated,
                        RunRights.MayOwnPhoneGate(context, humanLaunch: human));

                    // ОТКРЫТИЕ: владение И включённый телефон.
                    Assert.Equal(
                        human && !isolated && enabled,
                        RunRights.MayOpenPhoneGate(context, humanLaunch: human, enabled: enabled));
                }
            }
        }
    }

    /// <summary>
    /// МАШИННУЮ УСТАНОВКУ ДВИЖКА В КОПИЮ БЕРЁТ ТОЛЬКО ОБЫЧНЫЙ ЗАПУСК ЧЕЛОВЕКОМ (A4, находка C1 CH-08).
    ///
    /// **От какого случая.** Изолированная копия (`--backup` с `--run-root`) унесла в архив каталоги
    /// МАШИНЫ — `%APPDATA%\npm` и `%ProgramFiles%\nodejs` — 276,0 МБ / 29 899 файлов. Личных данных
    /// там не было, но изоляция не держалась: у изолированного прогона появился путь за свой корень.
    ///
    /// **Зачем четыре сочетания, а не одно.** У права два условия (`humanLaunch` и `!IsIsolated`),
    /// и снятое любое из них открывает дыру. Прогон проверки (`--shot`, `--shell-selftest`) НЕ
    /// изолирован — и без второго условия он получил бы право уносить машинный движок.
    ///
    /// **Мутации, которые обязаны упасть:** «право всегда true» · «право всегда false» (это ловит
    /// третья проверка — «человек берёт машинную установку в свою копию»).
    /// </summary>
    [Fact]
    public void Машинную_установку_движка_в_копию_берёт_только_обычный_запуск_человеком()
    {
        var cases = new (bool Isolated, bool Human, bool Expected)[]
        {
            (false, true,  true),    // обычный запуск человеком — можно: его копия самодостаточна
            (false, false, false),   // прогон проверки — НЕЛЬЗЯ (он не изолирован, и это главный случай)
            (true,  true,  false),   // изолированный прогон — нельзя
            (true,  false, false),   // изолированный прогон проверки — нельзя
        };

        Assert.Equal(4, cases.Length);

        foreach (var (isolated, human, expected) in cases)
        {
            var context = isolated ? Isolated() : Owner();

            Assert.Equal(expected, RunRights.MachineEngineData(context, humanLaunch: human));
        }
    }
    /// <summary>
    /// ПРАВИЛО БРАНДМАУЭРА МЕНЯЕТ ТОЛЬКО ОБЫЧНЫЙ ЗАПУСК ПАНЕЛИ ЧЕЛОВЕКОМ (этап F4).
    ///
    /// **Зачем отдельная проверка на четыре сочетания.** Это право про ИЗМЕНЕНИЕ НА МАШИНЕ, а не
    /// про своё состояние панели: правило брандмауэра открывает входящее соединение ИЗ СЕТИ, и живёт
    /// оно в системе, переживая сам прогон. Поэтому «прогон не изолирован» здесь НЕ достаточно —
    /// и снятое условие «только человек» проверка обязана увидеть: прогон проверки (он как раз
    /// не изолирован) иначе получил бы право править брандмауэр владельца.
    /// </summary>
    [Fact]
    public void Правило_брандмауэра_меняет_только_запуск_человеком()
    {
        var cases = new (bool Isolated, bool Human, bool Expected)[]
        {
            (false, true,  true),    // обычный запуск человеком — можно
            (false, false, false),   // прогон проверки — НЕЛЬЗЯ (и это главный случай: он не изолирован)
            (true,  true,  false),   // изолированный прогон — нельзя
            (true,  false, false),   // изолированный прогон проверки — нельзя
        };

        foreach (var (isolated, human, expected) in cases)
        {
            var context = isolated ? Isolated() : Owner();

            Assert.Equal(expected, RunRights.MayChangeFirewall(context, humanLaunch: human));
        }

        // И право ЭТО — СВОЁ: у него своё имя, и оно не «то же самое», что порт или реестр.
        Assert.True(RunRights.MayChangeFirewall(Owner(), humanLaunch: true));
    }
}
