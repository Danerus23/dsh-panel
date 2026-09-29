using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DshPanel.Update;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЗАМЕТКИ К ВЫПУСКУ ЧИТАЮТСЯ, А НЕ ИДУТ ПРОСТЫНЁЙ.
///
/// Владелец жаловался на «сплошной неструктурированный текст» ДВАЖДЫ (`docs\DESIGN.md`, пункты
/// 32–33: про план наката и раздел «Сервер»). Заметки к выпуску приходят с GitHub одной строкой
/// с разметкой, и показанные одним <see cref="TextBlock"/> они читаются ровно так же.
///
/// Поэтому здесь проверяются обе половины:
///
/// 1. **разбор** (<see cref="UpdateNotes.Parse"/>) — чистые функции от текста, перебором случаев:
///    заголовок, подзаголовок, пункт списка, нумерованный пункт, цитата, ограждение, абзац,
///    пустая строка;
/// 2. **показ** (<see cref="UpdateNotesView.Fill"/>) — НАСТОЯЩИЙ построитель: строки становятся
///    отдельными органами, а не одним куском текста.
///
/// ⚠️ Проверка «всё свалилось в одну строку» ловится первой же: у разобранных заметок строк
/// заметно больше одной.
/// </summary>
public class UpdateNotesTests
{
    /// <summary>Заметки такие, какими их публикует выпуск: заголовок, список, абзацы.</summary>
    private const string Body =
        "### Что нового\n" +
        "\n" +
        "- Панель показывает ход обновления\n" +
        "- Замена файлов запускается отдельной кнопкой\n" +
        "\n" +
        "Если запуск не удался, панель скажет об этом словами.\n" +
        "\n" +
        "> Файлы берутся из публичного репозитория.\n" +
        "\n" +
        "1. Скачать архив\n" +
        "2. Сверить сумму\n";

    /// <summary>
    /// ГЛАВНОЕ: заметки разбираются НА СТРОКИ, и видов у них несколько. Одна строка вместо семи
    /// означает, что человек снова увидит простыню.
    /// </summary>
    [Fact]
    public void Заметки_разбираются_на_строки_а_не_в_одну_простыню()
    {
        var lines = UpdateNotes.Parse(Body);

        Assert.True(lines.Count > 5, $"строк в заметках: {lines.Count} — похоже, всё свалилось в одну");
        Assert.Contains(lines, line => line.Kind == UpdateNoteKind.SubHeading);
        Assert.Contains(lines, line => line.Kind == UpdateNoteKind.Bullet);
        Assert.Contains(lines, line => line.Kind == UpdateNoteKind.Numbered);
        Assert.Contains(lines, line => line.Kind == UpdateNoteKind.Quote);
        Assert.Contains(lines, line => line.Kind == UpdateNoteKind.Text);
        Assert.Contains(lines, line => line.Kind == UpdateNoteKind.Blank);
    }

    /// <summary>Заголовки различаются по уровню: одна-две решётки — заголовок, три и глубже — подзаголовок.</summary>
    [Fact]
    public void Заголовок_и_подзаголовок_различаются()
    {
        var lines = UpdateNotes.Parse("# Выпуск\n\n## Раздел\n\n### Мелочь\n\n#### Глубже\n");

        Assert.Equal(
            new[]
            {
                UpdateNoteKind.Heading,
                UpdateNoteKind.Blank,
                UpdateNoteKind.Heading,
                UpdateNoteKind.Blank,
                UpdateNoteKind.SubHeading,
                UpdateNoteKind.Blank,
                UpdateNoteKind.SubHeading,
            },
            lines.Select(line => line.Kind));

        // Решётки в текст НЕ попадают: их рисует окно размером и жирностью.
        Assert.Equal("Выпуск", lines[0].Text);
        Assert.Equal("Раздел", lines[2].Text);
        Assert.Equal("Мелочь", lines[4].Text);
    }

    /// <summary>«#1» без пробела — не заголовок: так выглядит нумерация, а не раздел.</summary>
    [Fact]
    public void Решётка_без_пробела_заголовком_не_считается()
    {
        var single = Assert.Single(UpdateNotes.Parse("#1 в списке"));

        Assert.Equal(UpdateNoteKind.Text, single.Kind);
        Assert.Equal("#1 в списке", single.Text);
    }

    /// <summary>Маркер списка снимается, а НОМЕР остаётся: он часть текста, а не разметка.</summary>
    [Fact]
    public void Пункт_списка_теряет_маркер_а_номер_остаётся()
    {
        var lines = UpdateNotes.Parse("- дефис\n* звёздочка\n+ плюс\n1. первый\n2) второй\n");

        Assert.Equal(
            new[]
            {
                UpdateNoteKind.Bullet, UpdateNoteKind.Bullet, UpdateNoteKind.Bullet,
                UpdateNoteKind.Numbered, UpdateNoteKind.Numbered,
            },
            lines.Select(line => line.Kind));

        Assert.Equal("дефис", lines[0].Text);
        Assert.Equal("звёздочка", lines[1].Text);
        Assert.Equal("плюс", lines[2].Text);
        Assert.Equal("1. первый", lines[3].Text);
        Assert.Equal("2) второй", lines[4].Text);
    }

    /// <summary>
    /// Пустая строка — это ОТСТУП между абзацами, и он нужен один: в теле выпуска пустых строк
    /// подряд бывает по три, и три отступа читались бы дырой. По краям они срезаются.
    /// </summary>
    [Fact]
    public void Пустые_строки_схлопываются_и_срезаются_по_краям()
    {
        var lines = UpdateNotes.Parse("\n\nпервый\n\n\n\nвторой\n\n\n");

        Assert.Equal(
            new[] { UpdateNoteKind.Text, UpdateNoteKind.Blank, UpdateNoteKind.Text },
            lines.Select(line => line.Kind));
    }

    /// <summary>
    /// Ограждённый блок: его знаки человеку не показываются, а содержимое остаётся строками
    /// и НЕ разбирается как заметки — там пример, а не заголовок.
    /// </summary>
    [Fact]
    public void Ограждённый_блок_не_показывает_свои_знаки_и_не_разбирается()
    {
        var lines = UpdateNotes.Parse("до\n\n```\n## это пример заголовка\n- это пример пункта\n```\n\nпосле");

        var code = lines.Where(line => line.Kind == UpdateNoteKind.Code).Select(line => line.Text).ToList();

        Assert.Equal(new[] { "## это пример заголовка", "- это пример пункта" }, code);
        Assert.DoesNotContain(lines, line => line.Text == "```");
        Assert.DoesNotContain(lines, line => line.Kind == UpdateNoteKind.Heading);
    }

    /// <summary>Знаки разметки внутри строки снимаются: человеку нужен текст, а не звёздочки.</summary>
    [Fact]
    public void Знаки_разметки_внутри_строки_снимаются()
    {
        var lines = UpdateNotes.Parse("- **Важно**: версия `v2.1.0` и __подчёркивание__");

        var single = Assert.Single(lines);

        Assert.Equal(UpdateNoteKind.Bullet, single.Kind);
        Assert.Equal("Важно: версия v2.1.0 и подчёркивание", single.Text);
    }

    /// <summary>
    /// Горизонтальная линия — разрыв, а не текст: показывать её знаками значило бы показать
    /// разметку. И она СЛИВАЕТСЯ с соседней пустой строкой — двух отступов подряд не бывает.
    /// </summary>
    [Fact]
    public void Горизонтальная_линия_становится_разрывом()
    {
        var lines = UpdateNotes.Parse("до\n\n---\n\nпосле");

        Assert.Equal(
            new[] { UpdateNoteKind.Text, UpdateNoteKind.Blank, UpdateNoteKind.Text },
            lines.Select(line => line.Kind));

        Assert.DoesNotContain(lines, line => line.Text.Contains("---", StringComparison.Ordinal));
    }

    /// <summary>Пустое тело — пустой список: выдумывать «заметок нет» текстом здесь нельзя, это дело окна.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\n")]
    [InlineData(null)]
    public void Пустые_заметки_дают_пустой_список(string? body) => Assert.Empty(UpdateNotes.Parse(body));

    /// <summary>
    /// ПОКАЗ: каждая разобранная строка становится СВОИМ органом, а пустая — воздухом между
    /// абзацами. Одним <see cref="TextBlock"/> заметки выглядели бы простынёй — ровно тем, на что
    /// жаловался владелец.
    /// </summary>
    [AvaloniaFact]
    public void Нарисованные_заметки_идут_строками_а_не_простынёй()
    {
        var panel = new StackPanel();
        var lines = UpdateNotes.Parse(Body);

        UpdateNotesView.Fill(panel, lines);

        Assert.Equal(lines.Count, UpdateNotesView.Count(panel));
        Assert.True(panel.Children.Count > 5, $"органов в заметках: {panel.Children.Count}");

        // Строки текста нарисованы по одной, и маркер пункта рисует ПОКАЗ, а не разбор.
        var texts = panel.Children.OfType<TextBlock>().Select(block => block.Text ?? string.Empty).ToList();

        Assert.Contains("• Панель показывает ход обновления", texts);
        Assert.Contains("Если запуск не удался, панель скажет об этом словами.", texts);

        // Заголовок отличается от абзаца видом: у него крупнее шрифт и он жирнее.
        var heading = panel.Children.OfType<TextBlock>().First(block => block.Text == "Что нового");
        var paragraph = panel.Children.OfType<TextBlock>().First(
            block => block.Text == "Если запуск не удался, панель скажет об этом словами.");

        Assert.True(heading.FontSize > paragraph.FontSize, "заголовок не отличается от абзаца размером");
        Assert.True(heading.FontWeight > paragraph.FontWeight, "заголовок не отличается от абзаца жирностью");

        // Пустая строка — воздух, а не текст.
        Assert.Contains(panel.Children, child => child is Border);
    }

    /// <summary>Пересборка заметок очищает панель: накопление дало бы заметки двух выпусков сразу.</summary>
    [AvaloniaFact]
    public void Пересборка_заметок_не_накапливает_строки()
    {
        var panel = new StackPanel();

        UpdateNotesView.Fill(panel, UpdateNotes.Parse(Body));
        var first = panel.Children.Count;

        UpdateNotesView.Fill(panel, UpdateNotes.Parse("- одна строка"));

        Assert.True(first > 1);
        Assert.Single(panel.Children);
    }
}
