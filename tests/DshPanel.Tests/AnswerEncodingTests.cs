using System.Globalization;
using System.Text;
using DshPanel.Platform;
using DshPanel.Restore;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// НАХОДКА 44 (заведена 30.09.2026 ночью, чинена тем же вечером): ответ консоли разбирался
/// кодировкой КУЛЬТУРЫ процесса, а не языком Windows.
///
/// **Что замерено 30.09.2026 на этой машине** (русская Windows, язык ОС ru-RU,
/// <c>GetOEMCP()</c> = 866, <c>GetACP()</c> = 1251): ответ <c>mklink</c> «соединение создано для …»
/// приходит байтами <c>e1 ae a5 a4 a8 ad a5 …</c>, и в слова он разбирается РОВНО кодовой
/// страницей системы (866). Кодовой страницей культуры — нет: 437 даёт «ß«Ññ¿¡Ñ¡¿Ñ ß«ºñá¡« ñ½∩»
/// (это и есть «кракозябры» из находки), 850 — «… ñ½´». Сами байты от культуры НЕ зависят:
/// прогон при ru-RU, en-US и de-DE дал одни и те же байты — значит решает язык СИСТЕМЫ.
///
/// ⚠️ **Почему проверка нужна, если на машине владельца дефекта не видно.** Русская Windows +
/// русская культура дают 866 = 866, и подмена проходит незамеченной. Зато прогон проверок идёт
/// с ЗАКРЕПЛЁННОЙ инвариантной культурой (<see cref="TestLocale"/>), у которой
/// <c>OEMCodePage</c> = 437, — то есть состояние дефекта в наборе ЕСТЬ, и проверки ниже проходят
/// именно через него, а не мимо.
///
/// ⚠️ **Живой путь стережёт не этот файл.** Что ссылка создаётся, а ответ системы доезжает до
/// человека (непустой, с откликом <c>mklink</c> и обоими путями), проверяет
/// <c>BackupJunctionTests.Создание_ссылки_отдаёт_ответ_системы</c>: она нарочно сверяет обещание,
/// а не редакцию ответа, и потому остаётся зелёной при любом языке Windows.
/// </summary>
public class AnswerEncodingTests
{
    /// <summary>
    /// Байты ответа, которыми проверяется выбор кодировки: не ASCII, потому что ASCII любая
    /// из этих кодовых страниц разбирает одинаково и подмена прошла бы незамеченной.
    /// Разбор 866 даёт «ДВар», 437 — «äéáα», 850 — «äéáÓ»: слова РАЗНЫЕ, и это здесь главное.
    /// </summary>
    private static readonly byte[] AnswerBytes = { 0x84, 0x82, 0xA0, 0xE0 };

    /// <summary>
    /// Культуры перебора. Одной мало: у каждой свой <c>OEMCodePage</c> (инвариантная и en-US — 437,
    /// de-DE — 850, ru-RU — 866, ja-JP — 932, zh-CN — 936), поэтому проверка остаётся строгой и на
    /// машине владельца, и на английском сборочном раннере: какая-нибудь из них да разойдётся
    /// с системной страницей. Пустое имя — инвариантная культура, та самая, что закреплена у прогона.
    /// </summary>
    private static readonly string[] Cultures = { "", "ru-RU", "en-US", "de-DE", "ja-JP", "zh-CN" };

    private static CultureInfo Culture(string name) =>
        name.Length == 0 ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(name);

    /// <summary>
    /// ЧИСТОЕ РЕШЕНИЕ: выбранная кодировка не зависит от культуры процесса — и не зависит НИКОГДА,
    /// ни при какой культуре. Функция здесь <see cref="SystemCodePage.ForAnswer"/>: Windows она
    /// не спрашивает вовсе (число приходит снаружи), поэтому и прогон не зависит от машины.
    ///
    /// Проверка падает, если выбор снова начнёт смотреть на культуру — например, если тело
    /// функции станет <c>CultureInfo.CurrentCulture.TextInfo.OEMCodePage</c>: тогда под ru-RU
    /// вернулось бы 866, под de-DE — 850, а не то число, которое спросили.
    /// </summary>
    [Fact]
    public void Чистое_решение_о_кодировке_не_смотрит_на_культуру()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            foreach (var name in Cultures)
            {
                CultureInfo.CurrentCulture = Culture(name);

                // Число, спрошенное у системы, возвращается КАК ЕСТЬ: панель не подменяет
                // показание системы своим.
                Assert.Equal(437, SystemCodePage.ForAnswer(437));
                Assert.Equal(866, SystemCodePage.ForAnswer(866));
                Assert.Equal(932, SystemCodePage.ForAnswer(932));

                // А ноль и меньше — это «система не ответила»: только тогда берётся запасной путь,
                // и назван он словами — CP866, кодовая страница русской Windows.
                Assert.Equal(SystemCodePage.RussianOem, SystemCodePage.ForAnswer(0));
                Assert.Equal(SystemCodePage.RussianOem, SystemCodePage.ForAnswer(-1));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// Кодировка ответа СПРАШИВАЕТСЯ У СИСТЕМЫ и не спрашивается у культуры процесса: проверка
    /// идёт через сам <see cref="RestoreEngine.ReadAnswer"/> — то место, где байты ответа
    /// становятся словами, — при нескольких культурах сразу.
    ///
    /// Падает ровно на возврате выбора по культуре (это и есть мутация находки): тогда под ru-RU
    /// ответ разобрался бы страницей 866, под en-US — 437, а ожидание здесь одно на все культуры —
    /// то, что сказала СИСТЕМА.
    ///
    /// Отдельно утверждается, что расходящаяся культура в переборе ЕСТЬ, и что разбор ею дал бы
    /// ДРУГОЕ слово: без этих двух сверок проверка могла бы «сойтись», ни разу не пройдя через
    /// состояние, в котором дефект возможен.
    /// </summary>
    [Fact]
    public void Кодировка_ответа_спрашивается_у_системы_а_не_у_культуры()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Ровно так ответ и читается из потока: байты, переведённые в знаки один в один.
        var raw = Encoding.Latin1.GetString(AnswerBytes);

        var system = SystemCodePage.ForAnswer(SystemCodePage.Oem());
        var expected = Encoding.GetEncoding(system).GetString(AnswerBytes);

        // Вопрос к Windows обязан РАБОТАТЬ на этой машине: ноль здесь значил бы, что P/Invoke
        // не позвался, и панель молча отвечала бы запасным путём — на машине владельца это
        // выглядело бы зелёным, а на чужой машине дало бы «кракозябры».
        Assert.True(SystemCodePage.Oem() > 0, "GetOEMCP не ответил: панель молча ушла бы на запасной путь");

        var original = CultureInfo.CurrentCulture;
        var differing = 0;

        try
        {
            foreach (var name in Cultures)
            {
                var culture = Culture(name);
                CultureInfo.CurrentCulture = culture;

                Assert.Equal(expected, RestoreEngine.ReadAnswer(raw));

                if (culture.TextInfo.OEMCodePage == system) continue;

                // Культура, которой система НЕ писала: разбор ею дал бы другое слово — значит
                // проверка выше действительно способна упасть, а не сверяет число само с собой.
                Assert.NotEqual(expected, Encoding.GetEncoding(culture.TextInfo.OEMCodePage).GetString(AnswerBytes));
                differing++;
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.True(
            differing > 0,
            "ни одной культуры, расходящейся с системной кодовой страницей, не нашлось: " +
            $"GetOEMCP = {system} — состояние дефекта не пройдено, проверка слепа");
    }
}
