using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DshPanel.Localization;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПУЛЬТ S1+S2 В EXE: оболочка и ассеты вложены в сборку, слова — из словарей панели, и СПИСОК СЛОВ
/// у калитки и у веба ОДИН.
///
/// **От какого случая.** Пульт живёт в двух языках сразу: TS зовёт ключи (`src\web\src\i18n.ts`),
/// C# кладёт их в `data-boot`. Разойдись списки — экран покажет имя ключа вместо слов, и это
/// не поймает ни `tsc`, ни сборка. Поэтому список сверяется ПРОГОНОМ, а не глазами.
///
/// Мутации, которые обязаны упасть здесь: **«выкинуть ключ из C#-списка»** (сравнение наборов),
/// **«вложить ассеты, но не оболочку»** (<see cref="PhoneWeb.Shell(string, bool, string)"/> вернёт null)
/// и **«оставить в словаре пустой перевод»** (проверка трёх языков).
/// </summary>
public class PhoneWebTests
{
    /// <summary>КЛЮЧИ, КОТОРЫЕ ЗОВЁТ ВЕБ, — из его же исходника. Это и есть вторая сторона сверки.</summary>
    private static IReadOnlyList<string> WebKeys()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var path = Path.Combine(root!, "src", "web", "src", "i18n.ts");

        Assert.True(File.Exists(path), $"нет списка слов веба: {path}");

        var text = File.ReadAllText(path);

        // Все ключи веба — строковые литералы вида "Phone…"; служебные слова (ru/en/zh) сюда
        // не попадают по префиксу, и это намеренно: сверяются только слова экрана.
        return Regex
            .Matches(text, "\"(Phone[A-Za-z0-9]+)\"", RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void Список_слов_пульта_совпадает_с_вебом()
    {
        var web = WebKeys();

        Assert.True(web.Count > 60, $"слов веба нашлось {web.Count} — сравнивать нечего");

        var gate = PhoneWeb.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

        Assert.Equal(web, gate);
    }

    [Fact]
    public void Оболочка_и_ассеты_вложены_в_сборку()
    {
        // Оболочка собирается: язык, вход, устройство подставлены, плейсхолдеров не осталось.
        var html = PhoneWeb.Shell("ru", authed: false, device: string.Empty);

        Assert.False(string.IsNullOrEmpty(html), "оболочка пульта не вложена в сборку");
        Assert.DoesNotContain("__", html, StringComparison.Ordinal);
        Assert.Contains("id=\"root\"", html!, StringComparison.Ordinal);

        // КАРТА — из сборки (`assets.json`), и оба файла из неё — РЕСУРСЫ, которые читаются.
        var map = PhoneWeb.Map;

        Assert.StartsWith(PhoneWeb.AssetPath, map.Js, StringComparison.Ordinal);
        Assert.StartsWith(PhoneWeb.AssetPath, map.Css, StringComparison.Ordinal);

        foreach (var (asset, name) in new[] { ("js", map.Js), ("css", map.Css) })
        {
            var file = name[PhoneWeb.AssetPath.Length..];

            Assert.Contains(file, PhoneWeb.AssetNames);
            Assert.False(string.IsNullOrEmpty(PhoneWeb.Asset(file, out var type)), asset);
            Assert.StartsWith(asset == "js" ? "text/javascript" : "text/css", type, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Каждое_слово_пульта_есть_на_трёх_языках()
    {
        foreach (var language in new[] { "ru", "en", "zh" })
        {
            foreach (var key in PhoneWeb.Keys)
            {
                var text = Loc.TIn(language, key);

                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}: пустое слово «{key}»");
                Assert.NotEqual(key, text);
            }
        }

        // И значения РАЗНЫЕ хотя бы у одного ключа на трёх языках — иначе «перевод» не перевод.
        var differs = PhoneWeb.Keys.Any(key =>
            !string.Equals(Loc.TIn("ru", key), Loc.TIn("en", key), StringComparison.Ordinal)
            || !string.Equals(Loc.TIn("ru", key), Loc.TIn("zh", key), StringComparison.Ordinal));

        Assert.True(differs, "все слова на трёх языках совпали — похоже, язык не применяется");
    }

    [Fact]
    public void Политика_оболочки_запрещает_чужое_и_не_пускает_рамку()
    {
        Assert.Contains("default-src 'none'", PhoneWeb.Csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", PhoneWeb.Csp, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", PhoneWeb.Csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", PhoneWeb.Csp, StringComparison.Ordinal);
        Assert.DoesNotContain("http", PhoneWeb.Csp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsafe-eval", PhoneWeb.Csp, StringComparison.OrdinalIgnoreCase);
    }
}
