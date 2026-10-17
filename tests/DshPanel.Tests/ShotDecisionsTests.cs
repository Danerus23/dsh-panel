using System;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// РАЗБОР КЛЮЧЕЙ СЪЁМКИ — ЧИСТОЕ, БЕЗ ОКНА И БЕЗ ПЛАТФОРМЫ.
///
/// **Зачем класс.** Ключи <c>--theme</c> и <c>--state</c> решают, ЧТО попадёт в кадр, а кадр уезжает
/// в README. Разобрать их в <c>Program.Shot</c> проверкой нельзя (съёмка поднимает платформу),
/// поэтому разбор вынесен в <see cref="ShotDecisions"/> и проверяется здесь.
///
/// ⚠️ **Главное, что стерегут эти проверки:** разбор НЕ СЪЕДАЕТ САМ РЕЖИМ. «<c>--theme --shot
/// кадр.png</c>» (человек забыл значение) обязан оставить съёмку на месте: иначе панель открыла бы
/// окно владельцу вместо кадра — ровно тот случай, ради которого имена ключей вообще отвергаются
/// громко.
/// </summary>
public class ShotDecisionsTests
{
    [Fact]
    public void Тема_разбирается_ровно_на_два_значения()
    {
        Assert.Equal(ShotDecisions.ThemeDark, ShotDecisions.Theme("dark"));
        Assert.Equal(ShotDecisions.ThemeLight, ShotDecisions.Theme("light"));

        // Регистр не важен: ключ набирает человек.
        Assert.Equal(ShotDecisions.ThemeDark, ShotDecisions.Theme("DARK"));
        Assert.Equal(ShotDecisions.ThemeLight, ShotDecisions.Theme(" Light "));

        // Пусто — «ключ не называли»: кадр снимается в теме панели, как было.
        Assert.Null(ShotDecisions.Theme(null));
        Assert.Null(ShotDecisions.Theme(string.Empty));
        Assert.Null(ShotDecisions.Theme("   "));

        // Незнакомое — тоже null, и вызывающий обязан отказать ГРОМКО (проверяется ниже).
        Assert.Null(ShotDecisions.Theme("тёмная"));
        Assert.Null(ShotDecisions.Theme("night"));

        Assert.False(ShotDecisions.KnowsTheme("night"));
        Assert.True(ShotDecisions.KnowsTheme("dark"));
        Assert.True(ShotDecisions.KnowsTheme("light"));
    }

    /// <summary>
    /// ОТКУДА ДАННЫЕ НА КАДРЕ: ОБРАЗЦОВЫЕ — У ВСЕХ КАДРОВ, КРОМЕ ОДНОГО.
    ///
    /// **От какого случая (мутация AJ4, `docs\HISTORY.md`).** Решение жило условием в одну строку
    /// внутри `Program.Shot`, и проверке до него было не дотянуться: мутация «образцовая дверь
    /// больше не доезжает до окна» не роняла ничего. Теперь решение — ЗДЕСЬ, и его спрашивает
    /// и съёмка, и проверка.
    ///
    /// ⚠️ **Исключение одно и названо:** кадр «два найденных сервера» снимает НАСТОЯЩУЮ модель
    /// окна с подставленной пробой процессов — там дело именно в пометках «наш движок (версия)»
    /// и «чужой», и образцовые двери его заменяют не делом, а помехой.
    ///
    /// Мутации, которые обязаны упасть здесь: «все кадры настоящие» · «образцовые и у кадра
    /// с находками» · «имя кадра без образцовых данных».
    /// </summary>
    [Fact]
    public void Образцовые_данные_у_всех_кадров_кроме_двух_находок()
    {
        foreach (var kind in StartModes.ShotWindows)
        {
            var expected = kind == StartModes.ShotMainTwoServers
                ? StartModes.ShotDoors.Real
                : StartModes.ShotDoors.Sample;

            Assert.True(
                Program.ShotDoorSource(kind) == expected,
                $"кадр «{kind}»: данные взяты не оттуда, откуда обещано");
        }

        // И НЕЗНАКОМОЕ ИМЯ — ТОЖЕ ОБРАЗЦОВЫЕ: незнакомого имени съёмка не примет вовсе
        // (громкий отказ кодом 2), но решение не должно выдавать «настоящие» по умолчанию —
        // забытая ветвь иначе открыла бы путь к данным владельца.
        Assert.True(Program.ShotDoorSource("givotnoe") == StartModes.ShotDoors.Sample);
    }

    [Fact]
    public void Состояние_разбирается_как_состояние_и_имя_органа()
    {
        Assert.Equal((ShotDecisions.StateFocus, "SaveButton"), ShotDecisions.State("focus:SaveButton"));
        Assert.Equal((ShotDecisions.StateHover, "SaveButton"), ShotDecisions.State("hover:SaveButton"));

        // Имя органа не режется по двоеточию: оно берётся ЦЕЛИКОМ после первого.
        Assert.Equal((ShotDecisions.StateFocus, "a:b"), ShotDecisions.State("focus:a:b"));

        Assert.Null(ShotDecisions.State(null));
        Assert.Null(ShotDecisions.State(string.Empty));
        Assert.Null(ShotDecisions.State("focus"));
        Assert.Null(ShotDecisions.State("focus:"));
        Assert.Null(ShotDecisions.State(":SaveButton"));
        Assert.Null(ShotDecisions.State("pressed:SaveButton"));

        Assert.True(ShotDecisions.KnowsState("focus:SaveButton"));
        Assert.False(ShotDecisions.KnowsState("pressed:SaveButton"));
    }

    /// <summary>
    /// РАЗБОР НЕ СЪЕДАЕТ РЕЖИМ — и это то же правило, что у <c>--lang</c>.
    ///
    /// Мутации, которые обязаны упасть здесь: «снимать значение даже у ключа» (тогда
    /// «--theme --shot кадр.png» превратилось бы в обычный запуск и открыло окно владельцу) ·
    /// «не снимать ключ вовсе» (разбор счёл бы его опечаткой).
    /// </summary>
    [Fact]
    public void Разбор_не_съедает_режим()
    {
        Assert.Equal(
            new[] { "--shot", "кадр.png" },
            ShotDecisions.WithoutSwitches(new[] { "--theme", "--shot", "кадр.png" }));

        Assert.Equal(
            new[] { "--shot", "кадр.png", "settings" },
            ShotDecisions.WithoutSwitches(new[] { "--theme", "dark", "--shot", "кадр.png", "settings" }));

        Assert.Equal(
            new[] { "--shot", "кадр.png" },
            ShotDecisions.WithoutSwitches(new[] { "--state", "focus:SaveButton", "--shot", "кадр.png" }));

        // Ни одного знакомого ключа — аргументы не тронуты.
        Assert.Equal(
            new[] { "--shot", "кадр.png" },
            ShotDecisions.WithoutSwitches(new[] { "--shot", "кадр.png" }));

        // А ЗНАЧЕНИЕ ЧИТАЕТСЯ ИЗ ТЕХ ЖЕ АРГУМЕНТОВ.
        Assert.Equal("dark", ShotDecisions.Value(new[] { "--theme", "dark", "--shot", "к.png" }, ShotDecisions.ThemeSwitch));
        Assert.Equal("focus:SaveButton", ShotDecisions.Value(new[] { "--state", "focus:SaveButton" }, ShotDecisions.StateSwitch));
        Assert.Null(ShotDecisions.Value(new[] { "--theme", "--shot", "к.png" }, ShotDecisions.ThemeSwitch));
        Assert.Null(ShotDecisions.Value(new[] { "--shot", "к.png" }, ShotDecisions.ThemeSwitch));
    }
}
