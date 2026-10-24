using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DshPanel.Isolation;
using DshPanel.Pricing;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Tray;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// РАЗДЕЛ «ТЕЛЕФОН» В ОКНЕ НАСТРОЕК И СОСТОЯНИЕ ТЕЛЕФОНА В МЕНЮ ЗНАЧКА (этап F1.d).
///
/// **Что здесь проверяется и от какого случая.** Решения владельца 10.10.2026, дословно:
/// состояние телефона живёт **строкой в меню значка** и **разделом в настройках**, а **главное окно
/// не трогается**; пока телефон не разрешён — **строки в меню нет вовсе**.
///
/// Шесть обещаний раздела, и у каждого своя беда, ради которой оно названо:
///
/// 1. **рубильник исполняет согласие, а не притворяется**: включение записывает ВРЕМЯ согласия,
///    выключение его стирает И ОТВЯЗЫВАЕТ ВСЕ УСТРОЙСТВА — иначе «выключил» ничего бы не значило,
///    потому что подписи считаются от пропуска и старые cookie продолжали бы пускать;
/// 2. **открытая калитка гасится** вместе с выключением: выключенный телефон не имеет права
///    держать порт в сети владельца;
/// 3. **код показывается крупно и словами**, а без живого кода кнопка НЕ показывает пустоту;
/// 4. **список подключённых** говорит «когда подключился» и «когда видели», и «Отвязать» убирает
///    РОВНО ОДНО устройство;
/// 5. **адрес выбирается по правилу, а не по метрике**: несколько подходящих — человек выбирает,
///    один — показывается, ни одного — сказано словами;
/// 6. **сохранение настроек не теряет телефонные поля** — окно пишет файл целиком, и поле,
///    которого оно не знает, обнулялось бы каждым нажатием «Сохранить».
///
/// ⚠️ **Адреса машины в проверках — ПОДСТАВНЫЕ** (и это работа продукта, а не стенда): дверь
/// адресов остаётся настоящей (<see cref="RealNetworkAddresses.Read"/>), а система подменяется
/// её же значением (<see cref="RealNetworkAddresses.Test"/>). Иначе проверка мерила бы сеть той
/// машины, где её запустили, и падала бы на другой. Номеров своей машины здесь нет ни одного.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class PhoneSectionTests
{
    private static void Settle() => PanelTestStand.Settle();

    private static void Click(Button button) =>
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    /// <summary>
    /// ПОДСТАВНАЯ ДВЕРЬ РАЗДЕЛА: окно спрашивает у неё состояние и просит сделать то, что человек
    /// нажал. Считает вызовы — так проверка видит, что рубильник ДЕЙСТВИТЕЛЬНО отвязал устройства
    /// и погасил калитку, а не только перерисовал галочку.
    /// </summary>
    private sealed class FakePhone : IPhoneSection
    {
        public bool Allowed { get; set; }

        public bool Listening { get; set; }

        public bool Live { get; set; }

        public bool Connected { get; set; }

        public string Consent { get; set; } = string.Empty;

        public string LastAddress { get; set; } = string.Empty;

        public string Chosen { get; set; } = string.Empty;

        public string LastSeen { get; set; } = string.Empty;

        /// <summary>
        /// ЕСТЬ ЛИ ЧЕМ ДОСТАТЬ ВОПРОС АГЕНТА — по умолчанию да (обычная дверь раздела).
        /// Ложь — «сервер поднят не панелью»: телефон сопряжён, а вопрос не придёт никогда.
        /// </summary>
        public bool EngineLinked { get; set; } = true;

        public List<PhoneDeviceView> Devices { get; } = new();

        /// <summary>«Код» подставной двери. Шесть цифр — столько же, сколько у настоящего.</summary>
        public string Code { get; set; } = "123456";

        /// <summary>Записать не удалось (прогон без права) — окно обязано сказать об этом словами.</summary>
        public bool WriteFails { get; set; }

        public int ForgetAll { get; private set; }

        /// <summary>Сколько раз окно просило ПОДНЯТЬ калитку (отдельное действие с 11.10.2026).</summary>
        public int Starts { get; private set; }

        /// <summary>Поднимается ли калитка по-настоящему: ложь — «не встала» (нет адреса, порт занят).</summary>
        public bool StartWorks { get; set; } = true;

        /// <summary>Разрешение на телефон из НАСТРОЕК — им решает <see cref="StartPhoneGate"/>.</summary>
        public bool EnabledInFile { get; set; }

        public int Issues { get; private set; }

        public List<string> Forgotten { get; } = new();

        public List<string> Chosen2 { get; } = new();

        public PhoneSectionInfo PhoneInfo()
        {
            // Consent хранится ВИДОМ ФАЙЛА (та же отметка, что пишет `PhoneSection`),
            // а отдаётся СЛОВАМИ — ровно как у настоящей двери: иначе проверка мерила бы
            // не тот формат, каким окно видит человека.
            return new PhoneSectionInfo(
                Allowed,
                Listening,
                Devices.Count,
                Devices.ToList(),
                Live,
                PricingDecisions.CheckedText(Consent),
                LastAddress,
                Chosen,
                PricingDecisions.CheckedText(LastSeen),
                EngineLinked);
        }

        public bool SetPhoneAllowed(bool allowed)
        {
            if (WriteFails) return false;

            Allowed = allowed;
            EnabledInFile = allowed;

            if (allowed)
            {
                // Тот же круговой вид отметки, что у цен: окно показывает её словами,
                // и вторая реализация формата разошлась бы с первой.
                Consent = PricingDecisions.Stamp(DateTimeOffset.Now);

                // ⚠️ С 11.10.2026 включение рубильника КАЛИТКУ НЕ ПОДНИМАЕТ: поднимает её
                // отдельное действие (`StartPhoneGate`), и делает это окно, когда правило готово.
            }
            else
            {
                Consent = string.Empty;
                Devices.Clear();
                ForgetAll++;
                Listening = false;
            }

            return true;
        }

        /// <summary>
        /// ПОДНЯТЬ КАЛИТКУ — отдельным действием. Считает вызовы: так проверка видит, что окно
        /// поднимает дверь ПОСЛЕ правила, а не «заодно с рубильником».
        /// </summary>
        public bool StartPhoneGate()
        {
            Starts++;

            // Настоящая дверь смотрит в НАСТРОЙКИ, а не в память окна: поднимать калитку при
            // выключенном телефоне нельзя, и это её собственное правило, а не правило окна.
            if (!EnabledInFile) return false;

            Listening = StartWorks;

            return StartWorks;
        }

        public string NewPhoneCode()
        {
            Issues++;
            Live = true;

            return Code;
        }

        public string PhoneCode() => Live ? Code : string.Empty;

        public bool ForgetPhoneDevice(string deviceId)
        {
            if (WriteFails) return false;

            Forgotten.Add(deviceId);
            Devices.RemoveAll(device => string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase));

            return true;
        }

        public bool ChoosePhoneAddress(string address)
        {
            if (WriteFails) return false;

            Chosen2.Add(address);
            Chosen = address;

            return true;
        }

        /// <summary>
        /// ДВЕРЬ QR — и ЗАОДНО ПАМЯТЬ: что именно окно попросило закодировать. Ради этой памяти
        /// проверка и живёт без библиотеки и без картинок: «в QR ровно показанный адрес» —
        /// утверждение о СТРОКЕ, а не о пикселях.
        /// </summary>
        public List<string> QrAsked { get; } = new();

        /// <summary>Отвечать ли картинкой. Ложь — «нарисовать не удалось» (адрес не закодировался).</summary>
        public bool QrWorks { get; set; } = true;

        /// <summary>Сколько байт отдаёт дверь: правдоподобный PNG — сотни байт, а не «что-нибудь».</summary>
        public byte[] QrPng(string address)
        {
            QrAsked.Add(address);

            return QrWorks ? FakePng(address) : Array.Empty<byte>();
        }

        /// <summary>
        /// ПОДСТАВНОЙ PNG — НАСТОЯЩИЙ PNG, а не «похожий»: правильная подпись, обязательные куски
        /// (<c>IHDR</c> · <c>IDAT</c> · <c>IEND</c>), честное сжатие и CRC у каждого куска. Поэтому
        /// окно его и вправду РАЗБЕРЁТ и покажет картинку — проверка «картинка не пустая» меряет
        /// то же, что увидит человек, а не договорённость с подставной дверью.
        ///
        /// ⚠️ **Это не «свой генератор QR»**: QR здесь не кодируется вовсе — рисуется ЧЁРНАЯ
        /// картинка с белым уголком, а «какой адрес» дверь запоминает ОТДЕЛЬНО (<see cref="QrAsked"/>).
        /// Кодировать QR своими руками запрещено решением владельца 11.10.2026 — и не делается.
        /// Картинка нужна ровно затем, чтобы проверить ПОКАЗ: без неё «Image.Source пуст» было бы
        /// неотличимо от «картинка пришла, но окно её не показало».
        ///
        /// ⚠️ **Пусто на входе — пусто и здесь**: это не удобство проверки, а то же правило, что
        /// у настоящей двери (<see cref="PhoneQr.Png"/>): кодировать нечего — картинки нет.
        /// Подставная дверь, отвечающая иначе, мерила бы не тот путь, каким идёт человек.
        /// </summary>
        public static byte[] PngOf(string address) =>
            string.IsNullOrWhiteSpace(address) ? Array.Empty<byte>() : FakePng(address);

        private static byte[] FakePng(string address)
        {
            const int side = 29;                    // сторона QR версии 3 — правдоподобный размер

            var pixels = new byte[(side * 4 + 1) * side];

            for (var y = 0; y < side; y++)
            {
                var row = y * (side * 4 + 1);

                pixels[row] = 0;                    // фильтр строки: у нас он не нужен (none)

                for (var x = 0; x < side; x++)
                {
                    var at = row + 1 + (x * 4);

                    // Белый уголок в левом верхнем углу — чтобы картинка не была сплошной, а по
                    // содержимому было видно, что адрес в неё ВЛОЖЕН, а не взят из воздуха.
                    var white = x < 3 && y < 3;

                    // Оттенок выведен из длины адреса: картинка зависит от того, ЧТО закодировано.
                    var shade = (byte)((address ?? string.Empty).Length * 7);

                    pixels[at] = white ? (byte)0xFF : shade;
                    pixels[at + 1] = white ? (byte)0xFF : (byte)0x40;
                    pixels[at + 2] = white ? (byte)0xFF : (byte)0x80;
                    pixels[at + 3] = 0xFF;
                }
            }

            var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

            var header = new List<byte>();
            header.AddRange(BigEndian(side));
            header.AddRange(BigEndian(side));
            header.AddRange(new byte[] { 8, 6, 0, 0, 0 });      // 8 бит на канал, RGBA

            Chunk(png, "IHDR", header.ToArray());
            Chunk(png, "IDAT", Deflate(pixels));
            Chunk(png, "IEND", Array.Empty<byte>());

            return png.ToArray();
        }

        /// <summary>Кусок PNG: длина, имя, данные и CRC — по имени И данным, как велит формат.</summary>
        private static void Chunk(List<byte> png, string name, byte[] data)
        {
            var type = System.Text.Encoding.ASCII.GetBytes(name);
            var body = new byte[type.Length + data.Length];

            type.CopyTo(body, 0);
            data.CopyTo(body, type.Length);

            png.AddRange(BigEndian(data.Length));
            png.AddRange(type);
            png.AddRange(data);
            png.AddRange(BigEndian(unchecked((int)Crc32(body))));
        }

        /// <summary>Данные PNG сжимаются zlib — ровно то, что читает любой разборщик картинок.</summary>
        private static byte[] Deflate(byte[] data)
        {
            using var output = new MemoryStream();

            using (var zlib = new System.IO.Compression.ZLibStream(
                output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(data, 0, data.Length);
            }

            return output.ToArray();
        }

        private static byte[] BigEndian(int value) => new[]
        {
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value,
        };

        /// <summary>CRC-32 для куска PNG — им проверяется целостность куска.</summary>
        private static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;

            foreach (var value in data)
            {
                crc ^= value;

                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
            }

            return crc ^ 0xFFFFFFFFu;
        }
    }

    /// <summary>
    /// Окно раздела «Телефон» с подставной дверью и подставными адресами машины.
    ///
    /// <paramref name="mask"/> — маска ПОКАЗА адреса: её ставит прогон съёмки (`Program.MaskAddress`),
    /// и уходит она в САМУ связку окна — иначе первый показ раздела успел бы показать настоящий
    /// адрес, и проверка мерила бы не тот кадр, какой уезжает в README.
    /// </summary>
    private static SettingsWindow Stand(
        string dir,
        FakePhone? phone,
        IReadOnlyList<RawAddress>? raw,
        Func<string, string>? mask = null)
    {
        RealNetworkAddresses.Test = raw;

        var window = PanelTestStand.SettingsStand(dir, phone: phone, phoneAddressMask: mask);

        window.ShowSection(SettingsWindow.PhoneSection);
        window.Show();
        Settle();

        return window;
    }

    private static void Close(SettingsWindow window)
    {
        window.CloseQuietly();
        RealNetworkAddresses.Test = null;
    }

    /// <summary>
    /// ЗАПИСАТЬ В ФАЙЛ НАСТРОЕК, ЧТО ТЕЛЕФОН УЖЕ РАЗРЕШЁН, — и только потом строить окно.
    ///
    /// ⚠️ Это не «подгонка под ответ», а та же правда, что в жизни: окно показывает РЕШЕНИЕ
    /// ИЗ НАСТРОЕК (<see cref="PanelSettings.PhoneEnabled"/>), а не то, что помнит дверь. Проверка,
    /// выставившая галочку в обход файла, мерила бы свою выдумку: у человека разрешение лежит
    /// в `settings.json`, и после перезапуска панели оно приходит именно оттуда.
    /// </summary>
    private static void Seed(string dir, bool allowed, bool chosenPort = false, string chosenAddress = "")
    {
        var paths = AppPaths.Under(dir);
        var store = new SettingsStore(paths.SettingsFile);

        Assert.True(
            store.Save(SettingsStore.Clean(new PanelSettings
            {
                PhoneEnabled = allowed,
                PhonePortChosen = chosenPort,
                PhoneChosenAddress = chosenAddress,
            })),
            "подготовка проверки не записалась");
    }

    /// <summary>Годный адрес телефону: физический адаптер, приватный адрес, подсеть on-link.</summary>
    private static RawAddress Wired(string ip, bool up = true, bool onLink = true, int metric = 25) =>
        new("Ethernet", "Realtek PCIe GbE Family Controller", up, ip, "255.255.255.0", metric, onLink);

    /// <summary>Туннель: адрес настоящий и приватный, адаптер поднят, а сети своей за ним нет.</summary>
    private static RawAddress Tunnel(string ip, int metric) =>
        new("CloudflareWARP", "Cloudflare WARP Tunnel", true, ip, "255.255.255.255", metric, OnLink: true);

    /// <summary>
    /// ПРИЗНАК «ПОДСЕТЬ ВЕДЁТ НАПРЯМУЮ» СЧИТАЕТСЯ ПО ДАННЫМ, А НЕ ПО МЕТРИКЕ: шлюз лежит
    /// в подсети адреса — значит в сети есть кто-то, кроме машины. Туннель WARP со своим шлюзом
    /// и подсетью `/32` в чужую подсеть не попадает.
    ///
    /// ⚠️ Проверка ЧИСТАЯ — без машины, без адаптеров. Она и сторожит правило: признак приходит
    /// от сети, а не выдумывается окном.
    /// </summary>
    [Fact]
    public void Признак_подсети_считается_по_шлюзу_в_подсети()
    {
        // Домашняя сеть: адрес и шлюз в одной /24 — признак есть.
        Assert.True(NetworkAddresses.GatewayInside("192.168.0.71", "255.255.255.0", new[] { "192.168.0.1" }));

        // Широковещательный адрес и сама машина шлюзом не считаются.
        Assert.False(NetworkAddresses.GatewayInside("192.168.0.71", "255.255.255.0", new[] { "192.168.0.255" }), "широковещательный");
        Assert.False(NetworkAddresses.GatewayInside("192.168.0.71", "255.255.255.0", new[] { "192.168.0.71" }), "сама машина");

        // Маска вперемешку — не маска: подсети у неё нет, и признак не выдумывается.
        Assert.False(NetworkAddresses.GatewayInside("192.168.0.71", "255.0.255.0", new[] { "192.168.0.1" }), "маска вперемешку");

        // Шлюз ЧУЖОЙ подсети — признака нет: в этой сети, кроме машины, никого.
        Assert.False(NetworkAddresses.GatewayInside("192.168.0.71", "255.255.255.0", new[] { "10.0.0.1" }), "чужая подсеть");

        // Туннель: своя подсеть /32, и шлюз домашней сети в неё не попадает.
        Assert.False(NetworkAddresses.GatewayInside("172.16.0.9", "255.255.255.255", new[] { "192.168.0.1" }), "туннель");

        // Ни одного шлюза — «нет»: непригодный адрес лучше выдуманного.
        Assert.False(NetworkAddresses.GatewayInside("192.168.0.71", "255.255.255.0", Array.Empty<string>()), "нет шлюзов");

        // Мусор в доводах не роняет решение.
        Assert.False(NetworkAddresses.GatewayInside("не адрес", "255.255.255.0", new[] { "192.168.0.1" }), "мусор в адресе");
        Assert.False(NetworkAddresses.GatewayInside("192.168.0.71", null, new[] { "мусор" }), "мусор в маске");
    }

    /// <summary>
    /// НАСТОЯЩАЯ КАЛИТКА НА СВОБОДНОМ ПОРТУ ПЕТЛИ БЕЗ ЕДИНОГО ЗАПРОСА НАРУЖУ. Нужна там, где
    /// мало подставной двери: «выключение гасит порт» и «выключает устройства» — утверждения
    /// о НАСТОЯЩЕЙ калитке, и подставная дверь их не проверяет вовсе (её `Stop` в мутации
    /// не участвует). Так же устроен стенд калитки (`PhoneGateTests`), и это не совпадение:
    /// проверка обязана уметь падать.
    ///
    /// ⚠️ Наружу не уходит ни один байт: адрес — петля, движок подставной, двери событий нет.
    /// Порт — свободный у системы и не <c>3080</c>/<c>3081</c>/<c>43127</c>.
    /// </summary>
    private sealed class RealGate : IDisposable
    {
        private readonly string _root;

        public RealGate(string root)
        {
            _root = root;
            Paths = AppPaths.Under(root);
            Port = FreeLoopbackPort();

            Doors = new PhoneGateDoors(
                () => "127.0.0.1",
                Port,
                (_, _) => new PhoneEngineAnswer(200, "{}"),
                PhonePassFiles.Under(Paths, _ => { }),
                PhoneEventDoors.None,
                () => DateTimeOffset.Now,
                _ => { },
                Bound: _ => { });

            Gate = new PhoneGate(Doors, () => true, humanLaunch: true, RunContext.Create(RunRequest.Owner()));
        }

        public AppPaths Paths { get; }

        public int Port { get; }

        public PhoneGateDoors Doors { get; }

        public PhoneGate Gate { get; }

        public void Dispose()
        {
            // Калитка гасится ВСЕГДА: незакрытый слушатель занял бы порт до конца прогона.
            Gate.Dispose();
            PanelTestStand.RemoveTemp(_root);
        }
    }

    /// <summary>Свободный порт петли, который калитка вправе занять: ни 3080/3081, ни 43127.</summary>
    private static int FreeLoopbackPort()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);

            try
            {
                listener.Start();
                var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

                if (!PhoneGate.PortForbidden(port) && port != Server.PhonePass.DefaultPort) return port;
            }
            finally
            {
                listener.Stop();
            }
        }

        throw new InvalidOperationException("не нашлось свободного порта петли, который калитка вправе занять");
    }

    /// <summary>
    /// ВЫКЛЮЧЕНИЕ НА НАСТОЯЩЕЙ КАЛИТКЕ: порт освобождается, устройства исчезают, согласие стирается.
    ///
    /// ⚠️ **Ради этого проверка и заведена.** Подставная дверь подтверждает только то, что окно
    /// ПОЗВАЛО её методы; «калитка действительно перестала слушать» и «устройства действительно
    /// забыты» — утверждения о настоящей калитке, и мутации H1/H2 падали именно здесь, а на
    /// подставной двери выживали. Заодно проверяется, что выключение вообще записывается в файл:
    /// человек после перезапуска панели не должен увидеть прежний список.
    /// </summary>
    [AvaloniaFact]
    public void Выключение_на_настоящей_калитке_гасит_порт_и_забывает_устройства()
    {
        var dir = PanelTestStand.TempDir();

        using var stand = new RealGate(dir);

        try
        {
            var settings = new SettingsController(
                new SettingsStore(stand.Paths.SettingsFile),
                stand.Paths,
                allowed: true,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,
                applyTheme: _ => { },
                log: _ => { });

            // Телефон разрешён, калитка открыта, устройство сопряжено и ВИДЕНО.
            Assert.True(settings.Save(SettingsStore.Clean(new PanelSettings
            {
                PhoneEnabled = true,
                PhoneConsentAt = PricingDecisions.Stamp(DateTimeOffset.Now.AddDays(-1)),
            })));

            Assert.True(stand.Gate.Start().Opened, "калитка не открылась — проверка пошла бы не тем путём");
            Assert.True(stand.Gate.Listening);

            PhonePass.Load(stand.Doors.Pass)
                .WithNewDevice("phone-1", "192.168.0.9", PricingDecisions.Stamp(DateTimeOffset.Now))
                .Save(stand.Doors.Pass);

            Assert.Equal(1, stand.Gate.DeviceCount);

            var section = new PhoneSection(settings, () => stand.Gate, _ => { }, () => DateTimeOffset.Now);

            var window = new SettingsWindow();
            window.Attach(settings, autostart: null, phone: section);
            window.ShowSection(SettingsWindow.PhoneSection);
            window.Show();
            Settle();

            // ЧЕЛОВЕК ВЫКЛЮЧАЕТ ТЕЛЕФОН.
            var check = window.FindControl<CheckBox>("PhoneAllowedCheck");
            Assert.True(check!.IsChecked);
            check.IsChecked = false;
            Settle();

            Assert.False(stand.Gate.Listening, "калитка продолжает слушать выключенный телефон");
            Assert.Equal(0, stand.Gate.DeviceCount);
            Assert.Equal(0, PhonePass.Load(stand.Doors.Pass).DeviceCount);

            // И в ФАЙЛЕ настроек согласия больше нет: иначе после перезапуска панель считала бы,
            // что человек разрешение не отзывал.
            var back = new SettingsStore(stand.Paths.SettingsFile).Load().Settings;

            Assert.False(back.PhoneEnabled);
            Assert.Equal(string.Empty, back.PhoneConsentAt);

            window.CloseQuietly();
        }
        finally
        {
            stand.Dispose();
        }
    }

    /// <summary>
    /// ВКЛЮЧЕНИЕ НАСТОЯЩЕЙ ДВЕРЬЮ ЗАПИСЫВАЕТ ВРЕМЯ СОГЛАСИЯ В ФАЙЛ. Подставная дверь пишет
    /// время СЕБЕ, и «согласие запомнено» иначе осталось бы непроверенным: у человека оно лежит
    /// именно в `settings.json`, и после перезапуска панели приходит оттуда.
    /// </summary>
    [Fact]
    public void Включение_настоящей_дверью_пишет_согласие_в_файл()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var paths = AppPaths.Under(dir);

            var settings = new SettingsController(
                new SettingsStore(paths.SettingsFile),
                paths,
                allowed: true,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,
                applyTheme: _ => { },
                log: _ => { });

            // Калитки нет — так и в жизни у прогона проверки: право её не пускает. Разрешение
            // от этого записаться обязано: оно решение ЧЕЛОВЕКА, а не следствие открытого порта.
            var section = new PhoneSection(settings, () => null, _ => { }, () => DateTimeOffset.Now);

            Assert.True(section.SetPhoneAllowed(true));

            var back = new SettingsStore(paths.SettingsFile).Load().Settings;

            Assert.True(back.PhoneEnabled);
            Assert.True(back.PhoneConsentAt.Length > 0, "время согласия не записалось");
            Assert.True(
                PricingDecisions.TryParse(back.PhoneConsentAt, out _),
                "время согласия записано не тем видом, каким его читает панель: " + back.PhoneConsentAt);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ЗАПОМНИТЬ АДРЕС, ВЫБРАННЫЙ ЧЕЛОВЕКОМ, — НАСТОЯЩЕЙ ДВЕРЬЮ. Выбор обязан пережить
    /// перезапуск панели: иначе человек выбирал бы его каждый раз заново.
    /// </summary>
    [Fact]
    public void Выбор_адреса_запоминается_в_файле()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var paths = AppPaths.Under(dir);

            var settings = new SettingsController(
                new SettingsStore(paths.SettingsFile),
                paths,
                allowed: true,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,
                applyTheme: _ => { },
                log: _ => { });

            var section = new PhoneSection(settings, () => null, _ => { }, () => DateTimeOffset.Now);

            Assert.True(section.ChoosePhoneAddress("10.0.0.7"));
            Assert.Equal("10.0.0.7", new SettingsStore(paths.SettingsFile).Load().Settings.PhoneChosenAddress);

            // И помнит его дверь, а не только файл: раздел показывает выбор из СВОЕГО ответа.
            Assert.Equal("10.0.0.7", section.PhoneInfo().ChosenAddress);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ---------------------------------------------------------------- раздел виден и назван

    /// <summary>
    /// РАЗДЕЛ ЕСТЬ В СПИСКЕ СЛЕВА, НАЗВАН И ЗАНИМАЕТ СВОЮ ПАНЕЛЬ. Проверяется ГЕОМЕТРИЕЙ
    /// показанного окна: раздел, объявленный в разметке и не показанный, выглядел бы точно так же.
    /// </summary>
    [AvaloniaFact]
    public void Раздел_телефона_виден_и_назван()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakePhone(), new[] { Wired("192.168.0.5") });
            var list = window.FindControl<ListBox>("SectionsList");

            Assert.NotNull(list);

            var titles = list!.ItemsSource!.Cast<string>().ToList();

            Assert.Contains(PanelStrings.PhoneSectionTitle, titles);
            Assert.Equal(7, titles.Count);
            Assert.Equal(SettingsWindow.PhoneSection, window.ShowSection(SettingsWindow.PhoneSection));
            Assert.Equal(SettingsWindow.PhoneSection, window.VisibleSection);
            Assert.Equal(SettingsWindow.PhoneSection, list.SelectedIndex);

            var title = window.FindControl<TextBlock>("PhoneSectionTitle");
            var subtitle = window.FindControl<TextBlock>("PhoneSectionSubtitleText");
            var panel = window.FindControl<StackPanel>("PhoneSectionPanel");

            Assert.NotNull(title);
            Assert.NotNull(subtitle);
            Assert.NotNull(panel);

            Assert.Equal(PanelStrings.PhoneSectionTitle, title!.Text);
            Assert.Equal(PanelStrings.PhoneSectionSubtitle, subtitle!.Text);
            Assert.True(title.Classes.Contains("sectionTitle"), "заголовок раздела потерял свой класс");
            Assert.Contains("hint", subtitle.Classes);

            // СЛОВА-ОГРАНИЧИТЕЛЬ (решение владельца 17.10.2026): телефон работает в своей сети,
            // ставить задачи и управлять агентами ещё нельзя, наружу он не выходит. Строка обязана
            // быть ВИДНА в разделе — «ключ есть в словаре» этого не доказывает.
            var limits = window.FindControl<TextBlock>("PhoneLimitsText");

            Assert.NotNull(limits);
            Assert.NotEqual(nameof(PanelStrings.PhoneLimits), PanelStrings.PhoneLimits);
            Assert.Equal(PanelStrings.PhoneLimits, limits!.Text);
            Assert.Contains("hint", limits.Classes);
            Assert.True(limits.IsVisible, "строка об ограничениях телефона не показана в разделе «Телефон»");
            Assert.True(panel!.IsVisible, "панель раздела не показана");

            // РУБИЛЬНИК ПОДПИСАН И СТОИТ В РАЗДЕЛЕ: галочка без подписи — это поле, о котором
            // человек не знает, что оно делает.
            var check = window.FindControl<CheckBox>("PhoneAllowedCheck");

            Assert.NotNull(check);
            Assert.Equal(PanelStrings.PhoneAllowedCheck, check!.Content);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// АДРЕС ВИДЕН И БЕЗ ДВЕРИ РАЗДЕЛА. «По какому адресу телефон найдёт панель» — свойство САМОЙ
    /// машины, а не калитки; карточка адреса, оставшаяся пустой при молчащей двери, выглядела бы
    /// как недоделка.
    ///
    /// ⚠️ Ради этого проверка и заведена: беду нашёл КАДР раздела (в нём не было ни адреса,
    /// ни слов о том, что его нет), а прежняя проверка смотрела на текст, которого не было вовсе,
    /// и зеленела.
    /// </summary>
    [AvaloniaFact]
    public void Адрес_виден_и_без_двери_раздела()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            RealNetworkAddresses.Test = new[] { Wired("192.168.0.7") };

            var window = PanelTestStand.SettingsStand(dir, phone: null);
            window.ShowSection(SettingsWindow.PhoneSection);
            window.Show();
            Settle();

            // Дверь молчит: ни рубильника, ни кода.
            Assert.Equal(
                PanelStrings.PhoneNoDoor,
                window.FindControl<TextBlock>("PhoneStatusText")!.Text);
            Assert.False(window.FindControl<Button>("PhoneCodeButton")!.IsEnabled);

            // А адрес — на месте, словами и с признаком подсети.
            var text = window.FindControl<TextBlock>("PhoneAddressText")!;

            Assert.True(text.IsVisible, "адрес не показан, хотя дверь тут ни при чём");
            Assert.Contains("192.168.0.7", text.Text ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains(PanelStrings.PhoneAddressOnLink, text.Text ?? string.Empty, StringComparison.Ordinal);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ---------------------------------------------------------------- рубильник и согласие

    /// <summary>
    /// СЕРВЕР ПОДНЯТ НЕ ПАНЕЛЬЮ — РАЗДЕЛ ГОВОРИТ ОБ ЭТОМ СЛОВАМИ, А НЕ «ТЕЛЕФОН ПОДКЛЮЧЁН»
    /// (находка владельца 13.10.2026, вечер).
    ///
    /// **От какого случая.** Человек поднял сервер DSH сам, не панелью. Телефон при этом сопряжён,
    /// калитка открыта — и раздел показывал «телефон подключён · адрес: … · устройств: 1». Это
    /// обещание, которого не будет: панель не знает, чем войти в чужой сервер (ссылки входа у неё
    /// нет), и вопрос агента на телефон НЕ ПРИДЁТ НИКОГДА. Человек узнавал об этом только по тому,
    /// что телефон молчал.
    ///
    /// Мутации, которые обязаны упасть здесь: «вернуть строку подключения при отсутствии связи» ·
    /// «считать связь существующей всегда» · «убрать слова о сервере DSH».
    /// </summary>
    [AvaloniaFact]
    public void Без_связи_с_сервером_раздел_говорит_правду_а_не_подключение()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var phone = new FakePhone
            {
                Listening = true,
                Connected = true,
                EngineLinked = false,
                LastAddress = "192.168.0.9",
            };

            phone.Devices.Add(new PhoneDeviceView("phone-1", "01.01.2026, 10:00", "01.01.2026, 10:05", "телефон"));

            // ТЕЛЕФОН РАЗРЕШЁН — В ФАЙЛЕ НАСТРОЕК, а не галочкой в обход него: окно показывает
            // РЕШЕНИЕ ИЗ НАСТРОЕК, и подстановка галочки мерила бы выдумку (см. `Seed`).
            Seed(dir, allowed: true);

            var window = Stand(dir, phone, new[] { Wired("192.168.0.9") });

            Assert.True(window.FindControl<CheckBox>("PhoneAllowedCheck")!.IsChecked == true, "разрешение не доехало из файла");

            var status = window.FindControl<TextBlock>("PhoneStatusText")!.Text ?? string.Empty;

            // И НИ ОДНОГО СЛОВА О ПОДКЛЮЧЕНИИ: «адрес: …» и «устройств: …» в этом положении —
            // обещание работающего телефона, которого нет.
            // ЭТО ИМЕННО СЛОВА ПРАВДЫ, а не другое состояние с теми же пропусками.
            Assert.Equal(PanelStrings.PhoneSectionNoEngine, status);

            Assert.DoesNotContain("адрес", status, StringComparison.Ordinal);
            Assert.DoesNotContain("устройств:", status, StringComparison.Ordinal);

            Close(window);

            // ⚠️ И ОБРАТНАЯ ПОЛОВИНА, БЕЗ КОТОРОЙ ПЕРВАЯ НИЧЕГО НЕ СТОИТ: когда вход
            // в сервер DSH ЕСТЬ, раздел обязан говорить своё обычное «подключён · адрес · устройств» —
            // иначе «правда» достигалась бы тем, что раздел всегда молчит про связь.
            var linked = new FakePhone
            {
                Listening = true,
                Connected = true,
                EngineLinked = true,
                LastAddress = "192.168.0.9",
            };

            linked.Devices.Add(new PhoneDeviceView("phone-1", string.Empty, string.Empty, "телефон"));

            var second = Stand(dir, linked, new[] { Wired("192.168.0.9") });

            var connected = second.FindControl<TextBlock>("PhoneStatusText")!.Text ?? string.Empty;

            Assert.NotEqual(PanelStrings.PhoneSectionNoEngine, connected);
            Assert.Contains("адрес", connected, StringComparison.Ordinal);
            Assert.Contains("устройств:", connected, StringComparison.Ordinal);

            Close(second);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ВКЛЮЧЕНИЕ ПИШЕТ ФАКТ И ВРЕМЯ СОГЛАСИЯ, И РАЗДЕЛ ГОВОРИТ ОБ ЭТОМ СЛОВАМИ.
    /// «Включено сейчас» и «когда это было решено» — разные вопросы, и второй виден человеку.
    /// </summary>
    [AvaloniaFact]
    public void Включение_запоминает_согласие_и_показывает_когда()    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var phone = new FakePhone { Allowed = false };
            var window = Stand(dir, phone, new[] { Wired("192.168.0.5") });

            var check = window.FindControl<CheckBox>("PhoneAllowedCheck");
            Assert.NotNull(check);
            Assert.False(check!.IsChecked, "умолчание — телефон НЕ разрешён");

            check.IsChecked = true;
            Settle();

            Assert.True(phone.Allowed);
            Assert.True(phone.Consent.Length > 0, "включение обязано записать время согласия");
            Assert.Equal(0, phone.ForgetAll);
            Assert.True(phone.Listening, "калитка обязана подняться по щелчку человека");

            // Слова: и состояние, и «когда разрешил» — оба на экране.
            var status = window.FindControl<TextBlock>("PhoneStatusText")!.Text ?? string.Empty;
            var consent = window.FindControl<TextBlock>("PhoneConsentText")!;

            // СТРОКА ГОВОРИТ АДРЕСОМ И ЧИСЛОМ УСТРОЙСТВ (решение владельца 13.10.2026), а не
            // внутренним словом о нашей двери: «слушает порт» человеку ничего не отвечает.
            // ⚠️ Признак берётся ПО ПОСТОЯННОЙ ЧАСТИ СТРОКИ, а не по слову состояния: слово зависит
            // от того, разрешён ли телефон в ЭТОМ стенде (здесь он выключен), а «адрес» и «устройств»
            // есть всегда — и именно они отвечают человеку на его вопрос.
            Assert.Contains("адрес", status, StringComparison.Ordinal);
            Assert.Contains("устройств", status, StringComparison.Ordinal);
            Assert.Contains("устройств: 0", status, StringComparison.Ordinal);
            Assert.True(consent.IsVisible, "строка «когда разрешил» обязана быть видна");
            Assert.Contains(
                PricingDecisions.CheckedText(phone.Consent),
                consent.Text ?? string.Empty,
                StringComparison.Ordinal);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ВЫКЛЮЧЕНИЕ СТИРАЕТ СОГЛАСИЕ И **ОТВЯЗЫВАЕТ ВСЕ УСТРОЙСТВА**, и гасит открытую калитку.
    ///
    /// ⚠️ Ради этого проверка и заведена: выключение, которое только снимает галочку, ничего
    /// не значит — подписи считаются от пропуска, и старые cookie продолжили бы пускать.
    /// Подставная дверь ВИДИТ вызовы, и мутация «выключение не отвязывает устройства» падает здесь.
    /// </summary>
    [AvaloniaFact]
    public void Выключение_стирает_согласие_и_отвязывает_устройства()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var phone = new FakePhone { Allowed = false };

            Seed(dir, allowed: true);
            phone.Allowed = true;
            phone.Listening = true;

            var window = Stand(dir, phone, new[] { Wired("192.168.0.5") });

            var check = window.FindControl<CheckBox>("PhoneAllowedCheck");
            Assert.NotNull(check);

            // (1) ЧЕЛОВЕК РАЗРЕШИЛ ТЕЛЕФОН, И У НЕГО УЖЕ ДВА УСТРОЙСТВА.
            Assert.True(check!.IsChecked, "разрешение из настроек не показано галочкой");

            phone.Devices.Add(new PhoneDeviceView("phone-1", "01.01.2026, 10:00", "01.01.2026, 10:05"));
            phone.Devices.Add(new PhoneDeviceView("phone-2", "02.01.2026, 11:00", "02.01.2026, 11:05"));

            window.Render();
            Settle();

            Assert.Equal(2, window.FindControl<StackPanel>("PhoneDevicesPanel")!.Children.Count);

            // (2) ВЫКЛЮЧАЕТ: согласие стирается, устройства отвязываются, калитка гаснет.
            check.IsChecked = false;
            Settle();

            Assert.False(phone.Allowed);
            Assert.Equal(string.Empty, phone.Consent);
            Assert.Equal(1, phone.ForgetAll);
            Assert.Empty(phone.Devices);
            Assert.False(phone.Listening, "выключенный телефон не имеет права держать порт");

            // И человеку сказано, что устройств больше нет, — а не показан прежний список.
            Assert.Empty(window.FindControl<StackPanel>("PhoneDevicesPanel")!.Children);
            Assert.True(window.FindControl<TextBlock>("PhoneDevicesEmptyText")!.IsVisible);
            Assert.Equal(
                string.Format(CultureInfo.CurrentCulture, PanelStrings.PhoneDevicesCountFormat, 0),
                window.FindControl<TextBlock>("PhoneDevicesCountText")!.Text);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// РУБИЛЬНИК, КОТОРЫЙ НЕ ЗАПИСАЛСЯ, ГОВОРИТ ОБ ЭТОМ СЛОВАМИ: сделать вид, что телефон разрешён,
    /// значило бы обещать дверь, которой нет.
    /// </summary>
    [AvaloniaFact]
    public void Рубильник_который_не_записался_сказан_словами()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var phone = new FakePhone { Allowed = false, WriteFails = true };

            // Умолчание: телефона нет — и рубильник, который не записался, обязан сказать об этом.
            var window = Stand(dir, phone, new[] { Wired("192.168.0.5") });

            var check = window.FindControl<CheckBox>("PhoneAllowedCheck");
            check!.IsChecked = true;
            Settle();

            Assert.False(phone.Allowed);
            Assert.Equal(PanelStrings.PhoneToggleFailed, window.FindControl<TextBlock>("StatusText")!.Text);

            // Галочка возвращается к правде: она показывает НАСТОЯЩЕЕ состояние, а не нажатие.
            Assert.False(check.IsChecked);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ---------------------------------------------------------------- порт

    /// <summary>
    /// ПОЛЕ ПОРТА ПИШЕТ ЗНАЧЕНИЕ И ПРИЗНАК «ЧЕЛОВЕК ВЫБИРАЛ», А ПОРТ ВНЕ ГРАНИЦ СТАНОВИТСЯ
    /// УМОЛЧАНИЕМ. Признак поставлен человеком, а не окном: без этой разницы «в файле умолчание»
    /// и «человек выбрал это число» неотличимы, и перенос умолчания однажды переписал бы его выбор.
    /// </summary>
    [AvaloniaFact]
    public void Поле_порта_пишет_значение_и_признак_а_вне_границ_даёт_умолчание()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakePhone(), new[] { Wired("192.168.0.5") });

            // (1) Умолчание: человек поля не трогал — признак не выставлен.
            Assert.Equal(Server.PhonePass.DefaultPort, window.PendingSettings().PhonePort);
            Assert.False(window.PendingSettings().PhonePortChosen);

            // (2) НАБРАЛ САМ: значение и признак.
            var box = window.FindControl<NumericUpDown>("PhonePortBox");
            Assert.NotNull(box);

            box!.Value = 43210;
            Settle();

            Assert.Equal(43210, window.PendingSettings().PhonePort);
            Assert.True(window.PendingSettings().PhonePortChosen, "набранный человеком порт обязан быть помечен");

            // (3) ВНЕ ГРАНИЦ: в файл уходит УМОЛЧАНИЕ, а не «порт 0».
            box.Value = 0;
            Settle();

            Assert.Equal(Server.PhonePass.DefaultPort, window.PendingSettings().PhonePort);

            // (4) И ПОДСКАЗКА НАЗЫВАЕТ ЗАПРЕЩЁННОЕ СЛОВАМИ — до нажатия, а не отказом после.
            var hint = window.FindControl<TextBlock>("PhonePortNoteText")!.Text ?? string.Empty;

            Assert.Contains(
                Server.PhonePass.DefaultPort.ToString(CultureInfo.InvariantCulture),
                hint,
                StringComparison.Ordinal);

            var forbidden = window.FindControl<TextBlock>("PhoneForbiddenPortsText")!.Text ?? string.Empty;

            Assert.Contains(ServerDecisions.OwnerPort.ToString(CultureInfo.InvariantCulture), forbidden, StringComparison.Ordinal);
            Assert.Contains(ServerDecisions.RunFallbackPort.ToString(CultureInfo.InvariantCulture), forbidden, StringComparison.Ordinal);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПОРТ, КОТОРЫЙ КАЛИТКА НЕ ЗАЙМЁТ НИКОГДА, НАЗВАН ЗАПРЕЩЁННЫМ ПРЯМО: <c>3080</c> — порт
    /// сервера панели, <c>3081</c> — его запасной. Сохранить их человек вправе (файл его),
    /// а вот ждать на них телефон нельзя, и это сказано словами.
    /// </summary>
    [AvaloniaFact]
    public void Порт_сервера_панели_назван_запрещённым()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakePhone(), new[] { Wired("192.168.0.5") });

            var box = window.FindControl<NumericUpDown>("PhonePortBox")!;
            box.Value = ServerDecisions.OwnerPort;
            Settle();

            var hint = window.FindControl<TextBlock>("PhonePortNoteText")!.Text ?? string.Empty;

            Assert.Contains(ServerDecisions.OwnerPort.ToString(CultureInfo.InvariantCulture), hint, StringComparison.Ordinal);
            Assert.True(PhoneGate.PortForbidden(ServerDecisions.OwnerPort));
            Assert.False(PhoneGate.PortAllowed(ServerDecisions.OwnerPort));

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ---------------------------------------------------------------- код сопряжения

    /// <summary>
    /// «ПОКАЗАТЬ КОД» ПОКАЗЫВАЕТ ШЕСТЬ ЦИФР И СЛОВА О СРОКЕ — крупной строкой. Код диктуют
    /// с экрана на телефон: без слов человек не поймёт, что с ним делать и сколько у него времени.
    /// </summary>
    [AvaloniaFact]
    public void Показать_код_показывает_шесть_цифр_и_слова_о_сроке()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var phone = new FakePhone { Allowed = true, Listening = true, Live = false, Code = "012345" };
            var window = Stand(dir, phone, new[] { Wired("192.168.0.5") });

            var button = window.FindControl<Button>("PhoneCodeButton");
            Assert.NotNull(button);
            Assert.Equal(PanelStrings.PhoneShowCodeButton, button!.Content);

            Click(button);
            Settle();

            Assert.Equal(1, phone.Issues);
            Assert.True(phone.Live);

            var text = window.FindControl<TextBlock>("PhoneCodeText")!;

            Assert.True(text.IsVisible, "код не показан человеку");
            Assert.Equal("012345", window.PhoneCode);
            Assert.Equal(6, window.PhoneCode.Length);
            Assert.All(window.PhoneCode, symbol => Assert.True(char.IsDigit(symbol), "в коде не цифра"));

            Assert.Contains("012345", text.Text ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(PhoneDecisions.PairingCodeText("012345"), text.Text);
            Assert.Contains(
                ((int)PhoneDecisions.CodeLifetime.TotalMinutes).ToString(CultureInfo.InvariantCulture),
                text.Text ?? string.Empty,
                StringComparison.Ordinal);

            // Крупно: строка кода заметно крупнее обычного текста раздела.
            Assert.True(text.FontSize >= 24, $"код показан мелко: {text.FontSize}");

            // И ЖИВОЙ КОД НЕ ВЫДАЁТСЯ ЗАНОВО: кнопка называет другое действие, а не берёт второй код.
            Assert.Equal(PanelStrings.PhoneShowCodeAgainButton, button.Content);

            Click(button);
            Settle();

            Assert.Equal(1, phone.Issues);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// БЕЗ ЖИВОГО КОДА ПУСТОТЫ НЕТ: кнопка берёт НОВЫЙ код и показывает его. Пустая строка вместо
    /// кода читалась бы как «панель сломалась», и человек не знал бы, что делать.
    /// </summary>
    [AvaloniaFact]
    public void Без_живого_кода_пустоты_нет()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var phone = new FakePhone { Allowed = true, Listening = true, Live = false, Code = "987654" };
            var window = Stand(dir, phone, new[] { Wired("192.168.0.5") });

            var text = window.FindControl<TextBlock>("PhoneCodeText")!;

            Assert.False(text.IsVisible, "код не просили — показывать нечего");

            // Кнопка называется «показать код» (а не «показать снова») и НЕ показывает пустоту.
            Assert.Equal(
                PanelStrings.PhoneShowCodeButton,
                window.FindControl<Button>("PhoneCodeButton")!.Content);

            Click(window.FindControl<Button>("PhoneCodeButton")!);
            Settle();

            Assert.True(text.IsVisible);
            Assert.False(string.IsNullOrWhiteSpace(text.Text), "показана пустота вместо кода");
            Assert.Contains("987654", text.Text ?? string.Empty, StringComparison.Ordinal);

            // Отказ двери (калитки нет) — тоже слова, а не пустое поле.
            var silent = new FakePhone { Allowed = true, Code = string.Empty };
            var other = Stand(dir, silent, new[] { Wired("192.168.0.5") });

            Click(other.FindControl<Button>("PhoneCodeButton")!);
            Settle();

            Assert.Equal(string.Empty, other.PhoneCode);
            Assert.Equal(
                PanelStrings.PhoneCodeFailed,
                other.FindControl<TextBlock>("PhoneCodeNoteText")!.Text);

            Close(other);
            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ---------------------------------------------------------------- список устройств

    /// <summary>
    /// СПИСОК ПОКАЗЫВАЕТ «КОГДА ПОДКЛЮЧИЛСЯ» И «КОГДА ВИДЕЛИ», И У КАЖДОГО ЕСТЬ «ОТВЯЗАТЬ».
    /// Одно «когда» вместо двух не ответило бы на вопрос «телефон жив или отвалился».
    /// </summary>
    [AvaloniaFact]
    public void Список_устройств_показывает_когда_и_у_каждого_есть_отвязать()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var phone = new FakePhone { Allowed = true, Listening = true };

            // ⚠️ ОТМЕТКИ — МАШИННОЙ СТРОКОЙ, КАК ИХ ПИШЕТ ПАНЕЛЬ (`PricingDecisions.Stamp`),
            // а НЕ «уже красивыми»: иначе проверка мерила бы свои же образцы,
            // и живой дефект 14.10.2026 (сырая отметка уходила на экран) в таком стенде не виден вовсе.
            var addedAt = PricingDecisions.Stamp(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(3)));
            var lastSeen = PricingDecisions.Stamp(new DateTimeOffset(2026, 1, 1, 10, 5, 0, TimeSpan.FromHours(3)));

            phone.Devices.Add(new PhoneDeviceView("phone-1", addedAt, lastSeen, "Pixel 8"));
            phone.Devices.Add(new PhoneDeviceView("phone-2", string.Empty, string.Empty));

            var window = Stand(dir, phone, new[] { Wired("192.168.0.5") });

            var panel = window.FindControl<StackPanel>("PhoneDevicesPanel");
            Assert.NotNull(panel);

            var rows = panel!.Children.OfType<DockPanel>().ToList();
            Assert.Equal(2, rows.Count);

            var labels = rows
                .Select(row => row.Children.OfType<TextBlock>().Single().Text ?? string.Empty)
                .ToList();

            // ⚠️ ЧЕЛОВЕКУ НАЗВАНО КОРОТКОЕ ИМЯ ТЕЛЕФОНА, А НЕ ВНУТРЕННИЙ `phone-N`
            // (решение владельца 12.10.2026). Телефон себя не назвал — честное «телефон».
            Assert.Contains("Pixel 8", labels[0], StringComparison.Ordinal);

            // И ОТМЕТКИ — ФОРМОЙ ПАНЕЛИ, А НЕ СЫРОЙ СТРОКОЙ ЗАПИСИ: та же дверь,
            // что у «Разрешение выдано» и у истории цен.
            Assert.Contains(PricingDecisions.CheckedText(addedAt), labels[0], StringComparison.Ordinal);
            Assert.Contains(PricingDecisions.CheckedText(lastSeen), labels[0], StringComparison.Ordinal);
            Assert.DoesNotContain("T", labels[0], StringComparison.Ordinal);
            Assert.Contains(PanelStrings.PhonePageDeviceFallback, labels[1], StringComparison.Ordinal);

            // И ВНУТРЕННЕГО НОМЕРА В СПИСКЕ НЕТ НИ У ОДНОГО: он остался в записи панели.
            Assert.All(labels, label => Assert.DoesNotContain("phone-", label, StringComparison.Ordinal));

            // У КАЖДОГО своя кнопка «Отвязать», и она называет одно устройство.
            var buttons = rows
                .Select(row => row.Children.OfType<Button>().Single())
                .ToList();

            Assert.Equal(2, buttons.Count);
            Assert.All(buttons, button => Assert.Equal(PanelStrings.PhoneForgetDeviceButton, button.Content));

            // Число устройств сказано словами, и пустого места в разделе нет.
            Assert.Equal(
                string.Format(CultureInfo.CurrentCulture, PanelStrings.PhoneDevicesCountFormat, 2),
                window.FindControl<TextBlock>("PhoneDevicesCountText")!.Text);
            Assert.False(window.FindControl<TextBlock>("PhoneDevicesEmptyText")!.IsVisible);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// РАЗДЕЛ «ТЕЛЕФОН» ОБНОВЛЯЕТСЯ ПРИ ВХОДЕ, А НЕ ТОЛЬКО ОТ НАШИХ НАЖАТИЙ.
    ///
    /// **От какого случая (решение владельца 12.10.2026).** `ShowSection` только переключал
    /// видимость, часов в окне не было вовсе, а `RenderPhoneSection` звался при загрузке и по
    /// действиям ВНУТРИ панели. **Телефон сопрягается СНАРУЖИ** — и человек, открыв раздел,
    /// видел «устройств нет», пока не тронет что-нибудь руками: правда о работе менялась не от
    /// наших нажатий. Это дефект шва, а не мелочь.
    ///
    /// Мутации, которые обязаны упасть здесь: «`ShowSection` не обновляет раздел» ·
    /// «обновление только по нажатиям внутри панели» · «часы заводятся, но не при входе».
    /// </summary>
    [AvaloniaFact]
    public void Раздел_телефон_обновляется_при_входе_а_не_только_по_нажатиям()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var phone = new FakePhone { Allowed = true, Listening = true };

            // ОКНО ПОСТРОЕНО БЕЗ УСТРОЙСТВ — как у человека, который телефон ещё не сопрягал.
            var window = Stand(dir, phone, new[] { Wired("192.168.0.5") });

            Assert.True(
                window.FindControl<TextBlock>("PhoneDevicesEmptyText")!.IsVisible,
                "на пустом списке раздел не сказал словами, что устройств нет");

            // А ТЕПЕРЬ ТЕЛЕФОН СОПРЯГСЯ СНАРУЖИ — панели об этом никто не нажимал.
            phone.Devices.Add(new PhoneDeviceView("phone-1", "01.01.2026, 10:00", "01.01.2026, 10:05", "Pixel 8"));

            Assert.Empty(window.FindControl<StackPanel>("PhoneDevicesPanel")!.Children);

            // ЧЕЛОВЕК ВХОДИТ В РАЗДЕЛ — и видит правду СРАЗУ, без единого нажатия.
            Assert.Equal(SettingsWindow.PhoneSection, window.ShowSection(SettingsWindow.PhoneSection));
            Settle();

            Assert.Single(window.FindControl<StackPanel>("PhoneDevicesPanel")!.Children);
            Assert.False(window.FindControl<TextBlock>("PhoneDevicesEmptyText")!.IsVisible);

            // И ЭТО ЖЕ ВИДНО ПО ДРУГОЙ ДВЕРИ: уход в соседний раздел и возврат тоже обновляет.
            phone.Devices.Add(new PhoneDeviceView("phone-2", "02.01.2026, 11:00", "02.01.2026, 11:07"));

            window.ShowSection(SettingsWindow.BackupSection);
            Settle();

            window.ShowSection(SettingsWindow.PhoneSection);
            Settle();

            Assert.Equal(2, window.FindControl<StackPanel>("PhoneDevicesPanel")!.Children.Count);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// «ОТВЯЗАТЬ» УБИРАЕТ **ОДНО** УСТРОЙСТВО И НЕ ТРОГАЕТ ОСТАЛЬНЫЕ. Отвязать всё можно
    /// рубильником; здесь человек назвал одно, и потерять вместе с ним соседа — это дефект.
    /// </summary>
    [AvaloniaFact]
    public void Отвязать_убирает_одно_устройство()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var phone = new FakePhone { Allowed = true, Listening = true };

            // ОТМЕТКИ МАШИННОЙ ФОРМОЙ ЗАПИСИ — так их и пишет панель.
            var secondAdded = PricingDecisions.Stamp(new DateTimeOffset(2026, 1, 2, 11, 0, 0, TimeSpan.FromHours(3)));
            var secondSeen = PricingDecisions.Stamp(new DateTimeOffset(2026, 1, 2, 11, 7, 0, TimeSpan.FromHours(3)));

            phone.Devices.Add(new PhoneDeviceView("phone-1", string.Empty, string.Empty, "Pixel 8"));
            phone.Devices.Add(new PhoneDeviceView("phone-2", secondAdded, secondSeen));

            var window = Stand(dir, phone, new[] { Wired("192.168.0.5") });

            var first = window.FindControl<StackPanel>("PhoneDevicesPanel")!
                .Children.OfType<DockPanel>().First()
                .Children.OfType<Button>().Single();

            Click(first);
            Settle();

            Assert.Equal(new[] { "phone-1" }, phone.Forgotten);
            Assert.Single(phone.Devices);
            Assert.Equal("phone-2", phone.Devices[0].Id);

            var labels = window.FindControl<StackPanel>("PhoneDevicesPanel")!
                .Children.OfType<DockPanel>()
                .Select(row => row.Children.OfType<TextBlock>().Single().Text ?? string.Empty)
                .ToList();

            Assert.Single(labels);

            // Осталось ВТОРОЕ устройство, и оно названо человеку честно: имени нет — «телефон».
            // ⚠️ Ищем НЕ по внутреннему номеру: `phone-2` в списке не показывается вовсе
            // (решение владельца 12.10.2026) — сверяем по «когда подключился» и по «нет номера».
            Assert.Contains(PricingDecisions.CheckedText(secondAdded), labels[0], StringComparison.Ordinal);
            Assert.DoesNotContain("T", labels[0], StringComparison.Ordinal);
            Assert.Contains(PanelStrings.PhonePageDeviceFallback, labels[0], StringComparison.Ordinal);
            Assert.DoesNotContain("phone-", labels[0], StringComparison.Ordinal);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }


    /// <summary>
    /// СЫРАЯ ОТМЕТКА НА ЭКРАН НЕ УХОДИТ, А ОДИН МИГ ПЕЧАТАЕТСЯ ОДИН РАЗ
    /// (находка владельца 14.10.2026, ночь).
    ///
    /// **Что прочитал владелец.** «SM-F926B · подключился: 2026-10-13T21:18:17.6132318+00:00 ·
    /// видели: 2026-10-13T21:18:17.6132318+00:00» — машинная строка записи пропуска как есть,
    /// время в UTC (на три часа раньше его часов) и один и тот же миг, напечатанный дважды.
    ///
    /// **Почему этого не увидела ни одна проверка.** Стенды брали устройства из образцов
    /// (`Shell\ShotSamples.cs`), где строки написаны руками уже в хорошем виде, — а живая запись
    /// приходит из `PhonePass.Safe()` строкой как есть. Здесь отметки — НАСТОЯЩЕЙ машинной
    /// формы, и проверка требует две вещи сразу: на экране форма панели
    /// (`PricingDecisions.CheckedText`) и ни одного следа сырой строки (`T`, смещение, доли секунд).
    ///
    /// Мутация, которая обязана упасть здесь: **вернуть в строку устройства `device.AddedAt`
    /// как есть** (`SettingsWindow.DeviceLine` без `CheckedText`) → в подписи появится `2026-10-13T21:18:17…`,
    /// и упадут обе половины: «форма панели» и «сырой строки нет».
    /// </summary>
    [AvaloniaFact]
    public void Сырая_отметка_устройства_на_экран_не_уходит()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var phone = new FakePhone { Allowed = true, Listening = true };

            // Один и тот же миг: так выглядит ТОЛЬКО ЧТО сопряжённый телефон —
            // именно его строку и читал владелец.
            var sameMoment = PricingDecisions.Stamp(
                new DateTimeOffset(2026, 10, 13, 21, 18, 17, TimeSpan.Zero));

            phone.Devices.Add(new PhoneDeviceView("phone-1", sameMoment, sameMoment, "SM-F926B"));

            var window = Stand(dir, phone, new[] { Wired("192.168.0.5") });

            var label = window.FindControl<StackPanel>("PhoneDevicesPanel")!
                .Children.OfType<DockPanel>().Single()
                .Children.OfType<TextBlock>().Single().Text ?? string.Empty;

            // (1) ФОРМА ПАНЕЛИ И МЕСТНОЕ ВРЕМЯ — та же дверь, что у «Разрешение выдано».
            var expected = PricingDecisions.CheckedText(sameMoment);
            Assert.False(string.IsNullOrEmpty(expected), "отметка не разобралась — мерить нечего");
            Assert.Contains(expected, label, StringComparison.Ordinal);

            // (2) И НИ ОДНОГО СЛЕДА СЫРОЙ СТРОКИ: ни `T` между датой и временем, ни смещения,
            // ни секунд с долями. Это и есть то, что человек увидел на экране.
            Assert.DoesNotContain("T21:18:17", label, StringComparison.Ordinal);
            Assert.DoesNotContain("+00:00", label, StringComparison.Ordinal);
            Assert.DoesNotContain(".6132318", label, StringComparison.Ordinal);

            // (3) ОДИН МИГ — ОДНА ОТМЕТКА, и сказано, что это одно событие: две одинаковые
            // даты рядом читаются как две разные величины (норма банка о единой форме даты).
            var marks = label.Split(expected, StringSplitOptions.None).Length - 1;
            Assert.Equal(1, marks);
            Assert.Contains("виден", label, StringComparison.Ordinal);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПУСТОЙ СПИСОК ГОВОРИТ СЛОВАМИ, что телефон ещё не подключался. Пустое место человек
    /// прочитал бы как «список не догрузился».
    /// </summary>
    [AvaloniaFact]
    public void Пустой_список_говорит_словами()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakePhone { Allowed = true, Listening = true }, new[] { Wired("192.168.0.5") });

            var empty = window.FindControl<TextBlock>("PhoneDevicesEmptyText")!;

            Assert.True(empty.IsVisible, "пустой список молчит");
            Assert.Equal(PanelStrings.PhoneDevicesEmpty, empty.Text);
            Assert.Empty(window.FindControl<StackPanel>("PhoneDevicesPanel")!.Children);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ---------------------------------------------------------------- адрес

    /// <summary>
    /// ОДИН ПОДХОДЯЩИЙ АДРЕС — ПОКАЗАН, И ВЫБИРАТЬ НЕЧЕГО.
    /// </summary>
    [AvaloniaFact]
    public void Один_кандидат_показан_без_выбора()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakePhone(), new[] { Wired("192.168.0.5") });

            var text = window.FindControl<TextBlock>("PhoneAddressText")!;
            var combo = window.FindControl<ComboBox>("PhoneAddressCombo")!;

            Assert.True(text.IsVisible, "адрес не показан человеку");
            Assert.Contains("192.168.0.5", text.Text ?? string.Empty, StringComparison.Ordinal);
            Assert.False(combo.IsVisible, "выбирать не из чего — списка быть не должно");
            Assert.False(window.FindControl<TextBlock>("PhoneAddressNoText")!.IsVisible);

            // Замечание о смене адреса стоит рядом: адрес выдан по DHCP и может измениться.
            Assert.Equal(
                PanelStrings.PhoneAddressNote,
                window.FindControl<TextBlock>("PhoneAddressNoteText")!.Text);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// НЕСКОЛЬКО ПОДХОДЯЩИХ — ПОКАЗАНЫ ВСЕ И ЕСТЬ ВЫБОР, а выбор человека запоминается дверью.
    /// Решение владельца дословно: «гадать нельзя». Мутация «несколько адресов решаются как один»
    /// падает здесь: она показала бы один адрес вместо списка.
    /// </summary>
    [AvaloniaFact]
    public void Несколько_кандидатов_показаны_все_и_есть_выбор()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var phone = new FakePhone();
            var window = Stand(
                dir,
                phone,
                new[] { Wired("192.168.0.5"), Wired("10.0.0.7", metric: 30) });

            var combo = window.FindControl<ComboBox>("PhoneAddressCombo")!;
            var text = window.FindControl<TextBlock>("PhoneAddressText")!;

            Assert.True(combo.IsVisible, "выбор есть, а списка нет");
            Assert.Equal(new[] { "192.168.0.5", "10.0.0.7" }, combo.ItemsSource!.Cast<string>().ToArray());
            Assert.Contains("2", text.Text ?? string.Empty, StringComparison.Ordinal);

            // ЧЕЛОВЕК ВЫБИРАЕТ — и выбор уходит двери, а не остаётся в окне.
            combo.SelectedIndex = 1;
            Settle();

            Assert.Equal(new[] { "10.0.0.7" }, phone.Chosen2);
            Assert.Equal("10.0.0.7", phone.Chosen);

            // И ПОСЛЕ ВЫБОРА С ЭКРАНА НЕ ПРОПАДАЕТ, ЧТО ОН ВЫБРАЛ.
            Assert.Contains("10.0.0.7", text.Text ?? string.Empty, StringComparison.Ordinal);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// НИ ОДНОГО ПОДХОДЯЩЕГО — СКАЗАНО СЛОВАМИ. Выдуманный адрес хуже его отсутствия: человек
    /// пошёл бы с телефоном туда, где панели нет.
    /// </summary>
    [AvaloniaFact]
    public void Ни_одного_кандидата_сказано_словами()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            // Поднятого физического адаптера нет: один выключен, второй — петля.
            var window = Stand(
                dir,
                new FakePhone(),
                new[]
                {
                    Wired("192.168.0.5", up: false),
                    new RawAddress("Loopback", "Loopback", true, "127.0.0.1", "255.0.0.0", 25, true),
                });

            var text = window.FindControl<TextBlock>("PhoneAddressText")!;
            var none = window.FindControl<TextBlock>("PhoneAddressNoText")!;

            Assert.True(none.IsVisible, "пусто — а человеку не сказано");
            Assert.Equal(PanelStrings.PhoneAddressNoAddresses, none.Text);
            Assert.False(window.FindControl<ComboBox>("PhoneAddressCombo")!.IsVisible);

            // И адреса на экране нет ни одного: показывать «ничего» словами — не то же самое,
            // что показать выдуманный адрес.
            Assert.DoesNotContain("127.0.0.1", text.Text ?? string.Empty, StringComparison.Ordinal);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// АДРЕС ТУННЕЛЯ В КАНДИДАТЫ НЕ ПОПАДАЕТ, ХОТЯ У НЕГО МЕТРИКА НИЖЕ.
    ///
    /// ⚠️ ЭТО ЛОВУШКА ЗАМЕРА (<c>docs\PHONE-F0.md</c> §7.2), а не придирка: у туннеля WARP
    /// метрика **5**, у домашнего Ethernet — **25**. Правило «взять меньшую метрику» выбрало бы
    /// туннель, то есть адрес, которого телефон в своей сети не увидит. Мутация «в кандидаты попал
    /// туннель» падает здесь.
    /// </summary>
    [AvaloniaFact]
    public void Адрес_туннеля_с_меньшей_метрикой_в_кандидаты_не_попадает()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(
                dir,
                new FakePhone(),
                new[] { Tunnel("172.16.0.9", metric: 5), Wired("192.168.0.5", metric: 25) });

            var text = window.FindControl<TextBlock>("PhoneAddressText")!.Text ?? string.Empty;

            Assert.Contains("192.168.0.5", text, StringComparison.Ordinal);
            Assert.DoesNotContain("172.16.0.9", text, StringComparison.Ordinal);
            Assert.False(window.FindControl<ComboBox>("PhoneAddressCombo")!.IsVisible, "туннель попал в выбор");

            // И то же самое — на чистом правиле, без окна: туннель негоден, как бы ни был поднят.
            var picked = PhoneDecisions.PickAddress(new[]
            {
                new Address("CloudflareWARP", "172.16.0.9", AddressKind.Tunnel, OnLink: true),                new Address("Ethernet", "192.168.0.5", AddressKind.Physical, OnLink: true),
            });

            Assert.Equal(AddressChoice.One, picked.Choice);
            Assert.Equal("192.168.0.5", picked.Chosen!.Ip);
            Assert.Single(picked.Candidates);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ---------------------------------------------------------------- строка меню значка

    /// <summary>
    /// СТРОКА МЕНЮ ЗНАЧКА: НЕ РАЗРЕШЁН — СТРОКИ НЕТ; РАЗРЕШЁН — «ЖДЁТ»; ПОДКЛЮЧЁН — «ПОДКЛЮЧЁН».
    ///
    ///⚠️ Проверяется ЧЕРЕЗ МЕНЮ (<see cref="PanelMenu.Build"/>), а не только предикатом: строка,
    /// решённая верно и не попавшая в меню, человеку не видна, и «зелёный» предикат ничего бы
    /// не доказывал. Мутация «строка меню есть и когда телефон не разрешён» падает здесь.
    /// </summary>
    [Fact]
    public void Строка_меню_значка_зависит_от_разрешения_и_связи()
    {
        static (TrayMenu Menu, TrayStatusLines Lines) Build(bool allowed, bool connected)
        {
            var lines = TrayStatus.Build(
                connected: true,
                presence: ServerPresence.Running,
                owner: ServerOwner.Panel,
                port: 3080,
                agentTitle: "DeepSeek",
                balanceSummary: "1 $",
                inPeak: false,
                update: new TrayStatusLine("обновление", TrayTone.Neutral),
                phone: PhoneDecisions.TrayLine(allowed, connected));

            var menu = PanelMenu.Build(
                status: () => lines,
                show: () => { },
                openAgent: () => { },
                settings: () => { },
                about: () => { },
                checkUpdate: () => { },
                updateSection: () => { },
                exit: () => { });

            return (menu, lines);
        }

        // НЕ РАЗРЕШЁН — СТРОКИ НЕТ ВОВСЕ (ни текста, ни пустого места).
        var (off, offLines) = Build(false, false);

        Assert.False(PhoneDecisions.TrayLineShown(false));
        Assert.Equal(string.Empty, offLines.Phone.Text);
        Assert.False(offLines.HasPhone);
        Assert.DoesNotContain(off.Items, item => string.IsNullOrEmpty(item.Text) && !item.IsSeparator);

        // РАЗРЕШЁН И НИКОГО НЕТ — «ждёт подключения».
        var (waiting, waitingLines) = Build(true, false);

        Assert.True(PhoneDecisions.TrayLineShown(true));
        Assert.Equal(PhoneTrayState.Waiting, PhoneDecisions.TrayState(false));
        Assert.Equal(PanelStrings.PhoneTrayWaiting, waitingLines.Phone.Text);
        Assert.Equal(waitingLines.Phone.Text, waiting.Items[4].Text);

        // ПОДКЛЮЧЁН — «подключён», и это ДРУГОЙ текст.
        var (live, liveLines) = Build(true, true);

        Assert.Equal(PhoneTrayState.Connected, PhoneDecisions.TrayState(true));
        Assert.Equal(PanelStrings.PhoneTrayConnected, liveLines.Phone.Text);
        Assert.NotEqual(waitingLines.Phone.Text, liveLines.Phone.Text);
        Assert.Equal(liveLines.Phone.Text, live.Items[4].Text);
    }

    // ---------------------------------------------------------------- кадр и сохранение

    /// <summary>
    /// КАДР РАЗДЕЛА «ТЕЛЕФОН» СНИМАЕТСЯ И НЕ ПУСТ. Кадр уезжает в README, поэтому в нём
    /// не должно быть ни пропуска, ни адреса машины, — а вот разные цвета обязаны быть:
    /// одноцветный лист означал бы «окно не нарисовано», и проверка «файл создан» этого не заметила бы.
    /// </summary>
    [AvaloniaFact]
    public void Кадр_раздела_телефона_снимается_и_не_пуст()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakePhone { Allowed = true, Listening = true }, new[] { Wired("192.168.0.5") });

            var frame = PanelTestStand.SaveFrame(window, "phone-section-" + Guid.NewGuid().ToString("N")[..8]);

            Assert.True(File.Exists(frame), "кадр не сохранён: " + frame);
            Assert.True(new FileInfo(frame).Length > 0, "кадр пустой файл");
            Assert.True(PanelTestStand.ColorsIn(window) > 2, "кадр одноцветный — окно не нарисовано");

            // В кадре — слова раздела, а не пропуск и не адрес: ни значения пропуска у окна нет
            // вовсе, ни адреса, кроме показанного правилом.
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? string.Empty).ToList();

            Assert.Contains(texts, text => text == PanelStrings.PhoneSectionTitle);
            Assert.Contains(texts, text => text.Contains(PanelStrings.PhoneAddressNote, StringComparison.Ordinal));

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// В КАДРЕ НЕТ НОМЕРА АДРЕСА МАШИНЫ: последнее число заменяется на <c>х</c>
    /// (решение владельца 11.10.2026). Кадр уезжает в README, и примета машины в нём лишняя;
    /// сторож личных данных адреса не видит вовсе, поэтому маска — единственная защита.
    ///
    /// ⚠️ Проверяется ФОРМА, а не «похоже»: адрес подставлен свой (подстановка — работа продукта,
    /// <see cref="RealNetworkAddresses.Test"/>), и в кадре обязано стоять <c>192.168.0.х</c>,
    /// а не <c>192.168.0.5</c>.
    /// </summary>
    [AvaloniaFact]
    public void В_кадре_нет_номера_адреса_машины()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            // Так кадр и собирается: адреса приходят настоящие (у проверки — подставные),
            // а на ПОКАЗ ложится маска (в съёмке — `Program.MaskAddress`).
            RealNetworkAddresses.Test = new[] { Wired("192.168.0.7") };

            var window = PanelTestStand.SettingsStand(dir, phone: new FakePhone());
            window.AttachAddressMaskForTests(ip =>
            {
                var parts = ip.Split('.');

                return parts.Length == 4 ? parts[0] + "." + parts[1] + "." + parts[2] + ".х" : "х.х.х.х";
            });

            window.ShowSection(SettingsWindow.PhoneSection);
            window.Show();
            Settle();

            var text = window.FindControl<TextBlock>("PhoneAddressText")!.Text ?? string.Empty;

            Assert.Contains("192.168.0.х", text, StringComparison.Ordinal);
            Assert.DoesNotContain("192.168.0.7", text, StringComparison.Ordinal);

            // И признак «подсеть ведёт напрямую» на месте: маска не должна стирать смысл строки.
            Assert.Contains(PanelStrings.PhoneAddressOnLink, text, StringComparison.Ordinal);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// СОХРАНЕНИЕ НАСТРОЕК НЕ ТЕРЯЕТ ТЕЛЕФОННЫЕ ПОЛЯ. Окно пишет файл ЦЕЛИКОМ, и поле, которого
    /// оно не знает, обнулялось бы каждым нажатием «Сохранить»: человек, тронувший любую настройку
    /// в другом разделе, молча терял бы и разрешение, и запомненный адрес, и время согласия.
    /// Ловушка та же, что была с согласием на сервер и таблицей цен.
    /// </summary>
    [AvaloniaFact]
    public void Сохранение_не_теряет_телефонные_поля()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var (window, settings, paths) = PanelTestStand.SettingsWithOwner(dir, phone: new FakePhone());

            // Человек уже настроил телефон раньше: файл полон телефонных полей.
            var seeded = SettingsStore.Clean(new PanelSettings
            {
                PhoneEnabled = true,
                PhonePort = 43210,
                PhonePortChosen = true,
                PhoneLastAddress = "192.168.0.77",
                PhoneChosenAddress = "192.168.0.78",
                PhoneLastSeen = PricingDecisions.Stamp(DateTimeOffset.Now.AddHours(-2)),
                PhoneConsentAt = PricingDecisions.Stamp(DateTimeOffset.Now.AddDays(-1)),
            });

            Assert.True(settings.Save(seeded), "подготовка проверки не записалась");
            Assert.True(File.Exists(paths.SettingsFile));

            // НОВОЕ ОКНО: раздел «Телефон» не трогали, поменяли рабочую папку и нажали «Сохранить».
            var fresh = PanelTestStand.SettingsStand(dir, phone: new FakePhone());
            fresh.Show();
            Settle();

            fresh.FindControl<TextBox>("WorkDirBox")!.Text = Path.Combine(dir, "рабочая папка");
            Click(fresh.FindControl<Button>("SaveButton")!);
            Settle();

            Assert.True(fresh.Saved, "«Сохранить» не сработало");

            var back = new SettingsStore(paths.SettingsFile).Load().Settings;

            Assert.True(back.PhoneEnabled, "разрешение на телефон потеряно сохранением");
            Assert.Equal(43210, back.PhonePort);
            Assert.True(back.PhonePortChosen);
            Assert.Equal("192.168.0.77", back.PhoneLastAddress);
            Assert.Equal("192.168.0.78", back.PhoneChosenAddress);
            Assert.Equal(seeded.PhoneLastSeen, back.PhoneLastSeen);
            Assert.Equal(seeded.PhoneConsentAt, back.PhoneConsentAt);

            fresh.CloseQuietly();
            window.CloseQuietly();
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ВЫБОР АДРЕСА, СДЕЛАННЫЙ ЧЕЛОВЕКОМ, ОСТАЁТСЯ ВЫБРАННЫМ И ПОСЛЕ ПЕРЕРИСОВКИ: окно
    /// показывает не «первый из списка», а его решение. Обратное выглядело бы как «панель забыла».
    /// </summary>
    [AvaloniaFact]
    public void Выбор_адреса_виден_в_списке_после_перерисовки()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var phone = new FakePhone();
            var window = Stand(dir, phone, new[] { Wired("192.168.0.5"), Wired("10.0.0.7") });

            var combo = window.FindControl<ComboBox>("PhoneAddressCombo")!;

            combo.SelectedIndex = 1;
            Settle();

            // Человек ушёл в другой раздел и вернулся: выбор обязан быть на месте.
            window.ShowSection(0);
            Settle();
            window.ShowSection(SettingsWindow.PhoneSection);
            Settle();

            Assert.Equal("10.0.0.7", combo.SelectedItem);
            Assert.Equal(1, combo.SelectedIndex);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    // ---------------------------------------------------------------- QR (хвост 1 этапа F1)

    /// <summary>
    /// ШИРИНА И ВЫСОТА КАРТИНКИ ИЗ IHDR — единственный кусок PNG, где они записаны.
    ///
    /// ⚠️ Проверка читает ИМЕННО ФОРМАТ, а не «размер файла»: «байты на месте» ничего не говорят
    /// о том, что внутри картинка. Так «нарисован ли QR» проверяется БЕЗ библиотеки и без камеры.
    /// </summary>
    private static (int Width, int Height) PngSize(byte[] png)
    {
        Assert.True(png.Length > 24, $"PNG короче заголовка: {png.Length} байт");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);

        int Read(int at) => (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];

        // IHDR начинается с 8-го байта: длина (4) + тип (4), ширина и высота — за ними.
        return (Read(16), Read(20));
    }

    /// <summary>Слова семи цифр кода — «123456» и соседи: их в QR быть не должно.</summary>
    private static readonly string[] CodeWords =
        { "123456", "654321", "012345", "987654", "111111", "000000", "999999" };

    /// <summary>
    /// QR НАРИСОВАН, КОГДА АДРЕС ЕСТЬ, И В НЁМ РОВНО ПОКАЗАННЫЙ АДРЕС.
    ///
    /// **Ради этого проверка и заведена.** Правило проекта одно и оно проверяемо: *QR кодирует
    /// ровно ту строку, которую раздел показывает как адрес* (`PhoneQr.PanelsQrRule`). Проверка
    /// берёт строку С ЭКРАНА и строку, отданную двери, и требует их равенства — сойдись они
    /// порознь (в QR один адрес, на экране другой) — человек навёл бы камеру на одну машину,
    /// а панель ждала бы телефон на другой.
    ///
    /// Мутации, которые обязаны упасть здесь: «QR не рисуется» · «QR кодирует чужой адрес» ·
    /// «QR кодирует выдуманный адрес» · «адрес в QR взят до маски».
    /// </summary>
    [AvaloniaFact]
    public void QR_нарисован_и_кодирует_ровно_показанный_адрес()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            // АДРЕС «ПО КОТОРОМУ ТЕЛЕФОН ПРИХОДИЛ РАНЬШЕ» ЗАВЕДОМО ДРУГОЙ. Он лежит рядом в том же
            // ответе раздела, и соблазн взять его вместо показанного настоящий: он «настоящий», а не
            // подставной. Но человеку показан ДРУГОЙ — и наводить камеру он будет на показанный.
            var phone = new FakePhone { LastAddress = "203.0.113.9" };
            var window = Stand(dir, phone, new[] { Wired("192.168.0.5") });

            // (1) СТРОКА С ЭКРАНА: то, что человек видит и по чему набирает адрес руками.
            var shown = window.FindControl<TextBlock>("PhoneAddressText")!;

            Assert.True(shown.IsVisible, "адрес не показан — проверять кодирование нечего");
            Assert.Contains("192.168.0.5", shown.Text ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(phone.LastAddress, shown.Text ?? string.Empty, StringComparison.Ordinal);

            // (2) ДВЕРЬ ПОЗВАЛИ, и ровно эту строку: не «примерно эту», а ту же самую. Спрашивать
            //     могут не раз (окно рисует раздел и при переключении), и это не дефект: важно,
            //     чтобы ЛЮБОЙ заданный вопрос был про тот же адрес, что стоит на экране.
            //
            // ⚠️ С 11.10.2026 В КОДЕ АДРЕС СО СХЕМОЙ И ПОРТОМ (решение владельца: камера обязана
            // ОТКРЫВАТЬ страницу, а не показывать текст). Правило «кодируется ровно показанное»
            // осталось — оно стало точнее: `http://` + показанный адрес + `:` + показанный порт.
            var wanted = PhoneDecisions.QrText("192.168.0.5", 43127);

            Assert.NotEmpty(phone.QrAsked);
            Assert.All(phone.QrAsked, asked => Assert.Equal(wanted, asked));

            var asked0 = phone.QrAsked[0];

            // Адрес с экрана — ВНУТРИ кода: строка показа и строка кода не разошлись.
            Assert.Contains("192.168.0.5", asked0, StringComparison.Ordinal);

            // И порт в коде — тот, который раздел показывает рядом (умолчание `43127`).
            Assert.Contains(":43127", asked0, StringComparison.Ordinal);

            // (2б) НЕ ПОДМЕНА: ни адреса, по которому телефон приходил раньше, ни «своего» порта.
            //      Мутация «в QR поехал последний виденный адрес» падает ЗДЕСЬ: `Null` ниже
            //      превратится в «в QR уехал `203.0.113.9`» — адрес, которого человеку не показывали.
            Assert.Null(phone.QrAsked.FirstOrDefault(asked => asked.Contains(phone.LastAddress, StringComparison.Ordinal)));

            // (3) КАРТИНКА НАРИСОВАНА И НЕ ПУСТА, и размеры у неё правдоподобные: квадрат,
            //     который человек увидит и на который наведёт камеру.
            var image = window.FindControl<Image>("PhoneQrImage")!;

            Assert.NotNull(image.Source);

            var bitmap = Assert.IsType<Bitmap>(image.Source);

            Assert.True(bitmap.PixelSize.Width >= 29, $"QR мельче одного модуля на точку: {bitmap.PixelSize}");
            Assert.Equal(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            Assert.True(bitmap.PixelSize.Width <= 1024, $"QR неправдоподобно велик: {bitmap.PixelSize}");
            Assert.True(
                image.Bounds.Width > 0 && image.Bounds.Height > 0,
                $"место под картинку не отведено: {image.Bounds}");

            // (4) И она НЕ «нарисована словами»: у неё настоящий PNG с настоящим квадратом внутри.
            var png = FakePhone.PngOf("192.168.0.5");
            var (width, height) = PngSize(png);

            Assert.True(png.Length > 100, $"PNG подозрительно короток: {png.Length} байт");
            Assert.True(width >= 29 && width == height, $"размеры PNG не те: {width}x{height}");

            // (5) НИЧЕГО КРОМЕ АДРЕСА: ни цифр кода сопряжения, ни пропуска.
            foreach (var word in CodeWords)
            {
                Assert.DoesNotContain(word, asked0, StringComparison.Ordinal);
            }

            // И сам код на экране есть — значит проверка «в QR его нет» смотрит на настоящее
            // состояние, а не на пустое место: показать код тут есть чем.
            Assert.Equal(6, phone.Code.Length);

            // (6) Пустой адрес не рисует ничего — и это тоже про правило, а не про вкус.
            Assert.Empty(FakePhone.PngOf(string.Empty));

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// НЕТ АДРЕСА — НЕТ И QR, А ЧЕЛОВЕКУ СКАЗАНО СЛОВАМИ, ПОЧЕМУ.
    ///
    /// Выдуманный код хуже отсутствия: человек пошёл бы с телефоном туда, где панели нет.
    /// Проверка требует ДВУХ вещей сразу — пустой картинки И слов: молчащая пустота читалась бы
    /// как «раздел не догрузился». Мутации «QR кодирует выдуманный адрес, когда настоящего нет»
    /// и «QR нарисован, хотя показывать нечего» падают здесь.
    /// </summary>
    [AvaloniaFact]
    public void Без_адреса_QR_не_нарисован_и_сказано_словами()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            // Поднятого физического адаптера нет: один выключен, второй — петля.
            var phone = new FakePhone();
            var window = Stand(
                dir,
                phone,
                new[]
                {
                    Wired("192.168.0.5", up: false),
                    new RawAddress("Loopback", "Loopback", true, "127.0.0.1", "255.0.0.0", 25, true),
                });

            Assert.True(window.FindControl<TextBlock>("PhoneAddressNoText")!.IsVisible);

            // (1) НИ ОДНОГО ВЫДУМАННОГО КОДА: дверь не позвали вовсе.
            Assert.Empty(phone.QrAsked);

            // (2) И картинки нет: пустое место вместо кода — правда, а не недоделка.
            Assert.Null(window.FindControl<Image>("PhoneQrImage")!.Source);

            // (3) А человеку СКАЗАНО СЛОВАМИ, почему кода нет.
            var note = window.FindControl<TextBlock>("PhoneQrNoText")!;

            Assert.True(note.IsVisible, "QR не нарисован — и об этом молчат");
            Assert.Equal(PanelStrings.PhoneQrNoAddress, note.Text);

            // (4) Признак маски — не выдумка окна: адреса нет вовсе, и решение говорит «ни одного».
            var picked = PhoneDecisions.PickAddressChosen(Array.Empty<Address>(), string.Empty);

            Assert.Equal(AddressChoice.None, picked.Choice);
            Assert.False(picked.Masked);

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// В QR НЕТ НИЧЕГО, КРОМЕ АДРЕСА: НИ ПРОПУСКА, НИ ЦИФР КОДА СОПРЯЖЕНИЯ.
    ///
    /// Решение владельца 10.10.2026: **шесть цифр сопряжения в код не кладутся** — человек вводит
    /// их руками. ⚠️ **Про порт решение ИЗМЕНЕНО 11.10.2026** (по живому прогону): в коде теперь
    /// адрес СО СХЕМОЙ И ПОРТОМ, чтобы камера открывала страницу; прежнее «в коде нет порта»
    /// отменено владельцем, и проверка сторожит ровно новое правило.
    ///
    /// Ради этого проверка и стоит отдельно от «адрес тот»: код ЖИВОЙ и лежит в том же разделе —
    /// значит соблазн «положить его в ссылку» настоящий, и он обязан падать.
    ///
    /// Мутации, которые обязаны упасть здесь: «в QR попали цифры кода» · «в QR уехала ссылка входа».
    /// </summary>
    [AvaloniaFact]
    public void В_QR_нет_ничего_кроме_адреса()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var phone = new FakePhone { Allowed = true, Listening = true, Code = "123456" };
            var window = Stand(dir, phone, new[] { Wired("192.168.0.5") });

            // Код ЖИВОЙ и показан — иначе проверка сторожила бы пустоту.
            Click(window.FindControl<Button>("PhoneCodeButton")!);
            Settle();

            Assert.Equal("123456", window.PhoneCode);

            Assert.NotEmpty(phone.QrAsked);
            Assert.All(phone.QrAsked, asked => Assert.Equal(PhoneDecisions.QrText("192.168.0.5", 43127), asked));

            var asked = phone.QrAsked[0];

            // (1) НИ ОДНОЙ ЦИФРЫ КОДА: ни живого, ни выдуманного соседнего.
            foreach (var word in CodeWords)
            {
                Assert.DoesNotContain(word, asked, StringComparison.Ordinal);
            }

            // И по цифрам целиком: в закодированной строке нет ШЕСТИ подряд идущих цифр — значит
            // кода сопряжения (он ровно из шести цифр) там нет. Сам код при этом живой: проверка
            // выше его показала, то есть соблазн «положить код в ссылку» здесь настоящий.
            Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"\d{6}"), asked);

            // (2) В КОДЕ — АДРЕС СО СХЕМОЙ И ПОРТОМ, и НИЧЕГО БОЛЬШЕ: хвоста ссылки входа,
            //     токена и чужих путей там нет.
            Assert.Equal("http://192.168.0.5:43127", asked);
            Assert.DoesNotContain("token", asked, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dsh", asked, StringComparison.OrdinalIgnoreCase);

            // (3) И слова о том, что цифры вводят руками, стоят рядом с картинкой — человеку
            //     неоткуда больше узнать, что код придётся набирать.
            var hint = window.FindControl<TextBlock>("PhoneQrNoteText")!;

            Assert.Equal(PanelStrings.PhoneQrNote, hint.Text);
            Assert.False(string.IsNullOrWhiteSpace(hint.Text));

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПОД МАСКОЙ КОД РИСУЕТСЯ — ИЗ ПОКАЗАННОЙ (ЗАМАСКИРОВАННОЙ) СТРОКИ, и об этом сказано словами.
    ///
    /// ⚠️ **Требование владельца 11.10.2026: «кадр `settings-phone` обязан показать код».** Оно
    /// и правило «QR кодирует ровно ту строку, что показана» сходятся в одну строку: под маской
    /// показывается `192.168.0.х` — обычный текст БЕЗ номера машины, и код рисуется ИЗ НЕГО
    /// (утечь из такой строки нечему). В обычном запуске та же строка — настоящий адрес, и код
    /// рабочий. Что в кадре код — **образец**, сказано словами: молчащий кадр читался бы как
    /// рабочий код, и человек пошёл бы с телефоном к замаскированному адресу.
    ///
    /// Мутации, которые обязаны упасть здесь: «под маской QR не рисуется» · «рисуется, но не из
    /// показанной строки» · «в кадре молчат, что код — образец» · «маска не доезжает до строки».
    /// </summary>
    [AvaloniaFact]
    public void Под_маской_код_рисуется_из_показанной_строки_и_сказано_словами()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var phone = new FakePhone();

            // Маска — ОДНИМ значением, как её и ставит `Program`: второй такой же лямбды в окне
            // нет, а сравнение «та же это маска или нет» иначе не сработало бы вовсе.
            Func<string, string> mask = ip =>
            {
                var parts = ip.Split('.');

                return parts.Length == 4 ? parts[0] + "." + parts[1] + "." + parts[2] + ".х" : "х.х.х.х";
            };

            // И уходит она в САМУ связку окна — как в съёмке: раздел ни разу не показывается
            // с настоящим адресом, а значит и в QR он попасть не может.
            var window = Stand(dir, phone, new[] { Wired("192.168.0.7") }, mask);

            // Так кадр и собирается: адрес показан замаскированным (решение владельца 11.10.2026).
            var shown = window.FindControl<TextBlock>("PhoneAddressText")!.Text ?? string.Empty;

            Assert.Contains("192.168.0.х", shown, StringComparison.Ordinal);

            // (1) КОД НАРИСОВАН — и ровно из ТОЙ строки, что показана: ни настоящего номера,
            //     ни досочинённых порта и схемы, ни цифр сопряжения.
            //     ⚠️ Просили код НЕ ОДИН РАЗ, и это правильно: раздел обновляется при входе
            //     и тихими часами, пока виден (решение владельца 12.10.2026), а каждый пересчёт
            //     перерисовывает код. Поэтому сверяется КАЖДЫЙ запрос, а не только первый:
            //     настоящий номер обязан не попасть НИ В ОДИН.
            Assert.NotEmpty(phone.QrAsked);
            Assert.All(phone.QrAsked, asked => Assert.Contains("192.168.0.х", asked, StringComparison.Ordinal));
            Assert.All(phone.QrAsked, asked => Assert.DoesNotContain("192.168.0.7", asked, StringComparison.Ordinal));

            // (2) И картинка на месте — видно по САМОМУ окну, а не по договорённости с дверью.
            var image = window.FindControl<Image>("PhoneQrImage")!;

            Assert.NotNull(image.Source);

            // (2а) НА МЕСТЕ КАРТИНКИ — БОЛЬШЕ ОДНОГО ЦВЕТА: код действительно нарисован, а не
            //      «картинка есть, но пустая». Спрятанный и несозданный код выглядят в коде
            //      одинаково, а человеку и README — по-разному.
            var where = image.TranslatePoint(default, window) ?? default;

            Assert.True(
                where.Y > 0 && where.Y + image.Bounds.Height < window.ClientSize.Height,
                $"место под QR не влезло в окно: {where.Y} + {image.Bounds.Height} из {window.ClientSize.Height}");

            var colors = PanelTestStand.ColorsIn(window, new Rect(where, image.Bounds.Size));

            Assert.True(colors > 1, $"на месте QR один цвет — кода не видно: разных цветов {colors}");

            // (3) И в кадре СКАЗАНО СЛОВАМИ, что код — образец: молчащий кадр читался бы как
            //     рабочий код, а человек пошёл бы с телефоном к замаскированному адресу.
            var note = window.FindControl<TextBlock>("PhoneQrNoText")!;

            Assert.True(note.IsVisible);
            Assert.Equal(PanelStrings.PhoneQrMasked, note.Text);

            // (4) ПРИЗНАК МАСКИ — решение домена, а не догадка окна по буквам в строке.
            //     ⚠️ На ОДНОМ адресе `Address` — это выбор человека (`Picked`), а маска ложится
            //     на ПОКАЗ (`Text()`): у человека выбора нет, и решает `Chosen`. Проверяются обе
            //     половины — иначе правило сторожило бы не тот путь, каким идёт окно.
            var one = PhoneDecisions.PickAddressChosen(
                new[] { new Address("Ethernet", "192.168.0.7", AddressKind.Physical, OnLink: true) },
                string.Empty,
                ip => ip[..ip.LastIndexOf('.')] + ".х");

            Assert.True(one.Masked);
            Assert.Contains("192.168.0.х", one.Text(), StringComparison.Ordinal);

            var several = PhoneDecisions.PickAddressChosen(
                new[]
                {
                    new Address("Ethernet", "192.168.0.7", AddressKind.Physical, OnLink: true),
                    new Address("Ethernet", "10.0.0.9", AddressKind.Physical, OnLink: true),
                },
                "192.168.0.7",
                ip => ip[..ip.LastIndexOf('.')] + ".х");

            Assert.True(several.Masked);
            Assert.Equal("192.168.0.х", several.Address);

            // (5) И ПРАВИЛО НАЗВАНО В КОДЕ СЛОВАМИ — не только проверкой.
            Assert.False(string.IsNullOrWhiteSpace(PhoneQr.PanelsQrRule));

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// СЛОВА О QR ПЕРЕВЕДЕНЫ НА ТРИ ЯЗЫКА, и все три — про одно и то же: <b>в коде только адрес,
    /// цифры вводят руками</b>. Второй формулировки у этого правила быть не должно.
    /// </summary>
    [Fact]
    public void Слова_о_QR_есть_на_трёх_языках()
    {
        foreach (var name in new[]
                 {
                     nameof(PanelStrings.PhoneQrTitle),
                     nameof(PanelStrings.PhoneQrNote),
                     nameof(PanelStrings.PhoneQrNoAddress),
                     nameof(PanelStrings.PhoneQrFailed),
                     nameof(PanelStrings.PhoneQrMasked),
                 })
        {
            foreach (var language in new[] { "ru", "en", "zh" })
            {
                var text = DshPanel.Localization.Loc.TIn(language, name);

                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}: пустая строка {name}");
                Assert.NotEqual(name, text);
            }
        }
    }

    /// <summary>
    /// СЛОВА О РУКАХ НАЗВАНЫ ТАМ, ГДЕ ИХ ПРОЧТУТ: подпись к QR говорит и про адрес в коде,
    /// и про шесть цифр, которые человек вводит САМ. Без вторых слов человек искал бы код
    /// сопряжения в картинке — а его там нет намеренно.
    /// </summary>
    [Fact]
    public void Слова_про_QR_называют_ручной_ввод_цифр()
    {
        var hint = PanelStrings.PhoneQrNote;

        // ⚠️ ЧИСЛО НАЗВАНО СЛОВОМ («шести цифр»), а не цифрой (S-12, пачка B): строка стала короче
        // и человечнее. Проверка держит СМЫСЛ — что названо ровно шесть цифр, — и сверяет само число
        // с решением кода (`PhoneDecisions.CodeLength`), а не с выдуманным литералом.
        Assert.Contains("шести", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6, PhoneDecisions.CodeLength);

        // Про адрес в коде — тоже сказано: иначе подпись обещала бы не то, что в картинке.
        Assert.Contains("адрес", hint, StringComparison.OrdinalIgnoreCase);

        // И сказано, что код набирает ЧЕЛОВЕК на телефоне: путь назван словами.
        Assert.Contains("наберите", hint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// КАРТИНКА И СЛОВА О НЕЙ — ВЫШЕ СГИБА, БЕЗ ПРОКРУТКИ. На кадре окно 1080×900, а раздел
    /// «Телефон» длиннее окна и прокручивается: карточка, уехавшая под нижний край, в README
    /// не попадает вовсе.
    ///
    /// ⚠️ **ЭТО НАЙДЕНО КАДРОМ, А НЕ НА ГЛАЗ, и проверка заведена ровно затем.** Прежде адрес
    /// и QR стояли ТРЕТЬЕЙ карточкой — после порта, кода и списка устройств, — и на снятом кадре
    /// их не было видно целиком: человек (и README) видел шапку раздела, а главного ответа
    /// «вот адрес, вот код» — нет. Проверка сторожит ПОРЯДОК ОТВЕТОВ: адрес с QR идёт раньше
    /// подробностей, и оба целиком влезают в окно.
    ///
    /// Мутации, которые обязаны упасть здесь: «карточка адреса снова уехала под сгиб» ·
    /// «QR спрятан от человека» · «адрес вернулся ниже порта и устройств».
    /// </summary>
    [AvaloniaFact]
    public void Адрес_и_QR_видны_выше_сгиба()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new FakePhone(), new[] { Wired("192.168.0.5") });

            var title = window.FindControl<TextBlock>("PhoneSectionTitle")!;
            var address = window.FindControl<TextBlock>("PhoneAddressText")!;
            var image = window.FindControl<Image>("PhoneQrImage")!;
            var hint = window.FindControl<TextBlock>("PhoneQrNoteText")!;

            // Место считается от верха ОКНА: карточка раздела прокручивается внутри него.
            double TopOf(Visual visual) =>
                visual.TranslatePoint(default, window)?.Y ?? double.NaN;

            var fold = window.ClientSize.Height;

            foreach (var (what, visual) in new (string, Visual)[]
                     {
                         ("адрес", address),
                         ("место под QR", image),
                         ("слова о QR", hint),
                     })
            {
                var top = TopOf(visual);

                Assert.False(double.IsNaN(top), $"{what}: не удалось измерить место в окне");
                Assert.True(top > 0, $"{what}: уехал выше шапки раздела ({top})");
                Assert.True(top < fold, $"{what} оказался ниже сгиба окна ({top} из {fold}) — в кадр он не попадёт");
            }

            // Порядок ответов: адрес и QR — РАНЬШЕ подробностей (порт, код, устройства).
            Assert.True(
                TopOf(image) < TopOf(window.FindControl<NumericUpDown>("PhonePortBox")!),
                "карточка адреса снова уехала ниже порта и устройств");

            // И место под картинку вправду отведено, а не «ноль на ноль»: у органа есть размер,
            // и он не выродился в полоску.
            Assert.True(image.Bounds.Width >= 100, $"под QR отведено мало места: {image.Bounds}");
            Assert.True(image.Bounds.Height >= 100, $"под QR отведено мало места: {image.Bounds}");

            // Заголовок раздела — выше всего остального: это он отвечает «где я».
            Assert.True(TopOf(title) < TopOf(address));

            Close(window);
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }
}
