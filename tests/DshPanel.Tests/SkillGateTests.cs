using System;
using System.Collections.Generic;
using System.Linq;
using DshPanel.Localization;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ГЕЙТ ПО УМЕНИЯМ (этап E2 плана): функция без нужного умения **отказывает словами**, а не молчит
/// и не падает. Решение — чистая функция от вердиктов умений, поэтому обе стороны (умение есть ·
/// умения нет) проверяются здесь без движка, без сети и без машины.
///
/// ⚠️ Связка с живой дверью (строка баланса) проверяется отдельно, в `BalanceTests`: там видно,
/// что человек ЧИТАЕТ эти слова, а не что они просто собраны.
/// </summary>
public sealed class SkillGateTests
{
    /// <summary>Вердикты «всё на месте» — по одному на каждое умение.</summary>
    private static IReadOnlyList<SkillVerdict> AllSkills() =>
        EngineSkills.All.Select(skill => SkillVerdict.Yes(skill, SkillVerdictYesHow(skill))).ToList();

    private static string SkillVerdictYesHow(EngineSkill skill) => skill switch
    {
        EngineSkill.TaskPrompt or EngineSkill.TaskCancel => SkillKeys.HowDeclared,
        EngineSkill.ModelKey or EngineSkill.BackupLayout => SkillKeys.HowFile,
        EngineSkill.EngineVersion => SkillKeys.HowVersion,
        _ => SkillKeys.HowLive,
    };

    private static IReadOnlyList<SkillVerdict> Without(EngineSkill missing) =>
        AllSkills().Select(v => v.Skill == missing ? SkillVerdict.No(v.Skill, SkillKeys.Reason(v.Skill), v.HowKey) : v).ToList();

    /// <summary>
    /// КАЖДАЯ ФУНКЦИЯ ОБЪЯВЛЯЕТ, ЧТО ЕЙ НУЖНО, и у каждой есть имя для человека. Трею не нужно
    /// ничего — панель живёт и без движка; остальным нужно хоть что-то, иначе гейт был бы пустым.
    /// </summary>
    [Fact]
    public void У_каждой_функции_есть_имя_и_объявленные_умения()
    {
        var functions = Enum.GetValues<PanelFunction>();

        Assert.All(functions, f => Assert.False(string.IsNullOrWhiteSpace(SkillGate.NameKey(f)), $"у функции «{f}» нет имени"));
        Assert.Empty(SkillGate.Needs(PanelFunction.Tray));
        Assert.All(
            functions.Where(f => f != PanelFunction.Tray),
            f => Assert.NotEmpty(SkillGate.Needs(f)));

        // Функции, которых ещё нет, объявлены заранее — и объявлены так, как сказал владелец:
        // балансу ключ, копиям раскладка, телефону — вызовы.
        Assert.Equal(new[] { EngineSkill.ModelKey }, SkillGate.Needs(PanelFunction.Balance));
        Assert.Equal(new[] { EngineSkill.BackupLayout }, SkillGate.Needs(PanelFunction.Copies));
        Assert.Contains(EngineSkill.SessionList, SkillGate.Needs(PanelFunction.Phone));
        Assert.Contains(EngineSkill.QuestionAnswer, SkillGate.Needs(PanelFunction.Phone));
    }

    [Fact]
    public void Когда_умения_на_месте_функции_доступны()
    {
        var verdicts = AllSkills();

        Assert.All(Enum.GetValues<PanelFunction>(), f => Assert.True(SkillGate.Decide(f, verdicts).Allowed, $"«{f}» отказано без причины"));
        Assert.Equal(Enum.GetValues<PanelFunction>().Length, SkillGate.Allowed(verdicts).Count);
    }

    /// <summary>
    /// НЕТ УМЕНИЯ — ФУНКЦИЯ НЕДОСТУПНА, И ОБЪЯСНЯЕТ СЛОВАМИ. Остальное живёт: баланс без ключа
    /// отказывает, а значок, копии и версия — нет (решение владельца: «остальное живёт»).
    /// </summary>
    [Fact]
    public void Без_ключа_баланс_отказывает_а_остальное_живёт()
    {
        var verdicts = Without(EngineSkill.ModelKey);

        var balance = SkillGate.Decide(PanelFunction.Balance, verdicts);
        Assert.False(balance.Allowed);

        var text = balance.Text();
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("{0}", text, StringComparison.Ordinal);
        Assert.Contains(Loc.T(SkillGate.NameKey(PanelFunction.Balance)), text, StringComparison.Ordinal);
        Assert.Contains(Loc.T(SkillKeys.Reason(EngineSkill.ModelKey)), text, StringComparison.Ordinal);

        Assert.True(SkillGate.Decide(PanelFunction.Tray, verdicts).Allowed);
        Assert.True(SkillGate.Decide(PanelFunction.Copies, verdicts).Allowed);
    }

    /// <summary>Без раскладки профиля отказывают копии — и говорят именно про раскладку.</summary>
    [Fact]
    public void Без_раскладки_отказывают_копии()
    {
        var verdicts = Without(EngineSkill.BackupLayout);
        var copies = SkillGate.Decide(PanelFunction.Copies, verdicts);

        Assert.False(copies.Allowed);
        Assert.Contains(Loc.T(SkillKeys.Name(EngineSkill.BackupLayout)), copies.Text(), StringComparison.Ordinal);
    }

    /// <summary>
    /// УМЕНИЯ В НАБОРЕ НЕТ ВОВСЕ — ЭТО «НЕ ЗНАЕМ», И ЧЕСТНЫЙ ОТВЕТ «НЕДОСТУПНО». Пропустить
    /// проверку значило бы разрешить функцию, ничего о ней не зная.
    /// </summary>
    [Fact]
    public void Неизвестное_умение_не_разрешает_функцию()
    {
        var verdicts = AllSkills().Where(v => v.Skill != EngineSkill.ModelKey).ToList();

        var balance = SkillGate.Decide(PanelFunction.Balance, verdicts);

        Assert.False(balance.Allowed);
        Assert.Contains(Loc.T(SkillKeys.Name(EngineSkill.ModelKey)), balance.Text(), StringComparison.Ordinal);
    }

    /// <summary>
    /// ОТКАЗ НАЗЫВАЕТ ПЕРВОЕ НЕДОСТАЮЩЕЕ УМЕНИЕ В ПОРЯДКЕ ОБЪЯВЛЕНИЯ: телефону нужны пять умений,
    /// и человеку важно, чего именно не хватает, чтобы понять, чем это лечится.
    /// </summary>
    [Fact]
    public void Отказ_называет_первое_недостающее_умение()
    {
        var verdicts = AllSkills()
            .Select(v => v.Skill is EngineSkill.EntryLink or EngineSkill.QuestionAnswer
                ? SkillVerdict.No(v.Skill, SkillKeys.Reason(v.Skill), v.HowKey)
                : v)
            .ToList();

        var text = SkillGate.Decide(PanelFunction.Phone, verdicts).Text();

        Assert.Contains(Loc.T(SkillKeys.Name(EngineSkill.EntryLink)), text, StringComparison.Ordinal);
    }

    /// <summary>Имена ключей гейта объявлены в `PanelStrings` — опечатка иначе дошла бы до человека.</summary>
    [Fact]
    public void Ключи_гейта_объявлены_в_PanelStrings()
    {
        var members = typeof(DshPanel.Shell.PanelStrings)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var keys = Enum.GetValues<PanelFunction>().Select(SkillGate.NameKey).Append(SkillKeys.GateRefusedFormat);

        foreach (var key in keys)
        {
            Assert.True(members.Contains(key), $"ключа «{key}» нет среди членов PanelStrings");
        }

        Assert.Equal(Enum.GetValues<PanelFunction>().Length, Enum.GetValues<PanelFunction>().Select(SkillGate.NameKey).Distinct(StringComparer.Ordinal).Count());
    }
}
