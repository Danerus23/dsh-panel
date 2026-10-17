using System;
using DshPanel.Localization;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ИМЯ ТЕЛЕФОНА — ЧИСТОЕ РЕШЕНИЕ (решение владельца 12.10.2026).
///
/// **Зачем класс.** Владелец сказал: показывать **короткое имя телефона**, а не внутренний `phone-N`.
/// Источник имени один — <c>User-Agent</c>, который телефон присылает при сопряжении. Здесь
/// проверяется РАЗБОР этой строки: он консервативный (незнакомая строка даёт пустоту, а не
/// выдуманное имя), ограниченный по длине и ничего не решающий про доступ.
///
/// ⚠️ **Строки <c>User-Agent</c> здесь — ПОДСТАВНЫЕ**, а не замер с телефона владельца: его
/// телефона у прогона нет и быть не может (личные данные, красная линия 4). Это обычные строки
/// браузеров, какие объявляют себя сами; живое сопряжение проверяется на телефоне владельца.
/// </summary>
public class PhoneDeviceNameTests
{
    [Fact]
    public void Айфон_называется_айфоном_в_любом_браузере()
    {
        // Safari, Chrome (CriOS) и Firefox (FxiOS) на iPhone — модель всё равно iPhone.
        Assert.Equal("iPhone", PhoneDecisions.DeviceName(
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1"));

        Assert.Equal("iPhone", PhoneDecisions.DeviceName(
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/126.0.6478.54 Mobile/15E148 Safari/604.1"));

        Assert.Equal("iPhone", PhoneDecisions.DeviceName(
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/127.0 Mobile/15E148 Safari/605.1.15"));

        Assert.Equal("iPad", PhoneDecisions.DeviceName(
            "Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1"));
    }

    [Fact]
    public void Андроид_называется_моделью_а_не_словом_андроид()
    {
        Assert.Equal("SM-S911B", PhoneDecisions.DeviceName(
            "Mozilla/5.0 (Linux; Android 13; SM-S911B Build/TP1A.220624.014) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36"));

        Assert.Equal("Pixel 8", PhoneDecisions.DeviceName(
            "Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/AP1A.240505.004) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36"));

        // Модели нет (браузер сам не назвался) — честное «Android», а не «wv» и не пустота.
        Assert.Equal("Android", PhoneDecisions.DeviceName(
            "Mozilla/5.0 (Linux; Android 10; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/126.0 Mobile Safari/537.36"));

        Assert.Equal("Android", PhoneDecisions.DeviceName(
            "Mozilla/5.0 (Linux; Android 10) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Mobile Safari/537.36"));
    }

    /// <summary>
    /// НЕЗНАКОМАЯ СТРОКА — ПУСТОТА, А НЕ ДОГАДКА: выдуманное имя хуже отсутствия имени.
    ///
    /// Мутации, которые обязаны упасть здесь: «брать первые два слова строки» · «вернуть `Unknown`» ·
    /// «вернуть саму строку» · «принять пустоту за имя».
    /// </summary>
    [Fact]
    public void Незнакомая_строка_даёт_пустоту_а_не_догадку()
    {
        Assert.Equal(string.Empty, PhoneDecisions.DeviceName(null));
        Assert.Equal(string.Empty, PhoneDecisions.DeviceName(string.Empty));
        Assert.Equal(string.Empty, PhoneDecisions.DeviceName("   "));
        Assert.Equal(string.Empty, PhoneDecisions.DeviceName("curl/8.7.1"));
        Assert.Equal(string.Empty, PhoneDecisions.DeviceName("Mozilla/5.0 (X11; Linux x86_64) Firefox/127.0"));
        Assert.Equal(string.Empty, PhoneDecisions.DeviceName("<script>alert(1)</script>"));
    }

    /// <summary>
    /// ДЛИННОЕ ИМЯ ОБРЕЗАЕТСЯ ПО ЧИСЛУ, А НЕ «КАК ВЫШЛО»: имя стоит тихой строкой на странице
    /// вопроса, и длинная фраза вытеснила бы с экрана сам вопрос.
    ///
    /// Мутации, которые обязаны упасть здесь: «снять потолок» · «обрезать посередине слова».
    /// </summary>
    [Fact]
    public void Длинное_имя_обрезается_по_числу_знаков()
    {
        var overlong = new string('A', 40);

        Assert.Equal(PhoneDecisions.DeviceNameLimit, PhoneDecisions.DeviceDisplay(overlong, "телефон").Length);

        var phrase = PhoneDecisions.DeviceDisplay("Samsung Galaxy S23 Ultra 5G Enterprise Edition", "телефон");

        Assert.True(phrase.Length <= PhoneDecisions.DeviceNameLimit, $"имя «{phrase}» длиннее потолка");
        Assert.DoesNotContain("  ", phrase, StringComparison.Ordinal);
        Assert.False(phrase.EndsWith(' '), "имя кончается пробелом");

        // А КОРОТКОЕ НЕ ТРОГАЕТСЯ ВООБЩЕ.
        Assert.Equal("Pixel 8", PhoneDecisions.DeviceDisplay("Pixel 8", "телефон"));
        Assert.Equal("Pixel 8", PhoneDecisions.DeviceDisplay("  Pixel 8  ", "телефон"));
    }

    /// <summary>
    /// ИМЕНИ НЕТ — ГОВОРИТСЯ ЧЕСТНОЕ «телефон» (решение владельца 12.10.2026), и это слово
    /// есть в ТРЁХ языках. Внутренний `phone-N` человеку не показывается ни при каком исходе.
    /// </summary>
    [Fact]
    public void Без_имени_говорится_честное_телефон()
    {
        foreach (var language in new[] { "ru", "en", "zh" })
        {
            var fallback = Loc.TIn(language, nameof(PanelStrings.PhonePageDeviceFallback));

            Assert.False(string.IsNullOrWhiteSpace(fallback), $"{language}: честного «телефон» нет");

            Assert.Equal(fallback, PhoneDecisions.DeviceDisplay(string.Empty, fallback));
            Assert.Equal(fallback, PhoneDecisions.DeviceDisplay("   ", fallback));

            // И САМА СТРОКА УСТРОЙСТВА — БЕЗ ВНУТРЕННЕГО НОМЕРА.
            Assert.DoesNotContain("phone-", fallback, StringComparison.Ordinal);
        }

        Assert.Equal("телефон", Loc.TIn("ru", nameof(PanelStrings.PhonePageDeviceFallback)));
    }
}
