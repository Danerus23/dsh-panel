using System.Globalization;

namespace DshPanel.Server;

/// <summary>
/// Запись «этот сервер — НАШ»: порт, номер процесса, время его создания **и какой движок поднят**
/// (путь к <c>bin.js</c> и версия пакета).
///
/// Зачем она нужна. Панель при выходе сервер НЕ гасит (так решено: закрытие окна не должно
/// обрывать работу человека). Значит после перезапуска панели её собственный сервер продолжал бы
/// работать — а живого дескриптора процесса у новой панели нет, и он выглядел бы «найденным чужим»:
/// панель предложила бы в него «встроиться», а «Остановить» отказывалась бы его гасить.
///
/// Три числа, а не одно: **номера процессов переиспользуются**, и по одному номеру панель однажды
/// погасила бы чужой процесс (урок v1). Поэтому процесс признаётся своим только тогда, когда
/// совпало И время его создания.
///
/// ⚠️ **С этапа E3 запись несёт ещё путь и версию движка** (решение владельца 10.10.2026):
/// панель ставит **свой** движок и **обновляет** его, а обновление порождает **новый процесс** —
/// другая пара «номер + время». Одной этой пары мало: свой же сервер стал бы «чужим». Третий,
/// version-независимый признак владения живёт рядом (<see cref="OwnEngine.IsOwnBinPath"/>) и
/// спрашивается у **живого** процесса, а в записи хранится то, **чем панель его поднимала** —
/// чтобы отчёт и откат (E3.3) знали, какая версия сейчас работает.
///
/// Формат — одна строка: <c>порт;PID;время создания;путь к bin.js;версия движка</c>. Хвост
/// **необязателен**: записи прежних сборок (три поля) читаются по-прежнему, и это не «пустая
/// запись», а «движок не назван».
/// </summary>
public readonly record struct OwnServerRecord(
    int Port,
    int Pid,
    long StartTicks,
    string EnginePath = "",
    string EngineVersion = "")
{
    /// <summary>
    /// Строка файла. Инвариантная культура: файл читает и машина, и человек.
    ///
    /// ⚠️ **Движок не назван — строка остаётся прежней, из трёх полей.** Так записи, которые
    /// панель писала до этапа E3, не переписываются зря, а «нет движка» видно по самой строке,
    /// а не по пустым полям на конце.
    /// </summary>
    public string Format()
    {
        var head = string.Join(
            ';',
            Port.ToString(CultureInfo.InvariantCulture),
            Pid.ToString(CultureInfo.InvariantCulture),
            StartTicks.ToString(CultureInfo.InvariantCulture));

        if (!NamesEngine) return head;

        return head + ";" + (EnginePath ?? string.Empty) + ";" + (EngineVersion ?? string.Empty);
    }

    /// <summary>
    /// Разбор строки файла. Мусор, обрезанная строка и пустое дают <c>null</c> — «записи нет»,
    /// а не «запись с нулями»: по нулевой записи панель не признала бы своим ничего, но и
    /// сказать о ней было бы нечего.
    ///
    /// ⚠️ **Хвост (путь и версия) разбирается терпимо и НЕ ломает ядро записи:** испорченный хвост
    /// (в том числе путь с точкой с запятой в имени — а такое имя в Windows законно) не должен
    /// стоить панели признания своего сервера. Три первых поля — то, чем живо владение.
    /// Путь принимается только похожий на движок (<see cref="ServerDecisions.LooksLikeDshBin"/>):
    /// иначе мусор в хвосте стал бы «именем движка» в журнале и в отчёте — а этого панель
    /// не делает даже про себя.
    /// </summary>
    public static OwnServerRecord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 5 частей максимум: точка с запятой внутри пути остаётся в четвёртой части, а не рвёт строку.
        var parts = text.Trim().Split(';', 5);
        if (parts.Length < 3) return null;

        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)) return null;
        if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return null;
        if (!long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)) return null;

        if (port is <= 0 or > 65535 || pid <= 0 || ticks <= 0) return null;

        var enginePath = parts.Length > 3 && ServerDecisions.LooksLikeDshBin(parts[3]) ? parts[3].Trim() : string.Empty;
        var engineVersion = enginePath.Length > 0 && parts.Length > 4 ? parts[4].Trim() : string.Empty;

        return new OwnServerRecord(port, pid, ticks, enginePath, engineVersion);
    }

    /// <summary>
    /// Тот же ли это процесс, что записан. Сверяются ОБА числа: порт и время создания.
    /// Номер процесса один — по нему сын чужой работы признавался бы своим.
    /// </summary>
    public bool Matches(int listenerPid, long listenerStartTicks) =>
        listenerPid == Pid && listenerStartTicks != 0 && listenerStartTicks == StartTicks;

    /// <summary>
    /// Свой ли это сервер — по **пути запуска живого процесса**. Третья примета владения
    /// (этап E3): она не зависит ни от номера процесса, ни от версии движка, и именно она спасает
    /// признание своего сервера **после обновления движка**.
    /// </summary>
    public bool LaunchedFromOwnEngine(string? runningBinPath, string? engineDir) =>
        Port > 0 && OwnEngine.IsOwnBinPath(runningBinPath, engineDir);

    /// <summary>
    /// НАЗЫВАЕТ ЛИ ЗАПИСЬ НАШ ДВИЖОК — по своей же четвёртой части (путь к <c>bin.js</c>).
    ///
    /// **Чем это отличается от <see cref="LaunchedFromOwnEngine"/>.** Та примета спрашивает путь
    /// у ЖИВОГО процесса и потому требует, чтобы слушатель был найден. Эта отвечает по файлу:
    /// «панель когда-то поднимала сервер на этом порту СВОИМ движком». Нужна там, где слушателя
    /// назвать не удалось (DEF-074): запись остаётся единственным доказательством владения,
    /// и без неё «Запустить» отказало бы «порт занят» в свой же сервер.
    ///
    /// Ответ «нет» безопасен всегда: запись прежних сборок движка не называет, а чужой
    /// глобальный движок в нашу папку не попадает — сверка идёт по границе ИМЕНИ папки,
    /// а не по вхождению подстроки (<see cref="OwnEngine.IsOwnBinPath"/>).
    /// </summary>
    public bool NamesOwnEngine(string? engineDir) =>
        Port > 0 && OwnEngine.IsOwnBinPath(EnginePath, engineDir);

    /// <summary>Назван ли в записи движок: пусто — запись прежней сборки, и это не ошибка.</summary>
    public bool NamesEngine =>
        !string.IsNullOrWhiteSpace(EnginePath) || !string.IsNullOrWhiteSpace(EngineVersion);

    /// <summary>
    /// Версия движка словами для отчёта: из записи, а если её там нет — из папки движка по пути.
    /// Пусто — «не названа», и врать про неё панель не станет.
    /// </summary>
    public string EngineVersionOrFolder(string? engineDir)
    {
        if (!string.IsNullOrWhiteSpace(EngineVersion)) return EngineVersion.Trim();

        return OwnEngine.VersionFolder(EnginePath, engineDir);
    }
}
