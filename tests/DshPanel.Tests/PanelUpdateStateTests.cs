using System;
using DshPanel.Shell;
using DshPanel.Update;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СОСТОЯНИЕ ПОДГОТОВКИ ОБНОВЛЕНИЯ — ОБЪЕКТ УРОВНЯ ПАНЕЛИ (DEF-058/059, 2.4.2).
///
/// **От какого случая.** Владелец 24.10.2026 на 2.4.1: запустил подготовку, закрыл окно настроек,
/// открыл снова — прогресса нет, кнопка «Скачать и подготовить» снова доступна. Второе нажатие
/// запускало ВТОРУЮ подготовку в те же файлы: она удаляла куски первой, и владелец прочитал
/// «FileNotFoundException: Could not find file …DshPanel-2.4.1.zip.part-0» — тип исключения и
/// свой путь в строке окна.
///
/// Здесь проверяется само правило, без окон и без сети: **единственная дверь старта** и то, что
/// конец попытки оставляет только удачную сборку. Два окна на одном состоянии проверяются живым
/// путём в <c>SettingsUpdateSectionTests</c> — там же, где их видит человек.
///
/// **Мутации, которые обязаны упасть здесь:** «второй <c>TryBegin</c> разрешён» (M3: снять
/// сверку занятости) · «отказ второго старта без слов» (не заполнять <c>Status</c>) · «начало
/// не забывает прошлую готовность и ход» · «<c>Finish</c> кладёт в готовое неудачный ответ».
/// </summary>
public class PanelUpdateStateTests
{
    /// <summary>Готовая подготовка — тот же ответ, что отдаёт настоящее ядро.</summary>
    private static UpdatePreparation Prepared() => new(
        true,
        UpdateRefusal.None,
        string.Empty,
        "2.1.0",
        @"C:\Users\person\AppData\Local\DshPanel2\state\update\2.1.0",
        @"C:\Users\person\AppData\Local\DshPanel2\state\update\backup-2.0.0",
        @"C:\Users\person\AppData\Local\Programs\DSH Panel",
        @"C:\Users\person\AppData\Local\DshPanel2\state\update\update.cmd",
        @"C:\Users\person\AppData\Local\DshPanel2\state\update\update.log",
        "\"\"update.cmd\" \"42\" \"C:\\Panel\" \"C:\\Staged\" \"C:\\Backup\" \"C:\\Log\" \"Mutex\" \"\"\"");

    /// <summary>
    /// ВТОРОЙ СТАРТ НЕ ОТКРЫВАЕТСЯ И НАЗЫВАЕТ ПРИЧИНУ СЛОВАМИ. Это главное правило DEF-058/059:
    /// дверь одна на процесс, и она не спрашивает, из какого окна её позвали.
    /// </summary>
    [Fact]
    public void Второй_старт_не_открывается_и_называет_причину()
    {
        var state = new PanelUpdateState();
        var changes = 0;

        state.Changed += () => changes++;

        Assert.False(state.Busy, "новое состояние сразу занято");
        Assert.Equal(string.Empty, state.Status);

        Assert.True(state.TryBegin(), "первый старт не открылся");
        Assert.True(state.Busy);
        Assert.Equal(string.Empty, state.Status);

        Assert.False(state.TryBegin(), "второй старт открылся поверх идущего — это гонка двух загрузок");

        // ПРИЧИНА — СЛОВА ИЗ СЛОВАРЯ, а не исключение и не путь: человек читает окно.
        Assert.Equal(PanelStrings.UpdatePreparationBusy, state.Status);
        Assert.DoesNotContain("C:\\Users", state.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", state.Status, StringComparison.Ordinal);

        // Оба вызова сказали подписчикам: окно перерисуется и покажет причину.
        Assert.Equal(2, changes);
    }

    /// <summary>
    /// НОВАЯ ПОПЫТКА НАЧИНАЕТСЯ С ПУСТОГО МЕСТА: прошлая готовность и прошлый ход забыты. Иначе
    /// после отказа на экране до первого сообщения движка висели бы числа прошлой попытки и
    /// человек читал бы их как текущие.
    /// </summary>
    [Fact]
    public void Начало_забывает_прошлую_готовность_и_ход()
    {
        var state = new PanelUpdateState();

        state.TryBegin();
        state.Report(new UpdateProgress(UpdateStage.Archive, "DshPanel.zip", 50, 100));
        state.Finish(Prepared(), string.Empty);

        Assert.NotNull(state.Prepared);
        Assert.False(state.Busy);

        Assert.True(state.TryBegin());

        Assert.Null(state.Prepared);
        Assert.Null(state.Progress);
        Assert.Equal(string.Empty, state.Status);
    }

    /// <summary>
    /// КОНЕЦ ПОПЫТКИ — ОДНА ДВЕРЬ НА УСПЕХ И ОТКАЗ: занятость снимается, ход забывается всегда,
    /// а в готовое попадает ТОЛЬКО удачный ответ. Отказ, положенный в <see cref="PanelUpdateState.Prepared"/>,
    /// оставил бы открытой дверь замены файлов.
    /// </summary>
    [Fact]
    public void Конец_оставляет_готовой_только_удачную_сборку()
    {
        var state = new PanelUpdateState();

        // Успех.
        state.TryBegin();
        state.Report(new UpdateProgress(UpdateStage.Unpack, "архив", 0, -1));
        state.Finish(Prepared(), string.Empty);

        Assert.False(state.Busy);
        Assert.Null(state.Progress);
        Assert.NotNull(state.Prepared);
        Assert.Equal(string.Empty, state.Status);

        // Отказ.
        state.TryBegin();
        state.Report(new UpdateProgress(UpdateStage.Archive, "DshPanel.zip", 10, 100));
        state.Finish(UpdatePreparation.Refuse(UpdateRefusal.ArchiveMissing, "служебное значение"), "слова отказа");

        Assert.False(state.Busy);
        Assert.Null(state.Progress);
        Assert.Null(state.Prepared);
        Assert.Equal("слова отказа", state.Status);
    }

    /// <summary>
    /// ХОД ХРАНИТСЯ СНИМКОМ: закрыл окно и открыл снова — числа на месте. Проверяется здесь,
    /// потому что «видно, что делается» после переоткрытия держится ровно этим.
    /// </summary>
    [Fact]
    public void Ход_остаётся_в_состоянии_а_не_в_окне()
    {
        var state = new PanelUpdateState();

        state.TryBegin();
        state.Report(new UpdateProgress(UpdateStage.Archive, "DshPanel.zip", 25, 100));

        Assert.Equal(25, state.Progress!.Done);
        Assert.Equal(100, state.Progress.Total);
        Assert.Equal(25, state.Progress.Percent);
    }

    /// <summary>
    /// СТРОКА, КОТОРАЯ К ПОДГОТОВКЕ НЕ ОТНОСИТСЯ («пропустить версию», отказ открыть страницу),
    /// ставится своей дверью и НЕ трогает ни занятость, ни готовую сборку. Через
    /// <see cref="PanelUpdateState.Finish"/> она снесла бы готовую сборку, которой не касается.
    /// </summary>
    [Fact]
    public void Строка_состояния_не_трогает_подготовку()
    {
        var state = new PanelUpdateState();

        state.TryBegin();
        state.Finish(Prepared(), string.Empty);
        state.ReportStatus("пропуск запомнен");

        Assert.Equal("пропуск запомнен", state.Status);
        Assert.NotNull(state.Prepared);
        Assert.False(state.Busy);
    }

}
