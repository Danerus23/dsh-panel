using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DshPanel.Localization;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Update;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОБРАЗЦОВЫЕ ДАННЫЕ СЪЁМКИ — ВЫДУМАНЫ, ОБЕЗЛИЧЕНЫ И ЖИВУТ ОДНИМ МЕСТОМ.
///
/// **Зачем класс.** Кадр уезжает в публичную витрину, а туда попадать личному нельзя (красная
/// линия 7). До 12.10.2026 проверять это было нечем: кадры были ПУСТЫМИ, и «нет личного» держалось
/// на том, что в них вообще ничего не было. С образцовыми данными это перестало быть верным —
/// значит правило обязано стать проверкой, а не обещанием.
///
/// ⚠️ **Чего класс НЕ проверяет и не может:** как образцовые значения выглядят **глазом**.
/// Проверка видит строки, числа и то, где они объявлены; «похоже ли это на правду» читает человек.
///
/// ⚠️ **И чего в нём намеренно НЕТ:** проверки кадра целиком (отрисовки окна). Она живёт отдельно
/// и строится тем же членом, каким собирает окно съёмка, — иначе проверка мерила бы свою сборку.
/// </summary>
public class ShotSampleTests
{
    /// <summary>
    /// ЛИЧНОГО В ОБРАЗЦОВЫХ ЗНАЧЕНИЯХ НЕТ: ни пути пользователя, ни имени машины, ни адреса.
    ///
    /// ⚠️ **Ищем ЛИЧНОЕ, а не «правильное»** — так проверка не разойдётся с жизнью: она падает
    /// на том, чего в кадре быть не должно, а не на том, что кто-то переформулировал.
    ///
    /// Мутации, которые обязаны упасть здесь: «в образцовую рабочую папку вернулся настоящий путь» ·
    /// «в адрес телефона вернулся номер машины» · «в текст кадра вернулось имя пользователя».
    /// </summary>
    [Fact]
    public void В_образцовых_значениях_нет_личного()
    {
        var values = SampleStrings();

        Assert.True(values.Count >= 15, $"образцовых значений всего {values.Count} — проверять нечего");

        foreach (var (name, value) in values)
        {
            Assert.DoesNotContain("C:\\Users\\", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Users\\", value, StringComparison.OrdinalIgnoreCase);

            // ⚠️ Настоящий номер адреса машины: три первых числа и ЧЕТВЁРТОЕ — цифрой.
            // Маска `192.168.0.х` — разрешена, и именно она и должна быть.
            Assert.False(
                Regex.IsMatch(value, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b"),
                $"{name}: в образцовом значении «{value}» стоит настоящий адрес — в кадре нужна маска");

            // Имя машины и имя пользователя — в том виде, в каком они попадают в текст.
            foreach (var personal in new[]
                     {
                         Environment.MachineName,
                         Environment.UserName,
                         @"C:\Harness",
                     })
            {
                if (string.IsNullOrWhiteSpace(personal) || personal.Length < 3) continue;

                Assert.DoesNotContain(personal, value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// ПУТИ В КАДРЕ — ПОД МАСКОЙ: у каждого названного пути первый знак — `~`.
    ///
    /// Норма — решение владельца 11.10.2026 («номера и пути не пишем либо заменяем»), и она же
    /// в банке: `Shell\DisplayMask` показывает каталог пользователя как `~`.
    ///
    /// Мутации, которые обязаны упасть здесь: «путь снова настоящий» · «маска забыта у нового пути».
    /// </summary>
    [Fact]
    public void Пути_в_образцовых_данных_под_маской()
    {
        foreach (var (name, path) in new[]
                 {
                     ("рабочая папка", ShotSamples.WorkDir),
                     ("домашний каталог движка", ShotSamples.DshHome),
                     ("файл настроек", ShotSamples.SettingsFile),
                     ("папка копий", ShotSamples.BackupFolder),
                     ("папка движка", ShotSamples.EngineDir),
                 })
        {
            Assert.StartsWith("~", path, StringComparison.Ordinal);
            Assert.DoesNotContain(":", path, StringComparison.Ordinal);
        }

        // И У КОПИЙ ПУТИ ТОЖЕ ПОД МАСКОЙ — они показываются в списке.
        foreach (var entry in ShotSamples.Backups())
        {
            Assert.StartsWith(ShotSamples.BackupFolder, entry.Path, StringComparison.Ordinal);
            Assert.StartsWith("~", entry.Path, StringComparison.Ordinal);
        }

        // И В ОТЧЁТЕ ОКРУЖЕНИЯ — И В ПОДРОБНОСТИ, И В РАБОЧЕЙ ПАПКЕ СТРОКИ: подробность показывает
        // экран, папка нужна действию строки и наружу не выводится. Обе обязаны быть под маской:
        // имя пользователя в кадр не попадает.
        foreach (var group in ShotSamples.Environment().Groups)
        {
            foreach (var line in group.Lines)
            {
                if (line.Detail.StartsWith('~')) Assert.DoesNotContain(":", line.Detail, StringComparison.Ordinal);
                if (line.Folder.Length > 0) Assert.StartsWith("~", line.Folder, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// ОБРАЗЦОВЫЕ ДАННЫЕ ЖИВУТ ОДНИМ МЕСТОМ И НЕ ВЫДАЮТСЯ ОБЫЧНОЙ ЖИЗНИ.
    ///
    /// **От какого случая (это и есть «образцовые данные не текут в обычную жизнь»).** Двери
    /// съёмки (`ShotDrawers`, `ShotSamples`) — не «данные панели», а подстановка для кадра. Если
    /// их позовёт обычный запуск или прогон проверки, кадр перестанет быть кадром, а человек
    /// получит на экране выдуманный баланс — то есть панель соврёт ему про его деньги.
    ///
    /// ⚠️ Проверка идёт **по исходникам**, а не по слову: она называет ФАЙЛЫ, которым можно знать
    /// об образцовых данных. Всё остальное — утечка, и она падает с именем файла.
    ///
    /// Мутации, которые обязаны упасть здесь: «образцовую дверь позвали из `App`» ·
    /// «образцовую дверь позвали из контроллера» · «значения вынесены в общий файл».
    /// </summary>
    [Fact]
    public void Образцовые_двери_знает_только_съёмка()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var source = Path.Combine(root, "src", "DshPanel");

        // КОМУ МОЖНО: файл самих значений, файл дверей и разбор съёмки.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ShotSamples.cs",
            "ShotDrawers.cs",
            "Program.cs",
        };

        var leaks = new List<string>();

        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);

            if (allowed.Contains(name)) continue;

            var text = File.ReadAllText(file);

            if (text.Contains("ShotSamples", StringComparison.Ordinal)
                || text.Contains("ShotDrawers", StringComparison.Ordinal))
            {
                leaks.Add(name);
            }
        }

        Assert.True(
            leaks.Count == 0,
            "образцовые данные съёмки знает кто-то, кроме самой съёмки: " + string.Join(", ", leaks));

        // И ОБРАТНАЯ СТОРОНА: сам разбор съёмки их ЗОВЁТ — иначе кадры снова станут пустыми,
        // а проверка выше «зеленела» бы на вырезанной подстановке.
        var program = File.ReadAllText(Path.Combine(source, "Program.cs"));

        Assert.Contains("ShotDrawers.All()", program, StringComparison.Ordinal);

        // ⚠️ И КАЖДАЯ ДВЕРЬ ДОЕЗЖАЕТ ДО ОКНА (урок мутации AJ4, `docs\HISTORY.md`). Прежняя
        // редакция требовала только двух вызовов, и мутация «образцового сервера в окне больше нет»
        // **не роняла ничего**: кадр оставался непустым за счёт двери баланса. Поэтому здесь
        // перечислены ВСЕ двери, и у каждой ищется её место в сборке кадра — `drawers.<Дверь>`.
        foreach (var door in new[] { "Server", "Balance", "Settings", "Phone" })
        {
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(program, @"drawers\." + door + @"\b"),
                $"образцовая дверь «{door}» не доезжает до окна: в разборе съёмки нет ни одного «drawers.{door}»");
        }

        Assert.Contains("ShotDrawers.Backup()", program, StringComparison.Ordinal);

        // АВТОЗАПУСК И ФАКТЫ ПЕРВОГО ЗАПУСКА — ТОЖЕ ОБРАЗЦОВЫЕ, и это стережёт не только
        // «дверь доехала»: без этих двух строк кадр снова зависел бы от машины (A3) либо окно
        // первого запуска показывало бы состояние чужого профиля (A5).
        Assert.Contains("ShotDrawers.Autostart()", program, StringComparison.Ordinal);
        Assert.Contains("ShotDrawers.FirstRun()", program, StringComparison.Ordinal);

        // ⚠️ И ОБРАТНАЯ СТОРОНА ДЛЯ АВТОЗАПУСКА: ветвь съёмки НЕ ЧИТАЕТ машинную запись вовсе.
        // Прежде здесь стоял настоящий `AutostartController` поверх `AutostartStores`, и кадр
        // зависел от `HKCU\...\Run` этой машины (находка C1, пачка A). Теперь в разборе съёмки
        // этого имени нет ни одного раза, и вернуть чтение реестра сюда нельзя, не уронив проверку.
        Assert.DoesNotContain("AutostartStores", program, StringComparison.Ordinal);
        Assert.Contains("ShotDrawers.Engine()", program, StringComparison.Ordinal);
        Assert.Contains("ShotDrawers.Install()", program, StringComparison.Ordinal);

        // ⚠️ И ДВЕРЬ КАДРА «ПОДГОТОВЛЕНО» (DEF-009, 2.4.1): без неё состояние, ради которого имя
        // заведено, не производится вовсе — кадр остался бы пустым местом под тем же именем.
        Assert.Contains("ShotDrawers.PreparedInstall()", program, StringComparison.Ordinal);
        Assert.Contains("ShotPricingWithHistory()", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОБРАЗЦОВЫЕ ВЕРСИИ КАДРОВ ОБНОВЛЕНИЯ ЗАКРЕПЛЕНЫ ЧИСЛАМИ, И ВЫПУСК ДЕЙСТВИТЕЛЬНО НОВЕЕ
    /// (DEF-009, 2.4.1, решение капитана 24.10.2026).
    ///
    /// **От какого случая.** Установленная версия на кадре была версией самой панели и после выпуска
    /// 2.4.0 совпала с образцовым выпуском — кадры обновления стали говорить «обновляться не нужно»
    /// над готовой сборкой. Теперь установлена **2.3.2** (выпущена 22.10.2026), выпуск **2.4.0**
    /// (выпущен 24.10.2026), и обе версии — настоящие.
    ///
    /// ⚠️ **Числа закреплены ЛИТЕРАЛАМИ, а не «ожидание = источник»:** проверка о том, что видит
    /// человек, и сравнение константы с самой собой не поймало бы возврат к 2.4.0.
    /// Второе утверждение — не украшение: если выпуск перестанет быть новее, кадры снова скажут
    /// «обновляться не нужно», даже когда числа по отдельности верны.
    ///
    /// Мутации, которые обязаны упасть: вернуть установленную 2.4.0 (равна выпуску).
    /// </summary>
    [Fact]
    public void Образцовые_версии_кадров_обновления_закреплены_и_выпуск_новее()
    {
        Assert.Equal("2.3.2", ShotSamples.UpdateInstalled);
        Assert.Equal("2.4.0", ShotSamples.UpdateVersion);

        Assert.True(
            UpdateDecisions.IsNewer("v" + ShotSamples.UpdateVersion, ShotSamples.UpdateInstalled),
            "выпуск на кадре не новее установленной версии — кадры обновления снова скажут «обновляться не нужно»");

        // ВЕРСИИ, КОТОРОЙ НЕТ, НА КАДРАХ НЕТ: 2.4.1 на момент этого цикла ещё не выпущена.
        Assert.NotEqual("2.4.1", ShotSamples.UpdateVersion);
        Assert.NotEqual("2.4.1", ShotSamples.UpdateInstalled);
    }

    /// <summary>
    /// ДВЕРЬ КАДРА «ПОДГОТОВЛЕНО»: ПОДГОТОВКА ОТВЕЧАЕТ УСПЕХОМ С ПУТЯМИ-МАСКАМИ, А ЗАМЕНА — ОТКАЗОМ
    /// СЛОВАМИ (DEF-009, 2.4.1).
    ///
    /// **От какого случая.** Кадр «подготовлено» обязан показывать состояние, которое продукт
    /// действительно производит, но при этом съёмка не имеет права ничего скачать, записать или
    /// заменить. Значит дверь отвечает ДВУМЯ разными ответами, и оба проверяются здесь: успех
    /// подготовки (путь сборки — литерал под маской `~`) и отказ замены словами.
    ///
    /// ⚠️ **И <see cref="ShotDrawers.Install"/> обязан ОСТАТЬСЯ отказывающим:** «съёмка не готовит
    /// и не заменяет файлы» — записанное правило всех прочих кадров, а не забывчивость. Подменить
    /// его успехом значило бы ослабить правило изоляции ради одного кадра.
    ///
    /// Мутации, которые обязаны упасть здесь: «дверь подготовленного кадра снова отказывает» (нет
    /// успеха и пути) · «`Launch` начинает „запускать“» (пустой ответ вместо отказа словами) ·
    /// «`Install` перестал отказывать» (правило кадров подменено).
    /// </summary>
    [Fact]
    public void Дверь_подготовленного_кадра_готовит_но_не_запускает()
    {
        var door = ShotDrawers.PreparedInstall();

        var prepared = door.Prepare("v2.4.0", ReleaseAssets.None);

        Assert.True(prepared.Ok, "дверь кадра «подготовлено» отказывает — карточки замены в кадре не будет");

        // ПУТЬ СБОРКИ — ЛИТЕРАЛ ПОД МАСКОЙ: так его увидит человек, и личного в нём нет.
        Assert.Equal(@"~\AppData\Local\DshPanel2\state\update\2.4.0", prepared.StagedFolder);
        Assert.DoesNotContain("Users", prepared.StagedFolder, StringComparison.OrdinalIgnoreCase);

        // ЗАМЕНА — ОТКАЗ СЛОВАМИ: на кадре нажимать некому, а «успех» соврал бы о работе, которой
        // не было. Пустая строка здесь и была бы молчаливым «получилось».
        var launch = door.Launch(prepared);

        Assert.False(
            string.IsNullOrWhiteSpace(launch),
            "замена на кадре отвечает молчанием вместо отказа словами");

        Assert.Equal(PanelStrings.UpdateInstallUnavailable, launch);

        // И СОСЕДНЯЯ ДВЕРЬ НЕ ТРОНУТА: прочие кадры по-прежнему снимаются с отказом.
        Assert.False(
            ShotDrawers.Install().Prepare("v2.4.0", ReleaseAssets.None).Ok,
            "дверь остальных кадров начала «готовить» — записанное правило съёмки подменено");
    }

    /// <summary>
    /// У КАЖДОГО НАЗВАНИЯ КАДРА, КОТОРЫЙ ЧТО-ТО ПОКАЗЫВАЕТ, ЕСТЬ НЕПУСТОЕ ЗНАЧЕНИЕ.
    ///
    /// **Зачем.** «Кадр непустой» — не про пиксели только: он непустой, когда в нём есть ЧТО
    /// показать. У пустого каталога значений кадр выйдет белым листом, и это тот самый дефект,
    /// на который владелец смотрел в публичной витрине.
    ///
    /// Мутации, которые обязаны упасть здесь: «из каталога вычистили значения» ·
    /// «список копий оставили пустым» · «отчёт окружения остался без строк».
    /// </summary>
    [Fact]
    public void У_кадров_съёмки_есть_что_показать()
    {
        Assert.Equal(2, ShotSamples.Phone().Devices.Count);
        Assert.True(ShotSamples.Phone().Listening);
        Assert.True(ShotSamples.Phone().LiveCode);
        Assert.False(string.IsNullOrWhiteSpace(ShotSamples.PhoneCode));

        Assert.Equal(3, ShotSamples.Backups().Count);
        Assert.All(ShotSamples.Backups(), entry =>
        {
            Assert.True(entry.Bytes > 0, $"{entry.Name}: размер копии нулевой — в кадре будет «0 Б»");
            Assert.True(entry.Files > 0, $"{entry.Name}: число файлов копии нулевое");
            Assert.True(entry.Readable, $"{entry.Name}: копия помечена нечитаемой");
        });

        Assert.True(ShotSamples.Balance > 0, "баланс на кадре нулевой — это читается как «денег нет»");

        // ⚠️ ДВЕ КАРТОЧКИ, А НЕ ТРИ ГРУППЫ (решение владельца 14.10.2026 по рендеру): таблица умений
        // и находки на машине. Прежние три группы CODE-порядка («Папки», «Чем поднимается сервер»,
        // «DSH») стали одной карточкой — группировку несёт сама карточка.
        Assert.True(ShotSamples.Environment().Groups.Count >= 2, "в отчёте окружения меньше двух карточек");

        // И у КАЖДОЙ карточки на кадре есть строка пояснения под заголовком: она отвечает,
        // о чём этот список (прежний подзаголовок раздела убран — решение владельца).
        Assert.All(ShotSamples.Environment().Groups, group =>
            Assert.False(string.IsNullOrWhiteSpace(group.Subtitle), $"у карточки «{group.Title}» нет пояснения"));

        Assert.All(ShotSamples.Environment().Groups, group =>
            Assert.True(group.Lines.Count > 0, $"карточка «{group.Title}» пуста — в кадре будет пустая рамка"));

        // И НА КАДРЕ ЕСТЬ ВСЕ ЧЕТЫРЕ СОСТОЯНИЯ СТРОКИ: по одному цвету метки о виде не судят.
        var states = ShotSamples.Environment().Groups
            .SelectMany(group => group.Lines)
            .Select(line => line.State)
            .Distinct()
            .ToList();

        foreach (var state in new[] { EnvLineState.Ok, EnvLineState.Warning, EnvLineState.Error, EnvLineState.Grey })
        {
            Assert.Contains(state, states);
        }

        // И СЛОВА НА КАДРЕ — НА ЯЗЫКЕ ПАНЕЛИ, А НЕ ИМЕНА КЛЮЧЕЙ: подстановка берёт строку
        // из словаря, и непереведённый ключ вернулся бы сам собой.
        foreach (var language in new[] { "ru", "en", "zh" })
        {
            var value = Loc.TIn(language, nameof(PanelStrings.SrvAnswersFormat));

            Assert.NotEqual(nameof(PanelStrings.SrvAnswersFormat), value);
        }
    }

    /// <summary>Все строковые значения каталога — по имени, чтобы падение называло место.</summary>
    private static List<(string Name, string Value)> SampleStrings()
    {
        var values = new List<(string, string)>
        {
            ("рабочая папка", ShotSamples.WorkDir),
            ("домашний каталог движка", ShotSamples.DshHome),
            ("файл настроек", ShotSamples.SettingsFile),
            ("папка копий", ShotSamples.BackupFolder),
            ("папка движка", ShotSamples.EngineDir),
            ("версия движка", ShotSamples.EngineVersion),
            ("версия Node", ShotSamples.NodeVersion),
            ("версия npm", ShotSamples.NpmVersion),
            ("адрес телефона", ShotSamples.PhoneAddress),
            ("код сопряжения", ShotSamples.PhoneCode),
            ("имя процесса", ShotSamples.Process),
            ("валюта", ShotSamples.Currency),
        };

        foreach (var entry in ShotSamples.Backups())
        {
            values.Add(("имя копии " + entry.Name, entry.Path));
            values.Add(("особенность копии " + entry.Name, entry.EngineVersion));
        }

        foreach (var device in ShotSamples.Phone().Devices)
        {
            values.Add(("имя устройства " + device.Id, device.Name));
        }

        foreach (var group in ShotSamples.Environment().Groups)
        {
            values.Add(("заголовок группы", group.Title));

            foreach (var line in group.Lines)
            {
                values.Add(("что искали", line.What));
                values.Add(("что нашли", line.Result));
                values.Add(("слово метки", line.Word));
                values.Add(("подробность", line.Detail));
            }
        }

        return values;
    }
}
