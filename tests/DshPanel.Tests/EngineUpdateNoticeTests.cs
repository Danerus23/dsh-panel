using System;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ШАРИК О НОВОЙ ВЕРСИИ ДВИЖКА (пункт 7 плана E3.2: «авто-проверка как у панели»).
///
/// Правило взято у обновления самой панели и проверяется отдельно, потому что ошибка здесь видна
/// человеку каждый день: слишком щедрое «говорить» превращается в приставание, слишком скупое —
/// в «панель молчала, а версия была».
///
/// Три случая, и все три — из предмета: версия новее (говорим), версия та же или старше (молчим),
/// про эту версию уже сказали (молчим), движка на машине нет вовсе (молчим — это не «есть новее»).
/// </summary>
public class EngineUpdateNoticeTests
{
    /// <summary>РЕКОМЕНДОВАННАЯ НОВЕЕ ПОСТАВЛЕННОЙ — говорим, и в словах названа версия.</summary>
    [Fact]
    public void Новее_поставленной_говорим()
    {
        Assert.True(EngineUpdateNotice.ShouldAnnounce("0.1.5-rc.2", "0.1.6-rc.2", announced: string.Empty));

        var message = EngineUpdateNotice.Message("0.1.6-rc.2");

        Assert.Contains("0.1.6-rc.2", message, StringComparison.Ordinal);
        Assert.Contains("Обновление", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ТА ЖЕ ИЛИ СТАРШЕ — молчим: «обновите то, что уже стоит» — это ложь.</summary>
    [Fact]
    public void Та_же_или_старше_молчим()
    {
        Assert.False(EngineUpdateNotice.ShouldAnnounce("0.1.5-rc.2", "0.1.5-rc.2", announced: string.Empty));
        Assert.False(EngineUpdateNotice.ShouldAnnounce("0.1.5-rc.2", "0.1.4-rc.2", announced: string.Empty));
    }

    /// <summary>ПРО ЭТУ ВЕРСИЮ УЖЕ СКАЗАЛИ — второй раз не пристаём (правило «один раз на версию»).</summary>
    [Fact]
    public void Про_сказанную_версию_молчим()
    {
        Assert.False(EngineUpdateNotice.ShouldAnnounce("0.1.5-rc.2", "0.1.6-rc.2", announced: "0.1.6-rc.2"));

        // А про следующую — снова говорим: «один раз на версию» не значит «один раз навсегда».
        Assert.True(EngineUpdateNotice.ShouldAnnounce("0.1.5-rc.2", "0.1.7-rc.2", announced: "0.1.6-rc.2"));
    }

    /// <summary>ДВИЖКА НЕТ ВОВСЕ ИЛИ НЕЧЕГО РЕКОМЕНДОВАТЬ — молчим: сравнивать не с чем.</summary>
    [Fact]
    public void Без_движка_и_без_рекомендации_молчим()
    {
        Assert.False(EngineUpdateNotice.ShouldAnnounce(installed: string.Empty, recommended: "0.1.6-rc.2", announced: string.Empty));
        Assert.False(EngineUpdateNotice.ShouldAnnounce(installed: "0.1.5-rc.2", recommended: string.Empty, announced: string.Empty));
        Assert.False(EngineUpdateNotice.ShouldAnnounce(installed: null, recommended: null, announced: null));
    }

    /// <summary>
    /// СРАВНЕНИЕ ИДЁТ ПО ЧИСЛОВОЙ ЧАСТИ — правило панели, названное прямо в
    /// <see cref="EngineUpdateDecisions.FreshIsNewer"/>: предрелизные хвосты им НЕ различаются
    /// (`0.1.5-rc.2` и `0.1.5` — «одна версия»), а решает старшая часть (`0.2.0` против `0.1.5`).
    /// Панель не имеет права выдумывать здесь своё правило: решает проба, а не хвост номера.
    /// </summary>
    [Fact]
    public void Сравнение_идёт_по_числовой_части()
    {
        Assert.False(EngineUpdateNotice.ShouldAnnounce("0.1.5", "0.1.5-rc.2", announced: string.Empty));
        Assert.False(EngineUpdateNotice.ShouldAnnounce("0.1.5-rc.2", "0.1.5", announced: string.Empty));
        Assert.True(EngineUpdateNotice.ShouldAnnounce("0.1.5-rc.2", "0.2.0-rc.2", announced: string.Empty));
    }
}
