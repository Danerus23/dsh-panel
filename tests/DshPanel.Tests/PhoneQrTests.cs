using System;
using System.Collections.Generic;
using System.Linq;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// НАСТОЯЩАЯ ДВЕРЬ QR — ТА, ЧТО РАБОТАЕТ БИБЛИОТЕКОЙ (этап F1, хвост 1).
///
/// **Зачем отдельный класс, когда всё уже проверено в `PhoneSectionTests`.** Там проверка стоит
/// на ПОДСТАВНОЙ двери: она доказывает, что окно просит ровно тот адрес и показывает картинку,
/// — и ничего не говорит о том, что НАСТОЯЩАЯ реализация вообще способна нарисовать QR. Собери
/// её кто-нибудь из пустых байтов, `PhoneSectionTests` остались бы зелёными, а человек увидел бы
/// пустое место. Здесь проверяется ровно это: библиотека (QRCoder, лицензия MIT, в сеть не ходит)
/// действительно кодирует адрес в настоящий PNG.
///
/// ⚠️ **Ни одного своего генератора QR здесь нет** (решение владельца 11.10.2026): тест не
/// рисует модули и не считает маски — он разбирает то, что отдала библиотека, и требует, чтобы
/// результат был настоящей картинкой правдоподобного размера.
///
/// ⚠️ Ни сети, ни файлов, ни портов: кодирование считается на месте, и это тоже утверждение
/// о работе ОФЛАЙН — оно обязано падать, если однажды появится сетевая зависимость.
/// </summary>
public class PhoneQrTests
{
    /// <summary>
    /// Размеры настоящей картинки из её же заголовка. Разбирается ИМЕННО ФОРМАТ PNG, а не
    /// «сколько байт пришло»: «байты непустые» ничего не говорят о том, что внутри картинка.
    /// </summary>
    private static (int Width, int Height) SizeOf(byte[] png)
    {
        Assert.True(png.Length > 24, $"PNG короче заголовка: {png.Length} байт");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));

        int Read(int at) => (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];

        return (Read(16), Read(20));
    }

    /// <summary>Куски PNG, как они записаны в файле: имя и его содержимое.</summary>
    private static List<(string Type, byte[] Data)> Chunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        var at = 8;

        while (at + 12 <= png.Length)
        {
            var length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);

            if (length < 0 || at + 12 + length > png.Length)
            {
                Assert.Fail($"кусок «{type}» оборван: длина {length}, остаток {png.Length - at - 12}");
            }

            var data = new byte[length];

            Array.Copy(png, at + 8, data, 0, length);
            chunks.Add((type, data));

            at += 12 + length;

            if (type == "IEND") break;
        }

        return chunks;
    }

    /// <summary>
    /// НАСТОЯЩАЯ ДВЕРЬ РИСУЕТ НАСТОЯЩИЙ QR, И РАЗМЕР У НЕГО ПРАВДОПОДОБНЫЙ.
    ///
    /// Что здесь доказывается числом: картинка — это PNG (подпись, `IHDR` первым куском,
    /// есть `IDAT` и `IEND`), она КВАДРАТНАЯ, и её сторона кратна числу точек на модуль
    /// (<see cref="PhoneQr.PixelsPerModule"/>) — то есть внутри действительно сетка модулей,
    /// а не произвольная заливка. Сторона для адреса домашней сети — от одного модуля и до
    /// разумного предела: мельче человек не наведёт, крупнее в карточку не влезет.
    /// </summary>
    [Fact]
    public void Настоящий_QR_рисуется_и_размер_его_правдоподобен()
    {
        var png = PhoneQr.Png("192.168.0.5");

        Assert.True(png.Length > 100, $"QR подозрительно короток: {png.Length} байт");

        var (width, height) = SizeOf(png);

        Assert.Equal(width, height);

        // Сторона = (число модулей + поля) × точек на модуль: модулей в QR не меньше 21 (версия 1),
        // поэтому сторона не может быть меньше 21 × точек на модуль.
        Assert.True(
            width >= 21 * PhoneQr.PixelsPerModule,
            $"сторона {width} меньше одного модуля QR: точек на модуль {PhoneQr.PixelsPerModule}");

        Assert.True(width <= 1024, $"QR неправдоподобно велик: {width}×{height}");
        Assert.Equal(0, width % PhoneQr.PixelsPerModule);

        var chunks = Chunks(png).Select(chunk => chunk.Type).ToList();

        Assert.Equal("IHDR", chunks[0]);
        Assert.Contains("IDAT", chunks);
        Assert.Equal("IEND", chunks[^1]);
    }

    /// <summary>
    /// РАЗНЫЕ АДРЕСА — РАЗНЫЕ КАРТИНКИ, ОДИН И ТОТ ЖЕ — ОДНА. Это и есть проверяемое утверждение
    /// «в QR закодирован ИМЕННО этот адрес»: генератор, рисующий одно и то же на что угодно,
    /// провалил бы сравнение, хотя «непустые байты» у него были бы на месте.
    ///
    /// Совпадение двух РАЗНЫХ адресов здесь означало бы, что адрес в код не попадает вовсе.
    /// </summary>
    [Fact]
    public void Разные_адреса_дают_разные_коды_а_один_и_тот_же_один()
    {
        var first = PhoneQr.Png("192.168.0.5");
        var second = PhoneQr.Png("192.168.0.6");
        var again = PhoneQr.Png("192.168.0.5");

        Assert.True(first.Length > 0);
        Assert.True(second.Length > 0);

        Assert.NotEqual(Convert.ToHexString(first), Convert.ToHexString(second));
        Assert.Equal(Convert.ToHexString(first), Convert.ToHexString(again));

        // И адрес другой подсети — тоже другая картинка: кодируется вся строка, а не хвост её.
        var other = PhoneQr.Png("10.0.0.7");

        Assert.NotEqual(Convert.ToHexString(first), Convert.ToHexString(other));
    }

    /// <summary>
    /// НЕТ АДРЕСА — НЕТ КАРТИНКИ. Пустая строка, пробелы и <c>null</c> дают ПУСТО, а не «QR
    /// с ничем»: выдуманный код хуже отсутствия — человек навёл бы камеру на пустоту и решил,
    /// что панель сломалась.
    ///
    /// ⚠️ Проверка стоит на НАСТОЯЩЕЙ двери, а не на подставной: именно она решает, что рисовать,
    /// и «пусто» — это её решение, а не отказ окна.
    /// </summary>
    [Fact]
    public void Без_адреса_картинки_нет()
    {
        Assert.Empty(PhoneQr.Png(string.Empty));
        Assert.Empty(PhoneQr.Png("   "));
        Assert.Empty(PhoneQr.Png(null));
    }

    /// <summary>
    /// ПРАВИЛО, КОТОРЫМ ЖИВЁТ ЭТА ДВЕРЬ, НАЗВАНО СЛОВАМИ В КОДЕ — не только проверкой.
    ///
    /// Почему это утверждение, а не украшение: правило «QR кодирует ровно ту строку, которую
    /// раздел показывает» — единственное, что удерживает настоящий адрес машины от утечки в кадр
    /// README (решение владельца 11.10.2026). Формулировка, потерянная при правке кода, оставила
    /// бы одну проверку — а проверку можно и не заметить.
    /// </summary>
    [Fact]
    public void Правило_кодирования_названо_словами()
    {
        Assert.False(string.IsNullOrWhiteSpace(PhoneQr.PanelsQrRule));
        Assert.Contains("адрес", PhoneQr.PanelsQrRule, StringComparison.OrdinalIgnoreCase);

        // И предупреждение о маске — тоже в правиле: в прогоне съёмки строка другая.
        Assert.Contains("маск", PhoneQr.PanelsQrRule, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// НАСТОЯЩАЯ ДВЕРЬ ОТДАЁТ РОВНО ТУ СТРОКУ, КОТОРУЮ ПОКАЗЫВАЕТ РАЗДЕЛ, — и на замаскированном
    /// адресе тоже. Строку выбирает <see cref="PhoneDecisions.PhoneAddressPicked.Address"/>, и здесь
    /// проверяется, что дверь ЕГО НЕ ПЕРЕСОЧИНЯЕТ: ни порта, ни схемы, ни цифр кода.
    ///
    /// ⚠️ Ради этого проверка и заведена: соблазн «сделать удобнее» (дописать `http://…` или порт
    /// калитки) настоящий, а цена — правило перестаёт быть проверяемым, и в кадр может уехать
    /// не то, что показано человеку.
    /// </summary>
    [Fact]
    public void Дверь_кодирует_ровно_переданную_строку()
    {
        var picked = PhoneDecisions.PickAddressChosen(
            new[] { new Address("Ethernet", "192.168.0.5", AddressKind.Physical, OnLink: true) },
            string.Empty);

        Assert.Equal("192.168.0.5", picked.Address);

        var png = PhoneQr.Png(picked.Address);

        Assert.True(png.Length > 0);
        Assert.False(picked.Masked);

        // Маска в прогоне съёмки: та же строка, но замаскированная — и раздел показывает ИМЕННО её.
        var masked = PhoneDecisions.PickAddressChosen(
            new[] { new Address("Ethernet", "192.168.0.5", AddressKind.Physical, OnLink: true) },
            string.Empty,
            ip => ip[..ip.LastIndexOf('.')] + ".х");

        Assert.True(masked.Masked);
        Assert.Equal("192.168.0.х", masked.Address);

        // Замаскированную строку библиотека тоже закодирует — и это НЕ беда: ровно её кадр
        // и показывает (`Program.BuildShotSettingsWindow` даёт съёмке дверь только на рисование,
        // а строка в код уходит из ПОКАЗА). Здесь важно ДРУГОЕ: дверь не подменила строку настоящей,
        // когда получила маску.
        var maskedPng = PhoneQr.Png(masked.Address);

        Assert.NotEqual(Convert.ToHexString(png), Convert.ToHexString(maskedPng));
    }
}
