using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using DshPanel.Backup;
using DshPanel.Isolation;
using DshPanel.Pricing;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПРОПУСК ТЕЛЕФОНА — ОТДЕЛЬНЫМ ФАЙЛОМ С ПРАВАМИ ВЛАДЕЛЬЦА (этап F1).
///
/// Здесь проверяется ровно то, чем пропуск отличается от настройки: он **живёт своим файлом**
/// (в <c>settings.json</c> секрету места нет), его разбор **терпим** (битый файл — это «сопряжения
/// нет», а не падение панели), сравнение **постоянно по времени**, а наружу **не выходит ни адрес
/// устройства, ни сам пропуск**. Файлы — только под своим временным корнем
/// (<see cref="PanelTestStand.TempDir"/>): каталогов владельца эта проба не касается.
/// </summary>
public sealed class PhonePassTests : IDisposable
{
    private readonly string _root = PanelTestStand.TempDir();

    public void Dispose() => PanelTestStand.RemoveTemp(_root);

    private AppPaths Paths => AppPaths.Under(_root);

    private PhonePassFiles Files(AppPaths paths, System.Collections.Generic.List<string>? log = null) =>
        PhonePassFiles.Under(paths, text => (log ?? new System.Collections.Generic.List<string>()).Add(text));

    // ---------------------------------------------------------------- файл под своим корнем

    /// <summary>
    /// ЗАПИСЬ И ЧТЕНИЕ КРУГЛЫМ ХОДОМ: пропуск, добавленное устройство и адрес переживают запись
    /// и чтение. Ловит правку «пишем не то, что читаем» — тогда телефон переставал бы пускаться
    /// после перезапуска панели, а панель говорила бы «сопряжено».
    /// </summary>
    [Fact]
    public void Запись_и_чтение_круглым_ходом()
    {
        var paths = Paths;
        var files = Files(paths);

        var pass = PhonePass.Create().WithDevice("device-1", "192.168.0.10", PricingDecisions.Stamp(DateTimeOffset.Now));

        Assert.True(pass.Any);
        Assert.Equal(1, pass.DeviceCount);
        Assert.True(pass.Save(files));
        Assert.True(File.Exists(paths.PhonePassFile), $"нет файла пропуска: «{paths.PhonePassFile}»");

        var read = PhonePass.Load(files);

        Assert.True(read.Any);
        Assert.Equal(1, read.DeviceCount);
        Assert.Equal(pass.ToJson(), read.ToJson());

        // Файл — РОВНО ОДНА СТРОКА JSON: формат объявлен одной строкой, и это не украшение.
        var text = File.ReadAllText(paths.PhonePassFile);

        Assert.DoesNotContain("\n", text.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.StartsWith("{", text.TrimStart(), StringComparison.Ordinal);
        Assert.EndsWith("}", text.TrimEnd(), StringComparison.Ordinal);

        // И он БЕЗ BOM — правило проекта для файлов панели.
        var bytes = File.ReadAllBytes(paths.PhonePassFile);

        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "файл пропуска записан с BOM");
    }

    /// <summary>
    /// ФАЙЛА НЕТ — ЭТО «НЕЧЕГО», А НЕ СБОЙ: загрузка даёт пустое состояние, и калитка закрыта.
    /// Ловит правку «нет файла — исключение»: панель падала бы у всякого, кто телефон ещё не заводил.
    /// </summary>
    [Fact]
    public void Без_файла_пропуска_нет()
    {
        var paths = Paths;

        Assert.False(File.Exists(paths.PhonePassFile));

        var pass = PhonePass.Load(Files(paths));

        Assert.False(pass.Any);
        Assert.Equal(0, pass.DeviceCount);
        Assert.False(pass.Match("что угодно"));
        Assert.Empty(pass.SafeDevices);
    }

    /// <summary>
    /// БИТЫЙ ФАЙЛ — ТОЖЕ «СОПРЯЖЕНИЯ НЕТ», и панель при этом жива. Перебирается ВСЁ, что человек
    /// может сделать с файлом руками: мусор, обрезанный JSON, пустой объект, версия из будущего,
    /// пустой пропуск. Ловит правку «разбор строгий»: падение панели на содержимом её же файла —
    /// дефект, а не защита (тот же довод, что у настроек).
    /// </summary>
    [Fact]
    public void Битый_файл_читается_как_пустое_состояние()
    {
        var broken = new[]
        {
            string.Empty,
            "   ",
            "не json вовсе",
            "{\"version\":1,\"pass\":",
            "{",
            "[]",
            "null",
            "{}",
            "{\"version\":99,\"pass\":\"AAAA\",\"devices\":[]}",       // чужая версия
            "{\"version\":0,\"pass\":\"AAAA\",\"devices\":[]}",        // и нулевая тоже
            "{\"version\":1,\"pass\":\"\",\"devices\":[]}",            // пропуска нет
            "{\"version\":1,\"devices\":[]}",                          // поля пропуска нет вовсе
            "{\"version\":1,\"pass\":\"AAAA\",\"devices\":\"строка\"}", // устройства не список
        };

        foreach (var text in broken)
        {
            var pass = PhonePass.Parse(text);

            Assert.False(pass.Any, $"«{text}» признано пропуском, а пропуска тут нет");
            Assert.False(pass.Match("AAAA"));
        }

        // И то же самое, но текстом из НАСТОЯЩЕГО файла — путь разбора один и тот же.
        var paths = Paths;
        Directory.CreateDirectory(paths.StateDir);
        File.WriteAllText(paths.PhonePassFile, "{\"version\":1,\"pass\":");

        var fromFile = PhonePass.Load(Files(paths));

        Assert.False(fromFile.Any);
        Assert.False(fromFile.Match("AAAA"));
    }

    /// <summary>
    /// ЧУЖАЯ ВЕРСИЯ ФОРМАТА — ЭТО «СОПРЯЖЕНИЯ НЕТ», А НЕ «ПРОЧИТАЕМ КАК ПОНЯЛИ». Проверка отдельная,
    /// потому что версия и есть тот самый предохранитель: формат мог сменить СМЫСЛ полей, и пустить
    /// по нему устройство значило бы пустить по догадке.
    /// </summary>
    [Fact]
    public void Чужая_версия_файла_не_пускает()
    {
        var ours = PhonePass.Create();
        var text = ours.ToJson();

        Assert.Contains("\"version\":1", text, StringComparison.Ordinal);
        Assert.True(PhonePass.Parse(text).Any);

        var future = text.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal);

        Assert.False(PhonePass.Parse(future).Any);
    }

    // ---------------------------------------------------------------- пропуск наружу не выходит

    /// <summary>
    /// ПРОПУСК НАРУЖУ НЕ ВЫХОДИТ: ни значение, ни его часть. Наружу класс отдаёт только
    /// «совпало/нет». Ловит правку «добавили свойство с пропуском»: оно однажды уехало бы в окно,
    /// в журнал или в отчёт — и пропуск перестал бы быть секретом.
    /// </summary>
    [Fact]
    public void Пропуск_наружу_не_выходит()
    {
        var pass = PhonePass.Create();
        var token = TokenOf(pass);

        // Единственный способ достать пропуск — разобрать файл, который класс сам и пишет;
        // снаружи иного пути к нему нет. Именно поэтому здесь свой разбор, а не вызов класса.
        Assert.True(pass.Match(token));
        Assert.True(PhonePass.Parse(pass.ToJson()).Match(token));

        var publicStrings = typeof(PhonePass)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .ToList();

        // Ни одного свойства-строки, которое отдавало бы пропуск: наружу — только «совпало/нет».
        Assert.DoesNotContain(publicStrings, name =>
            name.Contains("Token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Pass", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Достать пропуск ИЗ ФАЙЛА — единственный способ, каким его вообще можно увидеть: сам класс
    /// наружу его не отдаёт. Проверка делает это сама, а не через свойство класса: иначе она
    /// стерегла бы то, чего нет.
    /// </summary>
    private static string TokenOf(PhonePass source) =>
        System.Text.Json.JsonDocument.Parse(source.ToJson()).RootElement.GetProperty("pass").GetString()!;

    /// <summary>
    /// АДРЕС УСТРОЙСТВА ТЕЛЕФОНУ НЕ ВИДЕН: список, который покидает класс, адреса не несёт —
    /// ни в поле, ни в JSON. Адрес — примета самого телефона, и показывать её ему незачем.
    /// Ловит правку «отдали наружу тот же список, что храним».
    /// </summary>
    [Fact]
    public void Адрес_устройства_телефону_не_виден()
    {
        var pass = PhonePass.Create().WithDevice("device-1", "192.168.0.10", "2026-10-11T12:00:00.0000000+00:00");

        var views = pass.SafeDevices;

        var view = Assert.Single(views);

        Assert.Equal("device-1", view.Id);

        // Тип, который уходит наружу, поля адреса НЕ ИМЕЕТ вовсе — вернуть его нечем.
        Assert.DoesNotContain(
            typeof(PhoneDeviceView).GetProperties(),
            property => property.Name.Contains("Address", StringComparison.OrdinalIgnoreCase));

        // И в том, что уходит наружу, адреса нет ни строкой, ни подстрокой.
        var outward = string.Join("|", views.Select(item => item.Id + " " + item.AddedAt + " " + item.LastSeen));

        Assert.DoesNotContain("192.168", outward, StringComparison.Ordinal);

        // А внутри класса адрес ЕСТЬ — он нужен панели, чтобы заметить смену адреса.
        Assert.Contains("192.168.0.10", pass.ToJson(), StringComparison.Ordinal);
    }

    /// <summary>
    /// СРАВНЕНИЕ ПРОПУСКА — ТОЛЬКО «СОВПАЛО ИЛИ НЕТ», И НИЧЕГО БОЛЬШЕ: пустой, чужой и обрезанный
    /// пропуск не совпадают, а точный — совпадает. Ловит правку «сравниваем через ==» вместе
    /// с ослаблением «пустое совпадает с пустым» — тогда пропуск подбирался бы по времени ответа,
    /// а пустой файл пускал бы всех.
    /// </summary>
    [Fact]
    public void Сравнение_пропуска_только_совпало_или_нет()
    {
        var pass = PhonePass.Create();
        var token = TokenOf(pass);

        Assert.True(pass.Match(token));
        Assert.False(pass.Match(token + "x"));
        Assert.False(pass.Match(token.Substring(0, token.Length - 1)));
        Assert.False(pass.Match(string.Empty));
        Assert.False(pass.Match(null));
        Assert.False(pass.Match(" "));

        // Пустое состояние не совпадает НИ С ЧЕМ, включая пустую строку.
        Assert.False(PhonePass.Empty.Match(string.Empty));
        Assert.False(PhonePass.Empty.Match("любой"));
    }

    /// <summary>
    /// ПРОПУСК — 32 СЛУЧАЙНЫХ БАЙТА В base64url: два выпуска дают РАЗНЫЕ пропуски, и длина у них
    /// та же, что у ссылки входа движка. Ловит правку «пропуск из головы» (например из имени
    /// машины): у двух людей он тогда совпал бы.
    /// </summary>
    [Fact]
    public void Пропуск_случаен_и_той_же_длины()
    {
        var first = PhonePass.Create();
        var second = PhonePass.Create();

        var one = TokenOf(first);
        var two = TokenOf(second);

        Assert.NotEqual(one, two);

        // 32 байта в base64url — 43 знака БЕЗ выравнивания: столько же, сколько у ссылки входа движка.
        Assert.Equal(32, PhonePass.TokenBytes);
        Assert.Equal(43, one.Length);
        Assert.DoesNotContain("=", one, StringComparison.Ordinal);
        Assert.DoesNotContain("+", one, StringComparison.Ordinal);
        Assert.DoesNotContain("/", one, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- устройства

    /// <summary>
    /// ПОВТОРНЫЙ ВХОД ТЕМ ЖЕ УСТРОЙСТВОМ НЕ ЗАВОДИТ ВТОРОЙ ЗАПИСИ: список растёт только на новое
    /// устройство, а «когда видели» обновляется. Ловит правку «просто добавляем»: список рос бы
    /// на каждое обращение, а «подключённые» показывало бы одно и то же устройство десятки раз.
    /// </summary>
    [Fact]
    public void Повторный_вход_не_заводит_второго_устройства()
    {
        var first = PhonePass.Create().WithDevice("device-1", "192.168.0.10", "2026-10-11T10:00:00+00:00");
        var again = first.WithDevice("DEVICE-1", "192.168.0.11", "2026-10-11T11:00:00+00:00");

        Assert.Equal(1, again.DeviceCount);

        var view = Assert.Single(again.SafeDevices);

        Assert.Equal("device-1", view.Id);
        Assert.Equal("2026-10-11T11:00:00+00:00", view.LastSeen);
        Assert.NotEqual(view.AddedAt, view.LastSeen);

        var second = again.WithDevice("device-2", "192.168.0.12", "2026-10-11T12:00:00+00:00");

        Assert.Equal(2, second.DeviceCount);

        // «Отвязать» убирает ровно названное устройство и не трогает остальные.
        var without = second.WithoutDevice("device-1");

        Assert.Equal(1, without.DeviceCount);
        Assert.Equal("device-2", Assert.Single(without.SafeDevices).Id);

        // ⚠️ Отвязка НЕ трогает исходное состояние: список неизменяемый, и «убрал одного»
        // не имеет права молча укоротить тот список, из которого его взяли (проверка ловила
        // ровно это — здесь стояло 1, и она падала на собственном противоречии со строкой выше).
        Assert.Equal(2, second.DeviceCount);
    }

    /// <summary>
    /// НОВЫЙ ПРОПУСК ЗАБЫВАЕТ СТАРЫЕ УСТРОЙСТВА — и это ЦЕЛЬ, а не потеря: «сопрячь заново»
    /// обязано означать «старые устройства больше не пускаются». Ловит правку «Create сохраняет
    /// список»: старый телефон остался бы сопряжён после смены пропуска.
    /// </summary>
    [Fact]
    public void Новый_пропуск_забывает_устройства()
    {
        var withDevice = PhonePass.Create().WithDevice("device-1", "192.168.0.10", "2026-10-11T10:00:00+00:00");

        Assert.Equal(1, withDevice.DeviceCount);

        var fresh = PhonePass.Create();

        Assert.Equal(0, fresh.DeviceCount);
        Assert.False(fresh.Match(TokenOf(withDevice)));
    }

    // ---------------------------------------------------------------- права при записи

    /// <summary>
    /// ПРАВА ФАЙЛА ПРОПУСКА — ТОЛЬКО ВЛАДЕЛЕЦ И СИСТЕМА, И НАСЛЕДОВАНИЕ СНЯТО. Проверяется
    /// НАСТОЯЩИЙ список доступа файла, а не факт вызова функции: пропуск — секрет, и «закрыли,
    /// но не тот файл» здесь стоило бы дорого. Ловит правку «пропуск пишется без прав владельца» —
    /// тогда его прочитал бы любой, кто вошёл в ту же машину.
    /// </summary>
    [Fact]
    public void Права_пропуска_только_владельцу_и_SYSTEM()
    {
        if (!OperatingSystem.IsWindows()) return;

        var paths = Paths;
        var pass = PhonePass.Create();

        Assert.True(pass.Save(Files(paths)));

        var security = new FileInfo(paths.PhonePassFile).GetAccessControl();

        Assert.True(security.AreAccessRulesProtected, "наследование прав у файла пропуска не снято");

        var owner = WindowsIdentity.GetCurrent().User!;
        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

        Assert.NotEmpty(rules);

        foreach (var rule in rules)
        {
            var identity = rule.IdentityReference.Value;

            Assert.True(
                rule.IdentityReference.Equals(owner) || identity == "S-1-5-18",
                $"в правах файла пропуска лишняя запись: {identity}");

            Assert.False(
                identity is "S-1-1-0" or "S-1-5-11" or "S-1-5-32-545",
                $"пропуск доступен другим учётным записям: {identity}");
        }

        // И та же дверь, что у ссылки входа: закрывает тем же способом, а не «своим».
        var again = BackupEngine.RestrictToOwner(paths.PhonePassFile);

        Assert.True(again.Ok, again.Reason);
    }

    /// <summary>
    /// ПРОГОН БЕЗ ПРАВА НЕ ЧИТАЕТ И НЕ ПИШЕТ ПРОПУСК ВЛАДЕЛЬЦА: у пустышек
    /// (<see cref="PhonePassFiles.None"/>) <c>Persists</c> — <c>false</c>, загрузка даёт пустое,
    /// а запись честно отвечает «не сохранил», а не «сохранил» (иначе журнал врал бы).
    /// Ловит правку «Persists всегда истина».
    /// </summary>
    [Fact]
    public void Без_права_пропуск_не_читается_и_не_пишется()
    {
        var none = PhonePassFiles.None;

        Assert.False(none.Persists);
        Assert.False(PhonePass.Load(none).Any);

        var pass = PhonePass.Create();

        Assert.False(pass.Save(none));
        Assert.False(pass.Save(none));
    }

    /// <summary>
    /// ОТКАЗ В ПРАВАХ НЕ ОТМЕНЯЕТ ЗАПИСЬ, НО И НЕ МОЛЧИТ: путь занят каталогом — запись не проходит,
    /// и <c>Save</c> отвечает «нет», а не бросает. Молчаливый отказ здесь стоил бы дорого: панель
    /// сказала бы «сопряжено», а пропуска на диске не было бы.
    /// </summary>
    [Fact]
    public void Некуда_писать_отвечает_нет_и_не_бросает()
    {
        var paths = Paths;

        // Занимаем путь ФАЙЛА каталогом: записать туда нельзя.
        Directory.CreateDirectory(paths.PhonePassFile);

        var pass = PhonePass.Create();

        Assert.False(pass.Save(Files(paths)));
        Assert.False(File.Exists(paths.PhonePassFile + ".tmp"));
    }

    /// <summary>
    /// ПУТЬ ПРОПУСКА — 18-й В ПЕРЕЧНЕ, И ОН ПОД КОРНЕМ ПРОГОНА. Это последний довод изоляции:
    /// путь, забытый в <see cref="AppPaths.AllPaths"/>, изолированный прогон не подменит, и однажды
    /// он прочитал бы (или записал) НАСТОЯЩИЙ пропуск владельца.
    /// </summary>
    [Fact]
    public void Путь_пропуска_в_перечне_путей_и_под_корнем()
    {
        var paths = Paths;

        Assert.Contains(paths.PhonePassFile, paths.AllPaths);
        Assert.Equal(18, paths.AllPaths.Count);
        Assert.EndsWith(Path.Combine("state", "phone-pass.txt"), paths.PhonePassFile, StringComparison.OrdinalIgnoreCase);

        var prefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;

        Assert.StartsWith(prefix, paths.PhonePassFile, StringComparison.OrdinalIgnoreCase);

        // И у настоящего прогона он тоже под корнем панели, а не рядом с чужими данными.
        var owner = AppPaths.ForOwner();

        Assert.StartsWith(
            Path.TrimEndingDirectorySeparator(owner.Root) + Path.DirectorySeparatorChar,
            owner.PhonePassFile,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ПРОПУСК — НАШ ФАЙЛ, И ОН В ОПИСИ ПОСТАВЛЕННОГО (этап G снимает поставленное панелью).
    /// Проверяется ПУТЁМ, а не содержимым: <see cref="InstalledInventory.Collect"/> файл только
    /// называет, а внутрь не смотрит — там пропуск.
    /// </summary>
    [Fact]
    public void Пропуск_назван_в_описи_поставленного()
    {
        var paths = Paths;

        var entries = InstalledInventory.Collect(paths, @"C:\Panel\DshPanel.exe", autostart: false, Array.Empty<string>());

        Assert.Contains(
            entries,
            entry => entry.Kind == InstalledInventory.KindFile && entry.Path == paths.PhonePassFile);

        // И опись его НЕ ЧИТАЕТ: содержимое файла остаётся нетронутым, а сам он может не существовать.
        Assert.False(File.Exists(paths.PhonePassFile));
    }

    // ---------------------------------------------------------------- CH-01: гонка записи пропуска

    /// <summary>
    /// ПРОПУСК ЧИТАЕТСЯ ТАК, ЧТО НЕ ЗАПИРАЕТ ЗАПИСЬ (дефект `CH-01` цикла C1, пачка D).
    ///
    /// **Что было.** `PhonePassFiles` читала файл через `File.ReadAllText`, а тот открывает его
    /// с `FileShare.Read` — то есть **пускает только читателей**. Читателей в продукте много и они
    /// частые: список устройств читается на КАЖДЫЙ рендер раздела настроек, состояние значка —
    /// на каждое обновление трея. Пока такой читатель держал файл, запись сопряжения получала отказ
    /// совместного доступа, `Save` возвращал `false`, и человек видел **500 вместо входа**. Отсюда
    /// и мерцание `PhoneLookTests` в C1: прогон падал на РАЗНЫХ тестах одного класса — дело было
    /// в общем файле, а не в тесте.
    ///
    /// **Как проверяется — детерминированно, без гонки.** Читатель держит файл открытым
    /// (через ту же дверь, какой его читает продукт: <see cref="PhonePassFiles.OpenRead"/>), и
    /// в это время идёт запись. До правки запись отказывала (тест красный), после — проходит.
    /// Никакого «повезло/не повезло» здесь нет: отказ воспроизводится каждый раз.
    ///
    /// **Мутация, которая обязана упасть здесь:** вернуть чтению `FileShare.Read` (как у
    /// `File.ReadAllText`) — `CF5`.
    /// </summary>
    [Fact]
    public void Чтение_пропуска_не_запирает_запись()
    {
        var paths = Paths;
        var files = Files(paths);
        var stamp = PricingDecisions.Stamp(DateTimeOffset.Now);

        Assert.True(PhonePass.Create().WithDevice("phone-1", "192.168.0.10", stamp).Save(files));

        // ЧИТАТЕЛЬ ДЕРЖИТ ФАЙЛ — ровно так, как его держит рендер раздела и состояние значка.
        using var held = PhonePassFiles.OpenRead(paths.PhonePassFile);

        var next = PhonePass.Load(files).WithDevice("phone-2", "192.168.0.11", stamp);

        Assert.True(
            next.Save(files),
            "запись пропуска отказала, пока файл читает сама панель: это и есть дефект CH-01");

        // И запись ДОШЛА: второго устройства не потеряли.
        Assert.Equal(2, PhonePass.Load(files).DeviceCount);
    }

    /// <summary>
    /// ОТКАЗ ЗАПИСИ НАЗЫВАЕТ ПРИЧИНУ — И НЕ УНОСИТ ПУТЬ (дефект `CH-01`, пачка D).
    ///
    /// **Что было.** `PhonePass.Save` ловил ЛЮБОЕ исключение и возвращал `false`, ничего не записав.
    /// Поэтому мерцание сопряжения в C1 осталось загадкой на четыре зелёных прогона: человек видел
    /// 500, а журнал — ни строки. «Отказ без причины» нельзя ни доказать, ни опровергнуть.
    ///
    /// **Как проверяется.** Запись доводится до отказа ДЕТЕРМИНИРОВАННО: на месте файла пропуска
    /// лежит КАТАЛОГ, переименовать поверх него файл нельзя. Требуется: `false`, ровно одна строка
    /// в журнале, в ней назван вид отказа и код, и **нет пути этой машины** (журнал уезжает
    /// в резервную копию и в чужой отчёт — красная линия 7).
    ///
    /// **Мутация, которая обязана упасть здесь:** вернуть голый `catch` без строки журнала — `CF6`.
    /// </summary>
    [Fact]
    public void Отказ_записи_пропуска_называет_причину_и_не_уносит_путь()
    {
        var paths = Paths;

        // ДЕТЕРМИНИРОВАННЫЙ ОТКАЗ: на месте файла — каталог, поверх него файл не встанет.
        Directory.CreateDirectory(paths.PhonePassFile);

        var log = new System.Collections.Generic.List<string>();
        var files = Files(paths, log);

        var pass = PhonePass.Create().WithDevice(
            "phone-1", "192.168.0.10", PricingDecisions.Stamp(DateTimeOffset.Now));

        Assert.False(pass.Save(files), "запись поверх каталога объявилась удачей");

        // ПРИЧИНА НАЗВАНА — ровно одной строкой, той самой дверью.
        var line = Assert.Single(log);

        var head = DshPanel.Shell.PanelStrings.PhonePassSaveFailedLogFormat.Split("{0}")[0].Trim().TrimEnd(':', '—').Trim();

        Assert.Contains(head, line, StringComparison.Ordinal);

        // Вид отказа и код — это и есть «причина»: по коду видно, занят файл или нет прав.
        Assert.Matches(@"\(0x[0-9A-F]{8}\)", line);

        // И ПУТИ В СТРОКЕ НЕТ: каталог пользователя — личные данные (красная линия 7).
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.DoesNotContain(profile, line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(paths.Root, line, StringComparison.OrdinalIgnoreCase);

        // Недописанный временный файл за собой убран: следующий проход не должен найти мусор.
        Assert.False(File.Exists(paths.PhonePassFile + ".new"));
    }

    /// <summary>
    /// ЗАПИСЬ ПРОПУСКА НЕ РЕЖЕТ ФАЙЛ НА МЕСТЕ: читатель видит ЛИБО прежнее, ЛИБО новое (CH-01).
    ///
    /// **Зачем отдельная проверка.** Разрешения на запись мало: если писать «поверх», файл на
    /// мгновение становится пустым, и читатель, вошедший в это мгновение, увидит «сопряжения нет» —
    /// у человека, который только что сопрягся. Поэтому запись идёт во временный файл рядом
    /// и переименовывается поверх (замена атомарна в пределах каталога).
    ///
    /// **Как проверяется без гонки.** Читатель держит файл открытым, а запись обязана пройти
    /// и **не тронуть** открытый дескриптор: содержимое, прочитанное через уже открытый поток,
    /// остаётся прежним (старый файл не обрезан), а новый читатель видит новое содержимое целиком.
    /// Если писать на месте, открытый поток увидел бы обрезанный файл.
    ///
    /// **Мутация, которая обязана упасть здесь:** писать прямо в целевой файл, без временного
    /// и переименования — `CF10`.
    /// </summary>
    [Fact]
    public void Запись_пропуска_не_режет_файл_на_месте()
    {
        var paths = Paths;
        var files = Files(paths);
        var stamp = PricingDecisions.Stamp(DateTimeOffset.Now);

        var one = PhonePass.Create().WithDevice("phone-1", "192.168.0.10", stamp);

        Assert.True(one.Save(files));

        using (var held = PhonePassFiles.OpenRead(paths.PhonePassFile))
        {
            var two = one.WithDevice("phone-2", "192.168.0.11", stamp);

            Assert.True(two.Save(files));

            // ОТКРЫТЫЙ ПОТОК ВИДИТ ПРЕЖНЕЕ ЦЕЛИКОМ — файл не обрезан и не переписан на месте.
            using var reader = new StreamReader(held);

            Assert.Equal(one.ToJson(), reader.ReadToEnd());
        }

        // А новый читатель видит новое — и целиком: два устройства на месте.
        var fresh = PhonePass.Load(files);

        Assert.Equal(2, fresh.DeviceCount);
        Assert.Equal(one.WithDevice("phone-2", "192.168.0.11", stamp).ToJson(), fresh.ToJson());
    }
}
