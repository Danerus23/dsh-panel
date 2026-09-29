using System.Text.Json;
using DshPanel.Shell;

namespace DshPanel.Settings;

/// <summary>Готовая к предложению рабочая папка: сам путь и откуда он взялся.</summary>
public readonly record struct WorkDirSuggestion(string Path, string Source);

/// <summary>
/// Откуда взять рабочую папку, которую человек уже использует.
///
/// Требование владельца 24.09.2026: панель может попасть к человеку, у которого окружение уже
/// настроено, и тогда она обязана **предложить** его рабочую папку, а не молча подставить свою.
///
/// ⚠️ **Источник ровно один — настройки панели 1.x** (`serverWorkingDir` в её `settings.json`).
/// У самого движка рабочей папки в конфиге НЕТ: проверено 24.09.2026 по ключам
/// `~/.dsh/settings.yaml` (там модель, провайдер, тема, онбординг — ни каталога, ни рабочей
/// папки). Рабочий каталог `dsh web` — это просто тот каталог, из которого его запустили,
/// и нигде не записан. Поэтому «предложить найденную папку» пока означает «предложить то,
/// что помнит прежняя панель»; когда появится встраивание в чужой DSH, источников может стать
/// больше — и это будет видно здесь, в одном месте.
///
/// Чтение настроек прежней панели — это чтение данных владельца, поэтому оно разрешено
/// **только настоящему прогону** (<paramref name="mayReadOwnerEnvironment"/>): ни изолированный
/// прогон, ни прогон проверки туда не смотрит. Иначе путь человека уехал бы в кадр или в отчёт.
/// </summary>
public static class WorkDirSuggestions
{
    /// <param name="previousPanelSettingsPath">
    /// Файл настроек панели 1.x (`%APPDATA%\DshPanel\settings.json`). Путь приходит снаружи:
    /// так проверка может подставить свой файл и не трогать настоящий.
    /// </param>
    /// <param name="directoryExists">Проверка существования каталога — тоже снаружи, чтобы решение было проверяемым.</param>
    public static IReadOnlyList<WorkDirSuggestion> Find(
        bool mayReadOwnerEnvironment,
        string previousPanelSettingsPath,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);

        if (!mayReadOwnerEnvironment) return Array.Empty<WorkDirSuggestion>();
        if (string.IsNullOrWhiteSpace(previousPanelSettingsPath)) return Array.Empty<WorkDirSuggestion>();
        if (!File.Exists(previousPanelSettingsPath)) return Array.Empty<WorkDirSuggestion>();

        var path = ReadWorkDir(previousPanelSettingsPath);
        if (path.Length == 0) return Array.Empty<WorkDirSuggestion>();
        if (!directoryExists(path)) return Array.Empty<WorkDirSuggestion>();

        return new[] { new WorkDirSuggestion(path, PanelStrings.WorkDirSourcePreviousPanel) };
    }

    /// <summary>
    /// Читает поле рабочей папки из файла прежней панели. Разбор терпимый: файл мог остаться
    /// от другой версии, и падать из-за него панель не имеет права.
    /// </summary>
    private static string ReadWorkDir(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "serverWorkingDir", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.String) return string.Empty;

                return PanelSettings.NormalizeWorkDir(property.Value.GetString());
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
