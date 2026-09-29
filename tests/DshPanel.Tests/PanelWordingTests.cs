using System.Collections.Generic;
using DshPanel.Localization;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки СЛОВ: то, что человек читает в окне, обязано говорить то, что есть.
///
/// Заведены пачкой 27.09.2026 по списку `docs\DESIGN.md` (жалобы владельца, п. 1, 7, 9, 13, 14, 15),
/// и у каждой своя беда, уже случившаяся:
///
/// * **п. 1** — «Сервер не запущен» над карточкой «Найден работающий DSH» читалось как
///   противоречие: формально речь о СВОЁМ сервере панели, но на экране это два разных сервера;
/// * **п. 7** — подпись кнопки «Добавить «каталог ключей Android: …»» спотыкала глаз
///   вложенными кавычками;
/// * **п. 9** — «ссылок-джункций в описи»: по-русски так не говорят;
/// * **п. 13** — два разных «ключа» одним словом: приватные ключи (SSH) и ключ доступа к модели;
/// * **п. 14** — «Примерно 764,4 МБ» при готовом архиве в 514 МБ, потому что число считалось
///   ДО сжатия, а сказано об этом не было;
/// * **п. 15** — тексты посылали читателя в issues ЗАКРЫТОГО репозитория и обещали вперёд,
///   что панель «открыта», притом что адрес репозитория нарочно скрыт (`ProductLinks`).
///
/// Язык берётся через <see cref="Loc.TIn"/>: он не трогает выбранный язык процесса, а значит
/// не мешает проверкам, которые xunit гоняет параллельно.
/// </summary>
public class PanelWordingTests
{
    /// <summary>Языки и слово «панель» на каждом — по нему видно, что строка говорит о СВОЁМ сервере.</summary>
    private static readonly (string Language, string Panel)[] Languages =
    {
        ("ru", "панел"),
        ("en", "panel"),
        ("zh", "面板"),
    };

    private static string Text(string language, string key) => Loc.TIn(language, key);

    /// <summary>
    /// ПЕРВОЕ: строка состояния говорит, о ЧЬЁМ сервере речь. Обе строки одного виджета —
    /// и «не запущен», и «работает» — называют хозяина, иначе рядом с карточкой найденного
    /// сервера они читаются как противоречие (п. 1).
    /// </summary>
    [Fact]
    public void Строка_состояния_сервера_говорит_о_своём_сервере_панели()
    {
        foreach (var (language, panel) in Languages)
        {
            foreach (var key in new[]
                     {
                         nameof(PanelStrings.ServerStopped),
                         nameof(PanelStrings.ServerRunning),
                         nameof(PanelStrings.ServerForeign),
                     })
            {
                var text = Text(language, key);

                Assert.Contains(panel, text, System.StringComparison.OrdinalIgnoreCase);
            }
        }

        // И состояние при этом НЕ потерялось: «свой сервер панели» без «не запущен» ничего
        // не сообщало бы человеку.
        Assert.Contains("не запущен", Text("ru", nameof(PanelStrings.ServerStopped)), System.StringComparison.Ordinal);
        Assert.Contains("not running", Text("en", nameof(PanelStrings.ServerStopped)), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Все три кнопки «Отмена»/«Сохранить» в окне вопроса — те же строки, что и в прочих окнах:
    /// подпись кнопки сохранения берётся у самого окна настроек, а не заводится двойником.
    /// </summary>
    [Fact]
    public void Вопрос_о_несохранённой_правке_подписан_строками_словаря()
    {
        foreach (var (language, _) in Languages)
        {
            foreach (var key in new[]
                     {
                         nameof(PanelStrings.SettingsUnsavedTitle),
                         nameof(PanelStrings.SettingsUnsavedQuestion),
                         nameof(PanelStrings.SettingsUnsavedDiscard),
                         nameof(PanelStrings.SettingsUnsavedCancel),
                     })
            {
                var text = Text(language, key);

                Assert.False(string.IsNullOrWhiteSpace(text), $"пустая строка: {language}/{key}");
                Assert.NotEqual(key, text);
            }
        }
    }

    /// <summary>
    /// ВТОРОЕ: во вложенных кавычках кнопка не читается (п. 7). Кавычек в подписи нет вовсе —
    /// подстановка и без них отделена двоеточием.
    /// </summary>
    [Fact]
    public void Кнопка_добавления_каталога_ключей_без_кавычек()
    {
        char[] quotes = { '«', '»', '"', '“', '”', '„', '\'' };

        foreach (var (language, _) in Languages)
        {
            var text = Text(language, nameof(PanelStrings.BackupKeysAddFormat));
            var withoutPlaceholder = text.Replace("{0}", string.Empty, System.StringComparison.Ordinal);

            // Подстановка на месте: кнопка обязана называть то, что добавит.
            Assert.Contains("{0}", text, System.StringComparison.Ordinal);

            foreach (var quote in quotes)
            {
                Assert.DoesNotContain(quote.ToString(), withoutPlaceholder, System.StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// ТРЕТЬЕ: по-русски «джункций» не говорят (п. 9). en/zh и до правки говорили
    /// «directory links» / «目录链接» — проверка сторожит, чтобы это не вернулось ни на одном языке.
    /// </summary>
    [Fact]
    public void Ссылки_на_каталоги_названы_словами_а_не_джункциями()
    {
        foreach (var (language, _) in Languages)
        {
            var text = Text(language, nameof(PanelStrings.RestoreLinksInManifestFormat));

            Assert.DoesNotContain("джункц", text, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("junction", text, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("{0}", text, System.StringComparison.Ordinal);
        }

        Assert.Contains("каталог", Text("ru", nameof(PanelStrings.RestoreLinksInManifestFormat)), System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ЧЕТВЁРТОЕ: два разных «ключа» названы по-разному (п. 13). В списке копий «с ключом» — это
    /// ключ ДОСТУПА (файл <c>.credentials.yaml</c>), а «приватные ключи» — каталоги SSH;
    /// одно слово на две вещи и путало человека.
    /// </summary>
    [Fact]
    public void Ключ_доступа_в_списке_копий_назван_ключом_доступа()
    {
        Assert.Contains("доступ", Text("ru", nameof(PanelStrings.BackupWithKey)), System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("access", Text("en", nameof(PanelStrings.BackupWithKey)), System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("访问", Text("zh", nameof(PanelStrings.BackupWithKey)), System.StringComparison.Ordinal);

        // И это по-прежнему НЕ про приватные ключи: смешать два имени значило бы вернуть дефект.
        Assert.DoesNotContain("приватн", Text("ru", nameof(PanelStrings.BackupWithKey)), System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ПЯТОЕ: число в окне копий названо тем, чем оно является, — объёмом ДО сжатия (п. 14).
    /// «Примерно 764,4 МБ» при готовом архиве в 514 МБ человек понимал как ошибку панели.
    /// </summary>
    [Fact]
    public void Объём_копии_назван_объёмом_до_сжатия()
    {
        Assert.Contains("сжат", Text("ru", nameof(PanelStrings.BackupScopeEstimateFormat)), System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compress", Text("en", nameof(PanelStrings.BackupScopeEstimateFormat)), System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("压缩", Text("zh", nameof(PanelStrings.BackupScopeEstimateFormat)), System.StringComparison.Ordinal);

        // Подстановка размера на месте — иначе число пропало бы вместе с пояснением.
        foreach (var (language, _) in Languages)
        {
            Assert.Contains("{0}", Text(language, nameof(PanelStrings.BackupScopeEstimateFormat)), System.StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// ШЕСТОЕ: окно «О программе» говорит только то, что верно СЕГОДНЯ (п. 15). Репозиторий 2.0
    /// закрыт, его адрес нарочно пуст (`ProductLinks.RepositoryUrl`), поэтому:
    ///
    /// * нельзя посылать читателя «в issues репозитория» — их нет;
    /// * нельзя обещать, что панель «открыта»: обещание вперёд, а ссылки на исходники в окне нет.
    /// </summary>
    [Fact]
    public void Окно_о_программе_не_шлёт_в_issues_и_не_обещает_открытости()
    {
        foreach (var (language, _) in Languages)
        {
            var note = Text(language, nameof(PanelStrings.AboutTranslationNote));
            var donate = Text(language, nameof(PanelStrings.AboutDonateNote));

            Assert.DoesNotContain("issues", note, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("issue", note, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("议题", note, System.StringComparison.Ordinal);

            Assert.DoesNotContain("open source", donate, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("开源", donate, System.StringComparison.Ordinal);
            Assert.DoesNotContain("и открыта", donate, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// СЕДЬМОЕ, решение владельца 27.09.2026 (п. 20 `docs\DESIGN.md`): **одна вещь — одно имя**.
    ///
    /// Слова владельца: *«в настройках это скорее пункт меню отвечающий за автоматическое
    /// резервное копирование, а в главном панели этот пункт меню отвечающий за восстановление»*.
    /// Отсюда три имени, и каждое называет РАЗНОЕ:
    ///
    /// * раздел настроек — «Резервное копирование»: это ПРОЦЕСС (что панель делает сама);
    /// * дверь в главном окне и заголовок её окна — «Копии и восстановление»: это ДЕЙСТВИЕ
    ///   (снять копию, вернуть данные);
    /// * сама вещь везде называется **копией** — второго имени («резервная копия») в интерфейсе
    ///   больше нет: оно и путало человека.
    /// </summary>
    [Fact]
    public void Раздел_настроек_резервное_копирование_а_дверь_копии_и_восстановление()
    {
        Assert.Equal("Резервное копирование", Text("ru", nameof(PanelStrings.BackupSectionTitle)));
        Assert.Equal("Backup", Text("en", nameof(PanelStrings.BackupSectionTitle)));
        Assert.Equal("备份", Text("zh", nameof(PanelStrings.BackupSectionTitle)));

        Assert.Equal("Копии и восстановление…", Text("ru", nameof(PanelStrings.BackupsButton)));
        Assert.Contains(
            "Копии и восстановление",
            Text("ru", nameof(PanelStrings.BackupWindowTitle)),
            System.StringComparison.Ordinal);

        // Заголовок ВНУТРИ окна копий — то же слово «копии», без второго имени.
        Assert.Equal("Копии", Text("ru", nameof(PanelStrings.BackupHeading)));

        // Дверь и заголовок окна называют одно и то же ДЕЙСТВИЕ, и на трёх языках оно не
        // расходится: en говорит «backups and restore», zh — «备份与恢复».
        Assert.Contains("restore", Text("en", nameof(PanelStrings.BackupsButton)), System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("恢复", Text("zh", nameof(PanelStrings.BackupsButton)), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// ВТОРОГО ИМЕНИ КОПИИ В ИНТЕРФЕЙСЕ НЕТ. «Резервная копия» и «копия» — одна вещь, и два имени
    /// для неё человек и читал как разные вещи (п. 20). Исключение ровно одно: НАЗВАНИЕ РАЗДЕЛА
    /// настроек — «Резервное копирование»: это процесс, а не вещь, и оно названо владельцем.
    ///
    /// Проверяются ВСЕ строки всех трёх словарей, а не только те, что помнили: следующая строка
    /// со словом «резервн» заведётся не сейчас, а через месяц — и её поймает именно этот прогон.
    /// </summary>
    [Fact]
    public void Второго_имени_копии_в_интерфейсе_нет()
    {
        var twins = new List<string>();

        foreach (var (language, _) in Languages)
        {
            foreach (var (key, value) in Loc.Dictionary(language))
            {
                // Исключение названо владельцем и стоит здесь явным именем ключа.
                if (key == nameof(PanelStrings.BackupSectionTitle)) continue;
                if (value is null) continue;

                if (System.Text.RegularExpressions.Regex.IsMatch(
                        value, @"резервн|\breserve", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    twins.Add($"{language}/{key}: {value}");
                }
            }
        }

        Assert.True(
            twins.Count == 0,
            "в интерфейсе снова два имени одной вещи — «копия» и «резервная копия». " +
            "Решение владельца 27.09.2026: везде «копия», а «резервное копирование» — только " +
            "название РАЗДЕЛА настроек:" + Environment.NewLine + string.Join(Environment.NewLine, twins));
    }

    /// <summary>
    /// В ИНТЕРФЕЙСЕ НЕТ СЛОВА «НАКАТ». Решение владельца 27.09.2026: людям показываем
    /// «восстановить / восстановление», а «накат» — внутренний язык проекта (в документах он
    /// остаётся). Слово это читается как жаргон ровно там, где человек решает, вернуть ли данные.
    /// </summary>
    [Fact]
    public void Слова_накат_в_интерфейсе_нет()
    {
        var leftovers = new List<string>();

        foreach (var (language, _) in Languages)
        {
            foreach (var (key, value) in Loc.Dictionary(language))
            {
                if (value is null) continue;

                if (value.Contains("накат", System.StringComparison.OrdinalIgnoreCase))
                {
                    leftovers.Add($"{language}/{key}: {value}");
                }
            }
        }

        Assert.True(
            leftovers.Count == 0,
            "в интерфейсе осталось слово «накат» — решение владельца: только «восстановление»:" +
            Environment.NewLine + string.Join(Environment.NewLine, leftovers));
    }
}
