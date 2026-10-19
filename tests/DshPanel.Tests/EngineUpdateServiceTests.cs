using System;
using System.Collections.Generic;
using System.Linq;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// РАЗДЕЛ «ОБНОВЛЕНИЕ ДВИЖКА» СО СТОРОНЫ ДЕЛА (пункты 4–7 плана E3.2): две строки версий, что
/// делает главная кнопка, что отдельная, и чем кончается путь.
///
/// **Почему это проверяется без окна, сети и живого движка.** У раздела две двери: «чем узнать
/// версии» и «чем обновить» (<see cref="EngineUpdateSectionDoors"/>). Здесь обе подставные, поэтому
/// проверка идёт по ВСЕМУ пути — включая то, что установка не начинается, когда не снялась копия,
/// — но ни одной сети, ни одного npm и ни одного процесса.
///
/// ⚠️ Отдельно проверено, что **без дверей раздел не ходит в сеть вовсе**: у прогона проверки
/// и в кадре окна обновление движка недоступно, и читать подсказки ради строк, которых всё равно
/// не будет, — это сеть без причины.
/// </summary>
public class EngineUpdateServiceTests
{
    private static EngineHints Hints(string verified, string unverified) => EngineHints.Parse(
        "{\"format\":1,\"versions\":[" +
        "{\"version\":\"" + verified + "\",\"verified\":true,\"notes\":{\"ru\":[\"з\"]},\"troubles\":{\"ru\":[\"б\"]}}," +
        "{\"version\":\"" + unverified + "\",\"verified\":false,\"notes\":{\"ru\":[\"з\"]},\"troubles\":{\"ru\":[\"б\"]}}]}");

    private static EngineInstallResult Installed(string version) =>
        new(EngineInstallOutcome.Installed, version, "C:\\проба\\engine\\" + version, string.Empty, "поставлено по замку", null);

    /// <summary>Стенд: подставные двери, которые считают, ЧТО у них спросили.</summary>
    private sealed class Stand
    {
        public List<string> AskedFor { get; } = new();
        public int HintsReads { get; private set; }
        public int FreshReads { get; private set; }
        public int StopCalls { get; private set; }

        public EngineHints Hints { get; set; } = Hints("0.1.5-rc.2", "0.2.0-rc.2");
        public string Fresh { get; set; } = "0.2.0-rc.2";
        public bool Ready { get; set; } = true;
        public bool Stop { get; set; } = true;
        public bool Copy { get; set; } = true;
        public bool Probe { get; set; } = true;

        /// <summary>Есть ли замок для свежей версии: по умолчанию — НЕТ (замок снят с проверенной).</summary>
        public bool LockForFresh { get; set; }

        /// <summary>
        /// ЧИСЛА ЖУРНАЛА, КОТОРЫЕ ОТДАЁТ ДВЕРЬ (блок C). По умолчанию — «двери нет»: тогда раздел
        /// обязан сказать «узнать не удалось», а не «прятать нечего».
        /// </summary>
        public EngineJournalState Journal { get; set; } = EngineJournalState.Unexamined;

        /// <summary>Запись «что стоит сейчас» и папки версий — для отката; по умолчанию отката нет.</summary>
        public EngineInstallRecord Record { get; set; } = EngineInstallRecord.Empty;

        public IReadOnlyList<string> Folders { get; set; } = Array.Empty<string>();

        /// <summary>Двери отката: по умолчанию все три согласны — проверяется путь, а не машина.</summary>
        public bool RollbackStop { get; set; } = true;
        public bool RollbackProbe { get; set; } = true;
        public bool RollbackRecordWriting { get; set; } = true;

        public EngineUpdateSectionDoors Doors() => new(
            ReadHints: () =>
            {
                HintsReads++;
                return Hints;
            },
            ReadFresh: () =>
            {
                FreshReads++;
                return Fresh;
            },
            OwnPort: () => 3081,
            Run: new EngineUpdateDoors(
                StopOwnEngine: (_, _) =>
                {
                    StopCalls++;
                    return Stop;
                },
                CopyProfile: (_, _) => Copy,
                Install: (version, _) =>
                {
                    AskedFor.Add(version);
                    return Installed(version);
                },
                Probe: (_, _) => Probe),
            Ready: Ready)
        {
            HasLockFor = version => LockForFresh && version == Fresh,
            ReadJournal = () => Journal,
            ReadRecord = () => Record,
            ReadFolders = () => Folders,
            RollbackDoors = new EngineRollbackDoors(
                _ => RollbackStop,
                _ => RollbackProbe,
                _ => RollbackRecordWriting),
        };

        public EngineUpdateService Service(List<string>? log = null) =>
            new(Doors(), log is null ? null : log.Add);
    }

    /// <summary>
    /// БЕЗ ДВЕРЕЙ — НЕЧЕМ, И В СЕТЬ НЕ ХОДИМ: у прогона проверки обновление движка недоступно,
    /// и раздел говорит это ключом, а не пустыми строками.
    /// </summary>
    [Fact]
    public void Без_дверей_обновлять_нечем_и_в_сеть_не_ходим()
    {
        var stand = new Stand { Ready = false };
        var service = stand.Service();

        Assert.False(service.Available, "раздел показал «можно обновлять» без дверей");
        Assert.Equal(EngineInstallRefusals.UpdateUnavailable, service.RefusalKey);
        Assert.Equal(string.Empty, service.Recommended);
        Assert.Equal(0, stand.HintsReads);
        Assert.Equal(0, stand.FreshReads);

        // И нажатие кнопки ничего не делает — говорит словами, что здесь нельзя.
        Assert.Equal(
            EngineInstallPhrases.UpdateRefusal(EngineInstallRefusals.UpdateUnavailable),
            service.Run(freshWanted: false));
        Assert.Empty(stand.AskedFor);
    }

    /// <summary>ДВЕ СТРОКИ РАЗНЫЕ: рекомендуемая — проверенная пробой, свежая — из реестра npm.</summary>
    [Fact]
    public void Рекомендованная_и_свежая_показываются_раздельно()
    {
        var service = new Stand().Service();

        Assert.True(service.Available);
        Assert.Equal(string.Empty, service.RefusalKey);
        Assert.Equal("0.1.5-rc.2", service.Recommended);
        Assert.Equal("0.2.0-rc.2", service.Fresh);
        Assert.True(service.FreshIsNewer, "свежая обязана быть новее — иначе случай не тот");

        // Свежая не новее (или её не узнали) — отдельная строка не показывается вовсе.
        var stand = new Stand { Fresh = "0.1.5-rc.2" };
        var same = stand.Service();

        Assert.False(same.FreshIsNewer);

        var unknown = new Stand { Fresh = string.Empty }.Service();
        Assert.False(unknown.FreshIsNewer);
    }

    /// <summary>ГЛАВНАЯ КНОПКА СТАВИТ ТОЛЬКО РЕКОМЕНДОВАННУЮ — даже когда свежая новее.</summary>
    [Fact]
    public void Главная_кнопка_ставит_рекомендованную()
    {
        var stand = new Stand();
        var service = stand.Service();

        var answer = service.Run(freshWanted: false);

        Assert.Equal(new[] { "0.1.5-rc.2" }, stand.AskedFor);
        Assert.Equal(EngineInstallPhrases.UpdateDone("0.1.5-rc.2"), answer);
    }

    /// <summary>
    /// ОТДЕЛЬНАЯ КНОПКА СТАВИТ СВЕЖУЮ — но ТОЛЬКО ЕСЛИ ДЛЯ НЕЁ ЕСТЬ ЗАМОК.
    ///
    /// ⚠️ Панель ставит движок только по замку: без этой проверки она поставила бы проверенную
    /// версию, а объявила свежую (то есть сказала бы неправду). Отказ — словами и ДО работы.
    /// </summary>
    [Fact]
    public void Отдельная_кнопка_ставит_свежую()
    {
        var stand = new Stand { LockForFresh = true };
        var service = stand.Service();

        Assert.True(service.FreshInstallable, "замок для свежей есть — ставить можно");

        service.Run(freshWanted: true);

        Assert.Equal(new[] { "0.2.0-rc.2" }, stand.AskedFor);

        // Свежая новее, но ЗАМКА для неё нет: кнопка обязана отказать ДО всякой работы.
        var unlocked = new Stand();
        var unlockedService = unlocked.Service();

        Assert.False(unlockedService.FreshInstallable, "замка для свежей нет — ставить нельзя");
        Assert.Equal(EngineInstallPhrases.NoLockForVersion("0.2.0-rc.2"), unlockedService.Run(freshWanted: true));
        Assert.Empty(unlocked.AskedFor);
        Assert.Equal(0, unlocked.StopCalls);

        // Та же версия, что рекомендованная: ставить нечего — отказ словами, установки нет.
        var same = new Stand { Fresh = "0.1.5-rc.2" };
        var sameService = same.Service();

        Assert.Equal(
            EngineInstallPhrases.UpdateRefusal(EngineUpdateFlow.Words.FreshNotNewer),
            sameService.Run(freshWanted: true));
        Assert.Empty(same.AskedFor);
    }

    /// <summary>
    /// ПОЛУАРХИВА НЕ БЫВАЕТ: копия не снялась — установка НЕ начинается, и это видно по дверям
    /// (установку не спросили вовсе), а не только по строке ответа.
    /// </summary>
    [Fact]
    public void Копия_не_снялась_значит_установки_не_было()
    {
        var stand = new Stand { Copy = false };
        var service = stand.Service();

        var answer = service.Run(freshWanted: false);

        Assert.Empty(stand.AskedFor);
        Assert.Equal(EngineInstallPhrases.UpdateStep(EngineUpdateFlow.Words.CopyNotWhole), answer);
    }

    /// <summary>СВОЙ ДВИЖОК НЕ ПОГАС — версию не подменяем: словами «сначала остановите его».</summary>
    [Fact]
    public void Движок_не_погас_значит_установки_не_было()
    {
        var stand = new Stand { Stop = false };
        var service = stand.Service();

        var answer = service.Run(freshWanted: false);

        Assert.Equal(1, stand.StopCalls);
        Assert.Empty(stand.AskedFor);
        Assert.Equal(EngineInstallPhrases.UpdateStopFailed(), answer);
    }

    /// <summary>ПРОБА НЕ ПРОШЛА — «готово» панель не говорит, хотя версия уже поставлена.</summary>
    [Fact]
    public void Проба_не_прошла_значит_готово_не_говорим()
    {
        var stand = new Stand { Probe = false };
        var service = stand.Service();

        var answer = service.Run(freshWanted: false);

        Assert.Equal(new[] { "0.1.5-rc.2" }, stand.AskedFor);
        Assert.Equal(EngineInstallPhrases.UpdateProbeFailed(), answer);
    }

    /// <summary>ПОВТОРНЫЙ ВОПРОС О ВЕРСИЯХ: `Refresh` берёт новые — и подсказки, и свежую.</summary>
    [Fact]
    public void Повторный_вопрос_берёт_новые_версии()
    {
        var stand = new Stand();
        var service = stand.Service();

        Assert.Equal("0.1.5-rc.2", service.Recommended);

        stand.Hints = Hints("0.1.6-rc.2", "0.2.0-rc.2");
        stand.Fresh = "0.3.0-rc.2";
        service.Refresh();

        Assert.Equal("0.1.6-rc.2", service.Recommended);
        Assert.Equal("0.3.0-rc.2", service.Fresh);
        Assert.Equal(2, stand.HintsReads);
        Assert.Equal(2, stand.FreshReads);
    }

    /// <summary>СТРОКИ ХОДА ИДУТ В ЖУРНАЛ: путь обновления говорит словами до дела и после.</summary>
    [Fact]
    public void Ход_обновления_уходит_в_журнал()
    {
        var log = new List<string>();
        var stand = new Stand();
        var service = stand.Service(log);

        service.Run(freshWanted: false);

        Assert.Contains(log, line => line.Contains("0.1.5-rc.2", StringComparison.Ordinal));
        Assert.Contains(EngineInstallPhrases.UpdateDone("0.1.5-rc.2"), log);
        Assert.Equal(EngineInstallPhrases.UpdateDone("0.1.5-rc.2"), log[^1]);
    }

    /// <summary>
    /// ⚠️ ПРОВЕРКА, КОТОРАЯ ЛОВИТ «УМНОЕ ОПРЕДЕЛЕНИЕ»: путь идёт ЧЕРЕЗ ВСЕ ЧЕТЫРЕ ДВЕРИ и в том
    /// порядке, в каком их назвал план. Если однажды кто-то «оптимизирует» путь и начнёт ставить
    /// движок, не погасив прежний, — это упадёт здесь, а не у человека.
    /// </summary>
    [Fact]
    public void Путь_проходит_все_четыре_двери()
    {
        var order = new List<string>();

        var doors = new EngineUpdateSectionDoors(
            ReadHints: () => Hints("0.1.5-rc.2", "0.2.0-rc.2"),
            ReadFresh: () => "0.2.0-rc.2",
            OwnPort: () => 3081,
            Run: new EngineUpdateDoors(
                StopOwnEngine: (port, _) =>
                {
                    order.Add("погасить " + port);
                    return true;
                },
                CopyProfile: (version, _) =>
                {
                    order.Add("копия " + version);
                    return true;
                },
                Install: (version, _) =>
                {
                    order.Add("поставить " + version);
                    return Installed(version);
                },
                Probe: (version, _) =>
                {
                    order.Add("проба " + version);
                    return true;
                }));

        var service = new EngineUpdateService(doors);

        service.Run(freshWanted: false);

        Assert.Equal(
            new[] { "погасить 3081", "копия 0.1.5-rc.2", "поставить 0.1.5-rc.2", "проба 0.1.5-rc.2" },
            order);
    }

    // ---------------------------------------------------- предохранитель журнала (блок C)

    /// <summary>Замер `l2` числами: 197 сессий, 5 переехали в новый формат, 192 ещё в старом.</summary>
    private static EngineJournalState Journal(int sessions, int newFormat, int oldFormatOnly) =>
        new(true, sessions, newFormat, oldFormatOnly, 0, 0, string.Empty);

    /// <summary>Запись «стоит сейчас» 0.2.1-rc.2, на диске обе версии — откат возможен.</summary>
    private static EngineInstallRecord RollbackRecord() =>
        new("0.2.1-rc.2", "C:\\проба\\engine\\0.2.1-rc.2", "22.23.3", "C:\\проба\\node.exe", true,
            new List<string> { "0.1.5-rc.2", "0.2.1-rc.2" });

    private static Stand RollbackStand(EngineJournalState journal) => new()
    {
        Record = RollbackRecord(),
        Folders = new[] { "0.1.5-rc.2", "0.2.1-rc.2" },
        Journal = journal,
    };

    /// <summary>
    /// ВОПРОС ПЕРЕД ОТКАТОМ НАЗЫВАЕТ ЧИСЛО — ГЛАВНОЕ ТРЕБОВАНИЕ БЛОКА C. Не «сессии могут стать
    /// невидимыми», а «5 из 197»: число снято с диска, и оно единственное делает предупреждение
    /// проверяемым (замер `l2`: 197 → 196 молча).
    /// </summary>
    [Fact]
    public void Вопрос_перед_откатом_называет_число_сессий()
    {
        var service = RollbackStand(Journal(197, 5, 192)).Service();
        var question = service.RollbackQuestion;

        Assert.True(service.RollbackAvailable, "откат возможен — иначе случай не тот");
        Assert.Equal("0.1.5-rc.2", service.RollbackTarget);

        Assert.Contains("5", question.Text, StringComparison.Ordinal);
        Assert.Contains("197", question.Text, StringComparison.Ordinal);
        Assert.Contains("0.1.5-rc.2", question.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("{", question.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПОСЛЕ ОТКАТА ПАНЕЛЬ ГОВОРИТ САМА — это вторая половина предохранителя. Движок о спрятанных
    /// сессиях не скажет никогда (замер: 197 → 196 без поля и без слова), поэтому итоговая строка
    /// обязана назвать число, а журнал панели — сохранить её.
    /// </summary>
    [Fact]
    public void После_отката_сказано_сколько_сессий_спряталось()
    {
        var log = new List<string>();
        var stand = RollbackStand(Journal(197, 5, 192));
        var service = stand.Service(log);

        var answer = service.Rollback();

        Assert.Contains("5", answer, StringComparison.Ordinal);
        Assert.Contains("197", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("{", answer, StringComparison.Ordinal);

        // Строка журнала — не та же, что у пути отката: она про сессии и сказана ПОСЛЕ дела.
        Assert.Contains(log, line => line.Contains("5", StringComparison.Ordinal)
            && line.Contains("197", StringComparison.Ordinal));
    }

    /// <summary>
    /// ПРЯТАТЬ НЕЧЕГО — итог не обрастает лишним: сказано то, чем кончился путь, и только.
    /// Предупреждение «ничего не спрячется» человек прочитал в вопросе — повторять его после дела
    /// значит удлинять строку, в которой человек ищет «получилось или нет».
    /// </summary>
    [Fact]
    public void Если_прятать_нечего_итог_не_обрастает()
    {
        var stand = RollbackStand(Journal(197, 0, 197));
        var service = stand.Service();

        var answer = service.Rollback();

        Assert.Equal(EngineInstallPhrases.UpdateStep(EngineRollbackDecisions.Words.Done, "0.1.5-rc.2", "0.2.1-rc.2"), answer);
        Assert.DoesNotContain("197", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОТКАТ НЕ СОСТОЯЛСЯ — про спрятанные сессии не говорим вовсе: на машине ничего не изменилось,
    /// и «сервер не видит 5 из 197» было бы неправдой о том, чего не случилось.
    /// </summary>
    [Fact]
    public void Откат_не_состоялся_про_сессии_не_говорим()
    {
        var stand = RollbackStand(Journal(197, 5, 192));
        stand.RollbackProbe = false;

        var service = stand.Service();
        var answer = service.Rollback();

        Assert.Equal(EngineInstallPhrases.UpdateStep(EngineRollbackDecisions.Words.ProbeFailed, "0.1.5-rc.2", "0.2.1-rc.2"), answer);
        Assert.DoesNotContain("197", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// ВОПРОС ПЕРЕД ОБНОВЛЕНИЕМ — ПОД ТУ ВЕРСИЮ, КОТОРУЮ ПОСТАВЯТ, и с числом «сколько переедет».
    ///
    /// ⚠️ Отдельной кнопке непроверенной версии это нужно не меньше главной: решение владельца
    /// 10.10.2026 разрешает её, но человек обязан видеть последствие ДО подтверждения, а прежнее
    /// окно собирало текст само и добавленное дверью слово теряло.
    /// </summary>
    [Fact]
    public void Вопрос_перед_обновлением_называет_переезд_и_ту_версию()
    {
        // Проверенная 0.2.0 (по ней поставит главная кнопка), свежая 0.2.1 — новее и с замком.
        var stand = new Stand
        {
            Hints = Hints("0.2.0-rc.2", "0.2.1-rc.2"),
            Fresh = "0.2.1-rc.2",
            LockForFresh = true,
            Journal = Journal(197, 5, 192),
        };

        var service = stand.Service();

        var main = service.UpdateQuestionFor(freshWanted: false);

        Assert.Contains("0.2.0-rc.2", main.Text, StringComparison.Ordinal);
        Assert.Contains("192", main.Text, StringComparison.Ordinal);
        Assert.Contains("197", main.Text, StringComparison.Ordinal);

        var fresh = service.UpdateQuestionFor(freshWanted: true);

        Assert.Contains("0.2.1-rc.2", fresh.Text, StringComparison.Ordinal);
        Assert.Contains("192", fresh.Text, StringComparison.Ordinal);
        Assert.NotEqual(main.Text, fresh.Text);

        // Главная кнопка — всегда про рекомендованную версию, даже если спросили «как для свежей».
        Assert.Equal(main.Text, service.UpdateQuestion.Text);
    }

    /// <summary>
    /// ОБНОВЛЕНИЕ НА ВЕРСИЮ, ЧИТАЮЩУЮ ТОЛЬКО СТАРЫЙ ЖУРНАЛ (наш случай: проверенная — 0.1.5):
    /// вперёд переезда не будет, зато названо уже спрятанное — те, кто переехал раньше. Это другое
    /// число и другие слова, и путать их нельзя: человек читает вопрос перед подменой версии.
    /// </summary>
    [Fact]
    public void Вопрос_перед_обновлением_на_старую_версию_называет_уже_спрятанное()
    {
        var stand = new Stand { Journal = Journal(197, 5, 192) };
        var question = stand.Service().UpdateQuestionFor(freshWanted: false);

        Assert.Contains("0.1.5-rc.2", question.Text, StringComparison.Ordinal);
        Assert.Contains("5", question.Text, StringComparison.Ordinal);
        Assert.Contains("197", question.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ДВЕРИ ЖУРНАЛА НЕТ — ВОПРОС ГОВОРИТ «УЗНАТЬ НЕ УДАЛОСЬ», а не «прятать нечего»: в прогоне
    /// проверки и в кадре окна чисел нет вовсе, и притворяться нулём там нельзя.
    /// </summary>
    [Fact]
    public void Без_двери_журнала_вопрос_говорит_не_узнали()
    {
        var stand = new Stand();

        Assert.Equal(EngineJournalState.Unexamined, stand.Journal);

        var question = stand.Service().UpdateQuestionFor(freshWanted: false);

        Assert.Contains("no-door", question.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("сессий нет", question.Text, StringComparison.OrdinalIgnoreCase);
    }
}
