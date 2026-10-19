using System;
using System.IO;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЧИСЛА ЖУРНАЛА СЕССИЙ С ДИСКА (блок C плана 2.2.0, шаг «узнать формат»).
///
/// **Почему это проверяется на настоящей раскладке, а не на выдуманной.** Всё, что панель скажет
/// человеку перед откатом, стоит на этих четырёх числах: сколько сессий, сколько уже в новом
/// формате (`session.v4…`), сколько ещё в старом (`session.v3…`) и у скольких журнала нет вовсе.
/// Ошибка в счёте — это не «неточность», а НЕВЕРНОЕ предупреждение: «ничего не спрячется» там,
/// где спрячется сто девяносто шесть сессий (замер `l2`: 197 → 196 молча).
///
/// **Что здесь доказывается:**
/// * форматы различаются по ИМЕНИ файла (`session.v3…` / `session.v4…`), а не по содержимому —
///   ни один байт журнала не читается;
/// * сессия с ДВУМЯ файлами (старый рядом с новым — так выглядит ленивая миграция) считается
///   ОДИН раз и по старшему формату;
/// * папка без журнала — отдельное число, а не «старый формат»;
/// * «плоская» раскладка и незаконченная запись (`…zstd.<хеш>.tmp`) сессиями не считаются;
/// * отсутствие каталога — честный ноль («сессий нет»), а неразбираемый довод — «не знаю»;
///   это разные ответы, и различие закреплено проверкой, а не комментарием.
///
/// ⚠️ Каталог берётся только из <see cref="TempRoots.For"/>, уборка — <see cref="TempRoots.Remove"/>:
/// свой `Directory.Delete` в проверке запрещён (`tests\AGENTS.md`).
/// </summary>
public class EngineJournalFormatTests
{
    /// <summary>Свой корень прогона + раскладка сессий, собранная проверкой.</summary>
    private static string Tree(Action<string> build)
    {
        var root = TempRoots.For("journal-format");
        var sessions = Path.Combine(root, EngineJournalFormat.FolderName);

        Directory.CreateDirectory(sessions);
        build(sessions);

        return root;
    }

    /// <summary>Папка сессии с названными файлами (сами файлы пустые: читаются ИМЕНА).</summary>
    private static void Session(string sessionsDir, string project, string session, params string[] files)
    {
        var folder = Path.Combine(sessionsDir, project, session);

        Directory.CreateDirectory(folder);

        foreach (var file in files) File.WriteAllText(Path.Combine(folder, file), string.Empty);
    }

    private static string Sessions(string root) => Path.Combine(root, EngineJournalFormat.FolderName);

    /// <summary>
    /// ГЛАВНЫЙ СЛУЧАЙ: четыре вида папок в одной раскладке — и каждое число отдельно.
    ///
    /// Здесь же и главная тонкость замера `l2`: после ленивой миграции рядом со старым журналом
    /// лежит новый, и сессия считается ОДИН раз — по старшему формату. Если считать «по числу
    /// файлов», вышло бы больше сессий, чем есть на диске.
    /// </summary>
    [Fact]
    public void Считает_оба_формата_и_папки_без_журнала()
    {
        var root = Tree(sessions =>
        {
            Session(sessions, "проект-1", "старая", "session.v3.jsonl.zstd");
            Session(sessions, "проект-1", "переехавшая", "session.v3.jsonl.zstd", "session.v4.jsonl.zstd");
            Session(sessions, "проект-2", "новая", "session.v4.jsonl.zstd");
            Session(sessions, "проект-2", "только-заведённая");

            File.WriteAllText(Path.Combine(sessions, "проект-1", "session.v3.jsonl"), string.Empty);
            File.WriteAllText(
                Path.Combine(sessions, "проект-2", "новая", "session.v4.jsonl.zstd.a1b2.tmp"),
                string.Empty);
        });

        var state = EngineJournalFormat.Read(Sessions(root));

        try
        {
            Assert.True(state.Measured, "раскладка читается — Measured обязан быть true: " + state.Detail);
            Assert.Equal(4, state.Sessions);
            Assert.Equal(2, state.NewFormat);
            Assert.Equal(1, state.OldFormatOnly);
            Assert.Equal(1, state.NoJournal);
            Assert.Equal(1, state.Flat);
            Assert.Equal(2, state.HiddenFromOld);
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }

    /// <summary>
    /// КАТАЛОГА НЕТ — ЭТО ФАКТ «СЕССИЙ НЕТ», А НЕ «НЕ ПРОЧИТАЛИ»: у чистой установки дома движка
    /// может не быть вовсе, и пугать человека неизвестностью там нечем. Но и путать эти два ответа
    /// нельзя — на различии стоит вся честность предупреждения.
    /// </summary>
    [Fact]
    public void Каталога_нет_значит_сессий_нет()
    {
        var root = TempRoots.For("journal-format");

        try
        {
            var state = EngineJournalFormat.Read(Path.Combine(root, "нет-такого"));

            Assert.True(state.Measured);
            Assert.Equal(0, state.Sessions);
            Assert.Equal(string.Empty, state.Detail);
            Assert.Equal(0, state.HiddenFromOld);
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }

    /// <summary>
    /// НЕРАЗБИРАЕМЫЙ ДОВОД — «НЕ ЗНАЮ» С НАЗВАННОЙ ПРИЧИНОЙ. Пустой путь и «дверь не дана» —
    /// именно те случаи, в которых прежняя панель молча показала бы «прятать нечего».
    /// </summary>
    [Fact]
    public void Неизвестный_путь_не_выдаётся_за_ноль()
    {
        foreach (var bad in new[] { null, string.Empty, "   " })
        {
            var state = EngineJournalFormat.Read(bad);

            Assert.False(state.Measured, "пустой путь выдан за прочитанный ноль");
            Assert.Equal("no-path", state.Detail);
            Assert.Equal(0, state.Sessions);
        }

        Assert.False(EngineJournalState.Unexamined.Measured, "умолчание двери выдано за измеренный ноль");
        Assert.Equal("no-door", EngineJournalState.Unexamined.Detail);
    }

    /// <summary>
    /// ФОРМАТ — ИЗ ИМЕНИ. Здесь ловится самая тихая беда этого шага: если считать «любой `session.v*`»,
    /// незаконченная запись (`…zstd.&lt;хеш&gt;.tmp`, замер 10.10.2026: погашенный движок оставляет
    /// журнал в `.tmp`) выдала бы себя за переехавшую сессию, и панель сказала бы про несуществующее.
    /// </summary>
    [Fact]
    public void Формат_берётся_из_имени_файла()
    {
        Assert.Equal(3, EngineJournalFormat.GenerationOf("session.v3.jsonl.zstd"));
        Assert.Equal(4, EngineJournalFormat.GenerationOf("session.v4.jsonl.zstd"));
        Assert.Equal(3, EngineJournalFormat.GenerationOf("session.v3.jsonl"));

        // Незаконченная запись форматом не считается.
        Assert.Equal(0, EngineJournalFormat.GenerationOf("session.v4.jsonl.zstd.a1b2c3.tmp"));
        Assert.Equal(0, EngineJournalFormat.GenerationOf("session.v3.jsonl.zstd.TMP"));

        // Не наш файл — не журнал: метаданные сессии, чужое имя, мусор.
        Assert.Equal(0, EngineJournalFormat.GenerationOf("session.json"));
        Assert.Equal(0, EngineJournalFormat.GenerationOf("session.v.jsonl"));
        Assert.Equal(0, EngineJournalFormat.GenerationOf("session.v4x"));
        Assert.Equal(0, EngineJournalFormat.GenerationOf("other.v4.jsonl"));
        Assert.Equal(0, EngineJournalFormat.GenerationOf(string.Empty));
        Assert.Equal(0, EngineJournalFormat.GenerationOf(null));
    }

    /// <summary>
    /// ПЛОСКАЯ РАСКЛАДКА: файл журнала прямо в папке проекта. Движок такую папку отвергает целиком
    /// («uses the unsupported flat-file layout»), поэтому это НЕ сессия — но и не ноль: число
    /// называется отдельно, чтобы предохранитель сказал «узнать не удалось», а не «прятать нечего».
    ///
    /// И старший формат в папке сессии: `HighestIn` обязан вернуть 4, когда рядом лежат 3 и 4 —
    /// на этом стоит «переехавшая считается один раз».
    /// </summary>
    [Fact]
    public void Плоская_раскладка_и_старший_формат_считаются_по_своему()
    {
        var root = Tree(sessions =>
        {
            var flatProject = Path.Combine(sessions, "плоский-проект");

            Directory.CreateDirectory(flatProject);
            File.WriteAllText(Path.Combine(flatProject, "session.v3.jsonl.zstd"), string.Empty);

            Session(sessions, "обычный", "сессия", "session.v3.jsonl.zstd", "session.v4.jsonl.zstd");
        });

        try
        {
            var state = EngineJournalFormat.Read(Sessions(root));

            Assert.True(state.Measured);
            Assert.Equal(1, state.Sessions);
            Assert.Equal(1, state.NewFormat);
            Assert.Equal(0, state.OldFormatOnly);
            Assert.Equal(1, state.Flat);

            Assert.Equal(4, EngineJournalFormat.HighestIn(Path.Combine(Sessions(root), "обычный", "сессия")));
            Assert.Equal(0, EngineJournalFormat.HighestIn(Path.Combine(Sessions(root), "нет-такой-папки")));
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }

    /// <summary>
    /// КАТАЛОГ СЕССИЙ В ДОМЕ ДВИЖКА — ОДНО МЕСТО (<c>~\.dsh\sessions</c>, у изолированного прогона —
    /// под его корнем). Второго способа сложить этот путь в панели быть не должно: разошедшийся путь
    /// означал бы «сессий нет» на машине, где их сто девяносто семь.
    /// </summary>
    [Fact]
    public void Каталог_сессий_складывается_в_одном_месте()
    {
        Assert.Equal(
            Path.Combine("C:\\дом", "sessions"),
            EngineJournalFormat.SessionsDir("C:\\дом"));

        Assert.Equal(string.Empty, EngineJournalFormat.SessionsDir(string.Empty));
        Assert.Equal("sessions", EngineJournalFormat.FolderName);
    }
}
