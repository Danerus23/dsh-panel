using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Окно вопроса «сервер работает — погасить?» проверяется так же, как окно копий: БЕЗ экрана и
/// по ПИКСЕЛЯМ, а решения — по фактическому поведению кнопок, а не по объявлению перечня.
///
/// Смысл тот же, что у соседей: критерий этапа «видно на экране» обязан доказываться машинно,
/// а у этого окна цена ошибки выше — перепутанная кнопка означает «копия на ходу» там, где
/// человек выбрал гашение (или наоборот: панель гасит сервер, которого гасить не просили).
/// </summary>
public class BackupStopWindowRenderTests
{
    /// <summary>
    /// Порт своего сервера из настроек. Именно он, а не 3080: 3080 — живой сервер владельца,
    /// и в проверке ему делать нечего.
    /// </summary>
    private const int Port = 3081;

    private static (int Width, int Height, long Opaque, int Colors) Render()
    {
        var window = new BackupStopWindow();

        // Окно строится с наполнением, как его строит вызывающий: пустое окно — это не кадр вопроса.
        window.Attach(Port);

        window.Show();

        // Дать вёрстке и очереди диспетчера отработать до конца.
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

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

    /// <summary>
    /// Нажать кнопку в НАСТОЯЩЕМ диалоге и вернуть то, что вернул <c>ShowDialog&lt;StopChoice&gt;</c>:
    /// именно так окно спрашивает окно копий, и никакого другого пути узнать выбор у него нет.
    /// </summary>
    private static StopChoice Press(string buttonName)
    {
        var owner = new Window { Width = 240, Height = 160 };
        owner.Show();

        try
        {
            var dialog = new BackupStopWindow();
            dialog.Attach(Port);

            var answer = dialog.ShowDialog<StopChoice>(owner);
            Dispatcher.UIThread.RunJobs();

            dialog.FindControl<Button>(buttonName)!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // Окно закрывается своим ходом, но продолжение задачи может ждать очереди диспетчера:
            // даём ей отработать, а не полагаемся на «успело само».
            for (var i = 0; i < 300 && !answer.IsCompleted; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }

            Assert.True(answer.IsCompleted, $"нажатие «{buttonName}» не закрыло окно вопроса");
            return answer.GetAwaiter().GetResult();
        }
        finally
        {
            try { owner.Close(); } catch { /* уборка не должна ронять проверку */ }
        }
    }

    [AvaloniaFact]
    public void Окно_вопроса_о_сервере_рисуется_непустым_кадром()
    {
        var (w, h, opaque, colors) = Render();

        Assert.True(opaque > 0, "в кадре нет ни одного непрозрачного пикселя — окно не нарисовалось");
        Assert.True(colors > 1, $"в кадре всего {colors} цвет(ов) — это заливка, а не интерфейс");

        var total = (long)w * h;
        Assert.True(opaque >= total / 2, $"непрозрачных всего {opaque} из {total} — похоже, вёрстка схлопнулась");
    }

    [AvaloniaFact]
    public void Кадр_окна_вопроса_о_сервере_воспроизводим_между_запусками()
    {
        var first = Render();
        var second = Render();

        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        Assert.Equal(first.Opaque, second.Opaque);
    }

    /// <summary>
    /// Наполнение окна: порт назван прямо, и все три исхода названы словами. «Погасить» и «на ходу»
    /// обязаны быть РАЗНЫМИ надписями — иначе человек выбирает вслепую, а панель получает не тот
    /// ответ, который он прочитал.
    /// </summary>
    [AvaloniaFact]
    public void Окно_вопроса_называет_порт_и_три_исхода_словами()
    {
        var window = new BackupStopWindow();
        window.Attach(Port);

        Assert.Equal(PanelStrings.BackupStopTitle, window.Title);
        Assert.Contains(
            Port.ToString(CultureInfo.InvariantCulture),
            window.FindControl<TextBlock>("QuestionText")!.Text!,
            StringComparison.Ordinal);

        Assert.Equal(PanelStrings.BackupStopCancel, window.FindControl<Button>("CancelButton")!.Content);
        Assert.Equal(PanelStrings.BackupStopNo, window.FindControl<Button>("LiveButton")!.Content);
        Assert.Equal(PanelStrings.BackupStopYes, window.FindControl<Button>("StopButton")!.Content);

        Assert.NotEqual(
            window.FindControl<Button>("LiveButton")!.Content,
            window.FindControl<Button>("StopButton")!.Content);
    }

    /// <summary>
    /// Каждая кнопка возвращает РОВНО свой исход. Проверка идёт через настоящее нажатие и настоящий
    /// диалог: перепутанная кнопка — это «копия на ходу» вместо гашения, то есть другое действие
    /// с данными человека, а не косметика.
    /// </summary>
    [AvaloniaFact]
    public void Кнопки_окна_возвращают_объявленные_решения()
    {
        Assert.Equal(StopChoice.Cancel, Press("CancelButton"));
        Assert.Equal(StopChoice.ContinueLive, Press("LiveButton"));
        Assert.Equal(StopChoice.StopAndContinue, Press("StopButton"));
    }

    /// <summary>
    /// Три исхода различимы, а умолчание перечня — отмена. Умолчание здесь не мелочь: окно,
    /// закрытое крестиком или Esc, не возвращает ничего, и вызывающий получает именно
    /// <c>default(StopChoice)</c>. Если поедут порядок или номера, «ничего не делать» молча
    /// превратится в «копировать на ходу» или в «погасить сервер».
    /// </summary>
    [AvaloniaFact]
    public void Умолчание_перечня_это_отмена_а_исходы_различимы()
    {
        Assert.Equal(StopChoice.Cancel, default(StopChoice));
        Assert.Equal(3, Enum.GetValues<StopChoice>().Length);

        Assert.NotEqual(StopChoice.Cancel, StopChoice.ContinueLive);
        Assert.NotEqual(StopChoice.Cancel, StopChoice.StopAndContinue);
        Assert.NotEqual(StopChoice.ContinueLive, StopChoice.StopAndContinue);

        // И то же самое на живом окне: закрытие без выбора обязано читаться как отмена.
        var owner = new Window { Width = 240, Height = 160 };
        owner.Show();
        try
        {
            var dialog = new BackupStopWindow();
            dialog.Attach(Port);

            var answer = dialog.ShowDialog<StopChoice>(owner);
            Dispatcher.UIThread.RunJobs();

            dialog.Close();

            for (var i = 0; i < 300 && !answer.IsCompleted; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }

            Assert.True(answer.IsCompleted, "окно вопроса не закрылось без выбора");
            Assert.Equal(StopChoice.Cancel, answer.GetAwaiter().GetResult());
        }
        finally
        {
            try { owner.Close(); } catch { /* уборка не должна ронять проверку */ }
        }
    }
}
