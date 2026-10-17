using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// УСТАНОВКА СВОЕГО ДВИЖКА ПО ЗАМКУ (этап E3.1, шаг 2 владельца).
///
/// Установка — единственное место в панели, которое **ходит в сеть и запускает чужую программу**
/// (npm), поэтому здесь проверяется не «получилось ли», а ГРАНИЦЫ: что панель кладёт рядом с npm
/// ровно то, что лежит в замке; что при отказе npm, при старом Node и при разошедшемся дереве
/// она говорит словами и НЕ считает установку удавшейся; и что нужное этой машине считается
/// правилом npm, а не «чего не хватает — то пропажа» (замер 10.10.2026: 69 законных исключений).
///
/// ⚠️ Настоящий npm и настоящий диск здесь подменены дверями (<see cref="EngineInstallDoors"/>):
/// «прогоним живьём на машине владельца» — не проверка, а расход. Живой прогон — в ВМ, отдельно.
/// </summary>
public class EngineInstallerTests
{
    private const string EngineDir = @"C:\Проба\панель\engine";

    /// <summary>Версия Node разбирается, а порог — 20 (решение владельца 10.10.2026).</summary>
    [Theory]
    [InlineData("v26.9.0", 26, true)]
    [InlineData("26.9.0", 26, true)]
    [InlineData("v20.0.0", 20, true)]
    [InlineData("v19.9.9", 19, false)]
    [InlineData("v18.20.4", 18, false)]
    [InlineData("мусор", 0, false)]
    [InlineData("", 0, false)]
    public void Версия_Node_разбирается_и_сравнивается_с_порогом(string text, int major, bool suits)
    {
        Assert.Equal(major, EngineNode.Major(text));
        Assert.Equal(suits, EngineNode.Suits(text));
        Assert.Equal(20, EngineNode.MinimumMajor);
    }

    /// <summary>
    /// УСТАНОВОЧНЫЕ СКРИПТЫ НЕ ЗАПУСКАЮТСЯ, И «NODE» ВИДЕН ДОЧЕРНИМ ПРОЦЕССАМ npm.
    ///
    /// Оба — из ЖИВОГО ПРОГОНА В ВМ 10.10.2026, где установка упала: `koffi` запустил свой
    /// установочный скрипт, тот позвал `node`, а `node` не был виден в PATH (панель поставила СВОЙ
    /// Node и зовёт его полным путём). На машине владельца те же скрипты блокировал npm 12 — потому
    /// проба и была зелёной. Здесь проверяется и то, и другое: доводы `npm ci` и PATH.
    /// </summary>
    [Fact]
    public void Скрипты_не_запускаются_а_свой_Node_виден_npm()
    {
        Assert.Contains("ci", EngineNpm.Arguments, StringComparison.Ordinal);
        Assert.Contains("--ignore-scripts", EngineNpm.Arguments, StringComparison.Ordinal);
        Assert.Contains("--no-audit", EngineNpm.Arguments, StringComparison.Ordinal);

        // Хвост длиннее трёх строк: трёх не хватило, чтобы увидеть причину беды в ВМ.
        Assert.True(EngineNpm.TailLines >= 6, "хвост вывода npm снова короче причины беды");

        // Папка своего Node идёт ПЕРВОЙ — иначе дочерние процессы найдут чужой node или никакой.
        var with = EngineNpm.PathWith(@"C:\панель\node\22.11.0\node.exe", @"C:\Windows;C:\Program Files\nodejs");

        Assert.StartsWith(@"C:\панель\node\22.11.0", with, StringComparison.Ordinal);
        Assert.EndsWith(@"C:\Windows;C:\Program Files\nodejs", with, StringComparison.Ordinal);

        Assert.Equal(@"C:\панель\node\22.11.0", EngineNpm.PathWith(@"C:\панель\node\22.11.0\node.exe", null));
        Assert.Equal(@"C:\Windows", EngineNpm.PathWith(null, @"C:\Windows"));
        Assert.Equal(string.Empty, EngineNpm.PathWith(null, null));
    }

    /// <summary>
    /// ХВОСТ ВЫВОДА npm — ПОСЛЕДНИЕ НЕПУСТЫЕ СТРОКИ. Человеку нужна последняя сказанная строка,
    /// а не весь лог установки: «чем кончилось» и «что чинить» живут в конце.
    /// </summary>
    [Fact]
    public void Хвост_вывода_npm_берётся_с_конца()
    {
        var tail = EngineNpm.Tail("первая\n\n  вторая  \nтретья\nчетвёртая\n");

        Assert.Equal("первая; вторая; третья; четвёртая", tail);
        Assert.Equal(string.Empty, EngineNpm.Tail(null));
        Assert.Equal(string.Empty, EngineNpm.Tail("  \n \n"));
    }

    /// <summary>
    /// npm ИЩЕТСЯ РЯДОМ С NODE: панель знает путь к Node, а npm лежит с ним в одной папке.
    /// Спрашивать PATH первым значило бы поставить движок не тем npm, которым пользуется человек.
    /// </summary>
    [Fact]
    public void npm_ищется_рядом_с_Node()
    {
        var folder = NewFolder();
        var node = Path.Combine(folder, "node.exe");
        var npm = Path.Combine(folder, "npm.cmd");

        try
        {
            File.WriteAllText(node, string.Empty);
            File.WriteAllText(npm, string.Empty);

            Assert.Equal(npm, EngineNpm.PathNear(node));

            // `null` — «поищи сам»: ответом будет npm из PATH (или пусто, если его там нет),
            // но НЕ падение. Пустой путь — это «не нашли», и его проверяет установщик.
            var fromPath = EngineNpm.PathNear(null);

            Assert.True(
                fromPath.Length == 0 || Path.GetFileName(fromPath).StartsWith("npm", StringComparison.OrdinalIgnoreCase),
                "из PATH вернулось нечто не похожее на npm: " + fromPath);
        }
        finally
        {
            Remove(folder);
        }
    }

    /// <summary>
    /// ЧТО ЛЕЖИТ В ДЕРЕВЕ — ЧИТАЕТСЯ ПРАВИЛОМ «пакет — это каталог с package.json»: скрытое
    /// (`.bin`) и папки областей (`@scope`) пакетами не считаются, а вложенные `node_modules`
    /// читаются по СВОИМ путям. Без этого правила замер 10.10.2026 показал 11 «лишних пакетов»
    /// у честной установки.
    /// </summary>
    [Fact]
    public void Дерево_читается_по_путям_и_без_служебных_каталогов()
    {
        var root = NewFolder();

        try
        {
            Package(root, "node_modules/a", "1.0.0");
            Package(root, "node_modules/@scope/b", "2.0.0");
            Package(root, "node_modules/a/node_modules/c", "3.0.0");

            // Служебные каталоги пакетами не считаются ДАЖЕ ЕСЛИ в них лежит package.json:
            // правило — «скрытый каталог не пакет», а не «повезло, что файла нет».
            Directory.CreateDirectory(Path.Combine(root, "node_modules", ".bin"));
            File.WriteAllText(
                Path.Combine(root, "node_modules", ".bin", "package.json"), "{\"version\":\"9.9.9\"}");
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "@scope", "не-пакет"));

            var read = EngineTreeDisk.From(root);

            Assert.Equal(
                new[] { "node_modules/@scope/b", "node_modules/a", "node_modules/a/node_modules/c" },
                read.Paths.OrderBy(path => path, StringComparer.Ordinal).ToArray());

            Assert.Equal("1.0.0", read.VersionOf("node_modules/a"));
            Assert.Equal("3.0.0", read.VersionOf("node_modules/a/node_modules/c"));
            Assert.Null(read.VersionOf("node_modules/.bin"));
            Assert.Null(read.VersionOf("node_modules/@scope/не-пакет"));

            // Папки нет — это пустое дерево, а не исключение: сверка скажет словами, чего не хватает.
            Assert.Empty(EngineTreeDisk.From(Path.Combine(root, "нет-такой")).Paths);
        }
        finally
        {
            Remove(root);
        }
    }

    /// <summary>
    /// УСТАНОВКА ПРОШЛА: панель кладёт рядом с npm ЗАМОК КАК ЕСТЬ и `package.json` из его корневой
    /// записи, зовёт `npm ci`, получает 0 и сверяет дерево. Проверяется и то, что записанный
    /// `package-lock.json` совпадает с замком файла: если панель что-то выбросит, npm соберёт
    /// ДРУГОЕ дерево (замер: без `dependencies` — 1 пакет вместо 588).
    /// </summary>
    [Fact]
    public void Установка_кладёт_замок_и_зовёт_npm()
    {
        var root = NewFolder();
        var lockFile = Lock();

        try
        {
            var asked = string.Empty;
            var doors = new EngineInstallDoors(
                _ => "v26.9.0",
                (npm, dir, _, _) =>
                {
                    asked = npm + "|" + dir;
                    return new EngineNpmRun(0, "added 588 packages", false);
                },
                _ => Tree(lockFile));

            var result = EngineInstaller.Install(lockFile, Hints(), root, @"C:\Node\node.exe", @"C:\Node\npm.cmd", doors, null);

            Assert.True(result.Ok, result.Detail);
            Assert.Equal(EngineInstallOutcome.Installed, result.Outcome);
            Assert.Equal("0.1.5-rc.2", result.Version);
            Assert.Equal(Path.Combine(root, "0.1.5-rc.2"), result.InstallDir);
            Assert.Equal(@"C:\Node\npm.cmd|" + result.InstallDir, asked);

            // Замок записан КАК ЕСТЬ, а `package.json` — из его корневой записи.
            var written = File.ReadAllText(Path.Combine(result.InstallDir, "package-lock.json"));

            Assert.Equal(lockFile.NpmLockJson, written);
            Assert.Equal(lockFile.PackageJson, File.ReadAllText(Path.Combine(result.InstallDir, "package.json")));
            Assert.Equal("0.1.5-rc.2", JsonDocument.Parse(written)
                .RootElement.GetProperty("packages").GetProperty(string.Empty)
                .GetProperty("dependencies").GetProperty("@deepseek-ai/dsh").GetString());

            // И числа сверки сказаны словами, а не «ок».
            Assert.DoesNotContain("{", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Remove(root);
        }
    }

    /// <summary>
    /// НЕУДАЧА НЕ СНОСИТ УЖЕ СТОЯВШУЮ ПАПКУ. Папка версии могла стоять до этой попытки (человек
    /// переставляет ту же версию) — и там лежит РАБОЧАЯ установка. «Откат», снёсший её, сделал бы
    /// ровно то, от чего откат и заведён, поэтому убирается только созданное ЭТОЙ попыткой.
    /// </summary>
    [Fact]
    public void Отказ_не_сносит_уже_стоявшую_папку()
    {
        var root = NewFolder();
        var lockFile = Lock();

        try
        {
            var installDir = Path.Combine(root, "0.1.5-rc.2");
            Directory.CreateDirectory(installDir);

            var worker = Path.Combine(installDir, "рабочая-установка.txt");
            File.WriteAllText(worker, "здесь уже стоял движок");

            var failed = EngineInstaller.Install(
                lockFile, Hints(), root, @"C:\Node\node.exe", @"C:\Node\npm.cmd",
                new EngineInstallDoors(_ => "v26.9.0",
                    (_, _, _, _) => new EngineNpmRun(1, "npm error code EINTEGRITY", false),
                    _ => Tree(lockFile)),
                null);

            Assert.False(failed.Ok);
            Assert.False(failed.RolledBack, "откат снёс папку, которая стояла до нас");
            Assert.True(Directory.Exists(installDir), "папка рабочей установки исчезла");
            Assert.True(File.Exists(worker), "рабочая установка внутри папки пропала");

            // Но предупреждение про сессии сказано и здесь: попытка не удалась, панель осталась
            // на прежней версии — человеку надо об этом знать.
            Assert.Contains("журнал новее", failed.Warning, StringComparison.Ordinal);
        }
        finally
        {
            Remove(root);
        }
    }

    /// <summary>
    /// <summary>
    /// СИСТЕМНЫЙ NODE НИЖЕ ПОРОГА ЗАМКА — ОТКАЗ, И В СЛОВАХ НАЗВАН ПОРОГ (решение владельца
    /// `7265d9d`): панель решает по данным замка, а не по константе 20. Живой случай 10.10.2026:
    /// у человека Node `22.11.0`, а замок объявляет `>= 22.19.0`.
    /// </summary>
    [Fact]
    public void Системный_Node_ниже_порога_замка_отвергается_словами()
    {
        var root = NewFolder();
        var lockFile = Lock();

        try
        {
            // Замок подставной: порог в нём объявлен выше, чем даёт системный Node.
            var strict = EngineTreeLock.Parse(
                "{\"format\":2,\"engine\":{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.1.5-rc.2\"},\"packages\":1," +
                "\"lock\":{\"lockfileVersion\":3,\"packages\":{" +
                "\"\":{\"dependencies\":{\"@deepseek-ai/dsh\":\"0.1.5-rc.2\"}}," +
                "\"node_modules/@deepseek-ai/dsh\":{\"version\":\"0.1.5-rc.2\",\"integrity\":\"sha512-A\"," +
                "\"resolved\":\"https://registry.npmjs.org/@deepseek-ai/dsh/-/dsh-0.1.5-rc.2.tgz\"," +
                "\"engines\":{\"node\":\">=22.19.0\"}}}}}" );

            Assert.Equal("22.19.0", EngineNode.RequiredFrom(strict));

            var result = EngineInstaller.Install(
                strict, Hints(), root, @"C:\Node\node.exe", @"C:\Node\npm.cmd",
                new EngineInstallDoors(_ => "v22.11.0", (_, _, _, _) => new EngineNpmRun(0, string.Empty, false), _ => Tree(lockFile)),
                null);

            Assert.Equal(EngineInstallOutcome.Refused, result.Outcome);
            Assert.Equal(EngineInstallRefusals.NodeOld, result.RefusalKey);

            // Порог назван ЧИСЛОМ из замка, а не глухим «нужен 20»: иначе человек не понял бы, чем
            // его Node не угодил.
            Assert.Contains("22.19.0", EngineInstallPhrases.NodeOld("v22.11.0", "22.19.0"), StringComparison.Ordinal);
        }
        finally
        {
            Remove(root);
        }
    }
    /// ОТКАТ ПРИ СБОЕ И УБОРКА ПРЕЖНЕЙ ВЕРСИИ (шаг 2 плана владельца).
    ///
    /// (а) npm отказал — **папка версии убрана** (половина дерева хуже, чем её отсутствие),
    ///     а предупреждение про сессии сказано словами: *«вернёте движок — потеряете работу,
    ///     сделанную на новой версии: её журнал новее, старый его не читает»*;
    /// (б) удачная установка пишет **запись «что стоит сейчас»** — из неё берутся откат и опись;
    /// (в) версии сверх текущей и прежней **убираются**, а папка, которую панель не ставила, —
    ///     не трогается.
    /// </summary>
    [Fact]
    public void Отказ_откатывается_а_лишние_версии_убираются()
    {
        var root = NewFolder();
        var lockFile = Lock();

        try
        {
            var stateDir = Path.Combine(root, "state");
            var recordPath = Path.Combine(stateDir, "engine.json");
            var engineDir = Path.Combine(root, "engine");

            // Прежняя установка: панель уже ставила 0.9.0 и 1.0.0, сейчас стоит 1.0.0; рядом лежит
            // папка «чужой-движок», которую панель не заводила.
            new EngineInstallRecord(
                EngineVersion: "1.0.0",
                EnginePath: Path.Combine(engineDir, "1.0.0"),
                NodeVersion: "v26.9.0",
                NodePath: @"C:\Program Files\nodejs\node.exe",
                NodeOurs: false,
                Installed: new[] { "0.9.0", "1.0.0" }).Write(recordPath, out _);
            Directory.CreateDirectory(Path.Combine(engineDir, "1.0.0"));
            Directory.CreateDirectory(Path.Combine(engineDir, "0.9.0"));
            Directory.CreateDirectory(Path.Combine(engineDir, "чужой-движок"));

            // (а) npm отказал — версия не поставлена, папка убрана, предупреждение сказано.
            var failed = EngineInstaller.Install(
                lockFile, Hints(), engineDir, @"C:\Node\node.exe", @"C:\Node\npm.cmd",
                new EngineInstallDoors(_ => "v26.9.0",
                    (_, _, _, _) => new EngineNpmRun(1, "npm error code EINTEGRITY", false),
                    _ => Tree(lockFile)),
                null, null, recordPath);

            Assert.False(failed.Ok);
            Assert.Equal(EngineInstallOutcome.NpmFailed, failed.Outcome);
            Assert.True(failed.RolledBack, "папка неудавшейся версии осталась на диске");
            Assert.False(Directory.Exists(Path.Combine(engineDir, "0.1.5-rc.2")));
            Assert.Contains("журнал новее", failed.Warning, StringComparison.Ordinal);

            // Прежняя запись НЕ тронута: панель осталась на прежней версии.
            Assert.Equal("1.0.0", EngineInstallRecord.Read(recordPath).EngineVersion);

            // (б) удача: запись сменилась на новую версию, движок в описи назван.
            var doors = new EngineInstallDoors(_ => "v26.9.0", (_, _, _, _) => new EngineNpmRun(0, string.Empty, false),
                _ => Tree(lockFile));

            var ok = EngineInstaller.Install(lockFile, Hints(), engineDir, @"C:\Node\node.exe", @"C:\Node\npm.cmd",
                doors, null, null, recordPath);

            Assert.True(ok.Ok, ok.Detail);

            var record = EngineInstallRecord.Read(recordPath);

            Assert.Equal("0.1.5-rc.2", record.EngineVersion);
            Assert.Equal(Path.Combine(engineDir, "0.1.5-rc.2"), record.EnginePath);
            Assert.Equal("v26.9.0", record.NodeVersion);
            Assert.False(record.NodeOurs, "системный Node записан как наш — он попал бы в опись на удаление");

            // (в) убрана ровно НЕнужная версия: 0.9.0 (её панель ставила), а 1.0.0 — прежняя,
            // её держим для отката; «чужой-движок» не наш и не трогается.
            Assert.Equal(1, ok.RemovedCount);
            Assert.Contains("0.9.0", ok.Removed!, StringComparer.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(engineDir, "0.9.0")));
            Assert.True(Directory.Exists(Path.Combine(engineDir, "1.0.0")), "прежняя версия убрана — откат сломан");
            Assert.True(Directory.Exists(Path.Combine(engineDir, "чужой-движок")), "уборка тронула чужую папку");
        }
        finally
        {
            Remove(root);
        }
    }

    /// <summary>
    /// КАЖДЫЙ ОТКАЗ НАЗВАН И НЕ ПРОХОДИТ ЗА УДАЧУ. Четыре случая: npm отказал, срок вышел,
    /// дерево разошлось, Node старый. Ни в одном панель не говорит «поставлено» — и в каждом
    /// называет причину словами (проверяется, что строка не осталась шаблоном с `{0}`).
    /// </summary>
    [Fact]
    public void Каждый_отказ_назван_и_не_проходит_за_удачу()
    {
        var root = NewFolder();
        var lockFile = Lock();

        try
        {
            var cases = new (string Name, EngineInstallOutcome Outcome, string Node, EngineNpmRun Run, EngineTreeDisk.Read Tree)[]
            {
                ("npm отказал", EngineInstallOutcome.NpmFailed, "v26.9.0",
                    new EngineNpmRun(1, "npm error code EINTEGRITY", false), Tree(lockFile)),
                ("срок вышел", EngineInstallOutcome.TooLong, "v26.9.0",
                    new EngineNpmRun(-1, "ставится", true), Tree(lockFile)),
                ("дерево разошлось", EngineInstallOutcome.TreeWrong, "v26.9.0",
                    new EngineNpmRun(0, string.Empty, false),
                    new EngineTreeDisk.Read(new[] { "node_modules/a" }, _ => "1.0.0")),
                ("Node старый", EngineInstallOutcome.Refused, "v19.9.9",
                    new EngineNpmRun(0, string.Empty, false), Tree(lockFile)),
            };

            foreach (var item in cases)
            {
                var doors = new EngineInstallDoors(_ => item.Node, (_, _, _, _) => item.Run, _ => item.Tree);

                var result = EngineInstaller.Install(lockFile, Hints(), root, @"C:\Node\node.exe", @"C:\Node\npm.cmd", doors, null);

                Assert.False(result.Ok, item.Name);
                Assert.Equal(item.Outcome, result.Outcome);
                Assert.NotEqual(string.Empty, result.RefusalKey);
                Assert.DoesNotContain("{", result.Detail, StringComparison.Ordinal);
            }

            // И у разошедшегося дерева причина названа ПОИМЁННО, а не «что-то не так».
            var wrong = EngineInstaller.Install(
                lockFile,
                Hints(),
                root,
                @"C:\Node\node.exe",
                @"C:\Node\npm.cmd",
                new EngineInstallDoors(_ => "v26.9.0", (_, _, _, _) => new EngineNpmRun(0, string.Empty, false),
                    _ => new EngineTreeDisk.Read(new[] { "node_modules/a" }, _ => "9.9.9")),
                null);

            // И у разошедшегося дерева причина названа: расхождения перечислены в отчёте,
            // а слова по каждому виду берутся из словарей (их проверяет `EngineInstallTests`
            // через `Loc.TIn` — здесь язык не переключается: он общий на процесс).
            Assert.Equal(EngineInstallRefusals.TreeWrong, wrong.RefusalKey);
            Assert.NotNull(wrong.Tree);
            Assert.Equal(2, wrong.Tree!.Problems.Count);
            Assert.NotEqual(string.Empty, wrong.Detail);
            Assert.DoesNotContain("{", wrong.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Remove(root);
        }
    }

    /// <summary>
    /// БЕЗ NODE И БЕЗ npm СТАВИТЬ НЕЧЕМ — и панель об этом говорит, а не заводит папку «на удачу».
    /// Отдельно: **пустой путь к npm** значит «не нашли» (а `null` — «поищи сам»), поэтому отказ
    /// проверяется без оглядки на то, что установлено на машине, где гоняют проверки.
    /// </summary>
    [Fact]
    public void Без_Node_и_без_npm_ставить_нечем()
    {
        var root = NewFolder();
        var lockFile = Lock();

        try
        {
            var doors = new EngineInstallDoors(_ => string.Empty, (_, _, _, _) => new EngineNpmRun(0, string.Empty, false), _ => Tree(lockFile));

            var noNode = EngineInstaller.Install(lockFile, Hints(), root, @"C:\Node\node.exe", @"C:\Node\npm.cmd", doors, null);

            Assert.Equal(EngineInstallOutcome.Refused, noNode.Outcome);
            Assert.Equal(EngineInstallRefusals.NodeMissing, noNode.RefusalKey);

            // Старый Node — тоже отказ: порог 20 не «на всякий случай», а измеренный.
            var oldNode = EngineInstaller.Install(
                lockFile, Hints(), root, @"C:\Node\node.exe", @"C:\Node\npm.cmd",
                new EngineInstallDoors(_ => "v19.9.9", (_, _, _, _) => new EngineNpmRun(0, string.Empty, false), _ => Tree(lockFile)),
                null);

            Assert.Equal(EngineInstallOutcome.Refused, oldNode.Outcome);
            Assert.Equal(EngineInstallRefusals.NodeOld, oldNode.RefusalKey);

            var ready = new EngineInstallDoors(_ => "v26.9.0", (_, _, _, _) => new EngineNpmRun(0, string.Empty, false), _ => Tree(lockFile));

            var noNpm = EngineInstaller.Install(lockFile, Hints(), root, @"C:\Node\node.exe", string.Empty, ready, null);

            Assert.Equal(EngineInstallOutcome.Refused, noNpm.Outcome);
            Assert.Equal(EngineInstallRefusals.NpmMissing, noNpm.RefusalKey);

            // И папки версии после отказов не остаётся: «ставить» не начиналось вовсе.
            Assert.False(Directory.Exists(Path.Combine(root, "0.1.5-rc.2")));
        }
        finally
        {
            Remove(root);
        }
    }

    /// <summary>
    /// ЧУЖОЙ ЗАМОК ОСТАНАВЛИВАЕТ УСТАНОВКУ ДО npm: подменённый набор (пакет не с официального
    /// реестра) не должен доходить до запуска чужой программы. Здесь проверяется, что npm
    /// не звали вовсе — то есть отказ пришёл из решения, а не из установки.
    /// </summary>
    [Fact]
    public void Подменённый_замок_до_npm_не_доходит()
    {
        var root = NewFolder();

        try
        {
            var called = false;
            var foreign = EngineTreeLock.Parse(
                "{\"format\":2,\"engine\":{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.1.5-rc.2\"},\"packages\":1," +
                "\"lock\":{\"lockfileVersion\":3,\"packages\":{" +
                "\"\":{\"dependencies\":{\"@deepseek-ai/dsh\":\"0.1.5-rc.2\"}}," +
                "\"node_modules/@deepseek-ai/dsh\":{\"version\":\"0.1.5-rc.2\",\"integrity\":\"sha512-A\"," +
                "\"resolved\":\"https://злой-зеркало.example/dsh.tgz\"}}}}");

            var doors = new EngineInstallDoors(
                _ => "v26.9.0",
                (_, _, _, _) => { called = true; return new EngineNpmRun(0, string.Empty, false); },
                _ => Tree(foreign));

            var result = EngineInstaller.Install(foreign, Hints(), root, @"C:\Node\node.exe", @"C:\Node\npm.cmd", doors, null);

            Assert.False(result.Ok);
            Assert.Equal(EngineInstallRefusals.ForeignSource, result.RefusalKey);
            Assert.False(called, "npm позвали, хотя замок подменён");
        }
        finally
        {
            Remove(root);
        }
    }

    // ---- подставные замок, подсказки и дерево ------------------------------------------------

    /// <summary>Замок на две записи — как настоящий, только короткий.</summary>
    private static EngineTreeLock Lock() => EngineTreeLock.Parse(
        "{\"format\":2,\"engine\":{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.1.5-rc.2\"}," +
        "\"product\":{\"name\":\"dsh-panel-engine\",\"version\":\"0.0.0\"},\"packages\":2," +
        "\"lock\":{\"name\":\"dsh-panel-engine\",\"version\":\"0.0.0\",\"lockfileVersion\":3,\"requires\":true,\"packages\":{" +
        "\"\":{\"name\":\"dsh-panel-engine\",\"version\":\"0.0.0\",\"dependencies\":{\"@deepseek-ai/dsh\":\"0.1.5-rc.2\"}}," +
        "\"node_modules/@deepseek-ai/dsh\":{\"version\":\"0.1.5-rc.2\",\"integrity\":\"sha512-A\"," +
        "\"resolved\":\"https://registry.npmjs.org/@deepseek-ai/dsh/-/dsh-0.1.5-rc.2.tgz\"}," +
        "\"node_modules/a\":{\"version\":\"1.0.0\",\"integrity\":\"sha512-B\"," +
        "\"resolved\":\"https://registry.npmjs.org/a/-/a-1.0.0.tgz\"}}}}");

    /// <summary>Подсказки: версия та же, что в замке, и отмечена проверенной.</summary>
    private static EngineHints Hints() => EngineHints.Parse(
        "{\"format\":1,\"versions\":[{\"version\":\"0.1.5-rc.2\",\"verified\":true," +
        "\"notes\":{\"ru\":[\"заметка\"]},\"troubles\":{\"ru\":[\"беда\"]}}]}");

    /// <summary>Дерево, сошедшееся с подставным замком.</summary>
    private static EngineTreeDisk.Read Tree(EngineTreeLock lockFile) => new(
        lockFile.Entries.Select(entry => entry.Path).ToList(),
        path => lockFile.Entries
            .FirstOrDefault(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))?.Version);

    private static void Package(string root, string relative, string version)
    {
        var directory = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "package.json"), "{\"version\":\"" + version + "\"}");
    }

    private static string NewFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "dsh-installer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);

        return path;
    }

    private static void Remove(string folder) => Files.TreeDeletion.Remove(folder);
}
