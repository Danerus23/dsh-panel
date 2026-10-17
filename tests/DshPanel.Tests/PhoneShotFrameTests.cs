using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// КАДР РАЗДЕЛА «ТЕЛЕФОН» — ОБВЯЗКА СЪЁМКИ, А НЕ ЧИСТОЕ РЕШЕНИЕ (третий хвост после F1).
///
/// **Зачем отдельный класс, когда про QR уже есть две проверки.** И `PhoneQrTests` (настоящая
/// библиотека кодирует), и `PhoneSectionTests` (окно рисует код ровно из показанной строки) были
/// зелены, а **на кадре `--shot settings-phone` кода НЕ БЫЛО ВООБЩЕ**: съёмка собирала окно сама
/// и не давала разделу двери (`phone: null`), окно честно молчало, а кадр уезжал в README
/// с подписью «код здесь образец», которого в нём не было. Обе зелёные проверки собирали окно
/// СВОИМИ руками — со своей подставной дверью, — и до обвязки съёмки не дотягивались ни одна.
/// Нашёл это владелец своим декодером, а не проверка (`docs\HISTORY.md`, «третий хвост»).
///
/// **Что здесь поэтому проверяется и как.** Окно собирается ТЕМ ЖЕ членом, каким его собирает
/// съёмка (`<see cref="Program.BuildShotSettingsWindow"/>`), — то есть пропадёт дверь или маска
/// на пути съёмки, и эти проверки упадут. Дальше меряется то, что видно: показанный адрес
/// замаскирован, картинка в окне есть, и она **побитово та же**, что даёт настоящая библиотека
/// на показанной строке (`PhoneQr.Png(показанное)`) — значит в коде именно она, а не настоящий
/// адрес машины.
///
/// ⚠️ **Расшифровки QR внутри проверки нет и быть не может** (свой декодер — это второй генератор,
/// а свой генератор запрещён решением владельца 11.10.2026): «в коде ровно эта строка» доказывается
/// тождеством картинке библиотеки. Живое чтение кода камерой — F4 и человек.
///
/// ⚠️ **Номера адреса своей машины здесь нет ни одного**: адреса подставные
/// (<see cref="RealNetworkAddresses.Test"/>), как и во всём разделе «Телефон».
/// </summary>
[Collection(PanelLookCollection.Name)]
public class PhoneShotFrameTests
{
    /// <summary>Годный адрес телефону: физический адаптер, приватный адрес, подсеть on-link.</summary>
    private static RawAddress Wired(string ip) =>
        new("Ethernet", "Realtek PCIe GbE Family Controller", true, ip, "255.255.255.0", 25, OnLink: true);

    /// <summary>Сервер окна — тот же прогон проверки, что и в съёмке: порт владельца не занимается.</summary>
    private static ServerController ShotServer(AppPaths paths) =>
        new(
            paths.DshHome,
            () => paths.DataDir,
            _ => { },
            mayOccupyOwnerPort: false,
            probe: ServerProbe.System(),
            ownEngineDir: () => paths.EngineDir);

    /// <summary>
    /// БАЙТЫ КАРТИНКИ — так две картинки сравниваются ПО СОДЕРЖИМОМУ, а не по ссылке.
    ///
    /// ⚠️ **Прямо у картинки из PNG пиксели не прочитать:** у неё (в отличие от кадра отрисовки)
    /// `CopyPixels` отвечает «не поддерживается» — это выяснилось прогоном, а не по памяти.
    /// Поэтому картинка кладётся в <see cref="Image"/> и рисуется В РАЗМЕР ОДИН К ОДНОМУ
    /// (`Stretch.None` при стороне, равной стороне картинки): масштабирования нет вовсе, значит
    /// сравнение остаётся посимвольным сравнением самих кодов, а не «похоже/не похоже».
    /// </summary>
    private static byte[] Pixels(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;

        Assert.True(size.Width > 0 && size.Height > 0, $"пустая картинка {size.Width}×{size.Height}");

        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.None,
            Width = size.Width,
            Height = size.Height,
        };

        image.Measure(new Size(size.Width, size.Height));
        image.Arrange(new Rect(0, 0, size.Width, size.Height));

        var target = new RenderTargetBitmap(size, new Vector(96, 96));
        target.Render(image);

        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            target.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return buffer;
    }

    /// <summary>PNG-байты → картинка тем же способом, каким её строит окно.</summary>
    private static Bitmap Decode(byte[] png)
    {
        Assert.True(png.Length > 0, "библиотека не нарисовала код — сравнивать не с чем");

        return new Bitmap(new MemoryStream(png));
    }

    /// <summary>
    /// КАДР ПОКАЗЫВАЕТ КОД — И В КОДЕ РОВНО ПОКАЗАННАЯ (ЗАМАСКИРОВАННАЯ) СТРОКА.
    ///
    /// **Ради этого проверка и заведена.** Мутации, которые обязаны упасть здесь: «съёмка не даёт
    /// разделу двери» (тот самый дефект) · «съёмка не передаёт маску» · «в код уходит настоящий
    /// адрес» · «дверь съёмки отдаёт пустое вместо картинки» · «раздел кадра — не раздел телефона» ·
    /// «карточка с кодом не рисуется вовсе».
    /// </summary>
    [AvaloniaFact]
    public void Кадр_раздела_Телефон_показывает_код_из_показанной_строки()
    {
        var dir = PanelTestStand.TempDir();
        var paths = AppPaths.Under(dir);

        // АДРЕСА — ПОДСТАВНЫЕ: проверка не должна зависеть от сети той машины, где её гоняют,
        // и уж тем более не должна мерить её настоящий номер (красная линия 7).
        RealNetworkAddresses.Test = new[] { Wired("192.168.0.7") };

        try
        {
            var window = Program.BuildShotSettingsWindow(
                StartModes.ShotSettingsPhone,
                paths,
                ShotServer(paths),
                autostart: null);

            window.Show();
            PanelTestStand.Settle();

            try
            {
                // (1) КАДР — ИМЕННО РАЗДЕЛ «ТЕЛЕФОН»: дверь, данная не тому разделу, оставила бы
                //     человека без адреса и кода вовсе.
                Assert.Equal(SettingsWindow.PhoneSection, window.VisibleSection);

                // (2) ПОКАЗАННЫЙ АДРЕС — ЗАМАСКИРОВАННЫЙ. Это и есть строка, которую увидит читатель
                //     README; настоящий номер машины в кадр уехать не должен (решение владельца).
                //     Строка раздела — СЛОВА, а не голый адрес («адрес для телефона: 192.168.0.х …»),
                //     поэтому из них и берётся адрес: набирать его в проверке заново значило бы
                //     мерить свою выдумку, а не то, что показано.
                var shown = window.FindControl<TextBlock>("PhoneAddressText")!.Text ?? string.Empty;

                Assert.Contains("192.168.0.х", shown, StringComparison.Ordinal);
                Assert.DoesNotContain("192.168.0.7", shown, StringComparison.Ordinal);

                var masked = shown
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .First(token => token.StartsWith("192.168.0.", StringComparison.Ordinal));

                // (3) КОД НАРИСОВАН — и это НАСТОЯЩАЯ картинка библиотеки ровно на той строке,
                //     которую собирает ОДНО решение: `http://` + показанный адрес + `:` + порт
                //     калитки (решение владельца 11.10.2026: камера обязана открывать страницу).
                //     Сравнение идёт по ПИКСЕЛЯМ: подмени код на настоящий адрес машины — картинка
                //     станет другой, и проверка упадёт (разные строки дают разные QR — `PhoneQrTests`).
                var image = window.FindControl<Image>("PhoneQrImage")!;
                var drawn = Assert.IsType<Bitmap>(image.Source);
                var port = PanelSettings.NormalizePhonePort(new PanelSettings().PhonePort);
                var expected = Decode(PhoneQr.Png(PhoneDecisions.QrText(masked, port)));

                Assert.Contains(":43127", PhoneDecisions.QrText(masked, port), StringComparison.Ordinal);

                Assert.Equal(expected.PixelSize, drawn.PixelSize);
                Assert.Equal(Convert.ToHexString(Pixels(expected)), Convert.ToHexString(Pixels(drawn)));

                Assert.True(
                    drawn.PixelSize.Width >= 21 * PhoneQr.PixelsPerModule,
                    $"сторона кода {drawn.PixelSize.Width} меньше одного модуля QR");

                // (4) И ЭТО ВИДНО НА САМОМ ОКНЕ, а не только в свойстве: спрятанный код и код,
                //     которого нет, выглядят в коде одинаково, а человеку и README — по-разному.
                var where = image.TranslatePoint(default, window) ?? default;
                var colors = PanelTestStand.ColorsIn(window, new Rect(where, image.Bounds.Size));

                Assert.True(colors > 1, $"на месте кода один цвет — его не видно: разных цветов {colors}");

                // (5) И ПРО КОД СКАЗАНО СЛОВАМИ: в кадре он образец, а не рабочий код.
                var note = window.FindControl<TextBlock>("PhoneQrNoText")!;

                Assert.True(note.IsVisible);
                Assert.Equal(PanelStrings.PhoneQrMasked, note.Text);

                // (6) КАЛИТКИ У КАДРА НЕТ: съёмка — прогон проверки, и раздел это ПОКАЗЫВАЕТ,
                //     а не делает вид, что телефоном можно управлять. Кнопка кода не нажимается,
                //     а состояние говорит, что калитка закрыта.
                var codeButton = window.FindControl<Button>("PhoneCodeButton")!;
                var status = window.FindControl<TextBlock>("PhoneStatusText")!;

                Assert.False(codeButton.IsEnabled, "на кадре предлагают взять код сопряжения");
                // Состояние говорит СЛОВАМИ ЧЕЛОВЕКА: «не открыто» и адрес, а не имя нашей двери.
                Assert.Contains(PanelStrings.PhoneStateOffFormat.Split('·')[0].Trim(), status.Text ?? string.Empty, StringComparison.Ordinal);
            }
            finally
            {
                window.CloseQuietly();
            }
        }
        finally
        {
            RealNetworkAddresses.Test = null;
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ДВЕРЬ КАДРА УМЕЕТ ТОЛЬКО РИСОВАТЬ — И НИ ОДНОГО ПРАВА КАЛИТКИ У НЕЁ НЕТ.
    ///
    /// **Зачем отдельно от кадра.** Кадр показывает, что код есть; а вот «у этой двери нет права
    /// ни сопрячь, ни отвязать, ни занять порт» из кадра не видно вовсе — окно просто не нажимают.
    /// Здесь это называется поимённо: калитки нет (`gate: () => null`), код не выпускается
    /// и не выдумывается, устройств не забыть, адрес человека не запоминается, настройки
    /// в прогоне проверки не пишутся — а рисует она **тем же**, чем и настоящая
    /// (<see cref="PhoneQr.Png"/>, а не подставными байтами).
    ///
    /// Мутации, которые обязаны упасть здесь: «дверь съёмки выдумывает код» · «отвязывает
    /// устройство без калитки» · «записывает настройку в прогоне проверки» · «рисует пустое
    /// вместо картинки».
    /// </summary>
    [Fact]
    public void Дверь_кадра_умеет_только_рисовать()
    {
        var dir = PanelTestStand.TempDir();
        var paths = AppPaths.Under(dir);

        try
        {
            // ТОТ ЖЕ контроллер, что у съёмки: `allowed: false` — «показаны умолчания и ничего
            // не сохраняется». Право — часть проверки, а не удобство: с правом эта же дверь
            // записала бы настройку.
            var control = new SettingsController(
                new SettingsStore(paths.SettingsFile),
                paths,
                allowed: false,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,
                applyTheme: _ => { },
                log: _ => { });

            var door = new PhoneSection(control, gate: () => null, log: _ => { });

            // НИ ОДНОГО ПРАВА КАЛИТКИ.
            Assert.Empty(door.NewPhoneCode());
            Assert.Empty(door.PhoneCode());
            Assert.False(door.ForgetPhoneDevice("phone-1"));
            Assert.False(door.ChoosePhoneAddress("192.168.0.5"));
            Assert.False(door.SetPhoneAllowed(true));

            // И состояние — честное: калитки нет вовсе, а не «телефон выключен кем-то».
            var info = door.PhoneInfo();

            Assert.False(info.Listening);
            Assert.Equal(0, info.DeviceCount);
            Assert.Empty(info.Devices);
            Assert.False(info.LiveCode);

            // И В ФАЙЛ НИЧЕГО НЕ ЛЕГЛО: прогон проверки не сохраняет (это его право, а не обещание).
            var stored = new SettingsStore(paths.SettingsFile).Load();

            Assert.False(stored.Settings.PhoneEnabled, "прогон проверки записал разрешение на телефон");
            Assert.Empty(stored.Settings.PhoneChosenAddress);

            // А РИСУЕТ — НАСТОЯЩИМ: те же байты, что даёт библиотека на той же строке.
            Assert.Equal(
                Convert.ToHexString(PhoneQr.Png("192.168.0.х")),
                Convert.ToHexString(door.QrPng("192.168.0.х")));
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }
}
