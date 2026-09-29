using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DshPanel.Autostart;
using DshPanel.Isolation;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки автозапуска. Ни одна из них не трогает настоящий <c>HKCU\...\Run</c>:
/// решения перебираются на чистых функциях, хранилище — на файле во временном каталоге,
/// а для «а что если реестр заперт» есть подставное хранилище в памяти.
///
/// Настоящий реестр упражняет только проба на собранном exe (<c>--autostart-selftest</c>),
/// и она заводит СВОЮ ветку.
/// </summary>
public class AutostartTests
{
    /// <summary>Хранилище в памяти. Умеет ещё и врать: записать «получилось», ничего не записав.</summary>
    private sealed class FakeStore : IAutostartStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public FakeStore(bool writable = true, bool readable = true, bool lieAboutWrites = false)
        {
            Writable = writable;
            Readable = readable;
            LieAboutWrites = lieAboutWrites;
        }

        public string Describe => "память";

        public bool Writable { get; }
        public bool Readable { get; }
        public bool LieAboutWrites { get; }

        /// <summary>Имена, к которым обращались на запись и на снятие. По ним и проверяем, что мы не трогаем чужое.</summary>
        public List<string> Written { get; } = new();
        public List<string> Deleted { get; } = new();

        public AutostartRecord Read(string name) => Readable
            ? _values.TryGetValue(name, out var value) ? new AutostartRecord(true, value) : AutostartRecord.Missing
            : AutostartRecord.Unreadable;

        public bool Write(string name, string value)
        {
            Written.Add(name);
            if (LieAboutWrites) return true;
            if (!Writable) return false;

            _values[name] = value;
            return true;
        }

        public bool Delete(string name)
        {
            Deleted.Add(name);
            if (!Writable) return false;

            _values.Remove(name);
            return true;
        }

        public void Seed(string name, string value) => _values[name] = value;
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "dsh-panel-autostart-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Убрать временный каталог и, если он опустел, его родителя: за проверкой не должно оставаться мусора.</summary>
    private static void RemoveTemp(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

            var parent = Path.GetDirectoryName(dir);
            if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent);
        }
        catch
        {
            // Родитель занят другой проверкой — это не ошибка.
        }
    }

    private static string Absent => @"C:\нет\такого\каталога\DshPanel.exe";

    // ---------------------------------------------------------------- разбор значения

    /// <summary>
    /// Кавычки — не украшение: панель ставят и в путь с пробелом, а без кавычек Windows разберёт
    /// командную строку по пробелу. В v1 это был реальный путь `%LOCALAPPDATA%\Programs\DSH Panel`.
    /// </summary>
    [Fact]
    public void Путь_с_пробелом_записывается_в_кавычках_и_разбирается_обратно()
    {
        var path = @"C:\Program Files\DSH Panel\DshPanel.exe";
        var value = AutostartDecisions.BuildValue(path);

        Assert.Equal("\"" + path + "\"", value);
        Assert.Equal(path, AutostartDecisions.EntryPath(value));
        Assert.Equal(path, AutostartDecisions.EntryPath(value + " --ключ"));
    }

    [Fact]
    public void Значение_разбирается_и_без_кавычек_и_терпит_мусор()
    {
        Assert.Equal(
            @"C:\Panel\DshPanel.exe",
            AutostartDecisions.EntryPath(@"C:\Panel\DshPanel.exe --tray"));

        // Пустое, пробельное и вовсе не путь — не падение, а «разобрать нечего».
        Assert.Equal(string.Empty, AutostartDecisions.EntryPath(""));
        Assert.Equal(string.Empty, AutostartDecisions.EntryPath("   "));
        Assert.Equal(string.Empty, AutostartDecisions.EntryPath(null));
        Assert.Equal(string.Empty, AutostartDecisions.EntryPath("\""));

        // Хвостовой разделитель срезается: в v1 он ломал разбор путей при обновлении.
        Assert.Equal(
            "\"C:\\Panel\\DshPanel.exe\"",
            AutostartDecisions.BuildValue(@"C:\Panel\DshPanel.exe\"));
    }

    [Fact]
    public void Один_и_тот_же_файл_узнаётся_записанным_по_разному()
    {
        var self = @"C:\Panel\DshPanel.exe";

        Assert.True(AutostartDecisions.IsSelf(self, self));

        // Значение из реестра сначала разбирается, и уже разобранный путь сравнивается с собой.
        Assert.True(AutostartDecisions.IsSelf(AutostartDecisions.EntryPath("\"" + self + "\""), self));
        Assert.True(AutostartDecisions.IsSelf(self.ToUpperInvariant(), self));
        Assert.True(AutostartDecisions.IsSelf(self + @"\", self));

        Assert.False(AutostartDecisions.IsSelf(@"C:\Panel2\DshPanel.exe", self));
        Assert.False(AutostartDecisions.IsSelf("", self));
    }

    // ---------------------------------------------------------------- решения

    /// <summary>
    /// Таблица ожиданий записана ЯВНО, а не формулой: формула повторяла бы код и не заметила бы,
    /// что её поменяли.
    /// </summary>
    [Fact]
    public void Классификация_записи_перебирает_все_случаи()
    {
        const string Self = @"C:\Panel\DshPanel.exe";
        const string Other = @"C:\Другая\DshPanel.exe";

        var cases = new (AutostartRecord Record, bool Exists, AutostartWhere Expected)[]
        {
            (AutostartRecord.Missing,                     false, AutostartWhere.None),    // записи нет
            (new AutostartRecord(true, ""),               false, AutostartWhere.None),    // пустое значение — тоже нет
            (AutostartRecord.Unreadable,                  false, AutostartWhere.Unknown), // не прочиталось — НЕ «выключено»
            (new AutostartRecord(true, "\"" + Self + "\""), true,  AutostartWhere.Self),
            (new AutostartRecord(true, "\"" + Self + "\""), false, AutostartWhere.Missing), // файла нет
            (new AutostartRecord(true, Other),             true,  AutostartWhere.Other),
            (new AutostartRecord(true, "\""),              false, AutostartWhere.Missing), // разобрать нечего
            (new AutostartRecord(true, @"\\сервер\папка\DshPanel.exe"), true, AutostartWhere.Other), // сетевое не щупаем
        };

        Assert.Equal(8, cases.Length);
        foreach (var c in cases)
        {
            var state = AutostartDecisions.Classify(c.Record, Self, _ => c.Exists);
            Assert.Equal(c.Expected, state.Where);
        }
    }

    /// <summary>
    /// Сверка при старте переписывает РОВНО свою запись и только когда она негодная.
    /// Живая чужая копия не трогается: две копии на машине — законная ситуация.
    /// </summary>
    [Fact]
    public void Сверка_при_старте_переписывает_только_негодную_свою_запись()
    {
        const string Self = @"C:\Panel\DshPanel.exe";
        var expected = AutostartDecisions.BuildValue(Self);

        var cases = new (AutostartState State, bool Expected)[]
        {
            (AutostartState.Off, false),                                                  // выключен — не наше дело
            (new AutostartState(AutostartWhere.Self, Self, expected), false),              // всё в порядке
            (new AutostartState(AutostartWhere.Self, Self, Self), true),                   // своя, но без кавычек
            (new AutostartState(AutostartWhere.Self, Self, expected + " --ключ"), true),   // своя, но с чужим ключом
            (new AutostartState(AutostartWhere.Missing, Absent, "\"" + Absent + "\""), true),
            (new AutostartState(AutostartWhere.Other, @"C:\Другая\DshPanel.exe", @"C:\Другая\DshPanel.exe"), false),
            (AutostartState.UnknownState, false),                                          // не знаем — не трогаем
        };

        Assert.Equal(7, cases.Length);
        foreach (var c in cases)
            Assert.Equal(c.Expected, AutostartDecisions.ShouldRewrite(c.State, Self));
    }

    /// <summary>
    /// Красная линия проекта в коде: панель 2.0 не трогает автозапуск v1. Там живая установка
    /// владельца, и «перенос прежнего имени на себя» (правильный в v1 для своей прежней
    /// генерации) здесь был бы кражей автозапуска у работающей панели.
    /// </summary>
    [Fact]
    public void Чужие_имена_записи_не_трогаются_ни_при_включении_ни_при_выключении()
    {
        Assert.Equal("DSHPanel2", AutostartDecisions.ValueName);
        Assert.NotEqual("DSHPanel", AutostartDecisions.ValueName);
        Assert.NotEqual("DeepSeekHarness", AutostartDecisions.ValueName);

        var store = new FakeStore();
        var controller = new AutostartController(store, Environment.ProcessPath!, _ => { });

        Assert.True(controller.Set(true));
        Assert.True(controller.Set(false));

        // Тронуто ровно одно имя — своё. Ни «DSHPanel» (v1), ни «DeepSeekHarness» (до неё).
        Assert.Equal(new[] { AutostartDecisions.ValueName }, store.Written);
        Assert.Equal(new[] { AutostartDecisions.ValueName }, store.Deleted);
        Assert.DoesNotContain("DSHPanel", store.Written);
        Assert.DoesNotContain("DeepSeekHarness", store.Written);
    }

    [Fact]
    public void Своя_ветка_пробы_не_считается_настоящим_Run()
    {
        Assert.True(AutostartDecisions.IsOwnerRunKey(AutostartDecisions.OwnerRunKeyPath));
        Assert.True(AutostartDecisions.IsOwnerRunKey(@"HKCU\" + AutostartDecisions.OwnerRunKeyPath));
        Assert.True(AutostartDecisions.IsOwnerRunKey(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run\"));

        Assert.False(AutostartDecisions.IsOwnerRunKey(AutostartDecisions.SelfTestKeyPath));
        Assert.False(AutostartDecisions.IsOwnerRunKey(null));
        Assert.False(AutostartDecisions.IsOwnerRunKey(""));
    }

    // ---------------------------------------------------------------- изоляция

    /// <summary>
    /// Изолированный прогон ПИШЕТ АВТОЗАПУСК В ФАЙЛ под своим корнем — до реестра у него
    /// пути нет вовсе. В v1 ветку уводили переменной окружения и стерегли подмену на настоящую;
    /// здесь стеречь нечего, но проверка всё равно нужна: она ловит того, кто однажды выберет
    /// хранилище не по признаку изоляции.
    /// </summary>
    [Fact]
    public void В_изолированном_прогоне_автозапуск_идёт_в_файл_под_корнем()
    {
        var root = TempDir();
        var isolated = RunContext.Create(
            RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, root }, _ => null));

        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = canonical + Path.DirectorySeparatorChar;

        foreach (var humanLaunch in new[] { true, false })
        {
            var store = AutostartStores.For(isolated, humanLaunch);

            Assert.IsType<FileAutostartStore>(store);
            Assert.StartsWith(prefix, store.Describe, StringComparison.OrdinalIgnoreCase);
            Assert.False(AutostartDecisions.IsOwnerRunKey(store.Describe));
        }
    }

    /// <summary>
    /// Право менять машинную запись есть ТОЛЬКО у обычного запуска человеком. У прогона проверки
    /// хранилище то же (читаем, чтобы показать состояние), но только на чтение — иначе проверка
    /// перевела бы запись владельца на себя, как это уже случилось в v1.
    /// </summary>
    [Fact]
    public void Право_писать_в_машинную_запись_есть_только_у_запуска_человеком()
    {
        var owner = RunContext.Create(RunRequest.Owner());

        var byHuman = AutostartStores.For(owner, humanLaunch: true);
        Assert.IsType<RegistryAutostartStore>(byHuman);
        Assert.True(byHuman.Writable);

        var byCheck = AutostartStores.For(owner, humanLaunch: false);
        Assert.IsType<ReadOnlyAutostartStore>(byCheck);
        Assert.False(byCheck.Writable);
        Assert.Equal(byHuman.Describe, byCheck.Describe);
    }

    /// <summary>
    /// Сверка при старте в прогоне проверки не только «не пишет» — она и не пытается:
    /// подставное хранилище не получает ни одной записи, а в журнал уходит строка о причине.
    /// </summary>
    [Fact]
    public void Сверка_в_прогоне_проверки_ничего_не_пишет()
    {
        var inner = new FakeStore();
        inner.Seed(AutostartDecisions.ValueName, "\"C:\\нет\\такого\\DshPanel.exe\"");

        var log = new List<string>();
        var controller = new AutostartController(
            new ReadOnlyAutostartStore(inner), @"C:\Panel\DshPanel.exe", log.Add);

        Assert.False(controller.Repair());
        Assert.False(controller.Set(true));

        Assert.Empty(inner.Written);
        Assert.Empty(inner.Deleted);
        Assert.False(controller.Writable);
        Assert.Contains(log, line => line.Contains("прогон проверки", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- файловое хранилище

    [Fact]
    public void Файловое_хранилище_кладёт_читает_и_снимает_запись()
    {
        var dir = TempDir();
        try
        {
            var store = new FileAutostartStore(Path.Combine(dir, "state", "autostart.txt"));

            Assert.False(store.Read("DSHPanel2").Exists);
            Assert.True(store.Write("DSHPanel2", "\"C:\\Panel\\DshPanel.exe\""));
            Assert.Equal("\"C:\\Panel\\DshPanel.exe\"", store.Read("DSHPanel2").Value);

            // Чужое имя остаётся нетронутым: панель 2.0 распоряжается только своим.
            Assert.True(store.Write("DSHPanel", "\"C:\\v1\\DshTray.exe\""));
            Assert.True(store.Delete("DSHPanel2"));
            Assert.False(store.Read("DSHPanel2").Exists);
            Assert.True(store.Read("DSHPanel").Exists);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    // ---------------------------------------------------------------- контроллер

    /// <summary>
    /// Успех — это СОСТОЯНИЕ ПОСЛЕ записи, а не «запись прошла без ошибки»: реестр бывает заперт
    /// политикой, и тогда человеку надо сказать «не вышло», а не показать включённую галочку.
    /// </summary>
    [Fact]
    public void Запись_которая_не_легла_считается_неудачей()
    {
        var log = new List<string>();
        var store = new FakeStore(lieAboutWrites: true);
        var controller = new AutostartController(store, @"C:\Panel\DshPanel.exe", log.Add);

        Assert.False(controller.Set(true));
        Assert.False(controller.State.Enabled);
        Assert.Contains(log, line => line.Contains("не удалось", StringComparison.Ordinal));
    }

    [Fact]
    public void Нечитаемое_хранилище_даёт_неизвестность_а_не_выключенность()
    {
        var store = new FakeStore(readable: false);
        var controller = new AutostartController(store, @"C:\Panel\DshPanel.exe", _ => { });

        Assert.Equal(AutostartWhere.Unknown, controller.State.Where);
        Assert.False(controller.State.Enabled);
        Assert.False(controller.Repair());
    }

    /// <summary>
    /// Чужая ЖИВАЯ копия панели — не повод отбирать у неё автозапуск: сверка при старте
    /// её не трогает. Решение принимает человек — галочкой в окне.
    /// </summary>
    [Fact]
    public void Живая_чужая_запись_сверкой_не_переписывается()
    {
        var self = Environment.ProcessPath!;
        var neighbour = Path.Combine(AppContext.BaseDirectory, "DshPanel.dll");

        var log = new List<string>();
        var store = new FakeStore();
        store.Seed(AutostartDecisions.ValueName, AutostartDecisions.BuildValue(neighbour));

        var controller = new AutostartController(store, self, log.Add);

        Assert.Equal(AutostartWhere.Other, controller.State.Where);
        Assert.False(controller.Repair());
        Assert.Empty(store.Written);
        Assert.Contains(log, line => line.Contains("ничего не меняла", StringComparison.Ordinal));
    }

    /// <summary>А по прямой просьбе человека запись переводится на эту копию — и сверяется после записи.</summary>
    [Fact]
    public void По_просьбе_человека_запись_переводится_на_эту_копию()
    {
        var self = Environment.ProcessPath!;
        var neighbour = Path.Combine(AppContext.BaseDirectory, "DshPanel.dll");

        var store = new FakeStore();
        store.Seed(AutostartDecisions.ValueName, AutostartDecisions.BuildValue(neighbour));

        var controller = new AutostartController(store, self, _ => { });
        Assert.Equal(AutostartWhere.Other, controller.State.Where);

        Assert.True(controller.Set(true));
        Assert.Equal(AutostartWhere.Self, controller.State.Where);

        Assert.True(controller.Set(false));
        Assert.False(controller.State.Enabled);
    }

    // ---------------------------------------------------------------- отказ виден человеку
    //
    // Галочка после неудачи возвращается в прежнее положение, и без подписи человек видит
    // «щелчок — и ничего». Поэтому отказ ПОМНИТ модель (<c>AutostartPanelModel</c>) и показывает
    // его подписью под галочкой, а не только пишет строку в журнал. Окно при этом ничего
    // не сочиняет: оно рисует <c>NoteText</c>, как и раньше.

    /// <summary>
    /// Удачная попытка — обычная подпись про состояние записи. Отказ не выдумывается там,
    /// где всё получилось.
    /// </summary>
    [Fact]
    public void Удачная_попытка_оставляет_обычную_подпись()
    {
        var self = Environment.ProcessPath!;
        var store = new FakeStore();
        var model = new AutostartPanelModel(new AutostartController(store, self, _ => { }));

        Assert.Equal(PanelStrings.AutostartOff, model.NoteText);

        Assert.True(model.Set(true));
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, PanelStrings.AutostartSelfFormat, self),
            model.NoteText);
    }

    /// <summary>
    /// Отказ ВИДЕН у галочки, а не только в журнале: запись не легла (реестр заперт политикой,
    /// хранилище подделало ответ — неважно), галочка вернулась в прежнее положение, и человек
    /// обязан прочитать, что именно не вышло. Строка журнала при этом никуда не девается:
    /// подпись в окне журнал не заменяет.
    /// </summary>
    [Fact]
    public void Отказ_изменить_запись_виден_подписью_под_галочкой()
    {
        var log = new List<string>();
        var store = new FakeStore(lieAboutWrites: true);   // отвечает «записал», ничего не записав

        var model = new AutostartPanelModel(new AutostartController(store, @"C:\Panel\DshPanel.exe", log.Add));

        Assert.Equal(PanelStrings.AutostartOff, model.NoteText);

        Assert.False(model.Set(true));
        Assert.Equal(PanelStrings.AutostartFailed, model.NoteText);

        Assert.Contains(log, line => line.Contains("изменить не удалось", StringComparison.Ordinal));
    }

    /// <summary>
    /// Отказ НЕ залипает: он говорит про ПОПЫТКУ, а не про состояние записи. Следующее перечитывание
    /// возвращает обычную подпись — иначе «не удалось» пережило бы и удачную попытку.
    /// </summary>
    [Fact]
    public void Отказ_не_залипает_и_снимается_следующим_обновлением()
    {
        var store = new FakeStore(lieAboutWrites: true);
        var model = new AutostartPanelModel(new AutostartController(store, @"C:\Panel\DshPanel.exe", _ => { }));

        Assert.False(model.Set(true));
        Assert.Equal(PanelStrings.AutostartFailed, model.NoteText);

        model.Refresh();

        Assert.Equal(PanelStrings.AutostartOff, model.NoteText);
    }

    /// <summary>
    /// Прогон проверки запись не меняет — и «не удалось» там не показывается: это была бы ложь
    /// про попытку, которой быть не могло (галочка недоступна). Прогон проверки ведёт себя как
    /// раньше: обычная подпись плюс оговорка, что запись не меняется.
    /// </summary>
    [Fact]
    public void В_прогоне_проверки_отказа_не_показывается()
    {
        var inner = new FakeStore();
        var model = new AutostartPanelModel(new AutostartController(
            new ReadOnlyAutostartStore(inner), @"C:\Panel\DshPanel.exe", _ => { }));

        Assert.False(model.CanToggle);
        Assert.False(model.Set(true));
        Assert.Equal(PanelStrings.AutostartOff + PanelStrings.AutostartLockedSuffix, model.NoteText);
        Assert.Empty(inner.Written);
    }

    // ---------------------------------------------------------------- что видит человек
    //
    // Галочка автозапуска переехала из главного окна в окно настроек (решение владельца
    // 24.09.2026: главное окно — про состояние, настройки — в настройках). Поэтому проверки
    // её показа, подстановки состояния и недоступности в прогоне проверки живут теперь
    // в `SettingsTests.cs` — вместе с окном, в котором она стоит. Домен автозапуска
    // и его хранилища проверяются здесь и никуда не переезжали.
}
