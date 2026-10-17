using DshPanel.Localization;

namespace DshPanel.Server;

/// <summary>
/// «ВЫЖИМКА» О ВЕРСИЯХ ДВИЖКА — чистые строки для блока движка (решение владельца 11.10.2026:
/// *«добавить СТРОКОЙ, не кнопкой: заметки из файла подсказок в своём поле и ссылка на выпуск»*).
///
/// **Что здесь и чего здесь нет.** Здесь только СБОРКА СТРОК из уже прочитанного файла подсказок
/// (`EngineHints`): заметки и известные беды на языке панели, с подписью, про КАКУЮ версию они.
/// Ни окна, ни сети, ни диска — поэтому проверяется без окна и без подсказок в сети.
///
/// **Почему беды показываются вместе с заметками, а не отдельно.** «Что про неё известно замером» —
/// это и есть выжимка: у непроверенной версии заметка без беды («проба не пройдена, семь имён
/// пропали») была бы рекламой, а не подсказкой. Молчать о бедах панель не имеет права тем более,
/// что имена бед — из наших же замеров.
///
/// ⚠️ **Ничего не выдумываем:** нет подсказок или нет перевода — строк не будет вовсе (окно скажет
/// это словами, а не покажет пустое поле как «всё хорошо»).
/// </summary>
public static class EngineLines
{
    /// <summary>КЛЮЧИ СЛОВ — здесь только имена, фразы живут в трёх словарях.</summary>
    public static class Words
    {
        public const string For = "EngineNotesForFormat";
        public const string Troubles = "EngineNotesTroubles";
    }

    /// <summary>
    /// СТРОКИ ВЫЖИМКИ: сначала про версию, которую ставит главная кнопка (проверенную), потом —
    /// если свежая новее — про неё. Пусто: подсказок нет или сказать нечего.
    /// </summary>
    public static IReadOnlyList<string> Digest(
        EngineHints? hints,
        string? recommended,
        string? fresh,
        bool freshIsNewer,
        string? language)
    {
        var known = hints ?? EngineHints.Empty;
        var lines = new List<string>();

        Add(lines, known, recommended, language);

        if (freshIsNewer) Add(lines, known, fresh, language);

        return lines;
    }

    /// <summary>Одна версия: подпись «про {версия}», заметки и — отдельной подписью — известные беды.</summary>
    private static void Add(List<string> lines, EngineHints hints, string? version, string? language)
    {
        var wanted = (version ?? string.Empty).Trim();
        var hint = wanted.Length == 0 ? null : hints.Find(wanted);

        if (hint is null) return;

        var notes = EngineHints.NotesFor(hint, language);
        var troubles = EngineHints.TroublesFor(hint, language);

        if (notes.Count == 0 && troubles.Count == 0) return;

        lines.Add(Loc.T(Words.For, hint.Version));

        foreach (var note in notes) lines.Add(note);

        if (troubles.Count == 0) return;

        lines.Add(Loc.T(Words.Troubles));

        foreach (var trouble in troubles) lines.Add(trouble);
    }
}
