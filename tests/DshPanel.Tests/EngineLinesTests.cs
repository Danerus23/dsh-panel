using System;
using System.Linq;
using DshPanel.Localization;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// «ВЫЖИМКА» ПРО ВЕРСИИ ДВИЖКА — что именно попадает в поле блока (решение владельца 11.10.2026:
/// *«заметки из файла подсказок в своём поле и ссылка на выпуск»*).
///
/// **Зачем отдельная проверка.** Окно показывает СТРОКИ, которые собрал домен
/// (<see cref="EngineLines.Digest"/>), и подставная дверь окна ничего о содержимом не говорит:
/// выжимка могла бы потерять беды, взять не тот язык или показать заметки про версию, которой
/// в подсказках нет. Здесь проверяется САМ сбор строк — по-настоящему разобранными подсказками.
///
/// ⚠️ Беды показываются ВМЕСТЕ с заметками, и это не украшение: у непроверенной версии заметка
/// без беды («проба не пройдена, семь имён пропали») была бы рекламой, а не подсказкой.
/// </summary>
public class EngineLinesTests
{
    private static EngineHints Hints() => EngineHints.Parse(
        "{\"format\":1,\"versions\":[" +
        "{\"version\":\"0.1.5-rc.2\",\"verified\":true," +
        "\"notes\":{\"ru\":[\"проверена целиком\"],\"en\":[\"verified in full\"],\"zh\":[\"已完整验证\"]}," +
        "\"troubles\":{\"ru\":[\"одна беда\"],\"en\":[\"one trouble\"],\"zh\":[\"一个问题\"]}}," +
        "{\"version\":\"0.2.0-rc.2\",\"verified\":false," +
        "\"notes\":{\"ru\":[\"проба не пройдена\"],\"en\":[\"probe failed\"],\"zh\":[\"探测未通过\"]}," +
        "\"troubles\":{\"ru\":[\"журнал v4\"],\"en\":[\"log v4\"],\"zh\":[\"日志 v4\"]}}]}");

    /// <summary>ЗАМЕТКИ И БЕДЫ ПРОВЕРЕННОЙ ВЕРСИИ — с подписью, про какую версию они.</summary>
    [Fact]
    public void Выжимка_берёт_заметки_и_беды_проверенной_версии()
    {
        var lines = EngineLines.Digest(Hints(), "0.1.5-rc.2", "0.2.0-rc.2", freshIsNewer: false, language: "ru");

        Assert.Equal(4, lines.Count);
        Assert.Contains("0.1.5-rc.2", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, line => line == "проверена целиком");
        Assert.Contains(Loc.T(EngineLines.Words.Troubles), lines);
        Assert.Contains(lines, line => line == "одна беда");

        // Свежая НЕ новее — про неё строк нет вовсе: подсказка не про то, что предлагают.
        Assert.DoesNotContain(lines, line => line.Contains("0.2.0-rc.2", StringComparison.Ordinal));
    }

    /// <summary>СВЕЖАЯ НОВЕЕ — её заметки и беды тоже показываются: иначе «новее» ничем не подкреплено.</summary>
    [Fact]
    public void Выжимка_добавляет_свежую_когда_она_новее()
    {
        var lines = EngineLines.Digest(Hints(), "0.1.5-rc.2", "0.2.0-rc.2", freshIsNewer: true, language: "ru");

        Assert.Contains(lines, line => line.Contains("0.2.0-rc.2", StringComparison.Ordinal));
        Assert.Contains(lines, line => line == "проба не пройдена");
        Assert.Contains(lines, line => line == "журнал v4");
    }

    /// <summary>ЯЗЫК ПАНЕЛИ: строки берутся на нём, а не всегда по-русски (файл подсказок трёхъязычный).</summary>
    [Fact]
    public void Выжимка_идёт_на_языке_панели()
    {
        var zh = EngineLines.Digest(Hints(), "0.1.5-rc.2", string.Empty, freshIsNewer: false, language: "zh");
        var en = EngineLines.Digest(Hints(), "0.1.5-rc.2", string.Empty, freshIsNewer: false, language: "en");

        Assert.Contains(zh, line => line == "已完整验证");
        Assert.Contains(en, line => line == "verified in full");
        Assert.DoesNotContain(zh, line => line == "проверена целиком");
    }

    /// <summary>НЕЧЕГО СКАЗАТЬ — строк НЕТ: окно говорит это словами, а не показывает пустое поле.</summary>
    [Fact]
    public void Без_подсказок_строк_нет()
    {
        Assert.Empty(EngineLines.Digest(null, "0.1.5-rc.2", "0.2.0-rc.2", freshIsNewer: true, language: "ru"));
        Assert.Empty(EngineLines.Digest(EngineHints.Empty, "0.1.5-rc.2", "0.2.0-rc.2", freshIsNewer: true, language: "ru"));

        // Версия, которой в подсказках нет, строк не даёт — выдумывать про неё нечего.
        Assert.Empty(EngineLines.Digest(Hints(), "0.9.9", string.Empty, freshIsNewer: false, language: "ru"));
        Assert.Empty(EngineLines.Digest(Hints(), string.Empty, string.Empty, freshIsNewer: false, language: "ru"));
    }
}
