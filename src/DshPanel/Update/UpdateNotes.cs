namespace DshPanel.Update;

/// <summary>
/// ЧТО ЗА СТРОКА перед человеком в заметках к выпуску.
///
/// Вид назван перечислением, а не цветом и не размером шрифта: решение «это заголовок, а это
/// пункт списка» принимается при РАЗБОРЕ и проверяется прогоном, а как именно его показать —
/// дело окна (<c>Views\UpdateNotesView.cs</c>). Так разбор заметок можно перебрать проверками,
/// не поднимая ни окна, ни рабочего стола владельца.
/// </summary>
public enum UpdateNoteKind
{
    /// <summary>Обычный абзац.</summary>
    Text,

    /// <summary>Заголовок выпуска (одна или две решётки).</summary>
    Heading,

    /// <summary>Подзаголовок внутри заметок (три решётки и глубже).</summary>
    SubHeading,

    /// <summary>Пункт списка с маркером («- », «* », «+ »).</summary>
    Bullet,

    /// <summary>Нумерованный пункт («1. », «1) ») — номер остаётся в тексте.</summary>
    Numbered,

    /// <summary>Цитата («&gt; »).</summary>
    Quote,

    /// <summary>Строка ограждённого блока (код или пример).</summary>
    Code,

    /// <summary>Пустая строка — она и есть абзацный отступ.</summary>
    Blank,
}

/// <summary>Одна строка заметок: вид и текст (у списка маркер уже снят либо оставлен по правилу).</summary>
public sealed record UpdateNoteLine(UpdateNoteKind Kind, string Text);

/// <summary>
/// РАЗБОР ЗАМЕТОК К ВЫПУСКУ НА ЧИТАЕМЫЕ СТРОКИ — БЕЗ ВНЕШНИХ БИБЛИОТЕК.
///
/// **Зачем это вообще.** Заметки приходят с GitHub ОДНИМ куском текста (разметка Markdown).
/// Показанный как одна простыня, он читается плохо — а владелец жаловался на это дважды
/// (`docs\DESIGN.md`, пункты 32–33: «сплошной неструктурированный текст» про план наката
/// и раздел «Сервер»). Поэтому заметки разбираются на строки со СВОИМ видом: заголовок,
/// подзаголовок, пункт списка, абзац, пустая строка. Окно рисует каждый вид по-своему —
/// строки, отступы и короткие абзацы вместо полотна.
///
/// **Почему свой разбор, а не библиотека.** Панель раздаётся одним exe и собирается из
/// трёх источников строк; тянуть ради заголовков и дефисов чужой Markdown-разбор — это
/// новая зависимость, новая версия и новая уязвимость ради десятка строк кода. Здесь
/// разбирается ровно то, чем заметки выпуска пользуются: заголовки, три вида списков,
/// цитата, ограждённый блок и пустые строки.
///
/// **Чего разбор НЕ делает.** Он не понимает HTML, таблиц, картинок, ссылок и вложенных
/// списков: заметки выпуска ими не пользуются, а «поддержать на всякий случай» здесь значит
/// завести непроверяемый код. Ссылки остаются текстом — как их и видно в теле выпуска.
///
/// **Знаки разметки внутри строки снимаются** (<c>**</c>, <c>`</c>, <c>__</c>): человеку нужен
/// текст, а не звёздочки. Снятие — ДО определения вида, чтобы «- **Важно**» осталось пунктом
/// списка, а не превратилось в абзац.
///
/// Строки не переносятся по ширине: перенос делает окно (<c>TextWrapping</c>), потому что
/// ширина окна — не дело разбора.
/// </summary>
public static class UpdateNotes
{
    /// <summary>
    /// Разобрать заметки в строки. Пустое и пробельное тело — пустой список: выдумывать
    /// из пустоты «заметок нет» текстом здесь нельзя, это дело окна.
    ///
    /// Крайние пустые строки срезаются, а ИДУЩИЕ ПОДРЯД схлопываются в одну: в теле выпуска
    /// между абзацами их бывает по три, и три пустых строки в окне выглядели бы дырой.
    /// Пустая строка ВНУТРИ заметок остаётся — она и есть абзацный отступ.
    /// </summary>
    public static IReadOnlyList<UpdateNoteLine> Parse(string? notes)
    {
        var body = (notes ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        if (body.Trim().Length == 0) return Array.Empty<UpdateNoteLine>();

        var lines = new List<UpdateNoteLine>();
        var fence = ' ';

        foreach (var raw in body.Split('\n'))
        {
            var text = Clean(raw).TrimEnd();

            // Внутри ограждения ничего не разбирается: там пример, а не заметки. Сами знаки
            // ограждения человеку не показываются — они служебные.
            if (fence != ' ')
            {
                if (IsFence(text, fence)) fence = ' ';
                else Add(lines, UpdateNoteKind.Code, text.Trim());

                continue;
            }

            if (IsFenceStart(text, out var marker))
            {
                fence = marker;
                continue;
            }

            Add(lines, Kind(text), Text(text));
        }

        // Пустая строка в начале и в конце — не структура, а край файла.
        while (lines.Count > 0 && lines[0].Kind == UpdateNoteKind.Blank) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Kind == UpdateNoteKind.Blank) lines.RemoveAt(lines.Count - 1);

        return lines;
    }

    /// <summary>
    /// Вид строки по её началу. Порядок проверок — от самого частного к общему: заголовок,
    /// потом списки, потом цитата, потом «всё остальное — абзац».
    /// </summary>
    private static UpdateNoteKind Kind(string text)
    {
        if (text.Trim().Length == 0) return UpdateNoteKind.Blank;

        var trimmed = text.TrimStart();

        var level = HeadingLevel(trimmed);
        if (level >= 3) return UpdateNoteKind.SubHeading;
        if (level > 0) return UpdateNoteKind.Heading;

        if (IsBullet(trimmed)) return UpdateNoteKind.Bullet;
        if (IsNumbered(trimmed)) return UpdateNoteKind.Numbered;
        if (trimmed.StartsWith('>')) return UpdateNoteKind.Quote;

        // Горизонтальная линия («---», «***») — тоже разрыв, а не текст: человеку она говорит
        // «дальше другая мысль», и показать её знаками значило бы показать разметку.
        return IsRule(trimmed) ? UpdateNoteKind.Blank : UpdateNoteKind.Text;
    }

    /// <summary>
    /// Текст строки в том виде, в каком его читает человек: маркер списка снят (кроме номера —
    /// он часть текста), знаки разметки убраны. Заголовок остаётся БЕЗ решёток: их рисует окно
    /// размером и жирностью, а не текст.
    /// </summary>
    private static string Text(string text)
    {
        var trimmed = text.TrimStart();

        var level = HeadingLevel(trimmed);
        if (level > 0) return Markup(trimmed[level..].Trim().TrimEnd('#').Trim());

        if (IsBullet(trimmed)) return Markup(trimmed[1..].Trim());

        if (trimmed.StartsWith('>')) return Markup(trimmed[1..].Trim());

        return Markup(trimmed);
    }

    /// <summary>
    /// Знаки разметки внутри строки: жирность и «код» остаются словами, а не звёздочками
    /// и обратными кавычками. Ссылки не разбираются намеренно: чужой адрес в тексте заметок
    /// — это текст, а открывать его панель не имеет права (браузер открывается по щелчку
    /// человека и только там, где он ждёт).
    /// </summary>
    private static string Markup(string text) =>
        text.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);

    /// <summary>Сколько решёток в начале строки (0 — не заголовок). «#» — заголовок, «####» — тоже.</summary>
    private static int HeadingLevel(string trimmed)
    {
        var level = 0;

        while (level < trimmed.Length && trimmed[level] == '#') level++;

        if (level == 0 || level >= trimmed.Length) return 0;

        // Решётки без пробела после них — не заголовок: «#1» это обычный текст.
        return trimmed[level] == ' ' ? level : 0;
    }

    private static bool IsBullet(string trimmed) =>
        trimmed.Length >= 2 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') && trimmed[1] == ' ';

    /// <summary>«1. » и «1) » — номер остаётся в тексте: человеку он и нужен.</summary>
    private static bool IsNumbered(string trimmed)
    {
        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;

        if (digits == 0 || digits + 1 >= trimmed.Length) return false;

        return (trimmed[digits] == '.' || trimmed[digits] == ')') && trimmed[digits + 1] == ' ';
    }

    /// <summary>Горизонтальная линия: три и больше одного и того же знака, и больше ничего.</summary>
    private static bool IsRule(string trimmed)
    {
        if (trimmed.Length < 3) return false;

        var symbol = trimmed[0];
        if (symbol != '-' && symbol != '*' && symbol != '_') return false;

        return trimmed.All(character => character == symbol);
    }

    /// <summary>
    /// Рядом стоящие пустые строки схлопываются в одну: абзацный отступ нужен один, а не тот,
    /// сколько их набрал автор заметок.
    /// </summary>
    private static void Add(List<UpdateNoteLine> lines, UpdateNoteKind kind, string text)
    {
        if (kind == UpdateNoteKind.Blank && lines.Count > 0 && lines[^1].Kind == UpdateNoteKind.Blank) return;

        lines.Add(new UpdateNoteLine(kind, text));
    }

    /// <summary>Открывающее ограждение: три и больше <c>`</c> или <c>~</c> с отступом не больше трёх.</summary>
    private static bool IsFenceStart(string text, out char marker)
    {
        marker = ' ';

        var trimmed = text.TrimStart();
        if (text.Length - trimmed.Length > 3 || trimmed.Length < 3) return false;

        var symbol = trimmed[0];
        if (symbol != '`' && symbol != '~') return false;

        var run = 0;
        while (run < trimmed.Length && trimmed[run] == symbol) run++;
        if (run < 3) return false;

        marker = symbol;
        return true;
    }

    /// <summary>Закрывающее ограждение: тот же знак, не меньше знаков, и больше ничего на строке.</summary>
    private static bool IsFence(string text, char marker)
    {
        var trimmed = text.Trim();

        return trimmed.Length >= 3 && trimmed.All(character => character == marker);
    }

    /// <summary>
    /// Вкладка — четыре пробела: заметки набирают руками, и вкладка внутри строки иначе
    /// выглядела бы одним знаком, а занимала разную ширину.
    /// </summary>
    private static string Clean(string line) => line.Replace("\t", "    ");
}
