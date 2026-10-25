using DshPanel.Shell;

namespace DshPanel.Update;

/// <summary>
/// СОСТОЯНИЕ ПОДГОТОВКИ ОБНОВЛЕНИЯ — ОДИН ОБЪЕКТ НА ПРОЦЕСС (DEF-058 и DEF-059, 2.4.2).
///
/// **От какого случая.** До 2.4.2 состояние жило ПОЛЯМИ ОКНА настроек
/// (<c>_updateBusy</c>, <c>_updatePrepared</c>, <c>_updateStatus</c>), а <see
/// cref="Shell.SingleWindowSlot"/> на закрытии окна обнуляет ссылку на него. Владелец
/// 24.10.2026 на 2.4.1: шарик → закрыл окно → открыл, — и новое окно построило пустое
/// состояние: «ничего не идёт», кнопка «Скачать и подготовить» снова доступна. Фоновая
/// задача при этом продолжала работать и писать в те же файлы <c>state\update\</c>.
/// Второе нажатие запускало ВТОРУЮ подготовку в одни пути: вторая удаляла куски первой,
/// и человек читал «FileNotFoundException: Could not find file …part-0».
///
/// **Почему объект, а не поля окна.** У этого состояния время жизни — панель, а не окно:
/// подготовку человек запускает из окна, но она продолжается, когда окна уже нет (и шарик
/// о ней говорит — <c>App._updateInstall.Preparing</c>). Значит и дом у него на уровне панели:
/// создаётся в <c>App</c> рядом с движком установки, окно получает его ДОВОДОМ
/// (<c>SettingsWindow.Attach</c>) и только читает.
///
/// **Единственная дверь старта — <see cref="TryBegin"/>** (сравнение-обмен под замком, а не
/// флаг окна): второй вызов возвращает <c>false</c> и называет причину СЛОВАМИ
/// (<see cref="PanelStrings.UpdatePreparationBusy"/>). Исключений и путей здесь нет:
/// человек читает окно, а не журнал.
///
/// ⚠️ <see cref="Changed"/> может прийти с ФОНОВОЙ нитки (ход загрузки сообщает загрузчик):
/// подписчик обязан перевести его в свою нитку интерфейса сам — окно делает это своей дверью
/// <c>OnUi</c>.
/// </summary>
public sealed class PanelUpdateState
{
    /// <summary>Замок один на все поля: их читают и меняют из двух ниток, а порознь они разошлись бы.</summary>
    private readonly object _gate = new();

    private bool _busy;
    private UpdatePreparation? _prepared;
    private string _status = string.Empty;
    private UpdateProgressView? _progress;

    /// <summary>Часы попытки: объём, средняя скорость и остаток считаются здесь (DEF-060, 2.4.2).</summary>
    private readonly UpdateProgressMeter _meter = new();

    /// <summary>
    /// СОСТОЯНИЕ СМЕНИЛОСЬ — и это единственное сообщение окнам. Ход подготовки поднимает его
    /// на каждый прочитанный буфер куска, поэтому подписчик обязан обновлять ход точечно, а не
    /// перерисовывать весь раздел (окно так и делает — <c>SettingsWindow.RenderUpdateState</c>).
    /// </summary>
    public event Action? Changed;

    /// <summary>Идёт подготовка: второй старт не начинается.</summary>
    public bool Busy
    {
        get { lock (_gate) return _busy; }
    }

    /// <summary>Подготовленная к замене сборка; <c>null</c> — не готовили или попытка не удалась.</summary>
    public UpdatePreparation? Prepared
    {
        get { lock (_gate) return _prepared; }
    }

    /// <summary>Строка состояния раздела: ответ об отказе, итог пропуска, «причина не названа».</summary>
    public string Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>
    /// СНИМОК ХОДА подготовки: этап, имя файла, сколько принято из всего, средняя скорость
    /// и остаток (DEF-060). Хранится здесь, а не в органе окна: закрыл окно и открыл снова —
    /// числа на месте, а не с нуля.
    /// </summary>
    public UpdateProgressView? Progress
    {
        get { lock (_gate) return _progress; }
    }

    /// <summary>
    /// ЕДИНСТВЕННАЯ ДВЕРЬ СТАРТА ПОДГОТОВКИ. <c>true</c> — эта попытка началась; <c>false</c> —
    /// подготовка уже идёт, и второй раз она не начинается (DEF-058/059).
    ///
    /// Отказ НАЗЫВАЕТСЯ СЛОВАМИ в <see cref="Status"/>: причина уже видна человеку в любом окне,
    /// которое на это состояние подписано, и это не исключение и не путь. На экране при этом
    /// остаётся ОДНА фраза об одном: пока работа идёт, кнопка подписана «Подготовка идёт…» и её
    /// подсказка говорит «нажать нельзя» (<see cref="PanelStrings.TipUpdatePrepareBusy"/>), а
    /// полная причина встаёт только по факту отказа — двух фраз рядом не бывает.
    /// </summary>
    public bool TryBegin()
    {
        bool begun;

        lock (_gate)
        {
            begun = UpdatePrepareDecisions.MayStart(_busy);

            if (begun)
            {
                // Новая попытка начинается с ПУСТОГО места: иначе после отказа на экране до первого
                // сообщения движка висели бы строка и готовность прошлой попытки.
                _busy = true;
                _prepared = null;
                _status = string.Empty;
                _progress = null;
                _meter.Reset();
            }
            else
            {
                _status = PanelStrings.UpdatePreparationBusy;
            }
        }

        Changed?.Invoke();
        return begun;
    }

    /// <summary>
    /// СООБЩИТЬ ХОД ПОДГОТОВКИ (зовёт загрузчик, в том числе с фоновой нитки). Здесь же он
    /// превращается в снимок ДЛЯ ПОКАЗА: объём, средняя скорость за этап и остаток считаются
    /// монотонными часами (<see cref="UpdateProgressMeter"/>), а не берутся из ответа загрузчика —
    /// загрузчик знает только «принято из всего» (DEF-060).
    /// </summary>
    public void Report(UpdateProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        lock (_gate) _progress = _meter.Sample(progress);

        Changed?.Invoke();
    }

    /// <summary>
    /// ЗАКОНЧИТЬ ПОПЫТКУ — любым исходом, и это ОДНА дверь на успех и отказ: разойдясь, они
    /// однажды оставили бы в одном исходе занятость, а в другом — застывший ход.
    ///
    /// <paramref name="preparation"/> попадает в <see cref="Prepared"/> ТОЛЬКО удачным: отказ,
    /// положенный туда, оставил бы открытой дверь замены файлов.
    /// </summary>
    public void Finish(UpdatePreparation? preparation, string status)
    {
        lock (_gate)
        {
            _busy = false;
            _prepared = preparation is { Ok: true } ? preparation : null;
            _status = status ?? string.Empty;
            _progress = null;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// СКАЗАТЬ СТРОКУ СОСТОЯНИЯ, НЕ ТРОГАЯ ПОДГОТОВКУ. Нужно ответам, которые к подготовке не
    /// относятся: «пропустить эту версию» и отказ открыть страницу выпуска. Отдельной дверью,
    /// а не <see cref="Finish"/>: тот снял бы занятость и забыл бы готовую сборку, которых эти
    /// ответы не касаются.
    /// </summary>
    public void ReportStatus(string status)
    {
        lock (_gate) _status = status ?? string.Empty;

        Changed?.Invoke();
    }
}
