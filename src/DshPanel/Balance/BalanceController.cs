using System.Globalization;
using Avalonia.Threading;
using DshPanel.Agents;
using DshPanel.Isolation;
using DshPanel.Peak;
using DshPanel.Settings;
using DshPanel.Shell;

namespace DshPanel.Balance;

/// <summary>
/// Решения о сообщениях по балансу и пикам — ЧИСТЫЕ функции. Именно они перебираются тестами
/// по всем сочетаниям, а не «проверяются один раз глазами».
///
/// Два сообщения и два правила, оба перенесены из v1 как решения:
///
/// * **низкий баланс** — предупреждаем ОДИН раз, пока баланс не поднимется выше порога.
///   Иначе человек получал бы один и тот же шарик каждые пять минут и перестал бы их читать;
/// * **приближение пика** — предупреждаем один раз НА КАЖДОЕ начало пика, за указанное
///   в настройках время. Это новое правило (в v1 его не было): «уведомить о наступлении пика
///   за указанное время» — решение владельца 24.09.2026.
/// </summary>
public static class BalanceDecisions
{
    /// <summary>Баланс ниже порога. <c>null</c> (неизвестен) порогом не считается: не знаем — молчим.</summary>
    public static bool IsBelow(decimal? total, decimal threshold) => total is not null && total.Value < threshold;

    public static bool ShouldWarnLow(bool enabled, decimal? total, decimal threshold, bool alreadyWarned) =>
        enabled && IsBelow(total, threshold) && !alreadyWarned;

    /// <summary>
    /// Пора предупредить, что скоро пик.
    ///
    /// Молчим, когда: предупреждения выключены (время 0 или меньше), пик уже идёт (предупреждать
    /// поздно), следующее переключение — не начало пика (значит будет дешевле, а это хорошая
    /// новость, а не повод торопиться), времени больше, чем просили, и когда про ЭТО начало уже
    /// предупреждали.
    ///
    /// Момент — <see cref="DateTime"/> в UTC, как и в <see cref="PeakState"/>. Именно так, а не
    /// <c>DateTimeOffset</c>: между этими типами есть НЕЯВНОЕ преобразование, которое считает
    /// <c>DateTime</c> местным временем, — и «момент» тихо превратился бы в другое число.
    /// </summary>
    public static bool ShouldWarnPeak(
        int leadMinutes,
        bool inPeak,
        PeakMoment kind,
        int minutesUntilNext,
        DateTime? nextSwitch,
        DateTime? alreadyWarnedFor)
    {
        if (leadMinutes <= 0) return false;
        if (inPeak) return false;
        if (kind != PeakMoment.PeakStart) return false;
        if (nextSwitch is null) return false;
        if (minutesUntilNext <= 0 || minutesUntilNext > leadMinutes) return false;

        return alreadyWarnedFor != nextSwitch;
    }
}

/// <summary>Что окно и значок знают про баланс активного агента и про окна пика.</summary>
public interface IBalanceControl
{
    AgentProfile Agent { get; }

    BalanceResult Result { get; }

    PeakState Peak { get; }

    bool Busy { get; }

    /// <summary>Можно ли вообще читать ключ и ходить в сеть в этом прогоне.</summary>
    bool Allowed { get; }

    /// <summary>Спросить баланс сейчас (в фоне). Повторный вызов во время запроса ничего не делает.</summary>
    void Refresh();

    /// <summary>
    /// Активный агент СМЕНИЛСЯ: перечитать и баланс, и окна пика.
    ///
    /// Отдельной дверью, а не «следующим тактом часов», по двум причинам. Первая: такт бывает
    /// раз в полминуты, и человек, выбравший агента, полминуты видел бы баланс ПРЕЖНЕГО агента —
    /// то есть неправду про того, кого только что выбрал. Вторая: часы пересчитывают пик
    /// по расписанию, а смена агента меняет само расписание, и до такта оно осталось бы старым.
    ///
    /// Смену агента ЗАПИСЫВАЕТ не контроллер, а владелец настроек (см. <c>ISettingsControl</c>):
    /// у значения одно место правды — файл настроек, — и эта дверь только перечитывает то,
    /// что там уже лежит.
    /// </summary>
    void AgentChanged();

    /// <summary>Один такт часов: пересчитать пик, при необходимости обновить баланс и предупредить.</summary>
    void Tick(DateTimeOffset now);

    event Action? Changed;

    string StatusText { get; }

    string DetailText { get; }

    /// <summary>«Пик: полная цена» или «Вне пика: вдвое дешевле».</summary>
    string PeakText { get; }

    /// <summary>Когда следующее переключение — по местному времени.</summary>
    string NextText { get; }

    /// <summary>
    /// КОРОТКАЯ строка главного окна: «Пики: проверено 24.09.2026» — когда окна пика проверены.
    ///
    /// Слова владельца 27.09.2026: *«Там где написано „окна пика“, проверено и дата с временем
    /// нужно… остальная информация не нужна в принципе»*. Поэтому здесь НЕТ ни источника (URL),
    /// ни расписания, ни дней недели: они остались в настройках и в отдельном окне
    /// «Пики и тарифы» (там работает <see cref="PeakDecisions.SourceText"/>).
    /// </summary>
    string PeakCheckedText { get; }

    /// <summary>
    /// Тон состояния баланса — тот же, что у строки агента в меню значка: баланс известен —
    /// зелёный, неизвестен или не спрашивали — янтарный («внимание», а не «авария»).
    ///
    /// Считает его КОНТРОЛЛЕР, а не окно: он один знает, есть ли ответ, доступен ли ключ и кто
    /// активный агент. Второе такое решение в окне однажды разошлось бы с меню значка — а человек
    /// видит оба цвета одновременно.
    /// </summary>
    TrayTone Tone { get; }
}

/// <summary>
/// Баланс и окна пика АКТИВНОГО агента — то, что человек видит крупно в окне и в подсказке значка.
///
/// Устройство простое и намеренно скучное:
///
/// * **активный агент один.** Он берётся из настроек, и по нему идут и баланс, и пики. Переключатель
///   в настройках уже есть (решение владельца: агентов будет несколько), но пока список из одного —
///   DeepSeek. Отдельное окно со сводкой по всем агентам — задача следующих версий;
/// * **часы одни на всё.** Раз в полминуты контроллер пересчитывает состояние пика (это чистая
///   арифметика, сети не нужно) и решает, пора ли обновить баланс по расписанию. Так предупреждение
///   о пике работает даже тогда, когда автообновление баланса выключено: оно про ВРЕМЯ, а не про деньги;
/// * **ключ читается в момент запроса** и только если прогон имеет на это право. В прогоне проверки
///   контроллер не читает ни файла ключа, ни сети — иначе `--shell-selftest` ходил бы в интернет
///   с ключом владельца;
/// * **сообщения отправляются наружу через <paramref name="notify"/>**, а решение о показе принимает
///   вызывающий: там стоит общая дверь изоляции (`IsolationRules.ShouldNotify`). Контроллер
///   не показывает ничего сам — поэтому и проверяется без значка и рабочего стола.
/// </summary>
public sealed class BalanceController : IBalanceControl, IDisposable
{
    /// <summary>Как часто пересчитываются часы: полминуты. Пик — арифметика, чаще незачем.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly IBalanceClient _client;
    private readonly string _credentialsPath;
    private readonly Func<PanelSettings> _settings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<Action> _dispatch;
    private readonly Action<string> _log;
    private readonly Action<NoticeKind, string, string> _notify;

    private DispatcherTimer? _timer;
    private DateTimeOffset? _lastCheck;
    private DateTime? _peakWarnedFor;
    private bool _lowWarned;

    /// <summary>
    /// Поколение активного агента. Смена агента его увеличивает, а запрос запоминает своё
    /// поколение на старте: ответ, вернувшийся из другого поколения, — про ПРЕЖНЕГО агента,
    /// и показывать его нельзя (дефект, найденный прогоном под загрузкой 29.09.2026).
    /// </summary>
    private int _generation;

    /// <summary>
    /// Смена агента случилась, пока запрос был в пути. Тогда нового агента надо спросить СРАЗУ,
    /// как панель освободится: сам по себе <see cref="Refresh()"/> во время запроса — молчаливый
    /// пропуск (<c>Busy</c>), и человек до такта часов остался бы без баланса нового агента.
    /// </summary>
    private bool _askAgainAfterBusy;

    public BalanceController(
        IBalanceClient client,
        string credentialsPath,
        Func<PanelSettings> settings,
        bool allowed,
        Func<DateTimeOffset> clock,
        Action<Action> dispatch,
        Action<string> log,
        Action<NoticeKind, string, string> notify,
        bool isolatedRun = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _credentialsPath = credentialsPath ?? string.Empty;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));

        Allowed = allowed;
        IsolatedRun = isolatedRun;
        Result = BalanceResult.NotRequested(Agent.Id);
        Peak = PeakDecisions.State(Agent, _clock());
    }

    /// <summary>Прогон изолированный: автоматической работы по расписанию в нём нет.</summary>
    public bool IsolatedRun { get; }

    public AgentProfile Agent => AgentCatalog.Find(_settings().ActiveAgent);

    public BalanceResult Result { get; private set; }

    public PeakState Peak { get; private set; }

    public bool Busy { get; private set; }

    public bool Allowed { get; }

    public event Action? Changed;

    public string StatusText
    {
        get
        {
            if (!Allowed) return PanelStrings.BalanceLocked;
            if (!Result.Ok) return PanelStrings.BalanceNotRequested;
            if (!Result.Available) return PanelStrings.BalanceUnavailable;

            return Result.Summary;
        }
    }

    public string DetailText
    {
        get
        {
            // Причина отказа уже сказана в крупной строке — второй раз её не повторяем.
            if (!Allowed) return string.Empty;

            if (!Result.Ok)
            {
                return Result.Error.Length > 0
                    ? string.Format(CultureInfo.CurrentCulture, PanelStrings.BalanceFailedFormat, Result.Error)
                    : PanelStrings.BalanceNotRequested;
            }

            var checkedAt = Result.CheckedAt == default
                ? string.Empty
                : " · " + string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.BalanceCheckedFormat, Result.CheckedAt.ToString("HH:mm"));

            return Result.Detail.Length > 0 ? Result.Detail + checkedAt : checkedAt.TrimStart(' ', '·');
        }
    }

    public string PeakText => PeakTable.NowText(Peak);

    public string NextText => PeakTable.NextText(Peak);

    /// <summary>
    /// Короткая строка «когда таблица цен проверена» — из ОДНОЙ строки словаря и решения
    /// <see cref="PeakDecisions.CheckedText"/>: дату приводит к читаемому виду чистая функция,
    /// а не контроллер.
    /// </summary>
    public string PeakCheckedText =>
        string.Format(
            CultureInfo.CurrentCulture,
            PanelStrings.PeakCheckedFormat,
            PeakDecisions.CheckedText(Agent));

    /// <summary>
    /// Тон строки баланса — то же решение, что у строки агента в меню значка
    /// (<see cref="TrayStatus.AgentTone"/>): сумма есть — зелёный, суммы нет — янтарный.
    /// «Не спрашивали» и «ключ не принят» — это «внимание», а не «авария»: панель не сломана,
    /// она просто ещё не знает числа.
    /// </summary>
    public TrayTone Tone => TrayStatus.AgentTone(
        Agent.Title,
        Result.Ok && Result.Available ? Result.Summary : string.Empty);

    /// <summary>Запустить часы. В прогоне без права на ключ часов не будет вовсе — и это в журнал.</summary>
    public void Start()
    {
        if (!Allowed)
        {
            _log(PanelStrings.BalanceLocked);
            return;
        }

        _timer ??= new DispatcherTimer { Interval = TickInterval };
        if (_timer.IsEnabled) return;

        _timer.Tick += (_, _) => Tick(_clock());
        _timer.Start();
        _log(PanelStrings.PanelLogBalanceClock);

        // И сразу первый такт: человек открыл панель и хочет видеть баланс сейчас, а не через
        // полминуты. Дальше — по расписанию из настроек.
        Tick(_clock());
    }

    public void Tick(DateTimeOffset now)
    {
        Peak = PeakDecisions.State(Agent, now);

        var settings = _settings();

        // Фоновое обновление баланса — это РАБОТА ПО РАСПИСАНИЮ, и её закрывает общий предикат
        // изоляции (`ShouldRunScheduledWork`), а не право на ключ: право отвечает на вопрос
        // «можно ли прочитать ключ», а предикат — на вопрос «делает ли панель это сама».
        // Явная просьба человека (кнопка «Обновить баланс») этим не закрыта — она не автоматика.
        var scheduled = IsolationRules.ShouldRunScheduledWork(IsolatedRun);

        if (scheduled
            && settings.BalanceAutoRefresh
            && (_lastCheck is null
                || (now - _lastCheck.Value).TotalMinutes >= Math.Max(1, settings.BalanceRefreshMinutes)))
        {
            Refresh(now);
        }

        WarnAboutPeak(now, settings);
        Changed?.Invoke();
    }

    /// <summary>Кнопка «Обновить баланс»: момент берётся у часов панели.</summary>
    public void Refresh() => Refresh(_clock());

    /// <summary>
    /// Активный агент сменился (его записал владелец настроек): показать ЧЕСТНО, что баланса
    /// нового агента ещё нет, пересчитать его окна пика и спросить баланс заново.
    ///
    /// Прежний ответ НЕ оставляем: он про ДРУГОГО агента, и «12.34 CNY» под именем нового
    /// читалось бы как его баланс. Поэтому ответ сбрасывается в «не спрашивали», и следом идёт
    /// настоящий запрос — если прогон имеет право читать ключ. Без права (прогон проверки,
    /// съёмка кадра) запроса не будет вовсе, и это то же поведение, что у кнопки «Обновить»:
    /// смена агента — ПРОСЬБА человека, а не работа по расписанию, поэтому изоляция её не глушит
    /// (глушит она автоматику — <c>IsolationRules.ShouldRunScheduledWork</c>).
    ///
    /// Предупреждения сбрасываются вместе с ответом: «о низком балансе уже сказано» и «о начале
    /// пика уже предупреждали» — это утверждения про ПРЕЖНЕГО агента, и переносить их на нового
    /// значит молча лишить человека обоих сообщений.
    ///
    /// ⚠️ **Сброса ответа МАЛО, если запрос уже в пути** (дефект, найденный прогоном под загрузкой
    /// 29.09.2026, и он ровно тот, от которого эта дверь и защищает). Пока ответ прежнего агента
    /// летит, продолжение запроса дописывало <c>Result</c> уже ПОСЛЕ сброса — и «12.34 CNY»
    /// прежнего агента вставало под именем нового. Поэтому смена агента растит ПОКОЛЕНИЕ
    /// (<see cref="_generation"/>), а продолжение запроса сверяет своё поколение с текущим.
    /// </summary>
    public void AgentChanged()
    {
        var agent = Agent;

        // Поколение растёт ДО сброса: ответ, уже летящий от прежнего агента, обязан стать чужим
        // в тот же миг.
        _generation++;

        Result = BalanceResult.NotRequested(agent.Id);
        Peak = PeakDecisions.State(agent, _clock());

        _lastCheck = null;
        _lowWarned = false;
        _peakWarnedFor = null;

        Changed?.Invoke();

        if (!Allowed) return;

        // Запрос уже идёт — но он про прежнего агента, и ждать его нечего. Своим ходом
        // «Обновить» во время запроса был бы МОЛЧАЛИВЫМ пропуском, поэтому помним, что нового
        // агента надо спросить, и спросим сразу, как панель освободится.
        if (Busy)
        {
            _askAgainAfterBusy = true;
            return;
        }

        Refresh();
    }

    /// <summary>
    /// Спросить баланс. Момент приходит СНАРУЖИ одним значением — и для запроса, и для отметки
    /// «когда спрашивали». Двух источников «сейчас» здесь быть не должно: с ними расписание
    /// обновления начинает зависеть от того, кто кого обогнал (на этом и поймала проверка).
    ///
    /// ⚠️ **«Свободен» становится видимым ПОСЛЕДНИМ** (дефект порядка, найденный 26.09.2026).
    /// До этого <c>Busy = false</c> стояло первой строкой продолжения — и наблюдатель, который
    /// ждёт <c>!Busy</c> (окно, проверка), успевал увидеть «панель свободна» РАНЬШЕ, чем отправлено
    /// предупреждение о низком балансе: он читал состояние, которого на самом деле ещё не было.
    /// Поэтому теперь вся работа идёт до снятия флага, а флаг снимается в <c>finally</c> — он обязан
    /// сняться и при исключении, иначе одна упавшая отправка сообщения оставила бы панель «занятой»
    /// навсегда, и баланс больше не обновлялся бы вовсе.
    ///
    /// ⚠️ **Ответ из ПРЕЖНЕГО поколения не показывается вовсе** (тот же дефект, что описан
    /// в <see cref="AgentChanged"/>): запрос запоминает <see cref="_generation"/> на старте,
    /// а продолжение сверяет его с текущим. Пропускаются все три следствия ответа — сам
    /// <c>Result</c>, отметка «когда спрашивали» и предупреждение о низком балансе: все они
    /// про прежнего агента. И если смена агента ждала, пока панель освободится, запрос за нового
    /// агента уходит здесь же — иначе он не ушёл бы до такта часов.
    /// </summary>
    private void Refresh(DateTimeOffset now)
    {
        if (!Allowed || Busy) return;

        var agent = Agent;
        var generation = _generation;
        Busy = true;
        Changed?.Invoke();

        var credentialsPath = _credentialsPath;
        var client = _client;

        Task.Run(() =>
        {
            var lookup = CredentialsKey.Read(credentialsPath, agent.KeyName);

            return lookup.Found
                ? client.Query(agent, lookup.Value, now)
                : BalanceResult.Failed(lookup.Problem, agent.Id, now);
        })
        .ContinueWith(task => _dispatch(() =>
        {
            try
            {
                if (generation != _generation) return;

                Result = task.IsCompletedSuccessfully
                    ? task.Result
                    : BalanceResult.Failed($"{task.Exception?.GetType().Name}", agent.Id, now);

                _lastCheck = now;

                if (Result.Ok)
                {
                    _log(string.Format(
                        CultureInfo.CurrentCulture, PanelStrings.BalanceLogFormat,
                        Result.Available ? Result.Summary : PanelStrings.BalanceUnavailable));
                }
                else
                {
                    _log(string.Format(
                        CultureInfo.CurrentCulture, PanelStrings.BalanceLogFormat, Result.Error));
                }

                WarnAboutLowBalance(_settings());
            }
            finally
            {
                // Последним действием — и обе двери наружу открываются здесь же: сначала «свободен»,
                // потом «состояние изменилось», чтобы подписчик, читающий по событию, видел уже
                // и снятый флаг, и отправленное предупреждение.
                Busy = false;
                Changed?.Invoke();

                // Смена агента ждала этого мгновения: спрашиваем нового агента теперь, своим ходом.
                if (_askAgainAfterBusy)
                {
                    _askAgainAfterBusy = false;
                    Refresh(_clock());
                }
            }
        }));
    }

    /// <summary>
    /// Низкий баланс: предупреждаем один раз, пока он не поднимется выше порога. Сброс делается
    /// здесь же — иначе после первого предупреждения человек не узнал бы о втором падении.
    /// </summary>
    private void WarnAboutLowBalance(PanelSettings settings)
    {
        if (!settings.BalanceWarnEnabled) return;

        if (!BalanceDecisions.IsBelow(Result.Total, settings.BalanceWarnThreshold))
        {
            _lowWarned = false;
            return;
        }

        if (!BalanceDecisions.ShouldWarnLow(true, Result.Total, settings.BalanceWarnThreshold, _lowWarned)) return;

        _lowWarned = true;

        var currency = Result.Currency.Length > 0 ? Result.Currency : string.Empty;
        var text = string.Format(
            CultureInfo.CurrentCulture,
            PanelStrings.NotifyLowBalanceFormat,
            Result.Total!.Value.ToString(CultureInfo.CurrentCulture),
            currency,
            settings.BalanceWarnThreshold.ToString(CultureInfo.CurrentCulture));

        _notify(NoticeKind.BalanceLow, PanelStrings.NotifyLowBalanceTitle, text);
    }

    /// <summary>Скоро пик: предупреждаем один раз на каждое начало пика, за указанное время.</summary>
    private void WarnAboutPeak(DateTimeOffset now, PanelSettings settings)
    {
        // Момент — в локальную переменную, и та же причина, что в NextText: без этого компилятор
        // не может доказать непустоту и выдаёт CS8629.
        var next = Peak.NextSwitchUtc;

        if (!BalanceDecisions.ShouldWarnPeak(
                settings.PeakNotifyMinutes,
                Peak.InPeak,
                Peak.NextKind,
                Peak.MinutesUntilNext,
                next,
                _peakWarnedFor)
            || next is null)
        {
            return;
        }

        _peakWarnedFor = next;

        var at = PeakDecisions.LocalClock(next.Value, PeakDecisions.OffsetMinutesAt(next.Value));
        var text = string.Format(
            CultureInfo.CurrentCulture,
            PanelStrings.NotifyPeakSoonFormat,
            PeakDecisions.FormatSpan(Peak.MinutesUntilNext),
            at);

        _notify(NoticeKind.PeakApproaching, PanelStrings.NotifyPeakSoonTitle, text);
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }
}
