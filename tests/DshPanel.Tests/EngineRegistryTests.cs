using System;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СВЕЖАЯ ВЕРСИЯ ИЗ РЕЕСТРА npm (пункт 5 плана E3.2, решения владельца `6565ac9`, `f09da5d`).
///
/// Здесь проверяется РАЗБОР ответа реестра — чистая функция: живое чтение делается только затем,
/// чтобы показать человеку свежую строку, и в проверках оно подставлено. Главное, что стережётся:
/// **свежая — это ровно то, что реестр зовёт `latest`**, с сохранением хвоста версии, а «не узнали»
/// честно отличается от «узнали».
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
