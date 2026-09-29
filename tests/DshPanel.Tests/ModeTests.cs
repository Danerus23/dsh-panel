using System;
using System.IO;
using System.Linq;
using DshPanel;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки разбора режимов запуска. Появились из находки холодного агента 24.09.2026: опечатка
/// в имени проверки («--balans-selftest») или ключ проверки без обязательного аргумента («--shot»
/// без имени файла) проваливались мимо всех ветвей и уходили в **обычный запуск** — открывали
/// окно на рабочем столе владельца. Запускал это скрипт, а не человек, и заметить было некому.
///
/// Проверка сторожит ровно две вещи: чужой ключ — отказ, и обычный запуск без ключей — по-прежнему
/// обычный запуск (иначе панель перестала бы открываться у человека).
/// </summary>
public class ModeTests
{
    [Fact]
    public void Неизвестный_ключ_отвергается_а_обычный_запуск_нет()
    {
        // Обычный запуск: ключей нет — это человек, открывший панель. И аргумент без дефиса
        // ключом не является: его разбирает платформа.
        Assert.Null(StartModes.Refuse(Array.Empty<string>()));
        Assert.Null(StartModes.Refuse(new[] { "файл.png" }));

        // Опечатка — отказ, и в отказе есть и то, что написали, и то, что имели в виду.
        var typo = StartModes.Refuse(new[] { "--balans-selftest" });
        Assert.NotNull(typo);
        Assert.Contains("--balans-selftest", typo!, StringComparison.Ordinal);
        Assert.Contains("--balance-selftest", typo!, StringComparison.Ordinal);

        // Ключ проверки без обязательного аргумента: снимать некуда.
        var shotWithoutFile = StartModes.Refuse(new[] { "--shot" });
        Assert.NotNull(shotWithoutFile);
        Assert.Contains("--shot", shotWithoutFile!, StringComparison.Ordinal);

        // Ключ-модификатор без режима: делать нечего.
        var modifierOnly = StartModes.Refuse(new[] { "--with-my-key" });
        Assert.NotNull(modifierOnly);
        Assert.Contains("БЕЗ РЕЖИМА", modifierOnly!, StringComparison.Ordinal);

        // А вот это — состояние, которого в `Program.Main` быть не может: известный режим
        // возвращает результат раньше, чем дело доходит до этого рубежа. Но если он сюда дожил,
        // значит его забыли обработать, и обычный запуск открыл бы окно владельцу. Отказываем.
        var missed = StartModes.Refuse(new[] { "--balance-selftest" });
        Assert.NotNull(missed);
        Assert.Contains("БЕЗ РЕЖИМА", missed!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Перечень известных ключей — тот же, что разбирает <c>Program.Main</c>. Число стережёт
    /// появление нового режима: добавили режим — сюда обязан попасть его ключ, иначе он будет
    /// отвергнут как неизвестный (это и есть тот случай, когда проверка ловит забывчивость).
    /// </summary>
    [Fact]
    public void Список_известных_ключей_содержит_все_режимы()
    {
        // 24, а не 18: перенос строк на словари добавил два ключа — `--lang-selftest`
        // (сверка трёх словарей на собранной сборке) и модификатор `--lang`, — а функция
        // обновления добавила `--update-selftest` (проверка выпусков и извещение).
        // Лабораторный прогон замены файлов добавил ещё два: `--update-check` и
        // `--update-prepare` — без них прогон упирался бы в «неизвестный ключ».
        // И ещё один дала разовая «копия для передачи» (п. 11 `docs\DESIGN.md`): `--shareable` —
        // передача перестала быть настройкой и стала ключом режима без окна.
        Assert.Equal(24, StartModes.Known.Length);

        foreach (var key in new[]
                 {
                     "--shot", "--tray-selftest", "--shell-selftest", "--instance-selftest",
                     "--autostart-selftest", "--settings-selftest", "--balance-selftest",
                     "--server-selftest", "--backup-selftest", "--isolation-selftest",
                     "--lang-selftest",

                     // Проба обновления: решения, разбор ответа GitHub, суточный гейт и следы
                     // в настройках — без сети и без файлов владельца.
                     "--update-selftest",

                     // Два режима замены файлов без окна: проверка выпуска и подготовка замены.
                     // Замена файлов у человека ими не запускается — запускает её update.cmd,
                     // и только у панели, вышедшей для этого сама.
                     "--update-check", "--update-prepare",

                     // Модификатор языка: снимается до разбора режимов (как `--run-root`),
                     // но обязан быть известным — иначе строка «известные ключи» о нём молчала бы.
                     "--lang",

                     // Два режима без окна и три согласия наката — из HeadlessDecisions: иначе
                     // ключ, который разбирается, оказался бы «неизвестным» на общем рубеже.
                     // `--shareable` — разовая «копия для передачи» того же режима копии.
                     "--backup", "--restore", "--with-engine", "--with-panel", "--with-keys",
                     "--shareable",
                 })
        {
            Assert.Contains(key, StartModes.Known);
        }
    }

    /// <summary>
    /// В строке «известные ключи» ключ с обязательным аргументом обязан быть написан ВМЕСТЕ с ним:
    /// иначе совет «есть такой ключ» не работает, и человек ищет причину в другом месте.
    /// </summary>
    [Fact]
    public void Перечень_известных_ключей_показывает_обязательные_аргументы()
    {
        var unknown = StartModes.Refuse(new[] { "--balans-selftest" });

        Assert.NotNull(unknown);
        Assert.Contains("--shot <файл>", unknown!, StringComparison.Ordinal);
        Assert.Contains("--backup <папка>", unknown, StringComparison.Ordinal);
        Assert.Contains("--restore <архив>", unknown, StringComparison.Ordinal);
    }

    /// <summary>
    /// ИМЕНА ОКОН СЪЁМКИ — ОДИН СПИСОК, И ОБЕ СТОРОНЫ ЕГО ЗНАЮТ.
    ///
    /// От какого случая. Прежде имена жили двумя копиями: описание ключа <c>--shot</c> обещало
    /// три окна (<c>settings|backup|about</c>), а разбор съёмки в <c>Program.Shot</c> принимал
    /// пять — описание молчало про <c>settings-balance</c> и <c>peaks</c>, хотя оба окна рабочие
    /// (нашёл разведчик 28.09.2026). Расхождение документа с кодом — дефект документа, но
    /// починка не в тексте, а в том, чтобы ИСТОЧНИК был один: строка описания строится из
    /// <see cref="StartModes.ShotWindows"/>, и разбор спрашивает <see cref="StartModes.KnowsShotWindow"/>.
    ///
    /// Проверяется поэтому ДВУМЯ способами, и второй важнее первого: строка описания (иначе
    /// список разошёлся бы снова) и ЧТЕНИЕ ИСХОДНИКА разбора — литералов этих имён в <c>Program</c>
    /// быть не должно, иначе копия заводится заново и замечается через год.
    /// </summary>
    [Fact]
    public void Описание_ключа_съёмки_и_разбор_называют_одни_имена_окон()
    {
        Assert.NotEmpty(StartModes.ShotWindows);

        // Строка «известные ключи» содержит КАЖДОЕ имя из списка...
        var unknown = StartModes.Refuse(new[] { "--balans-selftest" });
        Assert.NotNull(unknown);

        foreach (var window in StartModes.ShotWindows)
        {
            Assert.Contains(window, unknown!, StringComparison.Ordinal);
            Assert.True(StartModes.KnowsShotWindow(window), $"разбор съёмки не знает окна «{window}»");
        }

        // ...а чужое имя по-прежнему остаётся незнакомым: отказ кодом 2 не сломан.
        Assert.False(StartModes.KnowsShotWindow("givotnoe"), "чужое имя окна не должно быть знакомым");
        Assert.False(StartModes.KnowsShotWindow(string.Empty), "пустое имя — это «окно не назвали»");

        // Второй способ: в разборе съёмки нет НИ ОДНОГО литерала этих имён — только список.
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var source = File.ReadAllText(Path.Combine(root, "src", "DshPanel", "Program.cs"));

        var literals = StartModes.ShotWindows
            .Where(window => source.Contains($"\"{window}\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            literals.Count == 0,
            "в разборе съёмки снова завелись свои имена окон (должны браться из StartModes.ShotWindows): " +
            string.Join(", ", literals));
    }
}
