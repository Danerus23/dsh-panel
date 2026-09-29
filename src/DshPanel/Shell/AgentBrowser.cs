using System.Diagnostics;
using System.Globalization;
using DshPanel.Isolation;

namespace DshPanel.Shell;

/// <summary>
/// Открытие работы агента в браузере человека — ОДНА дверь на кнопку главного окна и на пункт меню
/// значка.
///
/// Дверь одна не для красоты. У неё два правила, и оба обязаны исполняться в любом входе:
///
/// 1. **Браузер открывается ТОЛЬКО по щелчку человека.** Панель ничего не открывает сама — ни при
///    старте, ни при подъёме сервера, ни при появлении ссылки. Право спрашивается у
///    <see cref="Isolation.RunRights.MayOpenBrowser"/> — у ТОЙ ЖЕ двери, что и остальные
///    «человеческие» права, а не у одной изолированности: <c>--shell-selftest</c> идёт обычным
///    путём панели и не изолирован (холодная проверка 27.09.2026).
/// 2. **Ссылка наружу не выходит.** Она приходит сюда ровно затем, чтобы уехать в браузер:
///    ни в подпись, ни в подсказку, ни в журнал она не попадает — в журнал уходит только
///    ОБЕЗВРЕЖЕННАЯ строка (<see cref="Server.EntryLinkDecisions.Redacted"/>).
/// </summary>
public static class AgentBrowser
{
    /// <summary>
    /// Открыть ссылку в браузере человека — с проверкой права и без исключений наружу.
    ///
    /// Возвращает <c>false</c> и <paramref name="error"/> словами, если открыть не удалось:
    /// «нажал, и ничего не произошло» — это ровно тот случай, ради которого в панели заведены
    /// строки отказа. Отказ оболочки (нет браузера по умолчанию, снятая ассоциация) — обычное
    /// дело на чистой машине, и падать из-за него панель не должна.
    /// </summary>
    /// <param name="mayOpenBrowser">
    /// Право этого прогона открыть браузер — считается у <see cref="Isolation.RunRights"/> и
    /// приходит ОТКРЫТЫМ параметром без значения по умолчанию: забыть выдать право безопасно
    /// (браузер не откроется), забыть запретить — нет. Своей копии решения здесь НЕ заводится:
    /// иначе предохранитель разошёлся бы с тем, что обещает <c>RunRights</c>, и держался бы
    /// случайностью вроде пустой ссылки (так и было до 27.09.2026).
    /// </param>
    /// <param name="opener">
    /// Чем открывать. Отдельным параметром — шов для проверок: с настоящим открытием проверка
    /// запустила бы браузер на рабочем столе владельца, а проверить обязана ровно одно —
    /// что в прогоне проверки шов НЕ ЗОВУТ вовсе.
    /// </param>
    public static bool TryOpen(
        bool mayOpenBrowser,
        string? url,
        Func<string, string> opener,
        Action<string> log,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(opener);
        ArgumentNullException.ThrowIfNull(log);

        error = string.Empty;

        if (!mayOpenBrowser)
        {
            // Строка говорит ПРАВДУ и про прогон без корня: «окно не показываем: прогон
            // изолированный» на нём было бы неправдой.
            error = PanelStrings.AgentBrowserSuppressedLog;
            log(error);
            return false;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            error = PanelStrings.OpenAgentNoLinkHint;
            log(error);
            return false;
        }

        try
        {
            error = opener(url);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name} — {ex.Message}";
        }

        if (error.Length == 0)
        {
            log(PanelStrings.OpenAgentOpenedLog);
            return true;
        }

        log(string.Format(CultureInfo.CurrentCulture, PanelStrings.OpenAgentOpenFailedLogFormat, error));
        return false;
    }

    /// <summary>
    /// Настоящее открытие: оболочка Windows сама находит браузер по умолчанию
    /// (<c>UseShellExecute = true</c>). Пустая строка — успех, иначе причина отказа словами.
    /// </summary>
    public static string OpenWithShell(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            // Процесс мог не запуститься вовсе (нет ассоциации для http) — это не исключение,
            // а честный отказ, и назвать его обязан вызывающий.
            return process is null && !OperatingSystem.IsWindows()
                ? PanelStrings.OpenAgentFailedTitle
                : string.Empty;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name} — {ex.Message}";
        }
    }

    /// <summary>
    /// СЛОВА ДВЕРИ «открыть каталог» — то, что человек прочтёт при отказе.
    ///
    /// Зачем отдельной записью. Дверей две (каталог настроек и папка копий, п. 31
    /// <c>docs\DESIGN.md</c>), и они открывают РАЗНЫЕ места: отказ одной, сказанный словами
    /// другой, назвал бы человеку не то место — «открывать нечего: каталог настроек неизвестен»
    /// на кнопке папки копий читается как чужая беда. Форма двери при этом одна, и это не
    /// совпадение: одинаковый предохранитель обязан выглядеть одинаково, а различаться — только
    /// словами.
    ///
    /// ⚠️ Свойства, а не поля: <see cref="PanelStrings"/> берёт строку ТЕКУЩЕГО языка, и запомненное
    /// однажды значение осталось бы русским на английском кадре.
    /// </summary>
    /// <param name="RefusedLog">Отказ по праву: этим прогоном это место не открывается.</param>
    /// <param name="Missing">Отказ: открывать нечего — каталога нет или он не назван.</param>
    /// <param name="OpenedLog">Строка журнала об удаче (в окне она не показывается — проводник человек и так видит).</param>
    /// <param name="FailedFormat">Отказ оболочки — с её причиной.</param>
    public sealed record FolderDoorWords(string RefusedLog, string Missing, string OpenedLog, string FailedFormat)
    {
        /// <summary>Слова КАТАЛОГА НАСТРОЕК — дверь окна настроек.</summary>
        public static FolderDoorWords Settings => new(
            PanelStrings.SettingsFolderRefusedLog,
            PanelStrings.SettingsFolderMissing,
            PanelStrings.SettingsFolderOpenedLog,
            PanelStrings.SettingsFolderFailedFormat);

        /// <summary>
        /// Слова ПАПКИ КОПИЙ — дверь окна копий. Отдельные, а не настройки: человек нажимает
        /// кнопку рядом с путём папки КОПИЙ и про папку копий обязан прочитать отказ.
        /// </summary>
        public static FolderDoorWords Backups => new(
            PanelStrings.BackupFolderRefusedLog,
            PanelStrings.BackupFolderMissing,
            PanelStrings.BackupFolderOpenedLog,
            PanelStrings.BackupFolderFailedFormat);
    }

    /// <summary>
    /// Показать человеку КАТАЛОГ ФАЙЛА НАСТРОЕК в проводнике — дверь того же вида, что
    /// <see cref="TryOpen"/>, и по тому же уговору: право приходит ОТКРЫТЫМ параметром
    /// без значения по умолчанию, отказ называет причину словами, исключений наружу не бывает,
    /// а шов <paramref name="opener"/> нужен проверкам (настоящее открытие показало бы окно
    /// на рабочем столе владельца).
    ///
    /// ⚠️ Почему своя дверь, а не переиспользование <see cref="TryOpen"/>. У дверей РАЗНЫЕ права
    /// и разные слова отказа: браузер панель открывает по щелчку человека
    /// (<see cref="Isolation.RunRights.MayOpenBrowser"/>), а каталог — только у того прогона,
    /// которому этот каталог принадлежит (см. <c>SettingsController.Writable</c> и
    /// <c>BackupController.Writable</c>).
    ///
    /// Открывает тем же <see cref="OpenWithShell"/>: оболочка одинаково открывает и адрес,
    /// и каталог, а второй способ запуска — вторая опечатка в нём.
    ///
    /// ⚠️ Этот вход оставлен БЕЗ проверки существования каталога намеренно: так дверь вела себя
    /// до появления папки копий, и окно настроек проверено именно в этом поведении. Папке копий
    /// мало «пути нет»: до первой копии каталога нет вовсе, и открывать пустой проводник нельзя —
    /// ей нужен <see cref="TryOpenFolder(bool, string?, FolderDoorWords, bool, Func{string, string}, Action{string}, out string)"/>
    /// с <c>mustExist: true</c>.
    /// </summary>
    /// <param name="mayOpenFolder">Право этого прогона показать каталог настроек. Нет права — отказ словами, шов не зовётся.</param>
    /// <param name="folder">Каталог файла настроек. Пусто — «открывать нечего», и это тоже отказ словами.</param>
    public static bool TryOpenFolder(
        bool mayOpenFolder,
        string? folder,
        Func<string, string> opener,
        Action<string> log,
        out string error) =>
        TryOpenFolder(mayOpenFolder, folder, FolderDoorWords.Settings, mustExist: false, opener, log, out error);

    /// <summary>
    /// Та же дверь со СВОИМИ словами и с проверкой, что каталог есть.
    ///
    /// <paramref name="mustExist"/> — не придирка: папки копий до первой копии не существует,
    /// и открытие несуществующего пути даёт пустой проводник (или ничего) — то есть «нажал,
    /// и не произошло ничего». Человеку в этом случае нужно СЛОВО, а не пустое окно.
    /// </summary>
    /// <param name="words">Слова отказа и журнала — про то место, которое открываем.</param>
    /// <param name="mustExist">Требовать ли, чтобы каталог существовал на диске.</param>
    public static bool TryOpenFolder(
        bool mayOpenFolder,
        string? folder,
        FolderDoorWords words,
        bool mustExist,
        Func<string, string> opener,
        Action<string> log,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(opener);
        ArgumentNullException.ThrowIfNull(log);

        error = string.Empty;

        if (!mayOpenFolder)
        {
            error = words.RefusedLog;
            log(error);
            return false;
        }

        if (string.IsNullOrWhiteSpace(folder) || (mustExist && !Exists(folder)))
        {
            error = words.Missing;
            log(error);
            return false;
        }

        try
        {
            error = opener(folder);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name} — {ex.Message}";
        }

        if (error.Length == 0)
        {
            log(words.OpenedLog);
            return true;
        }

        log(string.Format(CultureInfo.CurrentCulture, words.FailedFormat, error));
        return false;
    }

    /// <summary>
    /// Есть ли каталог — БЕЗ исключений наружу: недоступный или кривой путь это не «есть»,
    /// и отказ обязан остаться отказом словами, а не падением окна.
    /// </summary>
    private static bool Exists(string folder)
    {
        try
        {
            return Directory.Exists(folder);
        }
        catch
        {
            return false;
        }
    }
}
