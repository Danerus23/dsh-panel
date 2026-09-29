using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DshPanel.Shell;
using DshPanel.Update;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// РАЗДЕЛ «ОБНОВЛЕНИЕ» В ОКНЕ НАСТРОЕК — решение владельца 28.09.2026.
///
/// Слова владельца, ради которых это сделано: *«Я предполагал что это будет просто нормальный пункт
/// меню как и остальные во вкладке настройки, может быть просто „обновления“. Кнопки пропустить,
/// проверить и т.д. можно было в 1 ряд поместить»*. Прежде обновление было кнопкой-дверью внизу
/// окна, и это читалось как чужой элемент среди разделов, а кнопки действий стояли переносимым
/// рядом и на узком окне складывались в два.
///
/// Три обещания, и каждое проверяется прогоном:
///
/// 1. **раздел есть в списке слева** и называется так, как назвал владелец;
/// 2. **ряд действий стоит в ОДНУ строку** — меряется геометрией, а не взглядом на разметку:
///    четыре кнопки обязаны уложиться в ширину содержимого раздела;
/// 3. **состояние показывается словами** теми же решениями ядра, что у окна обновления: номер
///    с GitHub не выдаётся за новость, а заметки разбираются строками.
/// </summary>
public class SettingsUpdateSectionTests
{
    private sealed class FakeUpdate : IUpdateControl
    {
        public FakeUpdate(
            bool ok = true,
            string latest = "v2.1.0",
            string current = "2.0.0",
            bool skipped = false,
            bool allowed = true,
            string notes = "")
        {
            Current = current;
            Skipped = skipped;
            Allowed = allowed;

            Result = ok
                ? new UpdateRelease(
                    true,
                    string.Empty,
                    latest,
                    ProductLinks.ReleasesPageUrl,
                    "2026-09-27T10:00:00Z",
                    notes,
                    notes,
                    DateTimeOffset.Now)
                {
                    Assets = new ReleaseAssets(
                        "https://example.invalid/DshPanel.zip",
                        string.Empty,
                        "https://example.invalid/SHA256SUMS.txt"),
                }
                : UpdateRelease.Failed("HTTP 500", DateTimeOffset.Now);
        }

        public UpdateRelease Result { get; }

        public bool Busy => false;

        public bool Allowed { get; }

        public string Current { get; }

        public bool Newer => Result.Ok && UpdateDecisions.IsNewer(Result.Latest, Current);

        public bool Skipped { get; }

        public string SourceText => Result.Ok ? "проверено 28.09.2026, 15:00" : "Обновление ещё не проверялось.";

        public string StatusText => Result.Ok ? "Доступна версия " + Result.Latest : SourceText;

        public TrayStatusLine TrayLine => new(StatusText, TrayTone.Neutral);

        public int Checks { get; private set; }

        public event Action<string>? Announce { add { } remove { } }

        public event Action? Changed { add { } remove { } }

        public void Check() => Checks++;
    }

    /// <summary>Окно настроек с подставным состоянием выпусков: живой путь тот же, сети нет.</summary>
    private static SettingsWindow Stand(string dir, FakeUpdate? update)
    {
        var window = PanelTestStand.SettingsStand(dir, update: update);
        window.Show();
        PanelTestStand.Settle();

        return window;
    }

    /// <summary>
    /// РАЗДЕЛ «ОБНОВЛЕНИЕ» ЕСТЬ В СПИСКЕ — последним, как и остальные разделы настроек.
    /// </summary>
    [AvaloniaFact]
    public void Раздел_обновления_есть_в_списке_разделов()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakeUpdate());
            var list = window.FindControl<ListBox>("SectionsList")!;

            var titles = list.ItemsSource!.Cast<string>().ToList();

            Assert.Contains(PanelStrings.UpdateSectionTitle, titles);
            Assert.Equal(SettingsWindow.UpdateSection, titles.IndexOf(PanelStrings.UpdateSectionTitle));

            // И показывается именно он, когда его выбрали.
            Assert.Equal(SettingsWindow.UpdateSection, window.ShowSection(SettingsWindow.UpdateSection));

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// КНОПКИ ДЕЙСТВИЙ — В ОДНУ СТРОКУ. Это главное обещание раздела: владелец назвал переносимый
    /// ряд в окне обновления «сделано очень небрежно».
    ///
    /// ⚠️ Проверка МЕРЯЕТ ГЕОМЕТРИЮ на минимальной высоте и при выбранном разделе: четыре кнопки
    /// обязаны уложиться в ширину содержимого, а самая правая — не выйти за правый край.
    /// На разметку смотреть бесполезно: переносимый ряд и жёсткий ряд выглядят в тексте одинаково,
    /// а на экране ведут себя по-разному.
    /// </summary>
    [AvaloniaFact]
    public void Кнопки_действий_стоят_в_один_ряд()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakeUpdate());

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            var buttons = new[]
            {
                window.FindControl<Button>("UpdateCheckButton"),
                window.FindControl<Button>("UpdateGithubButton"),
                window.FindControl<Button>("UpdateSkipButton"),
                window.FindControl<Button>("UpdatePrepareButton"),
            };

            Assert.All(buttons, button => Assert.NotNull(button));

            // Все четыре — в ОДНОЙ строке: у них одна вертикаль и один родитель-панель.
            var tops = buttons.Select(button => button!.TranslatePoint(new Point(0, 0), window)!.Value.Y).ToList();
            var parents = buttons.Select(button => button!.Parent).Distinct().ToList();

            Assert.Single(parents);
            Assert.True(
                tops.Max() - tops.Min() <= 0.5,
                $"кнопки стоят на разных строках: Y = {string.Join(", ", tops.Select(y => y.ToString("0.#")))}; " +
                $"ширины: {string.Join(", ", buttons.Select(b => $"«{b!.Content}»={b.Bounds.Width:0.#}"))}; " +
                $"окно {window.ClientSize.Width:0.#}");

            // И ряд не выходит за правый край окна: кнопка, уехавшая за край, для человека
            // не существует — это и было замечанием про прокрутку.
            foreach (var button in buttons)
            {
                var widget = button!;
                var left = widget.TranslatePoint(new Point(0, 0), window)!.Value.X;
                var right = left + widget.Bounds.Width;

                Assert.True(widget.Bounds.Width > 0, $"у кнопки «{widget.Content}» нулевая ширина");
                Assert.True(right <= window.ClientSize.Width + 0.5,
                    $"кнопка «{widget.Content}» кончается на {right:0.#}, а окно — на {window.ClientSize.Width:0.#}; " +
                    $"ширины: {string.Join(", ", buttons.Select(b => $"{b!.Bounds.Width:0.#}"))}; " +
                    $"ряд = {((Control)parents[0]!).Bounds.Width:0.#}; содержимое = {window.FindControl<ScrollViewer>("ContentScroll")!.Bounds.Width:0.#}");
            }

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// СОСТОЯНИЕ В РАЗДЕЛЕ — ТЕМИ ЖЕ РЕШЕНИЯМИ, ЧТО У ОКНА ОБНОВЛЕНИЯ: номер с GitHub не выдаётся
    /// за новость, а заметки идут строками, не простынёй. Второго сравнения версий в панели быть
    /// не должно — оно однажды разошлось бы с первым.
    /// </summary>
    [AvaloniaFact]
    public void Раздел_показывает_состояние_и_заметки_строками()
    {
        var dir = PanelTestStand.TempDir();

        const string notes = "### Что нового\n\n- Первое\n- Второе\n\nАбзац заметок.";

        try
        {
            var window = Stand(dir, new FakeUpdate(notes: notes));

            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            // Версии и решение — словами словаря.
            Assert.Contains("2.0.0", window.FindControl<TextBlock>("UpdateCurrentText")!.Text!, StringComparison.Ordinal);
            Assert.Contains("v2.1.0", window.FindControl<TextBlock>("UpdateGithubText")!.Text!, StringComparison.Ordinal);
            Assert.Contains("v2.1.0", window.FindControl<TextBlock>("UpdateVerdictText")!.Text!, StringComparison.Ordinal);
            Assert.Equal(PanelStrings.UpdateSectionTitle, window.FindControl<TextBlock>("UpdateSectionTitle")!.Text);

            // Заметки — строками, и они прокручиваются в своём поле.
            var panel = window.FindControl<StackPanel>("UpdateNotesPanel")!;

            Assert.True(panel.Children.Count > 3, $"строк заметок: {panel.Children.Count} — похоже, простыня");
            Assert.Contains(
                panel.Children.OfType<TextBlock>(),
                block => block.Text == "Что нового");

            // Замены файлов в разделе по умолчанию НЕТ: кнопка замены появляется только после
            // подготовки — это единственная дверь продукта, подменяющая саму панель.
            Assert.False(window.FindControl<Button>("UpdateReplaceButton")!.IsVisible);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// «ПРОВЕРИТЬ СЕЙЧАС» ПРОСИТ ПРОВЕРКУ — кнопка действует, а не только выглядит. Суточный гейт
    /// при этом не спрашивается: человек попросил.
    /// </summary>
    [AvaloniaFact]
    public void Кнопка_проверить_сейчас_просит_проверку()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var update = new FakeUpdate();
            var window = Stand(dir, update);

            window.FindControl<Button>("UpdateCheckButton")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(1, update.Checks);

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПРОГОН БЕЗ ПРАВА ГОТОВИТЬ ЗАМЕНУ: кнопка недоступна и говорит причину словами — как
    /// в окне обновления, тем же решением (<see cref="UpdateWindow.PrepareState"/>).
    /// </summary>
    [AvaloniaFact]
    public void Без_права_подготовка_называет_причину()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakeUpdate());
            window.ShowSection(SettingsWindow.UpdateSection);
            PanelTestStand.Settle();

            // Окно построено с настоящим движком установки ЭТОГО прогона (проверка), поэтому права
            // готовить замену у него нет — и это сказано словами, а не молчанием кнопки.
            var prepare = window.FindControl<Button>("UpdatePrepareButton")!;
            var hint = window.FindControl<TextBlock>("UpdateHintText")!.Text ?? string.Empty;

            Assert.False(prepare.IsEnabled);
            Assert.True(hint.Length > 0, "кнопка недоступна, а причины не названо");

            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }
}
