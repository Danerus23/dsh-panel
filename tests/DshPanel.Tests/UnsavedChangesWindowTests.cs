using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОКНО ВОПРОСА О НЕСОХРАНЁННОЙ ПРАВКЕ (`docs\DESIGN.md`, п. 16).
///
/// Проверяется БЕЗ экрана и по ПИКСЕЛЯМ — как окна «О программе» и копий. Смысл не в том, чтобы
/// «тест прошёл», а в том, чтобы поймать пустой или схлопнувшийся кадр: окно с
/// <c>SizeToContent</c> легко может показаться человеку полоской без кнопок, и тогда он не сможет
/// ни сохранить правку, ни выйти без неё.
///
/// Здесь же сторожится САМОЕ ОПАСНОЕ свойство этого окна: закрытое крестиком, оно обязано значить
/// «Отмена», а не «Сохранить». Иначе человек, закрывший вопрос, молча записал бы в файл настройки,
/// которых не подтверждал.
/// </summary>
public class UnsavedChangesWindowTests
{
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Кадр окна: сколько пикселей непрозрачных и сколько разных цветов.</summary>
    private static (int Width, int Height, long Opaque, int Colors) Render()
    {
        var window = new UnsavedChangesWindow();

        try
        {
            window.Show();
            Settle();

            var w = (int)Math.Ceiling(window.ClientSize.Width);
            var h = (int)Math.Ceiling(window.ClientSize.Height);
            Assert.True(w > 0 && h > 0, $"пустой размер окна {w}x{h}");

            var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            rtb.Render(window);

            var stride = w * 4;
            var buf = new byte[stride * h];
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { rtb.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buf.Length, stride); }
            finally { handle.Free(); }

            long opaque = 0;
            var colors = new HashSet<int>();
            for (var i = 0; i + 3 < buf.Length; i += 4)
            {
                if (buf[i + 3] != 0) opaque++;
                colors.Add((buf[i] << 16) | (buf[i + 1] << 8) | buf[i + 2]);
            }

            return (w, h, opaque, colors.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Окно_вопроса_рисуется_непустым_кадром()
    {
        var (w, h, opaque, colors) = Render();

        Assert.True(opaque > 0, "в кадре нет ни одного непрозрачного пикселя — окно не нарисовалось");
        Assert.True(colors > 1, $"в кадре всего {colors} цвет(ов) — это заливка, а не интерфейс");

        var total = (long)w * h;
        Assert.True(opaque >= total / 2, $"непрозрачных всего {opaque} из {total} — похоже, вёрстка схлопнулась");
    }

    /// <summary>
    /// Три ответа — три РАЗНЫЕ подписи из словаря, и ни одна из них не имя ключа: пропавший
    /// перевод человек видит именно именем ключа.
    /// </summary>
    [AvaloniaFact]
    public void Три_ответа_подписаны_строками_словаря()
    {
        var window = new UnsavedChangesWindow();

        try
        {
            Assert.Equal(PanelStrings.SettingsUnsavedTitle, window.Title);
            Assert.Equal(PanelStrings.SettingsUnsavedQuestion, window.FindControl<TextBlock>("QuestionText")!.Text);

            var save = window.FindControl<Button>("SaveButton")!;
            var discard = window.FindControl<Button>("DiscardButton")!;
            var cancel = window.FindControl<Button>("CancelButton")!;

            Assert.Equal(PanelStrings.SettingsSave, save.Content);
            Assert.Equal(PanelStrings.SettingsUnsavedDiscard, discard.Content);
            Assert.Equal(PanelStrings.SettingsUnsavedCancel, cancel.Content);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// КАЖДАЯ КНОПКА ОТВЕЧАЕТ СВОИМ ОТВЕТОМ, и ответ читается у окна: сохранить, выйти без
    /// сохранения, отменить. Перепутанные ответы — это «нажал «Отмена», а настройки записались».
    /// </summary>
    [AvaloniaFact]
    public void Каждая_кнопка_отвечает_своим_ответом()
    {
        foreach (var (button, expected) in new[]
                 {
                     ("SaveButton", UnsavedChoice.Save),
                     ("DiscardButton", UnsavedChoice.Discard),
                     ("CancelButton", UnsavedChoice.Cancel),
                 })
        {
            var window = new UnsavedChangesWindow();

            try
            {
                window.Show();
                Settle();

                // До ответа — «Отмена»: окно ещё ничего не подтверждало.
                Assert.Equal(UnsavedChoice.Cancel, window.Choice);

                window.FindControl<Button>(button)!
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(expected, window.Choice);
                Assert.False(window.IsVisible, $"«{button}» обязан закрыть окно вопроса");
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>
    /// ЗАКРЫТОЕ КРЕСТИКОМ ОКНО НЕ ЗНАЧИТ «СОХРАНИТЬ». Значение перечня по умолчанию обязано быть
    /// «Отмена»: окно можно закрыть, не ответив, и ответом тогда становится умолчание — а умолчание
    /// «сохранить» молча записало бы в файл то, чего человек не подтверждал.
    /// </summary>
    [AvaloniaFact]
    public void Без_ответа_окно_значит_отмену_а_не_сохранение()
    {
        Assert.Equal(UnsavedChoice.Cancel, default(UnsavedChoice));

        var window = new UnsavedChangesWindow();

        try
        {
            window.Show();
            Settle();

            Assert.Equal(UnsavedChoice.Cancel, window.Choice);

            // Крестик: окно закрывается, ответа нет — и ответом обязано остаться «Отмена».
            window.Close();

            Assert.Equal(UnsavedChoice.Cancel, window.Choice);
        }
        finally
        {
            window.Close();
        }
    }
}
