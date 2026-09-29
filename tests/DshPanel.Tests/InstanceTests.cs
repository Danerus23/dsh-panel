using System;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки единого экземпляра: вторая панель не заводится, а стучится в первую.
///
/// Проверки идут на НАСТОЯЩЕМ замке и НАСТОЯЩЕМ окне-приёмнике — подделывать здесь нечего:
/// весь смысл механизма в том, что он работает через систему. Имя замка у каждой проверки своё,
/// иначе проверки мешали бы друг другу; имя класса окна-приёмника ВЫВОДИТСЯ из имени замка
/// (<see cref="InstanceSignal.ClassNameFor"/>), поэтому «своё имя замка» означает и «своё окно».
///
/// ⚠️ Общее имя класса (до 26.09.2026) делало третью проверку зависимой от ЧУЖОГО приёмника:
/// с работающей панелью она краснела оттого, что человек просто пользуется продуктом, и вдобавок
/// могла постучаться в чужое окно настоящей просьбой «покажи окно». Чей приёмник откликнулся
/// в тот раз — не установлено; журнал панели владельца просьбы не видел.
/// </summary>
public class InstanceTests
{
    private static string UniqueName() => $@"Local\DshPanel2.Test.{Guid.NewGuid():N}";

    [Fact]
    public void Первый_ставит_замок_а_второй_им_не_становится()
    {
        var name = UniqueName();

        using var first = new InstanceSignal(name);
        Assert.True(first.TryAcquire(), "первый экземпляр обязан стать первым");
        Assert.True(first.IsFirst);

        using var second = new InstanceSignal(name);
        Assert.False(second.TryAcquire(), "второй экземпляр стал первым — замок не держит");
        Assert.False(second.IsFirst);
    }

    [Fact]
    public void Просьба_показать_окно_доходит_до_первого_экземпляра()
    {
        var name = UniqueName();

        using var first = new InstanceSignal(name);
        Assert.True(first.TryAcquire());

        var requests = 0;
        first.ShowPanelRequested += () => requests++;

        using var second = new InstanceSignal(name);
        Assert.False(second.TryAcquire());

        Assert.True(second.RequestShowPanel(), "просьба не доставлена, хотя первая панель слушает");

        // Своего цикла сообщений у проверки нет — разбираем очередь вручную.
        Assert.True(first.PumpUntilShowRequested(TimeSpan.FromSeconds(3)), "просьба не дошла до первой панели");
        Assert.Equal(1, requests);
        Assert.Equal(1, first.ShowRequests);
    }

    [Fact]
    public void Без_работающей_панели_просьба_честно_отказывает()
    {
        // Никто не слушает: окна-приёмника нет. Молчаливый «успех» здесь означал бы,
        // что человек запускает ярлык второй раз и не получает ничего — и не знает почему.
        //
        // «Никто не слушает» обеспечено именем замка, а не надеждой: класс окна-приёмника
        // выводится из него, поэтому окно живой панели владельца этой проверке не видно.
        using var lonely = new InstanceSignal(UniqueName());
        Assert.False(lonely.RequestShowPanel());
    }

    /// <summary>
    /// ИМЯ КЛАССА ОКНА-ПРИЁМНИКА ВЫВОДИТСЯ ИЗ ИМЕНИ ЗАМКА — проверяется без окна, без замка
    /// и без рабочего стола. Требований два, и оба обязательны:
    ///
    /// * с РАЗНЫМИ именами замков классы разные — иначе проба с чужим замком находит окно живой
    ///   панели (так и вышло 26.09.2026: проверка краснела оттого, что владелец просто работает);
    /// * с ОДНИМ именем замка класс один и тот же в разных процессах — иначе второй запуск ярлыка
    ///   не найдёт первую панель.
    /// </summary>
    [Fact]
    public void Имя_класса_окна_приёмника_выводится_из_имени_замка()
    {
        var left = InstanceSignal.ClassNameFor(@"Local\DshPanel2.Test.Первая");
        var right = InstanceSignal.ClassNameFor(@"Local\DshPanel2.Test.Вторая");

        Assert.NotEqual(left, right);
        Assert.NotEqual(InstanceSignal.ClassNameFor(InstanceSignal.DefaultMutexName), left);

        // Детерминированность — она и есть причина, по которой здесь хеш имени, а не счётчик.
        Assert.Equal(left, InstanceSignal.ClassNameFor(@"Local\DshPanel2.Test.Первая"));

        // И это по-прежнему имя класса, а не что попало.
        Assert.StartsWith(InstanceSignal.SignalClassPrefix, left, StringComparison.Ordinal);
    }

    /// <summary>
    /// ЧУЖОЙ ЭКЗЕМПЛЯР ПРОСЬБУ НЕ ПОЛУЧАЕТ. Рядом живёт панель со СВОИМ замком и своим
    /// окном-приёмником — и просьба с ДРУГИМ замком обязана честно отказать, а не попасть
    /// в чужое окно.
    ///
    /// Оба замка здесь свои, поэтому проверка не зависит ни от машины, ни от того, запущена ли
    /// у человека настоящая панель. Положительный контроль рядом: со СВОИМ именем замка та же
    /// просьба доходит до первой панели — иначе «отказала» ничего не доказывало бы.
    /// </summary>
    [Fact]
    public void Чужой_экземпляр_просьбу_не_получает()
    {
        var name = UniqueName();

        using var busy = new InstanceSignal(name);
        Assert.True(busy.TryAcquire(), "первый экземпляр обязан стать первым");

        using var stranger = new InstanceSignal(UniqueName());
        Assert.False(stranger.RequestShowPanel(), "просьба ушла в чужое окно-приёмник");
        Assert.Equal(0, busy.ShowRequests);

        var requests = 0;
        busy.ShowPanelRequested += () => requests++;

        using var same = new InstanceSignal(name);
        Assert.False(same.TryAcquire());
        Assert.True(same.RequestShowPanel(), "своя просьба не доставлена");
        Assert.True(busy.PumpUntilShowRequested(TimeSpan.FromSeconds(3)), "просьба не дошла до панели");
        Assert.Equal(1, requests);
    }
}
