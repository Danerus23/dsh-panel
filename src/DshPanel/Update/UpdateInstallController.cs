using System.Diagnostics;
using DshPanel.Isolation;
using DshPanel.Shell;

namespace DshPanel.Update;

/// <summary>
/// ЧТО ОКНО ПРОСИТ У ДВИЖКА УСТАНОВКИ. Отдельный уговор по той же причине, что
/// <see cref="IUpdateClient"/> и <see cref="IUpdateDownload"/>: **проверки не ходят в сеть
/// и не готовят замену файлов**, а подставляют свой ответ. Интерфейс для того и отделён,
/// что за <see cref="Prepare"/> стоит скачивание в сотни мегабайт и запись в каталог состояния,
/// а за <see cref="Launch"/> — самая рискованная дверь продукта: она подменяет саму панель.
/// </summary>
public interface IUpdateInstall
{
    /// <summary>
    /// Право ЭТОГО прогона готовить замену файлов (<see cref="UpdateInstall.For"/>). Считается
    /// в одном месте — у того, кто строит движок, — и приходит сюда готовым ответом: окно
    /// по нему только ПОКАЗЫВАЕТ, можно ли нажимать, а решает движок.
    /// </summary>
    bool Allowed { get; }

    /// <summary>
    /// Скачать вложения выпуска, сверить суммы, распаковать, сверить версию, снять страховочную
    /// копию и написать сценарий замены. Файлы панели НЕ заменяются — это делает сценарий
    /// после её выхода, и запускает его человек (<see cref="Launch"/>).
    /// </summary>
    /// <param name="progress">Ход по этапам. Может быть <c>null</c> — ход тогда никому не нужен.</param>
    UpdatePreparation Prepare(string release, ReleaseAssets assets, Action<UpdateProgress>? progress = null);

    /// <summary>
    /// Запустить подготовленный сценарий замены. Пустая строка — запущен; иначе причина словами
    /// (её показывает окно). **Ни один прогон проверки этот путь не зовёт.**
    /// </summary>
    string Launch(UpdatePreparation preparation);
}

/// <summary>
/// ДВИЖОК УСТАНОВКИ, КАКИМ ЕГО ВИДИТ ОКНО: собирает запрос к <see cref="UpdateInstall"/>
/// из того, что знает панель, и запускает сценарий замены.
///
/// **Здесь нет ни одного решения.** «Есть ли выпуск новее», «не пропустил ли его человек»,
/// «пора ли проверять» — это <see cref="UpdateController"/> и <see cref="UpdateDecisions"/>;
/// сюда приходит готовый ответ, а уходит готовый запрос. Единственное, что решается тут, —
/// момент времени и то, что запуск сценария вообще возможен в этом прогоне.
///
/// ⚠️ **Право готовить замену приходит готовым** (<paramref name="allowed"/>) и берётся
/// у <see cref="UpdateInstall.For"/> тем, кто строит движок (<c>App.StartPanel</c>). Движок
/// не вычисляет его сам: у права один источник, и второго быть не должно.
/// </summary>
public sealed class UpdateInstallController : IUpdateInstall
{
    private readonly AppPaths _paths;
    private readonly Func<string> _current;
    private readonly string _target;
    private readonly string _startArgument;
    private readonly string _mutexName;
    private readonly Action<string> _log;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="target">
    /// Папка САМОЙ панели — та, которую заменит сценарий. Приходит путём, а не ищется здесь:
    /// у панели один источник правды о своём месте (<c>Environment.ProcessPath</c>), и второй
    /// поиск однажды нашёл бы не ту папку.
    /// </param>
    /// <param name="startArgument">
    /// С каким ключом поднять панель после замены (пусто — обычный запуск). Режим решает тот,
    /// кто строит движок: панель, работавшая значком, не должна после обновления открыть окно.
    /// </param>
    /// <param name="mutexName">Имя замка одной копии, которого сценарий обязан дождаться.</param>
    public UpdateInstallController(
        AppPaths paths,
        Func<string> currentVersion,
        string target,
        bool allowed,
        string startArgument,
        string mutexName,
        Action<string> log,
        Func<DateTimeOffset>? clock = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _current = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        _target = target ?? string.Empty;
        _startArgument = startArgument ?? string.Empty;
        _mutexName = mutexName ?? string.Empty;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? (() => DateTimeOffset.Now);

        Allowed = allowed;
    }

    public bool Allowed { get; }

    /// <summary>
    /// Подготовить замену. Момент берётся у часов панели и уходит в запрос — иначе шапка журнала
    /// замены врала бы, а проверка зависела бы от системного времени.
    ///
    /// Две строки в журнал пишутся ЗДЕСЬ, а не в окне: «подготовка началась» и «подготовлено» —
    /// это факты о работе панели, и они обязаны остаться в журнале даже тогда, когда человека
    /// у окна нет (он закрыл его, пока идёт скачивание).
    /// </summary>
    public UpdatePreparation Prepare(string release, ReleaseAssets assets, Action<UpdateProgress>? progress = null)
    {
        _log(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            PanelStrings.UpdatePrepareStartedLogFormat,
            release));

        var preparation = UpdateInstall.Prepare(
            new UpdateRequest(
                _paths,
                _current(),
                _target,
                release,
                assets,
                Allowed,
                IgnoreNewer: false,
                StartArgument: _startArgument,
                MutexName: _mutexName,
                At: _clock()),
            download: null,
            versionOf: null,
            progress: progress);

        _log(preparation.Ok
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.UpdatePreparedDoneLogFormat,
                preparation.Version,
                DisplayMask.Path(preparation.StagedFolder))
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.UpdateLogFormat,
                UpdateRefusalLines.Line(preparation)));

        return preparation;
    }

    /// <summary>
    /// ЗАПУСТИТЬ СЦЕНАРИЙ ЗАМЕНЫ. Это последнее нажатие, и оно делается ТОЛЬКО по щелчку
    /// человека: ни автоматики, ни «предложения за него» здесь нет.
    ///
    /// Сценарий запускается через <c>cmd /c</c>, а не напрямую: <c>.cmd</c> — это файл команд,
    /// и без cmd его не выполнить. Тело командной строки приходит от ядра уже собранным
    /// (<see cref="UpdatePreparation.CommandLine"/>) — с кавычками в том виде, в каком его
    /// понимает <c>cmd /c</c>; здесь оно не переклеивается, иначе кавычки удвоились бы.
    ///
    /// ⚠️ Окно консоли НЕ показывается (<c>CreateNoWindow</c>): человек не должен видеть чёрный
    /// прямоугольник поверх своей работы. Сценарий пишет свой ход в журнал замены — там его
    /// и читают.
    /// </summary>
    public string Launch(UpdatePreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);

        if (!preparation.Ok) return PanelStrings.UpdateInstallUnavailable;
        if (!Allowed) return PanelStrings.UpdateRefusedLocked;
        if (preparation.CommandLine.Length == 0) return PanelStrings.UpdateLaunchNotStarted;

        try
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = "/c " + preparation.CommandLine,
            };

            var process = Process.Start(start);

            if (process is null)
            {
                _log(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    PanelStrings.UpdateLaunchFailedLogFormat,
                    preparation.Version,
                    PanelStrings.UpdateLaunchNotStarted));

                return PanelStrings.UpdateLaunchNotStarted;
            }

            _log(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.UpdateLaunchLogFormat,
                preparation.Version));

            return string.Empty;
        }
        catch (Exception error)
        {
            // Причина уходит и в журнал, и человеку: он нажал последнюю кнопку и обязан узнать,
            // что ничего не произошло, — молчание здесь читалось бы как «панель обновилась».
            _log(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                PanelStrings.UpdateLaunchFailedLogFormat,
                preparation.Version,
                error.Message));

            return error.Message;
        }
    }
}
