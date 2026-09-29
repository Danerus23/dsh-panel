using System.Text.RegularExpressions;

namespace DshPanel.Server;

/// <summary>
/// Что панель записывает в журнал из вывода движка.
///
/// Зачем отдельный класс, а не «пишем строку как есть». Движок печатает ссылку входа
/// со СЛУЧАЙНЫМ ТОКЕНОМ запуска (`dsh web: http://127.0.0.1:…?token=…`, `docs\ENGINE.md` §6),
/// и журнал панели — файл на диске. Токен в файле переживёт сессию и окажется в резервной
/// копии; в v1 такая ссылка лежала в `web-url.txt` и считалась секретом.
///
/// Поэтому в журнал уходит строка с вырезанным токеном, и это МАСКИРОВАНИЕ, а не удаление
/// строки: без вывода движка «сервер не поднялся» объяснить нечем.
/// </summary>
public static class EngineLog
{
    /// <summary>Токен в ссылке: <c>token=…</c> до пробела, амперсанда или кавычки.</summary>
    private static readonly Regex TokenPattern = new(
        @"token=[^\s&""']+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TokenJsonPattern = new(
        @"""token""\s*:\s*""[^""]*""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Redact(string? line)
    {
        if (string.IsNullOrEmpty(line)) return string.Empty;

        var redacted = TokenPattern.Replace(line, "token=***");
        redacted = TokenJsonPattern.Replace(redacted, "\"token\":\"***\"");
        return redacted.TrimEnd();
    }
}
