using System.Globalization;
using DshPanel.Pricing;
using DshPanel.Shell;
using DshPanel.Tray;

namespace DshPanel.Update;

/// <summary>
/// ЧИСТЫЕ РЕШЕНИЯ ОБНОВЛЕНИЯ — что стоит, что на GitHub, что нового и что с этим делать,
/// ОДНИМ набором фраз на все места, где панель об этом говорит.
///
/// **Откуда этот файл.** Прежде решения жили в окне «Обновление панели»
/// (<c>Views\UpdateWindow.axaml.cs</c>). Решением владельца 09.10.2026 отдельного окна
/// у обновления НЕТ: *«Это не отдельное окно должно быть… Я не хочу отдельно вызываемое окно»* —
/// весь ответ живёт в РАЗДЕЛЕ «Обновление» окна настроек. Функции переехали сюда **не изменившись
/// по существу**: раздел настроек и прежнее окно показывали одни и те же ответы, и второго набора
/// фраз в панели быть не должно — разойдясь, они сказали бы человеку разное об одном выпуске.
///
/// **Четыре обещания, и каждое проверяется прогоном, а не словами.**
///
/// 1. **Версии СРАВНИВАЮТСЯ.** Панель не повторяет номер с GitHub как новость: «доступна версия»
///    говорится только тогда, когда выпуск ДЕЙСТВИТЕЛЬНО новее (решение принимает ядро —
///    <see cref="UpdateDecisions.IsNewer"/>, единственная реализация сравнения в панели).
/// 2. **Заметки читаются.** Строки заметок к выпуску разбираются на заголовки, пункты списка
///    и абзацы (<see cref="UpdateNotes"/>) и рисуются по одной (<c>Views\UpdateNotesView</c>) —
///    не простынёй: на «сплошной неструктурированный текст» владелец жаловался дважды
///    (`docs\DESIGN.md`, пункты 32–33).
/// 3. **Замена файлов не запускается сама.** «Скачать и подготовить» только готовит: скачивает,
///    сверяет сумму, распаковывает, сверяет версию и снимает страховочную копию. Кнопка
///    «Обновить и перезапустить панель» живёт в ОТДЕЛЬНОЙ карточке и появляется только после
///    удачной подготовки: это единственная дверь продукта, подменяющая саму панель.
/// 4. **Отказ называется словами.** Причина приходит ключом от ядра
///    (<see cref="UpdateRefusals.Key"/>) и превращается в фразу словарём
///    (<see cref="UpdateRefusalLines"/>); молчания нет ни на одном пути.
///
/// ⚠️ **Право готовить замену — не решение показа.** Здесь только ВИДНО, можно ли нажимать
/// (<see cref="PrepareState"/>); сам движок отказывает без права, и отказать он обязан
/// независимо от того, что показала панель.
///
/// ⚠️ **Ни одного текста в разметке**: все подписи ставятся кодом из <see cref="PanelStrings"/>,
/// а тот берёт их из словаря трёх языков (`docs\LOCALIZATION.md` §1).
/// </summary>
public static class UpdateLines
{
    /// <summary>Что стоит сейчас: «Установлено: 2.0.0». Номер берётся у сборки, а не у настроек.</summary>
    public static string CurrentLine(string? current) =>
        string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateCurrentFormat, (current ?? string.Empty).Trim());

    /// <summary>
    /// Что лежит на GitHub. Данных нет — панель говорит «данных нет», а не подставляет свою
    /// версию: подстановка выглядела бы как «на GitHub то же самое», то есть как проверка,
    /// которой не было.
    /// </summary>
    public static string GithubLine(bool ok, string? latest)
    {
        var version = (latest ?? string.Empty).Trim();

        return ok && version.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateOnGithubFormat, version)
            : PanelStrings.UpdateOnGithubUnknown;
    }

    /// <summary>
    /// Дата выпуска. Разобрать её не удалось — панель говорит «неизвестна» и НЕ выдумывает дату:
    /// выдуманная дата хуже отсутствующей, потому что выглядит проверенной. Выпуска нет вовсе —
    /// строки о дате нет совсем.
    /// </summary>
    public static string PublishedLine(bool ok, string? published)
    {
        if (!ok) return string.Empty;

        if (!PricingDecisions.TryParse(published, out var at)) return PanelStrings.UpdatePublishedUnknown;

        return string.Format(
            CultureInfo.CurrentCulture,
            PanelStrings.UpdatePublishedFormat,
            at.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.CurrentCulture));
    }

    /// <summary>
    /// ЧТО СКАЗАТЬ ПРО ВЕРСИИ. **Сравнение — здесь и только здесь** (через ядро
    /// <see cref="UpdateDecisions.IsNewer"/>): показывающий любой номер с GitHub как
    /// «доступна версия» сказал бы человеку неправду на выпуске, который не новее.
    ///
    /// Порядок ответов: данных нет → выпуска нет; человек пропустил эту версию → говорим о его
    /// решении (оно важнее новизны); выпуск новее → «доступна версия»; иначе → «у вас последняя
    /// версия» С НОМЕРОМ и словами «обновляться не нужно».
    ///
    /// ⚠️ **Слова про «не нужно» появились по замечанию владельца 09.10.2026.** Прежняя строка
    /// была «Последняя версия» — и человек, прочитав экран целиком, не понимал, у него ли она
    /// и надо ли что-то нажимать: рядом висела доступная кнопка «Пропустить эту версию».
    /// Теперь ответ называет и версию, и то, что делать нечего.
    /// </summary>
    public static string VerdictLine(bool ok, string? latest, string? current, bool skipped)
    {
        var version = (latest ?? string.Empty).Trim();
        if (!ok || version.Length == 0) return PanelStrings.UpdateNeverChecked;

        if (skipped) return string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateSkippedFormat, version);

        return UpdateDecisions.IsNewer(version, current)
            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateAvailableFormat, version)
            : string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.UpdateWindowLatestFormat,
                (current ?? string.Empty).Trim());
    }

    /// <summary>
    /// Тон строки решения — тот же язык, что у строк состояния в меню значка: новая версия —
    /// «внимание» (не сломано, но есть о чём подумать), пропуск и «данных нет» — серый
    /// (решение человека и неизвестность, а не беда), «последняя версия» — зелёный.
    /// </summary>
    public static TrayTone VerdictTone(bool ok, string? latest, string? current, bool skipped)
    {
        var version = (latest ?? string.Empty).Trim();

        if (!ok || version.Length == 0) return TrayTone.Neutral;
        if (skipped) return TrayTone.Neutral;

        return UpdateDecisions.IsNewer(version, current) ? TrayTone.Warning : TrayTone.Good;
    }

    /// <summary>
    /// МОЖНО ЛИ ГОТОВИТЬ ЗАМЕНУ и что об этом сказать человеку. Четыре отказа, и каждый назван
    /// своей причиной: движка нет вовсе (прогон, в котором раздел построен без связки), права нет
    /// (прогон проверки — <see cref="UpdateInstall.For"/>), панель ещё не знает адресов вложений
    /// (их узнают при проверке) — и **обновляться некуда**: выпуск не новее установленной панели.
    ///
    /// ⚠️ Последний отказ появился по замечанию владельца 09.10.2026: кнопка «Скачать и
    /// подготовить» была ДОСТУПНА и на своём же выпуске (движок потом отказывал словами
    /// `UpdateAlreadyLatest`). Доступная кнопка, которая ничего не сделает, читается как
    /// «обновиться всё-таки можно» — и это ровно то, чего человек не понял.
    ///
    /// ⚠️ Отдельной чистой функцией, а не условием в разметке: это ровно та проверка, которую
    /// обязана ловить мутация «право спрашивать перестали».
    /// </summary>
    public static (bool Ready, string Hint) PrepareState(
        IUpdateInstall? install, bool ok, bool newer, ReleaseAssets assets)
    {
        if (install is null) return (false, PanelStrings.UpdateInstallUnavailable);
        if (!install.Allowed) return (false, PanelStrings.UpdateRefusedLocked);

        if (!newer) return (false, PanelStrings.UpdateAlreadyLatest);

        return ok && assets.HasArchive && assets.HasSums
            ? (true, PanelStrings.UpdatePrepareHint)
            : (false, PanelStrings.UpdateNeedCheckHint);
    }

    /// <summary>
    /// ЧТО СКАЗАТЬ ПОДСКАЗКОЙ КНОПКИ «ПРОПУСТИТЬ ЭТУ ВЕРСИЮ». Три состояния — три разных ответа,
    /// и каждый говорит правду про ЭТО состояние:
    ///
    /// * выпуск новее — кнопка обещает молчание о нём;
    /// * выпуск есть, но он НЕ новее — «пропускать нечего: эта версия уже установлена» (ради
    ///   этого случая всё и заведено: замечание владельца 09.10.2026);
    /// * выпусков ещё не спрашивали — «сначала проверьте выпуски». Сказать здесь «версия уже
    ///   установлена» значило бы утверждать то, чего панель не знает.
    /// </summary>
    public static string SkipTip(bool ok, string? latest, bool newer)
    {
        if (newer) return PanelStrings.TipUpdateSkipButton;

        return ok && (latest ?? string.Empty).Trim().Length > 0
            ? PanelStrings.TipUpdateSkipNothingNew
            : PanelStrings.TipUpdateSkipNothingToSkip;
    }

    /// <summary>
    /// Ход подготовки словами: этап и проценты. Размер файла сервер не назвал — процентов нет,
    /// и «0 %» вместо честного «размер неизвестен» показало бы, что загрузка встала.
    /// </summary>
    public static string ProgressLine(UpdateProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var stage = UpdateStageLines.Text(progress.Stage);
        var percent = progress.Percent;

        return percent < 0
            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateProgressUnknownFormat, stage)
            : string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateProgressFormat, stage, percent);
    }

    /// <summary>
    /// Адрес страницы выпуска для кнопки «Открыть на GitHub»: своя страница выпуска, если ядро
    /// её принесло, иначе — страница выпусков продукта (<see cref="ProductLinks.ReleasesPageUrl"/>).
    /// Пустого адреса наружу не выходит: кнопки, ведущей в никуда, в панели не бывает.
    /// </summary>
    public static string GithubAddress(string? pageUrl) =>
        ProductLinks.ShouldShow(pageUrl) ? pageUrl!.Trim() : ProductLinks.ReleasesPageUrl;

    /// <summary>
    /// Строка «когда проверяли» — или ПУСТО, когда она повторяет решение.
    ///
    /// ⚠️ Замечание владельца 09.10.2026, увиденное на кадре раздела настроек: в непроверенном
    /// состоянии и решение, и эта строка говорят одно и то же («Обновление ещё не проверялось»)
    /// и стоят подряд — читается как сбой, а не как ответ. Одна и та же фраза дважды не нужна;
    /// когда проверка БЫЛА, строка называет дату и остаётся на месте.
    /// </summary>
    public static string SourceLine(string? source, string? verdict)
    {
        var text = (source ?? string.Empty).Trim();

        if (text.Length == 0) return string.Empty;

        return string.Equals(text, (verdict ?? string.Empty).Trim(), StringComparison.Ordinal)
            ? string.Empty
            : text;
    }
}
