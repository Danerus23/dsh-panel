using System;
using System.IO;
using DshPanel.Isolation;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// КАКОЙ ДВИЖОК ПОДНИМАЕТ ПАНЕЛЬ (пункт 7 плана E3.2): **сначала СВОЙ поставленный**, потом общая
/// установка человека.
///
/// **Зачем отдельная проверка.** До E3.2 панель поднимала движок только по общим местам
/// (`DshEngine.Locate`: PATH и глобальная установка npm). Этого мало: панель ставит СВОЙ движок
/// в свою папку (E3.1) и обязана поднимать сервер с него — иначе обновление ставило бы версию,
/// которую панель тут же перестала бы использовать, и «обновил» значило бы «скачал в сторонку».
///
/// ⚠️ **Про пути, а не про движок:** папку версии мог завести человек, поэтому своя версия берётся
/// ТОЛЬКО из записи `state\engine.json` — и если её там нет, панель честно идёт в общее место.
/// </summary>
public class EngineStartChoiceTests
{
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Положить движок версии на место (пустышки достаточно: проверка про ВЫБОР пути).</summary>
    private static void PlantEngine(AppPaths paths, string version)
    {
        var bin = OwnEngine.BinPath(paths.EngineDir, version);
        Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
        File.WriteAllText(bin, "// движок");
    }

    private static string PlantNode(AppPaths paths)
    {
        Directory.CreateDirectory(paths.NodeDir);
        var node = Path.Combine(paths.NodeDir, "node.exe");
        File.WriteAllText(node, "node");
        return node;
    }

    /// <summary>СВОЯ ПОСТАВЛЕННАЯ ВЕРСИЯ ПОДНИМАЕТСЯ ПЕРВОЙ — и каким Node её ставили, тем и поднимаем.</summary>
    [Fact]
    public void Своя_поставленная_версия_поднимается_первой()
    {
        var root = TempRoot();

        try
        {
            var paths = AppPaths.Under(root);
            var node = PlantNode(paths);
            PlantEngine(paths, "0.1.5-rc.2");

            var record = EngineInstallRecord.After(
                "0.1.5-rc.2", paths.EngineDir, new EngineNodeResult(
                    EngineNodeOutcome.Ready, node, "22.23.3", string.Empty, string.Empty, Ours: true));

            Assert.True(record.Write(paths.EngineStateFile, out var reason), reason);

            var engine = OwnEngine.Locate(paths);

            Assert.NotNull(engine);
            Assert.Equal(OwnEngine.BinPath(paths.EngineDir, "0.1.5-rc.2"), engine!.BinPath);
            Assert.Equal(node, engine.NodePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// ЗАПИСЬ ЕСТЬ, А ПАПКИ НЕТ — панель не отказывается поднимать сервер: она идёт в общее место.
    /// Это не тихая подмена: путь поднятого движка панель показывает и записывает, как и раньше.
    /// </summary>
    [Fact]
    public void Запись_есть_а_папки_нет_поднимаем_общую()
    {
        var root = TempRoot();

        try
        {
            var paths = AppPaths.Under(root);
            var node = PlantNode(paths);

            var record = EngineInstallRecord.After(
                "0.1.5-rc.2", paths.EngineDir, new EngineNodeResult(
                    EngineNodeOutcome.Ready, node, "22.23.3", string.Empty, string.Empty, Ours: true));

            Assert.True(record.Write(paths.EngineStateFile, out var reason), reason);

            var engine = OwnEngine.Locate(paths);
            var own = OwnEngine.BinPath(paths.EngineDir, "0.1.5-rc.2");

            Assert.NotEqual(own, engine?.BinPath ?? string.Empty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>ПАПКА ЕСТЬ, А ЗАПИСИ НЕТ — это чужая папка: свою версию панель по именам не угадывает.</summary>
    [Fact]
    public void Без_записи_чужая_папка_своей_не_становится()
    {
        var root = TempRoot();

        try
        {
            var paths = AppPaths.Under(root);
            PlantEngine(paths, "0.9.9");

            var engine = OwnEngine.Locate(paths);

            Assert.NotEqual(OwnEngine.BinPath(paths.EngineDir, "0.9.9"), engine?.BinPath ?? string.Empty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
