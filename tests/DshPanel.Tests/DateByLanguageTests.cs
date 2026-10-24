using System.Globalization;
using System.Text.RegularExpressions;
using DshPanel.Agents;
using DshPanel.Backup;
using DshPanel.Localization;
using DshPanel.Peak;
using DshPanel.Pricing;
using DshPanel.Shell;
using DshPanel.Update;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// DEF-041: ДАТА И ВАЛЮТА ПО ЯЗЫКУ ПАНЕЛИ, А НЕ ПО КУЛЬТУРЕ WINDOWS.
///
/// **Что здесь доказывается, и почему именно так.**
/// <list type="number">
/// <item>на ru / en / zh дата РАЗНАЯ и однозначная, в том числе в классическом спорном случае
/// «04/05» — месяц на en стоит словом, на zh подписан иероглифом;</item>
/// <item>старый жёсткий <c>dd.MM.yyyy</c> не появляется НИ В ОДНОЙ точке показа на en/zh —
/// перебором ВСЕХ шести точек (перечень ниже, число проверено), а не одной;</item>
/// <item>культура машины на формат не влияет: под русской культурой английская дата остаётся
/// английской;</item>
/// <item>ХРАНЕНИЕ отметок не изменилось: по-прежнему <c>"O"</c> по инвариантной культуре,
/// разбор круговой, и у двух штампов панели один вид;</item>
/// <item>знак валюты не подменён, а валюта названа КОДОМ (<c>USD</c>/<c>CNY</c>): юань не сходит
/// за доллар, пересчёт не выдаётся за факт.</item>
/// </list>
///
/// ⚠️ **Почему культура ставится внутри проверки.** Прогон идёт под
/// <see cref="CultureInfo.InvariantCulture"/> (<c>TestLocale</c>), а инвариантная культура
/// и есть английская: подмена «язык → текущая культура» осталась бы ЗЕЛЁНОЙ. Поэтому
/// проверка ставит русскую и китайскую культуры сама — и мутация M2 ловится, а не выживает.
/// </summary>
public class DateByLanguageTests
{
    /// <summary>Момент без пояса: формат показывает те часы, что дали, — пояс проверку не двигает.</summary>
    private static readonly DateTime Moment = new(2026, 10, 22, 14, 32, 0, DateTimeKind.Unspecified);

    /// <summary>Классический спорный случай: «04/05» читается по-разному в разных странах.</summary>
    private static readonly DateTime Ambiguous = new(2026, 3, 4, 9, 5, 0, DateTimeKind.Unspecified);

    /// <summary>Русская запись даты — то, что до DEF-041 стояло во ВСЕХ окнах.</summary>
    private static readonly Regex RussianNumericDate = new(@"\b\d{1,2}\.\d{1,2}\.\d{4}\b", RegexOptions.CultureInvariant);

    /// <summary>Отметка в том виде, в каком она лежит в настройках (хранение не менялось).</summary>
    private static readonly string Stamp =
        PricingDecisions.Stamp(new DateTimeOffset(2026, 10, 22, 11, 32, 0, TimeSpan.Zero));

    [Fact]
    public void Дата_на_каждом_языке_своя_и_однозначная()
    {
        var ru = DateText.Date(Moment, "ru");
        var en = DateText.Date(Moment, "en");
        var zh = DateText.Date(Moment, "zh");

        Assert.Equal("22.10.2026", ru);
        Assert.Equal("Oct 22, 2026", en);
        Assert.Equal("2026年10月22日", zh);

        // Три языка — три РАЗНЫЕ записи: «формат по языку» иначе ничего не значит.
        Assert.Equal(3, new[] { ru, en, zh }.Distinct(StringComparer.Ordinal).Count());

        // Однозначность: у en месяц СЛОВОМ (числовая запись и есть дефект), у zh — иероглифы,
        // у ru — обычай языка, который владелец называл словами.
        Assert.Contains("Oct", en, StringComparison.Ordinal);
        Assert.DoesNotContain(".", en, StringComparison.Ordinal);
        Assert.Matches(@"^\d{2}\.\d{2}\.\d{4}$", ru);
        Assert.Matches(@"^\d{4}年\d{1,2}月\d{1,2}日$", zh);

        // Тот самый случай «04/05»: на en и zh он не читается двояко.
        Assert.Equal("04.03.2026", DateText.Date(Ambiguous, "ru"));
        Assert.Equal("Mar 4, 2026", DateText.Date(Ambiguous, "en"));
        Assert.Equal("2026年3月4日", DateText.Date(Ambiguous, "zh"));
    }

    [Fact]
    public void Дата_и_время_на_каждом_языке_свои()
    {
        Assert.Equal("22.10.2026, 14:32", DateText.DateAndTime(Moment, "ru"));
        Assert.Equal("Oct 22, 2026, 14:32", DateText.DateAndTime(Moment, "en"));
        Assert.Equal("2026年10月22日 14:32", DateText.DateAndTime(Moment, "zh"));
    }

    /// <summary>
    /// **M2 ловится здесь.** «Формат по текущей культуре вместо языка» на машине с русской
    /// Windows даёт «окт 22, 2026» — и проверка краснеет; под инвариантной культурой прогона
    /// такая подмена выглядела бы верной, поэтому культуры ставятся явно.
    /// </summary>
    [Fact]
    public void Формат_не_зависит_от_культуры_Windows()
    {
        var saved = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            Assert.Equal("Oct 22, 2026", DateText.Date(Moment, "en"));
            Assert.Equal("2026年10月22日", DateText.Date(Moment, "zh"));

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            Assert.Equal("22.10.2026", DateText.Date(Moment, "ru"));
            Assert.Equal("Oct 22, 2026", DateText.Date(Moment, "en"));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    /// <summary>
    /// ПЕРЕБОР ВСЕХ ТОЧЕК ПОКАЗА. Их шесть, и каждая названа ниже по имени — это и есть число
    /// «7 мест» из реестра минус седьмое: журнал <c>Server\EngineExitReport.cs</c> остаётся
    /// машинным осознанно (это работа S1, формат журнала — инвариант, а не показ человеку).
    /// </summary>
    [Fact]
    public void Старый_формат_не_появляется_ни_в_одной_точке_показа()
    {
        var agent = AgentCatalog.DeepSeek with { PricingChecked = "2026-09-24" };
        var entry = new BackupEntry { CreatedAt = Moment };
        var now = new DateTimeOffset(2026, 10, 22, 14, 32, 0, TimeSpan.Zero);

        foreach (var language in new[] { "en", "zh" })
        {
            var points = new (string Point, string Text)[]
            {
                ("PricingDecisions.CheckedText", PricingDecisions.CheckedText(Stamp, language)),
                ("PricingDecisions.CheckedDateText", PricingDecisions.CheckedDateText(Stamp, language)),
                ("PeakDecisions.CheckedText", PeakDecisions.CheckedText(agent, language)),
                ("BackupSchedule.Due", BackupSchedule.Due(true, 24, now.AddHours(3), now, language: language).Reason),
                ("UpdateLines.PublishedLine", UpdateLines.PublishedLine(true, "2026-09-27T10:00:00Z", language)),
                ("BackupTableView.Moment", BackupTableView.Moment(entry, language)),
            };

            // «Все точки» — это ровно шесть, и их не стало меньше: перебор по пустому списку
            // прошёл бы, ничего не проверив.
            Assert.Equal(6, points.Length);

            foreach (var (point, text) in points)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), $"{point}: пусто на языке {language}");
                Assert.False(
                    RussianNumericDate.IsMatch(text),
                    $"{point} на языке {language} показал русскую запись даты: «{text}»");
            }
        }

        // На русском прежний вид ОСТАЛСЯ: правка не должна менять то, что владелец назвал сам.
        Assert.Equal("22.10.2026", PricingDecisions.CheckedDateText(Stamp));
        Assert.Equal("Sep 24, 2026", PeakDecisions.CheckedText(agent, "en"));
        Assert.Equal("24.09.2026", PeakDecisions.CheckedText(agent));
        Assert.Contains(
            "27.09.2026",
            UpdateLines.PublishedLine(true, "2026-09-27T12:00:00+03:00"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// ХРАНЕНИЕ ОТМЕТОК НЕ ИЗМЕНИЛОСЬ — и это доказано, а не обещано: <c>"O"</c> по инвариантной
    /// культуре, разбор круговой, у двух штампов панели ОДИН вид. Культура машины на хранение
    /// не влияет: иначе отметка, записанная на одной машине, не прочиталась бы на другой.
    /// </summary>
    [Fact]
    public void Хранение_отметок_не_изменилось()
    {
        var moment = new DateTimeOffset(2026, 10, 22, 11, 32, 45, TimeSpan.FromHours(3));

        var stamp = PricingDecisions.Stamp(moment);
        Assert.Equal("2026-10-22T08:32:45.0000000+00:00", stamp);

        Assert.True(PricingDecisions.TryParse(stamp, out var back));
        Assert.Equal(moment.ToUniversalTime(), back);

        // Одна машинная строка на всю панель: второй формат однажды прочитали бы не тем разбором.
        Assert.Equal(stamp, FirstRunDecisions.Stamp(moment));

        // Неразобранное остаётся «отметки нет», а не выдуманной датой.
        Assert.False(PricingDecisions.TryParse("когда-то в октябре", out _));
        Assert.False(PricingDecisions.TryParse(null, out _));
        Assert.Equal(string.Empty, PricingDecisions.CheckedText("когда-то в октябре", "en"));

        var saved = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal(stamp, PricingDecisions.Stamp(moment));
            Assert.Equal(stamp, FirstRunDecisions.Stamp(moment));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    /// <summary>
    /// ВАЛЮТА НАЗВАНА, НО НЕ ПЕРЕСЧИТАНА. Знак остаётся собой (<c>¥</c> не превращается в <c>$</c>
    /// и не пересчитывается), а рядом стоит КОД валюты — иначе знак выдавал бы одну валюту за
    /// другую. **M3 ловится здесь:** «валюта по культуре» под русской культурой назвала бы юань
    /// долларом.
    /// </summary>
    [Fact]
    public void Знак_валюты_не_подменён_а_валюта_названа_кодом()
    {
        Assert.Equal("$", PricingDecisions.Currency("ru"));
        Assert.Equal("$", PricingDecisions.Currency("en"));
        Assert.Equal("¥", PricingDecisions.Currency("zh"));

        Assert.Equal("USD", PricingDecisions.CurrencyCode("ru"));
        Assert.Equal("USD", PricingDecisions.CurrencyCode("en"));
        Assert.Equal("CNY", PricingDecisions.CurrencyCode("zh"));

        // Пересчёта нет: число китайской страницы остаётся своим, знак — своим.
        Assert.Equal("¥0.02", PricingDecisions.Currency("zh") + "0.02");
        Assert.Equal("$0.003", PricingDecisions.Currency("en") + "0.003");

        // Код стоит В ТОЙ ЖЕ строке, где числа, — и это третья подстановка строки словаря.
        Assert.Contains("{2}", PanelStrings.PriceSourceFormat, StringComparison.Ordinal);

        foreach (var (language, sign, code) in new[] { ("ru", "$", "USD"), ("en", "$", "USD"), ("zh", "¥", "CNY") })
        {
            var line = string.Format(
                CultureInfo.InvariantCulture,
                PanelStrings.PriceSourceFormat,
                "https://api-docs.deepseek.com/quick_start/pricing/",
                DateText.DateAndTime(Moment, language),
                PricingDecisions.CurrencyCode(language));

            Assert.Contains(code, line, StringComparison.Ordinal);
            Assert.Equal(sign, PricingDecisions.Currency(language));
        }

        var saved = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            Assert.Equal("CNY", PricingDecisions.CurrencyCode("zh"));
            Assert.Equal("¥", PricingDecisions.Currency("zh"));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    /// <summary>Новый язык в <see cref="LanguageDecisions.Known"/> не остаётся без формата.</summary>
    [Fact]
    public void Каждый_известный_язык_даёт_дату()
    {
        Assert.NotEmpty(LanguageDecisions.Known);

        foreach (var language in LanguageDecisions.Known)
        {
            var date = DateText.Date(Moment, language);

            Assert.False(string.IsNullOrWhiteSpace(date), language);
            Assert.Contains("2026", date, StringComparison.Ordinal);
        }
    }
}
