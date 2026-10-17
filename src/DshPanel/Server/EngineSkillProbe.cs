using DshPanel.Isolation;

namespace DshPanel.Server;

/// <summary>
/// КУДА СМОТРИТ ПРОБА УМЕНИЙ: адрес работающего движка и cookie входа. Отдельный набор, потому что
/// пробе ВСЁ РАВНО, чей это движок — свой одноразовый или тот, что стоит у человека. Умения
/// проверяются у любого, и это требование владельца: «проба умеет проверять РАБОТАЮЩИЙ движок,
/// а не только чистый одноразовый».
/// </summary>
public sealed record SkillTarget(string BaseUrl, string Cookie, int Port, string EntryLink)
{
    public SkillTarget(string baseUrl, string cookie, int port) : this(baseUrl, cookie, port, string.Empty)
    {
    }
}

/// <summary>
/// ИЗМЕРЕНИЕ УМЕНИЙ — то, что требует сети и диска. Решение по измеренному живёт отдельно
/// (<see cref="EngineSkills.Measure"/>), и это не формальность: так вердикты проверяются
/// подставными фактами (движок с умением и движок без него), а сеть в проверках не нужна вовсе.
///
/// **Что здесь делается ЖИВЫМ вызовом, а что читается.** Живым вызовом — список сессий (чтение,
/// ничего не меняет) и рукопожатие канала управления. По объявлению — задача и отмена: звать их
/// значит начать или прервать работу агента. По файлу — ключ модели и раскладка профиля.
/// По версии — сама версия движка. Каждое «есть» несёт пометку, КАК оно добыто.
/// </summary>
public static class EngineSkillProbe
{
    /// <summary>Собрать факты по работающему движку.</summary>
    /// <param name="target">Адрес и cookie входа.</param>
    /// <param name="paths">Пути прогона: дом движка, файл ключа.</param>
    /// <param name="engineVersion">Версия движка, прочитанная из его пакета (пусто — не прочиталась).</param>
    /// <param name="recognised">Имена вызовов, которые движок признал (их даёт проба поверхности).</param>
    /// <param name="exists">Есть ли файл (по умолчанию диск; подставляется проверками).</param>
    /// <param name="directoryExists">Есть ли каталог (по умолчанию диск; подставляется проверками).</param>
    /// <param name="timeout">Сколько ждать ответа на вызов и рукопожатие.</param>
    public static SkillFacts Gather(
        SkillTarget target,
        AppPaths paths,
        string engineVersion,
        IReadOnlyCollection<string> recognised,
        Func<string, bool>? exists = null,
        Func<string, bool>? directoryExists = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(paths);

        var wait = timeout ?? TimeSpan.FromSeconds(15);
        var fileExists = exists ?? File.Exists;
        var dirExists = directoryExists ?? Directory.Exists;

        var answered = false;
        var controlOpened = false;

        using (var client = new EngineRpcClient(target.BaseUrl, target.Cookie, wait))
        {
            // Список сессий — ЧТЕНИЕ: его звать не страшно, и он же доказывает, что вход работает.
            var list = client.Call(EngineRpcSurface.RealCallEndpoint, EngineRpcSurface.RealCallPayload);
            answered = list.Kind == EngineRpcAnswerKind.Ok;

            // Канал управления — рукопожатие потока: одиночным вызовом он не отвечает.
            controlOpened = client.MuxHandshake(wait);
        }

        return new SkillFacts
        {
            // ⚠️ ПРОБА БЫЛА — и это надо сказать ЯВНО. `Probed` решает, читается ли живое умение
            // как «проверена/беда» или как «не проверялась»; без этой строки проба, которая честно
            // спросила движок и получила ответ, показывала бы все живые умения как непроверенные
            // (дефект, найденный прогоном `--server-selftest` в изоляции 10.10.2026: «5 из 8»,
            // хотя движок отвечал). «Проба была» — это факт, а не вывод из её успеха: отказ движка
            // обязан читаться как БЕДА с причиной, а не как «не проверялась».
            Probed = true,
            EntryLinkParsed = EntryLinkDecisions.MatchesPort(target.EntryLink, target.Port),
            SessionListAnswered = answered,
            RecognisedEndpoints = recognised,
            ControlStreamOpened = controlOpened,
            ModelKeyFile = fileExists(paths.CredentialsPath),
            ProfileLayout = dirExists(Path.Combine(paths.DshHome, "profiles")),
            EngineVersion = engineVersion ?? string.Empty,
        };
    }

    /// <summary>
    /// Факты «ничего не вышло» — для случая, когда движка нет вовсе. Не выдумка: это ЧЕСТНЫЙ
    /// ответ «ни одного умения не подтверждено», и строка отчёта скажет причину словами.
    /// </summary>
    public static SkillFacts None(AppPaths paths) => new()
    {
        ModelKeyFile = File.Exists(paths.CredentialsPath),
        ProfileLayout = Directory.Exists(Path.Combine(paths.DshHome, "profiles")),
    };
}
