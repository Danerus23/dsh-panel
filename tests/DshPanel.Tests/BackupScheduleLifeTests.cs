using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Settings;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// РАСПИСАНИЕ, РОТАЦИЯ И СЕРИАЛИЗАЦИЯ В ЖИЗНИ ПАНЕЛИ (v2.2) — то, что уже не чистое решение,
/// а поведение контроллера: часы, снятие копии на ходу, уборка после неё и запрет второй работы.
///
/// Три вещи проверяются здесь потому, что дороже всего ошибиться именно в них:
///
/// 1. **автоматической копии не бывает в прогоне проверки и в изоляции** — это красная линия,
///    и нарушение её означало бы, что прогон снял копию в каталог владельца;
/// 2. **копия по расписанию снимается НА ХОДУ** и человек об этом предупреждается — решение
///    владельца 26.09.2026 («сервер ради копии не гасится»);
/// 3. **вторая работа не начинается**, пока идёт первая: копия и накат пишут в одни и те же файлы.
///
/// Всё идёт на подставном дереве под <see cref="AppPaths.Under"/>: ни каталогов владельца,
/// ни его копий эти проверки не касаются. Копии — настоящие (движок копии работает целиком),
/// но на дереве в считанные килобайты.
/// </summary>
public class BackupScheduleLifeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(3));

    private sealed record Stand(BackupController Control, AppPaths Paths, PanelSettings Settings, List<string> Log, List<NoticeKind> Notices);

    /// <summary>
    /// Стенд: подставной корень с мусором (чтобы режимы объёма различались), свой журнал,
    /// свой список сообщений. Часы неподвижны — иначе «пора/рано» зависело бы от скорости прогона.
    /// </summary>
    /// <param name="onLocateEngine">
    /// Зовётся КАЖДЫЙ раз, когда панель ищет движок, — то есть когда начинается настоящая работа
    /// с копией (<c>CreateCopy</c> зовёт это первым делом). Стенду это нужно, чтобы проверка красной
    /// линии могла утверждать «такта не было вовсе», а не только «решение было false»: у ложного
    /// решения много причин, а у поиска движка — ровно одна, и она означает ПОПЫТКУ.
    /// </param>
    private static Stand Build(
        Action<PanelSettings>? tune = null,
        bool allowed = true,
        bool isolatedRun = false,
        bool serverRunning = false,
        Action? onLocateEngine = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var paths = AppPaths.Under(root);
        Directory.CreateDirectory(paths.DshHome);

        Write(Path.Combine(paths.DshHome, "settings.yaml"), 200);
        Write(Path.Combine(paths.Root, "заметка.txt"), 300);
        Write(Path.Combine(paths.Root, "node_modules", "pkg", "index.js"), 20_000);
        Write(Path.Combine(paths.Root, "bin", "out.dll"), 20_000);
        Write(Path.Combine(paths.Root, "obj", "tmp.o"), 20_000);
        Write(Path.Combine(paths.Root, "logs", "run.log"), 20_000);

        var settings = new PanelSettings { BackupFolder = paths.BackupsDir };
        tune?.Invoke(settings);

        var log = new List<string>();
        var notices = new List<NoticeKind>();

        var control = new BackupController(
            paths,
            () => settings,
            allowed: allowed,
            locateEngine: () =>
            {
                onLocateEngine?.Invoke();
                return null;
            },
            clock: () => Now,
            log: line => log.Add(line),
            settingsOwner: null,
            confineToRunRoot: isolatedRun,
            isolatedRun: isolatedRun,
            serverRunning: () => serverRunning,
            notify: (kind, _, _) => notices.Add(kind),
            dispatch: action => action());

        return new Stand(control, paths, settings, log, notices);
    }

    /// <summary>Дождаться условия: фоновая копия заканчивается в чужой нити.</summary>
    private static bool WaitFor(Func<bool> condition, int milliseconds = 30000)
    {
        for (var waited = 0; waited < milliseconds && !condition(); waited += 50) Thread.Sleep(50);

        return condition();
    }

    private static void Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string('x', bytes));
    }

    /// <summary>Начало строки словаря до первой подстановки — по нему видно, какая это строка.</summary>
    private static string Head(string format) => format.Split('{')[0];

    private static void Cleanup(Stand stand)
    {
        try
        {
            Directory.Delete(stand.Paths.Root, true);
        }
        catch
        {
            // Уборка подставного корня не имеет права уронить проверку.
        }
    }

    // --- красная линия: автоматики в изоляции нет ---------------------------

    /// <summary>
    /// Изолированный прогон: право снимать копии есть (его просят человек или проверка), а часов
    /// автокопии НЕТ — ни в <c>Start</c>, ни в <c>Tick</c>.
    ///
    /// ⚠️ **Проверка утверждает то, что панель СДЕЛАЛА, а не «решение рядом»** (нашёл холодный
    /// проверяющий 26.09.2026: <c>Assert.False(due.Take)</c> — плохое доказательство, «false»
    /// бывает у многих причин). Поэтому здесь три независимых свидетеля, и каждый падает от своей
    /// снятой двери:
    ///
    /// 1. **часы не заведены** — <c>Start</c> не пишет строку «часы автокопии запущены»;
    /// 2. **решение такта — ИМЕННО подавленное**, а не «рано» или «выключено»;
    /// 3. **попытки не было вовсе** — движок не искали (его ищут первым делом при снятии копии),
    ///    папки копий не появилось, и даже через полторы секунды ни одного архива нет.
    /// </summary>
    [Fact]
    public void В_изолированном_прогоне_автокопии_не_бывает()
    {
        var lookups = 0;
        var stand = Build(isolatedRun: true, onLocateEngine: () => Interlocked.Increment(ref lookups));

        try
        {
            stand.Control.Start();

            // 1. Часов нет — и это в журнал, а не молчанием.
            Assert.Contains(PanelStrings.BackupScheduleSuppressedLog, stand.Log);
            Assert.DoesNotContain(PanelStrings.BackupScheduleClockLog, stand.Log);

            // 2. Такт отвечает подавленным решением — не «рано» и не «выключено».
            var due = stand.Control.Tick(Now.AddDays(30));

            Assert.Equal(PanelStrings.BackupScheduleSuppressedLog, due.Reason);
            Assert.False(due.Take);

            // 3. Ни одной попытки: движка не искали, папки нет, архивов нет и не появится.
            Assert.Equal(0, Volatile.Read(ref lookups));
            Assert.False(Directory.Exists(stand.Paths.BackupsDir));

            Thread.Sleep(1500);

            Assert.Equal(0, Volatile.Read(ref lookups));
            Assert.Empty(Directory.GetFiles(stand.Paths.Root, "dsh2-backup-*.zip", SearchOption.AllDirectories));
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// Прогон проверки (без права снимать копии): часов тоже нет, и причина названа словами.
    /// Это тот случай, ради которого красная линия и написана: иначе `--shot` снял бы копию
    /// в каталог владельца.
    /// </summary>
    [Fact]
    public void В_прогоне_проверки_автокопии_не_бывает()
    {
        var lookups = 0;
        var stand = Build(allowed: false, onLocateEngine: () => Interlocked.Increment(ref lookups));

        try
        {
            stand.Control.Start();

            Assert.Contains(PanelStrings.BackupNotAllowed, stand.Log);
            Assert.DoesNotContain(PanelStrings.BackupScheduleClockLog, stand.Log);

            var due = stand.Control.Tick(Now.AddDays(30));

            // Та же дверь, что у изоляции, но по другой причине — «копии в этом прогоне нельзя»:
            // решение обязано быть подавленным, а не «пора».
            Assert.Equal(PanelStrings.BackupScheduleSuppressedLog, due.Reason);

            // И попытки не было: движок не искали, папки копий нет.
            Assert.Equal(0, Volatile.Read(ref lookups));
            Assert.False(Directory.Exists(stand.Paths.BackupsDir));
        }
        finally
        {
            Cleanup(stand);
        }
    }

    // --- пора: копия снимается сама -----------------------------------------

    /// <summary>
    /// Копий ещё не было, расписание включено — панель снимает первую сама, В ФОНЕ, и говорит
    /// об этом в журнале. Это и есть «догоняет пропущенное при запуске».
    /// </summary>
    [Fact]
    public void Пора_панель_снимает_копию_сама()
    {
        var stand = Build();

        try
        {
            var due = stand.Control.Tick(Now);

            Assert.True(due.Take);

            Assert.True(
                WaitFor(() => Directory.Exists(stand.Paths.BackupsDir)
                              && Directory.GetFiles(stand.Paths.BackupsDir, "dsh2-backup-*.zip").Length == 1),
                "копия по расписанию не появилась: " + string.Join(" | ", stand.Log));

            // И журнал говорит о ней словами — копия снята РАСПИСАНИЕМ, а не человеком.
            Assert.True(
                WaitFor(() => stand.Log.Any(line =>
                    line.StartsWith(Head(PanelStrings.BackupScheduleDoneFormat), StringComparison.Ordinal))),
                "в журнале нет строки о снятой автокопии: " + string.Join(" | ", stand.Log));
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// Прошлая копия свежая — панель НЕ снимает вторую, и причина названа словами. Отсчёт берётся
    /// из ИМЕНИ файла: это и значит «прошлая копия определяется по файлу, а не по памяти».
    /// </summary>
    [Fact]
    public void Рано_копия_не_снимается()
    {
        var stand = Build();

        try
        {
            Directory.CreateDirectory(stand.Paths.BackupsDir);
            File.WriteAllText(
                Path.Combine(stand.Paths.BackupsDir, BackupNaming.ArchiveName(Now.AddHours(-3), withEngine: true)),
                "старая копия");

            var due = stand.Control.Tick(Now);

            Assert.False(due.Take);
            Assert.Contains("3", due.Reason, StringComparison.Ordinal);

            // Первый такт пишет причину в журнал: человек обязан видеть, чего ждёт панель.
            Assert.Contains(stand.Log, line => line.Contains(Head(PanelStrings.BackupScheduleEarlyFormat), StringComparison.Ordinal));

            Assert.Single(Directory.GetFiles(stand.Paths.BackupsDir, "dsh2-backup-*.zip"));
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// Копия, снятая РУКОЙ, — это тоже прошлая копия: отсчёт идёт от неё, а не от памяти панели.
    /// Поэтому «создать копию сейчас» не заводит второго места правды о том, когда была копия.
    /// </summary>
    [Fact]
    public void Ручная_копия_становится_прошлой_для_расписания()
    {
        var stand = Build();

        try
        {
            Directory.CreateDirectory(stand.Paths.BackupsDir);

            var copy = stand.Control.CreateCopy(
                Path.Combine(stand.Paths.BackupsDir, BackupNaming.ArchiveName(Now.AddHours(-1), withEngine: true)),
                serverRunning: false, shareable: false);

            Assert.True(copy.Ok, copy.Error);

            var due = stand.Control.Tick(Now);

            Assert.False(due.Take);
            Assert.Contains("1 ч", due.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(stand);
        }
    }

    // --- копии панели 1.x не сбивают наши часы ---------------------------------

    /// <summary>
    /// ДЕФЕКТ, РАДИ КОТОРОГО ПИСАЛАСЬ ПРАВКА: свежий архив панели 1.x НЕ отменяет нашу копию.
    /// Папка копий у двух панелей общая (решение владельца 26.09.2026), и до 26.09.2026 2.0
    /// отсчитывала свои сутки от ЧУЖОГО архива — в журнале владельца так и стояло
    /// «автокопия: рано: прошло 23 ч 34 мин из 24 ч», а своя копия не снималась вовсе.
    ///
    /// ⚠️ Доказывается не решением, а ДЕЛОМ: одного <c>Assert.True(due.Take)</c> мало (у «пора»
    /// бывают разные причины), поэтому проверка ДОЖИДАЕТСЯ появления нашего архива на диске.
    /// И рядом второе утверждение — чужой архив остался цел: ни съёмка, ни ротация за ним не пришли.
    /// </summary>
    [Fact]
    public void Свежая_копия_панели_1_x_не_отменяет_нашу()
    {
        var stand = Build();

        try
        {
            Directory.CreateDirectory(stand.Paths.BackupsDir);

            // Копия прежней панели, снятая ТОЛЬКО ЧТО, — свежее некуда.
            var foreign = "dsh-backup-" + Now.AddMinutes(-1).ToString("yyyy-MM-dd-HHmmss") + "-full.zip";
            File.WriteAllText(Path.Combine(stand.Paths.BackupsDir, foreign), "копия панели 1.x");

            var due = stand.Control.Tick(Now);

            Assert.True(due.Take, "чужая копия отменила нашу съёмку: " + due.Reason);

            Assert.True(
                WaitFor(() => Directory.GetFiles(stand.Paths.BackupsDir, "dsh2-backup-*.zip").Length == 1),
                "наша копия так и не появилась: " + string.Join(" | ", stand.Log));

            // Чужой архив цел: ни съёмка, ни ротация за ним не пришли.
            Assert.True(File.Exists(Path.Combine(stand.Paths.BackupsDir, foreign)), "чужая копия исчезла");
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// Обратная половина пары, и без неё первая ничего не стоит: НАША свежая копия съёмку отменяет —
    /// даже когда рядом лежит чужая и она ещё свежее. То есть отсчёт идёт по нашим архивам, а не
    /// «по самому свежему файлу в папке».
    /// </summary>
    [Fact]
    public void Наша_свежая_копия_отменяет_съёмку_даже_рядом_с_чужой()
    {
        var stand = Build();

        try
        {
            Directory.CreateDirectory(stand.Paths.BackupsDir);

            // Наша — час назад (интервал 24 ч): этого мало для новой копии.
            File.WriteAllText(
                Path.Combine(stand.Paths.BackupsDir, BackupNaming.ArchiveName(Now.AddHours(-1), withEngine: true)),
                "наша копия");

            // Чужая — минуту назад: она свежее, и именно на ней раньше ломался отсчёт.
            File.WriteAllText(
                Path.Combine(
                    stand.Paths.BackupsDir,
                    "dsh-backup-" + Now.AddMinutes(-1).ToString("yyyy-MM-dd-HHmmss") + "-full.zip"),
                "копия панели 1.x");

            var due = stand.Control.Tick(Now);

            Assert.False(due.Take, due.Reason);
            Assert.Contains("1 ч", due.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(stand);
        }
    }

    // --- копия на ходу и предупреждение -------------------------------------

    /// <summary>
    /// Сервер работает — копия по расписанию всё равно снимается (гасить его панель не станет:
    /// решение владельца 26.09.2026), но человек ПРЕДУПРЕЖДАЕТСЯ сообщением, а в отчёте остаётся
    /// оговорка «на ходу».
    /// </summary>
    [Fact]
    public void Копия_на_ходу_предупреждает_человека()
    {
        var stand = Build(serverRunning: true);

        try
        {
            stand.Control.Tick(Now);

            Assert.True(
                WaitFor(() => stand.Notices.Contains(NoticeKind.BackupLiveCopy)),
                "о копии на ходу человека не предупредили: " + string.Join(" | ", stand.Log));

            // И в отчёте движка оговорка на месте: копия снималась при работающем сервере.
            Assert.Contains(stand.Log, line => line.Contains(PanelStrings.BackupEngineLiveCopyNote, StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// Сервер не работает — предупреждать не о чем, и панель молчит. Половина пары к предыдущей
    /// проверке: без неё «предупредил» доказывало бы лишь то, что сообщение уходит всегда.
    /// </summary>
    [Fact]
    public void Без_работающего_сервера_предупреждения_нет()
    {
        var stand = Build(serverRunning: false);

        try
        {
            stand.Control.Tick(Now);

            Assert.True(WaitFor(() => Directory.Exists(stand.Paths.BackupsDir)
                                      && Directory.GetFiles(stand.Paths.BackupsDir, "dsh2-backup-*.zip").Length == 1));

            Assert.DoesNotContain(NoticeKind.BackupLiveCopy, stand.Notices);
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// Копия не снялась — человеку сообщение: у окна его нет (копию снимало расписание), и иначе
    /// он не узнал бы об этом вовсе.
    /// </summary>
    [Fact]
    public void Неудачная_автокопия_сообщается_человеку()
    {
        var stand = Build();

        try
        {
            Block(stand, "не-папка");

            stand.Control.Tick(Now);

            Assert.True(
                WaitFor(() => stand.Notices.Contains(NoticeKind.BackupFailed)),
                "о неудачной копии человека не предупредили: " + string.Join(" | ", stand.Log));

            Assert.Contains(stand.Log, line => line.StartsWith(Head(PanelStrings.BackupScheduleFailedFormat), StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// Попытка не удалась — следующая не раньше сторожа. Без этого панель долбила бы диск каждые
    /// пять минут (такт часов), и это была бы не забота, а издевательство.
    /// </summary>
    [Fact]
    public void После_неудачи_панель_не_долбит_диск()
    {
        var stand = Build();

        try
        {
            var blocked = Block(stand, "не-папка-2");

            Assert.True(stand.Control.Tick(Now).Take);
            Assert.True(WaitFor(() => stand.Log.Any(line =>
                line.StartsWith(Head(PanelStrings.BackupScheduleFailedFormat), StringComparison.Ordinal))));

            // Через две минуты — ещё рано: сторож при интервале 24 ч равен шести часам.
            var again = stand.Control.Tick(Now.AddMinutes(2));

            Assert.False(again.Take);
            Assert.Contains(Head(PanelStrings.BackupScheduleRetryFormat), again.Reason, StringComparison.Ordinal);

            // И файла в «папке»-файле не появилось: копия действительно не снялась.
            Assert.False(File.Exists(Path.Combine(blocked, BackupNaming.ArchiveName(Now, withEngine: true))));
        }
        finally
        {
            Cleanup(stand);
        }
    }

    // --- ротация после копии -------------------------------------------------

    /// <summary>
    /// Ротация идёт ЗА копией — и за ручной тоже: старая лишняя копия удаляется, свежая остаётся,
    /// а предохранительная копия перед накатом не трогается вовсе.
    /// </summary>
    [Fact]
    public void После_копии_ротация_убирает_лишнее_и_не_трогает_предохранительную()
    {
        var stand = Build(settings =>
        {
            settings.BackupKeepCount = 1;
            settings.BackupScheduleEnabled = false;
        });

        try
        {
            Directory.CreateDirectory(stand.Paths.BackupsDir);

            var old = new[]
            {
                BackupNaming.ArchiveName(Now.AddHours(-30), withEngine: true),
                BackupNaming.ArchiveName(Now.AddHours(-20), withEngine: true),
            };

            foreach (var name in old) File.WriteAllText(Path.Combine(stand.Paths.BackupsDir, name), "старьё");

            var safety = BackupNaming.SafetyPrefix + "2026-09-01-010101.zip";
            File.WriteAllText(Path.Combine(stand.Paths.BackupsDir, safety), "путь назад");

            var outcome = stand.Control.CreateCopy(
                Path.Combine(stand.Paths.BackupsDir, BackupNaming.ArchiveName(Now, withEngine: true)),
                serverRunning: false, shareable: false);

            Assert.True(outcome.Ok, outcome.Error);

            var left = Directory.GetFiles(stand.Paths.BackupsDir).Select(Path.GetFileName).ToArray();

            Assert.Contains(BackupNaming.ArchiveName(Now, withEngine: true), left);
            Assert.Contains(safety, left);
            Assert.DoesNotContain(old[0], left);
            Assert.DoesNotContain(old[1], left);

            // И человеку сказано, что именно убрано, — не только журналу.
            Assert.Contains("2", outcome.Rotation, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>
    /// Копия НЕ удалась — ротация не запускается вовсе: уборка не имеет права забрать старую
    /// копию ради новой, которой не получилось.
    /// </summary>
    [Fact]
    public void После_неудачной_копии_ротация_не_запускается()
    {
        var stand = Build(settings => settings.BackupKeepCount = 1);

        try
        {
            Directory.CreateDirectory(stand.Paths.BackupsDir);

            var old = BackupNaming.ArchiveName(Now.AddHours(-30), withEngine: true);
            File.WriteAllText(Path.Combine(stand.Paths.BackupsDir, old), "единственная копия");

            var blocked = Block(stand, "не-папка-3");
            var outcome = stand.Control.CreateCopy(Path.Combine(blocked, "копия.zip"), serverRunning: false, shareable: false);

            Assert.False(outcome.Ok);
            Assert.Empty(outcome.Rotation);
            Assert.True(File.Exists(Path.Combine(stand.Paths.BackupsDir, old)), "ротация забрала старую копию после неудачи");
        }
        finally
        {
            Cleanup(stand);
        }
    }

    // --- сериализация --------------------------------------------------------

    /// <summary>
    /// Пока идёт копия, вторая НЕ начинается — ни по кнопке, ни по расписанию: копия и накат пишут
    /// в одни и те же файлы. Проверка идёт по-настоящему: второй вызов делается ИЗ СЕРЕДИНЫ первой
    /// копии (её дверь прогресса) — это единственный способ поймать занятую дверь надёжно,
    /// без гонки на «успеть, пока копируется».
    /// </summary>
    [Fact]
    public void Вторая_работа_не_начинается_пока_идёт_первая()
    {
        var stand = Build(settings => settings.BackupScheduleEnabled = false);

        try
        {
            CopyOutcome? second = null;
            BackupDue? scheduled = null;
            var busySeen = false;

            var first = stand.Control.CreateCopy(
                Path.Combine(stand.Paths.BackupsDir, BackupNaming.ArchiveName(Now, withEngine: true)),
                serverRunning: false, shareable: false,
                progress: _ =>
                {
                    if (second is not null) return;

                    busySeen = stand.Control.Busy;
                    second = stand.Control.CreateCopy(
                        Path.Combine(stand.Paths.BackupsDir, "вторая.zip"), serverRunning: false, shareable: false);
                    scheduled = stand.Control.Tick(Now.AddDays(30));
                });

            Assert.True(first.Ok, first.Error);
            Assert.True(busySeen, "дверь работы с копиями не была занята во время копии");

            Assert.NotNull(second);
            Assert.False(second!.Ok);
            Assert.Equal(PanelStrings.BackupBusy, second.Error);

            Assert.NotNull(scheduled);
            Assert.False(scheduled!.Take);
            Assert.Equal(PanelStrings.BackupBusy, scheduled.Reason);
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>Дверь освобождается после работы: следующая копия проходит.</summary>
    [Fact]
    public void После_работы_дверь_свободна()
    {
        var stand = Build(settings => settings.BackupScheduleEnabled = false);

        try
        {
            Assert.True(stand.Control.CreateCopy(
                Path.Combine(stand.Paths.BackupsDir, BackupNaming.ArchiveName(Now, withEngine: true)), false, shareable: false).Ok);

            Assert.False(stand.Control.Busy);

            var second = stand.Control.CreateCopy(
                Path.Combine(stand.Paths.BackupsDir, BackupNaming.ArchiveName(Now.AddMinutes(1), withEngine: true)), false, shareable: false);

            Assert.True(second.Ok, second.Error);
        }
        finally
        {
            Cleanup(stand);
        }
    }

    // --- замер размера -------------------------------------------------------

    /// <summary>
    /// Оценка размера у режимов РАЗНАЯ и берётся у того же плана, что копия: обещанное в окне число
    /// обязано совпадать с тем, что действительно копируется. В прогоне проверки оценки нет вовсе —
    /// обход каталогов там не делается (красная линия 4).
    /// </summary>
    [Fact]
    public void Оценка_размера_различается_по_режимам()
    {
        var stand = Build(settings => settings.BackupScope = BackupScopeDecisions.Auto);

        try
        {
            var auto = stand.Control.Estimate(BackupScope.Auto);
            var full = stand.Control.Estimate(BackupScope.Full);

            Assert.True(auto.Known);
            Assert.True(full.Known);
            Assert.True(full.Bytes > auto.Bytes, $"полный режим обязан быть больше: {full.Bytes} против {auto.Bytes}");
        }
        finally
        {
            Cleanup(stand);
        }
    }

    [Fact]
    public void В_прогоне_проверки_оценки_нет()
    {
        var stand = Build(allowed: false);

        try
        {
            var estimate = stand.Control.Estimate(BackupScope.Full);

            Assert.False(estimate.Known);
            Assert.Equal(0, estimate.Bytes);
        }
        finally
        {
            Cleanup(stand);
        }
    }

    // --- режим объёма и лишние имена в настройках ---------------------------

    /// <summary>
    /// Режим объёма — НАСТРОЙКА, и меняется он только через владельца настроек: у окна копий нет
    /// своего значения, иначе два окна однажды разошлись бы.
    /// </summary>
    [Fact]
    public void Режим_и_лишние_имена_без_владельца_настроек_не_меняются()
    {
        var stand = Build();

        try
        {
            // Владельца настроек у стенда нет (прогон проверки, проверки без экрана).
            Assert.False(stand.Control.SetScope(BackupScope.Full));
            Assert.False(stand.Control.SetExtraExclusions(new[] { "vendor" }));

            Assert.Equal(BackupScope.Auto, stand.Control.Scope);
            Assert.Empty(stand.Control.ExtraExclusions);
        }
        finally
        {
            Cleanup(stand);
        }
    }

    /// <summary>«Занять» папку копий файлом: туда нельзя положить архив, и копия отказывает.</summary>
    private static string Block(Stand stand, string name)
    {
        var blocked = Path.Combine(stand.Paths.Root, name);
        File.WriteAllText(blocked, "это файл, а не папка");

        stand.Settings.BackupFolder = blocked;

        return blocked;
    }
}
