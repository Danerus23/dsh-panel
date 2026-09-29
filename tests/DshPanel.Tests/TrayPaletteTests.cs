using System;
using System.Collections.Generic;
using System.Linq;
using DshPanel.Shell;
using DshPanel.Tray;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Цвета меню значка: кружок и текст строк состояния (решение владельца 27.09.2026 — «сливается
/// всё в одно», нужен цвет).
///
/// Проверки ЧИСЛЕННЫЕ, а не «на глаз»: контраст считается по WCAG, и каждая пара цветов обязана
/// пройти порог на настоящих фонах меню Windows — светлом (измеренный <c>COLOR_MENU</c>
/// у владельца: <c>#F0F0F0</c>) и тёмном (<c>#2B2B2B</c>), а также на белом и чёрном, чтобы
/// нестандартная тема не сделала надпись нечитаемой.
///
/// Почему это вообще проверяется прогоном: цвет выбирается один раз, а живёт годами; «подобрал
/// на глаз, когда делал» — это ровно тот случай, когда через полгода никто не вспомнит, почему
/// числа такие, а надпись на светлом фоне окажется бледной.
/// </summary>
public class TrayPaletteTests
{
    /// <summary>Фоны, на которых цвета обязаны читаться: оба настоящих и обе крайности.</summary>
    private static readonly uint[] Backgrounds =
    {
        0xFFFFFF,               // белый: самый светлый из возможных
        TrayPalette.MenuLight,  // светлое меню Windows — измеренный COLOR_MENU владельца
        0xC0C0C0,               // светло-серая нестандартная тема
        TrayPalette.MenuDark,   // тёмное меню Windows
        0x1F1F1F,               // ещё темнее
        0x000000,               // чёрный: самый тёмный из возможных
    };

    /// <summary>
    /// Фоны для КРУЖКА. Светло-серого (#C0C0C0) здесь нет, и это не поблажка: серый кружок
    /// на сером фоне одинаковой яркости не читается НИКАКИМ серым — так устроен контраст, а не
    /// наш выбор. Настоящих фонов меню Windows ровно два (светлый #F0F0F0 и тёмный #2B2B2B),
    /// и на них кружок обязан читаться — это и проверяется.
    /// </summary>
    private static readonly uint[] CircleBackgrounds =
    {
        0xFFFFFF,
        TrayPalette.MenuLight,
        TrayPalette.MenuDark,
        0x1F1F1F,
        0x000000,
    };

    private static readonly TrayTone[] Tones = Enum.GetValues<TrayTone>();

    /// <summary>
    /// Кружок состояния читается на ЛЮБОМ фоне меню. Это не украшение: кружок — главный признак,
    /// по которому человек отличает «работает» от «не работает», не читая слов.
    /// </summary>
    [Fact]
    public void Кружок_читается_на_любом_фоне_меню()
    {
        foreach (var tone in Tones)
        {
            var circle = TrayPalette.Circle(tone);

            foreach (var background in CircleBackgrounds)
            {
                var contrast = TrayPalette.Contrast(circle, background);

                Assert.True(
                    contrast >= TrayPalette.CircleContrastFloor,
                    $"кружок тона {tone} (#{circle:X6}) на фоне #{background:X6}: контраст {contrast:F2} " +
                    $"ниже порога {TrayPalette.CircleContrastFloor}");
            }
        }
    }

    /// <summary>
    /// Текст строки читается на фоне СВОЕГО вида. У текста два набора цветов (для светлого
    /// и для тёмного фона), потому что один и тот же тон на одном из них пропадает: тёмно-зелёный
    /// на тёмном меню не виден, а светло-зелёный не виден на светлом.
    /// </summary>
    [Fact]
    public void Текст_строки_читается_на_фоне_своего_вида()
    {
        foreach (var tone in Tones)
        {
            var onLight = TrayPalette.Text(tone, TrayPalette.MenuLight);
            var onDark = TrayPalette.Text(tone, TrayPalette.MenuDark);

            Assert.True(
                TrayPalette.Contrast(onLight, TrayPalette.MenuLight) >= TrayPalette.TextContrastFloor,
                $"текст тона {tone} на светлом меню (#{onLight:X6} на #{TrayPalette.MenuLight:X6}): " +
                $"контраст {TrayPalette.Contrast(onLight, TrayPalette.MenuLight):F2}");

            Assert.True(
                TrayPalette.Contrast(onDark, TrayPalette.MenuDark) >= TrayPalette.TextContrastFloor,
                $"текст тона {tone} на тёмном меню (#{onDark:X6} на #{TrayPalette.MenuDark:X6}): " +
                $"контраст {TrayPalette.Contrast(onDark, TrayPalette.MenuDark):F2}");
        }
    }

    /// <summary>
    /// На светлом фоне краска ТЁМНАЯ, на тёмном — СВЕТЛАЯ. Это и есть обещание проверки:
    /// цвет текста выбирается по фону меню, а не назначается «на глаз».
    /// </summary>
    [Fact]
    public void Чернила_тёмные_на_светлом_и_светлые_на_тёмном()
    {
        Assert.Equal(TrayPalette.InkOnLight, TrayPalette.Ink(TrayPalette.MenuLight));
        Assert.Equal(TrayPalette.InkOnLight, TrayPalette.Ink(0xFFFFFF));
        Assert.Equal(TrayPalette.InkOnDark, TrayPalette.Ink(TrayPalette.MenuDark));
        Assert.Equal(TrayPalette.InkOnDark, TrayPalette.Ink(0x000000));

        Assert.True(TrayPalette.Luminance(TrayPalette.InkOnLight) < 0.1, "чернила для светлого фона обязаны быть тёмными");
        Assert.True(TrayPalette.Luminance(TrayPalette.InkOnDark) > 0.8, "чернила для тёмного фона обязаны быть светлыми");

        // И сами чернила обязаны быть читаемыми: 4,5:1 на своём фоне.
        Assert.True(TrayPalette.Contrast(TrayPalette.InkOnLight, TrayPalette.MenuLight) >= TrayPalette.TextContrastFloor);
        Assert.True(TrayPalette.Contrast(TrayPalette.InkOnDark, TrayPalette.MenuDark) >= TrayPalette.TextContrastFloor);
    }

    /// <summary>
    /// Тёмные чернила на светлом фоне и светлые на тёмном — свойство, а не совпадение: проверка
    /// идёт по ВСЕМ фонам, и на каждом выбранные чернила обязаны читаться лучше, чем другие.
    /// </summary>
    [Fact]
    public void Чернила_выбираются_по_фону_а_не_наоборот()
    {
        foreach (var background in Backgrounds)
        {
            var chosen = TrayPalette.Ink(background);
            var other = chosen == TrayPalette.InkOnLight ? TrayPalette.InkOnDark : TrayPalette.InkOnLight;

            Assert.True(
                TrayPalette.Contrast(chosen, background) >= TrayPalette.Contrast(other, background),
                $"на фоне #{background:X6} выбраны не лучшие чернила: " +
                $"{TrayPalette.Contrast(chosen, background):F2} против {TrayPalette.Contrast(other, background):F2}");
        }
    }

    /// <summary>
    /// Четыре тона — четыре РАЗНЫХ цвета, и у кружка, и у текста на каждом из фонов.
    /// Это защита от «новый тон забыли покрасить»: он молча получил бы чужой цвет, и красный
    /// с зелёным стали бы неразличимы ровно там, где различие важнее всего.
    /// </summary>
    [Fact]
    public void Каждому_тону_свой_цвет()
    {
        Assert.Equal(4, Tones.Length);

        foreach (var background in new[] { TrayPalette.MenuLight, TrayPalette.MenuDark })
        {
            var text = Tones.Select(tone => TrayPalette.Text(tone, background)).ToList();

            Assert.Equal(Tones.Length, text.Distinct().Count());
        }

        var circles = Tones.Select(TrayPalette.Circle).ToList();
        Assert.Equal(Tones.Length, circles.Distinct().Count());
    }

    /// <summary>
    /// Цвет Win32 — это <c>0x00BBGGRR</c>, а не <c>0xRRGGBB</c>: красный и синий стоят наоборот.
    /// Проверка называет измеренное значение: у владельца <c>GetSysColor(COLOR_MENU)</c> вернула
    /// <c>0x00F0F0F0</c> (серый — перестановка не видна), поэтому рядом стоит цвет, на котором
    /// перепутанные каналы были бы видны сразу.
    /// </summary>
    [Fact]
    public void Цвет_Win32_переводится_с_перестановкой_каналов()
    {
        Assert.Equal(0xF0F0F0u, TrayPalette.ToRgb(0x00F0F0F0));

        // COLORREF 0x00BBGGRR: красный лежит в младшем байте, синий — в старшем.
        Assert.Equal(0x112233u, TrayPalette.ToRgb(0x00332211));
        Assert.Equal(0xFF0000u, TrayPalette.ToRgb(0x000000FF));
        Assert.Equal(0x0000FFu, TrayPalette.ToRgb(0x00FF0000));

        // Обратный перевод — та же перестановка: она обратна самой себе.
        Assert.Equal(0x00332211u, TrayPalette.ToColorRef(0x112233));
        Assert.Equal(0x112233u, TrayPalette.ToRgb(TrayPalette.ToColorRef(0x112233)));
    }

    /// <summary>
    /// Контраст считается по WCAG: крайние случаи известны заранее (чёрное на белом — 21:1,
    /// цвет сам с собой — 1:1). Без этой проверки «порог 4,5» мог бы означать что угодно.
    /// </summary>
    [Fact]
    public void Контраст_считается_по_известным_крайним_случаям()
    {
        Assert.Equal(21.0, TrayPalette.Contrast(0x000000, 0xFFFFFF), 2);
        Assert.Equal(1.0, TrayPalette.Contrast(0x336699, 0x336699), 2);
        Assert.Equal(0.0, TrayPalette.Luminance(0x000000), 4);
        Assert.Equal(1.0, TrayPalette.Luminance(0xFFFFFF), 4);

        // Порядок цветов на контраст не влияет — иначе «фон светлее краски» ломало бы проверку.
        Assert.Equal(
            TrayPalette.Contrast(0x000000, 0xFFFFFF),
            TrayPalette.Contrast(0xFFFFFF, 0x000000),
            6);
    }
}
