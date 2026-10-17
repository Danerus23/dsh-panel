using System.Globalization;
using Avalonia.Threading;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Pricing;
using DshPanel.Settings;
using DshPanel.Shell;

namespace DshPanel.Update;

/// <summary>Что окно знает про обновление: последний выпуск, идёт ли проверка и что из этого вышло.</summary>
public interface IUpdateControl
{
    /// <summary>Что удалось узнать. До первой проверки — «не спрашивали».</summary>
    UpdateRelease Result { get; }

    /// <summary>Проверка идёт прямо сейчас.</summary>
    bool Busy { get; }

    /// <summary>Можно ли вообще ходить в сеть в этом прогоне.</summary>
    bool Allowed { get; }

    /// <summary>Версия панели, с которой сравнивается выпуск, — числовой частью.</summary>
    string Current { get; }

    /// <summary>Выпуск новее этой панели.</summary>
    bool Newer { get; }

    /// <summary>Эта версия пропущена человеком — о ней не напоминаем.</summary>
    bool Skipped { get; }

    /// <summary>
    /// Откуда взято состояние — СЛОВАМИ: адрес выпуска и время проверки; а когда выпуска нет —
    /// почему именно нет (прогон проверки, ещё не проверяли, не вышло). Молчания здесь быть
    /// не может: пустая строка без объяснения читается как «панель сломалась».
    /// </summary>
    string SourceText { get; }

    /// <summary>Короткая строка состояния для окна и подсказки: «доступна версия 2.1.0» и подобное.</summary>
    string StatusText { get; }

    /// <summary>
    /// СТРОКА О ПОСЛЕДНЕЙ ПОПЫТКЕ: когда панель стучалась и чем это кончилось. Пусто — попытки
    /// не было или она удалась (про удачную говорит <see cref="SourceText"/>).
    ///
    /// Отдельно от <see cref="SourceText"/>, потому что это РАЗНЫЕ правды: источник состояния —
    /// последний УДАЧНЫЙ ответ (он не затирается отказом), а попытка — само дело, которое было.
    /// </summary>
    string AttemptText { get; }

    /// <summary>
    /// СТРОКА ОБНОВЛЕНИЯ ДЛЯ МЕНЮ ЗНАЧКА — четвёртая строка состояния, рядом с сервером, агентом
    /// и тарифом, и с таким же ТОНОМ (цвет там означает то же, что слова).
    ///
    /// Отдельно от <see cref="StatusText"/> ровно одним случаем: когда новее ничего нет, в меню
    /// читается «Обновление: проверено 28.09.2026, 14:32», а не «Последняя версия». Слово
    /// «Обновление» в меню нужно: строка стоит сама по себе, и дата без хозяина читалась бы
    /// как что угодно.
    /// </summary>
    TrayStatusLine TrayLine { get; }

    /// <summary>
    /// Шарик о новом выпуске. Событие, а не действие: показывает его трей, а решает контроллер
    /// (<see cref="UpdateDecisions.ShouldAnnounce"/>) — второй такой проверки в панели быть не должно.
    /// </summary>
    event Action<string>? Announce;

    /// <summary>
    /// СОСТОЯНИЕ ИЗМЕНИЛОСЬ — окну пора перерисоваться: проверка началась, закончилась, пришёл
    /// ответ. Второго способа узнать об этом у окна нет: оно не спрашивает по часам, а рисует то,
    /// что ему сказали.
    /// </summary>
    event Action? Changed;

    /// <summary>Спросить выпуски сейчас (в фоне). Повторный вызов во время запроса ничего не делает.</summary>
    void Check();
}

/// <summary>
/// ОБНОВЛЕНИЕ ПАНЕЛИ — ПЕРВЫЙ СРЕЗ: проверка выпусков и извещение о новом.
///
/// **Ни скачивания, ни замены файлов здесь нет и не будет** — их делает второй срез. Это ядро
/// оставлено так, чтобы он лёг сверху: адрес выпуска и разбор ответа живут отдельно, решения
/// о версии и заметках — отдельно, а часы и права — здесь.
///
/// **Два повода, и они разные.**
///
/// * **Автоматическая проверка** — решение владельца: при запуске панели, но не чаще раза
///   в сутки. Поэтому часы идут ЧАС, а решение «пора» принимает чистая функция
///   (<see cref="UpdateDecisions.ShouldCheck"/>) по отметке последней проверки из файла настроек.
///   Отметка в файле, а не в памяти: иначе «раз в сутки» превращалось бы в «при каждом запуске».
/// * **Просьба человека** — «Проверить сейчас». Это не автоматика, и суточным гейтом она
///   не закрыта: человек попросил — панель пошла.
///
/// **Право то же, что у страницы цен и баланса** (<c>RunRights.LocalData</c>): прогон проверки
/// ходит в GitHub ничуть не охотнее, чем за ценами, — «сеть от имени владельца» это его право,
/// а не наше. Право приходит ОБЯЗАТЕЛЬНЫМ параметром конструктора.
///
/// ⚠️ **Заметка о проверке пишется ВСЕГДА и ДО того, как о выпуске рассказано.** Иначе прогон,
/// у которого выпуск новее и шарик уже показан, при следующем запуске рассказал бы о нём снова:
/// суточный гейт закрыт отметкой, а «один раз на версию» — той же отметкой.
/// </summary>
public sealed class UpdateController : IUpdateControl, IDisposable
{
    private readonly IUpdateClient _client;
    private readonly Func<PanelSettings> _settings;
    private readonly Func<string> _language;
    private readonly Func<string> _current;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<Action> _dispatch;
    private readonly Action<string> _log;
    private readonly Action<UpdateTraces> _remember;
    private readonly Action<string> _rememberCheckedAt;
    private readonly Action<UpdateAttempt> _rememberAttempt;

    private DispatcherTimer? _timer;
    private DateTimeOffset? _lastCheck;
    private string _checkedNotes = string.Empty;

    /// <summary>О «молчании из-за отступа» говорим ОДИН раз на период ожидания, а не каждый такт.</summary>
    private bool _waitLogged;

    /// <summary>
    /// ⚠️ **ДВЕРЬ ПОПЫТКИ — ОБЯЗАТЕЛЬНЫЙ ДОВОД, как право.** Сначала у неё была заглушка
    /// по умолчанию, и настоящий запуск панели её не подключил (найдено своей сверкой 11.10.2026):
    /// в памяти попытка жила, а в файл настроек не попадала — значит, отказ **не переживал
    /// перезапуск**, и следующий запуск панели снова стучался в выеденный лимит GitHub, то есть
    /// дефект владельца оставался в живом пути. Обязательный довод делает это не внимательностью,
    /// а сборкой: забыть дверь больше нельзя.
    /// </summary>
    public UpdateController(
        IUpdateClient client,
        Func<PanelSettings> settings,
        Func<string> language,
        Func<string> current,
        bool allowed,
        Func<DateTimeOffset> clock,
        Action<Action> dispatch,
        Action<string> log,
        Action<UpdateTraces> remember,
        Action<string> rememberCheckedAt,
        bool isolatedRun,
        Action<UpdateAttempt> rememberAttempt)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _language = language ?? throw new ArgumentNullException(nameof(language));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _remember = remember ?? throw new ArgumentNullException(nameof(remember));
        _rememberCheckedAt = rememberCheckedAt ?? throw new ArgumentNullException(nameof(rememberCheckedAt));
        _rememberAttempt = rememberAttempt ?? throw new ArgumentNullException(nameof(rememberAttempt));

        Allowed = allowed;
        IsolatedRun = isolatedRun;
        Current = UpdateDecisions.Numeric(current());

        // СОСТОЯНИЕ ПРОШЛОЙ ПРОВЕРКИ ЧИТАЕТСЯ СРАЗУ. Без этого окно и строка в меню значка
        // говорили бы «ещё не проверяли» при перезапуске панели в те же сутки: суточный гейт
        // закрыт отметкой, проверки не будет, а вчерашний ответ так и остался бы невидимым —
        // ровно то, чего человек от панели не ждёт. Настройки — место, где след лежит.
        Result = Seeded();

        // И ПОПЫТКА ЧИТАЕТСЯ СРАЗУ, по той же причине: без неё окно после отказа выглядело бы
        // пустым («ещё не проверялось»), а часы не знали бы, что идёт отступ после лимита GitHub.
        Attempt = SeededAttempt();

        // Заметки берутся у ТОГО ЖЕ состояния: окно показывает их сразу, не дожидаясь проверки,
        // и второй выемки того же текста в панели не заводится.
        _checkedNotes = Result.Notes;
    }

    /// <summary>Прогон изолированный: автоматической работы по расписанию в нём нет.</summary>
    public bool IsolatedRun { get; }

    public bool Allowed { get; }

    public UpdateRelease Result { get; private set; }

    /// <summary>
    /// ПОСЛЕДНЯЯ ПОПЫТКА — когда была и чем кончилась (в этом прогоне или из настроек).
    ///
    /// ⚠️ Отдельно от <see cref="Result"/> нарочно: `Result` — **последний удачный ответ**,
    /// и он НЕ затирается отказом. Иначе отказ сети уносил бы с собой и версию, и заметки,
    /// и окно выглядело бы как «ещё не проверялось» после настоящей работы.
    /// </summary>
    public UpdateAttempt Attempt { get; private set; } = UpdateAttempt.None;

    public bool Busy { get; private set; }

    public string Current { get; }

    public event Action<string>? Announce;

    public event Action? Changed;

    /// <summary>Выпуск новее этой панели — по числам, а не по строкам.</summary>
    public bool Newer => Result.Ok && UpdateDecisions.IsNewer(Result.Latest, Current);

    /// <summary>Человек сказал «пропустить эту версию» — и это его решение, а не забывчивость панели.</summary>
    public bool Skipped =>
        Result.Ok
        && !string.IsNullOrWhiteSpace(_settings().UpdateSkippedVersion)
        && !UpdateDecisions.IsNewer(Result.Latest, _settings().UpdateSkippedVersion)
        && !UpdateDecisions.IsNewer(_settings().UpdateSkippedVersion, Result.Latest);

    /// <summary>
    /// ОТКУДА ВЗЯТО СОСТОЯНИЕ — словами. Три разных ответа, и каждый называет свою причину:
    /// права нет, не проверяли вовсе, проверено когда.
    ///
    /// ⚠️ **Про неудачную попытку здесь НЕ говорится** — и это не потеря: состояние берётся
    /// из последнего УДАЧНОГО ответа, а попытка называется своей строкой
    /// (<see cref="AttemptText"/>). Так отказ сети не стирает ни версию, ни дату проверки.
    /// </summary>
    public string SourceText
    {
        get
        {
            if (!Allowed) return PanelStrings.UpdateLocked;

            if (!Result.Ok) return PanelStrings.UpdateNeverChecked;

            return string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.UpdateCheckedFormat,
                PricingDecisions.CheckedText(PricingDecisions.Stamp(Result.CheckedAt)));
        }
    }

    /// <summary>
    /// СТРОКА О ПОПЫТКЕ: когда панель стучалась и чем это кончилось. Пусто, когда попытки не было
    /// или она удалась (у удачной про время и исход говорит <see cref="SourceText"/>).
    ///
    /// **Зачем нужна.** Раньше отказ сети не оставлял в окне НИ СЛЕДА: панель показывала
    /// «обновление ещё не проверялось» после настоящей работы. Теперь прежний ответ стоит
    /// с датой, а рядом сказано, что новая попытка не вышла и когда её повторят.
    /// </summary>
    public string AttemptText => UpdateLines.AttemptLine(
        Attempt,
        Attempt.Failed ? UpdateDecisions.NextAttempt(Attempt) : (DateTimeOffset?)null);


    /// <summary>
    /// Короткая строка состояния: «последняя версия», «доступна версия 2.1.0» или «эта версия
    /// пропущена». Порядок ответов — от самого важного к самому спокойному: пропуск важнее
    /// новизны (человек уже ответил), новизна важнее «всё в порядке».
    /// </summary>
    public string StatusText
    {
        get
        {
            if (!Result.Ok) return SourceText;

            if (Skipped) return string.Format(
                CultureInfo.CurrentCulture, PanelStrings.UpdateSkippedFormat, Result.Latest);

            if (Newer) return string.Format(
                CultureInfo.CurrentCulture, PanelStrings.UpdateAvailableFormat, Result.Latest);

            return PanelStrings.UpdateLatest;
        }
    }

    /// <summary>
    /// Строка обновления для меню значка. Тот же порядок ответов, что у <see cref="StatusText"/>,
    /// и та же причина: пропуск — ответ человека, и он важнее новизны. Отличается одним случаем —
    /// «новее ничего нет» здесь звучит датой проверки, потому что в меню эта строка стоит без окна
    /// и обязана отвечать «когда смотрели».
    ///
    /// Тон: новая версия — «внимание» (не беда, но есть о чём подумать), пропуск и «панель
    /// не знает» — серый, «последняя версия» — зелёный. Тот же язык, что у трёх соседних строк.
    /// </summary>
    public TrayStatusLine TrayLine
    {
        get
        {
            if (!Allowed) return new TrayStatusLine(PanelStrings.UpdateLocked, TrayTone.Neutral);

            // В МЕНЮ ЗНАЧКА — САМАЯ СВЕЖАЯ ПРАВДА: строка одна, и «попытка не вышла» в ней важнее
            // прежнего ответа (человек обязан знать, что панель стучалась и не достучалась).
            if (Attempt.Failed) return new TrayStatusLine(
                string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateFailedFormat, Attempt.Error),
                TrayTone.Warning);

            if (!Result.Ok) return new TrayStatusLine(PanelStrings.UpdateNeverChecked, TrayTone.Neutral);

            if (Skipped) return new TrayStatusLine(
                string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateSkippedFormat, Result.Latest),
                TrayTone.Neutral);

            if (Newer) return new TrayStatusLine(
                string.Format(CultureInfo.CurrentCulture, PanelStrings.UpdateAvailableFormat, Result.Latest),
                TrayTone.Warning);

            // Отметки времени нет — дату не выдумываем: «проверено неизвестно когда» хуже, чем
            // честное «ещё не проверялось».
            if (Result.CheckedAt == default) return new TrayStatusLine(PanelStrings.UpdateNeverChecked, TrayTone.Neutral);

            return new TrayStatusLine(
                string.Format(
                    CultureInfo.CurrentCulture,
                    PanelStrings.UpdateTrayCheckedFormat,
                    PricingDecisions.CheckedText(PricingDecisions.Stamp(Result.CheckedAt))),
                TrayTone.Good);
        }
    }

    /// <summary>
    /// СОСТОЯНИЕ ПРОШЛОЙ ПРОВЕРКИ ИЗ НАСТРОЕК. Три правила, и каждое — не украшение:
    ///
    /// * **версии нет или отметка времени не разобралась — «не проверяли»**: показать версию
    ///   без даты значило бы выдумать, что её вообще видели;
    /// * **заметки выбираются ЗАНОВО на языке панели** из сырого тела выпуска: язык панели мог
    ///   смениться между проверками, и заметки обязаны пересобраться, а не ждать следующей
    ///   (так же устроена панель 1.x, и для этого сырое тело и хранится);
    /// * **вложений здесь нет** (<see cref="ReleaseAssets.None"/>): их адреса не хранятся, и окно
    ///   честно говорит «проверьте ещё раз» вместо того, чтобы готовить замену вслепую.
    /// </summary>
    private UpdateRelease Seeded()
    {
        // Прогон без права состояния выпусков не знает ВООБЩЕ: подставить ему вчерашний ответ
        // значило бы показать в прогоне проверки то, чего этот прогон не читал и читать не должен.
        if (!Allowed) return UpdateRelease.NotRequested();

        var settings = _settings();

        var latest = (settings.UpdateLatest ?? string.Empty).Trim();
        if (latest.Length == 0) return UpdateRelease.NotRequested();

        if (!PricingDecisions.TryParse(settings.UpdateCheckedAt, out var at)) return UpdateRelease.NotRequested();

        var raw = settings.UpdateNotesRaw ?? string.Empty;
        var notes = raw.Length > 0
            ? UpdateDecisions.PickNotes(raw, Language())
            : settings.UpdateNotes ?? string.Empty;

        return new UpdateRelease(
            true,
            string.Empty,
            latest,
            settings.UpdatePageUrl ?? string.Empty,
            settings.UpdatePublished ?? string.Empty,
            notes,
            raw,
            at);
    }

    /// <summary>
    /// ПОПЫТКА ИЗ НАСТРОЕК — тем же правилом, что следы: прогон без права состояния выпусков
    /// не знает о попытках вовсе, а неразобранная отметка значит «не помним».
    /// </summary>
    private UpdateAttempt SeededAttempt()
    {
        if (!Allowed) return UpdateAttempt.None;

        var settings = _settings();

        return UpdateAttempt.Parse(
            settings.UpdateAttemptAt,
            settings.UpdateAttemptKind,
            settings.UpdateAttemptError);
    }

    /// <summary>
    /// Запустить часы. В прогоне без права часов не будет вовсе — и это в журнал: «панель молчала»
    /// должно быть видно словами, а не догадкой.
    /// </summary>
    public void Start()
    {
        if (!Allowed)
        {
            _log(PanelStrings.PanelLogUpdateLocked);
            return;
        }

        _timer ??= UiThread.NewTimer(PricingDecisions.TickInterval);

        // Нитки интерфейса нет — часов нет: `new DispatcherTimer` на чужой нитке СОЗДАЛ бы
        // диспетчер и забрал нитку интерфейса у прогона проверок (см. `Shell\UiThread`).
        if (_timer is null)
        {
            _log(PanelStrings.PanelLogClockNoUiThread);
            return;
        }

        if (_timer.IsEnabled) return;

        _timer.Tick += (_, _) => Tick(_clock());
        _timer.Start();
        _log(PanelStrings.PanelLogUpdateClock);

        // И сразу первый такт: решение владельца — проверка ПРИ ЗАПУСКЕ панели, а не через час.
        // Второй раз за сутки она не сработает: такт спросит отметку в настройках.
        Tick(_clock());
    }

    /// <summary>
    /// Один такт часов: пора ли спрашивать выпуски заново. Сам запрос — в фоне: сеть на нитке
    /// интерфейса заморозила бы окно.
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        if (!Allowed) return;

        // Отметка из настроек И память этого прогона: настройки могут быть недоступны на запись
        // (тогда отметка не сохранится), и без памяти панель стучалась бы в сеть КАЖДЫЙ такт.
        // И ТРЕТЬЕ УСЛОВИЕ — ОТСТУП ПОСЛЕ НЕУДАЧИ: после отказа по числу запросов панель молчит
        // несколько часов, чтобы не добавлять своих запросов в выеденный лимит GitHub.
        var attempt = Attempt;

        if (!UpdateDecisions.ShouldCheck(now, _settings().UpdateCheckedAt, attempt, IsolatedRun))
        {
            // «Панель молчала» обязано объясняться словами — но ОДИН раз на период ожидания:
            // строка на каждый такт превратила бы журнал в шум.
            if (!_waitLogged && UpdateDecisions.Waiting(now, attempt))
            {
                _waitLogged = true;
                _log(string.Format(
                    CultureInfo.CurrentCulture,
                    PanelStrings.UpdateAttemptWaitingLogFormat,
                    PricingDecisions.CheckedText(
                        PricingDecisions.Stamp(UpdateDecisions.NextAttempt(attempt!)))));
            }

            return;
        }

        if (_lastCheck is not null && now - _lastCheck.Value < UpdateDecisions.CheckInterval) return;

        Check(now, humanRequest: false);
    }

    /// <summary>Кнопка «Проверить сейчас»: момент берётся у часов панели, гейт не спрашивается.</summary>
    public void Check() => Check(_clock(), humanRequest: true);

    /// <summary>
    /// Спросить выпуски. <paramref name="humanRequest"/> отличает щелчок человека от работы
    /// по расписанию — и это единственная разница между ними: щелчок идёт мимо суточного гейта,
    /// расписание гейт спрашивает.
    ///
    /// ⚠️ **«Свободен» становится видимым ПОСЛЕДНИМ** — тот же порядок, что у баланса и цен,
    /// и по той же причине: наблюдатель, ждущий <c>!Busy</c>, иначе успевал бы прочитать
    /// состояние, которого ещё нет. Флаг снимается в <c>finally</c> — он обязан сняться
    /// и при исключении, иначе одна упавшая попытка оставила бы панель «занятой» навсегда.
    /// </summary>
    private void Check(DateTimeOffset now, bool humanRequest)
    {
        if (!Allowed || Busy) return;

        Busy = true;
        Changed?.Invoke();

        var language = Language();
        var client = _client;

        Task.Run(() => client.Latest(language, now))
            .ContinueWith(task => _dispatch(() =>
            {
                try
                {
                    var answer = task.IsCompletedSuccessfully
                        ? task.Result
                        : UpdateRelease.Failed($"{task.Exception?.GetType().Name}", now);

                    // ПОПЫТКА ЗАПОМИНАЕТСЯ ВСЕГДА — и удачная, и нет: человек обязан видеть,
                    // что панель стучалась и чем это кончилось, а часы — знать про отступ.
                    var attempt = new UpdateAttempt(now, answer.Ok, answer.RateLimited, answer.Ok ? string.Empty : answer.Error);

                    Attempt = attempt;
                    _lastCheck = now;
                    _waitLogged = false;
                    _rememberAttempt(attempt);

                    _log(string.Format(
                        CultureInfo.CurrentCulture,
                        PanelStrings.UpdateLogFormat,
                        answer.Ok ? answer.Latest : answer.Error));

                    // ОТКАЗ НЕ УНОСИТ ПРЕЖНИЙ ОТВЕТ: `Result` остаётся последним УДАЧНЫМ.
                    // Иначе отказ сети стирал бы и версию, и заметки, и окно выглядело бы
                    // как «ещё не проверялось» после настоящей работы.
                    if (!answer.Ok) return;

                    Result = answer;
                    _checkedNotes = Result.Notes;

                    // Отметка времени — ВСЕГДА, и у автоматической проверки тоже: именно ею
                    // закрыто правило «не чаще раза в сутки». ⚠️ Пишется она ТОЛЬКО про удачную
                    // проверку: отметка после отказа соврала бы («проверено», хотя нет)
                    // и заперла бы проверку на сутки.
                    _rememberCheckedAt(PricingDecisions.Stamp(now));

                    // «Один раз на версию» считается ДО записи новой отметки: запись затрёт
                    // прежнюю увиденную версию, и сравнить с ней было бы уже нечем.
                    var announce = humanRequest == false
                        && UpdateDecisions.ShouldAnnounce(
                            Result.Latest,
                            _settings().UpdateSkippedVersion,
                            Current,
                            _settings().UpdateLatest);

                    _remember(new UpdateTraces(
                        Result.Latest, Result.Published, Result.PageUrl, Result.Notes, Result.NotesRaw));

                    if (announce) Announce?.Invoke(Result.Latest);
                }
                finally
                {
                    Busy = false;
                    Changed?.Invoke();
                }
            }));
    }

    /// <summary>Заметки к выпуску на языке панели — те, что взяты при последней проверке.</summary>
    public string Notes => _checkedNotes;

    /// <summary>Язык панели — тот, чей блок заметок будет выбран.</summary>
    public string Language() => _language() ?? LanguageDecisions.English;

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }
}
