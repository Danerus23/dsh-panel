using System;
using System.IO;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СВОЙ ДВИЖОК ПАНЕЛИ И ЕГО ПРИМЕТА ВЛАДЕНИЯ (этап E3, шаг E3.0).
///
/// Панель ставит **свой** движок в **свою** папку и обновляет его. Отсюда три вопроса, и все три
/// проверяются здесь **без установки движка и без сети** — это чистые решения о строках:
///
/// 1. где лежит свой движок (раскладка папки — одно место на всю панель);
/// 2. наш ли это путь — **по границе имени папки**, а не по вхождению подстроки
///    (`…\engine-чужой\…` не наш, хотя начинается похоже);
/// 3. чем поднят живой процесс — путь к <c>bin.js</c> берётся из его командной строки тем же
///    разбором, что и примета владения (`ServerDecisions`), — второго разбора быть не должно.
/// </summary>
public class OwnEngineTests
{
    private const string EngineDir = @"C:\Проба\панель\engine";
    private const string RealDshCommandLine =
        "\"C:\\Program Files\\nodejs\\node.exe\" " +
        "C:\\Users\\Public\\AppData\\Roaming\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js " +
        "web --no-open --port 3081";

    /// <summary>Раскладка своего движка — ОДНО место: папка версии внутри папки движка.</summary>
    [Fact]
    public void Свой_движок_лежит_в_папке_версии()
    {
        var path = OwnEngine.BinPath(EngineDir, "0.1.5-rc.2");

        Assert.Equal(
            @"C:\Проба\панель\engine\0.1.5-rc.2\node_modules\@deepseek-ai\dsh\lib\bin.js",
            path);

        // Пустая версия или пустая папка пути не дают: движок «вообще», без версии, — это не наш
        // случай: панель держит ДВЕ версии и обязана знать, какая работает (решение владельца).
        Assert.Equal(string.Empty, OwnEngine.BinPath(EngineDir, "  "));
        Assert.Equal(string.Empty, OwnEngine.BinPath(" ", "0.1.5-rc.2"));
    }

    /// <summary>
    /// НАШ ЛИ ЭТО ПУТЬ. Сверка идёт по границе имени папки: чужой движок в соседней папке
    /// (`engine-чужой`) и глобальная установка человека — НЕ наши, и это главное свойство приметы.
    /// </summary>
    [Fact]
    public void Наш_путь_узнаётся_по_границе_папки()
    {
        Assert.True(OwnEngine.IsOwnBinPath(OwnEngine.BinPath(EngineDir, "0.1.5-rc.2"), EngineDir));
        Assert.True(OwnEngine.IsOwnBinPath(@"C:\Проба\панель\engine\0.1.5-rc.2\что-то\bin.js", EngineDir));
        Assert.True(OwnEngine.IsOwnBinPath(@"c:\проба\ПАНЕЛЬ\ENGINE\0.1.5-rc.2\bin.js", EngineDir));

        // Хвостовой разделитель у папки движка не делает свой путь чужим.
        Assert.True(OwnEngine.IsOwnBinPath(@"C:\Проба\панель\engine\0.1.5-rc.2\lib\bin.js", EngineDir + @"\"));

        // Чужая папка с похожим началом имени — НЕ наша (урок `…\lib\bin.js.old`).
        Assert.False(OwnEngine.IsOwnBinPath(@"C:\Проба\панель\engine-чужой\0.1.5-rc.2\lib\bin.js", EngineDir));

        // Глобальная установка человека — не наша, чей бы это ни был движок.
        Assert.False(OwnEngine.IsOwnBinPath(
            @"C:\Users\Public\AppData\Roaming\npm\node_modules\@deepseek-ai\dsh\lib\bin.js", EngineDir));

        // Пустое и мусор — «нет», и это безопасная сторона: пустая папка движка не совпадёт ни с чем.
        Assert.False(OwnEngine.IsOwnBinPath(null, EngineDir));
        Assert.False(OwnEngine.IsOwnBinPath(@"C:\Проба\панель\engine\0.1.5-rc.2\lib\bin.js", null));
        Assert.False(OwnEngine.IsOwnBinPath("мусор", EngineDir));
    }

    /// <summary>Версия берётся из ИМЕНИ ПАПКИ, которую завела панель.</summary>
    [Fact]
    public void Версия_берётся_из_папки_своего_движка()
    {
        Assert.Equal("0.1.5-rc.2", OwnEngine.VersionFolder(
            @"C:\Проба\панель\engine\0.1.5-rc.2\node_modules\@deepseek-ai\dsh\lib\bin.js", EngineDir));

        Assert.Equal("0.2.0-rc.2", OwnEngine.VersionFolder(@"C:\Проба\панель\engine\0.2.0-rc.2\lib\bin.js", EngineDir));

        // Чужой путь версии не носит: «версия» из чужой папки — выдумка, а не замер.
        Assert.Equal(string.Empty, OwnEngine.VersionFolder(
            @"C:\Users\Public\AppData\Roaming\npm\node_modules\@deepseek-ai\dsh\lib\bin.js", EngineDir));
        Assert.Equal(string.Empty, OwnEngine.VersionFolder(null, EngineDir));
    }

    /// <summary>
    /// ЧЕМ ПОДНЯТ ЖИВОЙ ПРОЦЕСС: путь к <c>bin.js</c> из его командной строки. Разбор — тот же,
    /// что у приметы владения: кавычки снимаются, `…\lib\bin.js.old` движком НЕ считается.
    /// </summary>
    [Fact]
    public void Путь_движка_достаётся_из_командной_строки()
    {
        Assert.Equal(
            @"C:\Users\Public\AppData\Roaming\npm\node_modules\@deepseek-ai\dsh\lib\bin.js",
            OwnEngine.BinPathFromCommandLine(RealDshCommandLine));

        // Обратные и прямые слэши — одна и та же запись: движок и прежние сборки звали по-разному.
        Assert.Equal(
            @"C:/Проба/панель/engine/0.1.5-rc.2/node_modules/@deepseek-ai/dsh/lib/bin.js",
            OwnEngine.BinPathFromCommandLine(
                "\"node.exe\" C:/Проба/панель/engine/0.1.5-rc.2/node_modules/@deepseek-ai/dsh/lib/bin.js web"));

        // Похожее на движок — не движок: `.old` и просто `node.exe` в опись не попадают.
        Assert.Equal(string.Empty, OwnEngine.BinPathFromCommandLine(
            "\"node.exe\" C:\\temp\\@deepseek-ai\\dsh\\lib\\bin.js.old web"));
        Assert.Equal(string.Empty, OwnEngine.BinPathFromCommandLine("\"node.exe\" --version"));
        Assert.Equal(string.Empty, OwnEngine.BinPathFromCommandLine(null));
    }

    /// <summary>
    /// ВЕРСИЯ — ТО, ЧТО ДВИЖОК ГОВОРИТ О СЕБЕ, а не имя папки: папку мог завести человек.
    /// Поэтому версия читается из <c>package.json</c> рядом с <c>bin.js</c>.
    /// </summary>
    [Fact]
    public void Версия_читается_у_самого_движка()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-own-engine-" + Guid.NewGuid().ToString("N"));

        try
        {
            var package = Path.Combine(root, "0.1.5-rc.2", "node_modules", "@deepseek-ai", "dsh");
            Directory.CreateDirectory(Path.Combine(package, "lib"));
            File.WriteAllText(Path.Combine(package, "lib", "bin.js"), "// движок");
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"9.9.9-probe\"}");

            var bin = Path.Combine(package, "lib", "bin.js");

            Assert.Equal("9.9.9-probe", OwnEngine.VersionOf(bin));

            // Папка версии — своя, а версия пакета — своя: панель не путает одно с другим.
            Assert.Equal("0.1.5-rc.2", OwnEngine.VersionFolder(bin, root));

            // Пакета нет — «не прочиталась», и это не «пусто совпало».
            Assert.Equal(string.Empty, OwnEngine.VersionOf(Path.Combine(root, "нет", "package.json")));
            Assert.Equal(string.Empty, OwnEngine.VersionOf(null));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// ЗАПИСЬ О СВОЁМ СЕРВЕРЕ НАЗЫВАЕТ ДВИЖОК — и путь, и ВЕРСИЮ ИЗ ПАКЕТА, а не имя папки.
    ///
    /// ⚠️ Это закрывает дыру, найденную СВОЕЙ ЖЕ мутацией (10.10.2026): мутация «запись не называет
    /// движок» **выжила**, потому что проверялся только путь узнавания после обновления, а свежий
    /// запуск — нет. Теперь сборка записи живёт ОДНОЙ дверью (<see cref="OwnEngine.Record"/>),
    /// и она проверена напрямую.
    /// </summary>
    [Fact]
    public void Запись_о_своём_сервере_называет_движок_и_версию()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-own-record-" + Guid.NewGuid().ToString("N"));

        try
        {
            var package = Path.Combine(root, "0.1.5-rc.2", "node_modules", "@deepseek-ai", "dsh");
            Directory.CreateDirectory(Path.Combine(package, "lib"));
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"version\":\"9.9.9-probe\"}");
            var bin = Path.Combine(package, "lib", "bin.js");

            var record = OwnEngine.Record(3081, 4242, 63812345678901234, bin);

            Assert.Equal(bin, record.EnginePath);
            Assert.Equal("9.9.9-probe", record.EngineVersion);
            Assert.True(record.NamesEngine);

            // Строка переживает чтение — запись не «на словах», а в файле.
            Assert.Equal(record, OwnServerRecord.Parse(record.Format())!.Value);

            // Движка нет — запись остаётся прежней, из трёх полей: врать про движок нельзя.
            var bare = OwnEngine.Record(3081, 4242, 63812345678901234, null);

            Assert.False(bare.NamesEngine);
            Assert.Equal("3081;4242;63812345678901234", bare.Format());
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
