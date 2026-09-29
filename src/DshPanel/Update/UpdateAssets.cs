using System.Text.Json;

namespace DshPanel.Update;

/// <summary>Одно вложение выпуска: имя файла и адрес, откуда его берут.</summary>
public sealed record ReleaseAsset(string Name, string Url);

/// <summary>
/// ЧТО ИЗ ВЫПУСКА НУЖНО ПАНЕЛИ, чтобы обновиться. Три вложения, и у каждого своя роль:
/// архив панели (им и заменяются файлы), установщик (уезжает рядом — он же уходит в полную копию)
/// и файл контрольных сумм (единственное доказательство, что скачался тот самый архив).
///
/// **Запись, а не три поля у вызывающего.** Пустая ссылка — это «вложения нет», и разбирать
/// её каждый раз заново значило бы однажды посчитать пустое место за готовую сборку: панель
/// сказала бы «обновление готово» вместо честного отказа. Здесь пустоту видно одним вопросом
/// (<see cref="HasArchive"/>, <see cref="HasSums"/>, <see cref="HasSetup"/>).
/// </summary>
public sealed record ReleaseAssets(string ArchiveUrl, string SetupUrl, string SumsUrl)
{
    /// <summary>В выпуске не нашлось ни одного нужного вложения.</summary>
    public static ReleaseAssets None { get; } = new(string.Empty, string.Empty, string.Empty);

    /// <summary>Архив панели есть. Без него обновляться нечем.</summary>
    public bool HasArchive => ArchiveUrl.Length > 0;

    /// <summary>Установщик опубликован. Его отсутствие — не отказ: выпуск может быть без него.</summary>
    public bool HasSetup => SetupUrl.Length > 0;

    /// <summary>Файл контрольных сумм опубликован. Без него обновляться НЕЛЬЗЯ (см. <see cref="UpdateSums"/>).</summary>
    public bool HasSums => SumsUrl.Length > 0;
}

/// <summary>
/// ВЫБОР ВЛОЖЕНИЙ ИЗ ВЫПУСКА — чистые функции от разобранного ответа GitHub.
///
/// Ни сети, ни файлов здесь нет: адрес вложения приходит значением, поэтому выбор проверяется
/// перебором, а «ходит ли панель в GitHub» решается отдельно и по праву (<c>HttpUpdateClient</c>).
///
/// **Имена вложений не выдумываются.** Они те же, что у панели 1.x (<c>dsh-tray\UpdateService.cs</c>):
/// их кладёт в выпуск сборка, и своё имя здесь означало бы панель, которая ищет вложение,
/// которого в выпуске нет. Вариант сборки 1.x (<c>DshPanel-selfcontained.zip</c>) здесь
/// намеренно НЕ выбирается: 2.0 раздаётся одним exe (решение о способе раздачи — за дирижёром),
/// и подставлять «похожий» архив нельзя — он либо не запустится, либо потянет за собой среду.
///
/// ⚠️ <see cref="Pick"/> нарочно НЕ сглаживает пустоту: вложение без адреса — это отсутствие
/// вложения, а не «скачаем пустоту». Пустая ссылка наружу не выходит никогда.
/// </summary>
public static class UpdateAssets
{
    /// <summary>Архив панели: им заменяются файлы. Имя закреплено выпуском.</summary>
    public const string ArchiveName = "DshPanel.zip";

    /// <summary>Установщик выпуска: лежит рядом с панелью и уходит в полную копию.</summary>
    public const string SetupName = "dsh-panel-setup.exe";

    /// <summary>Файл контрольных сумм выпуска: без него обновление не готовится вовсе.</summary>
    public const string SumsName = "SHA256SUMS.txt";

    /// <summary>
    /// Разбор массива <c>assets</c> из ответа GitHub. Отдельной чистой функцией, а не внутри
    /// запроса: проверки гоняют ИМЕННО разбор, подставляя записанный ответ, и ни один живой
    /// запрос для этого не нужен.
    ///
    /// Чего в ответе нет — то пусто, и это не ошибка: вложение без имени или без адреса просто
    /// не попадает в список. Исключение наружу не летит: чужой JSON — обычное дело.
    /// </summary>
    public static IReadOnlyList<ReleaseAsset> Parse(JsonElement root)
    {
        var assets = new List<ReleaseAsset>();

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("assets", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return assets;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var name = Text(item, "name");
            if (name.Length == 0) continue;

            assets.Add(new ReleaseAsset(name, Text(item, "browser_download_url")));
        }

        return assets;
    }

    /// <summary>
    /// Выбор трёх нужных вложений из всего, что опубликовано в выпуске.
    ///
    /// Сверка имени — без учёта регистра (обычай GitHub и сборки не держат регистр одинаково),
    /// но ТОЧНО по имени: «DshPanel.zip.bak» или «DshPanel-selfcontained.zip» нашим вложением
    /// не считаются. Из одинаковых имён берётся ПЕРВОЕ с непустым адресом: повтор имени в выпуске —
    /// ошибка сборки, и выбирать «последнее попавшееся» значило бы гадать.
    /// </summary>
    public static ReleaseAssets Pick(IEnumerable<ReleaseAsset>? assets)
    {
        if (assets is null) return ReleaseAssets.None;

        var archive = string.Empty;
        var setup = string.Empty;
        var sums = string.Empty;

        foreach (var asset in assets)
        {
            if (asset.Name.Length == 0 || asset.Url.Length == 0) continue;

            if (archive.Length == 0 && Named(asset.Name, ArchiveName)) archive = asset.Url;
            else if (setup.Length == 0 && Named(asset.Name, SetupName)) setup = asset.Url;
            else if (sums.Length == 0 && Named(asset.Name, SumsName)) sums = asset.Url;
        }

        return new ReleaseAssets(archive, setup, sums);
    }

    /// <summary>Наше ли это вложение — по имени, без учёта регистра и без сглаживания.</summary>
    private static bool Named(string name, string wanted) =>
        string.Equals(name.Trim(), wanted, StringComparison.OrdinalIgnoreCase);

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim()
            : string.Empty;
}
