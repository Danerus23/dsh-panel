using Avalonia;
using Avalonia.Headless;
using DshPanel;

// Точка входа тестового приложения Avalonia. Здесь же — режим отрисовки.
[assembly: AvaloniaTestApplication(typeof(DshPanel.Tests.TestAppBuilder))]

namespace DshPanel.Tests;

public static class TestAppBuilder
{
    /// <summary>
    /// UseHeadlessDrawing = false включает НАСТОЯЩУЮ отрисовку через Skia вместо
    /// заглушки. Это принципиально: с заглушкой кадр «рисуется» без ошибок, но
    /// проверить по нему ничего нельзя — пиксели не те. Пустой кадр должен
    /// проваливать тест, а не проходить.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        // Язык тестов задаётся ЯВНО и до первой строки интерфейса (см. TestLocale):
        // иначе проверки зависели бы от языка сборочной машины.
        TestLocale.Initialize();

        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
    }
}
