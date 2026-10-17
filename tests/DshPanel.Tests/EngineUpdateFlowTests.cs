using System;
using System.Linq;
using DshPanel.Localization;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПУТЬ ОБНОВЛЕНИЯ ДВИЖКА: ПОРЯДОК ШАГОВ, ОТКАЗЫ И СЛОВА (пункты 4–5 плана E3.2).
///
/// Здесь проверяется план: спрашиваем **всегда**, предупреждаем и гасим свой движок **до** подмены,
/// снимаем предохранительную копию **до** установки, и «готово» говорим **после пробы**. Машину
/// трогает исполнитель (следующий кусок), поэтому план проверяется без окна, сети и живого движка.
/// </summary>
public class EngineUpdateFlowTests
{
    private static EngineHints Hints(string verified, string unverified) => EngineHints.Parse(
        "{\"format\":1,\"versions\":[" +
        "{\"version\":\"" + verified + "\",\"verified\":true,\"notes\":{\"ru\":[\"з\"]},\"troubles\":{\"ru\":[\"б\"]}}," +
        "{\"version\":\"" + unverified + "\",\"verified\":false,\"notes\":{\"ru\":[\"з\"]},\"troubles\":{\"ru\":[\"б\"]}}]}");

    /// <summary>
    /// ⚠️ ПЕРВЫЙ ШАГ — СПРОСИТЬ, И ОН ЕСТЬ ВСЕГДА: признака «идёт работа» у панели нет, а «сервер
    /// отвечает» признаком не является (замер: отвечающий 3080 при простое). Никакого «умного
    /// определения» здесь не будет, пока оно не измерено на живом движке с ключом модели.
    /// </summary>
    [Fact]
    public void Спрашиваем_всегда_и_это_первый_шаг()
    {
        var plan = EngineUpdateFlow.Plan(Hints("0.1.5-rc.2", "0.2.0-rc.2"), "0.2.0-rc.2", freshWanted: false, ownPort: 3080);

        Assert.True(plan.Any, plan.RefusalKey);
        Assert.Equal(EngineUpdateStepKind.AskHuman, plan.Steps[0].Kind);
        Assert.True(EngineUpdatePolicy.AskAlwaysBeforeUpdate, "«спрашивать ВСЕГДА» выключено — а признака работы у панели нет");
    }

    /// <summary>
    /// ПОРЯДОК: спросить → предупредить и погасить СВОЙ движок → снять копию профиля → поставить
    /// по замку → проба → запись. Копия стоит **до** установки: без неё подмена версии может стать
    /// необратимой, и «полуархива не бывает».
    /// </summary>
    [Fact]
    public void Порядок_шагов_предупреждение_копия_установка_проба()
    {
        var plan = EngineUpdateFlow.Plan(Hints("0.1.5-rc.2", "0.2.0-rc.2"), null, freshWanted: false, ownPort: 3081);

        Assert.True(plan.Any, plan.RefusalKey);
        Assert.Equal(
            new[]
            {
                EngineUpdateStepKind.AskHuman,
                EngineUpdateStepKind.WarnAndStopOwnEngine,
                EngineUpdateStepKind.CopyProfile,
                EngineUpdateStepKind.InstallByLock,
                EngineUpdateStepKind.Probe,
                EngineUpdateStepKind.WriteRecord,
            },
            plan.Steps.Select(step => step.Kind).ToArray());

        // В словах предупреждения назван ПОРТ своего сервера: человек обязан видеть, какой именно
        // сервер панель сейчас погасит.
        var stop = plan.Steps.Single(step => step.Kind == EngineUpdateStepKind.WarnAndStopOwnEngine);

        Assert.Equal("3081", stop.Detail);
        Assert.Contains("3081", Loc.TIn("ru", stop.WordsKey, 3081), StringComparison.Ordinal);

        // И ставим РЕКОМЕНДОВАННУЮ, когда свежую не просили.
        Assert.Equal("0.1.5-rc.2", plan.Version);
    }

    /// <summary>
    /// НЕПРОВЕРЕННАЯ — ТОЛЬКО ПО ОТДЕЛЬНОЙ ПРОСЬБЕ И ТОЛЬКО НОВЕЕ: главная кнопка её не ставит,
    /// а «поставить то же самое, только страшнее» — отказ словами.
    /// </summary>
    [Fact]
    public void Свежая_ставится_только_по_просьбе_и_только_новее()
    {
        var hints = Hints("0.1.5-rc.2", "0.2.0-rc.2");

        var asked = EngineUpdateFlow.Plan(hints, "0.2.0-rc.2", freshWanted: true, ownPort: 3081);

        Assert.True(asked.Any, asked.RefusalKey);
        Assert.Equal("0.2.0-rc.2", asked.Version);

        // ⚠️ А БЕЗ ПРОСЬБЫ главная кнопка остаётся на РЕКОМЕНДОВАННОЙ — даже когда свежая есть
        // и она новее: именно это и значит «одним щелчком непроверенная не ставится никогда».
        var notAsked = EngineUpdateFlow.Plan(hints, "0.2.0-rc.2", freshWanted: false, ownPort: 3081);

        Assert.True(notAsked.Any, notAsked.RefusalKey);
        Assert.Equal("0.1.5-rc.2", notAsked.Version);

        // Просьба поставить НЕ-новую версию — отказ, и он назван.
        var notNewer = EngineUpdateFlow.Plan(hints, "0.1.5-rc.2", freshWanted: true, ownPort: 3081);

        Assert.False(notNewer.Any);
        Assert.Equal(EngineUpdateRefusal.FreshNotNewer, notNewer.Refusal);
        Assert.Equal(EngineUpdateFlow.Words.FreshNotNewer, notNewer.RefusalKey);
        Assert.Empty(notNewer.Steps);

        // Просьба без свежей версии вовсе — тот же отказ, а не «поставим рекомендованную молча».
        Assert.False(EngineUpdateFlow.Plan(hints, null, freshWanted: true, ownPort: 3081).Any);
    }

    /// <summary>
    /// ПРОВЕРЕННОЙ НЕТ — ОБНОВЛЕНИЕ НЕ ПРЕДЛАГАЕТСЯ (а не «возьмём что посвежее»), и копия, снять
    /// которую не удалось, — ОТКАЗ, а не «предупреждение»: «полуархива не бывает».
    /// </summary>
    [Fact]
    public void Без_проверенной_не_обновляемся_а_без_копии_не_начинаем()
    {
        var noHints = EngineUpdateFlow.Plan(null, "0.2.0-rc.2", freshWanted: false, ownPort: 3080);

        Assert.False(noHints.Any);
        Assert.Equal(EngineUpdateRefusal.NoRecommended, noHints.Refusal);
        Assert.Equal(EngineUpdateFlow.Words.NoRecommended, noHints.RefusalKey);
        Assert.Empty(noHints.Steps);

        var copyFailed = EngineUpdateFlow.CopyFailed();

        Assert.False(copyFailed.Any);
        Assert.Equal(EngineUpdateRefusal.CopyNotWhole, copyFailed.Refusal);
        Assert.Empty(copyFailed.Steps);

        // Ключи отказов различимы и объявлены членами PanelStrings, а строки есть на трёх языках.
        var keys = new[]
        {
            EngineUpdateFlow.Words.NoRecommended, EngineUpdateFlow.Words.FreshNotNewer,
            EngineUpdateFlow.Words.CopyNotWhole, EngineUpdateFlow.Words.AskHuman,
            EngineUpdateFlow.Words.WarnAndStopOwnEngine, EngineUpdateFlow.Words.CopyProfile,
            EngineUpdateFlow.Words.InstallByLock, EngineUpdateFlow.Words.Probe,
            EngineUpdateFlow.Words.WriteRecord,
        };

        var members = typeof(DshPanel.Shell.PanelStrings)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(9, keys.Distinct(StringComparer.Ordinal).Count());

        foreach (var key in keys)
        {
            Assert.True(members.Contains(key), $"ключа «{key}» нет среди членов PanelStrings");

            foreach (var language in new[] { "ru", "en", "zh" })
            {
                var text = Loc.TIn(language, key, 3080, "0.2.0-rc.2");

                Assert.NotEqual(key, text);
                Assert.DoesNotContain("{", text, StringComparison.Ordinal);
            }
        }
    }
}
