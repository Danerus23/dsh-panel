using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using DshPanel.Issue;
using DshPanel.Localization;
using DshPanel.Shell;

namespace DshPanel.Views;

/// <summary>
/// ОКНО «СООБЩИТЬ О ПРОБЛЕМЕ» — блок issue из окна «О программе» (требование владельца
/// 29.09.2026, <c>DESIGN.md</c> п. 38).
///
/// **Порядок работы задан владельцем и соблюдён здесь буквально:**
/// собрать → вычистить → **показать ровно тот текст, который уйдёт** → отправить по нажатию.
/// Поэтому окно не строит текст само: его строит <see cref="IssueReport"/> — чистое решение,
/// которое можно проверить прогоном, — а окно только показывает готовое и меняет его, когда
/// человек дописывает свои слова.
///
/// ⚠️ **Панель сама никуда ничего не отправляет.** Наружу текст выходит тремя путями, и все три
/// начинает ЧЕЛОВЕК: открыть страницу нового issue в браузере, скопировать в буфер, сохранить
/// в файл. Ни по API, ни молча, ни «в фоне» отчёт не уходит — это не «мы постарались», а то,
/// что в окне нет ни одного другого пути.
///
/// ⚠️ **Чего этому окну не нужно и не дано:** ключа модели, баланса, ссылки входа, содержимого
/// <c>~/.dsh</c>. Их нет ни в собранных фактах, ни в чистке, ни в кнопках — см.
/// <see cref="IssueReport.OmittedNote"/>, которую человек читает рядом с текстом.
/// </summary>
public partial class IssueWindow : Window
{
    private IssueFacts? _facts;

    /// <summary>
    /// InitializeComponent, а не AvaloniaXamlLoader.Load(this): только первый прописывает поля
    /// <c>x:Name</c> (та же грабля, что у остальных окон панели).
    /// </summary>
    public IssueWindow()
    {
        InitializeComponent();

        Icon = PanelIcon.Window;

        Title = PanelStrings.IssueTitle;
        HeadingText.Text = PanelStrings.IssueHeading;
        SubtitleText.Text = PanelStrings.IssueSubtitle;
        MessageLabelText.Text = PanelStrings.IssueMessageLabel;
        PreviewLabelText.Text = PanelStrings.IssuePreviewLabel;
        OmittedText.Text = IssueReport.OmittedNote;
        OpenOnGithubButton.Content = PanelStrings.IssueOpenButton;
        CopyButton.Content = PanelStrings.IssueCopyButton;
        SaveButton.Content = PanelStrings.IssueSaveButton;
        CloseButton.Content = PanelStrings.IssueCloseButton;
        MessageBox.PlaceholderText = PanelStrings.IssueMessageWatermark;

        OpenOnGithubButton.Click += (_, _) => OpenOnGithub();
        CopyButton.Click += async (_, _) => await CopyAsync();
        SaveButton.Click += async (_, _) => await SaveAsync();
        CloseButton.Click += (_, _) => Close();

        PanelToolTip.Set(OpenOnGithubButton, PanelStrings.TipIssueOpenButton);
        PanelToolTip.Set(CopyButton, PanelStrings.TipIssueCopyButton);
        PanelToolTip.Set(SaveButton, PanelStrings.TipIssueSaveButton);
        PanelToolTip.Set(CloseButton, PanelStrings.TipIssueCloseButton);

        // Подсказка внутри поля — это и есть ответ на «а что писать»: без неё человек видит
        // пустое поле и не знает, чего от него хотят.
        MessageLabelText.Text = PanelStrings.IssueMessageLabel;

        Render();
    }

    /// <summary>
    /// Дать окну факты для отчёта. Окно НЕ собирает их само: сборка живёт у того, у кого есть
    /// право читать журнал и знать состояние (см. <c>App.CreateIssueWindow</c>).
    /// </summary>
    public void Attach(IssueFacts facts)
    {
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        Render();
    }

    /// <summary>
    /// Показать текст, который уйдёт. Ровно он, а не «примерно такой».
    /// Зовётся и при построении, и проверками — второго места, где текст собирается, нет.
    /// </summary>
    public void Render()
    {
        var facts = _facts ?? EmptyFacts();

        PreviewBox.Text = IssueReport.Text(facts, MessageBox.Text);
    }

    // ------------------------------------------------------------------ три пути наружу

    /// <summary>
    /// Открыть страницу НОВОГО issue с заполненным текстом. Открывает браузер человека —
    /// значит по его щелчку и только по нему. Ошибку открытия показываем СТРОКОЙ в самом окне,
    /// а не отдельным окном поверх него (то же правило, что у «О программе»).
    /// </summary>
    private void OpenOnGithub()
    {
        var facts = _facts ?? EmptyFacts();
        var error = ProductLinks.Open(IssueReport.GitHubUrl(facts, MessageBox.Text));

        StatusText.Text = error;
    }

    /// <summary>Скопировать текст в буфер обмена — на случай, когда браузера под рукой нет.</summary>
    private async Task CopyAsync()
    {
        // Буфер берётся у ПЛАТФОРМЫ через окно: у IClipboard в Avalonia 12 нет SetTextAsync,
        // и первая редакция пробовала именно его — не собралось. Дверь одна на окно.
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;

        if (clipboard is null)
        {
            StatusText.Text = PanelStrings.IssueClipboardUnavailable;
            return;
        }

        try
        {
            // У IClipboard в Avalonia 12 данные кладутся через перенос данных; строка —
            // это расширение SetTextAsync из Avalonia.Input.Platform. Первые редакции пробовали
            // DataObject и SetDataAsync(формат, строка) — оба устарели, и оба не собрались.
            await clipboard.SetTextAsync(PreviewBox.Text ?? string.Empty);

            StatusText.Text = PanelStrings.IssueCopied;
        }
        catch (Exception error)
        {
            // Причина как есть: «не удалось» без причины человеку ничего не говорит.
            StatusText.Text = error.Message;
        }
    }

    /// <summary>Сохранить отчёт в файл, который выберет человек. Панель ничего не решает за него.</summary>
    private async Task SaveAsync()
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = PanelStrings.IssueSaveTitle,
                SuggestedFileName = "dsh-panel-issue.txt",
                DefaultExtension = "txt",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(PanelStrings.IssueSaveFileType)
                    {
                        Patterns = new[] { "*.txt" },
                    },
                },
            });

            if (file is null)
            {
                // Отказ человека — не ошибка: молчим, как молчит любой отменённый выбор файла.
                return;
            }

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);
            await writer.WriteAsync(PreviewBox.Text ?? string.Empty);

            StatusText.Text = string.Format(
                CultureInfo.CurrentCulture, PanelStrings.IssueSavedFormat, file.Name);
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
        }
    }

    /// <summary>
    /// Факты «ничего не известно» — для окна, построенного без наполнения (проверки, кадр).
    /// Пустая версия не выдумывается: отчёт честно скажет «неизвестна».
    /// </summary>
    private static IssueFacts EmptyFacts() => new(
        PanelVersion: AboutWindow.PanelVersion,
        Revision: string.Empty,
        System: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        EngineVersion: string.Empty,
        NodeVersion: string.Empty,
        LaunchMode: string.Empty,
        ServerState: string.Empty,
        ServerPort: 0,
        LogTail: Array.Empty<string>());

    // ------------------------------------------------------------------ то, что видно (для проверок)

    /// <summary>Показанный текст отчёта — ровно тот, который уйдёт.</summary>
    public string PreviewLine => PreviewBox.Text ?? string.Empty;

    /// <summary>Строка состояния: пусто или причина (не открылось, нет буфера, сохранили).</summary>
    public string Status => StatusText.Text ?? string.Empty;

    /// <summary>Подпись кнопки «Открыть на GitHub».</summary>
    public string OpenLine => OpenOnGithubButton.Content?.ToString() ?? string.Empty;

    /// <summary>Подпись кнопки «Скопировать».</summary>
    public string CopyLine => CopyButton.Content?.ToString() ?? string.Empty;

    /// <summary>Подпись кнопки «Сохранить в файл».</summary>
    public string SaveLine => SaveButton.Content?.ToString() ?? string.Empty;

    /// <summary>Строка «чего в отчёте нет и почему».</summary>
    public string OmittedLine => OmittedText.Text ?? string.Empty;

    /// <summary>Ввести свои слова — так же, как это делает человек в поле.</summary>
    public void TypeMessage(string? message) => MessageBox.Text = message;
}
