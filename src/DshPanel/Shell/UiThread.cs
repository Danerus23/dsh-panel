using System;
using System.Threading;
using Avalonia.Threading;

namespace DshPanel.Shell;

/// <summary>
/// НИТКА ИНТЕРФЕЙСА ПАНЕЛИ — единственная дверь, через которую панель берёт диспетчер.
///
/// Зачем дверь. У <c>Dispatcher.UIThread</c> в Avalonia есть свойство, о котором легко не знать:
/// **диспетчер СОЗДАЁТСЯ тем, кто спросил первым**, если его в процессе ещё нет
/// (<c>UIThread =&gt; s_uiThread ?? CurrentDispatcher</c>, а конструктор диспетчера делает
/// <c>s_uiThread ??= this</c> — «первый созданный диспетчер и есть нитка интерфейса»).
/// Значит фоновая нитка, первой спросившая <c>Dispatcher.UIThread</c> (или создавшая
/// <c>new DispatcherTimer</c>: он берёт тот же диспетчер), **объявляет ниткой интерфейса СЕБЯ**.
///
/// Чем это плохо в жизни и в прогоне:
/// * в жизни — почти ничем: приложение уже подняло диспетчер на своей нитке, и фоновый вопрос
///   возвращает готовый. Панель поэтому и жила с прямыми обращениями;
/// * в прогоне проверок — падением чужой проверки. Оснастка проверок вида сбрасывает состояние
///   диспетчера между проверками, и в этот промежуток <c>s_uiThread</c> пуст. Фоновая нитка
///   панели (ответ фоновой задачи, таймер контроллера), спросившая диспетчер ровно тогда,
///   забирает его себе — и следующая проверка вида падает на инициализации оснастки:
///   `AvaloniaHeadlessPlatform.Initialize` → `new Compositor` → `DefaultRenderLoop.Add` →
///   `Dispatcher.VerifyAccess` → «The calling thread cannot access this object because a
///   different thread owns it».
///
/// **Это не догадка, а замер.** 09.10.2026 флейк пойман на этой машине (1 падение на 5 полных
/// прогонов; в CI — прогон `37972801441`, попытка 1), и двумя пробами доказано само свойство:
/// после `new DispatcherTimer { … }` и после `Dispatcher.UIThread.Post(…)` с нитки без
/// диспетчера <c>Dispatcher.FromThread(текущая нитка)</c> перестаёт быть пустым — нитка стала
/// ниткой интерфейса. Стек падения в CI назван в `docs\DESIGN.md` п. 46.
///
/// Поэтому дверь:
/// * **никогда не создаёт диспетчер** — спрашивает только тот, что уже есть у текущей нитки
///   (<see cref="Dispatcher.FromThread"/>), и помнит тот, что панель узнала на своей нитке
///   интерфейса (<see cref="Remember"/>);
/// * нитки интерфейса нет — работа **не отправляется**: адресата в этом прогоне не существует,
///   и «подождать в пустоте» тут нечего. Отказ возвращается вызывающему (<c>false</c> / <c>null</c>),
///   чтобы он сказал об этом словами, а не молчал;
/// * таймер заводится только у СВОЕЙ нитки (<see cref="NewTimer"/>): `new DispatcherTimer { … }`
///   без диспетчера — это и есть захват.
///
/// ⚠️ **Правило проекта:** в панели нет ни одного другого обращения к <c>Dispatcher.UIThread</c>
/// и ни одного <c>new DispatcherTimer</c>. Сторожит это проверка
/// `UiThreadTests.К_нитке_интерфейса_обращается_только_дверь` — она читает исходники, а не верит
/// слову. Нужное двери добавь сюда, а не на месте.
/// </summary>
internal static class UiThread
{
    /// <summary>
    /// Диспетчер, который панель узнала НА СВОЕЙ нитке интерфейса. Пусто, пока панель его
    /// не видела: тогда работает оглядка на текущую нитку (<see cref="Known"/>), а не создание.
    /// </summary>
    private static Dispatcher? _known;

    /// <summary>
    /// Запомнить нитку интерфейса — зовётся С НЕЁ: при запуске панели и при постройке окна.
    ///
    /// Помним **всегда заново**: у прогона проверок каждый опыт вида живёт в своём приложении,
    /// и запомненный диспетчер прошлого опыта к следующему уже не годится.
    /// </summary>
    public static void Remember()
    {
        var own = Dispatcher.FromThread(Thread.CurrentThread);
        if (own is not null) _known = own;
    }

    /// <summary>
    /// Мы на нитке интерфейса? **Диспетчер здесь не создаётся**: нитки интерфейса нет — ответ
    /// «нет», а не «сейчас появится».
    /// </summary>
    public static bool CheckAccess() => Known()?.CheckAccess() == true;

    /// <summary>
    /// Отправить работу в нитку интерфейса. Возвращает <c>false</c>, когда нитки интерфейса нет
    /// вовсе: работа тогда не отправляется и не пропадает молча — о ней скажет вызывающий.
    /// </summary>
    public static bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Known();
        if (dispatcher is null) return false;

        dispatcher.Post(action);
        return true;
    }

    /// <summary>
    /// ВЗЯТЬ НИТКУ ИНТЕРФЕЙСА НА ПАМЯТЬ ОКНУ (Н-1, задача D3 цикла 2.1.2).
    ///
    /// **От какого случая.** Память двери ОДНА на процесс, а окно живёт дольше одного ответа из фона:
    /// между постройкой окна и ответом фоновой задачи память может быть **забыта** (так делает
    /// проверка «нитки нет вовсе» — <see cref="ForgetForTests"/>, и в полном прогоне она идёт
    /// параллельно с проверками окон). Тогда <see cref="Post(Action)"/> честно отвечает `false`,
    /// а вызывающий, не спросивший ответа, терял работу — окно оставалось занятым навсегда.
    /// Замер: 1 падение из 5 полных прогонов, «окно не вернулось в рабочее состояние» через **1 min 2 s**.
    ///
    /// Поэтому окно, построенное на нитке интерфейса, берёт её ЗДЕСЬ и дальше отвечает через
    /// <see cref="PostTo"/>: свою нитку окно знает, и чужая забывчивость его больше не обезоруживает.
    /// Дверь остаётся единственным местом, которое знает про диспетчер, — правило проекта не нарушено.
    /// </summary>
    public static Dispatcher? Capture() => Known();

    /// <summary>
    /// Донести работу до ЗАХВАЧЕННОЙ нитки интерфейса (<see cref="Capture"/>) — не разыскивая её
    /// заново. `false` — нитки не было и при захвате: работа не отправлена, о ней должен сказать
    /// вызывающий (та же честность, что у <see cref="Post(Action)"/>).
    /// </summary>
    public static bool PostTo(Dispatcher? dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher is null) return false;

        dispatcher.Post(action);
        return true;
    }

    /// <summary>
    /// То же, но с ОЧЕРЕДЬЮ наперёд (t13): отложенная прокрутка плана в окне копий ждёт конца
    /// прохода вёрстки и просит `DispatcherPriority.Loaded` — «после того, как разметка измерена».
    /// </summary>
    public static bool PostTo(Dispatcher? dispatcher, Action action, DispatcherPriority priority)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher is null) return false;

        dispatcher.Post(action, priority);
        return true;
    }

    /// <summary>
    /// НИТКА ИНТЕРФЕЙСА ОКНУ ИЛИ ГРОМКИЙ ОТКАЗ (t13, требование владельца — на все окна).
    ///
    /// Пустое значение НЕ прячется в поле окна: окно без диспетчера своей нитки не умеет возвращать
    /// ответы из фона — занятость осталась бы навсегда, и человек увидел бы мёртвые кнопки без
    /// единого слова. «Иногда так бывает» здесь нет: окно живёт на нитке интерфейса по определению,
    /// и отказ дешевле полудохлого окна.
    ///
    /// ⚠️ **Почему отказ отдельным членом, а не строкой в конструкторе.** Окно на нитке БЕЗ
    /// диспетчера построить нельзя вовсе: базовый конструктор `Window` требует платформу и падает
    /// первым («Unable to locate 'Avalonia.Platform.IWindowingPlatform'», замер t11/t13). Поэтому
    /// решение проверяется напрямую — оно же и есть то, что зовёт конструктор; каждое окно зовёт
    /// его со СВОИМ именем, чтобы в отказе было видно, чей конструктор не смог.
    /// </summary>
    public static Dispatcher RequireWindow(Dispatcher? captured, string window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(window);

        return captured ?? throw new InvalidOperationException(
            window + " БЕЗ НИТКИ ИНТЕРФЕЙСА: у нитки, на которой строится окно, нет диспетчера. " +
            "Ответы из фона доставлять некуда — окно осталось бы занятым навсегда. " +
            "Стройте окно на нитке интерфейса (Shell\\UiThread).");
    }

    /// <summary>
    /// Отправить работу в нитку интерфейса с ОЧЕРЕДЬЮ наперёд (<see cref="DispatcherPriority"/>).
    /// Возвращает <c>false</c> ровно по той же причине, что <see cref="Post(Action)"/>.
    /// </summary>
    public static bool Post(Action action, DispatcherPriority priority)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Known();
        if (dispatcher is null) return false;

        dispatcher.Post(action, priority);
        return true;
    }

    /// <summary>
    /// Часы панели, привязанные к СВОЕЙ нитке интерфейса, или <c>null</c>, когда её нет.
    ///
    /// ⚠️ Именно здесь, а не на месте: `new DispatcherTimer { … }` спрашивает
    /// <c>Dispatcher.UIThread</c> и тем самым создаёт диспетчер на нитке, которая спросила, —
    /// то есть захватывает нитку интерфейса у прогона проверок (замер 09.10.2026).
    /// </summary>
    public static DispatcherTimer? NewTimer(TimeSpan interval)
    {
        var dispatcher = Known();
        if (dispatcher is null) return null;

        return new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = interval };
    }

    /// <summary>
    /// Прогнать очередь СВОЕЙ нитки до конца. У нитки без диспетчера прогонять нечего — и это
    /// не потеря: очередь пуста по определению.
    /// </summary>
    public static void RunJobs() => Dispatcher.FromThread(Thread.CurrentThread)?.RunJobs();

    /// <summary>
    /// Забыть запомненную нитку — ШОВ ТОЛЬКО ДЛЯ ПРОВЕРОК. Дверь держит память на процесс,
    /// а проверить её поведение «нитки интерфейса нет вовсе» иначе нечем: у прогона проверок
    /// к моменту вызова память уже может быть занята прошлым опытом вида.
    /// </summary>
    internal static void ForgetForTests() => _known = null;

    /// <summary>
    /// Диспетчер панели: запомненный, а если его ещё нет — тот, что уже принадлежит ЭТОЙ нитке.
    ///
    /// ⚠️ `Dispatcher.UIThread` здесь не спрашивается намеренно: он СОЗДАЁТ диспетчер на нитке,
    /// которая спросила первой. Дверь существует ровно затем, чтобы этого не делал никто.
    /// </summary>
    private static Dispatcher? Known()
    {
        if (_known is not null) return _known;

        var own = Dispatcher.FromThread(Thread.CurrentThread);
        if (own is not null) _known = own;

        return _known;
    }
}
