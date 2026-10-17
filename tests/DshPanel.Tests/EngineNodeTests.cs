using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СВОЙ NODE: СКАЧАТЬ, СВЕРИТЬ, РАСПАКОВАТЬ, ПРОВЕРИТЬ (этап E3.1, шаг 1 плана владельца).
///
/// Решение владельца 10.10.2026: *«Node — системный, проверяется ВЕРСИЯ (≥ 20), свой Node только
/// когда его нет или он старше, в свою папку, со сверкой SHA-256»* и *«если дёшево — проверь
/// и третье состояние: Node есть, но старый (18); панель обязана его отвергнуть и скачать свой»*.
///
/// ⚠️ Сеть и диск здесь ПОДСТАВНЫЕ (<see cref="EngineNodeDoors"/>): настоящая загрузка 30 МБ
/// с nodejs.org — это живой прогон, а не проверка. Проверяется ГРАНИЦА: кого панель берёт, кого
/// отвергает, что делает при расхождении сумм и что — при сломанном архиве.
/// </summary>
public class EngineNodeTests
{
    /// <summary>
    /// ПОИСК ОДНОГО NODE УВАЖАЕТ ПОДМЕНУ `DSH_PANEL_NODE`. Без этого проба «Node есть, но старый»
    /// не видит подставленный старый Node и проверяет не то состояние — так и вышло в живом прогоне
    /// 10.10.2026: проба сказала «системный Node не найден», хотя v18 был подставлен.
    /// </summary>
    [Fact]
    public void Подмена_DSH_PANEL_NODE_видна_и_поиску_одного_Node()
    {
        var folder = NewFolder();

        try
        {
            var exe = Path.Combine(folder, "node.exe");
            File.WriteAllText(exe, "подставной Node");

            var found = DshEngine.FindNode(name =>
                name == DshEngine.NodeOverrideVariable ? exe : null);

            Assert.Equal(exe, found);

            // Подмены нет — ищем как обычно (на этой машине Node есть; ответ «пусто» тоже законен).
            var ordinary = DshEngine.FindNode(_ => null);

            Assert.True(ordinary is null || ordinary.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Files.TreeDeletion.Remove(folder);
        }
    }

    /// <summary>Версия Node разбирается, а порог — 20 (решение владельца 10.10.2026).</summary>
    [Theory]
    [InlineData("v26.9.0", 26, true)]
    [InlineData("26.9.0", 26, true)]
    [InlineData("v20.0.0", 20, true)]
    [InlineData("v19.9.9", 19, false)]
    [InlineData("v18.20.4", 18, false)]
    [InlineData("мусор", 0, false)]
    [InlineData("", 0, false)]
    public void Версия_Node_разбирается_и_сравнивается_с_порогом(string text, int major, bool suits)
    {
        Assert.Equal(major, EngineNode.Major(text));
        Assert.Equal(suits, EngineNode.Suits(text));
        Assert.Equal(20, EngineNode.MinimumMajor);
    }

    /// <summary>
    /// ИСТОЧНИК — ОФИЦИАЛЬНЫЙ И ТОЧНЫЙ: адреса ведут на `nodejs.org/dist/v&lt;версия&gt;`, имя архива
    /// названо версией, а версия ЗАКРЕПЛЕНА, а не «последняя»: по движущемуся адресу завтра будет
    /// другой файл, и проверенная сумма перестанет что-либо значить.
    /// </summary>
    [Fact]
    public void Источник_официальный_а_версия_закреплена()
    {
        Assert.Equal("22.23.3", EngineNode.Release);
        Assert.Equal("node-v22.23.3-win-x64.zip", EngineNode.ArchiveName());
        Assert.Equal("https://nodejs.org/dist/v22.23.3", EngineNode.SumsRoot());
        Assert.Equal("https://nodejs.org/dist/v22.23.3/node-v22.23.3-win-x64.zip", EngineNode.ArchiveUrl());
        Assert.Equal("https://nodejs.org/dist/v22.23.3/SHASUMS256.txt", EngineNode.SumsUrl());

        // Закреплённая сумма — 64 шестнадцатеричных знака, и она не «пустая»: сверять есть чем.
        Assert.Equal(64, EngineNode.ArchiveSum.Length);
        Assert.All(EngineNode.ArchiveSum, symbol => Assert.True(Uri.IsHexDigit(symbol)));
    }

    /// <summary>
    /// ДОВОД ЗАМКА В ДОБЫЧЕ NODE — ОБЯЗАТЕЛЬНЫЙ, и проба передаёт его. Это не придирка:
    /// в пробе установки замок уже был прочитан и в комментарии стояло «решать, годится ли Node,
    /// без замка нельзя», а вызов шёл БЕЗ него — и решение принималось по 20, а не по замку.
    /// В ВМ это не заметили: подставленный Node (18.20.4) был ниже ОБОИХ порогов.
    /// </summary>
    [Fact]
    public void Довод_замка_в_добыче_Node_обязателен_и_проба_его_передаёт()
    {
        // (1) Забыть довод нельзя: параметр замка — не необязательный и стоит ДО дверей.
        var parameters = typeof(EngineNodeInstall).GetMethod(nameof(EngineNodeInstall.Ensure))!.GetParameters();
        var index = Array.FindIndex(parameters, parameter => parameter.ParameterType == typeof(EngineTreeLock));

        Assert.True(index >= 0, "в добыче Node нет довода замка — решение пойдёт по 20");
        Assert.False(parameters[index].IsOptional, "довод замка снова необязательный: его забудут");

        // И он НЕ может быть пустой ссылкой: «замок молчит» — это EngineTreeLock.Empty, а забыть
        // довод тогда нечем (замечание владельца 10.10.2026).
        var nullability = new System.Reflection.NullabilityInfoContext().Create(parameters[index]);

        Assert.Equal(System.Reflection.NullabilityState.NotNull, nullability.ReadState);
        Assert.Equal(System.Reflection.NullabilityState.NotNull, nullability.WriteState);
        Assert.True(
            index < Array.FindIndex(parameters, parameter => parameter.ParameterType == typeof(EngineNodeDoors)),
            "замок стоит после дверей — значит его можно не заметить");

        // (2) И проба передаёт ИМЕННО замок, а не пустоту.
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var path = Path.Combine(root!, "src", "DshPanel", "Server", "EngineInstallSelfTest.cs");

        Assert.True(File.Exists(path), "нет исходника пробы: " + path);

        var lines = File.ReadAllLines(path);
        var call = Array.FindIndex(lines, line => line.Contains("EngineNodeInstall.Ensure(", StringComparison.Ordinal));

        Assert.True(call >= 0, "проба не зовёт добычу Node вовсе");

        // Берём ВЕСЬ вызов целиком (до закрывающей «);») и сверяем ЧЕТВЁРТЫЙ довод.
        // ⚠️ Прежняя редакция искала «lockFile» в любых десяти строках после вызова: её
        // удовлетворял комментарий или вложенный `Tree(lockFile)`, а на росте списка доводов
        // она краснела на ровном месте. Сверка ИМЕННО довода — то, что здесь обещано.
        var callLines = new List<string>();

        for (var cursor = call; cursor < lines.Length; cursor++)
        {
            callLines.Add(lines[cursor]);

            if (lines[cursor].TrimEnd().EndsWith(");", StringComparison.Ordinal)) break;
        }

        var body = string.Join(' ', callLines);
        var open = body.IndexOf("Ensure(", StringComparison.Ordinal);
        var close = body.LastIndexOf(')');

        Assert.True(open >= 0, "не нашёл вызов Ensure в пробе");
        open = body.IndexOf('(', open);
        Assert.True(close > open, "не разобрал вызов добычи Node в пробе");

        var inside = body[(open + 1)..close];
        var arguments = new List<string>();
        var depth = 0;
        var current = new System.Text.StringBuilder();

        foreach (var symbol in inside)
        {
            if (symbol is '(' or '{' or '[') depth++;
            if (symbol is ')' or '}' or ']') depth--;

            if (symbol == ',' && depth == 0)
            {
                arguments.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(symbol);
        }

        arguments.Add(current.ToString().Trim());

        Assert.True(arguments.Count >= 4, "у вызова добычи Node меньше четырёх доводов");

        Assert.Equal("lockFile", arguments[3]);
    }
    /// <summary>
    /// ПОРОГ NODE — ИЗ ДАННЫХ ЗАМКА, И ЭТО ОДНА МЕРКА ДЛЯ СИСТЕМНОГО И СВОЕГО (решение владельца
    /// 10.10.2026, коммит `7265d9d`). **20 остаётся нижней границей, только когда замок молчит.**
    /// Живой прогон: Node `22.11.0` при замке, объявляющем `>= 22.19.0`, обязан быть отвергнут —
    /// константа 20 этого не ловит НИКОГДА.
    /// </summary>
    [Fact]
    public void Порог_Node_берётся_из_замка_а_двадцатка_только_нижняя_граница()
    {
        var lockFile = EngineTreeLock.Load();

        Assert.True(lockFile.Any, "встроенный замок не прочитался");

        // Порог замка — из данных, и он ВЫШЕ двадцатки (в замке есть >= 22.19.0).
        var required = EngineNode.RequiredFrom(lockFile);

        Assert.Equal(EngineNode.StrictestFrom(lockFile), required);
        Assert.True(
            EngineNode.Compare(required, "20.0.0") > 0,
            $"порог замка «{required}» не выше 20 — проверять нечего");

        // ⚠️ ОДНА МЕРКА для системного и для своего: 22.11.0 порог НЕ проходит…
        Assert.False(EngineNode.Fits("22.11.0", lockFile));
        Assert.False(EngineNode.Fits("v22.11.0", lockFile));

        // …а закреплённая проходит.
        Assert.True(EngineNode.Fits(EngineNode.Release, lockFile));

        // ЗАМОК МОЛЧИТ — остаётся нижняя граница 20, и только она.
        Assert.Equal("20.0.0", EngineNode.RequiredFrom(null));
        Assert.True(EngineNode.Fits("20.5.0", null));
        Assert.False(EngineNode.Fits("19.9.9", null));

        // И два вывода замеров объявлены в коде, а не только в разговоре.
        Assert.True(EngineUpdatePolicy.AskAlwaysBeforeUpdate, "«спрашивать ВСЕГДА» выключено — признака занятости у панели нет");
        Assert.True(EngineUpdatePolicy.SafetyCopyMustBeWhole, "«полуархива не бывает» выключено");
    }

    /// <summary>
    /// ПОРОГ NODE БЕРЁТСЯ ИЗ САМОГО ЗАМКА, А НЕ ИЗ ГОЛОВЫ. Живой прогон в ВМ 10.10.2026: свой Node
    /// был `22.11.0`, а пакет замка `undici@8.11.2` объявляет `>= 22.19.0` — установка шла
    /// с извещениями `EBADENGINE`. Здесь проверяется и разбор условий, и то, что ЗАКРЕПЛЁННАЯ
    /// версия этот порог проходит, а прежняя (22.11.0) — НЕ проходит.
    /// </summary>
    [Fact]
    public void Свой_Node_годится_для_замка()
    {
        // Разбор объявлений: нижние границы берутся, верхние и «*» — нет.
        Assert.Equal("22.19.0", EngineNode.LowerBound(">=22.19.0"));
        Assert.Equal("22.0.0", EngineNode.LowerBound(">22"));
        Assert.Equal("22.11.0", EngineNode.LowerBound("^22.11.0"));
        Assert.Equal("20.0.0", EngineNode.LowerBound("~20"));
        Assert.Equal("18.17.0", EngineNode.LowerBound(">=18.17.0 <21"));
        Assert.Equal("16.0.0", EngineNode.LowerBound("16.x"));
        Assert.Equal(string.Empty, EngineNode.LowerBound("*"));
        Assert.Equal(string.Empty, EngineNode.LowerBound(">=18 || >=20"));
        Assert.Equal(string.Empty, EngineNode.LowerBound(null));

        // Порог замка — самый строгий из объявленных, и он НЕ пуст: замок правда объявляет условие.
        var minimum = EngineNode.StrictestFrom(EngineTreeLock.Load());

        Assert.NotEqual(string.Empty, minimum);

        // ⚠️ Вот та самая проверка: закреплённая версия порог проходит, а прежняя — нет.
        Assert.True(
            EngineNode.Satisfies(EngineNode.Release, minimum),
            $"свой Node {EngineNode.Release} старше того, что требует замок ({minimum})");
        Assert.False(
            EngineNode.Satisfies("22.11.0", minimum),
            "порог замка перестал ловить старую версию Node — проверка ослепла");

        // И общее правило сравнения версий.
        Assert.True(EngineNode.Satisfies("22.23.3", "22.19.0"));
        Assert.False(EngineNode.Satisfies("22.19.0", "22.19.1"));
        Assert.True(EngineNode.Satisfies("22.19.0", "22.19.0"));
        Assert.True(EngineNode.Satisfies("22.0.0", string.Empty));
        Assert.False(EngineNode.Satisfies(string.Empty, "22.0.0"));
    }

    /// <summary>
    /// СУММА БЕРЁТСЯ ИЗ ОФИЦИАЛЬНОГО ФАЙЛА ПО ИМЕНИ АРХИВА, и только по нему: похожее имя
    /// (другая платформа, другой выпуск) не подходит. Строка без имени, короткая сумма и «нашего
    /// имени нет» дают ПУСТУЮ строку — «сверять нечем», а не «сошлось».
    /// </summary>
    [Fact]
    public void Сумма_берётся_из_официального_файла_по_имени()
    {
        var sums =
            "905373a059aecaf7f48c1ce10ffbd5334457ca00f678747f19db5ea7d256c236  node-v22.11.0-win-x64.zip\n" +
            "b9ff5a6b6ffb68a0ffec82cc5664ed48247dabbd25ee6d129facd2f65a8ca80d  node-v22.11.0-win-arm64.zip\n" +
            "d2a4fadb1f5e4abc634b6ac16c44cae7c73ffc3dbfe8b92b011d85f2df90f6c1 *node-v22.11.0-win-x64.7z\n" +
            "мусор без имени\n" +
            "1234  node-v22.11.0-win-x86.zip\n";

        Assert.Equal(
            "905373a059aecaf7f48c1ce10ffbd5334457ca00f678747f19db5ea7d256c236",
            EngineNode.SumFrom(sums, "node-v22.11.0-win-x64.zip"));

        // Звёздочка перед именем (двоичный режим у coreutils) сверке не мешает.
        Assert.Equal(
            "d2a4fadb1f5e4abc634b6ac16c44cae7c73ffc3dbfe8b92b011d85f2df90f6c1",
            EngineNode.SumFrom(sums, "node-v22.11.0-win-x64.7z"));

        // Чужая платформа и чужой выпуск НЕ подходят, хотя имена похожи.
        Assert.Equal(string.Empty, EngineNode.SumFrom(sums, "node-v22.11.0-win-arm64.7z"));
        Assert.Equal(string.Empty, EngineNode.SumFrom(sums, "node-v22.12.0-win-x64.zip"));

        // А короткая сумма — не сумма: «сверять нечем».
        Assert.Equal(string.Empty, EngineNode.SumFrom(sums, "node-v22.11.0-win-x86.zip"));
        Assert.Equal(string.Empty, EngineNode.SumFrom(null, "node-v22.11.0-win-x64.zip"));
        Assert.Equal(string.Empty, EngineNode.SumFrom(sums, null));
    }

    /// <summary>
    /// СИСТЕМНЫЙ NODE 20+ БЕРЁТСЯ КАК ЕСТЬ — и ничего не скачивается: сеть и 30 МБ даром не нужны.
    /// </summary>
    [Fact]
    public void Системный_Node_годится_и_ничего_не_качается()
    {
        var asked = false;
        var result = EngineNodeInstall.Ensure(
            @"C:\панель\node", @"C:\Program Files\nodejs\node.exe", "v26.9.0",
            EngineTreeLock.Empty,
            new EngineNodeDoors(
                (_, _, _) => { asked = true; return new EngineNodeDownload(true, "нет", string.Empty); },
                (_, _) => { },
                _ => "v26.9.0"));

        Assert.True(result.Ok);
        Assert.Equal(EngineNodeOutcome.SystemSuits, result.Outcome);
        Assert.False(result.Ours, "системный Node не наш — в описи его быть не должно");
        Assert.Equal(@"C:\Program Files\nodejs\node.exe", result.NodePath);
        Assert.False(asked, "панель полезла в сеть, хотя системный Node годится");
    }

    /// <summary>
    /// ⚠️ САМ СЛУЧАЙ ИЗ СЛОВ ВЛАДЕЛЬЦА: **Node есть, но старый (18)** — панель обязана его
    /// отвергнуть и скачать свой. Проверяется и то, что свой Node оказывается в СВОЕЙ папке
    /// этой версии, и что скачивали ОБА файла: официальные суммы, потом архив с этой суммой.
    /// </summary>
    [Fact]
    public void Старый_Node_отвергается_и_качается_свой()
    {
        var folder = NewFolder();

        try
        {
            var downloads = new System.Collections.Generic.List<(string Url, string? Sum)>();

            var result = EngineNodeInstall.Ensure(
                folder, @"C:\Program Files\nodejs\node.exe", "v18.20.4",
                EngineTreeLock.Empty,
            new EngineNodeDoors(
                    (url, path, sum) =>
                    {
                        downloads.Add((url, sum));
                        File.WriteAllText(path, url.Contains("SHASUMS") ? SumsFor(EngineNode.ArchiveSum) : "архив");
                        return new EngineNodeDownload(true, path, string.Empty);
                    },
                    (_, target) => Directory.CreateDirectory(target),
                    path => path.Contains(EngineNode.Release, StringComparison.Ordinal) ? "v22.11.0" : "v18.20.4"),
                null);

            Assert.True(result.Ok, result.Detail);
            Assert.Equal(EngineNodeOutcome.Ready, result.Outcome);
            Assert.True(result.Ours, "скачанный Node наш — он обязан попасть в опись");
            Assert.Equal("v22.11.0", result.Version);
            Assert.Equal(EngineNode.ExePath(folder), result.NodePath);
            Assert.Contains(EngineNode.Release, result.NodePath, StringComparison.Ordinal);

            // Два скачивания: файл сумм (без суммы — её неоткуда взять) и архив ЭТОЙ суммой.
            Assert.Equal(2, downloads.Count);
            Assert.Equal(EngineNode.SumsUrl(), downloads[0].Url);
            Assert.Null(downloads[0].Sum);
            Assert.Equal(EngineNode.ArchiveUrl(), downloads[1].Url);
            Assert.Equal(EngineNode.ArchiveSum, downloads[1].Sum);
        }
        finally
        {
            Files.TreeDeletion.Remove(folder);
        }
    }

    /// <summary>
    /// ПОДМЕНЁННЫЙ НАБОР И НЕИЗВЕСТНЫЙ АРХИВ — ОТКАЗ, и до скачивания самого архива: сеть и 30 МБ
    /// не тратятся на набор, которому панель не верит. Каждый отказ назван своим ключом.
    /// </summary>
    [Fact]
    public void Подменённая_сумма_и_неизвестный_архив_останавливают_до_загрузки()
    {
        var folder = NewFolder();

        try
        {
            var archiveAsked = false;

            EngineNodeDoors Doors(string sums)
            {
                return new EngineNodeDoors(
                    (url, path, _) =>
                    {
                        if (url.Contains("SHASUMS"))
                        {
                            File.WriteAllText(path, sums);
                            return new EngineNodeDownload(true, path, string.Empty);
                        }

                        archiveAsked = true;
                        return new EngineNodeDownload(true, path, string.Empty);
                    },
                    (_, target) => Directory.CreateDirectory(target),
                    _ => "v22.11.0");
            }

            // (а) официальный файл называет ДРУГУЮ сумму — набор подменён.
            var swapped = EngineNodeInstall.Ensure(folder, string.Empty, string.Empty, EngineTreeLock.Empty,
                Doors(SumsFor(new string('a', 64))), null);

            Assert.False(swapped.Ok);
            Assert.Equal(EngineInstallRefusals.NodeSumMismatch, swapped.RefusalKey);
            Assert.False(archiveAsked, "архив качали, хотя сумма уже разошлась");

            // (б) нашего архива в официальном файле нет — сверять нечем.
            var missing = EngineNodeInstall.Ensure(folder, string.Empty, string.Empty, EngineTreeLock.Empty,
                Doors("0000000000000000000000000000000000000000000000000000000000000000  node-v22.11.0-win-arm64.zip\n"), null);

            Assert.False(missing.Ok);
            Assert.Equal(EngineInstallRefusals.NodeSumMissing, missing.RefusalKey);
            Assert.False(archiveAsked);

            // (в) официальный файл не прочитался вовсе.
            var noSums = EngineNodeInstall.Ensure(folder, string.Empty, string.Empty,
                EngineTreeLock.Empty,
            new EngineNodeDoors((_, _, _) => EngineNodeDownload.Failed("HTTP 404"), (_, _) => { }, _ => string.Empty), null);

            Assert.False(noSums.Ok);
            Assert.Equal(EngineInstallRefusals.NodeSourceFailed, noSums.RefusalKey);
        }
        finally
        {
            Files.TreeDeletion.Remove(folder);
        }
    }

    /// <summary>
    /// СКАЧАННЫЙ И РАСПАКОВАННЫЙ NODE ОБЯЗАН ЗАПУСТИТЬСЯ И ОТВЕТИТЬ ВЕРСИЕЙ: «скачалось» — не
    /// «готово». Сломанный или старый распакованный Node — отказ словами, а не тихая попытка
    /// поставить им движок.
    /// </summary>
    [Theory]
    [InlineData("v18.20.4")]
    [InlineData("")]
    public void Сломанный_или_старый_распакованный_Node_не_годится(string version)
    {
        var folder = NewFolder();

        try
        {
            var result = EngineNodeInstall.Ensure(
                folder, string.Empty, string.Empty,
                EngineTreeLock.Empty,
            new EngineNodeDoors(
                    (url, path, _) =>
                    {
                        File.WriteAllText(path, url.Contains("SHASUMS") ? SumsFor(EngineNode.ArchiveSum) : "архив");
                        return new EngineNodeDownload(true, path, string.Empty);
                    },
                    (_, target) => Directory.CreateDirectory(target),
                    _ => version),
                null);

            Assert.False(result.Ok);
            Assert.Equal(EngineInstallRefusals.NodeBroken, result.RefusalKey);
            Assert.DoesNotContain("{", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Files.TreeDeletion.Remove(folder);
        }
    }

    /// <summary>
    /// СВОЙ NODE УЖЕ ЛЕЖИТ И ГОДИТСЯ — ВТОРОЙ РАЗ НЕ КАЧАЕМ: сеть и 30 МБ даром не нужны, а версия
    /// папки проверяется у самого <c>node.exe</c>, а не по имени каталога.
    /// </summary>
    [Fact]
    public void Свой_Node_рядом_не_качается_повторно()
    {
        var folder = NewFolder();

        try
        {
            var exe = EngineNode.ExePath(folder);
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "наш node");

            var asked = false;
            var result = EngineNodeInstall.Ensure(
                folder, string.Empty, string.Empty,
                EngineTreeLock.Empty,
            new EngineNodeDoors(
                    (_, _, _) => { asked = true; return new EngineNodeDownload(true, "нет", string.Empty); },
                    (_, _) => { },
                    _ => "v22.11.0"),
                null);

            Assert.True(result.Ok);
            Assert.True(result.Ours);
            Assert.Equal(exe, result.NodePath);
            Assert.False(asked, "панель качала Node заново, хотя свой уже лежит и годится");
        }
        finally
        {
            Files.TreeDeletion.Remove(folder);
        }
    }

    private static string SumsFor(string sum) => sum + "  " + EngineNode.ArchiveName() + "\n";

    private static string NewFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "dsh-node-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);

        return path;
    }
}
