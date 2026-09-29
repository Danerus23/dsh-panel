using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DshPanel.Backup;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// РОТАЦИЯ КОПИЙ (v2.2): «хранить N последних, старые удалять» — и четыре запрета, каждый из которых
/// дороже самой ротации. Проверяется и решение (<see cref="BackupRotation.Plan"/>, чистый),
/// и удаление на НАСТОЯЩИХ файлах в подставной папке: «план верный» без удаления не доказывает,
/// что человек не потеряет копию.
///
/// Красные правила, ради которых всё и написано:
///   * самую свежую не удалять НИКОГДА;
///   * предохранительные копии перед накатом (<c>dsh2-before-restore-*</c>) не трогать вовсе;
///   * копии панели 1.x (<c>dsh-backup-*</c>, <c>dsh-before-restore-*</c>) не удалять и не считать:
///     папка копий у двух панелей общая, и ночью 26.09.2026 эта общность уже стоила владельцу архива;
///   * чужие файлы в папке не трогать;
///   * копию без разборчивого времени в имени не удалять (её возраст неизвестен).
/// </summary>
public class BackupRotationTests
{
    /// <summary>Копия с временем в имени, отстоящим от «сейчас» на указанное число часов.</summary>
    private static BackupArchive Copy(int hoursAgo, long bytes = 1000, bool thin = false) =>
        Entry(BackupNaming.ArchiveName(Base.AddHours(-hoursAgo), withEngine: !thin), bytes);

    /// <summary>
    /// Запись файла по имени. Метод назван <c>Entry</c>, а не <c>File</c>: имя <c>File</c>
    /// перекрыло бы <see cref="System.IO.File"/>, которым здесь же создаются подставные файлы.
    /// </summary>
    private static BackupArchive Entry(string name, long bytes = 1000, string folder = @"C:\backups") =>
        new(Path.Combine(folder, name), BackupNaming.TimeFromName(name), bytes);

    /// <summary>
    /// Запись с ПРОСТАВЛЕННЫМ временем — там, где имя времени не даёт (предохранительная копия:
    /// её имя начинается с другого префикса). Нужна проверкам, которые обязаны ловить дефект,
    /// а не «проходить по совпадению»: без времени такая запись спасалась бы другим правилом
    /// (не удаляем то, чего не можем датировать) и правило про предохранительные не проверялось бы.
    /// </summary>
    private static BackupArchive Dated(string name, DateTimeOffset moment, long bytes = 1000) =>
        new(Path.Combine(@"C:\backups", name), moment, bytes);

    private static readonly DateTimeOffset Base = new(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(3));

    // --- что удаляется -------------------------------------------------------

    [Fact]
    public void Сверх_предела_удаляются_самые_старые()
    {
        var files = new[] { Copy(1), Copy(2), Copy(3), Copy(4), Copy(5) };

        var plan = BackupRotation.Plan(files, keepCount: 2);

        Assert.Equal(3, plan.Delete.Count);
        Assert.Equal(Copy(3).Name, plan.Delete[0].Name);
        Assert.Equal(Copy(5).Name, plan.Delete[2].Name);

        // Свежие две остаются, и это видно по плану, а не по словам.
        Assert.DoesNotContain(plan.Delete, file => file.Name == Copy(1).Name || file.Name == Copy(2).Name);
        Assert.Contains("2", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Копий_меньше_предела_удалять_нечего()
    {
        var plan = BackupRotation.Plan(new[] { Copy(1), Copy(2) }, keepCount: 5);

        Assert.Empty(plan.Delete);
        Assert.Equal(
            string.Format(PanelStrings.BackupRotationNothingFormat, 2, 5),
            plan.Reason);
    }

    /// <summary>Предел зажат снизу единицей: «хранить 0» означало бы «удалить всё».</summary>
    [Fact]
    public void Предел_ноль_всё_равно_оставляет_свежую()
    {
        var files = new[] { Copy(1), Copy(2), Copy(3) };

        var plan = BackupRotation.Plan(files, keepCount: 0);

        Assert.Equal(2, plan.Delete.Count);
        Assert.DoesNotContain(plan.Delete, file => file.Name == Copy(1).Name);
    }

    /// <summary>
    /// Совпало время в имени (полная и тонкая копия в одну секунду) — свежей считается та,
    /// которую НАЗВАЛ вызывающий: он снял её только что. Без этого правила ротация удаляла бы
    /// именно её, а «самая свежая не удаляется никогда» оставалось бы словами.
    /// </summary>
    [Fact]
    public void При_равном_времени_выживает_названная_свежей()
    {
        var full = Entry(BackupNaming.ArchiveName(Base, withEngine: true));
        var thin = Entry(BackupNaming.ArchiveName(Base, withEngine: false));

        // Порядок имён у этих двух обратный тому, какой нужен: «…-thin.zip» больше «…-full.zip».
        var plan = BackupRotation.Plan(new[] { thin, full }, keepCount: 1, newestName: full.Name);

        Assert.Single(plan.Delete);
        Assert.Equal(thin.Name, plan.Delete[0].Name);
    }

    /// <summary>И наоборот: назвали тонкую — выживает она. Проверка не «подстроена под ответ».</summary>
    [Fact]
    public void При_равном_времени_выживает_названная_свежей_любая()
    {
        var full = Entry(BackupNaming.ArchiveName(Base, withEngine: true));
        var thin = Entry(BackupNaming.ArchiveName(Base, withEngine: false));

        var plan = BackupRotation.Plan(new[] { thin, full }, keepCount: 1, newestName: thin.Name);

        Assert.Single(plan.Delete);
        Assert.Equal(full.Name, plan.Delete[0].Name);
    }

    // --- три запрета ---------------------------------------------------------

    /// <summary>
    /// Предохранительная копия перед накатом — последний путь назад для человека, который только
    /// что разложил архив поверх рабочей системы. Ротация не трогает её НИ ПРИ КАКОМ пределе,
    /// и говорит об этом в причине: молча не тронутая копия выглядела бы забытой.
    ///
    /// ⚠️ **Как устроена проверка и почему именно так** (разбор холодного проверяющего 26.09.2026 —
    /// «проверка не проходит через то состояние, в котором дефект возможен»):
    ///
    /// 1. **у предохранительной копии время ПРОСТАВЛЕНО ВРУЧНУЮ** (<c>Dated</c>), а не вычитано
    ///    из имени: при снятом фильтре видов она обязана попасть в ДАТИРОВАННЫЕ и оказаться
    ///    в удаляемых, а разбор её имени — отдельное правило (хвоста «-full/-thin» у неё нет),
    ///    и проверка не должна от него зависеть;
    /// 2. **она САМАЯ СТАРАЯ** (30 ч), а предел — 1. Значит при снятом фильтре видов она оказывается
    ///    в списке удаляемых: дефект становится возможным ИМЕННО ЗДЕСЬ, и проверка его ловит;
    /// 3. **предусловие проверяется тут же** (<c>Обязательное_условие</c>) — если кто-то сделает
    ///    предохранительную не самой старой, проверка не «слепо позеленеет», а упадёт сама;
    /// 4. **положительный контроль**: обычная копия в том же раскладе УДАЛЯЕТСЯ. Без него проверка
    ///    «всё не удаляем» была бы зелёной и на ротации, которая не удаляет вообще ничего.
    /// </summary>
    [Fact]
    public void Предохранительную_копию_ротация_не_трогает()
    {
        var safety = Dated(BackupNaming.SafetyPrefix + "2026-09-26-013500.zip", Base.AddHours(-30));
        var oldest = Copy(10);
        var newest = Copy(1);

        var files = new[] { safety, oldest, newest };

        Обязательное_условие(safety, files);

        var plan = BackupRotation.Plan(files, keepCount: 1);

        Assert.DoesNotContain(plan.Delete, file => file.Name == safety.Name);

        // Положительный контроль: обычная лишняя копия удаляется — ротация вообще работает.
        Assert.Single(plan.Delete);
        Assert.Equal(oldest.Name, plan.Delete[0].Name);

        Assert.Contains(string.Format(PanelStrings.BackupRotationSafetyKeptFormat, 1), plan.Reason);
    }

    /// <summary>Даже когда хранить разрешено ровно одну копию — предохранительная остаётся.</summary>
    [Fact]
    public void Предохранительная_остаётся_и_при_пределе_один()
    {
        var safety = Dated(BackupNaming.SafetyPrefix + "2026-09-01-010101.zip", Base.AddHours(-50));
        var files = new[] { safety, Copy(1), Copy(2) };

        Обязательное_условие(safety, files);

        var plan = BackupRotation.Plan(files, keepCount: 1);

        Assert.Single(plan.Delete);
        Assert.Equal(Copy(2).Name, plan.Delete[0].Name);
        Assert.DoesNotContain(plan.Delete, file => file.Name == safety.Name);
    }

    /// <summary>
    /// Условие, без которого проверки о предохранительной и чужой копии СЛЕПНУТ: эта копия обязана
    /// быть датированной и САМОЙ СТАРОЙ из датированных. Тогда при снятом фильтре видов она попадает
    /// в удаляемые, и проверка это видит; сделай её свежей — и её спасёт правило «свежую не удаляем»,
    /// а дефект пройдёт молча. Поэтому условие стережётся здесь же, а не остаётся на совести того,
    /// кто однажды тронет этот тест.
    /// </summary>
    private static void Обязательное_условие(BackupArchive protectedCopy, IReadOnlyList<BackupArchive> files)
    {
        Assert.NotNull(protectedCopy.CreatedAt);

        var dated = files
            .Where(file => file.CreatedAt is not null)
            .OrderBy(file => file.CreatedAt!.Value)
            .ToArray();

        Assert.Same(protectedCopy, dated[0]);
    }

    /// <summary>
    /// Чужие файлы в папке — не наше дело. Папку копий человек выбирает сам, и рядом с копиями
    /// у него лежит его собственное: другой архив, документ.
    ///
    /// ⚠️ Среди чужих нарочно есть файл с ЧИТАЕМЫМ временем в имени (наша копия, переименованная
    /// человеком в «<c>.bak</c>»), и он здесь **самый старый**. Без этого проверка была бы слепой:
    /// у остальных чужих имён время не читается, и они не удалились бы даже со снятым фильтром
    /// (проверено мутацией 26.09.2026). Положительный контроль тот же — обычная копия удаляется.
    /// </summary>
    [Fact]
    public void Чужие_файлы_в_папке_не_трогаем()
    {
        var renamed = Dated("dsh2-backup-2026-09-26-013500-full.zip.bak", Base.AddHours(-40));
        var foreign = new[]
        {
            Entry("моя-копия.zip"),
            Entry("dsh-panel-setup.exe"),
            renamed,
        };

        var oldCopy = Copy(10);
        var newCopy = Copy(1);

        var files = foreign.Concat(new[] { oldCopy, newCopy }).ToArray();

        Обязательное_условие(renamed, files);

        var plan = BackupRotation.Plan(files, keepCount: 1);

        // Положительный контроль: лишняя обычная копия удаляется, а чужие — нет.
        Assert.Single(plan.Delete);
        Assert.Equal(oldCopy.Name, plan.Delete[0].Name);

        foreach (var file in foreign) Assert.DoesNotContain(plan.Delete, victim => victim.Name == file.Name);
    }

    /// <summary>
    /// Копия без разборчивого времени в имени: по имени наша, по возрасту — неизвестная.
    /// Удалять неизвестное нельзя, и причина говорит об этом словами.
    /// </summary>
    [Fact]
    public void Копию_без_времени_в_имени_не_удаляем()
    {
        var undated = Entry("dsh2-backup-вручную.zip");

        var plan = BackupRotation.Plan(new[] { undated, Copy(1), Copy(2), Copy(3) }, keepCount: 1);

        Assert.DoesNotContain(plan.Delete, file => file.Name == undated.Name);
        Assert.Contains(undated.Name, plan.Reason, StringComparison.Ordinal);
    }

    // --- удаление на настоящих файлах ---------------------------------------

    [Fact]
    public void Удаление_убирает_названное_и_считает_освобождённое()
    {
        var root = NewRoot();

        try
        {
            var names = new[] { Copy(1).Name, Copy(2).Name, Copy(3).Name };

            foreach (var name in names) File.WriteAllText(Path.Combine(root, name), new string('x', 2048));

            var files = BackupRotation.List(root);
            Assert.Equal(3, files.Count);

            var plan = BackupRotation.Plan(files, keepCount: 1);
            Assert.Equal(2, plan.Delete.Count);

            var removal = BackupRotation.Apply(plan);

            Assert.Equal(2, removal.Deleted);
            Assert.Equal(4096, removal.Freed);
            Assert.Empty(removal.Failed);

            // Осталась ровно свежая — и это проверено на диске, а не по плану.
            var left = Directory.GetFiles(root).Select(Path.GetFileName).ToArray();
            Assert.Equal(new[] { Copy(1).Name }, left);

            Assert.Contains("2", removal.Summary(), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Отказ на одном файле не отменяет остальные. Отказ делается НАСТОЯЩИЙ: на месте первого
    /// по плану файла лежит КАТАЛОГ с таким именем — файлом его не удалить (а просто несуществующий
    /// файл <c>File.Delete</c> пропускает молча, и проверка была бы слепой).
    /// </summary>
    [Fact]
    public void Отказ_на_одном_файле_не_отменяет_остальные()
    {
        var root = NewRoot();

        try
        {
            var names = new[] { Copy(1).Name, Copy(2).Name, Copy(3).Name };

            foreach (var name in names) File.WriteAllText(Path.Combine(root, name), "данные");

            var files = BackupRotation.List(root);
            var plan = BackupRotation.Plan(files, keepCount: 1);

            var stuck = Copy(4);
            Directory.CreateDirectory(Path.Combine(root, stuck.Name));

            var removal = BackupRotation.Apply(plan with
            {
                Delete = new[] { new BackupArchive(Path.Combine(root, stuck.Name), stuck.CreatedAt, 10) }
                    .Concat(plan.Delete)
                    .ToArray(),
            });

            Assert.Equal(2, removal.Deleted);
            Assert.Single(removal.Failed);
            Assert.Contains(stuck.Name, removal.Failed[0], StringComparison.Ordinal);
            Assert.Contains("не удалось", removal.Summary(), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>Список папки читает имена и время: размер — украшение причины, а время решает всё.</summary>
    [Fact]
    public void Список_папки_несёт_время_из_имени()
    {
        var root = NewRoot();

        try
        {
            var name = BackupNaming.ArchiveName(Base, withEngine: true);
            File.WriteAllText(Path.Combine(root, name), new string('x', 512));

            var files = BackupRotation.List(root);

            var single = Assert.Single(files);
            Assert.Equal(name, single.Name);
            Assert.Equal(512, single.Bytes);
            Assert.Equal(Base, single.CreatedAt);
            Assert.Equal(BackupFileKind.Copy, single.Kind);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Несуществующая_папка_даёт_пустой_список()
    {
        Assert.Empty(BackupRotation.List(Path.Combine(Path.GetTempPath(), "dsh-нет-такой-" + Guid.NewGuid().ToString("N"))));
    }

    // --- имя как источник времени -------------------------------------------

    [Fact]
    public void Время_читается_из_имени_копии()
    {
        var name = BackupNaming.ArchiveName(Base, withEngine: true);

        Assert.Equal(Base, BackupNaming.TimeFromName(name));
    }

    /// <summary>
    /// Имя, которое лишь начинается как наше, временем не считается: «2026-09-26-0135000» —
    /// это НЕ та же секунда с лишним знаком, и принимать его за дату значило бы двигать расписание.
    ///
    /// ⚠️ Среди случаев — ОБА имени панели 1.x: для часов 2.0 они не дают времени вовсе
    /// (<see cref="BackupNaming.TimeFromName"/>), и это не «не разобралось», а правило: чужой архив
    /// не должен отменять нашу копию. Подробности — в <c>BackupNamingTests</c>.
    /// </summary>
    [Theory]
    [InlineData("dsh2-backup-2026-09-26-0135000-full.zip")]
    [InlineData("dsh2-backup-2026-13-45-999999-full.zip")]
    [InlineData("dsh2-backup-вручную.zip")]
    [InlineData("dsh2-backup-.zip")]
    [InlineData("dsh2-before-restore-2026-09-26-0135000.zip")]
    [InlineData("dsh-backup-2026-09-26-013500-full.zip")]
    [InlineData("dsh-before-restore-2026-09-26-013500.zip")]
    [InlineData("чужой.zip")]
    [InlineData("")]
    public void Неразборчивое_имя_не_даёт_времени(string name)
    {
        Assert.Null(BackupNaming.TimeFromName(name));
    }

    /// <summary>
    /// Вид файла различается по имени, и регистр не важен — файлы на Windows его не различают.
    ///
    /// ⚠️ Копии панели 1.x — отдельный вид, и случаев на них здесь ЧЕТЫРЕ: оба префикса и оба
    /// регистра. Сведи их к «нашей копии» — и ротация снова начнёт удалять чужие архивы.
    /// </summary>
    [Theory]
    [InlineData("dsh2-backup-2026-09-26-013500-full.zip", BackupFileKind.Copy)]
    [InlineData("DSH2-BACKUP-2026-09-26-013500-full.zip", BackupFileKind.Copy)]
    [InlineData("dsh2-before-restore-2026-09-26-013500.zip", BackupFileKind.Safety)]
    [InlineData("DSH2-BEFORE-RESTORE-2026-09-26-013500.zip", BackupFileKind.Safety)]
    [InlineData("dsh-backup-2026-09-26-013500-full.zip", BackupFileKind.Legacy)]
    [InlineData("DSH-BACKUP-2026-09-26-013500-full.zip", BackupFileKind.Legacy)]
    [InlineData("dsh-before-restore-2026-09-26-013500.zip", BackupFileKind.Legacy)]
    [InlineData("DSH-BEFORE-RESTORE-2026-09-26-013500.zip", BackupFileKind.Legacy)]
    [InlineData("чужой.zip", BackupFileKind.Other)]
    [InlineData("dsh2-backup.zip", BackupFileKind.Other)]
    [InlineData("dsh2-backup-.zip", BackupFileKind.Copy)]
    [InlineData("dsh-backup.zip", BackupFileKind.Other)]
    [InlineData("dsh2-backup-2026-09-26-013500-full.zip.bak", BackupFileKind.Other)]
    [InlineData("dsh-backup-2026-09-26-013500-full.zip.bak", BackupFileKind.Other)]
    public void Вид_файла_читается_по_имени(string name, BackupFileKind expected)
    {
        Assert.Equal(expected, BackupNaming.Kind(name));
    }

    // --- копии панели 1.x: не удаляем и не считаем -------------------------------------------

    /// <summary>
    /// ДЕФЕКТ, РАДИ КОТОРОГО ПИСАЛАСЬ ПРАВКА (ночь 26.09.2026): в ОБЩЕЙ папке лежат копии панели 1.x
    /// рядом с нашими, и ротация 2.0 не имеет права их трогать.
    ///
    /// ⚠️ Как устроена проверка (правило проекта: она обязана проходить через то состояние,
    /// в котором дефект ВОЗМОЖЕН):
    ///
    /// 1. **копии 1.x — САМЫЕ СТАРЫЕ** (18.09 против наших 26.09), и обе её панели в папке: обычная
    ///    и предохранительная. Сочти их ротация своими — они оказались бы первыми в удаляемых;
    /// 2. **предел — 2, наших копий ТРИ**: удаляется ровно одна наша, и это положительный контроль
    ///    (без него «чужое не тронули» проходило бы и на ротации, которая не удаляет ничего);
    /// 3. **условие «чужие старше наших» проверяется тут же**, а не остаётся на совести того, кто
    ///    однажды тронет эту проверку: будь чужая копия свежее — её спасло бы правило «свежую
    ///    не удаляем», и проверка ослепла бы;
    /// 4. **работает на ДИСКЕ, а не по плану**: копии созданы настоящими файлами, план применяется,
    ///    остаток папки сверяется целиком. Здесь же видно, что время из имени копии 1.x наши часы
    ///    НЕ читают (две независимые защиты: вид файла и правило имени) — и это тоже утверждается,
    ///    а не подразумевается.
    ///
    /// А сама мутация — на <c>Kind</c>: слей вид <see cref="BackupFileKind.Legacy"/> с
    /// <see cref="BackupFileKind.Copy"/> — и в причине пропадёт строка про чужие копии, а на диске
    /// не хватит файлов. Прицел по виду, а не по имени, тот же, что у самой ротации.
    /// </summary>
    [Fact]
    public void Копии_панели_1_x_ротация_не_удаляет_и_не_считает()
    {
        var root = NewRoot();

        try
        {
            var legacy = new[]
            {
                "dsh-backup-2026-09-18-010101-full.zip",
                "dsh-before-restore-2026-09-18-020202.zip",
            };

            var ours = new[]
            {
                BackupNaming.ArchiveName(Base.AddHours(-3), withEngine: true),
                BackupNaming.ArchiveName(Base.AddHours(-2), withEngine: true),
                BackupNaming.ArchiveName(Base.AddHours(-1), withEngine: true),
            };

            foreach (var name in legacy.Concat(ours))
                File.WriteAllText(Path.Combine(root, name), "копия");

            var files = BackupRotation.List(root);
            Assert.Equal(5, files.Count);

            // Обе чужие копии — того же вида, и время из ИХ имени наши часы не берут:
            // иначе чужой архив сдвинул бы интервал расписания (та самая беда 26.09.2026).
            foreach (var name in legacy)
            {
                var file = files.Single(candidate => candidate.Name == name);

                Assert.Equal(BackupFileKind.Legacy, file.Kind);
                Assert.Null(file.CreatedAt);
                Assert.NotNull(BackupNaming.ListedMoment(name));

                // Условие 3: чужая копия заведомо старее любой нашей.
                Assert.True(BackupNaming.ListedMoment(name)!.Value < Base.AddHours(-3));
            }

            var plan = BackupRotation.Plan(files, keepCount: 2);

            // Ни одна чужая копия не удаляется — и это утверждается по видам, а не по именам:
            // имена знает и сама проверка, а вид считает тот же код, что и ротация.
            Assert.All(
                files.Where(file => file.Kind == BackupFileKind.Legacy),
                file => Assert.DoesNotContain(plan.Delete, victim => victim.Name == file.Name));

            // Положительный контроль: наша лишняя копия уходит — ротация вообще работает.
            Assert.Single(plan.Delete);
            Assert.Equal(ours[0], plan.Delete[0].Name);

            // Причина называет чужие копии словами и ЧИСЛОМ: молча оставленный архив выглядел бы
            // забытым, а человек видит его в списке рядом со своими.
            Assert.Contains(string.Format(PanelStrings.BackupRotationLegacyKeptFormat, 2), plan.Reason);

            var removal = BackupRotation.Apply(plan);
            Assert.Equal(1, removal.Deleted);
            Assert.Empty(removal.Failed);

            // Остаток папки — на диске: обе чужие копии целы, из наших остались две свежие.
            var left = Directory.GetFiles(root).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            var expected = legacy.Concat(new[] { ours[1], ours[2] })
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expected, left);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Тот же запрет, но на ЧИСТОМ решении и с ПРОВЕРКОЙ СЧЁТА: копии 1.x не попадают ни в предел
    /// «храним N», ни в удаляемые, а причина называет их число.
    ///
    /// Здесь три чужие копии, и предел — 1: если бы чужие считались, в удаляемые ушли бы они
    /// (они старше всех), и наш счёт «удаляем одну свою» разошёлся бы с планом.
    /// </summary>
    [Fact]
    public void Копии_панели_1_x_не_идут_в_предел_хранения()
    {
        var legacy = new[]
        {
            Dated("dsh-backup-2026-09-01-010101-full.zip", Base.AddHours(-500)),
            Dated("dsh-backup-2026-09-02-010101-full.zip", Base.AddHours(-400)),
            Dated("dsh-before-restore-2026-09-03-010101.zip", Base.AddHours(-300)),
        };

        var files = legacy.Concat(new[] { Copy(3), Copy(2), Copy(1) }).ToArray();

        var plan = BackupRotation.Plan(files, keepCount: 1);

        // Удаляется ровно то, что сверх предела у НАШИХ: две старые наши копии.
        Assert.Equal(2, plan.Delete.Count);
        Assert.Equal(Copy(2).Name, plan.Delete[0].Name);
        Assert.Equal(Copy(3).Name, plan.Delete[1].Name);

        foreach (var file in legacy) Assert.DoesNotContain(plan.Delete, victim => victim.Name == file.Name);

        Assert.Contains(string.Format(PanelStrings.BackupRotationLegacyKeptFormat, 3), plan.Reason);

        // И счёт в причине — про НАШИ копии: «копий 3», а не «копий 6». Иначе человек читал бы
        // в журнале число, которого в его папке нет.
        Assert.Contains(PanelStrings.BackupRotationPlanFormat.Split('{')[0] + "3,", plan.Reason);
    }

    /// <summary>
    /// Копия 1.x остаётся в причине и тогда, когда удалять НЕЧЕГО: «ротация ничего не убрала» без неё
    /// выглядело бы как «в папке только наши копии», а человек видит там чужой архив.
    /// </summary>
    [Fact]
    public void Причина_ротации_называет_чужие_копии_даже_когда_удалять_нечего()
    {
        var files = new[]
        {
            Dated("dsh-backup-2026-09-01-010101-full.zip", Base.AddHours(-500)),
            Copy(1),
        };

        var plan = BackupRotation.Plan(files, keepCount: 5);

        Assert.Empty(plan.Delete);
        Assert.Contains(string.Format(PanelStrings.BackupRotationLegacyKeptFormat, 1), plan.Reason);
    }

    // --- уборка --------------------------------------------------------------

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-rotation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch
        {
            // Уборка подставной папки не имеет права уронить проверку.
        }
    }
}
