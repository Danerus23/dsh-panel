using DshPanel.Isolation;
using DshPanel.Localization;

namespace DshPanel.Server;

/// <summary>Итог пробы поверхности: прошла ли, на какой версии, сколько имён признано, и строки отчёта.</summary>
public sealed record EngineRpcProbeResult(
    bool Ok,
    string Version,
    int Recognised,
    int Total,
    IReadOnlyList<string> Lines)
{
    /// <summary>
    /// ФАКТЫ УМЕНИЙ, СОБРАННЫЕ ЭТОЙ ЖЕ ПРОБОЙ — то, из чего отчёт об окружении выводит таблицу
    /// умений. Отдельным полем, а не «пусть отчёт спросит ещё раз»: проба ходит в сеть и стоит
    /// 84 вызова, и второй её прогон ради той же таблицы был бы и медленнее, и врал бы — момент
    /// пробы был бы ДРУГОЙ, чем у вердиктов, которые показаны рядом.
    ///
    /// <c>null</c> — проба не дошла до умений (отказ входа и т.п.). Тогда таблица честно говорит
    /// «не проверялась», а не подставляет вердикты файла подсказок.
    /// </summary>
    public SkillFacts? Facts { get; init; }
}

/// <summary>
/// ПОВЕРХНОСТЬ RPC ДВИЖКА — то, на что панель (и будущая калитка телефона) опирается, — вынесена
/// из самотеста сервера в ОДНУ дверь. Вынос не косметический: ту же пробу обязан прогонять всякий,
/// кто поставил НОВЫЙ движок, — иначе «поставлено» говорилось бы без проверки (этап E3.2,
/// <see cref="EngineLiveProbe"/>).
///
/// **Зачем это в прогоне перед выпуском.** Панель не может позволить себе узнать о смене имён
/// от человека: «движок стал другим» обязан ловиться до людей. Проба держит список имён
/// (<see cref="EngineRpcSurface"/>) и проверяет его ТРЕМЯ способами, каждый из которых ловит
/// своё:
///
/// 1. **Опись движка против списка панели** — читаются объявления, которые движок генерирует
///    у себя (`typert.remote-client.d.ts`), и сверка идёт **в обе стороны**: пропавшее у движка
///    имя и появившееся у него имя, которого нет у нас. Второе — не придирка: проба выпускная,
///    и «поверхность изменилась» это ровно тот момент, когда на неё надо посмотреть;
/// 2. **Живой опрос имён** — каждое имя зовётся с заведомо негодными доводами: движок ищет
///    метод ДО проверки доводов, поэтому «доводы не те» доказывает, что имя есть. Обратная
///    сторона обязательна: выдуманное имя движок признавать НЕ должен — иначе признак
///    «признал» не значит ничего;
/// 3. **Один настоящий вызов** (`session/list`) — доказывает, что наш конверт движок принимает
///    целиком, а не только узнаёт имена. Контроль перед ним: без cookie движок обязан
///    отказать, иначе «принял» ничего не доказывает.
///
/// ⚠️ **Чего проба НЕ делает и почему.** Потоковые методы (`session/control`, `session/follow`,
/// `workspace/follow`, `workspaceFiles/changes`) живут на своём канале `/api/remote.mux`;
/// одиночным вызовом они не отвечают, поэтому у них проверяется только «имя признано»,
/// а содержимое потока — нет. Вызовы идут по **своему** серверу прогона (свой порт, свой
/// одноразовый `DSH_HOME`), поэтому даже методы, меняющие состояние, трогают только этот
/// выброшенный движок. Значение cookie и содержимое ответов в отчёт не попадают.
///
/// ⚠️ **Чей это порт.** Проба НИЧЕГО не предполагает про 3080/3081: порт и ссылку входа ей дают
/// снаружи, и на свободном порту с временным домом она работает ровно так же (так её и зовёт
/// <see cref="EngineLiveProbe"/>).
/// </summary>
public static class EngineRpcProbe
{
    /// <summary>
    /// Проба поверхности RPC на ЖИВОМ движке (объявления + вход + живой опрос + умения).
    ///
    /// <paramref name="timeout"/> — срок на сетевую часть пробы; пусто — ровно те значения, что
    /// стояли здесь до выноса (вход — 20 с, умения — 15 с), и это не мелочь: вывод
    /// <c>--server-selftest</c> обязан остаться дословно тем же.
    /// </summary>
    public static EngineRpcProbeResult Measure(
        string? binPath,
        string? entryUrl,
        int port,
        AppPaths paths,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var loginTimeout = timeout ?? TimeSpan.FromSeconds(20);
        var skillTimeout = timeout ?? TimeSpan.FromSeconds(15);

        var lines = new List<string>();
        var ok = true;

        void Check(string what, bool passed)
        {
            if (!passed) ok = false;
            lines.Add($"{what} = {passed}");
        }

        // --- (1) опись движка против закреплённого списка ---

        var declaration = EngineRpcDeclarations.Read(binPath);

        lines.Add($"движок: версия «{declaration.Version}»; список панели снят на «{EngineRpcSurface.MeasuredOnVersion}» ({EngineRpcSurface.MeasuredOn}); линия 0.2.x знакома по «{EngineRpcSurface.KnownOnVersion}» ({EngineRpcSurface.KnownOn})");
        lines.Add($"  объявлений у движка: {declaration.Endpoints.Count}; в списке панели: {EngineRpcSurface.Methods.Count} (нужных {EngineRpcSurface.RequiredTotal}, снятых с надобности {EngineRpcSurface.Retired.Count}); знакомых новых у движка: {EngineRpcSurface.KnownAddedPresent(declaration.Endpoints)} из {EngineRpcSurface.KnownAddedCount}");

        Check("опись движка прочитана", declaration.Endpoints.Count > 0);

        if (declaration.Endpoints.Count > 0)
        {
            // ⚠️ Сверка идёт по ТРЁМ разным множествам, и это не педантизм: нужное имя — провал
            // при пропаже; снятое с надобности (7 имён линии 0.2.x) — законная потеря; незнакомое
            // имя у движка — тревога «поверхность изменилась», и она обязана ронять пробу.
            var goneRequired = EngineRpcSurface.MissingRequired(declaration.Endpoints);

            Check(
                "движок объявляет КАЖДОЕ нужное панели имя"
                    + (goneRequired.Count > 0 ? $" — ПРОПАЛИ: {string.Join(", ", goneRequired.Take(10))}" : string.Empty),
                goneRequired.Count == 0);

            var lost = EngineRpcSurface.LostKnown(declaration.Endpoints);
            lines.Add(
                $"  снятые с надобности имена, которых движок больше не объявляет: {lost.Count} из {EngineRpcSurface.Retired.Count}"
                + " (панель их не зовёт — потеря законна, а не беда)");

            var unknown = EngineRpcSurface.UnknownAdded(declaration.Endpoints);

            Check(
                "у движка не появилось имён, с которыми панель не знакома"
                    + (unknown.Count > 0 ? $" — ПОЯВИЛИСЬ: {string.Join(", ", unknown.Take(10))}" : string.Empty),
                unknown.Count == 0);
        }

        // --- (2) вход: обмен ссылки на cookie и контроль без cookie ---

        var login = EngineRpcLogin.Exchange(entryUrl, loginTimeout);
        Check($"вход по ссылке входа состоялся (код {login.StatusCode})", login.Ok);

        var root = $"http://127.0.0.1:{port}";

        using (var anonymous = new EngineRpcClient(root, string.Empty, TimeSpan.FromSeconds(15)))
        {
            var control = anonymous.Call(EngineRpcSurface.RealCallEndpoint);
            Check(
                $"без cookie движок не пускает (ответ {control.Kind}, код {control.StatusCode})",
                control.Kind == EngineRpcAnswerKind.Unauthorized);
        }

        if (!login.Ok)
        {
            // Без входа сверять имена нечем — и это честнее, чем «признал» на отказе входа.
            return new EngineRpcProbeResult(
                false, declaration.Version, 0, EngineRpcSurface.RequiredTotal, lines);
        }

        // --- (3) живой опрос имён, обратная сторона и настоящий вызов ---

        using var client = new EngineRpcClient(root, login.CookieHeader, TimeSpan.FromSeconds(10));

        var unrecognised = new List<string>();
        var recognisedNames = new List<string>();
        var recognised = 0;

        // ⚠️ Зовутся ТОЛЬКО нужные имена (77), а не все 84: снятые с надобности семь панель живьём
        // не зовёт вовсе, и их отсутствие у 0.2.x не даёт права объявить годный движок негодным.
        foreach (var method in EngineRpcSurface.Required)
        {
            var answer = client.Call(method.Endpoint, EngineRpcSurface.InvalidPayload);

            if (answer.MethodExists)
            {
                recognised++;
                recognisedNames.Add(method.Endpoint);
            }
            else
            {
                unrecognised.Add($"{method.Endpoint} ({answer.Kind}, код {answer.StatusCode}, «{answer.Code}»)");
            }
        }

        lines.Add($"живой опрос: признано {recognised} из {EngineRpcSurface.RequiredTotal} нужных имён");

        Check(
            "движок признаёт КАЖДОЕ нужное имя из списка панели"
                + (unrecognised.Count > 0 ? $" — НЕ ПРИЗНАЛ: {string.Join("; ", unrecognised.Take(10))}" : string.Empty),
            unrecognised.Count == 0);

        var bogus = client.Call(EngineRpcSurface.BogusEndpoint, EngineRpcSurface.InvalidPayload);
        Check(
            $"выдуманное имя движок НЕ признаёт (ответ {bogus.Kind}, код {bogus.StatusCode})",
            !bogus.MethodExists);

        var real = client.Call(EngineRpcSurface.RealCallEndpoint, EngineRpcSurface.RealCallPayload);
        lines.Add($"  настоящий вызов {EngineRpcSurface.RealCallEndpoint}: вид {real.Kind}, код HTTP {real.StatusCode}" + (real.Code.Length > 0 ? $", код движка «{real.Code}»" : string.Empty));

        Check(
            $"настоящий вызов ({EngineRpcSurface.RealCallEndpoint}) движок принял",
            real.Kind == EngineRpcAnswerKind.Ok);

        // --- (4) УМЕНИЯ: что из этого панель умеет с ЭТИМ движком ---
        //
        // Имена вызовов и умения — РАЗНЫЕ вопросы, и здесь второй: «признано 84 из 84» ничего не
        // говорит о том, есть ли у панели ключ модели, раскладка профиля и открывается ли канал
        // управления. Умения печатаются СТРОКАМИ, у каждого — своё «есть» или «нет» с причиной
        // ключом словаря, и пометка, КАК это проверено (живым вызовом · по объявлению · по файлу ·
        // по версии). Требование владельца, этап E2.
        var skillFacts = EngineSkillProbe.Gather(
            new SkillTarget(root, login.CookieHeader, port, entryUrl ?? string.Empty),
            paths,
            declaration.Version,
            recognisedNames,
            timeout: skillTimeout);

        var skills = EngineSkills.Measure(skillFacts);

        lines.Add(Loc.T(SkillKeys.Title));

        foreach (var skill in skills)
        {
            // Строку умения собирает ОДНА дверь (`SkillPhrases`): её же показывает отчёт
            // об окружении, и разойтись пробе с окном нечем.
            lines.Add("  " + SkillPhrases.Line(skill));
        }

        lines.Add("  " + SkillPhrases.Summary(skills));
        lines.Add("  " + SkillPhrases.Versions(declaration.Version, string.Empty));

        Check(
            $"проба собрала вердикты по всем умениям ({EngineSkills.Confirmed(skills)} из {skills.Count} подтверждено)",
            skills.Count == EngineSkills.All.Count);

        // ⚠️ В ПРОГОНЕ С ЖИВЫМ ДВИЖКОМ НИ ОДНО УМЕНИЕ НЕ ИМЕЕТ ПРАВА ОСТАТЬСЯ «НЕ ПРОВЕРЯЛАСЬ»:
        // сервер поднят, вход получен — значит проба обязана была спросить. «Не проверялась»
        // здесь означала бы, что проба измеряет, но не считает своё измерение сделанным (ровно этот
        // дефект и нашёлся 10.10.2026: «5 из 8», хотя движок отвечал). Отказ движка — НЕ повод
        // молчать: он читается как «известна беда» с причиной, и это проверку проходит.
        Check(
            $"живая проба СПРОСИЛА движок (не проверялось: {EngineSkills.Unchecked(skills)})",
            EngineSkills.Unchecked(skills) == 0);

        // ⚠️ Отсутствие умения — НЕ провал прогона: проба обязана его НАЗВАТЬ. Провалом считается
        // только то, что вердиктов не столько, сколько умений (проба ослепла), и это проверено выше.
        var missing = EngineSkills.Missing(skills);
        if (missing.Count > 0)
        {
            lines.Add("  не хватает умений: " + string.Join(
                ", ",
                missing.Select(v => Loc.T(SkillKeys.Name(v.Skill)))));
        }

        return new EngineRpcProbeResult(
            ok, declaration.Version, recognised, EngineRpcSurface.RequiredTotal, lines)
        {
            // ФАКТЫ УМЕНИЙ — ЗДЕСЬ ЖЕ, из этой самой пробы (см. комментарий у самого поля).
            // Момент ставит тот, кто пробу ЗАКАЗАЛ: он один знает, когда она шла.
            Facts = skillFacts,
        };
    }
}
