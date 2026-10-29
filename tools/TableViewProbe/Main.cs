using System;

namespace TableViewProbe;

/// <summary>
/// ПРОБА TableView (этап 2.4.3). Ключи:
///   --api                       что вообще есть в ядре 12.1.3 (открытая поверхность TableView)
///   --frames &lt;каталог&gt;          кадр без экрана (--mode now|proto, --theme light|dark,
///                               --entry N, --select N, --width N, --name файл, --measure файл)
///   --live                      настоящее окно Win32 для UIA (--offscreen, --seconds N)
///
/// Незнакомый ключ — код 2, как у продуктовой съёмки: опечатка не должна давать «кадр как вышло».
/// </summary>
internal static class Entry
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            // Вывод пробы читает человек и скрипт замера: UTF-8, а не кодовая страница консоли.
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (System.IO.IOException)
        {
            // Консоли нет (запуск без перенаправления у WinExe) — писать всё равно есть куда.
        }

        if (args.Length == 0)
        {
            Console.WriteLine("ПРОБА: нужен ключ --api | --frames <каталог> | --live");
            return 2;
        }

        try
        {
            switch (args[0])
            {
                case "--api":
                    ApiDump.Run();
                    return 0;

                case "--frames":
                    return Probe.Frames(Options.Parse(args));

                case "--live":
                    return Probe.Live(Options.Parse(args));

                // ВТОРОЙ ШАГ ЭТАПА: «Пики» и «План наката» — свои мини-таблицы, свой замер (T2).
                case "--fit":
                    return Fit.Fit.Run(args);

                default:
                    Console.WriteLine($"ПРОБА ОТКАЗ: незнакомый ключ «{args[0]}»");
                    return 2;
            }
        }
        catch (ArgumentException error)
        {
            Console.WriteLine("ПРОБА ОТКАЗ: " + error.Message);
            return 2;
        }
        catch (Exception error)
        {
            Console.WriteLine("ПРОБА ПАДЕНИЕ: " + error);
            return 1;
        }
    }
}
