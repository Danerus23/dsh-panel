using DshPanel.Isolation;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// ЖИВАЯ ПРОБА УМЕНИЙ ДЛЯ ОТЧЁТА ОБ ОКРУЖЕНИИ — «проверено ЗДЕСЬ И СЕЙЧАС», а не запись файла
/// подсказок о версии вообще (задание владельца 14.10.2026, §2).
///
/// **От какого случая.** На одной карточке рядом стояли два РАЗНЫХ ответа: вердикт файла подсказок
/// («панель проверена на этой версии целиком: все 8 умений подтверждены живым прогоном в изоляции»)
/// и сводка таблицы умений — ответ про ЭТОТ сервер. Без названного момента пробы человек читал
/// проверенную версию как полурабочую.
///
/// **Почему в фоне.** Настоящая проба (<see cref="EngineRpcProbe.Measure"/>) зовёт движок по каждому
/// по нужным именам (77) и ходит в сеть — в перерисовке окна это недопустимо: окно замерло бы на секунды.
/// Поэтому проба идёт в фоне, а отчёт читает ПОСЛЕДНИЕ ГОТОВЫЕ факты и честно называет их момент.
///
/// ⚠️ **Одна проба на один свой сервер.** Заказ запоминается по паре «порт + номер процесса»:
/// перерисовка окна зовёт заказ каждую секунду, и без этой памяти панель запускала бы по пробе
/// в секунду. Сменился сервер (перезапуск, другая версия) — пара другая, и проба идёт заново:
/// ровно этого и требует задание («пересчитать после того, как сервер стал своим»).
///
/// ⚠️ Проба НИЧЕГО не предполагает про порт владельца: порт и ссылку входа ей дают снаружи.
/// </summary>
public sealed class LiveSkillProbe
{
    private readonly AppPaths _paths;
    private readonly Func<SkillTarget?> _target;
    private readonly Func<string?> _binPath;
    private readonly Action<string> _log;

    private readonly object _gate = new();

    private readonly SkillProbeDoor _door;

    private SkillFacts _facts = new();
    private string _asked = string.Empty;
    private bool _running;

    public LiveSkillProbe(AppPaths paths, Func<SkillTarget?> target, Func<string?> binPath, Action<string> log)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _binPath = binPath ?? throw new ArgumentNullException(nameof(binPath));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        // ДВЕРЬ ОДНА НА ВСЮ ЖИЗНЬ ПРОБЫ, а не новая на каждый вопрос: на неё подписывается окно
        // (через владельца настроек), и подписка на свежий объект каждый раз терялась бы молча.
        _door = new SkillProbeDoor(() => Facts, Ask);
    }

    /// <summary>ДВЕРЬ ДЛЯ ОТЧЁТА: факты последней УДАЧНОЙ пробы, заказ новой и сообщение «факты пришли».</summary>
    public SkillProbeDoor Door => _door;
    /// <summary>Последние готовые факты. Пробы ещё не было — пустые (отчёт скажет «не спрашивала»).</summary>
    public SkillFacts Facts
    {
        get
        {
            lock (_gate) return _facts;
        }
    }

    /// <summary>
    /// ЗАКАЗАТЬ ПРОБУ — если её для ЭТОГО сервера ещё не было и она не идёт прямо сейчас.
    /// Возвращается сразу: проба идёт в фоне, отчёт пока показывает прежние факты.
    /// </summary>
    public void Ask()
    {
        var target = _target();

        // Сервер не наш (нет ссылки входа или порта) — спрашивать нечего, и это НЕ «проба не вышла»:
        // так и говорится словами в сводке («панель сейчас не спрашивала»).
        if (target is null || string.IsNullOrWhiteSpace(target.BaseUrl)) return;

        var key = target.Port + "/" + target.Cookie.Length + "/" + target.EntryLink.Length;

        lock (_gate)
        {
            if (_running || string.Equals(_asked, key, StringComparison.Ordinal)) return;

            _running = true;
            _asked = key;
        }

        var bin = _binPath();

        Task.Run(() =>
        {
            try
            {
                var result = EngineRpcProbe.Measure(bin, target.EntryLink, target.Port, _paths);

                if (result.Facts is not { } facts)
                {
                    // Проба не дошла до умений (отказ входа и т.п.). Прежние факты не подменяем
                    // выдумкой и НЕ подставляем вердикты файла подсказок: пусть живёт честное
                    // «не проверялась», а причина — строкой в журнал.
                    _log(PanelStrings.EnvSkillsProbeNoFactsLog);
                    return;
                }

                lock (_gate) _facts = facts with { MeasuredAt = DateTimeOffset.Now };

                // ФАКТЫ ПРИШЛИ — СООБЩИТЬ (задание владельца 14.10.2026, §3): раздел «Сервер»
                // обязан показать правду САМ, без нажатия кнопки. Владелец открыл раздел, увидел
                // «кучу красных» и решил, что панель сломана, — а правду показывала только кнопка.
                Notify();
            }
            catch (Exception ex)
            {
                // Проба — не работа человека: её отказ говорится строкой в журнал и НЕ роняет окно.
                _log(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    PanelStrings.EnvSkillsProbeFailedLogFormat, ex.GetType().Name));
            }
            finally
            {
                lock (_gate) _running = false;
            }
        });
    }

    /// <summary>
    /// СООБЩИТЬ, ЧТО ФАКТЫ ПРИШЛИ, — на НИТКУ ИНТЕРФЕЙСА, тем же способом, что у остальных
    /// контроллеров панели (<c>Shell\UiThread.Post</c>). Проба идёт в фоне, окно живёт на своей
    /// нитке: позвать подписчиков прямо здесь значило бы рисовать окно из чужой нитки.
    ///
    /// ⚠️ **Нитки интерфейса нет** (прогон проверки, съёмка кадра) — сообщать некому, и это
    /// не потеря: окна, которое надо перерисовать, в том прогоне тоже нет. Молчания здесь
    /// не бывает «иногда»: <c>UiThread.Post</c> честно отвечает, отправил он работу или нет.
    /// </summary>
    private void Notify() => UiThread.Post(_door.Signal);
}
