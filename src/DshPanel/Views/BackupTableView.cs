using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using DshPanel.Backup;
using DshPanel.Shell;

namespace DshPanel.Views;

/// <summary>
/// ТАБЛИЦА ГОТОВЫХ КОПИЙ — то, что читает человек в окне «Копии».
///
/// Слова владельца 28.09.2026 (живой просмотр, список «Готовые копии (4)»): *«Мне кажется,
/// правильно будет сделать правкой строк с имеющимися копиями тоже в виде таблицы или как-то иначе
/// визуально эти строки отделить. Не совсем понятно даже, что это кликабельно»*. Отсюда две задачи,
/// и обе здесь:
///
/// 1. **строки — ТАБЛИЦА**, а не абзац: колонки «дата и время · тип · размер · файлов ·
///    особенности». Форма и оформление взяты у таблиц окон пика (<see cref="TableGrid"/> — тот же
///    разделитель строк, та же сетка, тот же отступ), чтобы «таблица» в панели выглядела одинаково
///    везде. Ничего сверх описи архива здесь не выдумывается: каждое слово колонки — поле
///    <see cref="BackupEntry"/>, прочитанное из описи;
/// 2. **кликабельность видна ДО щелчка**: курсор-рука, подсветка строки под курсором, подсветка
///    выбранной строки и тонкая линия между строками. Цвета берутся у ТЕМЫ
///    (<see cref="PanelLook.RowHoverKey"/>, <see cref="PanelLook.RowSelectedKey"/>) — своего
///    оттенка в панели не заводят, и состояние меняет только ЦВЕТ: размер строки от него не зависит
///    (правило, выросшее из «дрожания кнопок», сторожит <c>PanelRhythmTests</c>).
///
/// ⚠️ Копии панели 1.x сюда не доходят вовсе — их не отдаёт список (<see cref="BackupFolder.List"/>,
/// решение владельца 28.09.2026). Здесь нет ни строки, ни пометки, ни колонки «что можно»
/// для них: обходные пути владелец запретил прямо.
///
/// ⚠️ Таблица НИЧЕГО не решает: что можно с копией, знает окно и движок наката. Строка умеет
/// ровно одно — сказать «меня выбрали».
/// </summary>
public static class BackupTableView
{
    /// <summary>
    /// Класс строки таблицы копий. По нему <c>App.axaml</c> ставит подсветку наведения и выбора,
    /// а окно — курсор-руку и щелчок.
    /// </summary>
    public const string RowClass = "backupRow";

    /// <summary>Класс ВЫБРАННОЙ строки: подложка плотнее, чем у наведения.</summary>
    public const string SelectedClass = "backupSelected";

    /// <summary>
    /// Вид курсора у строки — «рука». ОДНО значение на всю таблицу и на проверку: стрелка вместо
    /// руки ничего не обещает, а «курсор вообще есть» прошло бы и с ней.
    /// </summary>
    public const StandardCursorType PointerCursor = StandardCursorType.Hand;

    /// <summary>
    /// Ширины колонок: дата, тип, размер, файлов — по содержимому (у каждого своя наименьшая
    /// ширина), «особенности» — остаток.
    ///
    /// ⚠️ Первые четыре колонки обязаны иметь ОДНУ ширину во всех строках, и задаётся она
    /// наименьшей шириной ЯЧЕЙКИ, а не `Auto` по содержимому: у каждой строки своя сетка, и `Auto`
    /// выровнял бы шапку не над своими значениями (ровно так же устроена таблица пиков).
    /// </summary>
    private const double MomentWidth = 128;

    private const double KindWidth = 72;

    private const double SizeWidth = 88;

    private const double FilesWidth = 96;

    private static readonly GridLength[] Columns =
    {
        GridLength.Auto,
        new(TableGrid.Gutter),
        GridLength.Auto,
        new(TableGrid.Gutter),
        GridLength.Auto,
        new(TableGrid.Gutter),
        GridLength.Auto,
        new(TableGrid.Gutter),
        new(1, GridUnitType.Star),
    };

    /// <summary>Момент копии словами: дата и время. Пустая дата названа словами, а не пустотой.</summary>
    public static string Moment(BackupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.CreatedAt == default
            ? PanelStrings.BackupTimeUnknown
            : entry.CreatedAt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>Тип копии: полная (с движком и Node) или тонкая.</summary>
    public static string Kind(BackupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.WithEngine ? PanelStrings.BackupKindFull : PanelStrings.BackupKindThin;
    }

    /// <summary>Размер архива. Неизвестен (файл не прочитался) — пустая ячейка, а не «0 Б».</summary>
    public static string Size(BackupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Bytes > 0 ? BackupFormat.Size(entry.Bytes) : string.Empty;
    }

    /// <summary>Сколько файлов в архиве. Ноль в описи значит «не сосчитано» — ячейка пуста.</summary>
    public static string Files(BackupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return FilesText(entry.Files);
    }

    /// <summary>
    /// «12 файл(ов)» — ОДНО место на две таблицы: список копий и план наката
    /// (<see cref="RestoreTableView"/>) считают файлы одним и тем же словом.
    ///
    /// Ноль значит «не сосчитано» — ячейка пуста, а не «0 файл(ов)»: ноль читался бы как
    /// «в копии ничего нет», и это была бы неправда о целом архиве.
    /// </summary>
    public static string FilesText(int files) => files > 0
        ? string.Format(
            CultureInfo.CurrentCulture,
            PanelStrings.BackupFilesFormat,
            files.ToString("N0", CultureInfo.InvariantCulture))
        : string.Empty;

    /// <summary>
    /// ОСОБЕННОСТИ КОПИИ — по тому, что панель действительно знает из описи: есть ли в архиве
    /// движок, сессии и ключ доступа. Опись нечитаемая — вместо особенностей стоит ПРИЧИНА,
    /// а не пустое место: молчащая строка читается как «всё в порядке».
    /// </summary>
    public static string Features(BackupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!entry.Readable)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.BackupManifestBrokenFormat,
                entry.Error.Length > 0 ? entry.Error : PanelStrings.BackupUnknown);
        }

        var parts = new List<string>();

        if (entry.WithEngine) parts.Add(PanelStrings.BackupWithEngine);
        if (entry.WithSessions) parts.Add(PanelStrings.BackupWithSessions);
        if (entry.WithCredentials) parts.Add(PanelStrings.BackupWithKey);

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Отпечаток строки — то, по чему окно решает, пересобирать ли таблицу.
    ///
    /// Нужен ровно от одной беды (она уже случалась у этого окна 26.09.2026): пересборка списка
    /// на каждой отрисовке теряла ВЫБОР, а потеря выбора возвращалась событием, которое снова
    /// звало отрисовку, — и окно крутилось, пока не кончится стек. Собирается из тех же слов,
    /// что видит человек: разошлись они — таблицу пора пересобрать.
    /// </summary>
    public static string Signature(BackupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return string.Join(" · ", Moment(entry), Kind(entry), Size(entry), Files(entry), Features(entry));
    }

    /// <summary>
    /// Наполнить сетку таблицей: строка заголовков колонок и по строке на каждую копию.
    ///
    /// <paramref name="selected"/> — номер выбранной строки (<c>-1</c> значит «ничего не выбрано»);
    /// <paramref name="choose"/> — что делать при щелчке по строке: ему уходит её номер. Сама
    /// таблица выбора не помнит и не хранит — состояние живёт в окне, и это одно место правды:
    /// помни таблица своё, при пересборке они разошлись бы.
    /// </summary>
    public static void Fill(
        Grid target,
        IReadOnlyList<BackupEntry> entries,
        int selected,
        Action<int> choose)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(choose);

        TableGrid.Prepare(target, Columns);

        var line = 0;

        TableGrid.AddLine(
            target,
            line++,
            Columns.Length,
            new[]
            {
                (TableGrid.Header(PanelStrings.BackupTableTime, minWidth: MomentWidth), 0),
                (TableGrid.Header(PanelStrings.BackupTableKind, minWidth: KindWidth), 2),
                (TableGrid.Header(PanelStrings.BackupTableSize, minWidth: SizeWidth), 4),
                (TableGrid.Header(PanelStrings.BackupTableFiles, minWidth: FilesWidth), 6),
                (TableGrid.Header(PanelStrings.BackupTableFeatures), 8),
            });

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];

            var row = TableGrid.AddLine(
                target,
                line++,
                Columns.Length,
                new[]
                {
                    (TableGrid.Cell(Moment(entry), minWidth: MomentWidth), 0),
                    (TableGrid.Cell(Kind(entry), minWidth: KindWidth), 2),
                    (TableGrid.Cell(Size(entry), minWidth: SizeWidth), 4),
                    (TableGrid.Cell(Files(entry), minWidth: FilesWidth), 6),
                    (TableGrid.Cell(Features(entry), wrap: true), 8),
                },
                RowClass);

            // Курсор-рука — первое, что говорит «по мне можно щёлкнуть», ещё до наведения.
            row.Cursor = new Cursor(PointerCursor);

            var number = index;
            row.PointerPressed += (_, _) => choose(number);
        }

        Select(target, selected);
    }

    /// <summary>
    /// Строки таблицы — в порядке отрисовки (шапка не считается: у неё нет класса строки).
    /// Нужны проверкам: «счётчик совпадает с числом строк» и «строка кликабельна» проверяются
    /// по настоящим органам окна, а не по числу в модели.
    /// </summary>
    public static IReadOnlyList<Border> Rows(Grid target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return target.Children
            .OfType<Border>()
            .Where(border => border.Classes.Contains(RowClass))
            .ToList();
    }

    /// <summary>
    /// Показать, какая строка выбрана. Зовётся и при наполнении, и отдельно — когда таблица
    /// та же, а выбор сменился: пересобирать ради этого строки незачем.
    /// </summary>
    public static void Select(Grid target, int index)
    {
        ArgumentNullException.ThrowIfNull(target);

        var rows = Rows(target);

        for (var number = 0; number < rows.Count; number++)
        {
            if (number == index) rows[number].Classes.Add(SelectedClass);
            else rows[number].Classes.Remove(SelectedClass);
        }
    }
}
