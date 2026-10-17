using System;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЕСТЬ ЛИ ЗАМОК ДЛЯ ЭТОЙ ВЕРСИИ — прямой вопрос к установщику (найден мутацией R7: без этой
/// проверки мутация «замок есть для любой версии» **выживала**).
///
/// **Зачем это важно человеку.** Панель ставит движок **только по замку**: замок снимается замером
/// с проверенной версии, и для свежей (непроверенной) замка нет. Если бы `HasLockFor` отвечал
/// «есть» на что угодно, раздел предложил бы поставить свежую версию — а установщик поставил бы
/// по замку **проверенную** и объявил свежую, то есть панель сказала бы неправду.
///
/// ⚠️ Сверка идёт **числовой частью** (`UpdateDecisions.Numeric`): хвост предрелиза ею НЕ снимается,
/// поэтому `0.1.5` и `0.1.5-rc.2` — РАЗНЫЕ версии. Это правило панели, а не мелочь: решает проба,
/// а не номер, и «похожая» версия не имеет права считаться той же самой.
///
/// ⚠️ Проверка идёт **по НАСТОЯЩЕМУ встроенному замку сборки** — не по выдуманному: так она
/// стережёт ещё и то, что замок вообще читается и что версия в нём одна.
/// </summary>
public class EngineLockForVersionTests
{
    /// <summary>ЗАМОК ЗНАЕТ СВОЮ ВЕРСИЮ — и только её: соседние версии и хвосты не «сходят».</summary>
    [Fact]
    public void Замок_годится_только_для_своей_версии()
    {
        var known = EngineTreeLock.Load();
        var own = known.EngineVersion;

        Assert.True(known.Any, "встроенный замок не разобрался — проверка была бы слепой");
        Assert.True(EngineInstallDecisions.HasLockFor(known, own), "своя версия обязана сходиться");

        Assert.False(EngineInstallDecisions.HasLockFor(known, "0.2.0-rc.2"), "чужая версия не должна сходиться");
        Assert.False(EngineInstallDecisions.HasLockFor(known, "0.9.9"));

        // Предрелизный хвост значим: если замок снят с предрелиза, «чистая» версия — НЕ он.
        if (own.Contains('-', StringComparison.Ordinal))
        {
            var release = own.Split('-')[0];
            Assert.False(EngineInstallDecisions.HasLockFor(known, release), "хвост предрелиза обязан быть значим");
        }

        // Пустое — «замка нет ни для чего»: ни пустой замок, ни пустая версия ничего не открывают.
        Assert.False(EngineInstallDecisions.HasLockFor(EngineTreeLock.Empty, own));
        Assert.False(EngineInstallDecisions.HasLockFor(null, own));
        Assert.False(EngineInstallDecisions.HasLockFor(known, string.Empty));
        Assert.False(EngineInstallDecisions.HasLockFor(known, null));
    }
}
