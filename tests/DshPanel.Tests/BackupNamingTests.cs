using System;
using System.IO;
using DshPanel.Backup;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Имя копии, папка по умолчанию и папка из настройки. Форма имени перенесена из v1
/// (<c>..\dsh-tray\BackupService.cs</c>), а ПРЕФИКС — свой, и это причина отдельной проверки:
/// папка копий у панели 1.x и у 2.0 ОБЩАЯ (решение владельца 26.09.2026), поэтому различаются
/// панели именно именами. Слей префиксы обратно — и обе панели снова начнут считать чужие архивы
/// своими: ротация 2.0 удалит копию 1.x, а расписание отсчитает сутки от чужого файла (так и вышло
/// ночью 26.09.2026).
/// </summary>
public class BackupNamingTests
{
    [Fact]
    public void Имя_полной_копии_несёт_префикс_панели_2_0()
    {
        var started = new DateTimeOffset(2026, 9, 26, 1, 35, 0, TimeSpan.Zero);

        Assert.Equal(
            "dsh2-backup-2026-09-26-013500-full.zip",
            BackupNaming.ArchiveName(started, withEngine: true));
    }

    [Fact]
    public void Имя_тонкой_копии_несёт_префикс_панели_2_0()
    {
        var started = new DateTimeOffset(2026, 9, 26, 1, 35, 0, TimeSpan.Zero);

        Assert.Equal(
            "dsh2-backup-2026-09-26-013500-thin.zip",
            BackupNaming.ArchiveName(started, withEngine: false));
    }

    /// <summary>
    /// Пустая настройка — это УМОЛЧАНИЕ v1, а не «папка не выбрана». Проверяются оба ответа:
    /// и сама папка, и признак «взято умолчание» — по нему окно говорит об этом словами.
    ///
    /// ⚠️ Папка остаётся ОБЩЕЙ с панелью 1.x — это решение владельца, а не недоделка: разводятся
    /// имена, а не папки. Проверка сторожит именно общность: перенеси 2.0 копии в свою папку —
    /// человек искал бы их в двух местах и не нашёл бы ни в одном.
    /// </summary>
    [Fact]
    public void Пустая_настройка_даёт_общую_папку_умолчания()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DeepSeekHarness-Backups");

        Assert.Equal(expected, BackupNaming.Folder(""));
        Assert.Equal(expected, BackupNaming.Folder("   "));
        Assert.Equal(expected, BackupNaming.Folder(null));

        Assert.True(BackupNaming.IsDefault(""));
        Assert.True(BackupNaming.IsDefault("  "));
        Assert.False(BackupNaming.IsDefault(@"D:\Копии DSH"));
    }

    /// <summary>Переменные окружения раскрываются: так делала v1, и человек этим пользуется.</summary>
    [Fact]
    public void Настроенная_папка_раскрывается_и_чистится()
    {
        var name = "DSH_PANEL_TEST_BACKUP_" + Guid.NewGuid().ToString("N");
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, @"C:\Копии DSH");

        try
        {
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(@"C:\Копии DSH"),
                BackupNaming.Folder("  %" + name + "%  "));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    /// <summary>
    /// Наша копия и наша предохранительная различаются префиксом, и предохранительная не попадает
    /// в список готовых копий как обычная: её имя начинается с другого префикса, и вид у неё свой
    /// (проверки — <c>BackupFolder</c> и <c>BackupRotation</c>).
    /// </summary>
    [Fact]
    public void Готовая_копия_и_предохранительная_различаются_префиксом()
    {
        var name = BackupNaming.ArchiveName(DateTimeOffset.Now, withEngine: true);

        Assert.StartsWith(BackupNaming.Prefix, name, StringComparison.Ordinal);
        Assert.NotEqual(BackupNaming.Prefix, BackupNaming.SafetyPrefix);
        Assert.False(name.StartsWith(BackupNaming.SafetyPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// САМОЕ ГЛАВНОЕ ПРАВИЛО ЭТОЙ ПРАВКИ: наши четыре префикса не путаются между собой, и копия
    /// панели 1.x не считается нашей НИКОГДА.
    ///
    /// ⚠️ Здесь проверяются ОБА префикса прежней панели и все три «наших» вида — и каждая строка
    /// взята из её исходного кода (<c>..\dsh-tray\BackupService.cs:204, 207</c> для 1.x,
    /// <see cref="BackupNaming"/> для 2.0). Слить два вида в один — и панель снова начнёт убирать
    /// чужое; поэтому проверка стоит на самих префиксах, а не только на <c>Kind</c>.
    /// </summary>
    [Fact]
    public void Префиксы_нашей_панели_и_панели_1_x_не_пересекаются()
    {
        Assert.Equal("dsh2-backup-", BackupNaming.Prefix);
        Assert.Equal("dsh2-before-restore-", BackupNaming.SafetyPrefix);
        Assert.Equal("dsh-backup-", BackupNaming.LegacyPrefix);
        Assert.Equal("dsh-before-restore-", BackupNaming.LegacySafetyPrefix);

        // Ни один наш префикс не начинается с чужого и наоборот: иначе выборка 1.x
        // (<c>Directory.GetFiles(Folder, Prefix + "*.zip")</c>) поймала бы наши архивы.
        foreach (var ours in new[] { BackupNaming.Prefix, BackupNaming.SafetyPrefix })
        {
            foreach (var theirs in new[] { BackupNaming.LegacyPrefix, BackupNaming.LegacySafetyPrefix })
            {
                Assert.False(
                    ours.StartsWith(theirs, StringComparison.OrdinalIgnoreCase),
                    $"наш префикс «{ours}» начинается с чужого «{theirs}» — 1.x удалит наши копии");
                Assert.False(
                    theirs.StartsWith(ours, StringComparison.OrdinalIgnoreCase),
                    $"чужой префикс «{theirs}» начинается с нашего «{ours}»");
            }
        }
    }

    /// <summary>
    /// Часы 2.0 читают время ТОЛЬКО у своих копий: имя копии прежней панели времени не даёт вовсе —
    /// иначе чужой архив сдвинул бы наш интервал (именно это и случилось ночью 26.09.2026:
    /// «автокопия: рано: прошло 23 ч 34 мин из 24 ч»). А для СПИСКА та же отметка читается и у чужой
    /// копии: порядок «свежие сверху» обязан быть общим для обеих панелей.
    ///
    /// ⚠️ **Проверка независима от ЧАСОВОГО ПОЯСА машины** (правка 30.09.2026). Сверять момент
    /// со смещением, вписанным в проверку руками (+03:00), нельзя: имя копии хранит МЕСТНУЮ стенную
    /// отметку, а разбирается она как местная, — и на машине с другим поясом (сборочный раннер идёт
    /// по UTC) ответ законно выходит с другим смещением. Прежняя редакция падала именно на этом:
    /// «Expected +03:00, Actual +00:00». Ожидание берёт СТЕННЫЕ ЧИСЛА у самой машины, поэтому
    /// проверяется КРУГОВОЙ оборот «имя → момент», а не пояс машины, на которой прогон.
    /// </summary>
    [Fact]
    public void Время_читается_у_своих_копий_а_чужое_часов_не_двигает()
    {
        var moment = new DateTimeOffset(2026, 9, 24, 23, 30, 0, TimeSpan.FromHours(3));

        // Имя хранит 23:30 и НЕ хранит смещения: обратно эти же стенные числа читаются в поясе
        // машины. Ожидание строится ровно так же — иначе оно сверяло бы пояс, а не круг.
        var expected = new DateTimeOffset(DateTime.SpecifyKind(moment.DateTime, DateTimeKind.Local));

        var ours = BackupNaming.ArchiveName(moment, withEngine: true);

        // Имя и вправду несёт эти стенные числа — то есть круг «момент → имя → момент» начинается
        // с той же отметки, которую проверка ждёт на выходе.
        Assert.Contains("2026-09-24-233000", ours, StringComparison.Ordinal);

        Assert.Equal(expected, BackupNaming.TimeFromName(ours));
        Assert.Equal(expected, BackupNaming.TimeFromName(BackupNaming.SafetyPrefix + "2026-09-24-233000.zip"));

        // Копия панели 1.x: и обычная, и предохранительная — времени для часов НЕ дают.
        Assert.Null(BackupNaming.TimeFromName("dsh-backup-2026-09-24-233000-full.zip"));
        Assert.Null(BackupNaming.TimeFromName("dsh-before-restore-2026-09-24-233000.zip"));

        // А для порядка строк в списке — дают: отметка у обеих панелей одна и та же.
        Assert.Equal(expected, BackupNaming.ListedMoment("dsh-backup-2026-09-24-233000-full.zip"));
        Assert.Equal(expected, BackupNaming.ListedMoment("dsh-before-restore-2026-09-24-233000.zip"));
        Assert.Equal(expected, BackupNaming.ListedMoment(ours));

        // Чужой файл не даёт ни того, ни другого.
        Assert.Null(BackupNaming.ListedMoment("чужой-2026-09-24-233000.zip"));
    }
}
