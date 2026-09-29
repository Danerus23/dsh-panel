using System;
using System.Collections.Generic;
using System.Linq;
using DshPanel.Localization;
using DshPanel.Shell;
using DshPanel.Update;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПРИЧИНЫ ОТКАЗА, ИТОГИ ЗАМЕНЫ И ЭТАПЫ ПОДГОТОВКИ — СЛОВАМИ, И НА ВСЕХ ТРЁХ ЯЗЫКАХ.
///
/// Ядро отдаёт КЛЮЧ, а не фразу («UpdateNoArchive», «UpdateReasonRolledBack»), и это правильно:
/// фразы живут в словарях. Но у ключа есть вторая сторона — если строки для него нет, человек
/// увидит «причина отказа не названа» там, где причина есть и названа ядром.
///
/// Поэтому проверка идёт ПЕРЕБОРОМ ВСЕХ значений ядра, а не по списку, который кто-то помнил:
/// добавь новый отказ (итог, этап) и забудь строку — падает здесь, а не в окне у человека.
///
/// ⚠️ Проверка читает словари ЯВНО (<see cref="Loc.TIn"/>): xunit гоняет классы параллельно,
/// и общий выбранный язык одного класса ломал бы другому.
/// </summary>
public class UpdatePhrasesTests
{
    private static readonly string[] Languages = { "ru", "en", "zh" };

    /// <summary>
    /// КАЖДЫЙ ОТКАЗ ЯДРА НАЗВАН СЛОВАМИ во всех трёх языках. Ключ ядра — единственный источник
    /// имён: таблица панели обязана знать ровно те ключи, что может вернуть <see cref="UpdateRefusals.Key"/>.
    /// </summary>
    [Fact]
    public void У_каждого_отказа_ядра_есть_строка_на_трёх_языках()
    {
        foreach (var refusal in Enum.GetValues<UpdateRefusal>())
        {
            var key = UpdateRefusals.Key(refusal);

            // «Отказа нет» — это пустой ключ, и строки ему не нужно.
            if (key.Length == 0) continue;

            Assert.True(UpdateRefusalLines.Knows(key), $"панель не знает ключа отказа «{key}»");

            foreach (var language in Languages)
            {
                var text = Loc.TIn(language, key);

                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}: пустая строка отказа «{key}»");
                Assert.NotEqual(key, text);
            }
        }
    }

    /// <summary>Каждый ИТОГ замены назван словами на трёх языках — перебором всех значений.</summary>
    [Fact]
    public void У_каждого_итога_замены_есть_строка_на_трёх_языках()
    {
        foreach (var kind in Enum.GetValues<UpdateResultKind>())
        {
            var key = UpdateOutcomes.ReasonKeyFor(kind);

            Assert.True(UpdateReasonLines.Knows(key), $"панель не знает ключа итога «{key}»");

            foreach (var language in Languages)
            {
                var text = Loc.TIn(language, key);

                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}: пустая строка итога «{key}»");
                Assert.NotEqual(key, text);
            }
        }
    }

    /// <summary>
    /// Каждый ЭТАП подготовки назван словами на трёх языках. Ключ этапа — «UpdateStage» + имя
    /// значения перечисления: так их и завели, и это здесь СВЕРЯЕТСЯ, а не подразумевается —
    /// переименуй значение, и строка потеряется, а проверка это назовёт.
    /// </summary>
    [Fact]
    public void У_каждого_этапа_подготовки_есть_подпись_на_трёх_языках()
    {
        var labels = new List<string>();

        foreach (var stage in Enum.GetValues<UpdateStage>())
        {
            Assert.True(UpdateStageLines.Knows(stage), $"панель не знает этапа «{stage}»");

            var key = "UpdateStage" + stage;
            labels.Add(Loc.TIn("ru", key));

            foreach (var language in Languages)
            {
                var text = Loc.TIn(language, key);

                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}: пустая подпись этапа «{stage}»");
                Assert.NotEqual(key, text);
            }
        }

        // Этапы РАЗЛИЧАЮТСЯ между собой: две одинаковые подписи у разных этапов читались бы
        // как «панель застряла на одном месте».
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// ПРАВИЛО ПОДСТАНОВКИ ВЗЯТО У ЯДРА ДОСЛОВНО: у ключа с «Format» в имени в строку идёт
    /// техническая подробность, у остальных — нет. Без этого «Сумма не сошлась: {0}» показала бы
    /// человеку фигурную скобку вместо того, ЧТО именно не сошлось.
    /// </summary>
    [Fact]
    public void У_отказа_с_Format_подробность_подставляется_а_у_остальных_нет()
    {
        var mismatch = UpdatePreparation.Refuse(UpdateRefusal.SumMismatch, "ожидалось abc, получено def");
        var line = UpdateRefusalLines.Line(mismatch);

        Assert.Contains("ожидалось abc", line, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", line, StringComparison.Ordinal);

        var noArchive = UpdatePreparation.Refuse(UpdateRefusal.NoArchive, "мелочь для журнала");

        Assert.Equal(PanelStrings.UpdateNoArchive, UpdateRefusalLines.Line(noArchive));
    }

    /// <summary>
    /// УСПЕШНАЯ ПОДГОТОВКА — НЕ ОТКАЗ: строки отказа у неё нет вовсе, и окну нечего показывать.
    /// </summary>
    [Fact]
    public void У_успешной_подготовки_строки_отказа_нет()
    {
        Assert.Equal(string.Empty, UpdateRefusalLines.Line(null));
        Assert.Equal(string.Empty, UpdateRefusalLines.Text(null));
        Assert.Equal(string.Empty, UpdateRefusalLines.Text(string.Empty));
    }

    /// <summary>
    /// НЕЗНАКОМЫЙ КЛЮЧ — «причина не названа», а не пустое место и не сам ключ: пустая строка
    /// в окне читается как сломанная панель, а имя ключа человеку ничего не говорит (урок v1).
    /// </summary>
    [Fact]
    public void Незнакомый_ключ_называет_себя_неизвестным_а_не_ключом()
    {
        Assert.Equal(PanelStrings.UpdateRefusedUnknown, UpdateRefusalLines.Text("UpdateИзБудущего"));
        Assert.Equal(PanelStrings.UpdateReasonUnknown, UpdateReasonLines.Text("UpdateReasonИзБудущего"));

        Assert.False(UpdateRefusalLines.Knows("UpdateИзБудущего"));
        Assert.False(UpdateReasonLines.Knows("UpdateReasonИзБудущего"));
    }

    /// <summary>
    /// КЛЮЧИ ЯДРА И КЛЮЧИ СЛОВАРЯ — ОДНИ И ТЕ ЖЕ ИМЕНА. Ядро называет ключ строкой, а словарь
    /// адресуется членом <see cref="PanelStrings"/>; разойтись они могут молча, поэтому сверка
    /// идёт сравнением: для каждого отказа строка словаря обязана быть НЕ самим ключом.
    /// </summary>
    [Fact]
    public void Ключи_ядра_доходят_до_словаря_и_не_остаются_именами()
    {
        foreach (var refusal in Enum.GetValues<UpdateRefusal>())
        {
            var key = UpdateRefusals.Key(refusal);
            if (key.Length == 0) continue;

            Assert.NotEqual(key, Loc.TIn("ru", key));
        }

        foreach (var kind in Enum.GetValues<UpdateResultKind>())
        {
            var key = UpdateOutcomes.ReasonKeyFor(kind);

            Assert.NotEqual(key, Loc.TIn("ru", key));
        }
    }

    /// <summary>
    /// ИТОГ ОБНОВЛЕНИЯ ПРИ СЛЕДУЮЩЕМ ЗАПУСКЕ: о НЕПРИМЕНИВШЕМСЯ человеку говорят словами, а об
    /// применившемся — нет (панель и так обновилась, шарик об этом был бы шумом).
    ///
    /// ⚠️ Мутация «неприменившееся не показываем» падает ИМЕННО здесь. Цена этой ошибки — человек,
    /// который ждал обновления и не знает, что его нет (ровно так однажды промолчала панель 1.x).
    /// </summary>
    [Fact]
    public void Неприменившееся_обновление_даёт_сообщение_а_применившееся_нет()
    {
        var failed = new UpdateOutcome(
            UpdateVerdict.NotApplied,
            "2.1.0",
            UpdateResultKind.NotAppliedMutex,
            "not-applied-mutex",
            UpdateOutcomes.ReasonKeyFor(UpdateResultKind.NotAppliedMutex),
            string.Empty,
            "замок занят");

        var notice = UpdateOutcomeLines.Notice(failed);

        Assert.False(string.IsNullOrWhiteSpace(notice));
        Assert.Contains("2.1.0", notice, StringComparison.Ordinal);
        Assert.Contains(UpdateReasonLines.Text(failed.ReasonKey), notice, StringComparison.Ordinal);

        // Откатившееся — тоже новость: «прежняя версия возвращена» это не то же, что «обновилось».
        var rolled = failed with { Verdict = UpdateVerdict.RolledBack };

        Assert.False(string.IsNullOrWhiteSpace(UpdateOutcomeLines.Notice(rolled)));

        // Применившееся и «понять не удалось»: первое — молчание, второе — словами (молчанием
        // панель не прячет даже то, чего не поняла: таково решение ядра).
        var applied = failed with { Verdict = UpdateVerdict.Applied, ReasonKey = string.Empty };

        Assert.Equal(string.Empty, UpdateOutcomeLines.Notice(applied));
        Assert.Equal(string.Empty, UpdateOutcomeLines.Notice(null));

        var unknown = failed with { Verdict = UpdateVerdict.Unknown, ReasonKey = UpdateOutcomes.ReasonKeyFor(UpdateResultKind.Unknown) };

        Assert.False(string.IsNullOrWhiteSpace(UpdateOutcomeLines.Notice(unknown)));

        // Журнал: у применившегося — короткая строка, у остальных — техническая причина.
        Assert.Contains("2.1.0", UpdateOutcomeLines.JournalLine(applied), StringComparison.Ordinal);
        Assert.Contains("замок занят", UpdateOutcomeLines.JournalLine(failed), StringComparison.Ordinal);
    }
}
