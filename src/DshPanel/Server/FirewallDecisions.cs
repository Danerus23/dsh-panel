using System.Globalization;
using DshPanel.Localization;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// ЧТО ОТВЕТИЛА МАШИНА на чтение правил брандмауэра: код возврата и весь вывод целиком.
///
/// Отдельной записью, а не двумя доводами: разбор состояния решается по ДВУМ чтениям сразу
/// (<see cref="FirewallDecisions.Read"/>), и «код без вывода» или «вывод без кода» — это уже
/// не ответ, а беда, которую разбор обязан назвать словами.
/// </summary>
public sealed record FirewallAnswer(int ExitCode, string Output);

/// <summary>
/// ЧТО С НАШИМ ПРАВИЛОМ СЕЙЧАС.
///
/// ⚠️ <see cref="Unknown"/> — ОТДЕЛЬНОЕ состояние, а не «правила нет»: «не прочиталось» и «правила
/// нет» — разные вещи, и первое, выданное за второе, повело бы человека ставить правило поверх
/// уже стоящего. Ради этого различия разбор и заведён отдельно от двери.
/// </summary>
public enum FirewallPresence
{
    /// <summary>Правила с нашим именем нет вовсе.</summary>
    Absent,

    /// <summary>Правило есть и оно такое, каким его ставит панель.</summary>
    Ready,

    /// <summary>Правило с нашим именем есть, но НЕ такое, каким его ставит панель (чужое или старое).</summary>
    Other,

    /// <summary>
    /// Правило с нашим именем есть, но проверить его форму НЕ УДАЛОСЬ — и это НЕ «не такое».
    ///
    /// ⚠️ Заведено по живому прогону владельца 11.10.2026: <c>netsh</c> печатает строку
    /// <c>Program:</c> только с ключом <c>verbose</c>, а панель звала его без него — и правило,
    /// поставленное ЕЮ ЖЕ, читалось как «чужое» (в выводе не было программы, и сверка значений
    /// не сходилась). Врать человеку «правило не такое» о своей же работе нельзя: это отдельное
    /// состояние, и слова у него свои.
    /// </summary>
    Unverified,

    /// <summary>Правил с нашим именем больше одного.</summary>
    Several,

    /// <summary>Прочитать не удалось — и это НЕ «правила нет».</summary>
    Unknown,
}

/// <summary>СОСТОЯНИЕ ПРАВИЛА И СЛОВА О НЁМ: что человеку сказать и что ему можно нажать.</summary>
public sealed record FirewallState(FirewallPresence Presence, string Words)
{
    /// <summary>Можно ставить правило — только когда его заведомо нет.</summary>
    public bool CanAdd => Presence == FirewallPresence.Absent;

    /// <summary>
    /// Можно убирать — когда правило есть в любом виде, в том числе НЕПРОВЕРЕННОЕ. Убираем МЫ ВСЕГДА
    /// ПО СВОЕМУ ИМЕНИ (<see cref="FirewallDecisions.Remove"/>), поэтому «не такое», «их несколько»
    /// и «проверить не удалось» убираются тем же действием: иначе человек остался бы без выхода
    /// из состояния, которое сам не создавал.
    /// </summary>
    public bool CanRemove =>
        Presence is FirewallPresence.Ready or FirewallPresence.Other
            or FirewallPresence.Several or FirewallPresence.Unverified;
}

/// <summary>
/// ЧЕМ КОНЧИЛОСЬ ПОВЫШЕНИЕ — и это РЕШЕНИЕ, а не разбор <c>try/catch</c> на месте: у запроса
/// подтверждения Windows ровно три исхода, и «человек отказал» обязан отличаться от «не вышло».
///
/// ⚠️ Ради этого разделения и заведён отдельный член: отказ человека — не беда панели, и проверка
/// обязана уметь его уронить, не поднимая запрос Windows (живой запрос на машине владельца
/// не поднимается вовсе — решение владельца 11.10.2026).
/// </summary>
public enum FirewallElevation
{
    /// <summary>Команда отработала.</summary>
    Done,

    /// <summary>Человек закрыл запрос подтверждения (<c>ERROR_CANCELLED</c>) — не изменено НИЧЕГО.</summary>
    Refused,

    /// <summary>Не вышло: код возврата не ноль или <c>netsh</c> не запустился.</summary>
    Failed,
}

/// <summary>
/// ГОТОВАЯ КОМАНДА netsh: сложилась — или отказала СЛОВАМИ.
///
/// Отказ вместо исключения не «мягкость», а требование: порт, который панель не занимает
/// (например <c>3080</c>), обязан быть назван человеку словами ДО окна подтверждения Windows,
/// а не упасть исключением внутри обработчика нажатия.
/// </summary>
public sealed record FirewallCommand(bool Ok, string Line, string Refusal);

/// <summary>
/// ПРАВИЛО БРАНДМАУЭРА ДЛЯ КАЛИТКИ ТЕЛЕФОНА — ВСЁ РЕШЕНИЕ ЗДЕСЬ, И НИ ОДНОГО ИЗМЕНЕНИЯ НА МАШИНЕ
/// (этап F4, кусок 1).
///
/// **Зачем отдельным членом.** Строить команду, разбирать ответ машины и говорить слова — это
/// РЕШЕНИЕ, и оно обязано проверяться без прав и без единого изменения на машине владельца:
/// <c>netsh … add</c> здесь не зовётся ни разу (им занимается дверь, <see cref="FirewallDoor"/>).
/// Проверки гоняют ЭТОТ файл: команда узкая, имя правила одно, состояние читается честно.
///
/// **Правило узкое, а не «открыть порт»**, и каждое слово в команде — из решения владельца
/// 11.10.2026 (живой прогон F4):
///
/// * <c>profile=private</c> — только частный профиль: в общедоступной сети правило не работает;
/// * <c>localport</c> — только порт калитки, а не «все порты»;
/// * <c>program</c> — только сама панель: разрешение не достаётся чужой программе;
/// * <c>remoteip=localsubnet</c> — только своя подсеть (то, что владелец назвал «желательно»);
/// * <c>action=allow dir=in protocol=TCP enable=yes</c> — и ничего больше: ни исходящих,
///   ни UDP, ни «любого адреса».
///
/// ⚠️ **ИМЯ ПРАВИЛА — ОДНО МЕСТО НА ВСЮ ПАНЕЛЬ** (<see cref="RuleName"/>) и оно уникально: снятие
/// идёт ТОЛЬКО по нему, поэтому чужое правило снять нечем. Проверка это стережёт: имя встречается
/// в исходниках ровно один раз.
/// </summary>
public static class FirewallDecisions
{
    /// <summary>
    /// ИМЯ НАШЕГО ПРАВИЛА. Одно место на всю панель: по нему ставим, по нему же и убираем.
    ///
    /// ⚠️ Имя ЛАТИНИЦЕЙ и без пробелов — намеренно: его печатает <c>netsh</c> в своей кодировке
    /// консоли, и от того, угадали ли мы кодировку, зависит, узнаем ли мы СВОЁ правило. Латинские
    /// буквы и дефисы читаются одинаково при любой кодировке, а слова человеку панель говорит
    /// отдельно — в разделе, а не именем правила.
    /// </summary>
    public const string RuleName = "DshPanel2-Phone-Gate";

    /// <summary>Как звать netsh. Путь к нему собирает дверь (<see cref="FirewallDoor.NetshPath"/>) — из System32, а не из PATH.</summary>
    public const string NetshFile = "netsh.exe";

    /// <summary><c>ERROR_CANCELLED</c>: человек закрыл запрос подтверждения, не согласившись.</summary>
    public const int CancelledNative = 1223;

    /// <summary>
    /// ЧТО ЗНАЧИТ ЭТОТ ИСХОД ПОВЫШЕНИЯ. Чистая функция от двух чисел — и это не формальность:
    /// **отказ человека читается по НОМЕРУ <c>ERROR_CANCELLED</c>**, а не по «не ноль», иначе
    /// «человек нажал Нет» и «netsh сломался» сказались бы человеку одинаково.
    ///
    /// ⚠️ Два пути РАЗНЫЕ и вместе не встречаются: либо получен код возврата (тогда
    /// <paramref name="nativeError"/> = 0), либо исключение с номером ошибки. Поэтому номер, если
    /// он есть, решает всё: <c>1223</c> — отказ человека, любой другой — беда.
    /// </summary>
    public static FirewallElevation Elevated(int exitCode, int nativeError) =>
        nativeError != 0
            ? nativeError == CancelledNative ? FirewallElevation.Refused : FirewallElevation.Failed
            : exitCode == 0 ? FirewallElevation.Done : FirewallElevation.Failed;

    /// <summary>
    /// СОБРАТЬ КОМАНДУ «РАЗРЕШИТЬ». Узкая по построению: иначе её не собрать.
    ///
    /// Порт спрашивается у того же правила, что и калитка (<see cref="PhoneGate.PortAllowed"/>):
    /// правило на порт, который панель не занимает (владельца <c>3080</c> и <c>3081</c>), было бы
    /// дырой в никуда.
    /// </summary>
    public static FirewallCommand Add(int port, string exePath)
    {
        if (!PhoneGate.PortAllowed(port))
        {
            return new FirewallCommand(
                false,
                string.Empty,
                Loc.T(PanelStrings.FirewallPortUnusableFormat, port));
        }

        var program = (exePath ?? string.Empty).Trim();

        // ПУТИ НЕТ — КОМАНДЫ НЕТ. Правило без `program=` разрешало бы порт ЛЮБОЙ программе,
        // а правило с пустым `program=""` netsh отвергнет молча: и то и другое хуже отказа словами.
        if (program.Length == 0 || program.Contains('"', StringComparison.Ordinal))
        {
            return new FirewallCommand(
                false,
                string.Empty,
                Loc.T(PanelStrings.FirewallFailedFormat, Loc.T(PanelStrings.FirewallNoDoor)));
        }

        return new FirewallCommand(
            true,
            "advfirewall firewall add rule name=" + Quote(RuleName)
                + " dir=in action=allow protocol=TCP localport=" + port.ToString(CultureInfo.InvariantCulture)
                + " program=" + Quote(program)
                + " profile=private remoteip=localsubnet enable=yes",
            string.Empty);
    }

    /// <summary>
    /// СОБРАТЬ КОМАНДУ «УБРАТЬ» — и только своё: имя правила здесь ЕДИНСТВЕННЫЙ отбор,
    /// поэтому чужое правило снять этой командой нечем.
    /// </summary>
    public static string Remove() =>
        "advfirewall firewall delete rule name=" + Quote(RuleName);

    /// <summary>
    /// Прочитать ВСЁ, что есть с нашим именем (любой формы).
    ///
    /// ⚠️ **Ключ <c>verbose</c> здесь ОБЯЗАТЕЛЕН, и это замер живого прогона 11.10.2026.** Без него
    /// <c>netsh</c> НЕ печатает строку <c>Program:</c> вовсе — и правило, поставленное самой панелью,
    /// читалось как «чужое»: сверять путь было не с чем. Замер владельца на его машине: с
    /// <c>verbose</c> в выводе есть <c>Program: C:\…\DshPanel.exe</c>, без него строки нет.
    /// </summary>
    public static string ShowByName() =>
        "advfirewall firewall show rule name=" + Quote(RuleName) + " verbose";

    /// <summary>
    /// ПРОЧИТАТЬ ТО ЖЕ ИМЯ, НО УЖЕ С ТРЕБОВАНИЯМИ: включённое, входящее, частный профиль.
    ///
    /// ⚠️ Почему фильтрами <c>netsh</c>, а не разбором слов вывода: <c>show rule</c> принимает
    /// только <c>name</c>, <c>dir</c>, <c>profile</c>, <c>status</c> и <c>type</c> (замер 11.10.2026:
    /// <c>protocol</c>, <c>localport</c>, <c>remoteip</c>, <c>program</c> он отвергает — «не является
    /// допустимым аргументом»). Остальные признаки (порт, программа) сверяются ПО ЗНАЧЕНИЯМ вывода,
    /// и это тоже замер: метки полей netsh переводит на язык Windows, а значения — нет.
    ///
    /// ⚠️ И <c>verbose</c> здесь тоже обязателен — по той же причине, что в <see cref="ShowByName"/>.
    /// </summary>
    public static string ShowNarrow() =>
        "advfirewall firewall show rule name=" + Quote(RuleName) + " status=enabled dir=in profile=private verbose";

    /// <summary>
    /// РАЗОБРАТЬ СОСТОЯНИЕ ПО ДВУМ ЧТЕНИЯМ. Чистая функция: ни машины, ни прав, ни времени.
    ///
    /// Порядок решений важен и потому назван:
    /// (1) ЧТЕНИЕ ПО ИМЕНИ не прочиталось — это НЕ «правила нет» (<see cref="FirewallPresence.Unknown"/>);
    /// (2) своего имени нет — правила нет; (3) имён больше одного — так быть не должно;
    /// (4) имя одно — читаем форму, и тут три разных исхода, а не два:
    /// **форма не прочиталась** → <see cref="FirewallPresence.Unverified"/> («есть, но проверить
    /// не удалось»); **порт другой** → <see cref="FirewallPresence.Other"/>; **в выводе есть ЧУЖАЯ
    /// программа** → тоже «не такое»; **программы в выводе нет вовсе** → снова
    /// <see cref="FirewallPresence.Unverified"/> (так печатает <c>netsh</c> без <c>verbose</c>).
    /// </summary>
    public static FirewallState Read(FirewallAnswer byName, FirewallAnswer narrow, int port, string exePath)
    {
        // (1) ЧТЕНИЕ ПО ИМЕНИ. Не прочиталось — это НЕ «правила нет».
        var nameProblem = Unreadable(byName);

        if (nameProblem is not null)
        {
            return new FirewallState(
                FirewallPresence.Unknown,
                Loc.T(PanelStrings.FirewallStateUnknownFormat, nameProblem));
        }

        var found = CountRules(byName.Output);

        if (found == 0) return Absent();

        if (found > 1)
        {
            return new FirewallState(FirewallPresence.Several, Loc.T(PanelStrings.FirewallStateSeveral));
        }

        // (2) ПРАВИЛО ЕСТЬ. Форму читаем вторым чтением — и «не прочиталось» здесь НЕ значит
        //     «не такое»: это отдельное состояние со своими словами.
        var narrowProblem = Unreadable(narrow);

        if (narrowProblem is not null) return Unverified(narrowProblem);

        // Форма не подтвердилась (правило не включено, не входящее или не для частного профиля) —
        // это заведомо НЕ то, что ставит панель, и это «не такое»: netsh ответил, правило есть,
        // но не в той форме. ⚠️ А вот «прочитать не удалось» — это Unverified выше, и разница
        // между ними ровно та, ради которой заведены оба состояния.
        if (narrow.ExitCode != 0 || CountRules(narrow.Output) != 1)
        {
            return new FirewallState(FirewallPresence.Other, Loc.T(PanelStrings.FirewallStateOther));
        }

        // Порт — первая примета: другой порт значит чужое правило, и это видно всегда.
        if (!ValuePresent(narrow.Output, port.ToString(CultureInfo.InvariantCulture)))
        {
            return new FirewallState(FirewallPresence.Other, Loc.T(PanelStrings.FirewallStateOther));
        }

        if (ValuePresent(narrow.Output, (exePath ?? string.Empty).Trim())) return Ready(port);

        // ПРОГРАММЫ В ВЫВОДЕ НЕТ ВОВСЕ — это «проверить не удалось», а НЕ «не такое»: ровно так
        // печатает netsh, когда его позвали без `verbose` (замер владельца 11.10.2026). А вот
        // ЧУЖАЯ программа в выводе — это уже «не такое», и различить их можно по одному:
        // есть ли в выводе хоть какая-то программа.
        return HasProgram(narrow.Output)
            ? new FirewallState(FirewallPresence.Other, Loc.T(PanelStrings.FirewallStateOther))
            : Unverified("netsh: программы в выводе нет (нужен verbose)");
    }

    /// <summary>Слова о том, что правило есть, но проверить его не удалось. Причина — доводом.</summary>
    public static FirewallState Unverified(string reason) =>
        new(FirewallPresence.Unverified, Loc.T(PanelStrings.FirewallStateUnverifiedFormat, reason));

    /// <summary>
    /// ЕСТЬ ЛИ В ВЫВОДЕ ПРОГРАММА — по ЗНАЧЕНИЮ, а не по метке (метку netsh переводит): значение
    /// пути к программе кончается на <c>.exe</c>. Пустая строка значением не считается.
    /// </summary>
    private static bool HasProgram(string? output) =>
        Lines(output).Any(line => ValueOf(line).EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>Слова о том, что правила нет.</summary>
    public static FirewallState Absent() =>
        new(FirewallPresence.Absent, Loc.T(PanelStrings.FirewallStateAbsent));

    /// <summary>Слова о том, что правило есть и оно наше.</summary>
    public static FirewallState Ready(int port) =>
        new(FirewallPresence.Ready, Loc.T(PanelStrings.FirewallStateReadyFormat, port));

    // ---- СЛОВА. Собраны здесь, а не в окне: «что человеку сказано» — часть решения, и её надо
    //      уметь проверить без экрана (то же правило, что у строк состояния телефона и умений).

    /// <summary>СЛОВА ДО: что именно произойдёт на машине и что Windows спросит подтверждение.</summary>
    public static string BeforeAdd(int port) => Loc.T(PanelStrings.FirewallBeforeAddFormat, port);

    /// <summary>СЛОВА ДО — для снятия правила.</summary>
    public static string BeforeRemove() => Loc.T(PanelStrings.FirewallBeforeRemove);

    /// <summary>Одна строка, пока идёт запрос подтверждения: человек видит, что панель ЖДЁТ его.</summary>
    public static string Working() => Loc.T(PanelStrings.FirewallWorking);

    /// <summary>ОДНА СТРОКА ПОСЛЕ: правило поставлено (и это подтверждено чтением).</summary>
    public static string DoneAdd(int port) => Loc.T(PanelStrings.FirewallDoneAddFormat, port);

    /// <summary>ОДНА СТРОКА ПОСЛЕ: правило убрано.</summary>
    public static string DoneRemove() => Loc.T(PanelStrings.FirewallDoneRemove);

    /// <summary>Человек не дал подтверждения Windows: ничего не изменено.</summary>
    public static string Refused() => Loc.T(PanelStrings.FirewallRefused);

    /// <summary>Не вышло, и названа причина (словами самого состояния или кодом netsh).</summary>
    public static string Failed(string reason) => Loc.T(PanelStrings.FirewallFailedFormat, reason);

    /// <summary>Убрать не удалось: правило на месте (проверено чтением, а не кодом возврата).</summary>
    public static string NotRemoved() => Loc.T(PanelStrings.FirewallNotRemoved);

    /// <summary>
    /// СКОЛЬКО ПРАВИЛ С НАШИМ ИМЕНЕМ в выводе. Считаются ЗНАЧЕНИЯ строк «метка: значение»,
    /// а не метки: метку netsh переводит, а имя правила печатает как есть. Для строки
    /// <c>Rule Name: DshPanel2-Phone-Gate</c> это значение и есть наше имя.
    /// </summary>
    public static int CountRules(string? output)
    {
        var count = 0;

        foreach (var line in Lines(output))
        {
            if (string.Equals(ValueOf(line), RuleName, StringComparison.OrdinalIgnoreCase)) count++;
        }

        return count;
    }

    /// <summary>
    /// ЕСТЬ ЛИ ЭТО ЗНАЧЕНИЕ В ВЫВОДЕ — сверка по значениям, а не по меткам (метки переведены).
    /// Пустое значение не ищется: «пусто» найдётся в любой строке и подтвердит что угодно.
    /// </summary>
    private static bool ValuePresent(string? output, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        foreach (var line in Lines(output))
        {
            if (string.Equals(ValueOf(line), value, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// БЕДА ЧТЕНИЯ, НАЗВАННАЯ ЧЕЛОВЕКУ, — или <c>null</c>, если ответ годный.
    ///
    /// ⚠️ Код <c>1</c> — ЭТО ОТВЕТ, А НЕ БЕДА: так <c>netsh</c> отвечает «правил с такими условиями
    /// нет» (замер 11.10.2026: <c>No rules match the specified criteria.</c>). Всё прочее
    /// (пустой вывод, чужой код, <c>netsh</c> не запустился) — «прочитать не удалось».
    /// </summary>
    private static string? Unreadable(FirewallAnswer answer)
    {
        if (answer is null) return "netsh: no answer";

        if (string.IsNullOrWhiteSpace(answer.Output)) return "netsh: no output";

        return answer.ExitCode is 0 or 1 ? null : "netsh: exit " + answer.ExitCode.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Непустые строки вывода.</summary>
    private static IEnumerable<string> Lines(string? output) =>
        (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Значение строки «метка: значение» — или пусто, если двоеточия нет вовсе.</summary>
    private static string ValueOf(string line)
    {
        var at = line.IndexOf(':', StringComparison.Ordinal);

        return at < 0 ? string.Empty : line[(at + 1)..].Trim();
    }

    /// <summary>
    /// ЗАКЛЮЧИТЬ ЗНАЧЕНИЕ В КАВЫЧКИ — для <c>netsh</c>, который читает командную строку целиком.
    ///
    /// ⚠️ Своих кавычек внутри быть не может, и это не украшение: <c>netsh</c> запускается с
    /// повышением через оболочку (<c>UseShellExecute</c>), где доводы — ОДНА строка, и лишняя
    /// кавычка в пути разобралась бы как чужая команда. Отказ даёт <see cref="Add"/> — до запуска.
    /// </summary>
    private static string Quote(string value) => "\"" + value + "\"";
}
