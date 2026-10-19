using System;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПРЕДОХРАНИТЕЛЬ ОТКАТА ЖУРНАЛА (блок C плана 2.2.0, «предупредить + не откатывать молча»).
///
/// **От какого замера.** `_team\220\l2-migraciya.md` (18.10.2026, копия профиля владельца, 197 сессий):
/// вперёд движок говорит (`formatStatus=migration-required`), **назад — молчит**: после одного
/// открытия под 0.2.1 список 0.1.5 стал 197 → **196** без поля, без слова, stderr 0 байт. Значит
/// предупредить может только панель, и предупредить она обязана **с числом**.
///
/// **Что здесь доказывается, и почему это не «проверка на слова»:**
/// * число берётся из ПРОТИВОПОЛОЖНОГО счётчика для каждой стороны: назад считаются уже переехавшие
///   (<c>NewFormat</c>), вперёд — ещё не переехавшие (<c>OldFormatOnly</c>). Перепутать их — значит
///   назвать человеку чужое число, и это падает здесь, а не у него на экране;
/// * «не знаю» — отдельный исход с причиной, а не ноль (та самая молчаливость, ради которой
///   предохранитель и заведён);
/// * откат на версию, читающую новый журнал, честно говорит «прятать нечего»;
/// * порог «с какой версии читается новый журнал» назван числом и совпадает с замером.
///
/// ⚠️ Ни диска, ни движка, ни окна: решения чистые, поэтому проверка идёт по всем клеткам сразу.
/// </summary>
public class EngineJournalFuseTests
{
    /// <summary>Состояние с диска: всего, в новом формате, только в старом, без журнала, плоских.</summary>
    private static EngineJournalState State(int sessions, int newFormat, int oldOnly, int noJournal = 0, int flat = 0) =>
        new(true, sessions, newFormat, oldOnly, noJournal, flat, string.Empty);

    /// <summary>Замер `l2` числами: 197 сессий, из них 5 уже переехали, 192 ещё в старом формате.</summary>
    private static EngineJournalState Measured => State(197, 5, 192);

    /// <summary>
    /// НАЗАД НА 0.1.x — ГЛАВНЫЙ СЛУЧАЙ: названо, сколько сессий прежний сервер DSH не увидит,
    /// и это именно ПЕРЕЕХАВШИЕ, а не оставшиеся.
    /// </summary>
    [Fact]
    public void Откат_на_старую_версию_называет_переехавшие_сессии()
    {
        var warning = EngineJournalFuse.ForRollback(Measured, "0.1.5-rc.2");

        Assert.Equal(EngineJournalRisk.Hidden, warning.Risk);
        Assert.True(warning.Hides, "откат на 0.1.x обязан считаться опасным");
        Assert.Equal(5, warning.AtRisk);
        Assert.Equal(197, warning.Total);
        Assert.Equal("0.1.5-rc.2", warning.Version);
        Assert.Equal(EngineJournalFuse.Words.RollbackHidden, warning.Key);

        // И то же число — в строке ПОСЛЕ отката, только в прошедшем времени: два ключа, один счёт.
        var after = EngineJournalFuse.ForRollback(Measured, "0.1.5-rc.2", after: true);

        Assert.Equal(EngineJournalFuse.Words.RollbackHiddenAfter, after.Key);
        Assert.Equal(warning.AtRisk, after.AtRisk);
        Assert.Equal(warning.Total, after.Total);
        Assert.NotEqual(warning.Key, after.Key);
    }

    /// <summary>
    /// ОТКАТ С 0.2.1 НА 0.2.0 (или на любую версию, читающую новый журнал) — ПРЯТАТЬ НЕЧЕГО:
    /// обе версии видят и `v3`, и `v4`. Это не «нет предупреждения», а честное «ничего не спрячется».
    /// </summary>
    [Fact]
    public void Откат_на_читающую_новый_журнал_ничего_не_прячет()
    {
        foreach (var target in new[] { "0.2.0-rc.2", "0.2.1-rc.2", "0.3.0" })
        {
            var warning = EngineJournalFuse.ForRollback(Measured, target);

            Assert.Equal(EngineJournalRisk.NothingHidden, warning.Risk);
            Assert.False(warning.Hides);
            Assert.Equal(0, warning.AtRisk);
            Assert.Equal(197, warning.Total);
            Assert.Equal(EngineJournalFuse.Words.NothingHidden, warning.Key);
        }
    }

    /// <summary>
    /// ПЕРЕЕХАВШИХ НЕТ — откат ничего не прячет, и это сказано словами (ключ «прятать нечего»),
    /// а не пустой строкой.
    /// </summary>
    [Fact]
    public void Без_переехавших_сессий_прятать_нечего()
    {
        var warning = EngineJournalFuse.ForRollback(State(197, 0, 197), "0.1.5-rc.2");

        Assert.Equal(EngineJournalRisk.NothingHidden, warning.Risk);
        Assert.False(warning.Hides);
        Assert.Equal(EngineJournalFuse.Words.NothingHidden, warning.Key);
        Assert.Equal(197, warning.Total);
    }

    /// <summary>
    /// СЕССИЙ НЕТ ВОВСЕ — ЧЕСТНЫЙ НОЛЬ, и он отличается от «не знаю». Это разные ответы человека
    /// на один и тот же вопрос, и различие закреплено ключом, а не интонацией.
    /// </summary>
    [Fact]
    public void Сессий_нет_это_честный_ноль()
    {
        var warning = EngineJournalFuse.ForRollback(EngineJournalState.Empty, "0.1.5-rc.2");

        Assert.Equal(EngineJournalRisk.NothingHidden, warning.Risk);
        Assert.Equal(EngineJournalFuse.Words.NoSessions, warning.Key);
        Assert.False(warning.Hides);
    }

    /// <summary>
    /// УЗНАТЬ НЕ УДАЛОСЬ — ОТДЕЛЬНЫЙ ИСХОД, а не ноль: причина названа и уезжает в строку.
    /// ⚠️ Ровно этот случай и был бы «молчаливым откатом», если бы панель считала непрочитанное нулём.
    /// </summary>
    [Fact]
    public void Не_прочитали_значит_не_знаю()
    {
        var warning = EngineJournalFuse.ForRollback(EngineJournalState.Unknown("UnauthorizedAccessException"), "0.1.5-rc.2");

        Assert.Equal(EngineJournalRisk.Unknown, warning.Risk);
        Assert.False(warning.Hides);
        Assert.Equal(EngineJournalFuse.Words.Unknown, warning.Key);
        Assert.Equal("UnauthorizedAccessException", warning.Detail);
        Assert.Equal(0, warning.AtRisk);

        // Дверь не дана вовсе — то же «не знаю», с названной причиной.
        var noDoor = EngineJournalFuse.ForRollback(EngineJournalState.Unexamined, "0.1.5-rc.2");

        Assert.Equal(EngineJournalRisk.Unknown, noDoor.Risk);
        Assert.Equal("no-door", noDoor.Detail);
    }

    /// <summary>
    /// ВЕРСИЯ НЕ НАЗВАНА — тоже «не знаю»: без версии нельзя сказать, читает ли она новый журнал,
    /// а угадывать «наверное старая» здесь дороже всего.
    /// </summary>
    [Fact]
    public void Версия_не_названа_значит_не_знаю()
    {
        foreach (var target in new[] { null, string.Empty, "  ", "мусор" })
        {
            var warning = EngineJournalFuse.ForRollback(Measured, target);

            Assert.Equal(EngineJournalRisk.Unknown, warning.Risk);
            Assert.Equal("version-not-named", warning.Detail);
        }
    }

    /// <summary>
    /// ПЛОСКАЯ РАСКЛАДКА: папок сессий нет, а файлы журнала на диске есть. Приравнять это к «сессий
    /// нет» значило бы соврать — движок такие папки отвергает целиком, и панель говорит «не знаю».
    /// </summary>
    [Fact]
    public void Плоская_раскладка_не_выдаётся_за_ноль()
    {
        var warning = EngineJournalFuse.ForRollback(State(0, 0, 0, 0, flat: 3), "0.1.5-rc.2");

        Assert.Equal(EngineJournalRisk.Unknown, warning.Risk);
        Assert.Equal("flat-layout", warning.Detail);
    }

    /// <summary>
    /// ВПЕРЁД: считаются те, кто ЕЩЁ в старом формате, — их и переведёт новая версия при открытии.
    /// Уже переехавшие вперёд не считаются: их судьба решена раньше, и врать про «переезд» нечего.
    ///
    /// ⚠️ Это не то же число, что у отката (5), и подменить одно другим — значит назвать человеку
    /// чужой счёт перед установкой.
    /// </summary>
    [Fact]
    public void Вперёд_считаются_ещё_не_переехавшие()
    {
        var warning = EngineJournalFuse.ForUpdate(Measured, "0.2.1-rc.2");

        Assert.Equal(EngineJournalRisk.Hidden, warning.Risk);
        Assert.True(warning.Hides);
        Assert.Equal(192, warning.AtRisk);
        Assert.Equal(197, warning.Total);
        Assert.Equal(EngineJournalFuse.Words.UpdateMigrates, warning.Key);
        Assert.NotEqual(EngineJournalFuse.ForRollback(Measured, "0.1.5-rc.2").AtRisk, warning.AtRisk);
    }

    /// <summary>
    /// ВСЕ УЖЕ ПЕРЕЕХАЛИ — «переезжать нечему»: ключ свой, чтобы человек не читал предупреждение
    /// о переезде там, где переезда не будет.
    /// </summary>
    [Fact]
    public void Вперёд_все_уже_в_новом_формате()
    {
        var warning = EngineJournalFuse.ForUpdate(State(197, 197, 0), "0.2.1-rc.2");

        Assert.Equal(EngineJournalRisk.NothingHidden, warning.Risk);
        Assert.False(warning.Hides);
        Assert.Equal(EngineJournalFuse.Words.UpdateNothing, warning.Key);
        Assert.Equal(197, warning.Total);
    }

    /// <summary>
    /// ОБНОВЛЕНИЕ НА ВЕРСИЮ, ЧИТАЮЩУЮ ТОЛЬКО СТАРЫЙ ЖУРНАЛ: переехавшие сессии она не увидит.
    /// Отдельный ключ, потому что это уже не «переезд», а спрятанное — и сказать надо другое.
    /// </summary>
    [Fact]
    public void Обновление_на_версию_старого_формата_называет_спрятанное()
    {
        var warning = EngineJournalFuse.ForUpdate(Measured, "0.1.5-rc.2");

        Assert.Equal(EngineJournalRisk.Hidden, warning.Risk);
        Assert.Equal(5, warning.AtRisk);
        Assert.Equal(197, warning.Total);
        Assert.Equal(EngineJournalFuse.Words.TargetOldFormat, warning.Key);
        Assert.Equal("0.1.5-rc.2", warning.Version);
    }

    /// <summary>
    /// ПОРОГ «С КАКОЙ ВЕРСИИ ЧИТАЕТСЯ НОВЫЙ ЖУРНАЛ» — замер, а не вкус: 0.1.5-rc.2 журнала `v4`
    /// не видит вовсе, 0.2.0-rc.2 и 0.2.1-rc.2 видят оба формата (`ENGINE.md` §4 и замер `l2`).
    /// Незаконченная версия (пустая, мусор) — «нет»: угадывать в эту сторону дороже.
    /// </summary>
    [Fact]
    public void Порог_чтения_нового_журнала_совпадает_с_замером()
    {
        Assert.Equal("0.2.0", EngineJournalFuse.FirstReadingNewJournal);

        Assert.True(EngineJournalFuse.ReadsNewJournal("0.2.0"));
        Assert.True(EngineJournalFuse.ReadsNewJournal("0.2.0-rc.2"));
        Assert.True(EngineJournalFuse.ReadsNewJournal("0.2.1-rc.2"));
        Assert.True(EngineJournalFuse.ReadsNewJournal("v0.2.0"));
        Assert.True(EngineJournalFuse.ReadsNewJournal("1.0.0"));

        Assert.False(EngineJournalFuse.ReadsNewJournal("0.1.5-rc.2"));
        Assert.False(EngineJournalFuse.ReadsNewJournal("0.1.9"));
        Assert.False(EngineJournalFuse.ReadsNewJournal(string.Empty));
        Assert.False(EngineJournalFuse.ReadsNewJournal(null));
        Assert.False(EngineJournalFuse.ReadsNewJournal("мусор"));
    }

    /// <summary>
    /// СЛОВА НАЗЫВАЮТ ЧИСЛА — и это отдельная проверка, а не следствие: предупреждение без числа
    /// («сессии могут стать невидимыми») человеку не помогает, и именно так выглядела прежняя
    /// строка про откат. Здесь же ловится подстановка не тех доводов: «{0} из {1}» с версией вместо
    /// числа дало бы «0.1.5-rc.2 из 197».
    /// </summary>
    [Fact]
    public void Слова_называют_оба_числа_и_версию()
    {
        var hidden = EngineJournalFuse.ForRollback(Measured, "0.1.5-rc.2");
        var sentence = EngineJournalPhrases.Sentence(hidden);

        Assert.False(string.IsNullOrWhiteSpace(sentence), "предупреждение об откате молчит");
        Assert.Contains("5", sentence, StringComparison.Ordinal);
        Assert.Contains("197", sentence, StringComparison.Ordinal);
        Assert.Contains("0.1.5-rc.2", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("{", sentence, StringComparison.Ordinal);

        // И после отката — те же два числа.
        var after = EngineJournalPhrases.Sentence(EngineJournalFuse.ForRollback(Measured, "0.1.5-rc.2", after: true));

        Assert.Contains("5", after, StringComparison.Ordinal);
        Assert.Contains("197", after, StringComparison.Ordinal);
        Assert.NotEqual(sentence, after);

        // Вперёд — своё число и своё слово.
        var forward = EngineJournalPhrases.Sentence(EngineJournalFuse.ForUpdate(Measured, "0.2.1-rc.2"));

        Assert.Contains("192", forward, StringComparison.Ordinal);
        Assert.Contains("197", forward, StringComparison.Ordinal);

        // Молчать нечем — но и пустой строки в вопрос не добавляется.
        Assert.Equal(string.Empty, EngineJournalPhrases.Sentence(null));
        Assert.Equal(string.Empty, EngineJournalPhrases.Sentence(EngineJournalWarning.Silent));
    }
}
