using System.Text.RegularExpressions;

namespace DshPanel.Restore;

/// <summary>
/// Файл ключа движка (<c>~/.dsh/.credentials.yaml</c>) — ровно в той части, которая нужна панели:
/// отличить «в файле не лежит ни одного ключа» от «в файле что-то есть».
///
/// Зачем это панели. Ключ читает сам движок, и на файл, где у ключа ПУСТОЕ значение, он падает
/// целиком: «the value for … is empty; remove the key instead» (dsh-credentials-local). Старый
/// («плоский») формат файла движок переводит в новый сам — но только если значения непустые:
/// на пустом он миграцию не признаёт и всё равно падает. Прогон в ВМ 23.09.2026 кончился именно
/// этим: в пробной копии лежал файл из 11 байт (<c>token: ""</c>), накат вернул его на место —
/// и сервер перестал подниматься вовсе, а наружу выходила только трассировка на тысячи строк.
///
/// Отсюда правило, и оно красной линией записано в `AGENTS.md`: **накат не возвращает файл ключа,
/// который не хранит ни одного ключа.** Отсутствующий файл для движка и есть «ключей нет»,
/// поэтому пропуск ничего не теряет.
///
/// ⚠️ **Перенесено из v1 «как есть»** (`..\dsh-tray\CredentialsFile.cs`, шаг 15 порядка переноса):
/// тела не менялись ни на символ. Это не формальность — это строгий разбор, оплаченный двумя
/// случаями (и тем, что файл нельзя удалить у человека зря), и «улучшать» его при переносе значит
/// переоткрывать те же грабли.
///
/// Понимаются ОБЕ раскладки: и плоская (<c>token: ""</c>), и version 1 (<c>version: 1</c> +
/// <c>refs:</c> с пустыми значениями, в том числе потоковой записью <c>refs: {token: ""}</c>) —
/// у обеих движок падает одинаково, и у обеих удаление ничего не теряет.
///
/// Проверка нарочно строгая и «закрывается при сомнении»: если хоть одно значение непустое или
/// хоть одна строка непонятна (список, блочное значение, директива <c>%YAML</c>, незнакомая версия
/// документа, <c>records</c> с содержимым), ответ — «в файле что-то есть», и панель его не трогает.
/// Потерять ключ человека хуже, чем показать ему ошибку движка.
///
/// ⚠️ **Второй пользователь этого класса — пуск сервера.** Перед запуском движка панель обязана
/// убрать такой файл (иначе сервер не поднимется вовсе). Это работа того шага, где живёт
/// `ServerController`; здесь класс стоит потому, что первым его спросил накат.
/// Родственный, но ДРУГОЙ вопрос — «какое значение у ключа» — решает `Balance\CredentialsKey.cs`.
/// </summary>
public static class CredentialsFile
{
    /// <summary>Строка «ключ: значение»: имя без двоеточия (и без пробела/решётки в начале), затем двоеточие.</summary>
    private static readonly Regex EntryLine = new(@"^([^:\s#][^:]*):(.*)$", RegexOptions.Compiled);

    /// <summary>Пара внутри потоковой записи: <c>{token: "", …}</c>.</summary>
    private static readonly Regex FlowPair = new(@"^\s*([^:\s#{}\[\],]+)\s*:(.*)$", RegexOptions.Compiled);

    /// <summary>Запись без значения: так YAML записывает «ничего».</summary>
    private static readonly string[] NullWords = { "~", "null", "Null", "NULL" };

    /// <summary>Ключи раскладки version 1: сами по себе они не значение, а разметка документа.</summary>
    private static readonly string[] LayoutKeys = { "version", "refs", "records" };

    /// <summary>
    /// Хранит ли документ хотя бы один ключ. Ложь означает «ни одного»: у всех записей значение
    /// пустое, и файл можно считать отсутствующим.
    /// </summary>
    public static bool StoresNothing(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        var entries = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;

            // Директивы документа: `---` открывает документ, `...` закрывает. Раскладки они не
            // меняют, а движок из-за них плоскую раскладку не признаёт — то есть файл тем вернее
            // не читается. Содержимым не считаем.
            if (line is "---" or "...") continue;

            // Список, блочное значение, директива `%YAML` — это уже не карта «ключ: значение».
            // Непонятное не трогаем.
            if (line.StartsWith("-", StringComparison.Ordinal)) return false;
            if (line.Contains('|') || line.Contains('>') || line.StartsWith("%", StringComparison.Ordinal)) return false;

            // Потоковая запись целиком: `{token: ""}` или `{token: "", other: ""}`.
            if (line.StartsWith("{", StringComparison.Ordinal) && line.EndsWith("}", StringComparison.Ordinal))
            {
                if (!FlowMapIsEmpty(line[1..^1], ref entries)) return false;
                continue;
            }

            var match = EntryLine.Match(line);
            if (!match.Success) return false;

            var key = match.Groups[1].Value.Trim();
            var value = match.Groups[2].Value;

            if (IsLayoutKey(key))
            {
                if (string.Equals(key, "version", StringComparison.Ordinal))
                {
                    // Разметка, а не значение: версия обязана быть ровно 1 (иная — чужой документ).
                    if (value.Trim() != "1") return false;
                    continue;
                }

                // `refs:` и `records:` — заголовки разделов; они могут быть и потоковой записью.
                if (!IsEmptyValue(value) && !IsFlowMap(value, ref entries)) return false;
                continue;
            }

            entries++;
            if (!IsEmptyValue(value)) return false;
        }

        // Ни одной записи (только комментарии, или `version: 1` с пустыми разделами) — файл ничего
        // не хранит, но и повода его трогать нет: движок такой документ читает как пустое
        // хранилище. Оставляем как есть — панель вмешивается только там, где движок падает.
        return entries > 0;
    }

    /// <summary>Есть ли по этому пути файл ключа, который не хранит ни одного ключа.</summary>
    public static bool ExistsAndStoresNothing(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;   // файла нет — и повода что-то делать нет
            return StoresNothing(File.ReadAllText(path));
        }
        catch
        {
            // Не прочитали (занят, кодировка, права) — считаем, что файл содержательный.
            return false;
        }
    }

    private static bool IsLayoutKey(string key)
    {
        foreach (var layout in LayoutKeys)
        {
            if (string.Equals(key, layout, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>Потоковая запись в значении: <c>refs: {token: ""}</c>. Проверяет её на пустоту.</summary>
    private static bool IsFlowMap(string value, ref int entries)
    {
        var trimmed = value.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}') return false;

        return FlowMapIsEmpty(trimmed[1..^1], ref entries);
    }

    /// <summary>Всё ли внутри потоковой записи — пустые значения. Считает пары как записи.</summary>
    private static bool FlowMapIsEmpty(string inside, ref int entries)
    {
        var parts = inside.Split(',');
        if (inside.Trim().Length == 0) return true;   // `{}` — пустая карта

        foreach (var part in parts)
        {
            var match = FlowPair.Match(part);
            if (!match.Success) return false;

            entries++;
            if (!IsEmptyValue(match.Groups[2].Value)) return false;
        }

        return true;
    }

    /// <summary>
    /// Пустое ли значение записи. Пусто — это ничего, кавычки БЕЗ содержимого, «ничто» по YAML и
    /// хвостовой комментарий после всего этого.
    ///
    /// Две осторожности, обе в сторону «оставить файл»:
    ///  • кавычки с пробелами внутри (<c>token: " "</c>) пустыми НЕ считаем, хотя выглядят так же:
    ///    движок принимает такую строку за настоящее значение, и стирать читаемый файл панель не
    ///    имеет права;
    ///  • решётка считается комментарием только в начале значения или после пробела. В YAML
    ///    `""#x` — не комментарий, а часть значения (нашёл аудит 23.09.2026), и посчитать такой
    ///    файл пустым значило бы удалить его у человека зря.
    /// </summary>
    private static bool IsEmptyValue(string value)
    {
        var trimmed = value.Trim();

        var hash = trimmed.IndexOf('#');
        if (hash == 0 || (hash > 0 && char.IsWhiteSpace(trimmed[hash - 1])))
        {
            trimmed = trimmed[..hash].Trim();
        }

        if (trimmed.Length == 0) return true;

        foreach (var word in NullWords)
        {
            if (string.Equals(trimmed, word, StringComparison.Ordinal)) return true;
        }

        if (trimmed.Length >= 2)
        {
            var quote = trimmed[0];
            if ((quote == '"' || quote == '\'') && trimmed[^1] == quote
                && trimmed[1..^1].Length == 0) return true;
        }

        return false;
    }
}
