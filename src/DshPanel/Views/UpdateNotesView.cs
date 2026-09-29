using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DshPanel.Views;

/// <summary>
/// РИСОВАНИЕ ЗАМЕТОК К ВЫПУСКУ — заголовки, пункты списка, абзацы и отступы.
///
/// **Зачем отдельным файлом.** Разбор заметок (<see cref="Update.UpdateNotes"/>) — чистая
/// функция от текста и проверяется без окна. Здесь только ВИД: какая строка крупнее, у какой
/// есть отступ и где стоит воздух. Разделение не ради красоты: правило «структура видна»
/// обязано проверяться прогоном, а прогон разбора не поднимает окна.
///
/// **Почему это вообще нужно.** Владелец дважды жаловался на «сплошной неструктурированный
/// текст» (`docs\DESIGN.md`, пункты 32–33). Заметки к выпуску приходят с GitHub одной простынёй;
/// показанные как один <c>TextBlock</c>, они читаются именно так. Поэтому каждая строка —
/// свой <c>TextBlock</c>: заголовок крупнее и жирнее, пункт списка с отступом, пустая строка
/// воздухом между абзацами.
///
/// ⚠️ Ни одного текста здесь нет — только раскладка. Строки приходят разобранными.
/// </summary>
internal static class UpdateNotesView
{
    /// <summary>Отступ пункта списка и цитаты — из шкалы панели (4/8/12/16/24).</summary>
    private const double Indent = 12;

    /// <summary>Воздух на месте пустой строки: одна пустая строка в тексте — один промежуток.</summary>
    private const double BlankHeight = 8;

    /// <summary>
    /// Заполнить панель строками заметок. Панель ОЧИЩАЕТСЯ целиком: её пересобирают при каждом
    /// изменении состояния, и накопление строк дало бы заметки двух выпусков сразу.
    /// </summary>
    public static void Fill(Panel target, IReadOnlyList<Update.UpdateNoteLine> lines)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(lines);

        target.Children.Clear();

        foreach (var line in lines) target.Children.Add(Block(line));
    }

    /// <summary>Сколько строк заметок сейчас на экране — для проверок без экрана.</summary>
    public static int Count(Panel target) => target.Children.Count;

    private static Control Block(Update.UpdateNoteLine line) => line.Kind switch
    {
        Update.UpdateNoteKind.Blank => new Border { Height = BlankHeight },

        Update.UpdateNoteKind.Heading => Text(line.Text, 15, FontWeight.SemiBold, new Thickness(0, 6, 0, 0)),

        Update.UpdateNoteKind.SubHeading => Text(line.Text, 13.5, FontWeight.SemiBold, new Thickness(0, 4, 0, 0), 0.9),

        // Маркер рисуется ЗДЕСЬ, а не приходит из текста: у разбора его нет, и это правильно —
        // вид строки решает вид, а не знак в данных.
        Update.UpdateNoteKind.Bullet => Text("• " + line.Text, 13, FontWeight.Normal, new Thickness(Indent, 0, 0, 0)),

        Update.UpdateNoteKind.Numbered => Text(line.Text, 13, FontWeight.Normal, new Thickness(Indent, 0, 0, 0)),

        Update.UpdateNoteKind.Quote => Text(line.Text, 13, FontWeight.Normal, new Thickness(Indent, 0, 0, 0), 0.75, italic: true),

        Update.UpdateNoteKind.Code => Text(line.Text, 12.5, FontWeight.Normal, new Thickness(Indent, 0, 0, 0), 0.9),

        _ => Text(line.Text, 13, FontWeight.Normal, new Thickness(0)),
    };

    /// <summary>
    /// Одна строка заметок. Перенос строк ВКЛЮЧЁН всегда: ширина окна — не дело разбора,
    /// а обрезанный по краю текст читался бы как потерянный.
    /// </summary>
    private static TextBlock Text(
        string text,
        double size,
        FontWeight weight,
        Thickness margin,
        double opacity = 0.85,
        bool italic = false) => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Margin = margin,
            Opacity = opacity,
            TextWrapping = TextWrapping.Wrap,
            FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        };
}
