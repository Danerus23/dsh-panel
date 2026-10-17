using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DshPanel.Server;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// БЛОК ДВИЖКА В ОКНЕ НАСТРОЕК — ЖИВОЙ ПУТЬ (пункты 5–7 плана E3.2).
///
/// **Зачем отдельная проверка.** Чистые решения блока (`EngineUpdateDecisions`) и путь обновления
/// (`EngineUpdateFlow`/`EngineUpdateRun`) проверены без окна. Но правило владельца — **«спрашивать
/// ВСЕГДА перед обновлением движка»** — это утверждение о ПУТИ КНОПКИ, и без окна его проверить
/// нельзя: можно иметь верный план и кнопку, которая его не спрашивает.
///
/// Проверяются четыре вещи, каждая — решение владельца:
///
/// 1. **две строки разные** (рекомендуемая — проверенная, свежая — «новее, но не проверена»),
///    и отдельная кнопка появляется ТОЛЬКО когда свежая новее;
/// 2. **главная кнопка спрашивает человека и только потом работает**, а отказ человека не начинает
///    ничего (признака «идёт работа» у панели нет — замер 10.10.2026 — поэтому спрашиваем всегда);
/// 3. **непроверенная версия ставится отдельной кнопкой и сначала словами** (разово, без галочек);
/// 4. **обновление здесь недоступно — окно говорит словами**, а не показывает пустые строки.
///
/// ⚠️ Дверь вопроса — подставная (<see cref="SettingsWindow.AttachEngineAsk"/>): настоящее окно
/// подтверждения в прогоне без экрана ждало бы ответа вечно.
/// </summary>
public class SettingsEngineBlockTests
{
    /// <summary>Подставной раздел: помнит, с чем его позвали, и отвечает тем, что задала проверка.</summary>
    private sealed class FakeSection : IEngineUpdateSection
    {
        public bool Available { get; set; } = true;
        public string RefusalKey { get; set; } = string.Empty;
        public string Recommended { get; set; } = "0.1.5-rc.2";

        /// <summary>
        /// СВОЯ СТОЯЩАЯ ВЕРСИЯ — по умолчанию СТАРШЕ рекомендуемой: так у карточки есть кнопка
        /// «Обновить», и прежние проверки блока (щелчок по ней, вопрос человеку) идут тем же путём.
        /// Состояния «то же самое» и «своя новее» задаются проверками отдельно.
        /// </summary>
        public string Installed { get; set; } = "0.1.4-rc.2";

        /// <summary>
        /// СЕРВЕР DSH НА МАШИНЕ ЕСТЬ ВООБЩЕ — по умолчанию ДА: состояния «сервера нет» добирается
        /// проверкой, а все прежние проверки блока идут в обычном случае (сервер есть, версия старше).
        /// </summary>
        public bool EnginePresent { get; set; } = true;

        public string Fresh { get; set; } = "0.2.0-rc.2";
        public bool FreshIsNewer { get; set; } = true;

        /// <summary>Есть ли замок для свежей: по умолчанию ДА (иначе кнопки не было бы вовсе).</summary>
        public bool FreshInstallable { get; set; } = true;

        public List<bool> Calls { get; } = new();
        public string Answer { get; set; } = "движок обновлён и проверен пробой";
        public int Refreshes { get; private set; }

        /// <summary>Вопрос перед обновлением и вопрос перед откатом — их показывает окно.</summary>
        public PanelQuestion UpdateQuestion { get; set; } =
            new("Обновить движок?", "поставим рекомендованную 0.1.5-rc.2?", "Да, обновить", "Отмена", "п.", "п.");

        public bool RollbackAvailable { get; set; }
        public string RollbackTarget { get; set; } = "0.1.4-rc.2";

        /// <summary>Выжимка про версии и адрес выпусков — их показывает окно.</summary>
        public IReadOnlyList<string> Notes { get; set; } = new[] { "Панель проверена на этой версии целиком." };

        public string ReleasesAddress { get; set; } = "https://example.invalid/engine/releases";

        public PanelQuestion RollbackQuestion { get; set; } =
            new("Вернуться к прежней версии?", "старый движок не откроет сессии новой версии", "Да, вернуться", "Отмена", "п.", "п.");

        public List<int> Rollbacks { get; } = new();
        public string RollbackAnswer { get; set; } = "вернулись к прежней версии 0.1.4-rc.2";

        public string Run(bool freshWanted)
        {
            Calls.Add(freshWanted);
            return Answer;
        }

        public string Rollback()
        {
            Rollbacks.Add(1);
            return RollbackAnswer;
        }

        public void Refresh() => Refreshes++;
    }

    /// <summary>Собрать окно с блоком движка. <paramref name="ask"/> — подставной ответ человека.</summary>
    private static (SettingsWindow Window, FakeSection Section, List<string> Asked) Stand(
        Func<PanelQuestion, Task<bool>>? ask = null,
        Action<FakeSection>? tune = null,
        List<string>? opened = null)
    {
        var window = PanelTestStand.SettingsStand(PanelTestStand.TempDir());
        var section = new FakeSection();

        tune?.Invoke(section);

        var asked = new List<string>();

        // ⚠️ ДВЕРЬ БРАУЗЕРА — ДО БЛОКА ДВИЖКА, и это не порядок ради красоты: блок при подключении
        // сразу себя перерисовывает, и если бы он открывал ссылку «сама», то открыл бы её ДО того,
        // как проверка подставила дверь, — то есть НАСТОЯЩИМ браузером у человека. Мутация
        // «ссылка открывается сама» на такой проверке ВЫЖИВАЛА (найдено 11.10.2026).
        window.AttachEngineBrowse(address =>
        {
            opened?.Add(address);
            return string.Empty;
        });

        window.AttachEngineUpdate(section);
        window.AttachEngineAsk(question =>
        {
            asked.Add(question.Text);
            return ask is null ? Task.FromResult(true) : ask(question);
        });

        window.ShowSection(SettingsWindow.UpdateSection);
        window.Show();
        PanelTestStand.Settle();

        return (window, section, asked);
    }

    private static Button FindButton(SettingsWindow window, string name)
    {
        var button = window.FindControl<Button>(name);

        Assert.NotNull(button);
        return button!;
    }

    /// <summary>Строка блока движка по имени органа — текстом (кнопка её и показывает).</summary>
    private static string Text(SettingsWindow window, string name)
    {
        var block = window.FindControl<TextBlock>(name);

        Assert.NotNull(block);
        return block!.Text ?? string.Empty;
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PanelTestStand.Settle();
    }

    /// <summary>
    /// ДОЖДАТЬСЯ РАБОТЫ ОКНА: нажатия уходят в <c>async void</c>-обработчик, и одного
    /// <c>Settle()</c> иногда не хватает — продолжение после <c>await</c> встаёт в очередь
    /// диспетчера ПОСЛЕ него. Без ожидания проверка становится мерцающей: она уже падала
    /// в воротах именно так (найдено 11.10.2026), а мерцающая проверка ничего не стоит.
    /// </summary>
    private static void SettleUntil(Func<bool> done, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);

        while (!done() && DateTime.UtcNow < until)
        {
            PanelTestStand.Settle();
            System.Threading.Thread.Sleep(10);
        }

        Assert.True(done(), what);
    }

    /// <summary>
    /// ТРИ СТРОКИ НАЗЫВАЮТ СВОИ ВЕРСИИ, и каждая — свою (решение владельца 14.10.2026):
    /// **своя** версия первой строкой, **решение** про обновление второй, **свежая** — тихой третьей.
    ///
    /// ⚠️ Прежде своей версии на карточке не было ВООБЩЕ, и панель предлагала «обновить» на то же
    /// самое: сравнить показанное было не с чем.
    /// </summary>
    [AvaloniaFact]
    public void Две_строки_называют_разные_версии()
    {
        var (window, section, _) = Stand();

        try
        {
            // СВОЯ — ПЕРВОЙ СТРОКОЙ.
            Assert.Contains(section.Installed, Text(window, "EngineRecommendedText"), StringComparison.Ordinal);

            // РЕШЕНИЕ про обновление — второй, и в нём названа РЕКОМЕНДУЕМАЯ версия (своя старше).
            var decision = Text(window, "EngineFreshText");

            Assert.Contains(section.Recommended, decision, StringComparison.Ordinal);
            Assert.DoesNotContain(section.Installed, decision, StringComparison.Ordinal);

            // СВЕЖАЯ — тихой строкой под решением, своим словом.
            Assert.Contains(section.Fresh, Text(window, "EngineFreshLineText"), StringComparison.Ordinal);

            Assert.True(FindButton(window, "EngineInstallFreshButton").IsVisible, "свежая новее — отдельной кнопки нет");
            Assert.True(FindButton(window, "EngineUpdateButton").IsVisible, "своя старше — кнопка обязана быть видна");
            Assert.True(FindButton(window, "EngineUpdateButton").IsEnabled, "главная кнопка обязана быть доступна");
        }
        finally
        {
            window.CloseQuietly();
        }
    }

    /// <summary>
    /// КНОПКА «ОБНОВИТЬ» ВИДНА РОВНО ТОГДА, КОГДА СТОЯЩАЯ ВЕРСИЯ СТАРШЕ РЕКОМЕНДУЕМОЙ
    /// (находка владельца 14.10.2026: панель предлагала обновить на то же самое).
    ///
    /// **Три состояния, и в двух из них кнопки нет:**
    /// * своя **та же**, что рекомендуемая → «обновлять нечего» — обновлять не на что;
    /// * своя **новее** → это понижение, а понижение — решение владельца, а не щелчок панели;
    /// * своя **старше** → единственный случай, когда кнопка есть.
    ///
    /// Мутации, которые обязаны упасть здесь: **«показывать кнопку всегда»** ·
    /// **«показывать её и при своей версии новее»**.
    /// </summary>
    [AvaloniaFact]
    public void Кнопка_обновить_видна_ровно_когда_своя_версия_старше()
    {
        var cases = new[]
        {
            (Name: "своя та же", Installed: "0.1.5-rc.2", Visible: false, Same: true),
            (Name: "своя старше", Installed: "0.1.4-rc.2", Visible: true, Same: false),
            (Name: "своя новее", Installed: "0.2.0-rc.2", Visible: false, Same: false),
        };

        foreach (var state in cases)
        {
            var (window, _, _) = Stand(tune: section => section.Installed = state.Installed);

            try
            {
                var button = FindButton(window, "EngineUpdateButton");
                var decision = Text(window, "EngineFreshText");

                Assert.True(
                    button.IsVisible == state.Visible,
                    $"{state.Name}: кнопка «Обновить» видна={button.IsVisible}, а обязана быть {state.Visible}");

                Assert.True(
                    decision.Length > 0,
                    $"{state.Name}: карточка молчит о том, что с обновлением, — это и было находкой владельца");

                // «Обновлять нечего» — ЭТО НАЗВАНО СЛОВАМИ, а не пустой строкой и не спрятанной кнопкой.
                if (state.Same) Assert.Contains("нечего", decision, StringComparison.OrdinalIgnoreCase);
                else Assert.DoesNotContain("нечего", decision, StringComparison.OrdinalIgnoreCase);

                // И своя версия названа ПЕРВОЙ строкой в любом из трёх состояний.
                Assert.Contains(state.Installed, Text(window, "EngineRecommendedText"), StringComparison.Ordinal);
            }
            finally
            {
                window.CloseQuietly();
            }
        }
    }

    /// <summary>СВЕЖАЯ НЕ НОВЕЕ — отдельной кнопки нет вовсе: «то же самое, только страшнее» не бывает.</summary>
    [AvaloniaFact]
    public void Свежая_не_новее_отдельной_кнопки_нет()
    {
        var (window, _, _) = Stand(tune: section => section.FreshIsNewer = false);

        try
        {
            Assert.False(FindButton(window, "EngineInstallFreshButton").IsVisible);
            Assert.Equal(string.Empty, Text(window, "EngineFreshLineText"));
        }
        finally
        {
            window.CloseQuietly();
        }
    }

    /// <summary>
    /// СВЕЖАЯ БЕЗ ЗАМКА: строка есть, КНОПКИ НЕТ, и причина сказана словами. Панель ставит движок
    /// только по замку — кнопка, которая всегда отказывает, была бы ловушкой, а «поставим тихо
    /// проверенную и объявим свежую» — ложью.
    /// </summary>
    [AvaloniaFact]
    public void Свежая_без_замка_кнопки_нет_а_причина_сказана()
    {
        var (window, section, asked) = Stand(tune: s => s.FreshInstallable = false);

        try
        {
            Assert.False(FindButton(window, "EngineInstallFreshButton").IsVisible, "кнопка без замка — ловушка");

            var line = Text(window, "EngineFreshLineText");

            Assert.Contains(section.Fresh, line, StringComparison.Ordinal);
            Assert.Contains("нельзя", line, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(asked);
        }
        finally
        {
            window.CloseQuietly();
        }
    }

    /// <summary>
    /// ⚠️ ГЛАВНОЕ: ГЛАВНАЯ КНОПКА СПРАШИВАЕТ ЧЕЛОВЕКА **ВСЕГДА** — и вопрос называет версию,
    /// которую панель поставит. Отказ человека не начинает работы вовсе.
    /// </summary>
    [AvaloniaFact]
    public void Главная_кнопка_спрашивает_всегда()
    {
        var (window, section, asked) = Stand();

        try
        {
            Click(FindButton(window, "EngineUpdateButton"));

            SettleUntil(() => section.Calls.Count == 1, "главная кнопка не дошла до работы");

            Assert.Equal(new[] { section.UpdateQuestion.Text }, asked);
            Assert.Contains("0.1.5-rc.2", asked[0], StringComparison.Ordinal);
            Assert.Equal(new[] { false }, section.Calls);
        }
        finally
        {
            window.CloseQuietly();
        }

        // А теперь человек сказал «нет»: работа не начинается, и окно говорит это словами.
        var (refused, refusedSection, refusedAsked) = Stand(ask: _ => Task.FromResult(false));

        try
        {
            Click(FindButton(refused, "EngineUpdateButton"));

            SettleUntil(
                () => refused.FindControl<TextBlock>("EngineStatusText")!.Text == PanelStrings.EngineUpdateAskCancelled,
                "отказ человека не сказан словами");

            Assert.Single(refusedAsked);
            Assert.Empty(refusedSection.Calls);
            Assert.Equal(PanelStrings.EngineUpdateAskCancelled, refused.FindControl<TextBlock>("EngineStatusText")!.Text);
        }
        finally
        {
            refused.CloseQuietly();
        }
    }

    /// <summary>
    /// КАРТОЧКА СЕРВЕРА DSH ВИДНА ЧЕЛОВЕКУ ЦЕЛИКОМ — и её вид СНЯТ КАДРОМ в двух темах.
    ///
    /// **Зачем.** Карточка стоит ПОСЛЕДНЕЙ в разделе «Обновление», и кадр раздела
    /// (`--shot settings-update`) до неё не доходит: он показывает начало раздела, а карточка
    /// остаётся ниже поля видимости. Проверка прокручивает раздел до конца и убеждается, что
    /// ЗАГОЛОВОК КАРТОЧКИ ПОПАЛ В ОКНО, — а заодно снимает кадр: по нему владелец и смотрит вид
    /// карточки, которого иначе не увидеть ничем, кроме живого окна.
    ///
    /// Мутации, которые обязаны упасть здесь: **«спрятать карточку»** (`IsVisible = false`) ·
    /// **«убрать её из раздела»**.
    /// </summary>
    [AvaloniaFact]
    public void Карточка_сервера_видна_человеку_целиком()
    {
        var saved = Application.Current!.RequestedThemeVariant;

        try
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                var (window, _, _) = Stand();

                try
                {
                    Application.Current.RequestedThemeVariant = variant;
                    PanelTestStand.Settle();

                    // ПРОКРУТКА ДО КОНЦА РАЗДЕЛА: без неё карточки в поле видимости нет вовсе.
                    var scroll = window.FindControl<ScrollViewer>("ContentScroll");

                    Assert.NotNull(scroll);

                    scroll!.Offset = new Avalonia.Vector(0, scroll.Extent.Height);
                    PanelTestStand.Settle();

                    var title = window.FindControl<TextBlock>("EngineBlockTitle");
                    var own = window.FindControl<TextBlock>("EngineRecommendedText");

                    Assert.NotNull(title);
                    Assert.NotNull(own);
                    Assert.True(title!.IsVisible, "карточка сервера DSH спрятана");

                    var top = title.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value.Y;

                    Assert.True(
                        top >= 0 && top < window.ClientSize.Height,
                        $"заголовок карточки на {top:0.#} при высоте окна {window.ClientSize.Height:0.#} — карточка не видна человеку");

                    // И ПЕРВАЯ СТРОКА КАРТОЧКИ — СВОЯ ВЕРСИЯ (решение владельца 14.10.2026):
                    // по ней человек и понимает, что обновлять, а что нет.
                    Assert.StartsWith(PanelStrings.EngineOwnVersionFormat.Split('{')[0].Trim(), own!.Text, StringComparison.Ordinal);

                    var frame = PanelTestStand.SaveFrame(window, "settings-engine-card-" + variant);

                    Assert.True(new FileInfo(frame).Length > 0, $"кадр карточки ({variant}) пуст");
                }
                finally
                {
                    window.CloseQuietly();
                }
            }
        }
        finally
        {
            Application.Current.RequestedThemeVariant = saved;
        }
    }

    /// <summary>
    /// ОТКАТ (E3.3): кнопка есть ТОЛЬКО когда возвращаться есть куда, и подписана версией, к которой
    /// вернёмся; нажатие спрашивает человека — и вопрос говорит про сессии СЛОВАМИ.
    /// </summary>
    [AvaloniaFact]
    public void Откат_кнопка_есть_и_спрашивает_про_сессии()
    {
        // Возвращаться некуда: кнопки нет вовсе (мёртвая кнопка была бы ловушкой).
        var (none, _, noneAsked) = Stand();

        try
        {
            Assert.False(FindButton(none, "EngineRollbackButton").IsVisible);
            Assert.Empty(noneAsked);
        }
        finally
        {
            none.CloseQuietly();
        }

        var (window, section, asked) = Stand(tune: s => s.RollbackAvailable = true);

        try
        {
            var button = FindButton(window, "EngineRollbackButton");

            Assert.True(button.IsVisible, "есть куда возвращаться — кнопка обязана быть видна");
            Assert.Contains(section.RollbackTarget, button.Content as string ?? string.Empty, StringComparison.Ordinal);

            Click(button);

            SettleUntil(
                () => window.FindControl<TextBlock>("EngineStatusText")!.Text == section.RollbackAnswer,
                "откат не дошёл до итога");

            Assert.Equal(new[] { section.RollbackQuestion.Text }, asked);
            Assert.Contains("сессии", asked[0], StringComparison.OrdinalIgnoreCase);
            Assert.Single(section.Rollbacks);
            Assert.Equal(section.RollbackAnswer, window.FindControl<TextBlock>("EngineStatusText")!.Text);
        }
        finally
        {
            window.CloseQuietly();
        }

        // Человек сказал «нет» — откат не начинается вовсе.
        var (refused, refusedSection, _) = Stand(
            ask: _ => Task.FromResult(false),
            tune: s => s.RollbackAvailable = true);

        try
        {
            Click(FindButton(refused, "EngineRollbackButton"));

            SettleUntil(
                () => refused.FindControl<TextBlock>("EngineStatusText")!.Text == PanelStrings.EngineUpdateAskCancelled,
                "отказ от отката не сказан словами");

            Assert.Empty(refusedSection.Rollbacks);
            Assert.Equal(PanelStrings.EngineUpdateAskCancelled, refused.FindControl<TextBlock>("EngineStatusText")!.Text);
        }
        finally
        {
            refused.CloseQuietly();
        }
    }

    /// <summary>
    /// НЕПРОВЕРЕННАЯ ВЕРСИЯ — ОТДЕЛЬНОЙ КНОПКОЙ И СНАЧАЛА СЛОВАМИ: первый щелчок показывает
    /// предупреждение с последствием для сессий и НИЧЕГО не спрашивает у двери, второй — работает.
    /// </summary>
    [AvaloniaFact]
    public void Непроверенная_ставится_отдельной_кнопкой_и_сначала_словами()
    {
        var (window, section, asked) = Stand();

        try
        {
            var fresh = FindButton(window, "EngineInstallFreshButton");

            Click(fresh);

            Assert.Empty(asked);
            Assert.Empty(section.Calls);

            var warning = window.FindControl<TextBlock>("EngineStatusText")!.Text!;

            Assert.Contains("журнал", warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(section.Fresh, warning, StringComparison.Ordinal);
            Assert.Equal(PanelStrings.EngineUpdateFreshConfirmButton, fresh.Content as string);

            Click(fresh);

            // Вопрос отдельной кнопки называет СВЕЖУЮ версию — ту, которую человек подтверждает.
            SettleUntil(() => section.Calls.Count == 1, "отдельная кнопка не дошла до работы");

            Assert.Single(asked);
            Assert.Contains("0.2.0-rc.2", asked[0], StringComparison.Ordinal);
            Assert.Equal(new[] { true }, section.Calls);
        }
        finally
        {
            window.CloseQuietly();
        }
    }

    /// <summary>
    /// ОБНОВЛЕНИЕ НЕДОСТУПНО — окно говорит это СЛОВАМИ двери, а не пустой строкой.
    ///
    /// ⚠️ Своя версия при этом ВСЁ РАВНО названа первой строкой: «обновлять нечем» — это ответ
    /// про обновление, а не повод молчать о том, что стоит у человека (решение владельца 14.10.2026).
    /// </summary>
    [AvaloniaFact]
    public void Недоступное_обновление_сказано_словами()
    {
        var (window, section, asked) = Stand(tune: section =>
        {
            section.Available = false;
            section.RefusalKey = EngineInstallRefusals.UpdateUnavailable;
        });

        try
        {
            Assert.Contains(section.Installed, Text(window, "EngineRecommendedText"), StringComparison.Ordinal);

            Assert.Equal(
                EngineInstallPhrases.UpdateRefusal(EngineInstallRefusals.UpdateUnavailable),
                Text(window, "EngineFreshText"));

            Assert.False(FindButton(window, "EngineUpdateButton").IsVisible, "кнопка, которая всегда откажет, — ловушка");
            Assert.False(FindButton(window, "EngineUpdateButton").IsEnabled, "недоступная кнопка обязана быть недоступной");
            Assert.False(FindButton(window, "EngineInstallFreshButton").IsVisible);

            Click(FindButton(window, "EngineUpdateButton"));

            Assert.Empty(asked);
        }
        finally
        {
            window.CloseQuietly();
        }
    }

    // ---------------------------------------------------------------- ХВОСТЫ E3.2/E3.3 (11.10.2026)

    /// <summary>
    /// РЯД — РОВНО ДВЕ КНОПКИ, А ОТКАТ ИДЁТ ТИХОЙ СТРОКОЙ ПОД НИМ (решение владельца 11.10.2026):
    /// «вперёд» и «назад» рядом — разные по смыслу действия, и утверждённая дверь остаётся двумя
    /// кнопками. Проверяется по ДЕРЕВУ и по ГЕОМЕТРИИ: третий орган в ряду и откат, уехавший вбок,
    /// выглядят в разметке безобидно.
    /// </summary>
    [AvaloniaFact]
    public void Ряд_остаётся_двумя_кнопками_а_откат_идёт_тихой_строкой_под_ним()
    {
        var (window, _, _) = Stand(tune: section => section.RollbackAvailable = true);

        try
        {
            var update = FindButton(window, "EngineUpdateButton");
            var fresh = FindButton(window, "EngineInstallFreshButton");
            var rollback = FindButton(window, "EngineRollbackButton");

            var row = update.Parent as StackPanel;

            Assert.NotNull(row);
            Assert.Same(row, fresh.Parent);

            var inRow = row!.Children.OfType<Button>().ToList();

            Assert.Equal(2, inRow.Count);
            Assert.Contains(update, inRow);
            Assert.Contains(fresh, inRow);
            Assert.DoesNotContain(rollback, inRow);

            // Откат — ТИХАЯ строка: гиперссылка, а не кнопка действия.
            Assert.IsType<HyperlinkButton>(rollback);

            // И она НИЖЕ ряда — геометрией, а не порядком строк в разметке.
            var rowBottom = update.TranslatePoint(new Point(0, 0), window)!.Value.Y + update.Bounds.Height;
            var rollbackTop = rollback.TranslatePoint(new Point(0, 0), window)!.Value.Y;

            Assert.True(
                rollbackTop >= rowBottom - 0.5,
                $"откат не под рядом: ряд кончается на {rowBottom:0.#}, откат начинается на {rollbackTop:0.#}");
        }
        finally
        {
            window.CloseQuietly();
        }
    }

    /// <summary>
    /// РЯД КНОПОК БЛОКА ДВИЖКА УМЕЩАЕТСЯ В ШИРИНУ — во ВСЕХ состояниях и при ширине 1080
    /// (решение владельца 11.10.2026: «мерить проверкой»). Эта беда уже была: четвёртая кнопка
    /// уезжала за край. Меряется ГЕОМЕТРИЯ: кнопки на одной вертикали и правая не вышла за
    /// содержимое раздела.
    /// </summary>
    [AvaloniaFact]
    public void Ряд_кнопок_блока_движка_умещается_в_ширину()
    {
        var states = new[]
        {
            (Name: "только рекомендуемая", Fresh: false, Rollback: false),
            (Name: "есть свежая", Fresh: true, Rollback: false),
            (Name: "есть куда откатываться", Fresh: false, Rollback: true),
        };

        foreach (var state in states)
        {
            var (window, _, _) = Stand(tune: section =>
            {
                section.FreshIsNewer = state.Fresh;
                section.FreshInstallable = state.Fresh;
                section.RollbackAvailable = state.Rollback;
            });

            try
            {
                window.Width = 1080;
                PanelTestStand.Settle();

                var content = window.FindControl<ScrollViewer>("ContentScroll")!;

                Assert.True(content.Bounds.Width > 0, "содержимое раздела не измерено — мерить нечего");

                var contentLeft = content.TranslatePoint(new Point(0, 0), window)!.Value.X;
                var contentRight = contentLeft + content.Bounds.Width;

                var buttons = new[] { FindButton(window, "EngineUpdateButton"), FindButton(window, "EngineInstallFreshButton") }
                    .Where(button => button.IsVisible)
                    .ToList();

                Assert.True(buttons.Count >= 1, $"[{state.Name}] в ряду нечего мерить");

                var tops = buttons.Select(button => button.TranslatePoint(new Point(0, 0), window)!.Value.Y).ToList();

                Assert.True(
                    tops.Max() - tops.Min() <= 0.5,
                    $"[{state.Name}] кнопки блока разъехались по строкам: Y = {string.Join(", ", tops.Select(y => y.ToString("0.#")))}");

                foreach (var button in buttons)
                {
                    var left = button.TranslatePoint(new Point(0, 0), window)!.Value.X;
                    var right = left + button.Bounds.Width;

                    Assert.True(button.Bounds.Width > 0, $"[{state.Name}] у кнопки «{button.Content}» нулевая ширина");
                    Assert.True(
                        right <= contentRight + 0.5,
                        $"[{state.Name}] кнопка «{button.Content}» кончается на {right:0.#}, а содержимое раздела — на {contentRight:0.#}; " +
                        $"ширины: {string.Join(", ", buttons.Select(b => $"«{b.Content}»={b.Bounds.Width:0.#}"))}");
                }

                // Откат, когда он есть, тоже обязан уместиться — он живёт в той же карточке.
                if (state.Rollback)
                {
                    var rollback = FindButton(window, "EngineRollbackButton");
                    var right = rollback.TranslatePoint(new Point(0, 0), window)!.Value.X + rollback.Bounds.Width;

                    Assert.True(
                        right <= contentRight + 0.5,
                        $"[{state.Name}] строка отката кончается на {right:0.#}, а содержимое — на {contentRight:0.#}");
                }
            }
            finally
            {
                window.CloseQuietly();
            }
        }
    }

    /// <summary>
    /// ВЫЖИМКА ПРО ВЕРСИИ: строки из файла подсказок показываются в СВОЁМ поле, а когда их нет —
    /// окно говорит это словами. Пустое поле человек прочитал бы как «всё хорошо».
    /// </summary>
    [AvaloniaFact]
    public void Выжимка_показывается_строками_а_пустая_говорит_словами()
    {
        var (window, _, _) = Stand(tune: section => section.Notes = new[] { "первая заметка", "известные беды: одна" });

        try
        {
            var panel = window.FindControl<StackPanel>("EngineNotesPanel")!;

            Assert.Equal(2, panel.Children.Count);
            Assert.Equal("первая заметка", (panel.Children[0] as TextBlock)!.Text);
            Assert.Equal("известные беды: одна", (panel.Children[1] as TextBlock)!.Text);
            // ⚠️ ВЫЖИМКА СВЁРНУТА ПО УМОЛЧАНИЮ (решение владельца 13.10.2026 №6): карточка движка
            // показывает решение и кнопки, а подробности — по ссылке «Показать все версии». Строки
            // при этом ЛЕЖАТ в поле (их видно после щелчка), и это проверяется здесь же.
            Assert.False(window.FindControl<ScrollViewer>("EngineNotesScroll")!.IsVisible, "выжимка раскрыта до щелчка");
            Assert.False(window.FindControl<TextBlock>("EngineNotesEmptyText")!.IsVisible, "пустое состояние видно до щелчка");

            var showAll = window.FindControl<HyperlinkButton>("EngineNotesShowAllButton")!;
            Assert.Equal(PanelStrings.EngineNotesShowAll, showAll.Content);

            showAll.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(HyperlinkButton.ClickEvent));

            Assert.True(window.FindControl<ScrollViewer>("EngineNotesScroll")!.IsVisible, "по ссылке выжимка не раскрылась");
            Assert.False(window.FindControl<TextBlock>("EngineNotesEmptyText")!.IsVisible, "пустое состояние видно до щелчка");
        }
        finally
        {
            window.CloseQuietly();
        }

        var (empty, _, _) = Stand(tune: section => section.Notes = Array.Empty<string>());

        try
        {
            Assert.Empty(empty.FindControl<StackPanel>("EngineNotesPanel")!.Children);
            Assert.False(empty.FindControl<ScrollViewer>("EngineNotesScroll")!.IsVisible, "поле выжимки видно до щелчка (пустой случай)");

            // ПУСТОЕ СОСТОЯНИЕ ГОВОРИТ СЛОВАМИ — но тоже ПОСЛЕ щелчка: свёрнутая карточка молчит.
            var emptyShowAll = empty.FindControl<HyperlinkButton>("EngineNotesShowAllButton")!;
            emptyShowAll.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(HyperlinkButton.ClickEvent));

            Assert.True(empty.FindControl<TextBlock>("EngineNotesEmptyText")!.IsVisible, "пустое поле прочиталось бы как «всё хорошо»");
        }
        finally
        {
            empty.CloseQuietly();
        }
    }

    /// <summary>
    /// ССЫЛКА НА ВЫПУСКИ ДВИЖКА: показывается, когда адрес есть, и открывается **только по щелчку
    /// человека** — до щелчка браузер не зовётся вовсе. Пустой адрес — ссылки нет (ссылка в никуда
    /// хуже её отсутствия).
    /// </summary>
    [AvaloniaFact]
    public void Ссылка_на_выпуски_открывается_только_по_щелчку()
    {
        var opened = new List<string>();
        var (window, section, _) = Stand(opened: opened);

        try
        {
            var link = FindButton(window, "EngineReleasesButton");

            Assert.True(link.IsVisible, "адрес есть — ссылки не видно");
            Assert.Empty(opened);

            Click(link);

            Assert.Equal(new[] { section.ReleasesAddress }, opened);
        }
        finally
        {
            window.CloseQuietly();
        }

        var (none, _, _) = Stand(tune: section => section.ReleasesAddress = string.Empty);

        try
        {
            Assert.False(FindButton(none, "EngineReleasesButton").IsVisible);
        }
        finally
        {
            none.CloseQuietly();
        }
    }

    /// <summary>
    /// ПОРЯДОК КАРТОЧЕК: СНАЧАЛА ВСЁ ПАНЕЛЬНОЕ, ПОТОМ ОДНИМ КУСКОМ «СЕРВЕР DSH».
    ///
    /// **От какого случая (владелец, живой просмотр 13.10.2026).** Его слова: *«changelog о самой
    /// панели надо ближе к карточке обновления панели, а про сервер DSH ниже… получается информация
    /// сначала о панели, кнопки обновления, потом неожиданно про сервер DSH и потом снова карточка
    /// про панель»*. Прежде блок сервера стоял МЕЖДУ карточками панели, и раздел читался вразнобой.
    ///
    /// ⚠️ Меряется ПОРЯДОК ДЕТЕЙ ПАНЕЛИ РАЗДЕЛА (настоящего <see cref="StackPanel"/> показанного
    /// окна), а не порядок строк в разметке: разметку можно переставить и не увидеть ничего, а
    /// человеку видно ровно то, что стоит в панели. Проверка, читающая текст разметки, ловила бы
    /// перестановку — но не ловила бы случай, когда карточка переставлена в ПАНЕЛИ.
    /// </summary>
    [AvaloniaFact]
    public void Порядок_карточек_панельное_выше_сервера()
    {
        var (window, _, _) = Stand();

        try
        {
            var panel = window.FindControl<StackPanel>("UpdateSectionPanel");
            Assert.NotNull(panel);

            var children = panel!.Children.ToList();

            var engines = new[]
            {
                "EngineBlockTitle", "EngineNotesShowAllButton", "EngineNotesTitleText", "EngineReleasesButton",
            };

            // ЗДЕСЬ НАМЕРЕННО НЕТ СЧЁТА КАРТОЧЕК: панель раздела — то, во что окно имеет право
            // дописать своё (карточку ожидания, строку состояния), и число сломалось бы на первой же
            // такой дописи, ничего не сказав о порядке. Порядок держат имена ниже.
            // ⚠️ ИМЕНА ИЩУТСЯ ПО ЛОГИЧЕСКОМУ ДЕРЕВУ, А НЕ ПО НАРИСОВАННОМУ: карточка замены файлов
            // и строки выжимки сервера скрыты до подготовки (`IsVisible="False"`), и в НАРИСОВАННОМ
            // дереве их нет вовсе — проверка порядка не нашла бы саму карточку (поймано прогоном).
            int At(string name) =>
                children.FindIndex(child => Named(child, name));

            // ЛОГИЧЕСКОЕ дерево — рекурсией по своим: у Avalonia 12 нет готового обхода,
            // а `GetVisualDescendants` скрытой карточки не видит вовсе (см. выше).
            static bool Named(ILogical node, string name)
            {
                if (node is Control control && control.Name == name) return true;

                foreach (var child in node.LogicalChildren)
                {
                    if (Named(child, name)) return true;
                }

                return false;
            }

            var panelLast = At("UpdateReplaceCard");
            var engineFirst = At("EngineBlockTitle");

            Assert.True(panelLast >= 0, "карточки замены файлов нет в панели раздела");
            Assert.True(engineFirst >= 0, "карточки сервера DSH нет в панели раздела");

            // ВСЁ ПАНЕЛЬНОЕ ВЫШЕ: та же проверка падает и на «вернуть блок на прежнее место».
            Assert.True(
                panelLast < engineFirst,
                "карточка панели ниже карточки сервера DSH — раздел снова читается вразнобой " +
                $"(замена файлов: {panelLast}, сервер DSH: {engineFirst})");

            foreach (var name in new[] { "UpdateVerdictText", "UpdateCheckButton", "UpdateNotesTitleText" })
            {
                var at = At(name);
                Assert.True(at >= 0, $"панельной карточки «{name}» нет в панели раздела");
                Assert.True(at < engineFirst, $"«{name}» оказалось НИЖЕ сервера DSH");
            }

            // И ВЕСЬ БЛОК СЕРВЕРА DSH — ОДНИМ КУСКОМ, без панельных карточек внутри: «вторым блоком»
            // значит после панельного, а не между его карточками.
            foreach (var name in engines)
            {
                var at = At(name);
                Assert.True(at >= 0, $"части блока сервера DSH «{name}» нет в панели раздела");
                Assert.True(at >= engineFirst, $"«{name}» стоит выше начала блока сервера DSH — блок разорван");
            }

            Assert.True(
                At("EngineNotesShowAllButton") > At("UpdateNotesTitleText"),
                "«Показать все версии» сервера DSH оказалось выше «что нового» панели");

            // ⚠️ И ВТОРАЯ ПОЛОВИНА — ГЕОМЕТРИЕЙ, А НЕ ТОЛЬКО ПОРЯДКОМ ДЕТЕЙ. Порядок детей верен
            // и тогда, когда карточка уехала за край экрана или получила высоту в никуда; человеку
            // же важно, ЧТО ВЫШЕ НА ЭКРАНЕ. Поэтому окно прокручивается до конца раздела и Y всех
            // видимых строк берётся в точках окна (приём `SettingsEngineBlockTests` же — строки 479+).
            var content = window.FindControl<ScrollViewer>("ContentScroll");
            Assert.NotNull(content);

            content!.Offset = new Vector(0, content.Extent.Height);
            PanelTestStand.Settle();

            var chain = new[]
            {
                "UpdateVerdictText", "UpdateCheckButton", "UpdateNotesTitleText",
                "EngineBlockTitle", "EngineNotesShowAllButton",
            };

            var tops = new List<(string Name, double Y)>();

            foreach (var name in chain)
            {
                var control = window.FindControl<Control>(name);
                Assert.NotNull(control);
                Assert.True(control!.IsVisible, $"«{name}» не видно — мерить нечего");

                var top = control.TranslatePoint(new Point(0, 0), window);
                Assert.NotNull(top);
                tops.Add((name, top!.Value.Y));
            }

            for (var i = 1; i < tops.Count; i++)
            {
                Assert.True(
                    tops[i - 1].Y < tops[i].Y,
                    $"«{tops[i].Name}» стоит НЕ НИЖЕ «{tops[i - 1].Name}»: " +
                    $"{tops[i - 1].Y:0.#} против {tops[i].Y:0.#} — раздел читается вразнобой");
            }
        }
        finally
        {
            window.CloseQuietly();
        }
    }
    /// <summary>
    /// В СОСТОЯНИИ «СЕРВЕРА DSH НЕТ» КНОПКА ЕСТЬ И НАЗЫВАЕТ УСТАНОВКУ (A5 §7, находка F-1).
    ///
    /// **От какого случая.** На чистой машине карточка говорила «У вас: DSH — версию прочитать
    /// не удалось» и **не показывала ни одной кнопки**: дорога к установке была тупиком ровно у того,
    /// кому она нужнее всего. Теперь кнопка видима и подписана «Установить», а её подсказка называет
    /// ТУ ЖЕ кнопку (прежде подсказка звала «Обновить» — которой в этом состоянии нет).
    ///
    /// **Мутации, которые обязаны упасть:** «видимость снова `standing == Older`» · «подпись всегда
    /// «Обновить»» · «подсказка карточки собирается безусловно».
    /// </summary>
    [AvaloniaFact]
    public void В_состоянии_без_движка_кнопка_есть_и_называет_установку()
    {
        var (window, _, _) = Stand(tune: section =>
        {
            section.EnginePresent = false;
            section.Installed = string.Empty;
        });

        try
        {
            var button = FindButton(window, "EngineUpdateButton");

            Assert.True(button.IsVisible, "в состоянии «сервера нет» кнопки установки нет — дорога в тупик");
            Assert.Equal(PanelStrings.EngineInstallButton, button.Content);

            // СВОЯ ВЕРСИЯ — СВОИМИ СЛОВАМИ: «не найден», а не «версию прочитать не удалось».
            Assert.Equal(PanelStrings.EngineOwnVersionAbsent, Text(window, "EngineRecommendedText"));

            // ПОДСКАЗКА НАЗЫВАЕТ ТУ ЖЕ КНОПКУ (F-1): «Установить», а не «Обновить».
            Assert.Equal(
                SettingsWindow.EngineHintParts(EngineVersionStanding.Absent, freshButtonVisible: false),
                Text(window, "EngineHintText"));
            Assert.Contains("Установить", Text(window, "EngineHintText"), StringComparison.Ordinal);
            Assert.DoesNotContain(PanelStrings.EngineUpdateFreshHint, Text(window, "EngineHintText"), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// СТОЯЩАЯ ВЕРСИЯ СТАРШЕ — КНОПКА «ОБНОВИТЬ», И ПОДСКАЗКА ПРО НЕЁ (прежнее поведение не сломано).
    ///
    /// ⚠️ И вторая половина подсказки (про непроверенную версию) показывается ТОЛЬКО вместе
    /// со своей кнопкой: у неё `FreshInstallable = false`, значит кнопки нет — и подсказки тоже.
    /// </summary>
    [AvaloniaFact]
    public void Стоящая_версия_старше_даёт_обновление_и_подсказку_про_видимую_кнопку()
    {
        var (window, _, _) = Stand(tune: section =>
        {
            section.EnginePresent = true;
            section.Installed = "0.1.4-rc.2";
            section.FreshInstallable = false;   // свежая без замка: кнопки нет
        });

        try
        {
            var button = FindButton(window, "EngineUpdateButton");

            Assert.True(button.IsVisible);
            Assert.Equal(PanelStrings.EngineUpdateButton, button.Content);
            Assert.Equal(PanelStrings.EngineUpdateMainHint, Text(window, "EngineHintText"));
            Assert.DoesNotContain(PanelStrings.EngineUpdateFreshHint, Text(window, "EngineHintText"), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// ПУСТАЯ ВЕРСИЯ ПРИ НАЙДЕННОМ СЕРВЕРЕ DSH НЕ ВЫДАЁТСЯ ЗА ОТСУТСТВИЕ (A5 §7).
    ///
    /// **Зачем отдельно.** Соблазн ровно один: считать `Absent` по пустой строке версии. Тогда
    /// «версию не прочитали» стало бы «сервера нет» — и панель предложила бы установку на машине,
    /// где сервер стоит. Признак присутствия приходит ОТДЕЛЬНЫМ фактом, и это здесь проверяется.
    ///
    /// **Мутация, которая обязана упасть:** «Absent выставляется по `installed.Length == 0`».
    /// </summary>
    [AvaloniaFact]
    public void Пустая_версия_при_найденном_сервере_не_выдаётся_за_отсутствие()
    {
        var (window, _, _) = Stand(tune: section =>
        {
            section.EnginePresent = true;       // сервер DSH на машине ЕСТЬ
            section.Installed = string.Empty;   // а версию прочитать не удалось
        });

        try
        {
            Assert.Equal(PanelStrings.EngineOwnVersionUnknown, Text(window, "EngineRecommendedText"));

            var button = FindButton(window, "EngineUpdateButton");

            // «Версию не прочитали» — это НЕ «сервера нет»: кнопка остаётся прежней («Обновить»),
            // и подпись «Установить» сюда не приходит.
            Assert.Equal(PanelStrings.EngineUpdateButton, button.Content);
            Assert.DoesNotContain(PanelStrings.EngineOwnVersionAbsent, Text(window, "EngineRecommendedText"), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }
}