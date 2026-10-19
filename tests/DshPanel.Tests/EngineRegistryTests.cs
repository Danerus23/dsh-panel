using System;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СВЕЖАЯ ВЕРСИЯ ИЗ РЕЕСТРА npm (пункт 5 плана E3.2, решения владельца `6565ac9`, `f09da5d`).
///
/// Здесь проверяется РАЗБОР ответа реестра — чистая функция: живое чтение делается только затем,
/// чтобы показать человеку свежую строку, и в проверках оно подставлено. Главное, что стережётся:
/// **свежая — это СТАРШАЯ из двух меток реестра (`latest` и `next`)**, с сохранением хвоста версии,
/// а «не узнали» честно отличается от «узнали».
///
/// ⚠️ **Почему двух меток, а не одной (замер 18.10.2026, задача t3 плана 2.2.0).** У npm `next`
/// бывает НОВЕЕ `latest`: реестр звал `latest` = `0.2.0-rc.2`, а `next` = `0.2.1-rc.2`. Прежнее
/// правило «свежая = `latest`» показывало человеку устаревшую строку и прятало более свежую версию
/// целиком — молчание о ней и было дефектом.
/// </summary>
public class EngineRegistryTests
{
    /// <summary>Обычный ответ реестра: свежая — `dist-tags.latest`, хвост версии сохраняется как есть.</summary>
    [Fact]
    public void Свежая_берётся_из_dist_tags_latest()
    {
        var fetch = EngineRegistry.Parse(
            "{\"name\":\"@deepseek-ai/dsh\",\"dist-tags\":{\"latest\":\"0.2.0-rc.2\",\"next\":\"0.2.0-rc.2\"," +
            "\"alpha\":\"0.2.1-alpha.2\"},\"versions\":{\"0.1.5-rc.2\":{},\"0.2.0-rc.2\":{}}}");

        Assert.True(fetch.Ok);
        Assert.Equal("0.2.0-rc.2", fetch.Version);
        Assert.Equal(string.Empty, fetch.Detail);

        // Хвост `-rc.2` НЕ срезается: человек видит ту версию, что назвал реестр.
        Assert.Contains("rc.2", fetch.Version, StringComparison.Ordinal);
    }

    /// <summary>
    /// `next` НОВЕЕ `latest` — СВЕЖАЯ БЕРЁТСЯ ИЗ `next`. Это ровно тот случай, ради которого правило
    /// переписано: иначе панель показала бы `0.2.0-rc.2` и не сказала бы про `0.2.1-rc.2` вовсе.
    /// Обе метки при этом остаются названными в ответе.
    /// </summary>
    [Fact]
    public void Свежая_берётся_из_двух_меток_и_это_next_когда_он_новее()
    {
        var fetch = EngineRegistry.Parse(
            "{\"dist-tags\":{\"latest\":\"0.2.0-rc.2\",\"next\":\"0.2.1-rc.2\",\"alpha\":\"0.2.1-alpha.5\"}}");

        Assert.True(fetch.Ok);
        Assert.Equal("0.2.1-rc.2", fetch.Version);   // старшая из двух
        Assert.Equal("0.2.0-rc.2", fetch.Latest);    // и обе названы как есть
        Assert.Equal("0.2.1-rc.2", fetch.Next);
    }

    /// <summary>
    /// `next` СТАРШЕ `latest` — свежая остаётся `latest`: правило сравнивает версии, а не порядок
    /// меток в ответе. Обратная сторона обязательна, иначе «всегда next» прошло бы молча.
    /// </summary>
    [Fact]
    public void Свежая_остаётся_latest_когда_next_старше()
    {
        var fetch = EngineRegistry.Parse(
            "{\"dist-tags\":{\"latest\":\"0.2.1-rc.2\",\"next\":\"0.2.0-rc.2\"}}");

        Assert.Equal("0.2.1-rc.2", fetch.Version);
        Assert.Equal("0.2.1-rc.2", fetch.Latest);
        Assert.Equal("0.2.0-rc.2", fetch.Next);
    }

    /// <summary>
    /// МЕТКИ `next` НЕТ ИЛИ ОНА НЕ СТРОКА — ЭТО НЕ «НЕ УЗНАЛИ»: свежая берётся из `latest`, а `Next`
    /// остаётся пустой. Иначе реестр без `next` объявил бы панель слепой.
    /// </summary>
    [Theory]
    [InlineData("{\"dist-tags\":{\"latest\":\"0.2.0-rc.2\"}}")]
    [InlineData("{\"dist-tags\":{\"latest\":\"0.2.0-rc.2\",\"next\":42}}")]
    [InlineData("{\"dist-tags\":{\"latest\":\"0.2.0-rc.2\",\"next\":\"\"}}")]
    [InlineData("{\"dist-tags\":{\"latest\":\"0.2.0-rc.2\",\"next\":null}}")]
    public void Без_next_свежая_остаётся_из_latest(string json)
    {
        var fetch = EngineRegistry.Parse(json);

        Assert.True(fetch.Ok);
        Assert.Equal("0.2.0-rc.2", fetch.Version);
        Assert.Equal(string.Empty, fetch.Next);
    }

    /// <summary>
    /// «СВЕЖУЮ НЕ УЗНАЛИ» — ЭТО ОТВЕТ, А НЕ ПАДЕНИЕ И НЕ ДОГАДКА: битый ответ, не объект, нет
    /// `dist-tags`, нет `latest`, пустая версия и пустота дают **пустую версию** с причиной.
    /// </summary>
    [Theory]
    [InlineData("{ это не json")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("<html>реестр переехал</html>")]
    [InlineData("{}")]
    [InlineData("{\"dist-tags\":[]}")]
    [InlineData("{\"dist-tags\":{}}")]
    [InlineData("{\"dist-tags\":{\"next\":\"0.2.0-rc.2\"}}")]
    [InlineData("{\"dist-tags\":{\"latest\":\"\"}}")]
    [InlineData("{\"dist-tags\":{\"latest\":42}}")]
    public void Без_свежей_версии_её_не_выдумывают(string json)
    {
        var fetch = EngineRegistry.Parse(json);

        Assert.False(fetch.Ok);
        Assert.Equal(string.Empty, fetch.Version);
        Assert.NotEqual(string.Empty, fetch.Detail);
    }

    /// <summary>
    /// УГО́ВОР ДЛЯ ПРОВЕРОК: подставленный ответ даёт версию без сети, а недоступный реестр —
    /// это отказ с причиной, а не исключение.
    /// </summary>
    [Fact]
    public void Подставленный_реестр_и_отказ_называются_по_разному()
    {
        var ok = new FakeRegistry("0.2.0-rc.2");
        var bad = new FakeRegistry(string.Empty);

        Assert.Equal("0.2.0-rc.2", ok.Latest().Version);
        Assert.True(ok.Latest().Ok);

        Assert.False(bad.Latest().Ok);
        Assert.NotEqual(string.Empty, bad.Latest().Detail);

        // Адрес — официальный реестр, и это проверяемо строкой, а не обещанием.
        Assert.StartsWith("https://registry.npmjs.org/", EngineRegistry.PackageUrl, StringComparison.Ordinal);
    }

    private sealed class FakeRegistry(string version) : IEngineRegistryClient
    {
        public EngineRegistryFetch Latest() =>
            version.Length > 0 ? new EngineRegistryFetch(version, string.Empty) : EngineRegistryFetch.Failed("нет сети");
    }
}
