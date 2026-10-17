using System;
using System.Collections.Generic;
using DshPanel.Settings;

namespace DshPanel.Shell;

/// <summary>
/// ЧИСТЫЕ РЕШЕНИЯ СЪЁМКИ КАДРОВ: тема, состояние органа и разбор ключей вида
/// <c>--theme dark</c> / <c>--state hover:Button</c>.
///
/// **Зачем отдельный класс.** Ключи съёмки разбирает <c>Program.Shot</c>, а он у проверки
/// недостижим целиком (съёмка поднимает платформу). Здесь живёт ровно то, что можно проверить
/// **без окна и без платформы**: какое значение ключа законно, что значит «тёмная тема»
/// и не съел ли разбор сам режим съёмки. Ошибка в этом разборе положила бы в витрину не тот кадр,
/// и заметить это было бы некому — поэтому разбор проверяется отдельно.
/// </summary>
public static class ShotDecisions
{
    /// <summary>Ключ выбора темы для съёмки: <c>--theme dark</c> или <c>--theme light</c>.</summary>
    public const string ThemeSwitch = "--theme";

    /// <summary>Ключ состояния органа для съёмки: <c>--state hover:SaveButton</c>.</summary>
    public const string StateSwitch = "--state";

    /// <summary>Значение «тёмная»: съёмка обязана уметь её применить (тёмных кадров не было ни одного).</summary>
    public const string ThemeDark = "dark";

    /// <summary>Значение «светлая».</summary>
    public const string ThemeLight = "light";

    /// <summary>Как называется тема панели, которую просят кадру. Только эти два значения и есть.</summary>
    public static readonly string[] Themes = { ThemeLight, ThemeDark };

    /// <summary>
    /// Что за состояние просят у органа. **Фокус важнее наведения** (доступность): он и назван
    /// первым, и проверяется обязательно; наведение снимается, если получится.
    /// </summary>
    public const string StateFocus = "focus";

    /// <summary>Наведение мыши.</summary>
    public const string StateHover = "hover";

    /// <summary>
    /// ТЕМА ПО ЗНАЧЕНИЮ КЛЮЧА: <c>null</c> — «ключ не назвали», и тогда кадр снимается в теме
    /// панели, как раньше. Незнакомое значение — тоже <c>null</c>: вызывающий обязан отказать
    /// громко, а не «снять как получится» (кадр по опечатке уехал бы в витрину).
    /// </summary>
    public static string? Theme(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var wanted = value!.Trim();

        foreach (var theme in Themes)
        {
            if (string.Equals(theme, wanted, StringComparison.OrdinalIgnoreCase)) return theme;
        }

        return null;
    }

    /// <summary>
    /// КАКИЕ ПСЕВДОКЛАССЫ СТАВИТ ЭТО СОСТОЯНИЕ — СПИСКОМ, и это решение проверяемо.
    ///
    /// **Зачем отдельно, когда есть `if` в съёмке.** Прежде перевод «`focus` → `:focus`
    /// и `:focus-visible`» жил ветвью внутри `Program.ShotState`, куда проверке не дотянуться.
    /// Из-за этого «ветвь наведения ничего не ставит» не роняло НИЧЕГО: псевдокласс проверялся
    /// у органа, а его ставила сама проверка — то есть своя же копия. Здесь перевод назван ОДНИМ
    /// местом, и его спрашивают и съёмка, и проверка.
    ///
    /// ⚠️ **Фокус — ДВА псевдокласса**, и оба нужны: `:focus` красит орган у нас, а
    /// `:focus-visible` — то, по чему браузер и Avalonia отличают фокус с клавиатуры; без второго
    /// кадр «фокус» не совпал бы с тем, что видит человек, обошедший окно Tab'ом.
    ///
    /// Незнакомое состояние — ПУСТОЙ список (ставит ноль псевдоклассов): вызывающий обязан
    /// отказать громко, а не «снять как получилось».
    /// </summary>
    public static IReadOnlyList<string> PseudoClasses(string? state) => state switch
    {
        StateFocus => new[] { ":focus", ":focus-visible" },
        StateHover => new[] { ":pointerover" },
        _ => Array.Empty<string>(),
    };

    /// <summary>Законное ли значение названо для ключа темы.</summary>
    public static bool KnowsTheme(string? value) => Theme(value) is not null;

    /// <summary>
    /// КАКАЯ ТЕМА ПАНЕЛИ СТАВИТСЯ КАДРУ ПО ЗНАЧЕНИЮ КЛЮЧА: <c>dark</c> → тёмная, <c>light</c> →
    /// светлая, «ключ не назвали» → <c>null</c> (тема панели, как раньше).
    ///
    /// **Зачем отдельное решение, когда перевод — одна строка.** Эта строка и есть то место,
    /// где кадр получает тему; ошибка в ней (перепутаны ветви, «тёмная» ставит светлую) не видна
    /// НИЧЕМ, пока перевод живёт внутри `Program.Shot`: съёмка поднимает платформу, и проверке
    /// туда не дотянуться. Ровно так и появилась непокрытая мутация **AJ3**. Теперь перевод назван
    /// здесь, его спрашивает и съёмка, и проверка (`ShotStateTests`).
    ///
    /// ⚠️ Возвращается тема ПАНЕЛИ, а не строка ключа: съёмка ставит её приложению
    /// (`App.ApplyTheme`), и второй перевод «строка → тема» заводить нельзя.
    /// </summary>
    public static PanelTheme? Palette(string? value) => Theme(value) switch
    {
        ThemeDark => PanelTheme.Dark,
        ThemeLight => PanelTheme.Light,
        _ => null,
    };

    /// <summary>
    /// СОСТОЯНИЕ ОРГАНА ПО ЗНАЧЕНИЮ КЛЮЧА: <c>hover:ИмяОргана</c> или <c>focus:ИмяОргана</c>.
    /// Возвращает состояние и имя органа; <c>null</c> — значение не разобралось.
    ///
    /// ⚠️ Имя органа здесь — **имя элемента в разметке** (`x:Name`), а не подпись: подпись меняется
    /// языком, и по ней орган однажды нашёлся бы не тот.
    /// </summary>
    public static (string State, string Control)? State(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var parts = value!.Split(':', 2);

        if (parts.Length != 2) return null;

        var state = parts[0].Trim().ToLowerInvariant();
        var control = parts[1].Trim();

        if (control.Length == 0) return null;

        if (state != StateFocus && state != StateHover) return null;

        return (state, control);
    }

    /// <summary>Законное ли значение названо для ключа состояния.</summary>
    public static bool KnowsState(string? value) => State(value) is not null;

    /// <summary>
    /// АРГУМЕНТЫ БЕЗ ПАР <c>--theme &lt;тема&gt;</c> И <c>--state &lt;состояние&gt;</c>. Ключи
    /// снимаются ДО разбора режимов — как <c>--lang</c> и <c>--run-root</c>, — иначе разбор счёл бы
    /// их опечаткой и отказал бы кодом 2.
    ///
    /// ⚠️ Значение снимается **только у не-ключа**: иначе «<c>--theme --shot кадр.png</c>» съело бы
    /// саму съёмку, и панель открыла бы окно владельцу вместо кадра. Это то же правило, что у языка,
    /// и оно проверяется прогоном.
    /// </summary>
    public static string[] WithoutSwitches(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var rest = new List<string>(args.Length);

        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];

            if (!string.Equals(current, ThemeSwitch, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(current, StateSwitch, StringComparison.OrdinalIgnoreCase))
            {
                rest.Add(current);
                continue;
            }

            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                index++;
            }
        }

        return rest.ToArray();
    }

    /// <summary>
    /// ЗНАЧЕНИЕ КЛЮЧА ИЗ АРГУМЕНТОВ: <c>--theme dark</c> → <c>dark</c>; ключа нет или за ним
    /// стоит другой ключ — <c>null</c>.
    /// </summary>
    public static string? Value(string[] args, string key)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], key, StringComparison.OrdinalIgnoreCase)) continue;

            return index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[index + 1]
                : null;
        }

        return null;
    }
}
