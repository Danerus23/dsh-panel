using System.Globalization;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// ПЕРВЫЙ ЛИ ЭТО ЗАПУСК ДВИЖКА — признак и надпись, которая от него зависит.
///
/// **От какого случая (слово владельца 18.10.2026).** Панель на каждом подъёме сервера писала
/// «Запускаю… **первый запуск** достраивает профиль, это до минуты» — и говорила неправду:
/// профиль DSH не пересоздавался (`profiles`, `sessions`, `skills` лежали с 26.09), сервер
/// поднимался обычным запуском. Человек читал обещание минуты ожидания там, где ждать было
/// нечего, и объяснение, которого не требовалось.
///
/// **Признак назван и он один.** «Первый запуск» — это когда в домашнем каталоге движка
/// (<c>DSH_HOME</c>, у панели это СВОЙ каталог, а не <c>~/.dsh</c> человека) нет НИ ОДНОГО из
/// трёх каталогов, которые движок заводит сам: <see cref="Markers"/>. Хоть один есть — профиль
/// уже строился, и обещать «первый запуск» нечего. Каталог дома отсутствует целиком — это
/// честно первый запуск (строить нечего, всё появится).
///
/// ⚠️ **Признак — ПАРАМЕТР решения, а не чтение окружения внутри надписи.** Так же, как момент
/// и смещение в кадрах (t13): решение о словах проверяется без каталогов, а чтение каталогов —
/// без окон. Иначе проверка надписи требовала бы настоящего профиля движка.
///
/// **Мутации, которые обязаны упасть:** «надпись всегда обещает первый запуск» · «надпись никогда
/// не обещает первый запуск» · «признак не смотрит каталоги (всегда первый)».
/// </summary>
public static class EngineFirstRun
{
    /// <summary>
    /// Каталоги, которые движок заводит у себя дома при первом запуске. Список — из замера
    /// профиля владельца 18.10.2026 (`profiles`, `sessions`, `skills`, все с 26.09) и из
    /// <see cref="EngineSkillProbe"/> — того места, где панель и так смотрит на профиль.
    /// </summary>
    public static readonly string[] Markers = { "profiles", "sessions", "skills" };

    /// <summary>
    /// Похоже ли, что профиль движка строится ВПЕРВЫЕ. Пустой или неизвестный каталог дома
    /// признаком не считается: «нечем мерить» — это не «первый запуск», и обещать по нему
    /// нечего (<c>false</c>).
    /// </summary>
    public static bool Looks(string? engineHome)
    {
        if (string.IsNullOrWhiteSpace(engineHome)) return false;

        foreach (var marker in Markers)
            if (Directory.Exists(Path.Combine(engineHome, marker))) return false;

        return true;
    }

    /// <summary>Надпись занятости на время подъёма сервера.</summary>
    public static string StartingLabel(bool firstRunLooks) =>
        firstRunLooks ? PanelStrings.ServerStartingFirstRun : PanelStrings.ServerStarting;

    /// <summary>Подробность состояния «сервер ещё поднимается» (порт назван).</summary>
    public static string StillStartingDetail(bool firstRunLooks, int port) =>
        string.Format(
            CultureInfo.CurrentCulture,
            firstRunLooks ? PanelStrings.SrvEngineStillStartingFirstRunFormat : PanelStrings.SrvEngineStillStartingFormat,
            port);
}
