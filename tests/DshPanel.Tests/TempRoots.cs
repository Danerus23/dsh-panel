using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace DshPanel.Tests;

/// <summary>
/// ВРЕМЕННЫЕ КОРНИ ПРОГОНА: У КАЖДОГО ПРОГОНА СВОИ, НИ ОДНОГО ОБЩЕГО КАТАЛОГА (t3, 18.10.2026).
///
/// **От какого случая.** Два ОДНОВРЕМЕННЫХ `dotnet test` делили каталоги в `%TEMP%`, и прогон
/// зависел от соседа. Владелец и проверяющий видели это как `PhonePassTests` →
/// `DirectoryNotFoundException` на `…\state\phone-pass.txt`; 18.10.2026 воспроизведено и вторым
/// лицом того же класса — `ServerSectionLookTests` → `IOException`: «файл
/// `%TEMP%\dsh-panel-look-frames\settings-server-Light.png` занят другим процессом».
///
/// Две общие вещи и были причиной:
/// * <c>PanelTestStand.SaveFrame</c> писал кадры в ОДНУ папку с ОДНИМИ именами файлов — второй
///   прогон открывал занятый файл;
/// * уборка (`RemoveTemp` в десятке фикстур) снимала не только свой корень, но и ОБЩИЙ родитель
///   семейства (`%TEMP%\dsh-panel-look-tests` и т. п.) — то есть один прогон мог удалить каталог,
///   в котором в это время работал другой.
///
/// **Правило.** Всё, что прогон создаёт, лежит под <see cref="Run"/> — каталогом С НОМЕРОМ ПРОЦЕССА.
/// Снимается ровно то, что создал этот прогон (<see cref="Remove"/>); общий каталог не снимается
/// никем и никогда. Имя семейства (`look-tests`, `settings-tests`, …) остаётся в пути — по нему
/// корни и кадры находят глазами.
///
/// ⚠️ **Флаг окружения не нужен и не заводится:** свой корень — это не режим, а всегда.
///
/// **УБОРКА БРОШЕННОГО (t2 цикла 2.1.2, D2).** Правило «никто не удаляет чужое» имеет цену: каталоги
/// копятся по одному на прогон. Замер на входе задачи: **66 каталогов / 28,54 МБ**, ~0,72 МБ на прогон
/// (кадры). Поэтому брошенное убирается — но по трём условиям сразу, и все три обязательны:
///
/// 1. **чужой** — каталог ЭТОГО прогона не убирается никогда (<see cref="SweepAbandoned"/> пропускает
///    его по имени; свой снимает выход прогона);
/// 2. **процесс с этим номером уже не жив** — номер берётся из имени `run-&lt;pid&gt;`, и живой процесс
///    не трогается НИКОГДА, даже если каталог старый;
/// 3. **возраст ≥ <see cref="AbandonedMinAge"/>** (1 час) — номер процесса переиспользуется: «номер не
///    жив» само по себе ещё не значит «прогон брошен», а вот живой прогон моложе часа точно не брошен.
///
/// ⚠️ **Номер процесса переиспользуется, и одним «жив» это не закрывается** (измерено на первом же
/// прогоне: из 66 каталогов **13** не убрались, потому что их номера заняли живые чужие процессы —
/// например узел MSBuild, стартовавший позже каталога). Поэтому живой номер защищает каталог только
/// тогда, когда этот процесс МОГ его создать: стартовал не позже последней записи в каталог
/// (+<see cref="PidReuseTolerance"/>). Живой прогон пишет в свой каталог ПОСЛЕ своего старта — он
/// под это правило не попадает никогда, а чужой номер — попадает.
///
/// Свой каталог снимается **при выходе** прогона (<see cref="Initialize"/> вешает обработчик выхода):
/// иначе он остался бы навсегда, ведь убирать чужое запрещено. ⚠️ **Цена названа вслух:** кадры
/// проверок после прогона больше НЕ лежат в `%TEMP%` — их видно только во время прогона (путь печатает
/// сама проверка). Это плата за то, что `%TEMP%` не растёт на 28 МБ в день.
/// </summary>
internal static class TempRoots
{
    private const string RunPrefix = "run-";

    private static int _started;

    /// <summary>
    /// Общий родитель прогонов: `%TEMP%\dsh-panel-tests`. Он НЕ снимается никем — только его
    /// брошенные дети по правилам <see cref="SweepAbandoned"/>.
    /// </summary>
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "dsh-panel-tests");

    /// <summary>
    /// Корень ЭТОГО прогона: `%TEMP%\dsh-panel-tests\run-&lt;номер процесса&gt;`.
    /// Номер процесса разводит прогоны: две копии `dotnet test` — два разных каталога.
    /// </summary>
    public static string Run { get; } = Path.Combine(Root, RunPrefix + Environment.ProcessId);

    /// <summary>Кадры, снятые прогоном (человек смотрит их глазами): `&lt;корень прогона&gt;\frames`.</summary>
    public static string Frames { get; } = Path.Combine(Run, "frames");

    /// <summary>
    /// Возраст, раньше которого каталог не считается брошенным. Час — не «на глаз»: номер процесса
    /// переиспользуется, и молодой каталог с уже мёртвым номером мог оставить ЖИВОЙ прогон, чей
    /// процесс успел завершиться секунду назад (или каталог завёл не прогон вовсе).
    /// </summary>
    public static TimeSpan AbandonedMinAge { get; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Допуск при сверке «когда стартовал процесс» с «когда последний раз писали в каталог».
    /// Номера процессов в Windows переиспользуются: замер на входе задачи — из 66 каталогов **13**
    /// не убрались именно потому, что их номера к тому моменту занимали ЖИВЫЕ чужие процессы
    /// (например узел MSBuild), стартовавшие много позже каталога. Пять секунд — на неточность
    /// часов и файловых отметок; этого хватает, чтобы живой прогон (он пишет в каталог ПОСЛЕ своего
    /// старта) остался неприкосновенным.
    /// </summary>
    public static TimeSpan PidReuseTolerance { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// СТАРТ ПРОГОНА: один раз на процесс — убрать брошенные каталоги и взять на себя снятие своего.
    /// Зовётся модульным инициализатором, то есть до первой проверки, а не «когда дойдут руки».
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        try
        {
            // Не Console.WriteLine: модульный инициализатор бежит и в том процессе, который отдаёт
            // xunit JSON о сборке, — печать в stdout ломает обмен («Test process did not return valid
            // JSON», замерено). Уборка поэтому молчит, а доказательство берётся из LastSwept и замера.
            LastSwept = SweepAbandoned(Root, DateTime.UtcNow, AbandonedMinAge);
        }
        catch
        {
            // Уборка — не предмет проверки: её сбой не должен ронять прогон.
        }

        // Своё снимает выход прогона — и только он. Сбой уборки проверку не роняет (см. Remove).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Remove(Run);
    }

    /// <summary>Что уборка на старте ЭТОГО процесса сняла (имена каталогов). Пусто — убирать было нечего.</summary>
    internal static IReadOnlyList<string> LastSwept { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Временный корень проверки: `&lt;корень прогона&gt;\&lt;семейство&gt;\&lt;guid&gt;`.
    /// Двух одинаковых не бывает, и чужой прогон в этот путь не заходит.
    /// </summary>
    public static string For(string family)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);

        return Path.Combine(Run, family, Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// УБОРКА: снимает ТОЛЬКО свой каталог — и ничего больше. Каталог семейства и каталог прогона
    /// не снимаются: рядом может идти второй прогон (или другая проверка этого же прогона), и
    /// удаление общего каталога — это ровно тот дефект, от которого этот класс и заведён.
    /// Пустой каталог семейства остаётся в `%TEMP%` — это дешевле, чем снесённый чужой корень.
    /// Ошибка уборки проверку не роняет: уборка не предмет проверки.
    /// </summary>
    public static void Remove(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;

        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Каталог занят другой проверкой (этого же или соседнего прогона) — уборка не обязана побеждать.
        }
    }

    /// <summary>
    /// УБОРКА БРОШЕННОГО: снимает каталоги `run-*` в названном родителе, которые (1) не свой,
    /// (2) старше <paramref name="minAge"/> и (3) чей процесс уже не жив — или чей номер достался
    /// ДРУГОМУ процессу (он стартовал позже последней записи в каталог, +<see cref="PidReuseTolerance"/>).
    /// Возвращает ИМЕНА убранных — по ним проверка предъявляет доказательство, а не «оно само почистилось».
    /// Всё, что не подошло хотя бы под одно условие, остаётся: ошибиться в сторону «не трогать» дешевле.
    /// </summary>
    internal static IReadOnlyList<string> SweepAbandoned(string root, DateTime utcNow, TimeSpan minAge)
    {
        var removed = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return removed;

        string[] candidates;
        try
        {
            candidates = Directory.GetDirectories(root, RunPrefix + "*");
        }
        catch
        {
            return removed;
        }

        foreach (string dir in candidates)
        {
            string name = Path.GetFileName(dir);

            // СВОЙ каталог не убирается никогда: его снимает выход прогона (Initialize → ProcessExit).
            if (string.Equals(name, Path.GetFileName(Run), StringComparison.OrdinalIgnoreCase)) continue;

            // Имя не нашего формата (`run-`, `run-abc`, каталоги семейств) — не трогаем.
            if (!TryReadRunPid(name, out int pid)) continue;

            // Молодой каталог не трогаем: номер процесса переиспользуется, и «номер мёртв» ещё не
            // значит «прогон брошен».
            DateTime lastWrite = LastWriteUtc(dir);
            if (utcNow - lastWrite < minAge) continue;

            // ЖИВОЙ процесс защищает каталог — но только если он МОГ его создать, то есть стартовал
            // не позже последней записи в каталог (+допуск). Иначе номер достался другому процессу:
            // такой каталог брошен, и держать его вечно из-за чужого номера незачем.
            // Номер 0 — не прогон, а простой системы: процесса-владельца у такого каталога нет.
            if (pid > 0)
            {
                (bool alive, DateTime? startedUtc) = ProcessIdentity(pid);
                bool couldOwn = alive && (startedUtc is null || startedUtc.Value <= lastWrite + PidReuseTolerance);
                if (couldOwn) continue;
            }

            if (TryDelete(dir)) removed.Add(name);
        }

        return removed;
    }

    /// <summary>
    /// Имя каталога → номер процесса прогона. Наш формат — `run-` и ТОЛЬКО цифры после него:
    /// `run-`, `run-abc`, `run--5` и `run- 12` не трогаются (это не то, что заводит оснастка).
    /// </summary>
    internal static bool TryReadRunPid(string name, out int pid)
    {
        pid = 0;
        if (string.IsNullOrEmpty(name)) return false;
        if (!name.StartsWith(RunPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        ReadOnlySpan<char> digits = name.AsSpan(RunPrefix.Length);
        if (digits.IsEmpty) return false;
        foreach (char c in digits)
        {
            if (c is < '0' or > '9') return false;
        }

        return int.TryParse(digits, out pid);
    }

    /// <summary>
    /// Жив ли процесс с названным номером. Недоступный процесс (нет прав) считается ЖИВЫМ: удалить
    /// чужой живой каталог хуже, чем оставить мёртвый.
    /// </summary>
    internal static bool IsProcessAlive(int pid) => ProcessIdentity(pid).Alive;

    /// <summary>
    /// «Жив ли» и «когда стартовал» одним заходом: номер процесса переиспользуется, и по одному
    /// «жив» нельзя понять, тот ли это процесс, что завёл каталог.
    /// Время старта недоступно (нет прав) — <c>null</c>: такой процесс считается возможным владельцем.
    /// </summary>
    internal static (bool Alive, DateTime? StartedUtc) ProcessIdentity(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited) return (false, null);

            try
            {
                return (true, process.StartTime.ToUniversalTime());
            }
            catch
            {
                return (true, null); // доступа к времени старта нет — считаем возможным владельцем
            }
        }
        catch (ArgumentException)
        {
            return (false, null); // процесса с таким номером нет
        }
        catch
        {
            return (true, null); // доступа нет — считаем живым
        }
    }

    /// <summary>Время последней записи каталога. Не прочиталось — считаем «молодым» (не трогаем).</summary>
    private static DateTime LastWriteUtc(string dir)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(dir);
        }
        catch
        {
            return DateTime.MaxValue;
        }
    }

    private static bool TryDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            Directory.Delete(dir, recursive: true);
            return !Directory.Exists(dir);
        }
        catch
        {
            return false;
        }
    }
}
