using System;
using System.Collections.Generic;
using System.IO;
using System.Security;

namespace DshPanel.Server;

/// <summary>
/// СКОЛЬКО СЕССИЙ НА ДИСКЕ И В КАКОМ ФОРМАТЕ ИХ ЖУРНАЛ — ФАКТ, СНЯТЫЙ С ДИСКА (блок C плана 2.2.0).
///
/// **Зачем панели свой счёт, когда у движка есть <c>formatStatus</c>.** Замер задачи `l2`
/// (18.10.2026, копия профиля владельца: 197 сессий) показал асимметрию, из-за которой предохранитель
/// и заводится: **вперёд движок говорит** (`session/list` отдаёт `formatStatus=migration-required`),
/// **а назад — МОЛЧИТ**: 0.1.5 не показал ни поля, ни слова, просто стало 197 → **196**. Значит сигнала
/// «я не вижу эти сессии» у панели нет и не будет — счёт она обязана уметь сама.
///
/// **Почему с диска, а не вызовом движка.** Три причины, и каждая — измеренная:
/// 1. **в момент отката движок может быть погашен** (его гасит сам путь обновления), а журнал на
///    диске никуда не девается;
/// 2. **у 0.1.5 поля `formatStatus` нет вовсе** — спросить некого, а вопрос «сколько спрячется»
///    задаётся ИМЕННО перед возвратом на 0.1.x;
/// 3. **чужой вызов в белом списке телефона — стоп-правило** (13 вызовов): считать надо так, чтобы
///    поверхность RPC не расширялась ни одним вызовом.
///
/// ⚠️ **Читаются ИМЕНА ФАЙЛОВ, а не содержимое.** Ни одной сессии не открывается, ни байта журнала
/// не разбирается: папка проекта → папка сессии → имя `session.vN.jsonl*`. Это же правило, по которому
/// копия показывает пути маской: содержимое сессий — личное, а формат — факт.
///
/// ⚠️ **Папки без журнала считаются отдельно** (<see cref="EngineJournalState.NoJournal"/>): у сессии,
/// которую движок только что завёл, файла ещё нет, и притворяться, что она «в старом формате», нельзя.
/// </summary>
public sealed record EngineJournalState(
    bool Measured,
    int Sessions,
    int NewFormat,
    int OldFormatOnly,
    int NoJournal,
    int Flat,
    string Detail)
{
    /// <summary>Дверей нет или не спрашивали: панель не имеет права считать это нулём.</summary>
    public static EngineJournalState Unexamined { get; } =
        new(false, 0, 0, 0, 0, 0, "no-door");

    /// <summary>Спросили и увидели: сессий нет вовсе (пустой или отсутствующий каталог).</summary>
    public static EngineJournalState Empty { get; } =
        new(true, 0, 0, 0, 0, 0, string.Empty);

    /// <summary>Спросить не удалось: причина — технической строкой (имя исключения, «нет пути»).</summary>
    public static EngineJournalState Unknown(string detail) =>
        new(false, 0, 0, 0, 0, 0, detail ?? string.Empty);

    /// <summary>
    /// СКОЛЬКО СЕССИЙ ПРЕЖНИЙ ДВИЖОК НЕ УВИДИТ: у них рядом со старым журналом уже лежит новый.
    /// Замер `l2`: сессия, открытая под 0.2.x, остаётся на диске целиком (v3 не переписывается),
    /// но 0.1.5 её не показывает вовсе — то есть «спрятать» здесь значит «исчезнуть из списка».
    /// </summary>
    public int HiddenFromOld => NewFormat;
}

/// <summary>
/// ЧТЕНИЕ ФОРМАТА ЖУРНАЛА С ДИСКА (блок C плана 2.2.0): раскладка — та же, что у движка и у копий.
///
/// <code>
/// &lt;дом движка&gt;\sessions\&lt;слаг рабочей папки&gt;\&lt;сессия&gt;\session.v3.jsonl.zstd
///                                                     \session.v4.jsonl.zstd   ← рядом, v3 не тронут
/// </code>
///
/// **Замер, на котором стоит вся эта арифметика** (`_team\220\l2-migraciya.md`, 18.10.2026):
/// 197 сессий, 179 подчинённых + 18 корневых; 0.2.1 видит 197/197 с `migration-required`; миграция
/// **ленивая** — по одной при открытии; v3 не переписывается (SHA-256 совпал, mtime не менялся);
/// после одного открытия под 0.2.1 список 0.1.5 стал **197 → 196 молча**. Новая работа под 0.2.x
/// (своя сессия, v4) старой версии тоже не видна: 0.2.1 видит 198, 0.1.5 — 196.
///
/// ⚠️ **Чего этот счёт не видит и не прячет:** «плоская» раскладка (файл журнала прямо в папке
/// проекта) считается отдельно (<see cref="EngineJournalState.Flat"/>) — движок такую папку отвергает
/// целиком, и молча приравнять её к «сессий нет» значило бы соврать. Незаконченная запись
/// (`…tmp`, замер 10.10.2026: погашенный движок оставляет файл журнала в `.tmp`) форматом
/// не считается: она ещё не журнал.
/// </summary>
public static class EngineJournalFormat
{
    /// <summary>Имя каталога сессий в доме движка (то же у 0.1.x и у 0.2.x — замер `l2`).</summary>
    public const string FolderName = "sessions";

    /// <summary>Начало имени файла журнала: `session.v3.jsonl.zstd`, `session.v4.jsonl.zstd`.</summary>
    public const string FilePrefix = "session.v";

    /// <summary>Формат, который читает 0.1.x (им и живёт наша установка).</summary>
    public const int OldGeneration = 3;

    /// <summary>Формат, который 0.2.x пишет и читает; 0.1.x его не видит вовсе (замер 10.10.2026).</summary>
    public const int NewGeneration = 4;

    /// <summary>Каталог сессий в названном доме движка — ОДНО место, где он складывается из частей.</summary>
    public static string SessionsDir(string dshHome) =>
        string.IsNullOrWhiteSpace(dshHome) ? string.Empty : Path.Combine(dshHome, FolderName);

    /// <summary>
    /// СОСЧИТАТЬ. Никогда не бросает: у двери раздела обновления нет права ронять окно из-за чужой
    /// беды, но и молчать о ней нельзя — причина уезжает в <see cref="EngineJournalState.Detail"/>
    /// и показывается человеку в скобках.
    /// </summary>
    public static EngineJournalState Read(string? sessionsDir)
    {
        if (string.IsNullOrWhiteSpace(sessionsDir))
            return EngineJournalState.Unknown("no-path");

        try
        {
            // Каталога нет — это ФАКТ «сессий нет», а не «не прочитали»: дом движка без `sessions`
            // бывает у чистой установки, и пугать человека неизвестностью там нечем.
            if (!Directory.Exists(sessionsDir)) return EngineJournalState.Empty;

            var sessions = 0;
            var newFormat = 0;
            var oldOnly = 0;
            var noJournal = 0;
            var flat = 0;

            foreach (var project in Directory.EnumerateDirectories(sessionsDir))
            {
                // «Плоская» раскладка: файл журнала прямо в папке проекта. Движок такую папку
                // отвергает целиком («uses the unsupported flat-file layout»), поэтому считаем
                // отдельным числом, а не сессией.
                foreach (var loose in Directory.EnumerateFiles(project))
                    if (GenerationOf(Path.GetFileName(loose)) > 0) flat++;

                foreach (var session in Directory.EnumerateDirectories(project))
                {
                    sessions++;

                    var generation = HighestIn(session);

                    if (generation >= NewGeneration) newFormat++;
                    else if (generation >= OldGeneration) oldOnly++;
                    else noJournal++;
                }
            }

            return new EngineJournalState(true, sessions, newFormat, oldOnly, noJournal, flat, string.Empty);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Каталог занят, доступ закрыт, диск отвалился — «не знаю» честнее нуля.
            return EngineJournalState.Unknown(error.GetType().Name);
        }
    }

    /// <summary>Старший формат журнала в папке сессии; 0 — журнала нет.</summary>
    public static int HighestIn(string sessionDir)
    {
        if (string.IsNullOrWhiteSpace(sessionDir) || !Directory.Exists(sessionDir)) return 0;

        var highest = 0;

        foreach (var file in Directory.EnumerateFiles(sessionDir))
        {
            var generation = GenerationOf(Path.GetFileName(file));
            if (generation > highest) highest = generation;
        }

        return highest;
    }

    /// <summary>
    /// ФОРМАТ ИЗ ИМЕНИ ФАЙЛА: `session.v4.jsonl.zstd` → 4, `session.v3.jsonl` → 3, всё прочее → 0.
    /// Незаконченная запись (`…tmp`) форматом не считается — это ещё не журнал (замер 10.10.2026).
    /// </summary>
    public static int GenerationOf(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return 0;

        var name = fileName.Trim();

        if (!name.StartsWith(FilePrefix, StringComparison.Ordinal)) return 0;
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return 0;

        var digits = new List<char>();
        for (var index = FilePrefix.Length; index < name.Length; index++)
        {
            if (char.IsAsciiDigit(name[index]))
            {
                digits.Add(name[index]);
                continue;
            }

            // За цифрами обязан идти разделитель: `session.v4x…` — не наш файл.
            if (name[index] != '.') return 0;

            break;
        }

        if (digits.Count == 0 || digits.Count > 3) return 0;

        return int.TryParse(new string(digits.ToArray()), out var generation) ? generation : 0;
    }
}
