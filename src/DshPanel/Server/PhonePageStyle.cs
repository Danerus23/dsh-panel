namespace DshPanel.Server;

/// <summary>
/// ВИД СТРАНИЦЫ ТЕЛЕФОНА — ОДНА ТАБЛИЦА СТИЛЕЙ НА ОБЕ СТРАНИЦЫ (решение владельца 11.10.2026).
///
/// **От какого случая.** Владелец открыл страницу с телефона и сказал: *«выглядит ужасно, без
/// разметки, как будто на HTML»*. Так и было: имена классов (`hint`, `problem`) в разметке стояли,
/// а правил к ним не существовало ни одного — страница отдавалась голым текстом.
///
/// ⚠️ **Это ПЕРЕПИСАННАЯ таблица: правила перенесены ДОСЛОВНО из выбранного владельцем макета
/// варианта A** (`_phone-mock\A-light.html:7-95`, `A-dark.html` — тёмная тема отличается только
/// принудительным `[data-theme="dark"]`, которого у нас нет). Дословность — не украшение:
/// **красоту судит глаз владельца на телефоне**, и макет — единственное, что он уже одобрил.
/// Числа контраста (5,38 светлая пара / 8,11 тёмная) **замерены в макете** и здесь не пересчитаны
/// (`_design-library\30-mobile-web.md` §10б, поправка 11.10.2026).
///
/// **Почему ОДНА таблица и почему ВНУТРИ страницы.**
///
/// * **внутри** — потому что страница обязана работать офлайн, в глухой домашней сети: ни `<link>`,
///   ни шрифтов, ни картинок из сети, ни `@import`, ни `url(` здесь не будет НИКОГДА. Страницу
///   открывает телефон, у которого, кроме этой панели, ничего может и не быть;
/// * **одна на обе страницы** (вход и телефон) — потому что это один продукт: разойдись они,
///   человек увидит два разных приложения на одном адресе. Обе страницы вставляют ЭТУ строку
///   в `<style>`, и второй таблицы в панели нет (`_design-standard-draft.md` §2.2; проверка
///   `PhonePageTests.Вид_живёт_одной_таблицей_стилей_внутри_страницы`).
///
/// **Что чем держится (нормы — со ссылкой, где нормы нет — так и сказано):**
///
/// * `.card{max-width:60ch}` — длина строки 50–60 знаков (`_design-standard-draft.md` §1.5 и §2.7;
///   потолок WCAG 1.4.8 — 80 знаков, для китайского 40). Прежняя редакция давала 640 точек, то есть
///   80+ знаков на 16 точках, и это была названная правка, а не то, что уже есть;
/// * `.opt-body{min-height:44px}` и `.autobox{min-height:44px}` — палец: WCAG 2.2 SC 2.5.5 (AAA)
///   «at least 44 by 44 CSS pixels», Apple HIG «default control size 44×44 pt»
///   (`_design-library\30-mobile-web.md` §3, `_design-standard-draft.md` §2.4);
/// * `gap:8px` между вариантами — web.dev «at least 8 pixels of spacing» (там же, §3): прежние
///   6 точек были норму ниже;
/// * `.opt:has(input:checked) .opt-body` — подложка И рамка ТОЙ ЖЕ ТОЛЩИНЫ (2 точки), то есть
///   состояние не меняет размер (`_design-standard-draft.md` §1.11) — и то же правило видно глазом;
/// * `button:focus-visible,input:focus-visible,label:focus-visible` — видимый фокус, `outline`
///   2 точки и отступ 2 (`_design-library\30-mobile-web.md` §8; WCAG 2.2 SC 2.4.13: «2 CSS pixel
///   thick perimeter», «contrast change of at least 3:1»);
/// * `.legend` и `.group{fieldset,legend}` — читалка связывает вопрос с его вариантами
///   (GOV.UK question pages, там же §8);
/// * `.hint{font-size:14px}` — кегль из шкалы панели (`_design-standard-draft.md` §3.2, 14/20);
///   для китайского подсказка 14 — предел нормы «не меньше 14 px на основном тексте» (§2.5);
/// * `.action`, `.ghost` — цель во всю ширину и высотой от 44: ответ жмут одной рукой и с ходу;
/// * `.auto` — «Проверить» уходит вниз ОДНОЙ тихой строкой: главное действие на экране одно
///   (`_design-standard-draft.md` §1.7: одно главное действие на карточку; у нормы числа нет —
///   это эвристика Нильсена №8, названная в банке);
/// * цвета — переменными `:root` в обеих темах (`_design-standard-draft.md` §2.6: у телефона нет
///   своего переключателя темы, только системный).
///
/// ⚠️ **Чего здесь НЕТ и почему:** ни одного внешнего адреса, ни одного `url(` и ни одного шрифта
/// из сети — всё это сломало бы работу офлайн и потянуло бы страницу в интернет, где панели нет.
/// Ни `style="…"` по разметке, ни второй таблицы: и то и другое — названное нарушение §2.2.
/// </summary>
public static class PhonePageStyle
{
    /// <summary>Единственная таблица стилей панели. Вставляется в обе страницы как есть.</summary>
    public const string Css =
        ":root{--page:#F0F0F0;--card:#FFFFFF;--ink:#1A1A1A;--ink-2:#666666;--line:#828282;"
        + "--accent:#0F6CBD;--accent-ink:#FFFFFF;--accent-wash:#0F6CBD1F;--focus:#000000;"
        + "--warn:#8A6508;--bad:#C62828}"
        + "@media (prefers-color-scheme: dark){:root{--page:#1F1F1F;--card:#2B2B2B;--ink:#F0F0F0;"
        + "--ink-2:#B0B0B0;--line:#828282;--accent:#4CC2FF;--accent-ink:#10222E;"
        + "--accent-wash:#4CC2FF47;--focus:#FFFFFF;--warn:#E8B84B;--bad:#F08A8A}}"
        + "*{box-sizing:border-box}"
        + "body{margin:0;padding:24px;background:var(--page);color:var(--ink);"
        + "font-family:system-ui,\"Segoe UI\",Roboto,sans-serif;font-size:16px;line-height:24px}"
        + ".card{max-width:60ch;margin:0 auto;padding:16px;background:var(--card);border-radius:8px}"
        + ".screen-title{font-size:20px;line-height:28px;font-weight:600;margin:0 0 12px}"
        + ".note{margin:0 0 12px}"
        + ".hint{font-size:14px;line-height:20px;color:var(--ink-2);margin:0 0 12px}"
        + ".count{font-size:14px;line-height:20px;color:var(--ink-2);margin:0 0 8px}"
        + ".problem{margin:0 0 12px;color:var(--bad);font-weight:600}"
        + ".error{margin:0 0 8px;color:var(--bad)}"
        + ".warn{margin:0 0 12px;color:var(--warn)}"
        + ".group{margin:0 0 16px;padding:0;border:0}"
        + ".legend{display:block;width:100%;padding:0;font-size:20px;line-height:28px;font-weight:600;"
        + "margin:0 0 8px}"
        + ".opts{display:flex;flex-direction:column;gap:8px;margin:0 0 12px;padding:0;border:0}"
        + ".opt{display:block;padding:0;margin:0;cursor:pointer}"
        + ".opt-body{display:flex;align-items:flex-start;gap:8px;min-height:44px;padding:12px;"
        + "background:var(--card);border:2px solid var(--line);border-radius:4px}"
        + ".opt-input{width:24px;height:24px;margin:8px 0 0;flex:0 0 auto;accent-color:var(--accent)}"
        + ".opt-text{display:block;flex:1 1 auto}"
        + ".opt:focus-within .opt-body{box-shadow:0 0 0 2px var(--focus)}"
        + ".opt:has(input:checked) .opt-body{border-color:var(--accent);background:var(--accent-wash)}"
        + ".opt:has(input:checked) .own{display:none}"
        + ".field-label{display:block;margin:0 0 4px}"
        + ".code{width:100%;min-height:44px;padding:8px 12px;font-family:inherit;font-size:20px;"
        + "line-height:28px;font-weight:700;color:var(--ink);background:var(--card);"
        + "border:2px solid var(--line);border-radius:4px}"
        + ".code-bad{border-color:var(--bad)}"
        + ".own-input{width:100%;min-height:44px;padding:8px 12px;font-family:inherit;font-size:16px;"
        + "line-height:24px;color:var(--ink);background:var(--card);border:2px solid var(--line);"
        + "border-radius:4px}"
        + ".action{display:block;width:100%;min-height:44px;padding:8px 16px;font-family:inherit;"
        + "font-size:16px;line-height:24px;font-weight:600;color:var(--accent-ink);"
        + "background:var(--accent);border:2px solid transparent;border-radius:4px;cursor:pointer}"
        + ".ghost{display:block;width:100%;min-height:44px;padding:8px 16px;font-family:inherit;"
        + "font-size:16px;line-height:24px;font-weight:600;color:var(--ink);background:var(--card);"
        + "border:2px solid var(--line);border-radius:4px;cursor:pointer}"
        + ".narrow{display:inline-block;width:auto}"
        + ".wide{margin-top:8px}"
        + ".auto{display:flex;gap:8px;align-items:center;margin:0 0 12px}"
        + "button:focus-visible,input:focus-visible,label:focus-visible,a:focus-visible{"
        + "outline:2px solid var(--focus);outline-offset:2px}"
        + "hr{border:0;border-top:1px solid var(--line);margin:16px 0}";
}
