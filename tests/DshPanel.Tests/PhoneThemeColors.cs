using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЦВЕТА ОБЕИХ ТЕМ ПАЛИТРЫ — ОДНИМ РАЗБОРОМ ТАБЛИЦЫ СТИЛЕЙ, ДЛЯ ВСЕХ ПРОВЕРОК.
///
/// **От какого случая (CH-04 цикла C1, пачка D).** Контраст обычного текста считался на
/// **литералах** (`#1A1A1A` на `#FFFFFF`) в двух проверках — `PhoneLookTests` и `PhonePageTests`, —
/// хотя комментарий рядом обещал «значения берутся ИЗ САМОЙ ТАБЛИЦЫ СТИЛЕЙ». Литералы совпадали
/// с палитрой **совпадением, а не проверкой**: поменяй кто-нибудь `--ink` на серый, и обе проверки
/// остались бы зелёными, потому что сравнивали бы старые числа сами с собой. Палитру сторожил
/// третий класс (`PhoneAccessibilityTests`), но он проверяет СВОИ пары, а не те, что написаны
/// в этих двух.
///
/// **Что здесь.** Один разбор `PhonePageStyle.Css` на две темы: `:root` — светлая, блок
/// `@media (prefers-color-scheme: dark)` — тёмная. Ссылки `var(--x)` разворачиваются: тёмная тема
/// переопределяет не все имена и наследует светлые.
///
/// ⚠️ **Разбор ОДИН на весь набор проверок.** Второй такой же означал бы, что однажды поправят
/// один и «палитра» в двух проверках станет разной. Разбор переехал сюда из
/// `PhoneAccessibilityTests` (там он и был написан) без изменения поведения — вместе с его счётом
/// имён: двенадцать записей в каждой теме (одиннадцать цветов плюс `--theme` — имя для сообщений).
/// </summary>
internal static class PhoneThemeColors
{
    /// <summary>Имя темы для сообщений проверок — лежит в карте вместе с цветами.</summary>
    public const string ThemeKey = "--theme";

    /// <summary>
    /// ОБЕ ТЕМЫ: [0] — светлая, [1] — тёмная. Карта «переменная → цвет» плюс <see cref="ThemeKey"/>.
    ///
    /// Порядок фиксирован и назван здесь: проверки берут светлую по индексу 0, тёмную по 1 — так же,
    /// как их назвал разбор. Искать «где тёмная» по имени в каждой проверке значило бы завести
    /// второе правило поиска.
    /// </summary>
    public static IReadOnlyList<Dictionary<string, string>> Themes()
    {
        var css = PhonePageStyle.Css;

        var light = new Dictionary<string, string>(StringComparer.Ordinal) { [ThemeKey] = "светлая тема" };
        var dark = new Dictionary<string, string>(StringComparer.Ordinal) { [ThemeKey] = "тёмная тема" };

        var start = css.IndexOf(":root{", StringComparison.Ordinal);
        Assert.True(start >= 0, "в таблице стилей нет :root со значениями палитры");

        var end = css.IndexOf('}', start);
        Assert.True(end > start, "правило :root не закрыто");

        Fill(light, css.Substring(start, end - start + 1), css);

        var darkStart = css.IndexOf("@media (prefers-color-scheme: dark){", StringComparison.Ordinal);
        Assert.True(darkStart >= 0, "в таблице стилей нет тёмной темы — тёмных кадров быть не может");

        var darkRoot = css.IndexOf(":root{", darkStart, StringComparison.Ordinal);
        var darkEnd = css.IndexOf('}', darkRoot);

        // ССЫЛКИ ТЁМНОЙ ТЕМЫ РАЗВОРАЧИВАЮТСЯ ПО СВЕТЛОЙ: тёмная переопределяет не всё
        // (`--accent-ink` ссылается на `--ink`, `--accent-wash` — на `--ink-2`), и без второго
        // словаря цвета остались бы нерешёнными.
        Fill(dark, css.Substring(darkRoot, darkEnd - darkRoot + 1), css, light);

        // ОДИННАДЦАТЬ ЦВЕТОВЫХ ПЕРЕМЕННЫХ В КАЖДОЙ ТЕМЕ (плюс `--theme` — имя для сообщений):
        // `--page`, `--card`, `--ink`, `--ink-2`, `--line`, `--accent`, `--accent-ink`,
        // `--accent-wash`, `--focus`, `--warn`, `--bad`. Меньше — значит тема не переопределила
        // цвет и унаследовала чужой; больше — значит в разбор приехало что-то незамеченное.
        Assert.True(
            12 <= light.Count,
            "светлая: " + string.Join(",", light.Keys.OrderBy(k => k, StringComparer.Ordinal)));

        Assert.Equal(12, light.Count);
        Assert.Equal(12, dark.Count);

        return new List<Dictionary<string, string>> { light, dark };
    }

    /// <summary>Значения одной темы: цвета — как есть, ссылки на переменные — разворачиваются.</summary>
    private static void Fill(
        Dictionary<string, string> theme, string block, string css, Dictionary<string, string>? fallback = null)
    {
        // ⚠️ Блок приходит ВМЕСТЕ с селектором (`:root{…}`): его надо снять, иначе первая
        // переменная (`--page`) останется склеенной с `:root` и молча выпадет из разбора —
        // ровно так и вышло в первом прогоне этой проверки, и заметил это её счёт.
        var open = block.IndexOf('{');
        var close = block.LastIndexOf('}');

        var body = open >= 0 && close > open ? block[(open + 1)..close] : block;

        foreach (var chunk in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = chunk.Split(':', 2);

            if (parts.Length != 2) continue;

            var name = parts[0].Trim();
            var value = parts[1].Trim();

            if (!name.StartsWith("--", StringComparison.Ordinal)) continue;

            if (value.StartsWith("var(", StringComparison.Ordinal))
            {
                var inner = value.Substring(4).TrimEnd(')');

                value = theme.TryGetValue(inner, out var resolved) ? resolved
                    : fallback is not null && fallback.TryGetValue(inner, out var inherited) ? inherited
                    : Resolve(css, inner);
            }

            theme[name] = value;
        }
    }

    /// <summary>Значение переменной из СВЕТЛОЙ темы — для ссылок тёмной темы на светлую.</summary>
    private static string Resolve(string css, string name)
    {
        var match = Regex.Match(css, Regex.Escape(name) + @":(#[0-9A-Fa-f]{6,8})");

        Assert.True(match.Success, $"в таблице стилей нет переменной {name}");

        return match.Groups[1].Value;
    }
}
