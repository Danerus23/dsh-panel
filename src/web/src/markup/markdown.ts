// РАЗМЕТКА РЕПЛИКИ АГЕНТА (S3c): marked 18.2.0 → DOMPurify 3.4.17 с ЯВНЫМ конфигом границ.
//
// Факты A1 (`_team\s3c\a1-parser.md`), на которых стоит этот файл:
//   * у marked 18.2.0 опции `html` НЕТ ВООБЩЕ — сырой HTML гасится только перекрытием рендерера
//     (`use({renderer:{html:()=>""}})`); а `options.renderer` частичным объектом ломает парсер
//     (`this.renderer.heading is not a function`), поэтому перекрытия идут через `use()` —
//     marked сливает их с базовым рендерером сам;
//   * ссылки marked НЕ фильтрует (`javascript:`, `data:`, `//host` выходят в `href` как есть) —
//     схемы режет только санитайзер через `ALLOWED_URI_REGEXP`;
//   * умолчания DOMPurify границы владельца НЕ держат: без конфига проходят `style=`, `<img>`
//     и `<table>` — поэтому конфиг ниже сужает теги и атрибуты явно;
//   * `data:`-картинку CSP пропускает (`img-src 'self' data:`), значит «картинок нет» обязан
//     обеспечить КОД: рендерер отдаёт alt-текст, а `<img>` стоит в `FORBID_TAGS`.
//
// Правила вида — D1 (`_team\s3c\d1-vid.md`): заголовки опускаются рендерером до h4–h6,
// таблица остаётся ТЕКСТОМ, у ссылок нет `target` (та же вкладка) и нет `title`.

import DOMPurify from "dompurify";
import { Marked } from "marked";

/** Теги, которые вообще могут появиться в ленте: ровно то, что перечислено в D1 §2. */
export const MARKUP_ALLOWED_TAGS = [
  "p",
  "br",
  "strong",
  "em",
  "del",
  "code",
  "pre",
  "blockquote",
  "ul",
  "ol",
  "li",
  "h4",
  "h5",
  "h6",
  "a",
  "hr",
] as const;

/**
 * Атрибуты — только эти два: `href` (ссылка) и `class` (его ставит marked для `language-*`;
 * в CSS он НЕ используется — вид держится на тегах, D1 §7 п. 3). `title` и `target` не разрешены:
 * на телефоне наведения нет, а ссылка открывается в той же вкладке (D1 §5–§6).
 */
export const MARKUP_ALLOWED_ATTR = ["href", "class"] as const;

/** Явный конфиг санитайзера — границы владельца: ни style, ни картинок, ни таблиц, ни сырого HTML. */
export const MARKUP_PURIFY = {
  ALLOWED_TAGS: [...MARKUP_ALLOWED_TAGS],
  ALLOWED_ATTR: [...MARKUP_ALLOWED_ATTR],
  ALLOW_DATA_ATTR: false,
  // Ссылки — только http/https. `javascript:`, `data:`, `ftp:`, `//host` и относительные
  // остаются `<a>` без `href`, то есть обычным текстом (D1 §5).
  ALLOWED_URI_REGEXP: /^https?:\/\//iu,
  FORBID_TAGS: [
    "img",
    "table",
    "style",
    "script",
    "svg",
    "math",
    "iframe",
    "object",
    "embed",
    "form",
    "input",
    "template",
    "noscript",
    "h1",
    "h2",
    "h3",
  ],
  FORBID_ATTR: ["style", "target", "title"],
};

/** Экранирование текста, который рендерер отдаёт как ТЕКСТ (alt картинки, аварийный путь). */
function escapeText(text: string): string {
  return text
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

/**
 * Парсер с перекрытиями. `use()` (а не `options.renderer`) — потому что частичный рендерер
 * в опциях marked 18.2.0 роняет парсер.
 */
const parser = new Marked({ gfm: true, breaks: false });

parser.use({
  renderer: {
    /** Сырой HTML — НЕ орган: инлайн-теги гасятся, текст между ними остаётся (D1 §7). */
    html() {
      return "";
    },

    /** Картинка — её alt-текст обычным текстом; пустой alt — ничего (D1 §7). */
    image({ text }) {
      return escapeText(text ?? "");
    },

    /**
     * Заголовок опускается РЕНДЕРЕРОМ, а не CSS: на экране уже есть `h1` сессии и `h2` разделов,
     * и `h1` внутри реплики читалка объявила бы новой страницей (D1 §3, база `03` п. 9).
     */
    heading({ tokens, depth }) {
      const level = depth <= 2 ? 4 : depth <= 4 ? 5 : 6;
      return `<h${level}>${this.parser.parseInline(tokens)}</h${level}>\n`;
    },

    /**
     * Таблица — не орган, но её СОДЕРЖИМОЕ остаётся текстом (D1 §7): строки через « | »,
     * каждая строка — своей строкой. Промолчать там, где агент сказал, нельзя.
     */
    table(token) {
      const rows = [token.header, ...token.rows];
      const lines = rows.map((row) =>
        row.map((cell) => this.parser.parseInline(cell.tokens)).join(" | "),
      );
      return `<p>${lines.join("<br>")}</p>\n`;
    },
  },
});

/**
 * Кэш по ТЕКСТУ записи (A1 §6, п. 2): санитайз дороже разбора примерно в десять раз и линеен
 * по размеру, а лента перерисовывается на каждом событии потока. Значит разметка считается
 * ОДИН раз на запись, а не на отрисовку. Ключ — сам текст: одинаковый текст даёт одинаковый HTML.
 */
const CACHE = new Map<string, string>();
/** Потолок кэша: лента длинная, а память телефона не бесконечная — старые записи вытесняются. */
export const MARKUP_CACHE_MAX = 200;

/** Сброс кэша — для замеров «первая отрисовка / повторная» и для тестов. */
export function resetMarkupCache(): void {
  CACHE.clear();
}

/** Сколько записей сейчас в кэше (для проверки «кэш работает»). */
export function markupCacheSize(): number {
  return CACHE.size;
}

function parse(text: string): string {
  try {
    return parser.parse(text, { async: false });
  } catch {
    // Разбор не удался — реплика не пропадает: показываем её текстом (названная деградация).
    return `<p>${escapeText(text)}</p>`;
  }
}

function sanitize(html: string): string {
  return DOMPurify.sanitize(html, MARKUP_PURIFY);
}

/**
 * HTML реплики агента: `render → sanitize → set`. Сырая строка парсера в DOM не попадает никогда —
 * `dangerouslySetInnerHTML` получает только результат этой функции.
 */
export function renderMarkup(text: string): string {
  if (text.trim() === "") return "";
  const cached = CACHE.get(text);
  if (cached !== undefined) return cached;
  const html = sanitize(parse(text));
  CACHE.set(text, html);
  if (CACHE.size > MARKUP_CACHE_MAX) {
    const oldest = CACHE.keys().next().value;
    if (oldest !== undefined) CACHE.delete(oldest);
  }
  return html;
}
