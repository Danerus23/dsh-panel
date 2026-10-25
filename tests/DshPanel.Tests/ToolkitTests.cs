using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ИНСТРУМЕНТАРИЙ ПАНЕЛИ — ТОЛЬКО ОФИЦИАЛЬНОЕ И ТОЛЬКО ЖИВОЕ (этап 2.4.2, задача D0).
///
/// **От какого случая.** В TT1 (24.10.2026) в дерево был подключён `Avalonia.Controls.DataGrid`
/// 12.1.2. Вечером того же дня владелец сказал: DataGrid не используем — продукт объявлен
/// устаревшим (страница `docs.avaloniaui.net/controls/data-display/structured-data/datagrid`
/// помечена **Deprecated**, открыта 24.10.2026), и разработчики его больше не поддерживают.
/// Замена названа и в этот этап НЕ входит: `TableView` — часть ядра `Avalonia.Controls` 12.1.3
/// (отдельного пакета и отдельной лицензии не требует), применяется этапом 2.4.3.
///
/// **Что стерегут проверки.** «Сняли» — это факт, который обязан быть проверяемым, а не
/// обещанием в комментарии: пакета нет ни в `Directory.Packages.props`, ни в ссылках проектов,
/// ни в ОБОИХ замках, ни в раздаче; и в props не появилось НИ ОДНОГО нового пакета взамен.
/// Запреты TT1 остаются в силе: `TreeDataGrid` (лицензируемый — nuspec тянет
/// `AvaloniaUI.Licensing` 3.1.3), сторонние DevTools и официальный `Avalonia.Diagnostics`
/// (под ветку 12 не выпущен вовсе).
///
/// **Мутации, которые обязаны упасть:**
/// M1 «вернуть `PackageVersion` в props» → красная <see cref="Таблицы_DataGrid_в_дереве_нет"/>
/// (если правку не сопровождать замком — ещё и `NU1004` на `restore --force --locked-mode`);
/// M2 «вернуть `PackageReference` в проект» → красная та же проверка;
/// M3 «подложить `Avalonia.Controls.DataGrid.dll` в публикацию» → красная
/// <see cref="Публикация_без_DataGrid_и_без_DevTools"/>.
/// </summary>
public class ToolkitTests
{
    /// <summary>
    /// СНЯТЫЙ ПАКЕТ: имени не должно быть ни в props, ни в проектах, ни в замках, ни в раздаче.
    /// Вернуть его можно только новым словом владельца — проверка валит и молчаливый возврат.
    /// </summary>
    private const string Removed = "Avalonia.Controls.DataGrid";

    /// <summary>Каркас, на котором снят замер. Версия берётся из props, а не литералом.</summary>
    private const string Framework = "Avalonia";

    /// <summary>Проекты, которые читаются сторожем: и ссылки, и замки у каждого свои.</summary>
    private static readonly string[] Projects = { "src/DshPanel/DshPanel.csproj", "tests/DshPanel.Tests/DshPanel.Tests.csproj" };

    /// <summary>Оба замка репозитория: свой у приложения, свой у проверок.</summary>
    private static readonly string[] Locks = { "src/DshPanel/packages.lock.json", "tests/DshPanel.Tests/packages.lock.json" };

    /// <summary>
    /// ПОЛНЫЙ СПИСОК ОБЪЯВЛЕННЫХ ПАКЕТОВ (CPM — версия ровно одно место, `Directory.Packages.props`).
    /// Перечислен по именам, а не «сколько получится»: этап 2.4.2 не добавляет НИ ОДНОГО пакета,
    /// поэтому список обязан совпасть целиком (порядок не важен). Новая строка здесь — это новое
    /// решение владельца, а не рядовая правка; так же ловится и молчаливое снятие чужого пакета.
    /// </summary>
    private static readonly string[] Declared =
    {
        "Avalonia",
        "Avalonia.Fonts.Inter",
        "Avalonia.HarfBuzz",
        "Avalonia.Headless",
        "Avalonia.Headless.XUnit",
        "Avalonia.Skia",
        "Avalonia.Themes.Fluent",
        "Avalonia.Win32",
        "Microsoft.NET.Test.Sdk",
        "QRCoder",
        "xunit.runner.visualstudio",
        "xunit.v3",
    };

    /// <summary>
    /// Пакеты, которые панель вправе держать, кроме официальных AvaloniaUI: каждый выбран замером
    /// (QRCoder — MIT, managed, офлайн; тестовые — xunit и его хост). Список — БЕЛЫЙ: всё, чего
    /// в нём нет и что не начинается на `Avalonia`, считается сторонним и валит проверку.
    /// </summary>
    private static readonly string[] Allowed = { "QRCoder", "Microsoft.NET.Test.Sdk", "xunit.v3", "xunit.runner.visualstudio" };

    /// <summary>
    /// ЗАПРЕЩЁННЫЕ ИМЕНА — решением владельца 24.10.2026 («только официальное, сторонние DevTools
    /// не берём ни в каком виде»). Перечень назван по именам, а не «похожестью»:
    ///
    /// * `Avalonia.Diagnostics` — официального под ветку 12 нет, а пока его нет, безусловная
    ///   ссылка на него была бы ровно тем, что владелец запретил;
    /// * `InProcess.DevTools`, `ProDiagnostics`, `AvaloniaSpy`, `Chrome.DevTools.*` — сторонние
    ///   отладчики интерфейса, запрещены поимённо;
    /// * `AvaloniaUI.Licensing` — лицензируемый контрол, а не MIT: именно из-за него отказано
    ///   `TreeDataGrid` (решение владельца 24.10.2026, подтверждено 24.10.2026 по nuspec 12.3.2).
    /// </summary>
    private static readonly string[] Forbidden =
    {
        "Avalonia.Diagnostics",
        "InProcess.DevTools",
        "ProDiagnostics",
        "AvaloniaSpy",
        "Chrome.DevTools",
        "AvaloniaUI.Licensing",
    };

    private static string Root()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        // Диагностика, а не ожидание для показа: строка печатается только когда проверка уже
        // упала, и путь машины в ней никому не показывается (то же правило, что у
        // `TestPathMaskTests`: `Assert.Fail` из ворот исключён).
        if (string.IsNullOrEmpty(root))
            Assert.Fail($"не нашёл корень репозитория: вверх от «{AppContext.BaseDirectory}» нет файла DshPanel.slnx");

        return root;
    }

    private static XDocument Xml(string path) => XDocument.Load(path);

    private static string At(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Версии из `Directory.Packages.props` — ОДНО место, где живут версии (CPM).</summary>
    private static Dictionary<string, string> Versions(string root) =>
        Xml(Path.Combine(root, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Where(e => e.Attribute("Include") is not null)
            .ToDictionary(
                e => e.Attribute("Include")!.Value,
                e => e.Attribute("Version")?.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

    /// <summary>Ссылки проекта — только имена: версия в проекте не пишется (центральное управление).</summary>
    private static List<XElement> References(string path) =>
        Xml(path).Descendants("PackageReference").Where(e => e.Attribute("Include") is not null).ToList();

    /// <summary>Запись пакета в замке: `resolved` и `type` для названного пакета (или `null`).</summary>
    private static (string Resolved, string Type)? Locked(string lockPath, string package)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
        foreach (var target in document.RootElement.GetProperty("dependencies").EnumerateObject())
        {
            if (!target.Value.TryGetProperty(package, out var entry)) continue;

            return (
                entry.TryGetProperty("resolved", out var resolved) ? resolved.GetString() ?? string.Empty : string.Empty,
                entry.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty : string.Empty);
        }

        return null;
    }

    /// <summary>
    /// DATAGRID СНЯТ, И ЭТО ВИДНО ВО ВСЕХ ЧЕТЫРЁХ МЕСТАХ: props, проекты, оба замка. Новых
    /// пакетов взамен не появилось, каркас не поехал, запрещённый `TreeDataGrid` по-прежнему не взят.
    ///
    /// **Почему проверка, а не слово в комментарии.** «Убрать пакет» — это три строки в трёх
    /// файлах плюс перезапись двух замков, и глазами приёмки разница между «снят» и «снят из
    /// props, но остался ссылкой» не видна. Слово владельца 24.10.2026 (вечер): DataGrid
    /// не используем; замена — `TableView` из ЯДРА 12.1.3, этапом 2.4.3, в этот этап ничего
    /// не добавляется. Потому набор объявленных пакетов сверяется ЦЕЛИКОМ: добавка сюда — это
    /// новое решение владельца, а не рядовая правка.
    ///
    /// **Замок ищется по ТЕКСТУ файла, а не по одному разделу:** в замке тестов та же строка
    /// живёт ещё и в зависимостях проекта `dshpanel`, поэтому разбор одного раздела оставил бы
    /// половину слепой (замер до снятия: 26 записей в замке приложения и 49 в замке проверок).
    ///
    /// **Мутации, которые обязаны упасть:** M1 — вернуть `PackageVersion` в props; M2 — вернуть
    /// `PackageReference` в проект. Обе валят эту проверку по имени, а не «по количеству».
    /// </summary>
    [Fact]
    public void Таблицы_DataGrid_в_дереве_нет()
    {
        var root = Root();
        var versions = Versions(root);

        // 1. props: объявления НЕТ.
        Assert.False(
            versions.ContainsKey(Removed),
            $"в Directory.Packages.props снова объявлен {Removed} — он снят словом владельца 24.10.2026 (Deprecated)");

        // 1б. и НИ ОДНОГО нового пакета: набор объявленного совпадает с названным списком.
        Assert.Equal(
            Declared.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
            versions.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

        // 2. Проекты: ссылки НЕТ; и каждая ссылка объявлена в props (иначе центральное управление
        //    обойдено, а версия в проекте запрещена).
        foreach (var project in Projects)
        {
            var references = References(At(root, project));
            Assert.False(references.Count == 0, $"в {project} не прочиталось ни одной ссылки — сторож ослеп");

            Assert.DoesNotContain(
                references,
                e => string.Equals(e.Attribute("Include")!.Value, Removed, StringComparison.OrdinalIgnoreCase));

            foreach (var reference in references)
            {
                var name = reference.Attribute("Include")!.Value;
                Assert.True(versions.ContainsKey(name), $"пакет «{name}» не объявлен в Directory.Packages.props — версия в проекте не пишется");
                Assert.Null(reference.Attribute("Version"));
            }
        }

        // 3. Замки: записи НЕТ НИ В ОДНОМ разделе.
        foreach (var lockFile in Locks)
        {
            var path = At(root, lockFile);
            Assert.True(File.Exists(path), $"нет замка {lockFile} — restore --locked-mode доказывать нечем");

            var text = File.ReadAllText(path);
            Assert.Contains("\"dependencies\"", text); // замок прочитан, а не пуст
            Assert.DoesNotContain($"\"{Removed}\"", text, StringComparison.OrdinalIgnoreCase);
        }

        // 4. Каркас не поехал: версия в props и в замке приложения — одна.
        var framework = versions[Framework];
        Assert.Equal("12.1.3", framework); // сторож: замер снят на этой линии

        var baseline = Locked(At(root, "src/DshPanel/packages.lock.json"), Framework);
        Assert.True(baseline is not null, $"в замке src\\DshPanel нет {Framework}");
        Assert.Equal(framework, baseline!.Value.Resolved);

        // 5. ВТОРАЯ ТАБЛИЦА названа и НЕ подключена — запрет TT1 сохранён (блокер лицензионный:
        //    nuspec 12.3.2 тянет `AvaloniaUI.Licensing` 3.1.3 и тега `license` не имеет).
        Assert.DoesNotContain(versions.Keys, id => id.Contains("TreeDataGrid", StringComparison.OrdinalIgnoreCase));
        foreach (var project in Projects)
        {
            Assert.DoesNotContain(
                References(At(root, project)),
                e => e.Attribute("Include")!.Value.Contains("TreeDataGrid", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// НИ ОДНОГО СТОРОННЕГО DEVTOOLS И НИ ОДНОГО НЕЗНАКОМОГО ПАКЕТА (сторож TT1, сохранён 2.4.2).
    ///
    /// **Почему проверка, а не слово в комментарии.** Решение владельца «только официальное»
    /// живёт в двух местах — в props и в проектах; добавить туда пакет — одна строка, и заметить
    /// это глазами приёмки нельзя. Проверка читает ОБА места и валит:
    /// * любое имя из <see cref="Forbidden"/> (в том числе `Avalonia.Diagnostics`, которого под 12 нет);
    /// * любое имя, где встречается `DevTools` или `Licensing`;
    /// * любой пакет, который не официальный AvaloniaUI и не из белого списка <see cref="Allowed"/>.
    ///
    /// ⚠️ **Когда появится официальный `Avalonia.Diagnostics` под 12** — тогда и только тогда
    /// эта проверка меняется: вместо запрета имени появляется правило «ссылка только под Debug»
    /// и проверка, что в раздаче Release нет ни одного файла DevTools.
    ///
    /// **Мутация, которая обязана упасть:** добавить в props любой сторонний DevTools-пакет.
    /// </summary>
    [Fact]
    public void Ни_одного_стороннего_DevTools_в_конфигурации()
    {
        var root = Root();

        var declared = Versions(root).Keys.ToList();
        var referenced = References(At(root, Projects[0]))
            .Concat(References(At(root, Projects[1])))
            .Select(e => e.Attribute("Include")!.Value)
            .ToList();

        // Непустота ДО утверждений: пустой список сделал бы обе проверки ниже слепыми.
        Assert.False(declared.Count == 0, "в props не прочиталось ни одного пакета — сторож ослеп");
        Assert.False(referenced.Count == 0, "в проектах не прочиталось ни одной ссылки — сторож ослеп");

        var everything = declared.Concat(referenced).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var name in everything)
        {
            Assert.DoesNotContain(
                Forbidden,
                forbidden => name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase));

            Assert.DoesNotContain("DevTools", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Licensing", name, StringComparison.OrdinalIgnoreCase);

            var official = name.Equals("Avalonia", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Avalonia.", StringComparison.OrdinalIgnoreCase)
                || Allowed.Contains(name, StringComparer.OrdinalIgnoreCase);

            Assert.True(official, $"пакет «{name}» не официальный AvaloniaUI и не из белого списка — стороннее не берём");
        }

        // Сегодняшний факт: официальной диагностики под 12 нет, и имени её в конфигурации быть
        // не должно. Проверка выше это уже запрещает, но имя называется отдельно — чтобы отчёт
        // и падение говорили одно и то же.
        Assert.DoesNotContain(everything, name => name.Contains("Avalonia.Diagnostics", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// В РАЗДАЧЕ RELEASE НЕТ НИ DATAGRID, НИ ФАЙЛА DEVTOOLS — и это не «на пустом каталоге»:
    /// рядом обязаны быть сами сборки Avalonia (этап 2.4.2, D0).
    ///
    /// **На чём.** На настоящей публикации задачи: `upd-build\242\publish-check-flat` — та же
    /// сборка, что уезжает людям, но без склейки в один файл, чтобы ВИДЕТЬ перечень сборок
    /// (склейка прячет имена, поэтому на одном `DshPanel.exe` «нет DataGrid.dll» не доказывается).
    ///
    /// ⚠️ **Сборки нет — проверка не выдумывает её наличие:** публикация лежит вне репозитория
    /// (`upd-build`, он в `.gitignore`), и на чистом клоне её нет. Тогда проверяются только факты
    /// конфигурации выше, а перечень файлов доказывается прогоном публикации и называется
    /// в отчёте задачи. Так честнее, чем «зелено» на пустом каталоге.
    ///
    /// **Мутация, которая обязана упасть:** M3 — подложить `Avalonia.Controls.DataGrid.dll`
    /// в публикацию; так же падает подложенный файл DevTools.
    /// </summary>
    [Fact]
    public void Публикация_без_DataGrid_и_без_DevTools()
    {
        var root = Root();

        var single = Path.Combine(root, "upd-build", "242", "publish-check");
        var flat = Path.Combine(root, "upd-build", "242", "publish-check-flat");

        if (!Directory.Exists(single) && !Directory.Exists(flat)) return; // см. комментарий: на чистом клоне доказывать нечего

        static string[] Files(string directory) =>
            Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetFileName(path))
                .ToArray();

        if (Directory.Exists(single))
        {
            var files = Files(single);

            // Это ДЕЙСТВИТЕЛЬНО раздача, а не пустой каталог: один exe — так собирает профиль.
            Assert.Contains("DshPanel.exe", files);

            // ⚠️ Ищем ИМЕННО `Avalonia.Diagnostics` и `DevTools`, а НЕ любое «Diagnostics»:
            // в самодостаточной раздаче законно лежат библиотеки самого .NET
            // (`System.Diagnostics.Process.dll` и родня) — это не отладчик интерфейса.
            Assert.DoesNotContain(files, name => name.Contains("Avalonia.Diagnostics", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(files, name => name.Contains("DevTools", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(files, name => name.Contains("AvaloniaUI.Licensing", StringComparison.OrdinalIgnoreCase));
        }

        if (Directory.Exists(flat))
        {
            var files = Files(flat);

            // Раздача на месте И собрана из этого дерева: сборки Avalonia лежат рядом...
            Assert.Contains("DshPanel.exe", files);
            Assert.Contains("Avalonia.Controls.dll", files);

            // ...а снятой таблицы среди них НЕТ — ни сборки, ни её ресурсов.
            Assert.DoesNotContain(files, name => name.Contains("Avalonia.Controls.DataGrid", StringComparison.OrdinalIgnoreCase));

            Assert.DoesNotContain(files, name => name.Contains("Avalonia.Diagnostics", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(files, name => name.Contains("DevTools", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(files, name => name.Contains("AvaloniaUI.Licensing", StringComparison.OrdinalIgnoreCase));
        }
    }
}
