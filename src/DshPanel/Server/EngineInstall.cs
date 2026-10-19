namespace DshPanel.Server;

/// <summary>
/// ПОЧЕМУ СТАВИТЬ НЕЧЕГО. Каждый случай — свой, и каждый называется человеку словами
/// (ключи — <see cref="EngineInstallRefusals"/>): «одна общая ошибка установки» не сказала бы,
/// что именно поправить.
/// </summary>
public enum EngineInstallRefusal
{
    /// <summary>Ничего не отказывает: версия выбрана.</summary>
    None,

    /// <summary>Замка нет (файл не прочитался, чужая схема, сумма не сошлась) — ставить не по чему.</summary>
    NoLock,

    /// <summary>Замка НЕТ ДЛЯ ЭТОЙ ВЕРСИИ: набор известен, а этого набора в сборке нет.</summary>
    NoLockForVersion,

    /// <summary>Подсказок о версиях нет — а версию панель берёт ИМЕННО оттуда.</summary>
    NoHints,

    /// <summary>Ни одна версия в подсказках не отмечена проверенной: ставить «на удачу» панель не станет.</summary>
    NotVerified,

    /// <summary>Проверенная версия из подсказок и версия замка — РАЗНЫЕ: две правды об одном дереве.</summary>
    Mismatch,

    /// <summary>В замке есть пакет не с официального реестра — набор подменён.</summary>
    ForeignSource,

    /// <summary>Замок обрезан: записей меньше, чем объявлено в файле.</summary>
    Incomplete,
}

/// <summary>
/// ВЫБОР ВЕРСИИ ДЛЯ УСТАНОВКИ — ЧИСТОЕ РЕШЕНИЕ (этап E3.1).
///
/// **Откуда берётся версия.** Решение владельца 10.10.2026, дословно: *«версию для установки бери
/// из файла подсказок (сейчас там проверена 0.1.5-rc.2; 0.2.0-rc.2 ставить вместо неё нельзя)»*.
/// Значит версия приходит **из подсказок** — и только оттуда: номер в коде, настройке или «самая
/// свежая из реестра» версией установки не является.
///
/// **Почему сверяется с замком.** Замок — это то, что панель поставит **фактически**
/// (<see cref="EngineTreeLock"/>). Если подсказки говорят «проверена 0.2.0-rc.2», а замок снят
/// с `0.1.5-rc.2`, то две части одной панели знают разное, и ставить нельзя **ни то, ни другое**:
/// выбор «поверим замку» или «поверим подсказкам» был бы догадкой там, где нужен замер.
///
/// **Чего здесь нет.** Ни сети, ни файлов, ни процессов: доводы приходят готовыми, поэтому решение
/// перебирается проверками целиком — как решения о порте и версии в <see cref="ServerDecisions"/>.
/// </summary>
public static class EngineInstallDecisions
{
    /// <summary>Что панель собирается поставить: версия и путь к её <c>bin.js</c>.</summary>
    public sealed record Plan(string Version, string BinPath, EngineInstallRefusal Refusal)
    {
        /// <summary>Можно ли ставить: версия выбрана и отказов нет.</summary>
        public bool Any => Refusal == EngineInstallRefusal.None && Version.Length > 0;

        /// <summary>Почему нельзя — ключом словаря; пусто, когда ставить можно.</summary>
        public string RefusalKey => EngineInstallRefusals.Key(Refusal);
    }

    /// <summary>
    /// ВЫБРАТЬ ВЕРСИЮ ДЛЯ УСТАНОВКИ: из подсказок, сверенную с замком.
    ///
    /// Порядок проверок — от «ставить не по чему» к «две правды»: сначала есть ли вообще замок
    /// и подсказки, потом есть ли проверенная версия, и только потом сходится ли она с замком.
    /// </summary>
    public static Plan Decide(EngineTreeLock? engineLock, EngineHints? hints, string? engineDir)
    {
        var known = engineLock ?? EngineTreeLock.Empty;
        var told = hints ?? EngineHints.Empty;

        if (!known.Any) return Refuse(EngineInstallRefusal.NoLock);
        if (!known.Complete) return Refuse(EngineInstallRefusal.Incomplete);

        // Замок без сумм — это список версий, а не замок: `npm ci` по такому поставил бы вложения,
        // не сверив ни одного (замер 10.10.2026: суммы есть у всех 588 записей, пропажа хотя бы
        // одной — уже другой файл).
        if (!known.FullySummed) return Refuse(EngineInstallRefusal.Incomplete);
        if (!known.AllFromRegistry) return Refuse(EngineInstallRefusal.ForeignSource);

        if (!told.Any) return Refuse(EngineInstallRefusal.NoHints);

        var verified = Verified(told);
        if (verified.Count == 0) return Refuse(EngineInstallRefusal.NotVerified);

        // Проверенная версия сверяется с замком ПРАВИЛОМ ПАНЕЛИ (числовая часть): у движка бывают
        // хвосты `-rc.2`, и сверка строкой не нашла бы свою же версию.
        var wanted = Update.UpdateDecisions.Numeric(known.EngineVersion);

        var match = verified.FirstOrDefault(version =>
            string.Equals(Update.UpdateDecisions.Numeric(version), wanted, StringComparison.OrdinalIgnoreCase));

        if (match is null) return Refuse(EngineInstallRefusal.Mismatch);

        // Версия выбрана — и сразу назван путь, куда её ставить: <папка движка>\<версия>\...
        // Имя папки — ВЕРСИЯ, поэтому две версии не смешиваются, а откат (E3.3) — это подъём
        // сервера из соседней папки.
        return new Plan(match, OwnEngine.BinPath(engineDir, match), EngineInstallRefusal.None);
    }

    /// <summary>
    /// КАКОЙ ДОРОГОЙ СТАВИТЬ НАЗВАННУЮ ВЕРСИЮ: <c>null</c> — главной (установщик сверит версию
    /// ГЛАВНОГО замка с подсказками: «две правды об одном дереве»), иначе — дорогой отдельной
    /// просьбы (у версии есть свой замок, и проверяется он, а не отметка в подсказках).
    ///
    /// ⚠️ **ПРАВИЛО ИЗМЕНЕНО 19.10.2026 (цикл G, выпуск 2.3.0): развилка идёт по ГЛАВНОМУ ЗАМКУ,
    /// а не по отметке «проверена».** Прежде главной дорогой шла ЛЮБАЯ проверенная версия — и это
    /// было верно, пока проверенная была одна. Теперь проверенных ДВЕ: `0.2.1-rc.2` — текущая
    /// (по умолчанию) и `0.1.5-rc.2` — прежняя, к которой возвращает откат. Подсказки рекомендуют
    /// 0.2.1, и прежнее правило отправило бы главную кнопку на главную дорогу — то есть поставило бы
    /// по ГЛАВНОМУ замку `0.1.5`, хотя карточка говорит «рекомендуемая 0.2.1». Это ровно та ложь,
    /// от которой и заведён отказ «две правды об одном дереве». Теперь главная дорога — только
    /// когда названная версия И ЕСТЬ версия главного замка; всякая другая (в том числе проверенная
    /// 0.2.1) идёт своей дорогой со своим замком.
    ///
    /// **Зачем отдельной функцией.** Развилка — РЕШЕНИЕ, и оно должно быть проверяемым без окна,
    /// без сети и без диска: в двери обновления эта же развилка решалась бы строкой кода, которую
    /// никакая проверка не увидит.
    /// </summary>
    public static string? RequestedFor(EngineTreeLock? engineLock, string? version)
    {
        var wanted = (version ?? string.Empty).Trim();
        if (wanted.Length == 0) return null;

        var known = engineLock ?? EngineTreeLock.Empty;
        if (!known.Any) return wanted;

        var same = string.Equals(
            Update.UpdateDecisions.Numeric(known.EngineVersion),
            Update.UpdateDecisions.Numeric(wanted),
            StringComparison.OrdinalIgnoreCase);

        return same ? null : wanted;
    }
    /// <summary>Версии, отмеченные в подсказках проверенными, — в порядке файла.</summary>
    public static IReadOnlyList<string> Verified(EngineHints hints)
    {
        ArgumentNullException.ThrowIfNull(hints);

        return hints.Versions
            .Where(hint => hint.Verified)
            .Select(hint => hint.Version)
            .ToList();
    }

    /// <summary>
    /// ЕСТЬ ЛИ ЗАМОК ДЛЯ ЭТОЙ ВЕРСИИ — то же правило, что у <see cref="Decide"/>: сверка идёт
    /// **числовой частью** (`0.1.5-rc.2` и `0.1.5` — одна версия, хвост предрелиза этим сравнением
    /// не различается, так решено владельцем). Пустой замок — «замка нет ни для чего».
    ///
    /// **Зачем отдельно.** Панель ставит движок только по замку, и «свежая версия» без замка
    /// поставиться НЕ МОЖЕТ: без этой проверки панель молча поставила бы по замку проверенную версию
    /// и объявила бы свежую — то есть сказала бы неправду.
    /// </summary>
    public static bool HasLockFor(EngineTreeLock? engineLock, string? version)
    {
        var known = engineLock ?? EngineTreeLock.Empty;

        if (!known.Any || string.IsNullOrWhiteSpace(version)) return false;

        return string.Equals(
            Update.UpdateDecisions.Numeric(known.EngineVersion),
            Update.UpdateDecisions.Numeric(version),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ЕСТЬ ЛИ ЗАМОК ДЛЯ ЭТОЙ ВЕРСИИ В НАБОРЕ ЗАМКОВ (задача `t4`, блок B: замков теперь два).
    ///
    /// Это тот же вопрос, что выше, но заданный ВСЕМУ набору: с 18.10.2026 панель знает два набора
    /// (0.1.5 — 588 пакетов, 0.2.1 — 683), и «поставить свежую версию» перестало быть синонимом
    /// «поставить нечего»: у 0.2.1 свой замок есть, и кнопка отдельной двери этим и живёт.
    /// </summary>
    public static bool HasLockForVersion(EngineTreeLocks? locks, string? version) =>
        (locks ?? EngineTreeLocks.Empty).Has(version);

    /// <summary>
    /// РЕШЕНИЕ ДЛЯ НАЗВАННОЙ ВЕРСИИ — путь отдельной просьбы о версии, у которой ЕСТЬ свой замок,
    /// но которая не отмечена в подсказках проверенной (так ставится 0.2.x до выпуска 2.2.0:
    /// человек нажал отдельную кнопку и подтвердил).
    ///
    /// **Чем отличается от <see cref="Decide"/>.** Там версия берётся ИЗ ПОДСКАЗОК и сверяется
    /// с замком («две правды об одном дереве»). Здесь версия названа человеком, и проверяется
    /// только одно: для неё есть годный замок — целый, с суммами, с официального реестра.
    /// Непроверенность версии — не отказ: это ровно то состояние, в котором живёт линия 0.2.x,
    /// и «ставить по замку» от неё не перестаёт быть верным.
    /// </summary>
    public static Plan DecideFor(EngineTreeLocks? locks, string? version, string? engineDir)
    {
        var all = locks ?? EngineTreeLocks.Empty;

        if (!all.Any) return Refuse(EngineInstallRefusal.NoLock);

        var known = all.For(version);
        if (!known.Any) return Refuse(EngineInstallRefusal.NoLockForVersion);

        if (!known.Complete || !known.FullySummed) return Refuse(EngineInstallRefusal.Incomplete);
        if (!known.AllFromRegistry) return Refuse(EngineInstallRefusal.ForeignSource);

        return new Plan(known.EngineVersion, OwnEngine.BinPath(engineDir, known.EngineVersion), EngineInstallRefusal.None);
    }

    private static Plan Refuse(EngineInstallRefusal refusal) =>
        new(string.Empty, string.Empty, refusal);
}

/// <summary>
/// ИМЕНА КЛЮЧЕЙ ОТКАЗОВ УСТАНОВКИ. Здесь только имена: строки живут в трёх словарях
/// (<c>PanelStrings</c> объявляет их свойствами с этими именами), второго списка фраз в коде нет —
/// иначе он разошёлся бы со словарём на первой же правке.
/// </summary>
public static class EngineInstallRefusals
{
    public const string NoLock = "EngineInstallNoLock";
    public const string NoHints = "EngineInstallNoHints";
    public const string NotVerified = "EngineInstallNotVerified";
    public const string Mismatch = "EngineInstallMismatch";
    public const string ForeignSource = "EngineInstallForeignSource";
    public const string Incomplete = "EngineInstallIncomplete";

    /// <summary>Node не нашёлся — установка идёт через npm, а он часть Node.</summary>
    public const string NodeMissing = "EngineInstallNodeMissing";

    /// <summary>Node старше порога (20) — панель не ставит движок под старый Node.</summary>
    public const string NodeOld = "EngineInstallNodeOld";

    /// <summary>npm рядом с Node и в PATH не нашёлся.</summary>
    public const string NpmMissing = "EngineInstallNpmMissing";

    /// <summary>npm отказал: не сошлась сумма вложения, нет сети, нет места — причина словами.</summary>
    public const string NpmFailed = "EngineInstallNpmFailed";

    /// <summary>Установка не уложилась в срок и снята вместе с потомками.</summary>
    public const string TooLong = "EngineInstallTooLong";

    /// <summary>Папку версии не завести или файлы замка не записать.</summary>
    public const string NoPlace = "EngineInstallNoPlace";

    /// <summary>Поставленное дерево разошлось с проверенным набором — установка не принята.</summary>
    public const string TreeWrong = "EngineInstallTreeWrong";

    /// <summary>Официальный файл сумм Node не прочитался.</summary>
    public const string NodeSourceFailed = "EngineInstallNodeSourceFailed";

    /// <summary>В официальном файле сумм нет нашего архива — сверять нечем.</summary>
    public const string NodeSumMissing = "EngineInstallNodeSumMissing";

    /// <summary>Сумма из официального файла и закреплённая в сборке разошлись.</summary>
    public const string NodeSumMismatch = "EngineInstallNodeSumMismatch";

    /// <summary>Архив Node не скачался.</summary>
    public const string NodeDownloadFailed = "EngineInstallNodeDownloadFailed";

    /// <summary>Архив Node не распаковался.</summary>
    public const string NodeUnpackFailed = "EngineInstallNodeUnpackFailed";

    /// <summary>Распакованный Node не запускается или моложе порога.</summary>
    public const string NodeBroken = "EngineInstallNodeBroken";

    /// <summary>Исполнитель: движок погашен.</summary>
    public const string Stopped = "EngineUpdateStopped";

    /// <summary>Исполнитель: погасить движок не удалось — установка не начиналась.</summary>
    public const string StopFailed = "EngineUpdateStopFailed";

    /// <summary>Исполнитель: предохранительная копия снята.</summary>
    public const string Copied = "EngineUpdateCopied";

    /// <summary>Исполнитель: проба на поставленном движке прошла.</summary>
    public const string Probed = "EngineUpdateProbed";

    /// <summary>Исполнитель: проба не прошла — «готово» не говорим.</summary>
    public const string ProbeFailed = "EngineUpdateProbeFailed";

    /// <summary>Исполнитель: обновление состоялось.</summary>
    public const string Done = "EngineUpdateDone";
    /// <summary>Строка блока движка: рекомендуемая (проверенная) версия.</summary>
    public const string RecommendedUpdate = "EngineUpdateRecommendedFormat";

    /// <summary>Строка блока движка: свежая версия — «новее, но нами не проверена».</summary>
    public const string FreshLine = "EngineUpdateFreshLineFormat";

    /// <summary>Кнопка отдельной двери для непроверенной версии.</summary>
    public const string FreshButton = "EngineUpdateFreshButton";

    /// <summary>Подтверждение перед постановкой НЕПРОВЕРЕННОЙ версии — с последствием для сессий словами.</summary>
    public const string FreshUnverifiedWarning = "EngineUpdateFreshWarningFormat";

    /// <summary>Обновление движка здесь недоступно вовсе (прогон проверки, изоляция, нет своей папки).</summary>
    public const string UpdateUnavailable = "EngineUpdateUnavailable";

    /// <summary>Замка для запрошенной версии нет — поставить её нельзя (ставить умеем только по замку).</summary>
    public const string NoLockForVersion = "EngineInstallNoLockForVersionFormat";

    /// <summary>Свежую версию из реестра узнать не удалось — строка свежей просто не показывается.</summary>
    public const string FreshUnavailableLog = "EngineUpdateFreshUnavailableLog";
    /// <summary>Ключ отказа. <see cref="EngineInstallRefusal.None"/> ключа не имеет — ставить можно.</summary>
    public static string Key(EngineInstallRefusal refusal) => refusal switch
    {
        EngineInstallRefusal.NoLock => NoLock,
        EngineInstallRefusal.NoLockForVersion => NoLockForVersion,
        EngineInstallRefusal.NoHints => NoHints,
        EngineInstallRefusal.NotVerified => NotVerified,
        EngineInstallRefusal.Mismatch => Mismatch,
        EngineInstallRefusal.ForeignSource => ForeignSource,
        EngineInstallRefusal.Incomplete => Incomplete,
        _ => string.Empty,
    };
}
