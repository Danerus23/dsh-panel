using System.Globalization;
using Avalonia.Threading;
using DshPanel.Agents;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Settings;
using DshPanel.Shell;

namespace DshPanel.Pricing;

/// <summary>
/// Что окно знает про цены со страницы: сколько стоит единица, откуда это взято и когда проверено.
///
/// Отдельный уговор от <c>IBalanceControl</c> намеренно: у баланса и у цен РАЗНЫЕ права и разные
/// поводы. Баланс ходит в сеть с ключом владельца, цены — на публичную страницу; баланс меряется
/// минутами, цены — сутками. Сведённые в один уговор, они поехали бы вместе на первой же правке.
/// </summary>
public interface IPricingControl
{
    /// <summary>Что удалось прочитать. До первого чтения — «не спрашивали».</summary>
    PricingResult Result { get; }

    /// <summary>Разбор идёт прямо сейчас.</summary>
    bool Busy { get; }

    /// <summary>Можно ли вообще ходить в сеть в этом прогоне.</summary>
    bool Allowed { get; }

    /// <summary>Заголовок таблицы цен: «Стоимость» или «Стоимость — deepseek-flash».</summary>
    string PriceHeadingText { get; }

    /// <summary>
    /// Откуда взяты цены — СЛОВАМИ: адрес страницы, модель и время проверки; а когда цен нет —
    /// почему именно нет (прогон проверки, ещё не читали, не разобралось). Молчания здесь быть
    /// не может: пустая таблица без объяснения читается как «панель сломалась».
    /// </summary>
    string PriceSourceText { get; }

    /// <summary>Спросить цены сейчас (в фоне). Повторный вызов во время запроса ничего не делает.</summary>
    void Refresh();

    /// <summary>
    /// ИСТОРИЯ ЦЕН: молчаливая базовая точка и записи об изменениях — то, что панель помнит
    /// о прошлых разборах. Окну истории больше нечего спрашивать: у цен и у истории один владелец,
    /// и разойтись они не могут.
    /// </summary>
    PricingHistory History { get; }

    event Action? Changed;
}

/// <summary>
/// ЦЕНЫ СО СТРАНИЦЫ ЦЕН — чтение по расписанию и по просьбе человека.
///
/// **Два повода, и они разные.**
///
/// * **Автоматический разбор** — решение владельца 27.09.2026, названное им для обновлений:
///   *при запуске панели, но не чаще раза в сутки*. Поэтому часы идут ЧАС, а решение «пора»
///   принимает чистая функция (<see cref="PricingDecisions.Due"/>) по отметке последнего разбора
///   из файла настроек. Отметка в файле, а не в памяти: иначе «раз в сутки» превращалось бы
///   в «при каждом запуске панели».
/// * **Просьба человека** — кнопка «Обновить информацию» в окне «Пики и тарифы». Это не автоматика,
///   и расписанием она не закрыта (как кнопка «Обновить баланс»): человек попросил — панель пошла.
///   Заодно она применяет окна пика со страницы: щелчок и есть то подтверждение, которого требует
///   правило панели 1.x («окна применяются только по подтверждению, цены — показ»).
///
/// **Права те же, что у баланса, и это не формальность.** Прогон проверки не ходит в сеть вовсе
/// (<c>RunRights.LocalData</c> ложно или работа по расписанию запрещена изоляцией) и говорит об
/// этом словами — в окне и в журнале. Ни один наш прогон не имеет права обратиться в интернет
/// от имени владельца.
///
/// ⚠️ **Сети в проверках не бывает вовсе.** Уговор <see cref="IPricingClient"/> подставляется:
/// проверка даёт готовый текст записанной страницы, а разбор — чистая функция.
/// </summary>
public sealed class PricingController : IPricingControl, IDisposable
{
    private readonly IPricingClient _client;
    private readonly Func<PanelSettings> _settings;
    private readonly Func<string> _language;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<Action> _dispatch;
    private readonly Action<string> _log;
    private readonly Action<string> _rememberCheckedAt;
    private readonly Action<string> _rememberWindows;
    private readonly Action<string> _rememberLast;
    private readonly PricingHistoryFiles _historyFiles;
    private readonly Action<NoticeKind, string, string> _notify;

    private DispatcherTimer? _timer;
    private DateTimeOffset? _lastCheck;

    public PricingController(
        IPricingClient client,
        Func<PanelSettings> settings,
        Func<string> language,
        bool allowed,
        Func<DateTimeOffset> clock,
        Action<Action> dispatch,
        Action<string> log,
        Action<string> rememberCheckedAt,
        Action<string> rememberWindows,
        Action<string> rememberLast,
        PricingHistoryFiles historyFiles,
        Action<NoticeKind, string, string> notify,
        bool isolatedRun = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _language = language ?? throw new ArgumentNullException(nameof(language));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _rememberCheckedAt = rememberCheckedAt ?? throw new ArgumentNullException(nameof(rememberCheckedAt));
        _rememberWindows = rememberWindows ?? throw new ArgumentNullException(nameof(rememberWindows));
        _rememberLast = rememberLast ?? throw new ArgumentNullException(nameof(rememberLast));
        _historyFiles = historyFiles ?? throw new ArgumentNullException(nameof(historyFiles));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));

        Allowed = allowed;
        IsolatedRun = isolatedRun;

        // ЦЕНЫ ПРОШЛОГО РАЗБОРА ЧИТАЮТСЯ СРАЗУ — иначе таблица «Стоимость» стояла бы пустой весь
        // день: суточный гейт закрыт, разбора не будет, а показать нечего. Владелец нашёл это
        // 28.09.2026 словами «тарифы не сохраняются, только пики». Память читается ровно там,
        // где у прогона есть право читать данные человека.
        Result = Seeded();

        // ИСТОРИЯ — тем же правилом и по той же причине: она прочитана при построении, поэтому
        // окно истории показывает записи даже тогда, когда разбор в этот день уже был сделан.
        History = SeededHistory();
    }

    /// <summary>
    /// ИСТОРИЯ ИЗ ФАЙЛА, а без права — пустая. Прогон, которому не позволено читать данные
    /// человека, не знает истории владельца ВООБЩЕ: подставить ему чужой файл значило бы показать
    /// то, чего этот прогон не читал и читать не должен (красная линия 4).
    ///
    /// Битая запись — «истории нет»: пережить правленый руками файл панель обязана
    /// (<see cref="PricingHistoryStore.Decode"/>), и это тот же приём, что у памяти цен.
    /// </summary>
    private PricingHistory SeededHistory() =>
        Allowed ? PricingHistoryStore.Decode(_historyFiles.Read()) : PricingHistory.Empty;

    /// <summary>
    /// ЦЕНЫ, ПРОЧИТАННЫЕ В ПРОШЛЫЙ РАЗ, — из настроек. Три правила, и каждое не украшение:
    ///
    /// * **прогон без права данных выпусков не знает ВООБЩЕ**: подставить ему вчерашнюю таблицу
    ///   значило бы показать в прогоне проверки то, чего этот прогон не читал и читать не должен;
    /// * **язык панели мог смениться** — тогда таблица перечитывается при первом же разборе,
    ///   а пока показывается прежняя и честно называет свой язык в подписи «откуда взято»:
    ///   выбросить её значило бы оставить человека без цен из-за смены языка интерфейса;
    /// * **битая запись — «не читали»**: причина отказа в память не пишется вовсе, потому что
    ///   вчерашний отказ сегодня выдавался бы за свежий (сеть могла починиться пять минут назад).
    /// </summary>
    private PricingResult Seeded()
    {
        if (!Allowed) return PricingResult.NotRequested(Language());

        var remembered = PricingMemory.Decode(_settings().PricingLast);

        return remembered ?? PricingResult.NotRequested(Language());
    }

    /// <summary>Прогон изолированный: автоматической работы по расписанию в нём нет.</summary>
    public bool IsolatedRun { get; }

    public bool Allowed { get; }

    public PricingResult Result { get; private set; }

    public PricingHistory History { get; private set; }

    public bool Busy { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// ЗАГОЛОВОК ТАБЛИЦЫ ЦЕН. Модель называется вслух: страница печатает цены для НЕСКОЛЬКИХ
    /// моделей, и подставить первую молча значило бы показать человеку цену не того, чем он
    /// пользуется. Пока цен нет, модель неизвестна — заголовок без имени.
    /// </summary>
    public string PriceHeadingText =>
        Result.Ok && Result.Model.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.PriceTableTitleFormat, Result.Model)
            : PanelStrings.PriceTableTitle;

    /// <summary>
    /// ОТКУДА ВЗЯТЫ ЦЕНЫ — словами, и это требование владельца: *«если страница нужного языка
    /// не разобралась — покажи то, что есть, и скажи словами, откуда взято; не молчи»*.
    /// Три разных ответа, и каждый называет свою причину: права нет, не читали вовсе, не вышло.
    /// </summary>
    public string PriceSourceText
    {
        get
        {
            if (!Allowed) return PanelStrings.PriceLocked;

            if (Result.Ok && Result.HasRows)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    PanelStrings.PriceSourceFormat,
                    Result.SourceUrl,
                    PricingDecisions.CheckedText(PricingDecisions.Stamp(Result.CheckedAt)));
            }

            if (Result.Error.Length > 0)
            {
                return string.Format(CultureInfo.CurrentCulture, PanelStrings.PriceFailedFormat, Result.Error);
            }

            return PanelStrings.PriceNeverRead;
        }
    }

    /// <summary>Язык панели — тот, чья страница цен будет прочитана и чей знак валюты встанет.</summary>
    public string Language() => _language() ?? LanguageDecisions.English;

    /// <summary>
    /// Запустить часы. В прогоне без права часов не будет вовсе — и это в журнал: «панель молчала»
    /// должно быть видно словами, а не догадкой.
    /// </summary>
    public void Start()
    {
        if (!Allowed)
        {
            _log(PanelStrings.PanelLogPricingLocked);
            return;
        }

        _timer ??= new DispatcherTimer { Interval = PricingDecisions.TickInterval };
        if (_timer.IsEnabled) return;

        _timer.Tick += (_, _) => Tick(_clock());
        _timer.Start();
        _log(PanelStrings.PanelLogPricingClock);

        // И сразу первый такт: решение владельца — разбор ПРИ ЗАПУСКЕ панели, а не через час.
        // Второй раз за сутки он уже не сработает: такт спросит отметку в настройках.
        Tick(_clock());
    }

    /// <summary>
    /// Один такт часов: пора ли разбирать страницу заново. Сам разбор — в фоне: сеть на нитке
    /// интерфейса заморозила бы окно.
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        if (!Allowed) return;

        // Отметка из настроек И память этого прогона: настройки могут быть недоступны на запись
        // (тогда отметка не сохранится), и без памяти панель стучалась бы в сеть КАЖДЫЙ такт.
        if (!PricingDecisions.Due(now, _settings().PricingCheckedAt, IsolatedRun)) return;
        if (_lastCheck is not null && now - _lastCheck.Value < PricingDecisions.CheckInterval) return;

        Refresh(now, humanRequest: false);
    }

    /// <summary>Кнопка «Обновить информацию»: момент берётся у часов панели.</summary>
    public void Refresh() => Refresh(_clock(), humanRequest: true);

    /// <summary>
    /// Прочитать страницу цен. <paramref name="humanRequest"/> отличает щелчок человека от работы
    /// по расписанию — и это единственная разница между ними: щелчок ПРИМЕНЯЕТ окна пика со
    /// страницы, расписание их только показывает (цены — показ, окна — по подтверждению).
    ///
    /// ⚠️ **«Свободен» становится видимым ПОСЛЕДНИМ** — тот же порядок, что у баланса и по той же
    /// причине (дефект, найденный 26.09.2026): наблюдатель, ждущий <c>!Busy</c>, иначе успевал бы
    /// прочитать состояние, которого ещё нет. Флаг снимается в <c>finally</c> — он обязан сняться
    /// и при исключении, иначе одна упавшая попытка оставила бы панель «занятой» навсегда.
    /// </summary>
    private void Refresh(DateTimeOffset now, bool humanRequest)
    {
        if (!Allowed || Busy) return;

        var language = Language();
        var url = PricingDecisions.Url(language);

        Busy = true;
        Changed?.Invoke();

        var client = _client;

        Task.Run(() => client.Query(language, now))
            .ContinueWith(task => _dispatch(() =>
            {
                try
                {
                    Result = task.IsCompletedSuccessfully
                        ? task.Result
                        : PricingResult.Failed($"{task.Exception?.GetType().Name}", url, language, now);

                    _lastCheck = now;

                    _log(string.Format(
                        CultureInfo.CurrentCulture,
                        PanelStrings.PriceLogFormat,
                        Result.Ok
                            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.PriceLogRowsFormat, Result.Rows.Count)
                            : Result.Error));

                    if (!Result.Ok) return;

                    // Отметка времени — ВСЕГДА, и у автоматического разбора тоже: именно ею
                    // закрыто правило «не чаще раза в сутки».
                    _rememberCheckedAt(PricingDecisions.Stamp(now));

                    // И САМА ТАБЛИЦА — рядом с отметкой, а не вместо неё: отметка закрывает
                    // суточный гейт, а таблица даёт ЧТО ПОКАЗАТЬ, когда гейт закрыт (после
                    // перезапуска панели). Без этой записи цены пропадали до нажатия «Обновить»
                    // — дефект, найденный владельцем 28.09.2026.
                    _rememberLast(PricingMemory.Encode(Result));

                    // Окна пика — ТОЛЬКО по щелчку человека: это и есть подтверждение, которого
                    // требует правило панели 1.x. Ночной разбор расписание не меняет.
                    if (humanRequest && Result.Windows.Count > 0)
                    {
                        _rememberWindows(PricingWindows.Encode(Result.Windows, now));
                    }

                    // ИСТОРИЯ — ПОСЛЕ памяти таблицы и до снятия «занят»: запись о найденном
                    // изменении и шарик — ОДНО событие, и оба обязаны появиться раньше, чем
                    // наблюдатель увидит «свободен» (тот же порядок, что у результата разбора).
                    RememberHistory();
                }
                finally
                {
                    Busy = false;
                    Changed?.Invoke();
                }
            }));
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>
    /// ЗАПИСАТЬ ТОЧКУ ИЗМЕНЕНИЯ — и, если изменение нашлось, сказать о нём шариком.
    ///
    /// **Три случая, и у каждого своё поведение.**
    ///
    /// * **истории нет** — пишется только молчаливая базовая точка: без неё первое изменение
    ///   не с чем сравнить. Ни записи, ни шарика;
    /// * **ничего не изменилось** — файл НЕ ТРОГАЕТСЯ ВООБЩЕ: прежняя запись остаётся текущей,
    ///   и панель не переписывает диск на каждой суточной проверке;
    /// * **изменилось** — появляется запись, и вместе с ней шарик, называющий ЧТО именно.
    ///   Шарик и запись — одно событие: нет записи — нет и шарика.
    ///
    /// ⚠️ **Сравнение — с предыдущей ЗАПИСЬЮ**, а не с прошлой проверкой (<see cref="PricingHistoryStore.Check"/>).
    /// ⚠️ **Шарик идёт общей дверью** (<c>App.Notify</c> → <see cref="IsolationRules.ShouldNotify"/>):
    /// своего пути к рабочему столу владельца у истории нет.
    /// ⚠️ Окна пика здесь только ЗАПИСЫВАЮТСЯ как то, что напечатано на странице: расписание
    /// применяет щелчок человека (см. выше), и истории оно не подчиняется.
    /// </summary>
    private void RememberHistory()
    {
        var snapshot = PricingSnapshot.From(Result);
        if (snapshot is null) return;

        var before = History;
        var after = PricingHistoryStore.Check(before, snapshot);

        var anchored = before.Anchor is null && after.Anchor is not null;
        var appended = after.Changes.Count > before.Changes.Count;

        // Ни точки, ни изменения — работы нет, и диск трогать незачем.
        if (!anchored && !appended) return;

        if (!_historyFiles.Persists)
        {
            _log(PanelStrings.PricingHistoryUnsavedLog);
        }
        else
        {
            _historyFiles.Write(PricingHistoryStore.Encode(after));
        }

        History = after;

        if (anchored)
        {
            _log(string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.PricingHistoryAnchorLogFormat,
                PricingDecisions.CheckedText(PricingDecisions.Stamp(after.Anchor!.At))));
        }

        if (!appended) return;

        _log(string.Format(
            CultureInfo.CurrentCulture, PanelStrings.PricingHistorySavedLogFormat, after.Changes.Count));

        // «Самая старая запись ушла» — словами и в журнал: молчаливая пропажа записи выглядела бы
        // порчей файла, а предел в 30 изменений — решение, а не случайность.
        if (after.Dropped > before.Dropped)
        {
            _log(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.PricingHistoryDroppedLogFormat, PricingHistory.MaxChanges));
        }

        _notify(
            NoticeKind.PricingChanged,
            PricingHistoryText.NoticeTitle(),
            PricingHistoryText.NoticeText(after.Changes[^1]));
    }
}
