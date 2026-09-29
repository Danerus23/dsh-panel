using System.Diagnostics;
using System.IO.Compression;
using DshPanel.Backup;

namespace DshPanel.Update;

/// <summary>Чем кончилась распаковка архива: получилось или нет, и что именно помешало.</summary>
public sealed record UpdateUnpack(bool Ok, string Detail)
{
    public static UpdateUnpack Done { get; } = new(true, string.Empty);

    public static UpdateUnpack Fail(string detail) => new(false, detail);
}

/// <summary>
/// ПОДГОТОВКА СКАЧАННОГО АРХИВА: распаковка в папку обновления и сверка ВЕРСИИ ВНУТРИ.
///
/// **Зачем сверять версию, если сумма сошлась.** Сумма доказывает, что скачался тот файл,
/// который лежит в выпуске. Она НЕ доказывает, что в выпуске лежит та сборка, которую обещает
/// номер версии: выпуск мог быть пересобран под тем же номером, вложение могло быть подменено
/// до публикации сумм, а сам номер — уехать вперёд. Урок взят у панели 1.x
/// (<c>dsh-tray\UpdateService.cs</c>): **без этой сверки подменённый архив тихо поставит не то**,
/// и человек узнает об этом по сломанной панели, а не по отказу.
///
/// Распаковка идёт средствами .NET, без 7-Zip: то же решение владельца, что и у копий —
/// «создаёт 7-Zip, распаковывает сама панель». Панель, которой для обновления нужна чужая
/// программа, не обновится там, где этой программы нет.
/// </summary>
public static class UpdateStaging
{
    /// <summary>Имя собранной панели внутри архива выпуска. По нему же проверяется версия.</summary>
    public const string PanelExeName = "DshPanel.exe";

    /// <summary>Имя процесса панели — для проверки, что поднялась именно новая копия.</summary>
    public static string PanelProcessName => Path.GetFileNameWithoutExtension(PanelExeName);

    /// <summary>
    /// Распаковать архив в папку. Папка создаётся, содержимое перезаписывается — папку обновления
    /// движок готовит заранее и пустой.
    ///
    /// **Перед распаковкой архив ЧИТАЕТСЯ отдельным читателем копий** (<see cref="ZipReader"/>).
    /// Архив — чужой файл, и запись вида <c>../../…</c> внутри него это не «странность», а способ
    /// записать файл мимо места распаковки. Такие архивы не распаковываются вовсе, и пропуск
    /// называет себя отказом, а не тихой потерей части файлов.
    ///
    /// Отказ называет причину ТЕХНИЧЕСКИ (это <see cref="UpdateUnpack.Detail"/> — для журнала
    /// и отчёта): фразу для человека собирает дирижёр по ключу отказа.
    /// </summary>
    public static UpdateUnpack Unpack(string archivePath, string folder)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return UpdateUnpack.Fail("архив не найден");

        if (string.IsNullOrWhiteSpace(folder))
            return UpdateUnpack.Fail("не названа папка распаковки");

        try
        {
            var read = ZipReader.Read(archivePath);

            if (!read.Ok) return UpdateUnpack.Fail("архив не читается");
            if (read.Files == 0) return UpdateUnpack.Fail("в архиве нет ни одного файла");
            if (read.Unsafe.Count > 0) return UpdateUnpack.Fail($"записей мимо каталога распаковки: {read.Unsafe.Count}");

            Directory.CreateDirectory(folder);
            ZipFile.ExtractToDirectory(archivePath, folder, overwriteFiles: true);

            return UpdateUnpack.Done;
        }
        catch (Exception error)
        {
            return UpdateUnpack.Fail($"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// ВЕРСИЯ ПАНЕЛИ, СОБРАННОЙ В ЭТОЙ ПАПКЕ, — из свойств самого файла, как делала панель 1.x.
    /// Читать версию из имени папки нельзя: имя придумал тот, кто распаковал, а свойства файла —
    /// тот, кто собрал.
    ///
    /// Пусто — версию прочитать не удалось (файла нет, свойств нет). Пустое значение НЕ совпадает
    /// ни с чем: см. <see cref="VersionMatches"/>.
    /// </summary>
    public static string ReadVersion(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return string.Empty;

        try
        {
            var exe = Path.Combine(folder, PanelExeName);
            if (!File.Exists(exe)) return string.Empty;

            return (FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? string.Empty).Trim();
        }
        catch
        {
            // Версию не прочитать — это «не знаю», а не «совпало»: решение принимает вызывающий.
            return string.Empty;
        }
    }

    /// <summary>
    /// Совпадает ли версия внутри архива с версией выпуска.
    ///
    /// Сравниваются ЧИСЛОВЫЕ части (<see cref="UpdateDecisions.Numeric"/>): .NET дописывает
    /// к <c>ProductVersion</c> хеш сборки, а тег GitHub несёт букву <c>v</c> — и то, и другое
    /// версией не является. При этом **пустое значение не совпадает ни с чем**, в том числе
    /// с пустым: «версию не прочитали» и «в архиве лежит неизвестно что» — это отказ, а не
    /// совпадение. Без этого правила подменённый архив без свойств версии прошёл бы проверку.
    /// </summary>
    public static bool VersionMatches(string? inside, string? expected)
    {
        var left = UpdateDecisions.Numeric(inside);
        var right = UpdateDecisions.Numeric(expected);

        if (left.Length == 0 || right.Length == 0) return false;

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
