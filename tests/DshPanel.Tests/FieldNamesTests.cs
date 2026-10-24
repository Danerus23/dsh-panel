using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using DshPanel.Server;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// У КАЖДОГО ПОЛЯ ПАНЕЛИ ЕСТЬ ДОСТУПНОЕ ИМЯ, РАВНОЕ ЕГО ВИДИМОЙ ПОДПИСИ (DEF-055, 2.4.1).
///
/// **От какого случая.** У `TextBox`/`ComboBox`/`NumericUpDown` своего имени в UIA нет: соседний
/// `TextBlock` с подписью именем поля не становится. Читалка объявляет такое поле ролью без
/// названия — «поле ввода», «список», «счётчик» (`85-desktop-av.md` §2), и человек, работающий
/// с экранной читалкой, не знает, что именно он вводит (`AV-5`, WCAG 4.1.2). Замер до правки:
/// полей **19** (TextBox 6 / ComboBox 6 / NumericUpDown 7), `LabeledBy` — **0**, `Name` — **0**.
///
/// **ЧЕМ МЕРИТСЯ, и это главное в проверке.** Не `AutomationProperties.GetName(field)`: при
/// `LabeledBy` там лежит СВОЙСТВО, а не выведенное имя, и оно вернёт пусто (поймано пробой
/// `85` §6.2). Мерится **`ControlAutomationPeer.CreatePeerForElement(field).GetName()`** — то,
/// что читалка действительно произнесёт.
///
/// **ЧТО ИМЕННО УТВЕРЖДАЕТСЯ, и оба утверждения обязательны:**
/// 1. у каждого поля `LabeledBy` задан, подпись непуста, а имя равно тексту подписи — иначе имя
///    «дрейфует» от того, что человек видит (`AV-12`, 2.5.3);
/// 2. полей в ПОКАЗАННОМ окне столько же, сколько объявлено в разметке: иначе «все поля названы»
///    зеленело бы на окне, где часть полей не нашлась. Число берётся из разметки, а не пишется
///    в проверке литералом: новый двадцатый TextBox не пройдёт мимо.
public class FieldNamesTests
{
    [AvaloniaFact]
    public void У_каждого_поля_есть_имя_равное_подписи()
    {
        var dir = TempRoots.For("field-names");

        try
        {
            var main = PanelTestStand.MainStand(ServerState.Stopped(0));
            var settings = PanelTestStand.SettingsStand(dir);
            var backups = new BackupWindow();
            var issue = new IssueWindow();

            backups.Attach(new BackupWindowStopFlowTests.StubBackups());

            main.Show();
            settings.Show();
            backups.Show();
            issue.Show();
            PanelTestStand.Settle();

            try
            {
                // КАЖДЫЙ РАЗДЕЛ НАСТРОЕК ПОКАЗЫВАЕТСЯ: подписи ставятся кодом раздела, и раздел,
                // которого не показывали, оставил бы свою подпись пустой — проверка ругалась бы
                // на пустоту, а не на имя.
                foreach (var section in new[]
                         {
                             SettingsWindow.GeneralSection, SettingsWindow.BackupSection,
                             SettingsWindow.BalanceSection, SettingsWindow.ServerSection,
                             SettingsWindow.UpdateSection, SettingsWindow.PhoneSection,
                             SettingsWindow.AboutSection,
                         })
                {
                    settings.ShowSection(section);
                    PanelTestStand.Settle();
                }

                // СЧЁТ ИЗ РАЗМЕТКИ: сколько полей объявлено — столько обязано найтись в окне.
                var counted =
                    AssertNamed(main, "MainWindow.axaml") +
                    AssertNamed(settings, "SettingsWindow.axaml") +
                    AssertNamed(backups, "BackupWindow.axaml") +
                    AssertNamed(issue, "IssueWindow.axaml");

                Assert.True(
                    counted > 0,
                    "не нашлось ни одного поля — проверка мерила бы пустоту");
            }
            finally
            {
                issue.Close();
                backups.Close();
                settings.Close();
                main.Close();
            }
        }
        finally
        {
            TempRoots.Remove(dir);
        }
    }

    /// <summary>
    /// ОТДЕЛЬНО ОТ ИМЁН: СТРОКА ПРИЧИНЫ ОТКАЗА ПОДГОТОВКИ ОБЪЯВЛЯЕТСЯ ЧИТАЛКЕ (DEF-008, `Ш-61`/`AV-8`).
    /// Она меняется ПОСЛЕ шага (успех, отказ, исключение), и без живой области изменение проходит
    /// молча: зрячий видит новую строку, незрячий — ничего. `LiveSetting=Polite` — объявление без
    /// перебивания речи.
    ///
    /// ⚠️ Чего это НЕ доказывает: что Narrator действительно произнесёт строку — живой проход
    /// читалки в прогоне не делается (`AV-13` — 0 проходов), и это названо непокрытым.
    /// </summary>
    [AvaloniaFact]
    public void Причина_отказа_подготовки_объявляется_читалке()
    {
        var dir = TempRoots.For("field-names-live");

        try
        {
            var window = PanelTestStand.SettingsStand(dir);

            window.ShowSection(SettingsWindow.UpdateSection);
            window.Show();
            PanelTestStand.Settle();

            try
            {
                var reason = window.FindControl<TextBlock>("UpdateStatusText");

                Assert.NotNull(reason);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(reason!));
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            TempRoots.Remove(dir);
        }
    }

    /// <summary>
    /// ОДНО УТВЕРЖДЕНИЕ НА ОКНО: каждое найденное поле названо, имя равно подписи, а число найденных
    /// полей равно числу, объявленному в разметке этого окна. Возвращает число полей.
    /// </summary>
    private static int AssertNamed(Window window, string markup)
    {
        // ЛОГИЧЕСКОЕ ДЕРЕВО, А НЕ ВИЗУАЛЬНОЕ: в шаблоне `NumericUpDown` и `ComboBox` есть СВОИ
        // внутренние TextBox/TextBlock, и визуальный перебор нашёл бы их тоже — они не поля
        // разметки, имени им не назначить, а счёт разошёлся бы с markup'ом (проверено прогоном:
        // «в окне 2, в разметке 1»). Поля разметки — логические дети окна.
        var fields = window.GetLogicalDescendants().OfType<Control>()
            .Where(control => control is TextBox or ComboBox or NumericUpDown)
            .ToList();

        var declared = DeclaredFields(markup);

        Assert.True(
            fields.Count == declared,
            $"{markup}: в окне нашлось полей {fields.Count}, а в разметке объявлено {declared} — "
            + "перебор идёт не по всем полям, и «все названы» было бы неправдой");

        foreach (var field in fields)
        {
            var where = $"{markup}, поле «{field.Name}»";
            var label = AutomationProperties.GetLabeledBy(field);

            Assert.True(
                label is not null,
                $"{where}: LabeledBy не задан — читалка назовёт его ролью без имени (DEF-055)");

            var caption = Caption(label!);

            Assert.False(
                string.IsNullOrWhiteSpace(caption),
                $"{where}: подпись пуста — сравнивать имя не с чем");

            // ИМЯ ЧИТАЕТСЯ ТЕМ ЖЕ ПУТЁМ, КАКИМ ЕГО ПРОИЗНОСИТ ЧИТАЛКА (85 §6.2).
            var peer = ControlAutomationPeer.CreatePeerForElement(field);

            Assert.NotNull(peer);

            var accessible = peer!.GetName();

            Assert.False(
                string.IsNullOrWhiteSpace(accessible),
                $"{where}: доступного имени нет — читалка объявит поле безымянным (DEF-055)");

            Assert.Equal(caption, accessible);
        }

        return fields.Count;
    }

    /// <summary>Подпись так, как её видит глаз: текст метки (или её собственное доступное имя).</summary>
    private static string Caption(Control label) => label switch
    {
        TextBlock text => text.Text ?? string.Empty,
        ContentControl content => content.Content as string ?? AutomationProperties.GetName(content) ?? string.Empty,
        _ => AutomationProperties.GetName(label) ?? string.Empty,
    };

    /// <summary>
    /// СКОЛЬКО ПОЛЕЙ ОБЪЯВЛЕНО В РАЗМЕТКЕ — числом из файла, а не литералом в проверке. Новое поле
    /// в разметке сразу требует, чтобы оно нашлось в окне и было названо.
    /// </summary>
    private static int DeclaredFields(string markup)
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var text = File.ReadAllText(Path.Combine(root, "src", "DshPanel", "Views", markup));

        return Occurrences(text, "<TextBox") + Occurrences(text, "<ComboBox") + Occurrences(text, "<NumericUpDown");
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;

        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
