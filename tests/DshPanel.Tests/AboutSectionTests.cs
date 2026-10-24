using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using DshPanel.Localization;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// «О ПРОГРАММЕ» — СЕДЬМОЙ РАЗДЕЛ НАСТРОЕК (u14, 2.4.0). Решение владельца 27.09.2026
/// (`docs\DECISIONS.md:32`: «„о программе“ не туда воткнул. Предлагаю перенести вообще
/// в настройки») исполнено до конца: отдельного ОКНА в панели больше нет, ответ живёт разделом.
///
/// Проверки делятся на два рода, и оба нужны:
/// * **чистые решения** (<see cref="AboutInfo"/>) — номер версии и «показывать ли строку»;
///   они проверяются без окна и ломаются мутацией;
/// * **то, что видно** — раздел собран из строк словаря, «Закрыть» в нём нет, ссылки живые,
///   дверь в отчёт работает. Это проверяется на НАСТОЯЩЕМ окне настроек.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class AboutSectionTests
{
    private static SettingsWindow Stand(string dir)
    {
        var window = PanelTestStand.SettingsStand(dir);
        window.Show();
        PanelTestStand.Settle();

        return window;
    }

    // ------------------------------------------------------------------ чистые решения

    /// <summary>
    /// ВЕРСИЯ — ЧИСЛОВОЙ ЧАСТЬЮ. Хеш коммита дописывает сам .NET, и человеку он ничего не говорит
    /// (правило проекта «версию сравнивать по числовой части»). Мутация «вернуть хеш в строку»
    /// роняет это утверждение.
    /// </summary>
    [Fact]
    public void Версия_показывается_числовой_частью()
    {
        Assert.Equal("2.0.0", AboutInfo.NumericVersion("2.0.0+9f3c1ab1"));
        Assert.Equal("2.0.0", AboutInfo.NumericVersion("  2.0.0  "));
        Assert.Equal(string.Empty, AboutInfo.NumericVersion(null));
        Assert.Equal(string.Empty, AboutInfo.NumericVersion(""));

        Assert.NotEqual(string.Empty, AboutInfo.PanelVersion);
        Assert.DoesNotContain("+", AboutInfo.PanelVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// СТРОКА О МАШИННОМ ПЕРЕВОДЕ — ВСЕМ, КРОМЕ РУССКОГО. Незнакомый язык (пусто) — показываем:
    /// не раскрыть машинный перевод хуже, чем раскрыть его зря. Мутация «показывать всегда»
    /// или «не показывать никогда» роняет перебор.
    /// </summary>
    [Fact]
    public void Строка_о_переводе_зависит_от_языка()
    {
        Assert.False(AboutInfo.ShowsTranslationNote("ru"));
        Assert.False(AboutInfo.ShowsTranslationNote("RU"));
        Assert.True(AboutInfo.ShowsTranslationNote("en"));
        Assert.True(AboutInfo.ShowsTranslationNote("zh"));
        Assert.True(AboutInfo.ShowsTranslationNote(null));
    }

    /// <summary>
    /// РАЗДЕЛ ПРО НОМЕР И СЕДЬМОЙ ПО СЧЁТУ. Номер раздела — ОДНО число на панель
    /// (<see cref="SettingsWindow.AboutSection"/>): им же пользуется кадр и дверь меню значка.
    /// Список подписей — семь, «О программе» последняя (строка дизайн-лидера u14).
    /// </summary>
    [AvaloniaFact]
    public void Раздел_о_программе_седьмой_и_последний()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir);

            var titles = window.FindControl<ListBox>("SectionsList")!.ItemsSource!.Cast<string>().ToList();

            Assert.Equal(7, titles.Count);
            Assert.Equal(PanelStrings.AboutSectionTitle, titles[^1]);
            Assert.Equal(6, SettingsWindow.AboutSection);
            Assert.Equal(SettingsWindow.AboutSection, window.ShowAboutSection());
            Assert.Equal(SettingsWindow.AboutSection, window.VisibleSection);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ------------------------------------------------------------------ что видно в разделе

    /// <summary>
    /// СОДЕРЖАНИЕ РАЗДЕЛА — ОБЯЗАТЕЛЬНЫЙ МИНИМУМ СТРОКИ ДИЗАЙН-ЛИДЕРА: версия, «что делает»,
    /// лицензия MIT, ©. И НИ ОДНОЙ КНОПКИ «ЗАКРЫТЬ»: её орган ушёл вместе с окном, закрытие
    /// раздела/настроек делает само окно.
    ///
    /// Мутация «вернуть кнопку закрытия в раздел» роняет `FindControl`-утверждение; мутация
    /// «не ставить версию» — первое.
    /// </summary>
    [AvaloniaFact]
    public void Содержание_раздела_на_месте_а_закрыть_в_нём_нет()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir);

            window.ShowSection(SettingsWindow.AboutSection);
            PanelTestStand.Settle();

            Assert.Contains(AboutInfo.PanelVersion, window.AboutVersionLine, StringComparison.Ordinal);
            Assert.Equal(PanelStrings.AboutPurpose, window.AboutPurposeLine);
            Assert.Equal(PanelStrings.AboutLicense, window.AboutLicenseLine);
            Assert.Equal(PanelStrings.AboutCopyright, window.AboutCopyrightLine);

            Assert.Equal(PanelStrings.AboutSectionTitle, window.FindControl<TextBlock>("AboutSectionTitle")!.Text);

            // «Закрыть» не существует как орган: имя прежней кнопки окна в разделе не находится.
            Assert.Null(window.FindControl<Control>("AboutCloseButton"));

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ССЫЛКИ — ТОЛЬКО ЖИВЫЕ: строку показывает тот же предикат, что решает это для продукта
    /// (<see cref="ProductLinks.ShouldShow"/>). Кнопки в никуда в продукте не бывает; пустой
    /// адрес значит «возможности нет» — и тогда строки нет вовсе, а не серая кнопка.
    /// </summary>
    [AvaloniaFact]
    public void Ссылки_показываются_только_живые()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir);

            Assert.Equal(ProductLinks.ShouldShow(ProductLinks.RepositoryUrl), window.AboutRepoVisible);
            Assert.Equal(ProductLinks.ShouldShow(ProductLinks.Donate), window.AboutDonateVisible);

            // Строка о переводе — по языку панели: то же чистое решение, что проверено выше.
            Assert.Equal(
                AboutInfo.ShowsTranslationNote(Loc.Language),
                window.AboutTranslationNoteVisible);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ДВЕРЬ В ОТЧЁТ О ПРОБЛЕМЕ: БЕЗ ДВЕРИ БЛОКА НЕТ, С ДВЕРЬЮ — ЕСТЬ, И ЩЕЛЧОК ЗОВЁТ РОВНО ЕЁ.
    /// Кнопка в никуда хуже отсутствующей: человек нажал бы и не понял, почему ничего не произошло.
    ///
    /// Мутация «показывать блок всегда» или «снять подписку на дверь» роняет это утверждение.
    /// </summary>
    [AvaloniaFact]
    public void Блок_о_проблеме_виден_только_с_дверью()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir);

            window.ShowSection(SettingsWindow.AboutSection);
            PanelTestStand.Settle();

            // Двери нет — блока нет.
            window.AttachIssueDoor(null);
            PanelTestStand.Settle();
            Assert.False(window.AboutIssueVisible);

            // Дверь есть — блок есть, и нажатие зовёт ровно её.
            var called = 0;
            window.AttachIssueDoor(() => called++);
            PanelTestStand.Settle();

            Assert.True(window.AboutIssueVisible);

            var button = window.FindControl<Button>("AboutIssueButton");
            Assert.NotNull(button);

            button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, called);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ОТКАЗ ССЫЛКИ ВИДЕН СЛОВАМИ, А НЕ МОЛЧАНИЕМ. Проверка подставляет дверь открытия
    /// (браузер человека ей не нужен), а раздел обязан показать причину СТРОКОЙ у себя.
    /// </summary>
    [AvaloniaFact]
    public void Отказ_открытия_ссылки_виден_строкой()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir);

            // Пустая дверь донатов: нажимаем только при живой ссылке — иначе нажимать нечего.
            if (!ProductLinks.ShouldShow(ProductLinks.Donate))
            {
                window.CloseQuietly();
                return;
            }

            window.AboutOpenLinkForTests = _ => "браузер не открылся";

            window.ClickAboutDonate();

            Assert.Contains("браузер не открылся", window.AboutStatus, StringComparison.Ordinal);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }
}
