using System.Reflection;

namespace DshPanel.Settings;

/// <summary>
/// Осталась ли в окне настроек НЕСОХРАНЁННАЯ правка — то, из-за чего окно обязано спросить
/// перед закрытием.
///
/// Жалоба владельца (п. 16 `docs\DESIGN.md`, слова его же): *«Вероятно требуется предупреждение
/// при закрытии окна что параметры изменены, сохранить ли перед закрытием»*. До этого крестик
/// закрывал окно, и введённое пропадало без единого слова: сохраняла только кнопка
/// «Сохранить» (`SettingsWindow.OnSave`), а закрытие не смотрело ни на что.
///
/// **Решение вынесено из окна ЧИСТОЙ функцией — и это не украшение.** В окне его нечем
/// проверить: чтобы дойти до вопроса, нужен настоящий щелчок по крестику и ответ человека.
/// Здесь же оно проверяется прогоном, который умеет падать, а само окно остаётся тонким:
/// спросить и поступить по ответу.
///
/// ⚠️ **Сравниваются НАСТОЯЩИЕ настройки, а не поля окна.** Оба значения проходят через
/// <see cref="SettingsStore.Clean"/> — ту же дверь, что и запись в файл. Иначе окно спрашивало бы
/// про правку, которой не было: например «D:\Папка\» и «D:\Папка» приводятся к одному виду
/// при сохранении, и разница между ними в поле — не изменение настройки.
/// </summary>
public static class SettingsCloseDecisions
{
    /// <summary>
    /// Поля, которые в сравнении НЕ УЧАСТВУЮТ, и у каждого своя причина.
    ///
    /// * <see cref="PanelSettings.ServerPortChosen"/> — признак «человек выбирал порт сам».
    ///   Его ставит САМА кнопка «Сохранить» (<c>SettingsWindow.PendingSettings</c>), а не человек:
    ///   он не выбирает этот признак ни в одном поле окна. Считай мы его правкой — окно
    ///   спрашивало бы «сохранить изменения?» у человека, который не менял ничего, то есть
    ///   вопрос перестали бы читать, и в следующий раз правка пропала бы молча. Это и есть
    ///   тот самый дефект, от которого эта работа и заведена, только с другой стороны.
    /// </summary>
    public static readonly string[] IgnoredFields =
    {
        nameof(PanelSettings.ServerPortChosen),
    };

    /// <summary>
    /// Есть ли что сохранять. <paramref name="pending"/> — то, что стоит в полях окна
    /// (<c>SettingsWindow.PendingSettings</c>), <paramref name="current"/> — то, что лежит
    /// в настройках сейчас.
    ///
    /// Сравниваются ВСЕ поля настроек, а не список из головы: список пришлось бы дополнять
    /// с каждым новым полем, и однажды новое поле молча выпало бы из проверки — то есть окно
    /// закрылось бы, не спросив ровно про него. Здесь такого промаха не может быть по устройству.
    /// </summary>
    public static bool HasUnsavedChanges(PanelSettings pending, PanelSettings current)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(current);

        var left = SettingsStore.Clean(pending);
        var right = SettingsStore.Clean(current);

        foreach (var property in Properties())
        {
            if (IgnoredFields.Contains(property.Name, StringComparer.Ordinal)) continue;

            if (!Same(property.GetValue(left), property.GetValue(right))) return true;
        }

        return false;
    }

    /// <summary>
    /// Поля настроек, которые УЧАСТВУЮТ в сравнении. Нужны проверке: она сверяет этот список
    /// с самим типом настроек — так «новое поле, забытое в сравнении» видно прогоном, а не глазами.
    /// </summary>
    public static IReadOnlyList<string> ComparedFields() =>
        Properties()
            .Select(property => property.Name)
            .Where(name => !IgnoredFields.Contains(name, StringComparer.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Одно значение против другого. Списки (каталоги ключей, лишние имена) сравниваются
    /// ПО СОДЕРЖИМОМУ: <c>List&lt;string&gt;</c> — ссылочный тип, и сравнение по ссылке объявило бы
    /// правкой любую перерисовку окна.
    /// </summary>
    private static bool Same(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (IEnumerable<string> a, IEnumerable<string> b) => a.SequenceEqual(b, StringComparer.Ordinal),
        _ => left.Equals(right),
    };

    private static IEnumerable<PropertyInfo> Properties() =>
        typeof(PanelSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.Ordinal);
}
