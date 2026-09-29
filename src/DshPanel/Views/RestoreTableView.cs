using System.Globalization;
using Avalonia.Controls;
using DshPanel.Restore;
using DshPanel.Shell;

namespace DshPanel.Views;

/// <summary>
/// ТАБЛИЦА «ЧТО ВЕРНЁМ» — то, что человек читает перед накатом.
///
/// Слова владельца 29.09.2026 (п. 32 <c>docs\DESIGN.md</c>): *«Что вернём, копия? Текст очень
/// неструктурированный, просто сплошной, и читать невозможно»*. Прежде план был ОДНИМ абзацем:
/// <c>RestorePlan.Summary()</c> склеивал выбранные группы через «; », и пять групп из шести
/// (пустых) занимали место, ничего не сообщая.
///
/// Приём тот же, что у таблицы готовых копий (п. 29, <see cref="BackupTableView"/>): строки идут
/// по ОБЩЕЙ сетке панели (<see cref="TableGrid"/> — тот же тонкий разделитель, тот же отступ,
/// те же подписи колонок из словаря), и «как выглядит строка» решается в одном месте.
///
/// Четыре правила, и каждое — про честность перед человеком:
///
/// 1. **Строки берутся из СТРУКТУРНЫХ данных плана** (<see cref="RestorePlan.Groups"/>, запись
///    <see cref="RestoreGroup"/>), а не разбором готового текста: текст — это то, что мы показываем,
///    а не то, из чего мы читаем;
/// 2. **пустые группы (0 файлов) не показываются вовсе.** Это и был главный источник «простыни»:
///    группа, которой в архиве нет, ничего не вернёт и места занимать не должна;
/// 3. **видно и то, что НЕ вернётся, и почему.** Группа с файлами, но без цели, — это «движок
///    не возвращаем: нет согласия», «ключи возвращаются только по отдельному согласию»: пропуск
///    без причины человек читает как потерю данных (правило v1);
/// 4. **числа — из описи архива**, а не пересказом: столько файлов, сколько насчитал читатель
///    архива; путь цели показывается маской (<see cref="DisplayMask"/>), а не сырым.
///
/// ⚠️ Чего здесь НЕТ: решений. Что можно и что нельзя, знает <see cref="RestoreEngine"/>; таблица
/// только раскладывает уже принятое по колонкам и ничего не считает заново.
/// </summary>
public static class RestoreTableView
{
    /// <summary>
    /// Ширины колонок: имя группы и число файлов — по содержимому (у каждой своя наименьшая
    /// ширина), цель и состояние — остаток места.
    ///
    /// ⚠️ Как и у таблицы копий, наименьшая ширина задаётся ЯЧЕЙКЕ, а не колонке: у каждой строки
    /// своя сетка, и `Auto` по содержимому выровнял бы шапку не над своими значениями.
    /// </summary>
    private const double GroupWidth = 132;

    private const double FilesWidth = 88;

    private static readonly GridLength[] Columns =
    {
        GridLength.Auto,
        new(TableGrid.Gutter),
        new(1.6, GridUnitType.Star),
        new(TableGrid.Gutter),
        GridLength.Auto,
        new(TableGrid.Gutter),
        new(1.4, GridUnitType.Star),
    };

    /// <summary>
    /// Группы, которые ВИДИТ ЧЕЛОВЕК: пустые (0 файлов) не показываются вовсе. Порядок — тот же,
    /// в каком группы лежат в описи архива: своя сортировка тут была бы вторым решением о том,
    /// что важнее, и разошлась бы с отчётом наката.
    /// </summary>
    public static IReadOnlyList<RestoreGroup> Rows(RestorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan.Groups.Where(group => group.Files > 0).ToList();
    }

    /// <summary>
    /// КУДА ЛЯЖЕТ — под маской, а не сырым путём: в кадре, в журнале и в чужом отчёте имени
    /// пользователя быть не должно (красная линия 7). У невыбранной группы цели нет вовсе,
    /// и пустая клетка честнее выдуманного пути — причину называет колонка состояния.
    /// </summary>
    public static string Target(RestoreGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return group.Selected ? DisplayMask.Path(group.Target) : string.Empty;
    }

    /// <summary>
    /// ЧТО С ГРУППОЙ: «вернётся» — и примечание, если оно у неё есть (движок ляжет по своему месту,
    /// рабочая папка разошлась с настройкой и т. п.); иначе — СЛОВА ПРИЧИНЫ из плана.
    ///
    /// ⚠️ Причина берётся у плана, а не сочиняется здесь: тот же текст человек читает в отчёте
    /// наката и в режиме без окна, и второй формулировки у одного состояния быть не должно.
    /// Пустая причина — не молчание: <see cref="PanelStrings.RestoreTableNotLands"/> называет
    /// состояние прямым словом.
    /// </summary>
    public static string State(RestoreGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (!group.Selected)
        {
            return group.Note.Length > 0 ? group.Note : PanelStrings.RestoreTableNotLands;
        }

        return group.Note.Length > 0
            ? PanelStrings.RestoreTableLands + " · " + group.Note
            : PanelStrings.RestoreTableLands;
    }

    /// <summary>Сколько файлов вернётся из этой группы — число из описи архива.</summary>
    public static string Files(RestoreGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return BackupTableView.FilesText(group.Files);
    }

    /// <summary>
    /// ИТОГ ПОД ТАБЛИЦЕЙ — короткая строка-счётчик, а не абзац: сколько файлов и в скольких
    /// группах вернётся, плюс — когда они есть — пропущенные по небезопасности записи.
    ///
    /// ⚠️ Считаются ФАЙЛЫ ВЫБРАННЫХ ГРУПП (<see cref="RestorePlan.RestoringFiles"/>), а не
    /// <see cref="RestorePlan.Files"/>: общее число по архиву включает группы, которые НЕ лягут
    /// (движок без согласия, ключи без согласия), а рядом в таблице у них стоит «не вернётся» —
    /// и счётчик спорил бы со своими же строками.
    ///
    /// ⚠️ А ГРУППЫ считаются ПО СТРОКАМ ТАБЛИЦЫ (решение дирижёра 29.09.2026 — владелец отдал его
    /// ему: *«здесь реши самостоятельно, мне не принципиально»*). Выбранная, но ПУСТАЯ группа
    /// (рабочая папка без файлов) в таблицу не попадает — и число, указывающее на строку, которой
    /// человек не видит, читается как ошибка: «в 2 групп(ах)», а «вернётся» стоит у одной.
    /// Файлы берутся у плана: пустая группа даёт ноль, поэтому оба счёта всё равно об одном —
    /// о том, что ляжет. Строка режима без окна (<see cref="RestorePlan.Summary"/>) считает группы
    /// по домену и может назвать на одну больше — но она перечисляет группы СЛЕДОМ, и там это
    /// не спор, а список.
    /// </summary>
    public static string Summary(RestorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var lands = Rows(plan).Count(group => group.Selected);

        var text = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.RestoreTableSummaryFormat, plan.RestoringFiles, lands);

        return plan.UnsafeEntries > 0
            ? text + string.Format(
                CultureInfo.CurrentCulture, PanelStrings.RestorePlanUnsafeSkippedFormat, plan.UnsafeEntries)
            : text;
    }

    /// <summary>
    /// Наполнить сетку таблицей: строка заголовков колонок и по строке на каждую НЕПУСТУЮ группу.
    ///
    /// Сетка чистится целиком (<see cref="TableGrid.Prepare"/>): план пересобирается при смене
    /// галочек согласий, и накопление строк показало бы два плана сразу.
    /// </summary>
    public static void Fill(Grid target, RestorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(plan);

        TableGrid.Prepare(target, Columns);

        var line = 0;

        TableGrid.AddLine(
            target,
            line++,
            Columns.Length,
            new[]
            {
                (TableGrid.Header(PanelStrings.RestoreTableGroup, minWidth: GroupWidth), 0),
                (TableGrid.Header(PanelStrings.RestoreTableTarget), 2),
                (TableGrid.Header(PanelStrings.RestoreTableFiles, right: true, minWidth: FilesWidth), 4),
                (TableGrid.Header(PanelStrings.RestoreTableState), 6),
            });

        foreach (var group in Rows(plan))
        {
            TableGrid.AddLine(
                target,
                line++,
                Columns.Length,
                new[]
                {
                    (TableGrid.Cell(group.Prefix, wrap: true, minWidth: GroupWidth), 0),
                    (TableGrid.Cell(Target(group), wrap: true), 2),
                    (TableGrid.Cell(Files(group), right: true, minWidth: FilesWidth), 4),
                    (TableGrid.Cell(State(group), wrap: true), 6),
                });
        }
    }

    /// <summary>
    /// Строки таблицы в порядке отрисовки: ПЕРВАЯ — шапка колонок, дальше по строке на группу.
    /// Нужны проверкам: «строк ровно столько, сколько непустых групп» проверяется по настоящим
    /// органам окна, а не по числу в модели.
    /// </summary>
    public static IReadOnlyList<Border> Lines(Grid target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return target.Children.OfType<Border>().ToList();
    }
}
