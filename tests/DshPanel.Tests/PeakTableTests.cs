using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DshPanel.Agents;
using DshPanel.Peak;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЧИТАЕМАЯ ФОРМА ОКОН ПИКА — таблица «График пиков». Слова владельца 27.09.2026: *«не хватает
/// формы запроса окон пиков и тарифов, там было сделано удобно чтобы можно было прочитать»*,
/// и его же замечание, глядя на панель живьём: *«табличка пиков собрана неверно — сделать ДВЕ
/// таблицы»*.
///
/// Ядро (<see cref="PeakTable"/>) — ЧИСТЫЕ функции от профиля агента: поэтому «что покажет
/// таблица» проверяется перебором смещений часового пояса, без окна, без сети и без часов
/// машины.
///
/// ⚠️ **Ожидания посчитаны ДРУГИМ способом.** Ядро раскладывает окна отрезками и вычитает их
/// из суток; проверка заводит на сутки 1440 отметок «здесь пик» и читает из них непрерывные
/// участки. Два разных способа об одном и том же — единственный способ поймать ошибку в ядре:
/// сравнение функции с самой собой молчит на любой ошибке в ней.
///
/// Четыре обещания:
///
/// 1. **семь дней, от понедельника к воскресенью** — неделя человека, а не <see cref="DayOfWeek"/>
///    (там она начинается с воскресенья);
/// 2. **часы МЕСТНЫЕ** — в профиле агента окна лежат в UTC, и окно, уехавшее за полночь,
///    переезжает вместе с днями недели;
/// 3. **пик и дешёвое время — РАЗНЫЕ колонки**, и вторая есть обратная сторона первой;
/// 4. **день без окон говорит это словами**, а не пустым местом: пустое место читается
///    как «не посчитали».
/// </summary>
public class PeakTableTests
{
    /// <summary>Дни недели в порядке таблицы: понедельник первый.</summary>
    private static readonly int[] Week = { 1, 2, 3, 4, 5, 6, 0 };

    /// <summary>Сутки в минутах — граница, по которую считает и ядро, и эта проверка.</summary>
    private const int DayMinutes = 1440;

    /// <summary>
    /// СВОЙ, НЕЗАВИСИМЫЙ расчёт обеих колонок: сутки размечаются 1440 отметками «здесь пик»,
    /// а колонки читаются из разметки непрерывными участками.
    /// </summary>
    private static (List<string> Peak, List<string> OffPeak) Expected(AgentProfile agent, int offsetMinutes)
    {
        var marked = new bool[7][];

        for (var day = 0; day < 7; day++) marked[day] = new bool[DayMinutes];

        foreach (var window in agent.PeakWindows)
        {
            var from = window.FromMinutes + offsetMinutes;
            var span = window.ToMinutes - window.FromMinutes;
            if (span <= 0) span += DayMinutes;

            var shift = (int)Math.Floor(from / 1440.0);
            var start = ((from % DayMinutes) + DayMinutes) % DayMinutes;

            foreach (var day in window.Days)
            {
                var index = Array.IndexOf(Week, ((day + shift) % 7 + 7) % 7);

                for (var step = 0; step < span; step++)
                {
                    marked[index][(start + step) % DayMinutes] = true;
                }
            }
        }

        var peak = new List<string>();
        var offPeak = new List<string>();

        foreach (var day in marked)
        {
            var busy = Runs(day, wanted: true);
            var free = Runs(day, wanted: false);

            peak.Add(busy.Count > 0 ? Join(busy) : PanelStrings.PeakTableNoPeak);
            offPeak.Add(busy.Count == 0
                ? PanelStrings.PeakTableOffPeakAllDay
                : free.Count > 0
                    ? Join(free)
                    : PanelStrings.PeakTableNoPeak);
        }

        return (peak, offPeak);
    }

    /// <summary>Непрерывные участки одной отметки: «00:00–04:00», «13:00–24:00».</summary>
    private static List<(int From, int To)> Runs(bool[] day, bool wanted)
    {
        var runs = new List<(int From, int To)>();
        var start = -1;

        for (var minute = 0; minute <= DayMinutes; minute++)
        {
            var inside = minute < DayMinutes && day[minute] == wanted;

            if (inside && start < 0) start = minute;
            else if (!inside && start >= 0)
            {
                runs.Add((start, minute));
                start = -1;
            }
        }

        return runs;
    }

    private static string Join(IReadOnlyList<(int From, int To)> runs) =>
        string.Join(", ", runs.Select(run => Clock(run.From) + "–" + Clock(run.To)));

    private static string Clock(int minutes) =>
        string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}", minutes / 60, minutes % 60);

    [Fact]
    public void Таблица_идёт_от_понедельника_и_называет_все_семь_дней()
    {
        var rows = PeakTable.Rows(AgentCatalog.DeepSeek, offsetMinutes: 0);

        Assert.Equal(7, rows.Count);

        Assert.Equal(
            new[]
            {
                PanelStrings.PeakDayMonday,
                PanelStrings.PeakDayTuesday,
                PanelStrings.PeakDayWednesday,
                PanelStrings.PeakDayThursday,
                PanelStrings.PeakDayFriday,
                PanelStrings.PeakDaySaturday,
                PanelStrings.PeakDaySunday,
            },
            rows.Select(row => row.Day));
    }

    /// <summary>
    /// ЧАСЫ МЕСТНЫЕ, и дни недели едут вместе с ними. Перебираются четыре смещения, включая
    /// ОТРИЦАТЕЛЬНОЕ (там окно уезжает на сутки назад, и «пн 01:00 UTC» становится «вс 20:00» —
    /// ровно тот случай, ради которого сдвигаются дни).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(180)]
    [InlineData(600)]
    [InlineData(-300)]
    [InlineData(-720)]
    public void Таблица_показывает_окна_по_местному_времени(int offsetMinutes)
    {
        var agent = AgentCatalog.DeepSeek;

        var rows = PeakTable.Rows(agent, offsetMinutes);
        var expected = Expected(agent, offsetMinutes);

        Assert.Equal(expected.Peak, rows.Select(row => row.PeakWindows));
        Assert.Equal(expected.OffPeak, rows.Select(row => row.OffPeakWindows));

        // И «день с окнами» отличается от «дня без окон» не только текстом: по этому признаку
        // строка красится тоном. Проверка нужна потому, что тон ставится ИМЕННО по нему.
        Assert.Equal(
            expected.Peak.Select(text => text != PanelStrings.PeakTableNoPeak),
            rows.Select(row => row.HasPeak));
    }

    /// <summary>
    /// ДВЕ КОЛОНКИ — ДВА РАЗНЫХ ОТВЕТА. Окна пика и окна дешёвого времени не совпадают ни у одного
    /// дня с пиком: это и есть то, ради чего таблица переделана (прежде оба ответа стояли одной
    /// склейкой и читались как один).
    /// </summary>
    [Fact]
    public void Окна_пика_и_окна_дешёвого_времени_не_совпадают()
    {
        var rows = PeakTable.Rows(AgentCatalog.DeepSeek, offsetMinutes: 0);

        foreach (var row in rows.Where(row => row.HasPeak))
        {
            Assert.NotEqual(row.PeakWindows, row.OffPeakWindows);
            Assert.NotEqual(PanelStrings.PeakTableNoPeak, row.OffPeakWindows);

            // Дешёвое время НАЧИНАЕТСЯ с полуночи: до первого окна пика (01:00 UTC, то есть
            // 01:00 на нулевом смещении) оно идёт с 00:00.
            Assert.StartsWith("00:00–", row.OffPeakWindows, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// НЕДЕЛЯ ЧЕЛОВЕКА ВИДНА И В ДАННЫХ: у DeepSeek окна — по будням, значит суббота
    /// и воскресенье говорят «нет» в колонке пика и «вне пика весь день» в колонке дешёвого
    /// времени, а понедельник — нет.
    /// </summary>
    [Fact]
    public void Будни_в_пике_а_выходные_говорят_что_дешевле_весь_день()
    {
        var rows = PeakTable.Rows(AgentCatalog.DeepSeek, offsetMinutes: 0);

        foreach (var day in new[]
                 {
                     PanelStrings.PeakDayMonday,
                     PanelStrings.PeakDayTuesday,
                     PanelStrings.PeakDayWednesday,
                     PanelStrings.PeakDayThursday,
                     PanelStrings.PeakDayFriday,
                 })
        {
            var row = rows.Single(candidate => candidate.Day == day);

            Assert.True(row.HasPeak, $"{day}: окна пика потерялись");
            Assert.Equal("01:00–04:00, 06:00–10:00", row.PeakWindows);
        }

        foreach (var day in new[] { PanelStrings.PeakDaySaturday, PanelStrings.PeakDaySunday })
        {
            var row = rows.Single(candidate => candidate.Day == day);

            Assert.False(row.HasPeak, $"{day}: пик появился там, где его нет");
            Assert.Equal(PanelStrings.PeakTableNoPeak, row.PeakWindows);
            Assert.Equal(PanelStrings.PeakTableOffPeakAllDay, row.OffPeakWindows);
        }
    }

    /// <summary>Заголовок называет ЧАСОВОЙ ПОЯС: без него «04:00» читалось бы как «на твоих часах».</summary>
    [Theory]
    [InlineData(0, "UTC+00:00")]
    [InlineData(180, "UTC+03:00")]
    [InlineData(-330, "UTC-05:30")]
    public void Заголовок_называет_местный_часовой_пояс(int offsetMinutes, string zone)
    {
        var heading = PeakTable.Heading(offsetMinutes);

        Assert.Contains(zone, heading, StringComparison.Ordinal);
        Assert.Contains("{0}", PanelStrings.PeakTableHeadingFormat, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПОЯСНЕНИЕ «ГДЕ ДОРОЖЕ, А ГДЕ ДЕШЕВЛЕ» СОБРАНО ИЗ ГОТОВЫХ СЛОВ ПАНЕЛИ: те же две строки
    /// стоят в окне панели и в подсказке значка, и своей формулировки у них быть не должно.
    /// </summary>
    [Fact]
    public void Пояснение_о_цене_берёт_готовые_слова()
    {
        var legend = PeakTable.Legend();

        Assert.Contains(PanelStrings.PeakInPeak, legend, StringComparison.Ordinal);
        Assert.Contains(PanelStrings.PeakOffPeak, legend, StringComparison.Ordinal);
    }

    /// <summary>
    /// СОСТОЯНИЕ «СЕЙЧАС» И ПЕРЕКЛЮЧЕНИЕ — те же слова, что в окне панели и в значке. Проверяется
    /// на обоих состояниях: пик и вне пика.
    /// </summary>
    [Fact]
    public void Сейчас_и_переключение_называются_теми_же_словами_что_в_окне()
    {
        var agent = AgentCatalog.DeepSeek;

        // 02:00 UTC понедельника — внутри первого окна пика (01:00–04:00).
        var inPeak = PeakDecisions.State(agent, new DateTimeOffset(2026, 9, 21, 2, 0, 0, TimeSpan.Zero));

        Assert.True(inPeak.InPeak);
        Assert.Equal(PanelStrings.PeakInPeak, PeakTable.NowText(inPeak));
        Assert.Equal(PeakDecisions.NextSwitchText(inPeak), PeakTable.NextText(inPeak));
        Assert.Contains("Пик кончится", PeakTable.NextText(inPeak), StringComparison.Ordinal);

        // 12:00 UTC понедельника — между окнами: цена вне пика, следующее — начало пика.
        var offPeak = PeakDecisions.State(agent, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));

        Assert.False(offPeak.InPeak);
        Assert.Equal(PanelStrings.PeakOffPeak, PeakTable.NowText(offPeak));
        Assert.Contains("Пик начнётся", PeakTable.NextText(offPeak), StringComparison.Ordinal);
    }

    /// <summary>
    /// КОРОТКАЯ СТРОКА ПОД ТАБЛИЦЕЙ: «сейчас: вне пика, до 04:00» — состояние коротким словом
    /// и час переключения ПО МЕСТНОМУ времени. Просил её владелец, и она обязана совпадать
    /// с состоянием: иначе строка под таблицей говорила бы не то, что строка над ней.
    /// </summary>
    [Fact]
    public void Короткая_строка_под_таблицей_называет_состояние_и_час()
    {
        var agent = AgentCatalog.DeepSeek;

        var inPeak = PeakDecisions.State(agent, new DateTimeOffset(2026, 9, 21, 2, 0, 0, TimeSpan.Zero));
        var inPeakLine = PeakTable.NowLine(inPeak);

        Assert.Contains(PanelStrings.PeakShortInPeak, inPeakLine, StringComparison.Ordinal);
        Assert.DoesNotContain(PanelStrings.PeakShortOffPeak, inPeakLine, StringComparison.Ordinal);

        // Конец первого окна — 04:00 UTC; при нулевом смещении это и есть 04:00 на стенке.
        var zone = PeakDecisions.OffsetMinutesAt(new DateTime(2026, 9, 21, 4, 0, 0, DateTimeKind.Utc));
        Assert.Contains(PeakDecisions.LocalClock(new DateTime(2026, 9, 21, 4, 0, 0, DateTimeKind.Utc), zone),
            inPeakLine, StringComparison.Ordinal);

        var offPeak = PeakDecisions.State(agent, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        var offPeakLine = PeakTable.NowLine(offPeak);

        Assert.Contains(PanelStrings.PeakShortOffPeak, offPeakLine, StringComparison.Ordinal);
        Assert.NotEqual(inPeakLine, offPeakLine);

        // Подстановки на месте в словаре: строка без них обещала бы час и не называла его.
        Assert.Contains("{0}", PanelStrings.PeakNowLineFormat, StringComparison.Ordinal);
        Assert.Contains("{1}", PanelStrings.PeakNowLineFormat, StringComparison.Ordinal);
        Assert.Contains("{0}", PanelStrings.PeakNowLineNoTimeFormat, StringComparison.Ordinal);
    }

    /// <summary>
    /// СЕГОДНЯШНИЙ ДЕНЬ СЧИТАЕТСЯ ОТ МОМЕНТА, а не берётся у системных часов внутри построителя:
    /// неделя таблицы начинается с понедельника, поэтому воскресенье — последняя строка.
    /// Перебираются все семь дней: ошибка на одном из них уехала бы на соседний день у человека.
    /// </summary>
    [Fact]
    public void Сегодняшний_день_считается_от_момента_и_неделя_начинается_с_понедельника()
    {
        var monday = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

        for (var step = 0; step < 7; step++)
        {
            var moment = monday.AddDays(step);

            Assert.Equal(step, PeakTable.TodayIndex(moment));
            Assert.Equal(
                PeakDecisions.DayName((int)moment.DayOfWeek),
                PeakTable.Rows(AgentCatalog.DeepSeek, 0)[PeakTable.TodayIndex(moment)].Day);
        }

        // Воскресенье — последняя строка: у DayOfWeek оно нулевое, и без сдвига таблица
        // начиналась бы с него.
        Assert.Equal(6, PeakTable.TodayIndex(monday.AddDays(6)));
    }

    // ------------------------------------------------------------------ в окне

    /// <summary>
    /// В РАЗДЕЛЕ «БАЛАНС И ТАРИФ» НЕТ НИ РАСПИСАНИЯ ПИКОВ, НИ ТАБЛИЦЫ ЦЕН — и это решение
    /// владельца 28.09.2026, а не пропажа: *«мы так и не убрали в пункте „настройки“ обновление
    /// и пики — информацию о пиках и ценах. Для этого у нас отдельное окно, вызываемое в главной
    /// панели»*.
    ///
    /// Прежде раздел повторял собой окно «Пики и тарифы» целиком, и один и тот же ответ на вопрос
    /// «сколько стоит и когда дешевле» жил в двух окнах. Проверка переписана, а не удалена: она
    /// обязана ловить ВОЗВРАТ дубля — если таблицы когда-нибудь снова появятся в настройках,
    /// это упадёт здесь, а не обнаружится глазами владельца.
    ///
    /// ⚠️ Меряется НАСТОЯЩЕЕ окно, а не разметка: орган может лежать в дереве и быть скрытым,
    /// а человеку важно, что он видит. Поэтому проверяются оба признака — органа нет ИЛИ он скрыт.
    /// </summary>
    [AvaloniaFact]
    public void В_разделе_баланса_нет_ни_расписания_пиков_ни_цены()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = PanelTestStand.SettingsStand(dir);
            window.Show();
            PanelTestStand.Settle();

            window.ShowSection(SettingsWindow.BalanceSection);
            PanelTestStand.Settle();

            // Ни построителя расписания, ни построителя цен в разделе не осталось вовсе.
            Assert.Null(window.FindControl<Grid>("PeakTablePanel"));
            Assert.Null(window.FindControl<Grid>("PriceTablePanel"));
            Assert.Null(window.FindControl<TextBlock>("PeakNowText"));
            Assert.Null(window.FindControl<TextBlock>("PeakScheduleText"));
            Assert.Null(window.FindControl<TextBlock>("PriceTitleText"));
            Assert.Null(window.FindControl<TextBlock>("PriceSourceText"));

            // И человеку сказано СЛОВАМИ, куда это уехало: раздел, у которого что-то исчезло молча,
            // читается как сломанный.
            var hint = window.FindControl<TextBlock>("PeakWhereHintText");

            Assert.NotNull(hint);
            Assert.Equal(PanelStrings.SettingsPeakWhereHint, hint!.Text);
            Assert.Contains(PanelStrings.PeakButton, hint.Text!, StringComparison.Ordinal);

            // А НАСТРОЙКИ раздела остались на месте: убраны таблицы, а не сам раздел.
            Assert.NotNull(window.FindControl<ComboBox>("AgentBox"));
            Assert.NotNull(window.FindControl<CheckBox>("BalanceAutoCheck"));
            Assert.NotNull(window.FindControl<CheckBox>("BalanceWarnCheck"));
            Assert.NotNull(window.FindControl<NumericUpDown>("PeakNotifyBox"));

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// СОСТОЯНИЕ ПОКРАШЕНО ТОНОМ ТАРИФА — в окне «Пики и тарифы», которое теперь единственное
    /// показывает состояние: строка «сейчас» зелёная вне пика и янтарная в пик, и цвет берётся
    /// у класса (<see cref="PanelLook.Tone"/>), а не подбирается в окне.
    ///
    /// Проверка переехала из окна настроек вместе с самой строкой (решение владельца 28.09.2026):
    /// сторожить цвет нужно там, где его видит человек, а не там, где он был раньше.
    /// </summary>
    [AvaloniaFact]
    public void Строка_состояния_покрашена_тоном_тарифа()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = new DshPanel.Views.PeakWindow();
            window.Attach(new PeakWindowTests.StubBalance(
                AgentCatalog.DeepSeek,
                PeakDecisions.State(AgentCatalog.DeepSeek, DateTimeOffset.Now)));
            window.Show();
            PanelTestStand.Settle();

            var now = window.FindControl<TextBlock>("NowText");
            Assert.NotNull(now);

            var inPeak = PeakDecisions.State(AgentCatalog.DeepSeek, DateTimeOffset.Now).InPeak;
            var expected = inPeak ? PanelLook.WarningClass : PanelLook.GoodClass;

            Assert.Contains(expected, now!.Classes);

            window.Close();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }
}
