using System.Text;

namespace DshTray;

/// <summary>
/// Свой журнал приложения: сюда попадает то, о чём не расскажет журнал сервера —
/// смена языка, проверки тарифа, резервные копии, перенос настроек.
/// Лежит рядом с состоянием панели (%LOCALAPPDATA%\DshPanel\dsh-tray.log).
///
/// Журнал не растёт бесконечно: установка движка пишет в него весь вывод npm (десятки и
/// сотни килобайт за один раз), а панель работает месяцами. Дойдя до предела, файл уступает
/// место новому, а прежние остаются рядом как dsh-tray.log.1 и .2.
/// </summary>
internal static class AppLog
{
    /// <summary>Предел одного файла. Прежние версии не ограничивали журнал вовсе.</summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>Сколько прежних файлов держим: сам журнал, .1 и .2.</summary>
    private const int KeepOld = 2;

    public static void Write(AppPaths paths, string text)
    {
        try
        {
            Directory.CreateDirectory(paths.LogDir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}";
            var path = Path.Combine(paths.LogDir, "dsh-tray.log");

            // Ротация до записи: иначе одна большая запись (вывод npm) переводила бы файл
            // через предел и он оставался бы единственным и огромным.
            if (File.Exists(path) && new FileInfo(path).Length + line.Length > MaxBytes)
            {
                Rotate(path);
            }

            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
        catch
        {
            // Журнал приложения — не повод падать.
        }
    }

    private static void Rotate(string path)
    {
        try
        {
            for (var index = KeepOld - 1; index >= 1; index--)
            {
                var older = $"{path}.{index}";
                var next = $"{path}.{index + 1}";
                if (File.Exists(older)) File.Move(older, next, overwrite: true);
            }

            File.Move(path, path + ".1", overwrite: true);
        }
        catch
        {
            // Не вышло переименовать (файл занят) — просто продолжаем писать в тот же файл.
        }
    }
}
