using System;
using System.IO;
using DshPanel.Pricing;
using DshPanel.Settings;
using DshPanel.Update;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПОСЛЕДНЯЯ ПОПЫТКА ПРОВЕРКИ ВЫПУСКОВ И ОТСТУП ПОСЛЕ НЕУДАЧИ (четвёртый хвост, 11.10.2026).
///
/// **Зачем это отдельно от следов.** Следы — про УДАЧНЫЙ ответ (он и показывается человеку).
/// Попытка — про само дело: панель стучалась и не достучалась. Отсюда два разных поведения,
/// и оба здесь проверяются **без сети и без часов**:
///
/// * **«проверено» — только про удачу**: отметка <see cref="Settings.PanelSettings.UpdateCheckedAt"/>
///   ставится лишь тогда, когда выпуски действительно прочитаны (иначе панель соврала бы
///   и заперла проверку на сутки);
/// * **«попытка» — про дело**: по ней считается отступ. После отказа по числу запросов (403/429)
///   он несколько часов, после прочей неудачи — час, после удачи — никакого.
/// </summary>
public class UpdateAttemptTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>ОТСТУП ПО ВИДУ ПОПЫТКИ: лимит — часы, прочая неудача — час, удача — ничего.</summary>
    [Fact]
    public void Отступ_зависит_от_вида_попытки()
    {
        var limited = new UpdateAttempt(Now.AddHours(-1), Ok: false, RateLimited: true, "лимит");
        var failed = new UpdateAttempt(Now.AddHours(-1), Ok: false, RateLimited: false, "нет сети");
        var ok = new UpdateAttempt(Now.AddHours(-1), Ok: true, RateLimited: false, string.Empty);

        Assert.Equal(UpdateDecisions.RateLimitBackoff, UpdateDecisions.Backoff(limited));
        Assert.Equal(UpdateDecisions.FailureBackoff, UpdateDecisions.Backoff(failed));
        Assert.Equal(TimeSpan.Zero, UpdateDecisions.Backoff(ok));

        // Попытки не было — ждать нечего.
        Assert.Equal(TimeSpan.Zero, UpdateDecisions.Backoff(UpdateAttempt.None));
        Assert.Equal(TimeSpan.Zero, UpdateDecisions.Backoff(null));

        // Лимит ждёт ДОЛЬШЕ прочей неудачи — иначе отступ не защищал бы выеденный лимит.
        Assert.True(UpdateDecisions.RateLimitBackoff > UpdateDecisions.FailureBackoff);
    }

    /// <summary>
    /// ЖДЁМ ЛИ. Внутри отступа — ждём, после — нет; удачная попытка не ждёт никогда;
    /// часы, переведённые назад, тоже считаются ожиданием (иначе отступ обходится переводом часов).
    /// </summary>
    [Fact]
    public void Ждём_только_внутри_отступа()
    {
        var limited = new UpdateAttempt(Now.AddHours(-1), false, true, "лимит");

        Assert.True(UpdateDecisions.Waiting(Now, limited));
        Assert.False(UpdateDecisions.Waiting(Now.AddHours(7), limited));

        // Прочая неудача держит ЧАС: полчаса назад — ждём, ровно час — уже нет (граница «<»).
        Assert.True(UpdateDecisions.Waiting(Now, new UpdateAttempt(Now.AddMinutes(-30), false, false, "нет сети")));
        Assert.False(UpdateDecisions.Waiting(Now, new UpdateAttempt(Now.AddHours(-1), false, false, "нет сети")));

        Assert.False(UpdateDecisions.Waiting(Now, new UpdateAttempt(Now.AddHours(-1), true, false, string.Empty)));
        Assert.False(UpdateDecisions.Waiting(Now, UpdateAttempt.None));

        // Часы назад: отметка «ещё не наступила» — тот же ответ, что у суточного гейта.
        var future = new UpdateAttempt(Now.AddHours(2), false, true, "лимит");

        Assert.True(UpdateDecisions.Waiting(Now, future));

        // И дата «когда можно снова» считается ОТ ВРЕМЕНИ ПОПЫТКИ, а не от «сейчас»:
        // иначе перевод часов вперёд сокращал бы отступ.
        Assert.Equal(Now.AddHours(8), UpdateDecisions.NextAttempt(future));
    }

    /// <summary>
    /// ГЕЙТ И ОТСТУП ВМЕСТЕ: «пора по расписанию» И «прошлая попытка не запрещает».
    ///
    /// ⚠️ Каждое условие обязательно: без суточного гейта панель стучалась бы каждый такт,
    /// без отступа — сразу после отказа, то есть добавляла бы свои запросы в выеденный лимит.
    /// </summary>
    [Fact]
    public void Пора_по_расписанию_и_отступ_вместе()
    {
        var stale = PricingDecisions.Stamp(Now.AddHours(-25));
        var fresh = PricingDecisions.Stamp(Now.AddHours(-1));

        // Пора, попыток не было — проверяем.
        Assert.True(UpdateDecisions.ShouldCheck(Now, stale, UpdateAttempt.None, isolatedRun: false));

        // Пора, но час назад был отказ по лимиту — молчим.
        Assert.False(UpdateDecisions.ShouldCheck(
            Now, stale, new UpdateAttempt(Now.AddHours(-1), false, true, "лимит"), isolatedRun: false));

        // Пора, отказ по лимиту был семь часов назад — проверяем.
        Assert.True(UpdateDecisions.ShouldCheck(
            Now, stale, new UpdateAttempt(Now.AddHours(-7), false, true, "лимит"), isolatedRun: false));

        // Не пора — молчим даже с пустой попыткой.
        Assert.False(UpdateDecisions.ShouldCheck(Now, fresh, UpdateAttempt.None, isolatedRun: false));

        // Изолированный прогон не ходит в сеть ни при каких условиях.
        Assert.False(UpdateDecisions.ShouldCheck(
            Now, stale, new UpdateAttempt(Now.AddHours(-7), false, true, "лимит"), isolatedRun: true));
    }

    /// <summary>
    /// ЧТЕНИЕ ПОПЫТКИ ИЗ НАСТРОЕК: вид — приметой (не фразой), неразобранное время — «не помним».
    /// </summary>
    [Fact]
    public void Попытка_читается_из_настроек()
    {
        var limited = UpdateAttempt.Parse(PricingDecisions.Stamp(Now), UpdateAttempt.KindLimited, "лимит запросов");

        Assert.True(limited.Any);
        Assert.True(limited.Failed);
        Assert.True(limited.RateLimited);
        Assert.Equal(Now, limited.At);
        Assert.Equal("лимит запросов", limited.Error);

        var ok = UpdateAttempt.Parse(PricingDecisions.Stamp(Now), UpdateAttempt.KindOk, string.Empty);

        Assert.True(ok.Any);
        Assert.False(ok.Failed);
        Assert.False(UpdateDecisions.Waiting(Now, ok));

        var failed = UpdateAttempt.Parse(PricingDecisions.Stamp(Now), UpdateAttempt.KindFailed, "нет сети");

        Assert.True(failed.Failed);
        Assert.False(failed.RateLimited);
        Assert.Equal(UpdateAttempt.KindFailed, failed.Kind);

        // Время не разобралось (или его нет) — попытки не помним: без времени и отступ не посчитать.
        Assert.False(UpdateAttempt.Parse("мусор", UpdateAttempt.KindFailed, "нет сети").Any);
        Assert.False(UpdateAttempt.Parse(null, UpdateAttempt.KindFailed, "нет сети").Any);

        // Вид незнакомый — «прочая неудача», а не «лимит»: не угадываем.
        var unknown = UpdateAttempt.Parse(PricingDecisions.Stamp(Now), "нечто", "беда");

        Assert.True(unknown.Failed);
        Assert.False(unknown.RateLimited);
    }

    /// <summary>
    /// ПОПЫТКА ЛОЖИТСЯ В ФАЙЛ НАСТРОЕК И ПЕРЕЖИВАЕТ ПЕРЕЧИТЫВАНИЕ — и удачная тоже (видом `ok`).
    ///
    /// ⚠️ **Это проверка ДЫРЫ В ЖИВОМ ПУТИ, найденной своей сверкой 11.10.2026.** Дверь попытки
    /// у контроллера была необязательной с пустой заглушкой, и настоящий запуск панели её
    /// **не подключил**: попытка жила в памяти, в файл не попадала, и отказ **не переживал
    /// перезапуск** — то есть дефект владельца («следующий запуск снова стучится») оставался
    /// в живом пути, хотя чистые решения и были проверены. Проверка держит три половины:
    /// запись ложится в файл · читается оттуда НОВЫМ хранилищем · удачная попытка перекрывает
    /// прежнюю неудачу (иначе отступ держался бы ни на чём).
    /// </summary>
    [Fact]
    public void Попытка_ложится_в_файл_и_переживает_перечитывание()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var settings = new SettingsController(
                new SettingsStore(Path.Combine(dir, "settings.json")),
                DshPanel.Isolation.AppPaths.Under(dir),
                allowed: true,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,
                applyTheme: _ => { },
                log: _ => { });

            Assert.True(settings.SetUpdateAttempt(new UpdateAttempt(Now, Ok: false, RateLimited: true, "лимит")));

            // Читаем НОВЫМ хранилищем — как это сделает следующий запуск панели.
            var reloaded = new SettingsStore(Path.Combine(dir, "settings.json")).Load().Settings;
            var read = UpdateAttempt.Parse(
                reloaded.UpdateAttemptAt, reloaded.UpdateAttemptKind, reloaded.UpdateAttemptError);

            Assert.True(read.Failed);
            Assert.True(read.RateLimited);
            Assert.Equal(Now, read.At);
            Assert.Equal("лимит", read.Error);

            // Хранится ПРИМЕТА, а не переведённая фраза: по фразе «лимит это или нет» не решают.
            Assert.Equal(UpdateAttempt.KindLimited, reloaded.UpdateAttemptKind);
            Assert.True(UpdateDecisions.Waiting(Now.AddMinutes(30), read));

            // Удачная попытка перекрывает прежнюю неудачу — иначе отступ ни на чём не держался бы.
            Assert.True(settings.SetUpdateAttempt(new UpdateAttempt(Now.AddHours(1), Ok: true, RateLimited: false, string.Empty)));

            var afterOk = UpdateAttempt.Parse(
                settings.Settings.UpdateAttemptAt, settings.Settings.UpdateAttemptKind, settings.Settings.UpdateAttemptError);

            Assert.False(afterOk.Failed);
            Assert.Equal(UpdateAttempt.KindOk, afterOk.Kind);
            Assert.False(UpdateDecisions.Waiting(Now.AddHours(1), afterOk));

            // «Попытки не помним» пишется пустотой, а не выдуманной датой.
            Assert.True(settings.SetUpdateAttempt(UpdateAttempt.None));

            var forgotten = UpdateAttempt.Parse(
                settings.Settings.UpdateAttemptAt, settings.Settings.UpdateAttemptKind, settings.Settings.UpdateAttemptError);

            Assert.False(forgotten.Any);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// НАСТОЯЩИЙ ЗАПУСК ПОДКЛЮЧАЕТ ДВЕРЬ ПОПЫТКИ. Обязательный довод конструктора не даёт её
    /// **забыть**, но не мешает передать пустышку — а пустышка и была дефектом (попытка не
    /// переживала перезапуск). Поэтому проверка читает НАСТОЯЩИЙ `App` и требует, чтобы дверь
    /// называлась и вела в хранилище настроек: «подключено» — это вызов, а не соседняя строка.
    /// </summary>
    [Fact]
    public void Настоящий_запуск_подключает_дверь_попытки()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        if (string.IsNullOrEmpty(root))
            Assert.Fail($"не нашёл корень репозитория: вверх от «{AppContext.BaseDirectory}» нет файла DshPanel.slnx");

        var file = Path.Combine(root, "src", "DshPanel", "App.axaml.cs");

        if (!File.Exists(file))
            Assert.Fail($"нет файла запуска панели: «{file}»");

        var text = UiThreadTests.WithoutComments(File.ReadAllText(file));

        Assert.Contains("rememberAttempt:", text, StringComparison.Ordinal);
        Assert.Contains(".SetUpdateAttempt(", text, StringComparison.Ordinal);
    }
}
