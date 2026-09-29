using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DshPanel.Localization;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Окно «О программе» проверяется БЕЗ экрана и по ПИКСЕЛЯМ — как главное окно и окно копий.
/// Смысл не в том, чтобы «тест прошёл», а в том, чтобы поймать пустой или схлопнувшийся кадр:
/// критерий этапа — «видно на экране», и он обязан доказываться машинно.
///
/// Здесь же проверяются ДВА решения окна, вынесенные в чистые функции, потому что условие,
/// спрятанное внутри разметки, проверить нечем:
///
///   * какие ссылки показывать (<see cref="ProductLinks.ShouldShow"/>) — пустой адрес это
///     «возможности нет», и строки в окне быть не должно;
///   * показывать ли строку о машинном переводе (<see cref="AboutWindow.ShowsTranslationNote"/>) —
///     есть для английского и китайского, нет для русского.
///
/// ⚠️ **Язык процесса здесь не переключается.** Тесты xunit гоняются параллельно, а язык —
/// глобальное состояние: подмена сломала бы соседние проверки. Три языка сравниваются через
/// <see cref="Loc.TIn"/>, который состояния не трогает, а на экране проверяются строки ТОГО
/// языка, который выставлен один раз для всего прогона (`TestLocale`).
/// </summary>
public class AboutWindowRenderTests
{
    // ------------------------------------------------------------------ кадр

    private static (int Width, int Height, long Opaque, int Colors) Render()
    {
        var window = new AboutWindow();

        try
        {
            window.Show();

            // Дать вёрстке и очереди диспетчера отработать до конца.
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var w = (int)Math.Ceiling(window.ClientSize.Width);
            var h = (int)Math.Ceiling(window.ClientSize.Height);
            Assert.True(w > 0 && h > 0, $"пустой размер окна {w}x{h}");

            var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            rtb.Render(window);

            var stride = w * 4;
            var buf = new byte[stride * h];
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { rtb.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buf.Length, stride); }
            finally { handle.Free(); }

            long opaque = 0;
            var colors = new HashSet<int>();
            for (var i = 0; i + 3 < buf.Length; i += 4)
            {
                if (buf[i + 3] != 0) opaque++;
                colors.Add((buf[i] << 16) | (buf[i + 1] << 8) | buf[i + 2]);
            }

            return (w, h, opaque, colors.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Окно_о_программе_рисуется_непустым_кадром()
    {
        var (w, h, opaque, colors) = Render();

        Assert.True(opaque > 0, "в кадре нет ни одного непрозрачного пикселя — окно не нарисовалось");
        Assert.True(colors > 1, $"в кадре всего {colors} цвет(ов) — это заливка, а не интерфейс");

        var total = (long)w * h;
        Assert.True(opaque >= total / 2, $"непрозрачных всего {opaque} из {total} — похоже, вёрстка схлопнулась");
    }

    [AvaloniaFact]
    public void Кадр_окна_о_программе_воспроизводим_между_запусками()
    {
        var first = Render();
        var second = Render();

        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        Assert.Equal(first.Opaque, second.Opaque);
    }

    // ------------------------------------------------------------------ тексты

    /// <summary>
    /// Видимые тексты приходят из строк текущего языка, и НИ ОДИН из них не является именем
    /// ключа. Второе — не придирка: пропавший перевод человек видит именно именем ключа
    /// (`AboutPurpose` в окне вместо объяснения), и в v1 такой случай доехал до человека
    /// (см. `docs\LOCALIZATION.md` §4).
    /// </summary>
    [AvaloniaFact]
    public void Видимые_тексты_приходят_из_строк_а_не_именами_ключей()
    {
        var window = new AboutWindow();

        try
        {
            Assert.Equal(PanelStrings.AboutTitle, window.Title);
            Assert.Equal(PanelStrings.AppName, window.ProductLine);
            Assert.Equal(PanelStrings.AboutPurpose, window.PurposeLine);
            Assert.Equal(PanelStrings.AboutDonateLink, window.DonateLine);
            Assert.Equal(PanelStrings.AboutRepoLink, window.RepoLine);
            Assert.Equal(PanelStrings.AboutCloseButton, window.CloseLine);
            Assert.Equal(PanelStrings.AboutLicense, window.LicenseLine);
            Assert.Equal(PanelStrings.AboutCopyright, window.CopyrightLine);
            Assert.Equal(PanelStrings.AboutTranslationNote, window.TranslationNoteLine);
            Assert.Contains(AboutWindow.PanelVersion, window.VersionLine, StringComparison.Ordinal);

            // Имена ключей — список членов PanelStrings. Ни один из них не имеет права оказаться
            // на экране: это признак пропавшей строки, а не «так и задумано».
            var keys = typeof(PanelStrings)
                .GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var text in new[]
                     {
                         window.Title, window.ProductLine, window.VersionLine, window.PurposeLine,
                         window.RepoLine, window.DonateLine, window.CloseLine, window.TranslationNoteLine,
                         window.LicenseLine, window.CopyrightLine,
                     })
            {
                Assert.False(string.IsNullOrWhiteSpace(text), "на экране пустая строка вместо текста");
                Assert.DoesNotContain(text, keys);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Строка версии — числовая часть: хеш коммита, который дописывает .NET, человеку не нужен.</summary>
    [AvaloniaFact]
    public void Строка_версии_несёт_число_без_хеша_коммита()
    {
        Assert.Equal("2.0.0", AboutWindow.NumericVersion("2.0.0+9f3c1ab1"));
        Assert.Equal("2.0.0", AboutWindow.NumericVersion("  2.0.0  "));
        Assert.Equal(string.Empty, AboutWindow.NumericVersion(null));
        Assert.Equal(string.Empty, AboutWindow.NumericVersion(""));

        var window = new AboutWindow();

        try
        {
            var expected = string.Format(
                CultureInfo.CurrentCulture, PanelStrings.AboutVersionFormat, AboutWindow.PanelVersion);

            Assert.Equal(expected, window.VersionLine);
            Assert.NotEqual(string.Empty, AboutWindow.PanelVersion);
            Assert.DoesNotContain("+", window.VersionLine, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    // ------------------------------------------------------------------ три языка

    /// <summary>
    /// Содержимое окна РАЗНОЕ на трёх языках: тексты берутся из словарей, а не из одного
    /// литерала. Сравниваем через <see cref="Loc.TIn"/> — он не трогает выбранный язык,
    /// а значит не мешает параллельным проверкам.
    /// </summary>
    [AvaloniaFact]
    public void Содержимое_окна_отличается_на_трёх_языках()
    {
        foreach (var key in new[]
                 {
                     nameof(PanelStrings.AboutTitle),
                     nameof(PanelStrings.AboutPurpose),
                     nameof(PanelStrings.AboutPurposeHeading),
                     nameof(PanelStrings.AboutDonateLink),
                     nameof(PanelStrings.AboutCloseButton),
                 })
        {
            var ru = Loc.TIn("ru", key);
            var en = Loc.TIn("en", key);
            var zh = Loc.TIn("zh", key);

            Assert.NotEqual(ru, en);
            Assert.NotEqual(ru, zh);
            Assert.NotEqual(en, zh);
        }

        // Название продукта переводится не везде: по-английски оно совпадает с русским,
        // а по-китайски — нет. Сравниваем то, что и правда обязано отличаться.
        Assert.NotEqual(Loc.TIn("ru", nameof(PanelStrings.AppName)), Loc.TIn("zh", nameof(PanelStrings.AppName)));
        Assert.NotEqual(Loc.TIn("en", nameof(PanelStrings.AppName)), Loc.TIn("zh", nameof(PanelStrings.AppName)));

        // А на экране — строки ТОГО языка, на котором панель говорит сейчас: окно не подставляет
        // чужой язык и не запоминает язык момента сборки.
        var window = new AboutWindow();

        try
        {
            Assert.Equal(Loc.TIn(Loc.Language, nameof(PanelStrings.AboutPurpose)), window.PurposeLine);
            Assert.Equal(Loc.TIn(Loc.Language, nameof(PanelStrings.AboutCloseButton)), window.CloseLine);
            Assert.Equal(Loc.TIn(Loc.Language, nameof(PanelStrings.AppName)), window.ProductLine);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Строка о машинном переводе есть для английского и китайского и НЕТ для русского: русскому
    /// языку раскрывать нечего (решение владельца, `docs\LOCALIZATION.md` §6).
    /// </summary>
    [AvaloniaFact]
    public void Строка_о_машинном_переводе_есть_для_en_и_zh_и_нет_для_ru()
    {
        Assert.False(AboutWindow.ShowsTranslationNote("ru"));
        Assert.False(AboutWindow.ShowsTranslationNote("RU"));

        Assert.True(AboutWindow.ShowsTranslationNote("en"));
        Assert.True(AboutWindow.ShowsTranslationNote("zh"));

        // Незнакомый язык — показываем: не раскрыть машинный перевод хуже, чем раскрыть зря.
        Assert.True(AboutWindow.ShowsTranslationNote(null));

        var window = new AboutWindow();

        try
        {
            Assert.Equal(
                AboutWindow.ShowsTranslationNote(Loc.Language),
                window.TranslationNoteVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ------------------------------------------------------------------ ссылки

    /// <summary>
    /// СТРОКА ПРО ИСХОДНИКИ ПОКАЗЫВАЕТСЯ, КОГДА АДРЕС ЕСТЬ, И МОЛЧИТ, КОГДА ЕГО НЕТ.
    ///
    /// ⚠️ **Посылка прежней проверки перевернулась 30.09.2026.** Адрес репозитория
    /// (<see cref="ProductLinks.RepositoryUrl"/>) был пуст решением владельца 26.09.2026
    /// («репозиторий 2.0 закрыт, вести человека некуда») и **заполнен при выпуске 30.09.2026** —
    /// панель публикуется в публичный <see cref="ProductLinks.ReleasesRepository"/>. Требовать
    /// пустоты проверка больше не может: она проверяла бы решение, которое отменено.
    ///
    /// Поэтому проверяются ОБЕ стороны, и ни одна не зависит от того, заполнен адрес сегодня:
    /// * обещание «пустой адрес — строки нет» живёт в предикате
    ///   (<see cref="ProductLinks.ShouldShow"/>): пустое и пробельное — ложь, настоящий адрес —
    ///   истина. Так проверка переживёт и следующий выпуск с другим адресом;
    /// * с настоящим адресом строка в окне ПОКАЗЫВАЕТСЯ — это то, что увидит человек.
    ///
    /// ⚠️ Чего эта проверка не покрывает: что щелчок открывает ИМЕННО этот адрес — браузер
    /// в прогоне не открывается (проверка не трогает рабочий стол владельца). Адрес уезжает
    /// в обработчик из одного места — <c>ProductLinks.RepositoryUrl</c>, и это видно кодом.
    /// Подпись у кнопки есть всегда: «строки нет в окне» и «строки нет в словаре» — разные вещи.
    /// </summary>
    [AvaloniaFact]
    public void Строка_про_исходники_показывается_когда_адрес_есть_и_молчит_когда_пуст()
    {
        // 1. Обещание предиката — на подставных значениях, без оглядки на сегодняшний адрес.
        Assert.False(ProductLinks.ShouldShow(null));
        Assert.False(ProductLinks.ShouldShow(string.Empty));
        Assert.False(ProductLinks.ShouldShow("   "));
        Assert.True(ProductLinks.ShouldShow(ProductLinks.RepositoryUrl));

        var window = new AboutWindow();

        try
        {
            // 2. В окне — настоящий адрес выпуска: строка видна, подпись из словаря и адрес тот же,
            // что у страницы выпусков (один репозиторий на всё — решение владельца).
            Assert.True(window.RepoLinkVisible, "адрес репозитория есть, а строки про исходники в окне нет");
            Assert.Equal(PanelStrings.AboutRepoLink, window.RepoLine);
            Assert.Contains(ProductLinks.ReleasesRepository, ProductLinks.RepositoryUrl, StringComparison.Ordinal);

            // Донаты — так же: адрес есть, значит и строка есть.
            Assert.True(window.DonateLinkVisible);
            Assert.Equal(PanelStrings.AboutDonateLink, window.DonateLine);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Пустой адрес — это «возможности нет», и <see cref="ProductLinks.Open"/> обязан сказать
    /// об этом СЛОВАМИ (строка <see cref="PanelStrings.AboutNoLink"/>), а не промолчать
    /// и не отдать имя ключа.
    /// </summary>
    [Fact]
    public void Пустой_адрес_не_показывается_а_Open_объясняет_почему()
    {
        Assert.False(ProductLinks.ShouldShow(null));
        Assert.False(ProductLinks.ShouldShow(string.Empty));
        Assert.False(ProductLinks.ShouldShow("   "));
        Assert.True(ProductLinks.ShouldShow(ProductLinks.DonateUrl));

        Assert.Equal(PanelStrings.AboutNoLink, ProductLinks.Open(string.Empty));
        Assert.Equal(PanelStrings.AboutNoLink, ProductLinks.Open(null));
        Assert.Equal(PanelStrings.AboutNoLink, ProductLinks.Open("  "));

        // Объяснение — настоящая строка, а не имя ключа: ровно этот случай в v1 показывал
        // человеку `about.noLink` вместо причины.
        Assert.NotEqual(nameof(PanelStrings.AboutNoLink), PanelStrings.AboutNoLink);
        Assert.False(string.IsNullOrWhiteSpace(PanelStrings.AboutNoLink));
    }

    /// <summary>
    /// Переменная окружения подменяет адрес донатов — тем же приёмом, что в v1: так проверяют,
    /// что ссылка доехала до окна, не трогая продукт.
    ///
    /// ⚠️ Переменная окружения — на ВЕСЬ процесс, а тесты идут параллельно. Поэтому прежнее
    /// значение не просто запоминается, а возвращается на место в <c>finally</c>: проверка,
    /// оставившая след в окружении, ломала бы соседние.
    /// </summary>
    [Fact]
    public void Подмена_адреса_донатов_переменной_окружения_возвращает_прежнее_значение()
    {
        lock (Gate)
        {
            var before = Environment.GetEnvironmentVariable(ProductLinks.DonateVariable);

            try
            {
                const string custom = "https://example.invalid/donate-check";

                Environment.SetEnvironmentVariable(ProductLinks.DonateVariable, custom);

                Assert.Equal(custom, ProductLinks.Donate);
                Assert.True(ProductLinks.ShouldShow(ProductLinks.Donate));
            }
            finally
            {
                Environment.SetEnvironmentVariable(ProductLinks.DonateVariable, before);
            }

            Assert.Equal(before, Environment.GetEnvironmentVariable(ProductLinks.DonateVariable));

            // А без подмены адрес — тот самый, что подтвердил владелец для 2.0.
            if (string.IsNullOrWhiteSpace(before)) Assert.Equal(ProductLinks.DonateUrl, ProductLinks.Donate);
        }
    }

    /// <summary>Общая щеколда вокруг переменной окружения: соседние проверки этого класса.</summary>
    private static readonly object Gate = new();

    /// <summary>
    /// Ссылку открыть не вышло — причина показывается СТРОКОЙ в самом окне, а не отдельным
    /// модальным окном поверх него. Браузер при этом не открывается: подставлен ответ,
    /// который дал бы <see cref="ProductLinks.Open"/> при отказе.
    /// </summary>
    [AvaloniaFact]
    public void Ошибка_открытия_ссылки_показывается_строкой_в_окне_а_не_окном_поверх()
    {
        var window = new AboutWindow();

        try
        {
            Assert.Equal(string.Empty, window.Status);
            Assert.Empty(window.OwnedWindows);

            const string reason = "нет доступа к браузеру";
            window.OpenLinkForTests = _ => reason;

            window.ClickDonate();

            Assert.Equal(
                string.Format(CultureInfo.CurrentCulture, PanelStrings.AboutLinkFailedFormat, reason),
                window.Status);

            // Модального окна поверх не появилось: причина сказана здесь же.
            Assert.Empty(window.OwnedWindows);

            // Успех очищает строку: старая ошибка не должна висеть над живой ссылкой.
            window.OpenLinkForTests = _ => string.Empty;
            window.ClickDonate();

            Assert.Equal(string.Empty, window.Status);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Второго окна «О программе» не бывает: повторная просьба (пункт меню, кнопка в главном окне)
    /// выводит на передний план уже открытое. В нём одни и те же ссылки, и два окна означали бы
    /// для человека два разных «настоящих» ответа на вопрос «что это».
    /// </summary>
    [AvaloniaFact]
    public void Второе_окно_о_программе_не_заводится_а_первое_поднимается()
    {
        var slot = new SingleWindowSlot();
        var created = 0;

        Window Create()
        {
            created++;
            return new AboutWindow();
        }

        try
        {
            Assert.True(slot.Open(Create));
            Assert.Equal(1, created);
            Assert.True(slot.IsOpen);

            Assert.False(slot.Open(Create));
            Assert.Equal(1, created);

            // Человек закрыл окно — следующая просьба обязана построить новое, а не «вывести»
            // на передний план то, которого уже нет.
            slot.Close();
            Assert.False(slot.IsOpen);

            Assert.True(slot.Open(Create));
            Assert.Equal(2, created);
        }
        finally
        {
            slot.Close();
        }
    }

    /// <summary>
    /// Кнопка закрытия действительно закрывает окно. Проверка не формальность: у окна нет
    /// владельца и нет «Скрыть» — если бы кнопка только подписывалась, человек остался бы
    /// с окном, которое нечем убрать.
    /// </summary>
    [AvaloniaFact]
    public void Кнопка_закрытия_закрывает_окно()
    {
        var window = new AboutWindow();
        window.Show();

        Assert.True(window.IsVisible);

        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.ClickClose();

        Assert.True(closed, "щелчок по «Закрыть» не закрыл окно");
        Assert.False(window.IsVisible);
    }
}
