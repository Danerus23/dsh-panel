using System.Text.RegularExpressions;
using DshPanel.Shell;

namespace DshPanel.Balance;

/// <summary>Что вышло из поиска ключа: само значение (пусто — не нашли) и объяснение для человека.</summary>
public readonly record struct KeyLookup(string Value, string Problem)
{
    public bool Found => Value.Length > 0;
}

/// <summary>
/// Ключ модели из файла ключей движка (<c>~/.dsh/.credentials.yaml</c>).
///
/// Панель ключ НЕ хранит и никуда не пишет: она читает его в момент запроса и держит в памяти
/// ровно столько, сколько идёт запрос. Это решение владельца 24.09.2026 — «читай спокойно
/// <c>~/.dsh/.credentials.yaml</c> для разработки и тестирования» — и одновременно правило
/// продукта: ключ не попадает ни в интерфейс, ни в журнал, ни в отчёт проверки.
///
/// Понимаются ОБЕ раскладки файла, которые пишет движок: плоская (<c>DEEPSEEK_API_KEY: …</c>)
/// и версии 1 (<c>records:</c> со списком, где рядом лежит <c>refs:</c> с тем же ключом) —
/// потому что ищем строку с именем ключа, а не разбираем YAML целиком. Так же делала v1
/// (<c>BalanceService.ReadApiKey</c>), и это единственный разбор, который пережил смену
/// раскладки файла движком.
///
/// Значение в объяснениях не появляется НИКОГДА: в текст идёт имя ключа и путь (маскированный).
/// </summary>
public static class CredentialsKey
{
    public static KeyLookup Read(string path, string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return new KeyLookup(string.Empty, PanelStrings.BalanceNoKeyFormat);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new KeyLookup(string.Empty, string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.BalanceNoKeyFormat,
                keyName,
                DisplayMask.Path(path)));
        }

        try
        {
            var text = File.ReadAllText(path);
            var pattern = new Regex(
                @"^\s*" + Regex.Escape(keyName) + @"\s*:\s*(?<value>.+?)\s*$",
                RegexOptions.Multiline);

            foreach (Match match in pattern.Matches(text))
            {
                var value = Unquote(match.Groups["value"].Value);
                if (value.Length > 0) return new KeyLookup(value, string.Empty);
            }

            return new KeyLookup(string.Empty, string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.BalanceNoKeyFormat,
                keyName,
                DisplayMask.Path(path)));
        }
        catch (Exception ex)
        {
            // Файл занят или не читается — это не «ключа нет», но и показать человеку нечего,
            // кроме причины. Значения здесь быть не может по построению.
            return new KeyLookup(string.Empty, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Кавычки вокруг значения — часть синтаксиса YAML, а не ключа.</summary>
    private static string Unquote(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0])
            text = text[1..^1];

        return text.Trim();
    }
}
