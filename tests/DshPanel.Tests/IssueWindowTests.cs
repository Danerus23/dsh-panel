using System;
using Avalonia.Headless.XUnit;
using DshPanel.Issue;
using DshPanel.Tests;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОКНО «СООБЩИТЬ О ПРОБЛЕМЕ» (требование владельца 29.09.2026, <c>DESIGN.md</c> п. 38).
///
/// **Главное, что здесь проверяется, — это ОБЕЩАНИЕ ВЛАДЕЛЬЦУ, а не вёрстка:** он видит
/// в окне ровно тот текст, который уйдёт. Проверка ставит окну подставные факты и сверяет
/// показанное с тем, что соберёт <see cref="IssueReport"/> — то есть с тем же решением,
/// которым пользуется кнопка «Открыть на GitHub». Разойдись они, человек прочитал бы одно,
/// а отправил другое.
///
/// ⚠️ Чего эти проверки НЕ покрывают: что отчёт открывается в браузере (браузер — это окно
/// на рабочем столе человека, и проверка его не открывает), что буфер обмена платформы
/// принимает текст и что диалог сохранения файла выбирает файл. Все три пути наружу
/// проверены глазами владельца, а не прогоном.
/// </summary>
public class IssueWindowTests
{
    private static IssueFacts Facts() => new(
        PanelVersion: "2.0.0",
        Revision: "abc1234",
        System: "Windows 11 (10.0.26100)",
        EngineVersion: "0.1.5-rc.3",
        NodeVersion: "26.0.0",
        LaunchMode: "обычный запуск человеком",
        ServerState: "Сервер: работает · порт 3080",
        ServerPort: 3080,
        LogTail: new[] { "2026-09-29 00:00:00 панель запущена", "2026-09-29 00:00:01 баланс: 4.10 USD" });

    /// <summary>
    /// ПОКАЗАНО РОВНО ТО, ЧТО УЙДЁТ. Сверяется с тем же решением, что строит ссылку для GitHub:
    /// второй сборки текста в окне нет.
    /// </summary>
    [AvaloniaFact]
    public void Окно_показывает_ровно_тот_текст_который_уйдёт()
    {
        var window = new IssueWindow();
        window.Attach(Facts());

        Assert.Equal(IssueReport.Text(Facts()), window.PreviewLine);
    }

    /// <summary>
    /// СВОИ СЛОВА ЧЕЛОВЕКА ПОПАДАЮТ В ПОКАЗАННЫЙ ТЕКСТ СРАЗУ. Владелец пишет «что случилось»,
    /// и обязан видеть, что его слова тоже уйдут, — до нажатия, а не после.
    /// </summary>
    [AvaloniaFact]
    public void Слова_человека_попадают_в_показанный_текст()
    {
        var window = new IssueWindow();
        window.Attach(Facts());

        window.TypeMessage("после перезагрузки сервер не поднялся");
        window.Render();

        Assert.Contains("после перезагрузки сервер не поднялся", window.PreviewLine, StringComparison.Ordinal);
        Assert.Equal(IssueReport.Text(Facts(), "после перезагрузки сервер не поднялся"), window.PreviewLine);
    }

    /// <summary>
    /// СТРОКА «ЧЕГО В ОТЧЁТЕ НЕТ» СТОИТ В ОКНЕ. Это не украшение: именно она делает «не утечёт»
    /// тем, что человек видит глазами, а не обещанием панели.
    /// </summary>
    [AvaloniaFact]
    public void Окно_говорит_чего_в_отчёте_нет()
    {
        var window = new IssueWindow();

        Assert.Equal(IssueReport.OmittedNote, window.OmittedLine);
        Assert.Contains("~/.dsh", window.OmittedLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// ТРИ ПУТИ НАРУЖУ ЕСТЬ, И ПОДПИСАНЫ. Кнопок ровно три пути — открыть, скопировать,
    /// сохранить, — и ни одной четвёртой: «панель сама никуда не отправляет» держится не
    /// обещанием, а тем, что других путей в окне нет.
    /// </summary>
    [AvaloniaFact]
    public void Окно_даёт_три_пути_наружу_и_все_по_нажатию()
    {
        var window = new IssueWindow();

        Assert.NotEmpty(window.OpenLine);
        Assert.NotEmpty(window.CopyLine);
        Assert.NotEmpty(window.SaveLine);
    }

    /// <summary>
    /// ФАКТЫ ЧИСТЯТСЯ ПРИ ПОКАЗЕ, а не «когда-нибудь в сборке». Подложен путь человека в хвосте
    /// журнала: в показанном тексте его быть не должно.
    ///
    /// ⚠️ Проходит через то состояние, в котором дефект возможен: строка сначала ПОПАДАЕТ
    /// в факты, и только потом — в окно.
    /// </summary>
    [AvaloniaFact]
    public void Окно_не_показывает_путь_человека()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(profile), "без профиля проверять нечего");

        var facts = Facts() with { LogTail = new[] { "рабочая папка сервера: " + profile + "\\работа" } };

        var window = new IssueWindow();
        window.Attach(facts);

        Assert.DoesNotContain(profile, window.PreviewLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("~", window.PreviewLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОКНО БЕЗ НАПОЛНЕНИЯ НЕ ПАДАЕТ И НЕ ВЫДУМЫВАЕТ. Так его строит кадровая съёмка
    /// (`--shot issue`): версия известна у самой сборки, остальное честно пусто.
    /// </summary>
    [AvaloniaFact]
    public void Окно_без_фактов_показывает_отчёт_с_неизвестными_полями()
    {
        var window = new IssueWindow();

        Assert.Contains("не удалось узнать", window.PreviewLine, StringComparison.Ordinal);
        Assert.Contains(AboutWindow.PanelVersion, window.PreviewLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// ДВЕРЬ В «О ПРОГРАММЕ» ПОКАЗЫВАЕТСЯ ТОЛЬКО ТОГДА, КОГДА ЕСТЬ КУДА ИДТИ. Кнопка в никуда
    /// хуже отсутствующей: человек нажал бы и не понял, почему ничего не произошло.
    /// </summary>
    [AvaloniaFact]
    public void Блок_о_проблеме_виден_только_с_дверью()
    {
        var window = new AboutWindow();

        // Двери нет — блока нет.
        window.AttachIssueDoor(null);
        window.Render();
        Assert.False(AboutWindow.ShowIssueBlock(false));

        // Дверь есть — блок есть, и нажатие зовёт ровно её.
        var called = 0;
        window.AttachIssueDoor(() => called++);
        window.Render();
        Assert.True(AboutWindow.ShowIssueBlock(true));
        Assert.Equal(0, called);
    }
}
