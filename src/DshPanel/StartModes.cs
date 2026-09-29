using DshPanel.Headless;
using DshPanel.Localization;

namespace DshPanel;

/// <summary>
/// Разбор режимов запуска — ЧИСТАЯ функция, потому что у неё есть проверка, которая обязана уметь
/// падать, а точка входа <c>Program.Main</c> для тестов недоступна.
///
/// Зачем это вообще. Раньше неизвестный ключ и ключ проверки без обязательного аргумента
/// проваливались мимо всех ветвей и уходили в **обычный запуск** — то есть открывали окно
/// на рабочем столе владельца. Для человека это нормально (он и есть человек), а для скрипта —
/// нет: опечатка в имени проверки («--balans-selftest») выглядела бы как «проверка прошла молча»,
/// а окно появлялось бы у него на экране. Нашёл холодный агент 24.09.2026.
///
/// Отсюда правило: **всё, что начинается с дефиса, обязано быть известно**, иначе — громкий отказ
/// кодом 2 («проверять нечем»). Обычный запуск без аргументов остаётся обычным запуском: это
/// единственный путь, которым идёт человек.
/// </summary>
public static class StartModes
{
    /// <summary>Ключи проверок, ролей и модификаторов. Обычный запуск ключа не имеет вовсе.</summary>
    public static readonly string[] Known = new[]
    {
        "--shot", "--tray-selftest", "--shell-selftest", "--instance-selftest", "--instance-probe",
        "--autostart-selftest", "--settings-selftest", "--balance-selftest", "--server-selftest",
        "--backup-selftest", "--isolation-selftest", "--lang-selftest", "--update-selftest",
        "--with-my-key", "--run-root",

        // Модификатор языка: сам по себе он не проверка, но и опечаткой быть не обязан —
        // им снимают окна на трёх языках. Снимается до разбора режимов, как и `--run-root`
        // (`LanguageDecisions.WithoutSwitch`), и здесь назван ради строки «известные ключи».
        LanguageDecisions.Switch,
    }
        // Ключи двух режимов без окна (копия и накат) — из ОДНОГО места: их разбирает
        // HeadlessDecisions, и второй список тех же имён здесь однажды разошёлся бы с ним.
        .Concat(HeadlessDecisions.Keys)
        .Concat(new[] { Headless.UpdateHeadless.CheckKey, Headless.UpdateHeadless.PrepareKey })
        .ToArray();

    /// <summary>Имя окна съёмки: раздел «Общее» настроек.</summary>
    public const string ShotSettings = "settings";

    /// <summary>Имя окна съёмки: раздел «Баланс и тариф» настроек (второй кадр того же окна).</summary>
    public const string ShotSettingsBalance = "settings-balance";

    /// <summary>
    /// Имя окна съёмки: раздел «Обновление» настроек (третий кадр того же окна). Появился
    /// 28.09.2026 вместе с самим разделом — кадр нужен и проверяющему, и README.
    /// </summary>
    public const string ShotSettingsUpdate = "settings-update";

    /// <summary>Имя окна съёмки: раздел «Сервер» настроек — отчёт окружения группами.</summary>
    public const string ShotSettingsServer = "settings-server";

    /// <summary>Имя окна съёмки: копии и восстановление.</summary>
    public const string ShotBackup = "backup";

    /// <summary>Имя окна съёмки: «О программе».</summary>
    public const string ShotAbout = "about";

    /// <summary>Имя окна съёмки: «Пики и тарифы».</summary>
    public const string ShotPeaks = "peaks";

    /// <summary>Имя окна съёмки: «Обновление панели».</summary>
    public const string ShotUpdate = "update";

    /// <summary>
    /// Имя окна съёмки: «История цен».
    ///
    /// ⚠️ Это имя появилось из-за НАЗВАННОГО пробела, а не «на всякий случай»: окно истории цен
    /// написал рабочий, и он честно сказал, что **кадра у него нет и глазами его никто не видел** —
    /// своего имени в списке съёмки не было, а --shot с незнакомым именем отвергается громко.
    /// То есть новое окно нельзя было посмотреть ВООБЩЕ. Проверить раскладку без кадра нельзя,
    /// поэтому имя добавлено.
    /// </summary>
    public const string ShotPricingHistory = "pricing-history";

    /// <summary>Имя окна съёмки: «Сообщить о проблеме».</summary>
    public const string ShotIssue = "issue";
    /// <summary>
    /// Имена окон, которые умеет снять <c>--shot</c>: <c>--shot &lt;файл&gt; [имя]</c>.
    /// Пустое имя — главное окно панели, и его в списке нет: это «имя не назвали», а не окно.
    ///
    /// ⚠️ **Список здесь ОДИН на всю панель, и это не украшение.** Прежде имена жили двумя
    /// копиями — в описании ключа (строка «известные ключи») и в разборе съёмки
    /// (<c>Program.Shot</c>), — и они разошлись: описание обещало три имени
    /// (<c>settings|backup|about</c>), а съёмка принимала пять. Человек, набравший
    /// «<c>--shot кадр.png peaks</c>», получал отказ «неизвестное окно», хотя окно есть,
    /// а строка «известные ключи» про него молчала (нашёл разведчик 28.09.2026).
    /// Теперь описание строится ИЗ этого списка, а разбор спрашивает <see cref="KnowsShotWindow"/>;
    /// именами из него названы и ветви самой съёмки, поэтому литерала этих имён в <c>Program</c>
    /// больше нет ни одного.
    ///
    /// ⚠️ Порядок здесь — порядок показа в описании ключа; проверка его не требует, но
    /// переставлять просто так не надо: по нему читают строку отказа.
    /// </summary>
    public static readonly string[] ShotWindows =
    {
        ShotSettings, ShotSettingsBalance, ShotSettingsUpdate, ShotSettingsServer, ShotBackup, ShotAbout, ShotPeaks, ShotUpdate,
        ShotPricingHistory, ShotIssue,
    };

    /// <summary>
    /// Знает ли панель такое имя окна съёмки. Спрашивает РАЗБОР (<c>Program.Shot</c>), а список —
    /// тот же, из которого строится описание ключа. Незнакомое имя остаётся громким отказом
    /// кодом 2: это по-прежнему важно (в скриптах съёмки опечатка положила бы в README не тот кадр).
    /// </summary>
    public static bool KnowsShotWindow(string kind) =>
        kind is not null && Array.IndexOf(ShotWindows, kind) >= 0;

    /// <summary>
    /// Почему этот набор аргументов запускать НЕЛЬЗЯ. <c>null</c> — можно, и это ровно два случая:
    /// аргументов нет вовсе (обычный запуск — то есть человек) либо первый аргумент не ключ
    /// (его разбирает платформа).
    ///
    /// Всё, что начинается с дефиса, — отказ, потому что известные режимы до этого рубежа
    /// **не доживают**: каждый опознаётся и возвращает результат раньше, а <c>--run-root</c>
    /// снимается разбором просьбы (<c>RunRequest.WithoutRootSwitch</c>) ещё до режимов.
    /// Значит здесь остаётся одно из трёх: опечатка в имени проверки, ключ-модификатор без режима
    /// либо ключ, который забыли обработать выше. Все три обязаны быть громкими.
    /// </summary>
    public static string? Refuse(string[] rest)
    {
        ArgumentNullException.ThrowIfNull(rest);

        if (rest.Length == 0) return null;

        // Не ключ — не наше дело: аргумент без дефиса разбирает платформа.
        if (!rest[0].StartsWith('-')) return null;

        // Ключ проверки без обязательного аргумента: снимать некуда.
        if (rest[0] == "--shot" && rest.Length < 2)
            return "SHOT ПРОВАЛ: ключ «--shot» указан без имени файла — снимать некуда.";

        // Режимы без окна отвечают на этот вопрос сами: у них обязательный аргумент — ПУТЬ,
        // и отказ «это модификатор, а не проверка» был бы про них неправдой. Спрашиваем их разбор,
        // чтобы текст отказа существовал в ОДНОМ месте.
        if (HeadlessDecisions.IsMode(rest[0]))
        {
            var headless = HeadlessDecisions.Parse(rest);
            return headless is null || headless.Ok ? null : headless.Refusal;
        }

        if (Known.Contains(rest[0]))
        {
            return $"КЛЮЧ БЕЗ РЕЖИМА: «{rest[0]}» — это модификатор, а не проверка. " +
                   "Укажите, что именно делать (например «--isolation-selftest»).";
        }

        return $"НЕИЗВЕСТНЫЙ КЛЮЧ: «{rest[0]}» — такой проверки нет. Известные: " +
               string.Join(", ", Known.Select(Describe)) + ".";
    }

    /// <summary>
    /// Ключ так, как его пишут в командной строке: у трёх ключей обязательный аргумент, и без него
    /// строка «известные ключи» выглядела бы как совет, который не работает. Имена окон съёмки
    /// берутся из <see cref="ShotWindows"/> — второй их копии в панели нет (и не должно быть:
    /// разошедшись, описание обещало бы не то, что принимает разбор).
    /// </summary>
    private static string Describe(string key) => key switch
    {
        "--shot" => "--shot <файл> [" + string.Join("|", ShotWindows) + "]",
        LanguageDecisions.Switch => LanguageDecisions.Switch + " <ru|en|zh>",
        HeadlessDecisions.BackupKey => HeadlessDecisions.BackupKey + " <папка>",
        HeadlessDecisions.RestoreKey => HeadlessDecisions.RestoreKey + " <архив>",
        _ => key,
    };
}
