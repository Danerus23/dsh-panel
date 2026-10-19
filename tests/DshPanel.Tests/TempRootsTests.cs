using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// УБОРКА БРОШЕННЫХ КАТАЛОГОВ ПРОГОНОВ (t2 цикла 2.1.2, D2): удаляется ТОЛЬКО тот каталог `run-*`,
/// чей процесс уже не жив И который старше часа; свой и живой не трогаются никогда.
///
/// **От какого случая.** `%TEMP%\dsh-panel-tests` копится по каталогу на прогон — замер на входе
/// задачи: **66 каталогов / 28,54 МБ**, ~0,72 МБ на прогон (кадры). Убирать надо, но ошибиться тут
/// дороже, чем не убрать: «удалять всё» — это ровно тот дефект, от которого заведён <see cref="TempRoots"/>
/// (цикл 2.1.1: один прогон сносил каталог соседнего, и это видели как флак). Поэтому проверки ниже
/// написаны так, чтобы мутация «удалять всё» была КРАСНОЙ: имя, номер процесса и возраст проверяются
/// по отдельности, каждый на своём каталоге.
///
/// ⚠️ **Порог возраста уборке ПЕРЕДАЁТСЯ** (`minAge` у <see cref="TempRoots.SweepAbandoned"/>) —
/// и проверки пользуются этим намеренно: где проверяется «чей это прогон», порог ноль (иначе ждать
/// час), а где проверяется сам порог — порог боевой, <see cref="TempRoots.AbandonedMinAge"/>.
///
/// **Стенд — свой каталог** `%TEMP%\dsh-panel-tests\sweep-tests-&lt;guid&gt;`: имя не `run-*`, поэтому
/// уборка соседнего прогона (и наша собственная на старте) его не видит; сам он снимается в конце.
/// Кадров стенд не снимает, окна не показывает — параллельность класса ничему не мешает.
/// </summary>
public sealed class TempRootsTests
{
    /// <summary>Возраст «брошенного» — много больше порога уборки.</summary>
    private static readonly TimeSpan Old = TimeSpan.FromHours(2);

    /// <summary>Возраст «молодого» — много меньше порога: такой каталог обязан остаться.</summary>
    private static readonly TimeSpan Young = TimeSpan.FromMinutes(5);

    /// <summary>Возраст «только что записанного» — при пороге «ноль» возраст обязан быть положительным.</summary>
    private static readonly TimeSpan JustWritten = TimeSpan.FromSeconds(1);

    [Fact]
    public void Уборка_удаляет_только_брошенный_каталог_мёртвого_прогона()
    {
        string root = NewStand();
        try
        {
            int deadPid = FinishedProcessPid();
            using Process live = StartLiveProcess();
            try
            {
                string dead = CreateRunDir(root, deadPid, Old);
                string liveDir = CreateRunDir(root, live.Id, JustWritten);

                // СВОЙ — нарочно СТАРЫЙ: возраст его не защищает (порог здесь ноль), защищает только
                // имя. Иначе «свой не трогаем» проверялось бы возрастным правилом, а не своим.
                string own = CreateRunDir(root, Environment.ProcessId, Old);

                // Все три каталога — НА МЕСТЕ: разница только в том, чей это прогон.
                Assert.True(Directory.Exists(dead));
                Assert.True(Directory.Exists(liveDir));
                Assert.True(Directory.Exists(own));

                IReadOnlyList<string> removed = TempRoots.SweepAbandoned(root, DateTime.UtcNow, TimeSpan.Zero);

                Assert.Equal(new[] { "run-" + deadPid }, removed);
                Assert.False(Directory.Exists(dead));   // брошенный — убран
                Assert.True(Directory.Exists(liveDir)); // ЖИВОЙ — не тронут
                Assert.True(Directory.Exists(own));     // СВОЙ — не тронут никогда
            }
            finally
            {
                Stop(live);
            }
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }

    [Fact]
    public void Уборка_удаляет_каталог_номера_переиспользованного_чужим_процессом()
    {
        // Живой номер сам по себе — не защита: замер на первом прогоне показал 13 каталогов из 66,
        // которые не убрались именно потому, что их номера заняли ЖИВЫЕ чужие процессы, стартовавшие
        // много позже. Процесс, стартовавший ПОСЛЕ последней записи в каталог, его создать не мог.
        string root = NewStand();
        try
        {
            using Process live = StartLiveProcess();
            try
            {
                string recycled = CreateRunDir(root, live.Id, Old);

                IReadOnlyList<string> removed = TempRoots.SweepAbandoned(root, DateTime.UtcNow, TimeSpan.Zero);

                Assert.Equal(new[] { "run-" + live.Id }, removed);
                Assert.False(Directory.Exists(recycled));
            }
            finally
            {
                Stop(live);
            }
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }

    [Fact]
    public void Уборка_не_трогает_молодой_каталог_мёртвого_прогона()
    {
        string root = NewStand();
        try
        {
            int deadPid = FinishedProcessPid();
            string young = CreateRunDir(root, deadPid, Young);

            IReadOnlyList<string> removed = TempRoots.SweepAbandoned(root, DateTime.UtcNow, TempRoots.AbandonedMinAge);

            Assert.Empty(removed);
            Assert.True(Directory.Exists(young));
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }

    [Fact]
    public void Уборка_не_трогает_имена_не_похожие_на_прогон()
    {
        string root = NewStand();
        try
        {
            // Ни одно из имён не заводит оснастка: `run-` без номера, `run-abc`, `run--5`, `run- 12`
            // и каталог семейства. Старые — и всё равно обязаны остаться: чужое не угадываем.
            var names = new[] { "run-", "run-abc", "run--5", "run- 12", "sweep-tests-stand", "frames" };
            foreach (string name in names)
            {
                string dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - Old);
            }

            IReadOnlyList<string> removed = TempRoots.SweepAbandoned(root, DateTime.UtcNow, TimeSpan.Zero);

            Assert.Empty(removed);
            foreach (string name in names)
            {
                Assert.True(Directory.Exists(Path.Combine(root, name)), "не наш формат имени — остаётся: " + name);
            }
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }

    [Fact]
    public void Уборка_считает_брошенным_каталог_с_нулевым_номером()
    {
        // Номер 0 в Windows — процесс простоя, а не прогон проверок: `run-0` остаётся только от сбоя,
        // и держать его вечно (GetProcessById(0) отвечает «жив») значит убирать брошенное наполовину.
        Assert.True(TempRoots.TryReadRunPid("run-0", out int pid));
        Assert.Equal(0, pid);

        string root = NewStand();
        try
        {
            string zero = CreateRunDir(root, 0, Old);

            IReadOnlyList<string> removed = TempRoots.SweepAbandoned(root, DateTime.UtcNow, TimeSpan.Zero);

            Assert.Equal(new[] { "run-0" }, removed);
            Assert.False(Directory.Exists(zero));
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }

    [Fact]
    public void Уборка_на_пропавшем_родителе_молчит_а_не_падает()
    {
        // «Убирать нечего» — это ответ, а не авария: иначе прогон на машине без %TEMP%\dsh-panel-tests
        // упал бы на пустом месте.
        string missing = Path.Combine(TempRoots.Root, "no-such-root-" + Guid.NewGuid().ToString("N"));

        IReadOnlyList<string> removed = TempRoots.SweepAbandoned(missing, DateTime.UtcNow, TempRoots.AbandonedMinAge);

        Assert.Empty(removed);
    }

    [Fact]
    public void Свой_корень_назван_номером_процесса_и_повторный_старт_его_не_снимает()
    {
        Assert.Equal(Path.Combine(TempRoots.Root, "run-" + Environment.ProcessId), TempRoots.Run);
        Assert.StartsWith(TempRoots.Run, TempRoots.Frames);

        Directory.CreateDirectory(TempRoots.Run);
        string marker = Path.Combine(TempRoots.Run, "own-run-marker.txt");
        File.WriteAllText(marker, "свой корень");

        TempRoots.Initialize(); // повторный старт: не второй проход по %TEMP% и не снятие своего

        Assert.True(File.Exists(marker));
    }

    /// <summary>Стенд: свой каталог внутри общего родителя, имя НЕ `run-*`.</summary>
    private static string NewStand()
    {
        string root = Path.Combine(TempRoots.Root, "sweep-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Каталог прогона с содержимым (как настоящий: `frames` и каталог семейства) и названным возрастом.</summary>
    private static string CreateRunDir(string root, int pid, TimeSpan age)
    {
        string dir = Path.Combine(root, "run-" + pid);
        Directory.CreateDirectory(Path.Combine(dir, "frames"));
        File.WriteAllText(Path.Combine(dir, "frames", "marker.txt"), "кадр");
        Directory.CreateDirectory(Path.Combine(dir, "look-tests", Guid.NewGuid().ToString("N")));

        // Возраст ставится ПОСЛЕ содержимого: запись в каталог обновляет его время.
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - age);
        return dir;
    }

    /// <summary>Номер процесса, который УЖЕ завершился (и его номер не успел достаться другому).</summary>
    private static int FinishedProcessPid()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using Process process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            process.WaitForExit();
            if (!TempRoots.IsProcessAlive(process.Id)) return process.Id;
        }

        throw new InvalidOperationException("не удалось получить номер завершённого процесса");
    }

    /// <summary>ЖИВОЙ процесс, чужой этому прогону: им проверяется условие «живой — не трогаем».</summary>
    private static Process StartLiveProcess()
    {
        Process child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        Assert.False(child.HasExited, "живой процесс обязан быть жив, иначе проверка ничего не проверяет");
        return child;
    }

    /// <summary>Убрать СВОЙ дочерний процесс (чужой не трогаем никогда).</summary>
    private static void Stop(Process child)
    {
        try
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            child.WaitForExit(5000);
        }
        catch
        {
            // Процесс уже завершился сам — это не беда проверки.
        }
    }
}
