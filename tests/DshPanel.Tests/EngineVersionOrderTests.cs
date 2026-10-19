using System;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПОРЯДОК ВЕРСИЙ С ПРЕДРЕЛИЗНОЙ ЧАСТЬЮ — ОДНО ПРАВИЛО НА ВСЮ ПАНЕЛЬ (задача F-1b, слово владельца
/// 20.10.2026).
///
/// **От какого случая.** Версии движка сравнивались по числу (`EngineNode.Compare` режет номер до
/// `X.Y.Z`), и `0.2.1-rc.3` против `0.2.1-rc.2` считалось «то же самое» — кнопка «Поставить
/// непроверенную» не показывалась ровно в сценарии «вышел новый rc».
///
/// **Что здесь доказано:** порядок `0.2.1 > 0.2.1-rc.3 > 0.2.1-rc.2 > 0.2.1-alpha.5`;
/// числа сравниваются как числа (`0.2.10` > `0.2.9`), номера хвоста тоже (`rc.10` > `rc.9`);
/// короткий хвост младше длинного; `v` и `+hash` на порядок не влияют; «похоже ли на версию» —
/// отдельный ответ, и мусор версией не считается.
///
/// ⚠️ **Обе стороны сравниваются одним правилом** (`Compare(a,b) == -Compare(b,a)`): второго мнения
/// о порядке в панели быть не должно.
///
/// **Мутации, которые обязаны упасть:** «сравнивать хвост как строку целиком» (`rc.10` < `rc.9`) ·
/// «финал считать младше предрелиза» · «числовой кусок хвоста считать старше буквенного» ·
/// «мусор считать версией».
/// </summary>
public sealed class EngineVersionOrderTests
{
    [Theory]
    [InlineData("0.2.1", "0.2.1-rc.3", 1)]          // финальный выпуск выше своего предрелиза
    [InlineData("0.2.1-rc.3", "0.2.1-rc.2", 1)]     // F-1b: ради этого случая задача и делалась
    [InlineData("0.2.1-rc.2", "0.2.1-alpha.5", 1)]  // rc выше alpha
    [InlineData("0.2.1-rc.10", "0.2.1-rc.9", 1)]    // номера хвоста — как числа
    [InlineData("0.2.10-rc.1", "0.2.9-rc.9", 1)]    // числа версии — как числа, хвост не спасает
    [InlineData("0.2.1-alpha", "0.2.1-alpha.1", -1)] // короткий хвост младше длинного
    [InlineData("0.2.1-rc.2", "0.2.1-rc.2.1", -1)]
    [InlineData("v1.21.0", "1.21.0", 0)]            // v — обычай тегов, не версия
    [InlineData("2.0.0+481fb6b", "2.0.0", 0)]       // +hash — хеш сборки, не версия
    [InlineData("0.2.1-rc.2", "0.2.1-rc.2", 0)]
    public void Порядок_версий_по_SemVer(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(EngineVersionOrder.Compare(left, right)));

        // Обратная сторона — тем же правилом, а не «вторым мнением».
        Assert.Equal(-expected, Math.Sign(EngineVersionOrder.Compare(right, left)));
    }

    /// <summary>
    /// «ПОХОЖЕ ЛИ НА ВЕРСИЮ» — ОТДЕЛЬНЫЙ ОТВЕТ: мусор не должен выглядеть «финальным выпуском,
    /// который новее любого rc». Поэтому <c>IsVersion</c> спрашивают ДО сравнения.
    /// </summary>
    [Theory]
    [InlineData("0.2.1-rc.2", true)]
    [InlineData("v1.21.0", true)]
    [InlineData("2.0.0+481fb6b", true)]
    [InlineData("0.2", true)]
    [InlineData("", false)]
    [InlineData("не-версия", false)]
    [InlineData("rc.2", false)]
    [InlineData("0. 2", false)]
    [InlineData("0.2.1|", false)]
    public void Похоже_ли_на_версию(string text, bool expected) =>
        Assert.Equal(expected, EngineVersionOrder.IsVersion(text));

    /// <summary>
    /// ЧИСЛОВОЕ СРАВНЕНИЕ — ОТДЕЛЬНЫЙ ОТВЕТ (объявление о новой версии зовёт именно его), и он
    /// НЕ смотрит хвост: «0.1.5 против 0.1.5-rc.2» для него «не новее», а для порядка версий —
    /// «новее». Два разных решения, два разных имени, и оба названы.
    /// </summary>
    [Fact]
    public void Числовое_сравнение_хвост_не_смотрит()
    {
        Assert.False(EngineVersionOrder.IsNewerByNumber("0.1.5", "0.1.5-rc.2"));
        Assert.False(EngineVersionOrder.IsNewerByNumber("0.1.5-rc.2", "0.1.5"));
        Assert.True(EngineVersionOrder.IsNewerByNumber("0.2.0-rc.2", "0.1.5-rc.2"));

        Assert.True(EngineVersionOrder.IsNewer("0.1.5", "0.1.5-rc.2"));
        Assert.False(EngineVersionOrder.IsNewer("0.1.5-rc.2", "0.1.5"));
    }
}
