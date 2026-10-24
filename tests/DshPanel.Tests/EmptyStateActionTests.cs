using System.Text.RegularExpressions;
using DshPanel.Localization;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПУСТОЕ СОСТОЯНИЕ ЗОВЁТ ДАЛЬШЕ, ОШИБКА НАЗЫВАЕТ ПРИЧИНУ И ВЫХОД (DEF-043, 2.4.1).
///
/// **От какого случая.** Замер 22.10.2026 (`_team\design-pass\p6-words.md` §4.1 и §5): из 120
/// пустых состояний следующий шаг словами был у ПЯТИ; а пять ошибок были тупиками — «Не удалось
/// сохранить настройки», «GitHub ответил HTTP {0}», «ответ GitHub не разобрался», «ключ не принят»
/// и «Не удалось изменить автозапуск»: человек оставался один на один с фактом, без причины и без
/// двери. Норма — Microsoft «Writing style» (что случилось + что будет + РЕАЛЬНОЕ решение),
/// Apple HIG «Writing» («Provide clear next steps on any blank screens»), GOV.UK error message.
///
/// **Почему проверка устроена так, а не «в строке есть глагол».** Глагол в строке есть почти
/// всегда («ответил», «не удалось») — такая проверка слепа: она зелёная и тогда, когда действия
/// нет. Поэтому у каждого ключа названо ТРИ вещи, и каждая проверяется отдельно:
/// <list type="number">
/// <item>ЗОВ — повелительное действие из набора языка («нажмите», «press», 「请按」…);</item>
/// <item>ДВЕРЬ — подпись настоящей кнопки, взятая из ТОГО ЖЕ словаря (`BackupCreateButton`,
/// `UpdateCheckNowButton`…), а не литерал проверки: переименуют кнопку — покраснеет и здесь;</item>
/// <item>ПРИЧИНА — чем случившееся названо (у честного «причина неизвестна» — им же).</item>
/// </list>
/// У трёх ключей кнопки нет вовсе (автозапуск — галочка, телефон — раздел, ключ модели — DSH):
/// там дверью работает повелительное действие и названное место, и это записано в таблице.
/// </summary>
public class EmptyStateActionTests
{
    /// <summary>Десять ключей реестра DEF-043 на трёх языках — 30 строк, перебор по всем.</summary>
    private sealed record Case(string Key, string? Door, string Ru, string En, string Zh);

    private static readonly Case[] Cases =
    {
        // --- пустые состояния: зовут дальше ---
        new("BackupListEmpty", nameof(PanelStrings.BackupCreateButton),
            "Копий в этой папке нет", "no backups in this folder", "没有备份"),

        new("PricingHistoryNeverChecked", nameof(PanelStrings.PeakRefreshButton),
            "ни разу не читала цены", "never read the prices", "尚未读取过价格"),

        new("PriceNeverRead", nameof(PanelStrings.PeakRefreshButton),
            "Цены ещё не читались", "prices have not been read", "尚未读取价格"),

        // Дверь — раздел «Телефон»: сопряжение идёт кодом с телефона, кнопки в панели нет.
        new("PhoneDevicesEmpty", nameof(PanelStrings.PhoneSectionTitle),
            "ещё не подключался", "has not connected yet", "还没有连接过"),

        new("UpdateNeverChecked", nameof(PanelStrings.UpdateCheckNowButton),
            "ещё не проверялось", "have not been checked yet", "尚未检查更新"),

        // --- ошибки-тупики: причина и выход ---
        new("SettingsSaveFailed", nameof(PanelStrings.SettingsSave),
            "причина неизвестна", "the reason is unknown", "原因不明"),

        // Автозапуск — галочка, кнопки нет: выход назван действием («повторите»/«try again»).
        new("AutostartFailed", null,
            "причина неизвестна", "the reason is unknown", "原因不明"),

        new("UpdateBadJson", nameof(PanelStrings.UpdateCheckNowButton),
            "не удалось разобрать", "could not be parsed", "无法解析"),

        new("UpdateHttpFailedFormat", nameof(PanelStrings.UpdateCheckNowButton),
            "ответ сервера обновлений", "answer of the update server", "更新服务器的回答"),

        // Ключ модели живёт в DSH — там и меняется; кнопки панели у этого выхода нет.
        new("BalanceKeyRejected", null,
            "не принял ключ модели", "rejected the model key", "未接受模型密钥"),
    };

    /// <summary>
    /// Повелительное действие — ядро проверки. Набор у каждого языка свой и намеренно узкий:
    /// только то, что человеку говорит «сделай», а не рассказывает «что было».
    /// </summary>
    private static readonly Dictionary<string, string[]> Calls = new(StringComparer.Ordinal)
    {
        ["ru"] = new[] { "нажмите", "откройте", "повторите", "проверьте", "возьмите", "впишите", "запустите", "введите" },
        ["en"] = new[] { "press", "open the", "try again", "check that", "take the", "enter it", "start the panel" },
        ["zh"] = new[] { "请按", "请打开", "请重试", "请检查", "请再按", "请稍后按", "请在那里填写", "请用", "请输入" },
    };

    /// <summary>Языки — те же три, что у панели; других проверка не знает и знать не должна.</summary>
    private static readonly string[] Languages = Loc.Known;

    [Fact]
    public void Пустое_зовёт_а_ошибка_называет_причину_и_выход()
    {
        Assert.Equal(new[] { "ru", "en", "zh" }, Languages);
        Assert.Equal(10, Cases.Length);

        // Перебор по ВСЕМ 10 ключам × 3 языка = 30 строк, а не «хотя бы одно».
        var checkedLines = 0;
        var bad = new List<string>();

        foreach (var item in Cases)
        {
            foreach (var language in Languages)
            {
                var dictionary = Loc.Dictionary(language);
                Assert.True(dictionary.ContainsKey(item.Key), $"{language}: ключа {item.Key} нет в словаре");

                var text = dictionary[item.Key];
                checkedLines++;

                // 1. ЗОВ: повелительное действие своего языка.
                if (!Calls[language].Any(call => text.Contains(call, StringComparison.OrdinalIgnoreCase)))
                {
                    bad.Add($"{language}/{item.Key}: нет действия («{text}»)");
                }

                // 2. ДВЕРЬ: подпись настоящей кнопки из ЭТОГО ЖЕ словаря.
                if (item.Door is not null)
                {
                    var label = dictionary[item.Door];
                    Assert.False(string.IsNullOrWhiteSpace(label), $"{language}: у {item.Door} пустая подпись");

                    if (!text.Contains(label, StringComparison.Ordinal))
                    {
                        bad.Add($"{language}/{item.Key}: не названа дверь «{label}» («{text}»)");
                    }
                }

                // 3. ПРИЧИНА: чем случившееся названо.
                var cause = language switch
                {
                    "ru" => item.Ru,
                    "en" => item.En,
                    _ => item.Zh,
                };

                if (!text.Contains(cause, StringComparison.OrdinalIgnoreCase))
                {
                    bad.Add($"{language}/{item.Key}: не названа причина «{cause}» («{text}»)");
                }

                // 4. ЛИШНИХ ПОДСТАНОВОК НЕТ: технический код человеку выносят только там, где он
                //    и есть объяснение (код ответа HTTP), и ровно одну.
                var expected = item.Key == "UpdateHttpFailedFormat" ? 1 : 0;
                var placeholders = Regex.Matches(text, @"\{\d+\}").Count;

                if (placeholders != expected)
                {
                    bad.Add($"{language}/{item.Key}: подстановок {placeholders}, ожидалось {expected}");
                }
            }
        }

        Assert.Equal(30, checkedLines);
        Assert.True(bad.Count == 0, "тупики без действия вернулись: " + Environment.NewLine + string.Join(Environment.NewLine, bad));
    }
}
