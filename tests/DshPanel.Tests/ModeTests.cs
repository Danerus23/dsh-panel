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
        // 25-й — `--engine-install-selftest` (этап E3.1): установка движка по замку настоящим npm
        // и проба на поставленном движке — без него ключ отвергался бы как опечатка.
        // 26-й — `--engine-update-selftest` (этап E3.2): ЦЕПОЧКА обновления настоящими дверями —
        // гашение своего движка, предохранительная копия профиля, установка по замку, живая проба
        // и подъём сервера заново; с доводом «beda» — отказ и возврат прежнего движка.
        // 27-й — `--engine-rollback-selftest` (этап E3.3): ЦЕПОЧКА отката настоящими дверями —
        // проба прежней версии живьём, гашение своего движка, запись и подъём сервера заново
        // уже на прежней версии; с доводом «beda» — отказ на пробе ДО всякой подмены.
        // 28-й — `--phone-selftest` (этап F1.e): самотест КАЛИТКИ ТЕЛЕФОНА в изоляции — слушатель
        // на своём свободном порту, сопряжение по коду, белый список, рубильник и ЖИВОЕ рукопожатие
        // мультиплексора (кадр `ready` с настоящим `clientId`). Без изоляции отказывается сам.
        // 29-й — `--windows-selftest` (u14, 2.4.0): ЖИВАЯ проба владения окнами — кнопка панели
        // задач числом, z-порядок «спутник поверх панели», свернуть/вернуть. Без неё ключ был бы
        // отвергнут как опечатка, и живую пробу нельзя было бы запустить вовсе.
        Assert.Equal(29, StartModes.Known.Length);

        foreach (var key in new[]
                 {
                     "--shot", "--tray-selftest", "--shell-selftest", "--windows-selftest",
                     "--instance-selftest",
                     "--autostart-selftest", "--settings-selftest", "--balance-selftest",
                     "--server-selftest", "--backup-selftest", "--isolation-selftest",
                     "--lang-selftest",

                     // Проба обновления: решения, разбор ответа GitHub, суточный гейт и следы
                     // в настройках — без сети и без файлов владельца.
                     "--update-selftest",

                     // Проба установки движка по замку (этап E3.1): настоящий npm, свой каталог
                     // под корнем прогона и полная проба сервера на поставленном движке.
                     "--engine-install-selftest",

                     // Проба ОБНОВЛЕНИЯ движка (этап E3.2): вся цепочка подмены версии настоящими
                     // дверями, включая предохранительную копию профиля и живую пробу поставленного.
                     "--engine-update-selftest",

                     // Проба ОТКАТА движка (этап E3.3): цепочка возврата к прежней версии.
                     "--engine-rollback-selftest",

                     // Самотест КАЛИТКИ ТЕЛЕФОНА (этап F1.e): слушатель, сопряжение, белый список,
                     // рубильник и живое рукопожатие мультиплексора — только в изоляции.
                     "--phone-selftest",

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

        // ⚠️ **Ищем ИМЯ ОКНА, а не всякое слово.** Строка `"settings"` встречается в разборе ещё
        // и как ИМЯ ОБРАЗЦОВОЙ ДВЕРИ (`doors.Add("settings")` в `ShotWindowWithDoors`), и это
        // не копия списка имён: дверь называется по своему месту в сборке кадра. Поэтому литерал
        // имени окна обязан стоять РЯДОМ С ВЫБОРОМ (`kind ==`, `kind is`, `KnowsShotWindow`) —
        // там, где он и был бы копией списка. Так проверка не падает на слове, а ловит именно
        // заведённый заново перечень имён.
        var literals = StartModes.ShotWindows
            .Where(window => System.Text.RegularExpressions.Regex.IsMatch(
                source,
                @"kind\s*(==|is)[^\r\n]*\""" + System.Text.RegularExpressions.Regex.Escape(window) + @"\"""))
            .ToList();

        Assert.True(
            literals.Count == 0,
            "в разборе съёмки снова завелись свои имена окон (должны браться из StartModes.ShotWindows): " +
            string.Join(", ", literals));
    }

    /// <summary>
    /// У КАЖДОГО ИМЕНИ КАДРА ЕСТЬ СВОЯ ВЕТВЬ — И ВЕТВЬ ЭТОГО ИМЕНИ, А НЕ СОСЕДА.
    ///
    /// **От какого случая (фаза 3, 12.10.2026).** Имя <c>settings</c> обещало «настройки»,
    /// а снимало раздел «Резервное копирование», и **«Общего» кадра не было вовсе**: имя одно,
    /// а разделов шесть. Владелец назвал это прямо: «кадр показывает не то, что обещает имя».
    ///
    /// ⚠️ **Почему это проверяется отдельно от «имя есть в списке».** Прежняя проверка
    /// (`Описание_ключа_съёмки…`) видит только СПИСОК: имя, оставшееся без ветви, она пропускает —
    /// и кадр не снимается вовсе (громкий отказ кодом 2 уже на съёмке). А имя, чья ветвь снимает
    /// СОСЕДА, не ловится ничем: кадр есть, он непустой — и он не про то, что обещано.
    ///
    /// Здесь на каждое имя назван признак его ветви, и признак обязан быть в разборе съёмки
    /// **ровно один раз**: у диалогов это имя класса окна, у разделов настроек — константа раздела.
    /// Признаки — не «слова в комментарии»: каждый из них — то, чем ветвь ДЕЙСТВИТЕЛЬНО снимает.
    ///
    /// Мутации, которые обязаны упасть здесь: «имя без ветви» · «ветвь снимает соседний раздел» ·
    /// «два имени ведут в одну ветвь».
    /// </summary>
    [Fact]
    public void У_каждого_имени_кадра_есть_своя_ветвь()
    {
        // Имя кадра → чем его ветвь снимает. ⚠️ `settings` и `settings-backup` — ОДНА ветвь
        // нарочно: первое имя старое и обещало не то, что снимало, второе названо тем, что видно.
        // Раздел «Резервное копирование» в разборе стоит УМОЛЧАНИЕМ (`_ =>`), поэтому признак
        // у него — имя самого раздела, а не строка ветви.
        var marks = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StartModes.ShotSettings] = "SettingsWindow.BackupSection",
            [StartModes.ShotSettingsBackup] = "SettingsWindow.BackupSection",
            [StartModes.ShotSettingsGeneral] = "SettingsWindow.GeneralSection",
            [StartModes.ShotSettingsBalance] = "SettingsWindow.BalanceSection",
            [StartModes.ShotSettingsUpdate] = "SettingsWindow.UpdateSection",
            [StartModes.ShotSettingsServer] = "SettingsWindow.ServerSection",
            [StartModes.ShotSettingsPhone] = "SettingsWindow.PhoneSection",
            [StartModes.ShotBackup] = "new BackupWindow()",
            [StartModes.ShotSettingsAbout] = "SettingsWindow.AboutSection",
            [StartModes.ShotPeaks] = "new PeakWindow()",
            [StartModes.ShotPricingHistory] = "new PricingHistoryWindow()",
            [StartModes.ShotIssue] = "new IssueWindow()",
            [StartModes.ShotMainTwoServers] = "probe: kind == StartModes.ShotMainTwoServers",
            [StartModes.ShotConfirmStop] = "new ConfirmStopWindow()",
            [StartModes.ShotConfirmEngine] = "new ConfirmEngineWindow()",
            [StartModes.ShotBackupStop] = "new BackupStopWindow()",
            [StartModes.ShotUnsavedChanges] = "new UnsavedChangesWindow()",
            [StartModes.ShotBackupClose] = "new BusyCloseWindow()",
            [StartModes.ShotFirstRun] = "new FirstRunWindow()",
        };

        // КАЖДОЕ имя списка названо — и наоборот: лишнего имени в разборе нет.
        var listed = StartModes.ShotWindows.ToList();
        var named = marks.Keys.ToList();

        listed.Sort(StringComparer.Ordinal);
        named.Sort(StringComparer.Ordinal);

        Assert.Equal(listed, named);

        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var source = File.ReadAllText(Path.Combine(root, "src", "DshPanel", "Program.cs"));

        // ПРИЗНАК КАЖДОЙ ВЕТВИ — РОВНО ОДИН РАЗ. Два имени с одним признаком (см. выше) дают
        // одну проверку, а не две: признак у них общий, и второго такого места в разборе быть
        // не должно — иначе одна из ветвей снимает не то, что обещано.
        foreach (var branch in marks
                     .GroupBy(pair => pair.Value, StringComparer.Ordinal)
                     .Select(group => (Mark: group.Key, Names: group.Select(pair => pair.Key).ToList())))
        {
            var count = Occurrences(source, branch.Mark);

            Assert.True(count == 1, $"признак ветви «{branch.Mark}» встречается в разборе {count} раз — "
                + "ветвь либо пропала, либо снимает не то, что обещано именами: "
                + string.Join(", ", branch.Names));
        }

        // ⚠️ И ВЕТВЬ ОБЯЗАНА БЫТЬ ЖИВОЙ, А НЕ ОТКЛЮЧЁННОЙ (урок мутации AJ1, `docs\HISTORY.md`).
        // Прежняя редакция этой проверки искала признак **строкой** и на «`if (false && kind == …)`»
        // молчала: строка-то на месте, а ветвь не работает — кадр не снимается вовсе либо снимается
        // соседний. Поэтому у КАЖДОГО имени сверяется САМА ОХРАНА: константа имени обязана стоять
        // в разборе как выбор (`kind == …`, `kind is …`, `=>`), и ни разу — под «`false &&`».
        foreach (var name in StartModes.ShotWindows)
        {
            var constant = ShotConstant(name);

            if (constant is null) continue;

            Assert.True(
                Occurrences(source, "StartModes." + constant) >= 1,
                $"у кадра «{name}» нет ветви: в разборе съёмки не нашлось «StartModes.{constant}»");

            Assert.DoesNotContain(
                "false && " + "StartModes." + constant,
                source,
                StringComparison.Ordinal);
        }

        // И НИ ОДНОЙ ОТКЛЮЧЁННОЙ ВЕТВИ ВООБЩЕ: «false &&» перед выбором имени окна — это ветвь,
        // которая выглядит живой, а не работает. Такую однажды и завели (AJ1).
        Assert.DoesNotContain("false && kind", source, StringComparison.Ordinal);
        Assert.DoesNotContain("false && (kind", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Имя константы <see cref="StartModes"/>, которой названо это имя кадра. <c>null</c> —
    /// имя, у которого своей ветви-охранника нет по устройству: у <c>settings</c> ветвь общая
    /// с <c>settings-backup</c> (умолчание разбора), и охранник у неё один — на второе имя.
    /// </summary>
    private static string? ShotConstant(string name) => name switch
    {
        "settings" => null,
        "settings-general" => nameof(StartModes.ShotSettingsGeneral),
        "settings-backup" => nameof(StartModes.ShotSettingsBackup),
        "settings-balance" => nameof(StartModes.ShotSettingsBalance),
        "settings-update" => nameof(StartModes.ShotSettingsUpdate),
        "settings-server" => nameof(StartModes.ShotSettingsServer),
        "settings-phone" => nameof(StartModes.ShotSettingsPhone),
        "backup" => nameof(StartModes.ShotBackup),
        "settings-about" => nameof(StartModes.ShotSettingsAbout),
        "peaks" => nameof(StartModes.ShotPeaks),
        "pricing-history" => nameof(StartModes.ShotPricingHistory),
        "issue" => nameof(StartModes.ShotIssue),
        "main-two-servers" => nameof(StartModes.ShotMainTwoServers),
        "confirm-stop" => nameof(StartModes.ShotConfirmStop),
        "confirm-engine" => nameof(StartModes.ShotConfirmEngine),
        "backup-stop" => nameof(StartModes.ShotBackupStop),
        "unsaved-changes" => nameof(StartModes.ShotUnsavedChanges),
        "backup-close" => nameof(StartModes.ShotBackupClose),
        "first-run" => nameof(StartModes.ShotFirstRun),
        _ => null,
    };

    private static int Occurrences(string text, string needle)
    {
        var count = 0;

        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// В ДЕРЕВЕ НЕТ ОКНА ОБНОВЛЕНИЯ, И ИМЕНИ <c>update</c> В СПИСКЕ СЪЁМКИ ТОЖЕ НЕТ.
    ///
    /// Решение владельца 09.10.2026, дословно: *«Это не отдельное окно должно быть… Я не хочу
    /// отдельно вызываемое окно»*. Обе половины этого решения сторожатся ЗДЕСЬ, и обе — про
    /// ОТСУТСТВИЕ, которое ничем другим не поймать:
    ///
    /// * файлов окна в дереве быть не должно вовсе: окно, оставленное «на всякий случай», снова
    ///   завело бы второе место с тем же ответом;
    /// * имени <c>update</c> не должно быть в списке съёмки: имя, оставшееся от удалённого окна,
    ///   обещало бы кадр, которого не существует, и человек получил бы отказ уже на съёмке
    ///   (отказ «незнакомое имя окна» при этом остаётся честным — см. проверку выше).
    ///
    /// ⚠️ Проверяется КОДОМ, а не взглядом: список съёмки читается у <see cref="StartModes"/>,
    /// а файлы — на диске. Кадр раздела обновления у съёмки остался и называется
    /// <c>settings-update</c> — он и есть замена удалённому имени.
    /// </summary>
    [Fact]
    public void Окна_обновления_в_дереве_нет_и_имени_update_в_съёмке_тоже()
    {
        Assert.DoesNotContain("update", StartModes.ShotWindows);
        Assert.False(StartModes.KnowsShotWindow("update"), "имя «update» снова стало знакомым разбору съёмки");

        // Замена на месте: кадр раздела «Обновление» настроек — в списке, и он знаком разбору.
        Assert.Contains(StartModes.ShotSettingsUpdate, StartModes.ShotWindows);
        Assert.True(StartModes.KnowsShotWindow(StartModes.ShotSettingsUpdate));

        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var views = Path.Combine(root, "src", "DshPanel", "Views");

        foreach (var name in new[] { "UpdateWindow.axaml", "UpdateWindow.axaml.cs" })
        {
            var path = Path.Combine(views, name);
            Assert.False(File.Exists(path), $"окно обновления вернулось в дерево: {path}");
        }

        // И пункт меню значка больше НЕ про окно: имя строки, оставшееся «про окно», врало бы
        // о том, куда ведёт пункт (сам переход в раздел сторожит `ShellPeaksDoorTests`).
        var shell = File.ReadAllText(Path.Combine(root, "src", "DshPanel", "Shell", "PanelMenu.cs"));

        Assert.Contains("PanelStrings.UpdateSectionMenuText", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateWindowMenuText", shell, StringComparison.Ordinal);
    }
}
