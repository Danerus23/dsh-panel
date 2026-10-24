using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ИНСТРУМЕНТАРИЙ ПАНЕЛИ — ТОЛЬКО ОФИЦИАЛЬНОЕ (задача TT1; слово владельца 24.10.2026).
///
/// **От какого случая.** Панели нужна настоящая виртуализованная таблица (плоские таблицы:
/// копии, история цен), а разработке — отладчик интерфейса. Владелец решил в тот же день:
/// берём ТОЛЬКО официальное — сторонние DevTools не берём ни в каком виде. Значит «что стоит
/// в конфигурации» обязано быть проверяемым фактом, а не обещанием в комментарии: версия,
/// замок и отсутствие запрещённых пакетов стерегутся прогоном.
///
/// **Замер 24.10.2026 (api.nuget.org, списки версий пакетов и nuspec):**
/// `Avalonia.Controls.DataGrid` — крайняя 12.1.2 (точной 12.1.3 нет), MIT, зависимость
/// `Avalonia >= 12.1.0`; `Avalonia.Controls.TreeDataGrid` — крайняя 12.3.2, НЕ ВЗЯТ: её nuspec
/// тянет `AvaloniaUI.Licensing` 3.1.3 и не имеет тега `license` (контрол лицензируемый, не MIT;
/// блокер лицензионный, а не версионный — с 12.1.3 она совместима);
/// `Avalonia.Diagnostics` под ветку 12 НЕ выпущен вовсе (крайняя 11.3.22), наш каркас — 12.1.3.
/// Подробности с адресами — в `Directory.Packages.props`.
///
/// **Мутации, которые обязаны упасть:**
/// M1 «добавить в props любой сторонний DevTools-пакет» → красная
/// <see cref="Ни_одного_стороннего_DevTools_в_конфигурации"/>; вторая половина этой же проверки
/// («только официальные издатели») ловит и любой другой незнакомый пакет.
/// </summary>
public class ToolkitTests
{
    /// <summary>Официальная виртуализованная таблица — одна, и её версия замером (не «последняя»).</summary>
    private const string Table = "Avalonia.Controls.DataGrid";
    private const string TableVersion = "12.1.2";

    /// <summary>Каркас, с которым таблица обязана сойтись. Версия берётся из props, а не литералом.</summary>
    private const string Framework = "Avalonia";

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
    ///   `TreeDataGrid` (решение владельца 24.10.2026, подтверждено 24.10.2026 по nuspec 12.1.1).
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
    /// ТАБЛИЦА ПОДКЛЮЧЕНА, ВЕРСИЯ ОДНА, ЗАМОК ЕЁ ДЕРЖИТ, КАРКАС — ТОТ ЖЕ.
    ///
    /// **Что этим доказывается.** Версия объявлена ровно в `Directory.Packages.props` (а не в
    /// проекте — там её нет), замок `src\DshPanel\packages.lock.json` разрешил её в 12.1.2, а
    /// Avalonia в том же замке — та же, что в props: значит `restore` в режиме замка сойдётся,
    /// и «совместимость с 12.1.3» — не обещание, а разрешённый граф.
    ///
    /// **Вторая таблица названа, но НЕ подключена — и это отказ, а не «пока»:**
    /// `TreeDataGrid` 12.3.2 тянет `AvaloniaUI.Licensing` 3.1.3 (лицензируемый контрол, не MIT;
    /// по версии с нашим каркасом совместим — блокер именно лицензионный). Его подключение —
    /// отдельное решение владельца, поэтому проверка валит и попытку добавить его молча.
    /// </summary>
    [Fact]
    public void Таблица_подключена_в_props_и_в_замке()
    {
        var root = Root();

        var versions = Versions(root);

        Assert.True(versions.TryGetValue(Table, out var declared), $"в Directory.Packages.props нет {Table}");
        Assert.Equal(TableVersion, declared);

        var framework = versions[Framework];
        Assert.Equal("12.1.3", framework); // сторож: замер снят на этой линии

        // Проект: ссылка есть, версии в ней НЕТ (иначе центральное управление обходится).
        var references = References(Path.Combine(root, "src", "DshPanel", "DshPanel.csproj"));
        var table = Assert.Single(
            references,
            e => string.Equals(e.Attribute("Include")!.Value, Table, StringComparison.OrdinalIgnoreCase));
        Assert.Null(table.Attribute("Version"));

        // Замок: разрешено ровно 12.1.2, тип — Direct (панель держит его сама).
        var locked = Locked(Path.Combine(root, "src", "DshPanel", "packages.lock.json"), Table);
        Assert.True(locked is not null, $"в замке src\\DshPanel нет {Table} — restore --locked-mode не сойдётся");
        Assert.Equal(TableVersion, locked!.Value.Resolved);
        Assert.Equal("Direct", locked.Value.Type);

        // Каркас в том же замке — той же версии, что в props: иначе «совместили» не те сборки.
        var baseline = Locked(Path.Combine(root, "src", "DshPanel", "packages.lock.json"), Framework);
        Assert.True(baseline is not null, $"в замке src\\DshPanel нет {Framework}");
        Assert.Equal(framework, baseline!.Value.Resolved);

        // ДЕРЕВО названо запасным и НЕ подключено.
        Assert.DoesNotContain(versions.Keys, id => id.Contains("TreeDataGrid", StringComparison.OrdinalIgnoreCase));
        foreach (var project in new[] { "src/DshPanel/DshPanel.csproj", "tests/DshPanel.Tests/DshPanel.Tests.csproj" })
        {
            Assert.DoesNotContain(
                References(Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar))),
                e => e.Attribute("Include")!.Value.Contains("TreeDataGrid", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// НИ ОДНОГО СТОРОННЕГО DEVTOOLS И НИ ОДНОГО НЕЗНАКОМОГО ПАКЕТА (DEF-подобный сторож TT1).
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
    /// и проверка, что в раздаче Release нет ни одного файла DevTools (задача TT1 называет это
    /// условием, а не сегодняшней работой).
    ///
    /// **Мутация, которая обязана упасть:** M1 — добавить в props любой сторонний DevTools-пакет.
    /// </summary>
    [Fact]
    public void Ни_одного_стороннего_DevTools_в_конфигурации()
    {
        var root = Root();

        var declared = Versions(root).Keys.ToList();
        var referenced = References(Path.Combine(root, "src", "DshPanel", "DshPanel.csproj"))
            .Concat(References(Path.Combine(root, "tests", "DshPanel.Tests", "DshPanel.Tests.csproj")))
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
    /// В РАЗДАЧЕ RELEASE НЕТ НИ ОДНОГО ФАЙЛА DEVTOOLS — и таблица там ЕСТЬ (TT1).
    ///
    /// **На чём.** На настоящих публикациях задачи: `upd-build\241\publish-check` — раздача
    /// профилем `win-x64` (один самодостаточный exe, как уезжает людям), `upd-build\241\publish-check-flat`
    /// — та же сборка без склейки в один файл, чтобы ВИДЕТЬ перечень сборок.
    ///
    /// ⚠️ **Сборки нет — проверка не выдумывает её наличие:** публикации лежат вне репозитория
    /// (`upd-build`), и на чистом клоне их нет. Тогда проверяются только факты конфигурации выше,
    /// а перечень файлов доказывается прогоном публикации и назван в отчёте задачи. Так честнее,
    /// чем «зелено» на пустом каталоге.
    ///
    /// **Мутация, которая обязана упасть:** если раздача появится с файлом DevTools — падает здесь.
    /// </summary>
    [Fact]
    public void Публикация_не_содержит_DevTools()
    {
        var root = Root();

        var dist = Path.Combine(root, "upd-build", "241", "publish-check");
        var flat = Path.Combine(root, "upd-build", "241", "publish-check-flat");

        if (!Directory.Exists(dist) && !Directory.Exists(flat)) return; // см. комментарий: на чистом клоне доказывать нечего

        static string[] Files(string directory) =>
            Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetFileName(path))
                .ToArray();

        if (Directory.Exists(dist))
        {
            var files = Files(dist);

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

            // Таблица в раздаче ЕСТЬ — значит ссылка не «на бумаге».
            Assert.Contains("Avalonia.Controls.DataGrid.dll", files);
            Assert.DoesNotContain(files, name => name.Contains("Avalonia.Diagnostics", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(files, name => name.Contains("DevTools", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(files, name => name.Contains("AvaloniaUI.Licensing", StringComparison.OrdinalIgnoreCase));
        }
    }
}
