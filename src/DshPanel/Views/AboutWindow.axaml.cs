using System;
using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DshPanel.Localization;
using DshPanel.Shell;
using DshPanel.Update;

namespace DshPanel.Views;

/// <summary>
/// ОКНО «О ПРОГРАММЕ» — что это, зачем, ссылки и поддержка.
///
/// Требование владельца: «что это, зачем, ссылка на GitHub, донаты; критерий — ссылки живые,
/// текст на трёх языках». Отсюда три правила устройства, и каждое проверяемо:
///
/// 1. **Ни одного текста в разметке.** Название продукта, версия, объяснение и все подписи
///    ставятся здесь из <see cref="PanelStrings"/>, а те берутся из словаря трёх языков
///    (`docs\LOCALIZATION.md` §1). Ключ — имя члена <c>PanelStrings</c>, и если перевод пропал,
///    человек увидит имя ключа, а не пустое место.
/// 2. **Решения — чистые функции, а не условия внутри разметки.** «Показывать ли строку ссылки»
///    (<see cref="ProductLinks.ShouldShow"/>) и «показывать ли строку о машинном переводе»
///    (<see cref="ShowsTranslationNote"/>) проверяются тестами без окна и ломаются мутацией.
/// 3. **Окно ничего не открывает само.** Ссылку открывает <see cref="ProductLinks.Open"/>
///    и возвращает текст ошибки; окно показывает её СТРОКОЙ у себя, а не модальным окном поверх
///    себя — владелец такого не любит, и это же требование записано в задаче.
///
/// ⚠️ **Репозиторий 2.0 пока закрытый** (решение владельца 26.09.2026): адрес пуст
/// (<see cref="ProductLinks.RepositoryUrl"/>), поэтому строки «Исходники и выпуски на GitHub»
/// в окне НЕТ вовсе. Это не забывчивость: пустой адрес — «возможности нет», и кнопки, ведущей
/// в никуда, в продукте не бывает (правило v1).
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>
    /// Язык-источник интерфейса. Ему раскрывать про машинный перевод нечего — переводов
    /// на него не делали, и строка <see cref="PanelStrings.AboutTranslationNote"/> по-русски
    /// не показывается (решение владельца).
    /// </summary>
    public const string SourceLanguage = "ru";

    /// <summary>
    /// Дверь «Сообщить о проблеме», данная владельцем окна (<see cref="AttachIssueDoor"/>).
    /// Пусто — кнопки нет: окно без пути наружу не должно показывать кнопку в никуда.
    /// </summary>
    private Action? _openIssue;

    /// <summary>
    /// InitializeComponent, а не AvaloniaXamlLoader.Load(this): только первый прописывает поля
    /// <c>x:Name</c> (та же грабля, что у главного окна, настроек и копий).
    /// </summary>
    public AboutWindow()
    {
        InitializeComponent();

        Icon = PanelIcon.Window;

        // Подписи ставятся сразу, а не «когда-нибудь потом»: окно, построенное без наполнения,
        // не должно выглядеть пустым — так его видят проверки и так его мог бы увидеть человек.
        Title = PanelStrings.AboutTitle;
        ProductText.Text = PanelStrings.AppName;

        // Версия — ЧИСЛОВОЙ частью: .NET дописывает к ProductVersion хеш коммита, и человеку
        // он ничего не говорит (правило проекта «версию сравнивать по числовой части»).
        VersionText.Text = string.Format(
            CultureInfo.CurrentCulture, PanelStrings.AboutVersionFormat, PanelVersion);

        PurposeHeadingText.Text = PanelStrings.AboutPurposeHeading;
        PurposeText.Text = PanelStrings.AboutPurpose;
        LinksHeadingText.Text = PanelStrings.AboutLinksHeading;
        RepoLinkButton.Content = PanelStrings.AboutRepoLink;
        DonateLinkButton.Content = PanelStrings.AboutDonateLink;
        DonateNoteText.Text = PanelStrings.AboutDonateNote;
        TranslationNoteText.Text = PanelStrings.AboutTranslationNote;
        IssueHeadingText.Text = PanelStrings.AboutIssueHeading;
        IssueNoteText.Text = PanelStrings.AboutIssueNote;
        IssueButton.Content = PanelStrings.AboutIssueButton;
        LicenseText.Text = PanelStrings.AboutLicense;
        CopyrightText.Text = PanelStrings.AboutCopyright;
        CloseButton.Content = PanelStrings.AboutCloseButton;

        RepoLinkButton.Click += (_, _) => OpenLink(ProductLinks.RepositoryUrl);
        DonateLinkButton.Click += (_, _) => OpenLink(ProductLinks.Donate);

        // Дверь в окно «Сообщить о проблеме». Окно СВОЁ, а не блок здесь: человеку надо
        // прочитать ровно тот текст, который уйдёт, а этому нужно место (требование владельца
        // 29.09.2026, DESIGN.md п. 38). Просьбу обрабатывает владелец окна — панель, которая
        // знает и журнал, и состояние сервера; «О программе» их не знает и знать не должно.
        IssueButton.Click += (_, _) => _openIssue?.Invoke();

        CloseButton.Click += (_, _) => Close();

        // Подсказки кнопок — у всех, включая ссылки: ссылка открывает БРАУЗЕР человека,
        // и это ровно то, о чём он обязан узнать до щелчка, а не после.
        PanelToolTip.Set(CloseButton, PanelStrings.TipCloseAboutButton);
        PanelToolTip.Set(RepoLinkButton, PanelStrings.TipRepoLinkButton);
        PanelToolTip.Set(DonateLinkButton, PanelStrings.TipDonateLinkButton);
        PanelToolTip.Set(IssueButton, PanelStrings.TipAboutIssueButton);

        Render();
    }

    /// <summary>
    /// Привязать дверь «Сообщить о проблеме». Ссылку даёт владелец окна, и это не формальность:
    /// отчёт собирается из журнала панели и состояния сервера, а «О программе» этих данных
    /// не имеет — и получать их ему незачем.
    ///
    /// ⚠️ Пустая ссылка — законное состояние (кадр `--shot about`): кнопка тогда ЕСТЬ, но ничего
    /// не делает. Так кадр честно показывает окно таким, какое оно у человека, и не выдумывает
    /// путь, которого в съёмке нет.
    /// </summary>
    /// ⚠️ Перерисовка ЗДЕСЬ обязательна, и это не украшение: конструктор уже вызвал Render(),
    /// когда двери ещё не было, — без этого вызова блок оставался скрытым НАВСЕГДА, сколько бы
    /// дверь потом ни давали. Нашлось на кадре --shot about: окно выглядело беднее, чем у человека.
    public void AttachIssueDoor(Action? openIssue)
    {
        _openIssue = openIssue;
        Render();
    }

    /// <summary>
    /// Разложить по строкам то, что РЕШЕНО предикатами: какие ссылки показывать и показывать ли
    /// строку о машинном переводе. Зовётся и при построении, и проверками — второго места,
    /// где эти решения принимаются, в окне нет.
    /// </summary>
    public void Render()
    {
        RepoLinkButton.IsVisible = ProductLinks.ShouldShow(ProductLinks.RepositoryUrl);

        DonateLinkButton.IsVisible = ProductLinks.ShouldShow(ProductLinks.Donate);
        // Пояснение про донаты живёт ПОД ссылкой и без неё выглядело бы брошенным.
        DonateNoteText.IsVisible = DonateLinkButton.IsVisible;

        TranslationNoteText.IsVisible = ShowsTranslationNote(Loc.Language);

        // Блок сообщения о проблеме показывается, только если у окна ЕСТЬ дверь. Кнопка в никуда
        // хуже отсутствующей кнопки: человек нажал бы и не понял, почему ничего не произошло.
        IssuePanel.IsVisible = ShowIssueBlock(_openIssue is not null);
    }

    // ------------------------------------------------------------------ решения (чистые)

    /// <summary>
    /// Показывать ли строку о том, что перевод машинный. Показывается ВСЕМ, кроме русского:
    /// русскому языку раскрывать нечего (решение владельца, `docs\LOCALIZATION.md` §6).
    ///
    /// Чистой функцией от языка, а не условием внутри разметки, — потому что это решение
    /// проверяется прогоном: «есть для en и zh, нет для ru» обязано УМЕТЬ упасть.
    ///
    /// Незнакомый язык (пусто) — показываем: не раскрыть машинный перевод хуже, чем раскрыть
    /// его зря, а языков, кроме русского, у панели два.
    /// </summary>
    /// <summary>
    /// Показывать ли блок «Сообщить о проблеме». Чистой функцией от наличия двери — чтобы это
    /// решение проверялось прогоном («есть дверь — блок есть, нет двери — блока нет»), а не
    /// держалось на одном присваивании, которое можно случайно снять.
    /// </summary>
    public static bool ShowIssueBlock(bool doorAvailable) => doorAvailable;

    public static bool ShowsTranslationNote(string? language) =>
        !string.Equals(language, SourceLanguage, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Версия панели ЧИСЛОВОЙ частью: <c>2.0.0+abc1234</c> → <c>2.0.0</c>. Хеш коммита дописывает
    /// сам .NET в <c>ProductVersion</c>, и показывать его человеку незачем.
    ///
    /// ⚠️ Реализация здесь БОЛЬШЕ НЕ ЖИВЁТ: правило одно на панель и лежит в ядре обновления
    /// (<see cref="Update.UpdateDecisions.Numeric"/>) — там же, где им сравниваются версии.
    /// До 27.09.2026 таких правил было два (здесь и в ядре обновления), и они однажды разошлись бы.
    /// </summary>
    public static string NumericVersion(string? productVersion) => UpdateDecisions.Numeric(productVersion);

    /// <summary>
    /// Версия панели для окна: сначала <c>ProductVersion</c> (у панели, собранной в репозитории,
    /// она с хешем коммита), затем — если её почему-то нет — версия сборки.
    /// </summary>
    public static string PanelVersion
    {
        get
        {
            var assembly = typeof(AboutWindow).Assembly;

            var product = NumericVersion(
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

            return product.Length > 0 ? product : NumericVersion(assembly.GetName().Version?.ToString());
        }
    }

    // ------------------------------------------------------------------ то, что видно (для проверок)

    /// <summary>Название продукта на экране.</summary>
    public string ProductLine => ProductText.Text ?? string.Empty;

    /// <summary>Строка версии на экране.</summary>
    public string VersionLine => VersionText.Text ?? string.Empty;

    /// <summary>Объяснение «что делает панель» на экране.</summary>
    public string PurposeLine => PurposeText.Text ?? string.Empty;

    /// <summary>Подпись ссылки на репозиторий (даже когда строка скрыта — проверке нужен текст).</summary>
    public string RepoLine => RepoLinkButton.Content?.ToString() ?? string.Empty;

    /// <summary>Подпись ссылки на донаты.</summary>
    public string DonateLine => DonateLinkButton.Content?.ToString() ?? string.Empty;

    /// <summary>Подпись кнопки закрытия.</summary>
    public string CloseLine => CloseButton.Content?.ToString() ?? string.Empty;

    /// <summary>Строка о машинном переводе (даже когда она скрыта).</summary>
    public string TranslationNoteLine => TranslationNoteText.Text ?? string.Empty;

    /// <summary>Лицензия на экране.</summary>
    public string LicenseLine => LicenseText.Text ?? string.Empty;

    /// <summary>Автор на экране.</summary>
    public string CopyrightLine => CopyrightText.Text ?? string.Empty;

    /// <summary>Строка состояния: пусто или причина, по которой ссылка не открылась.</summary>
    public string Status => StatusText.Text ?? string.Empty;

    /// <summary>Видна ли строка ссылки на репозиторий.</summary>
    public bool RepoLinkVisible => RepoLinkButton.IsVisible;

    /// <summary>Видна ли строка ссылки на донаты.</summary>
    public bool DonateLinkVisible => DonateLinkButton.IsVisible;

    /// <summary>Видна ли строка о машинном переводе.</summary>
    public bool TranslationNoteVisible => TranslationNoteText.IsVisible;

    /// <summary>Щёлкнуть по ссылке на донаты — так же, как это делает человек.</summary>
    public void ClickDonate() => DonateLinkButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    /// <summary>Щёлкнуть по кнопке закрытия — так же, как это делает человек.</summary>
    public void ClickClose() => CloseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    // ------------------------------------------------------------------ ссылки

    /// <summary>
    /// Шов ТОЛЬКО для проверок без экрана: чем отвечать на «открой ссылку».
    ///
    /// В жизни ответ даёт <see cref="ProductLinks.Open"/> — он же и открывает браузер. Проверка
    /// показать браузер не имеет права: это окно на рабочем столе владельца. А путь «открыть
    /// не вышло → причина строкой в окне» обязан быть проверен, а не обещан словами.
    /// У обычного запуска это свойство <c>null</c>.
    /// </summary>
    internal Func<string, string>? OpenLinkForTests { get; set; }

    /// <summary>
    /// Открыть ссылку и показать причину, если не вышло. Ошибка идёт СТРОКОЙ СОСТОЯНИЯ здесь же:
    /// модальное окно поверх этого окна было бы отдельным окном ради одной строки.
    /// </summary>
    private void OpenLink(string url)
    {
        var error = OpenLinkForTests is not null ? OpenLinkForTests(url) : ProductLinks.Open(url);

        StatusText.Text = error.Length == 0
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, PanelStrings.AboutLinkFailedFormat, error);
    }
}
