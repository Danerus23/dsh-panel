// Разметка реплики агента (S3c): что показываем, что гасим и чем это проверяется.
//
// Набор — прямой перечень границ владельца (решение 31.10.2026) и правил D1:
// ни сырого HTML, ни картинок, ни таблиц, ни `style=`, ссылки только http/https и в той же вкладке.
// Санитайзер обязателен: его умолчания эти границы НЕ держат (A1 §4.2), поэтому проверяем результат,
// а не «мы аккуратно написали».

import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { MARKUP_PURIFY, markupCacheSize, renderMarkup, resetMarkupCache } from "./markdown";

/** HTML разметки → DOM, как его получит браузер. */
function dom(text: string): HTMLElement {
  const host = document.createElement("div");
  host.innerHTML = renderMarkup(text);
  return host;
}

describe("разметка реплики агента", () => {
  it("абзац, жирный, курсив, зачёркнутое, инлайн-код и цитата", () => {
    const host = dom("Абзац **жирный** *курсив* ~~старое~~ `код` и хвост\n\n> цитата");
    expect(host.querySelectorAll("p")).toHaveLength(2);
    expect(host.querySelector("strong")?.textContent).toBe("жирный");
    expect(host.querySelector("em")?.textContent).toBe("курсив");
    expect(host.querySelector("del")?.textContent).toBe("старое");
    expect(host.querySelector("blockquote")?.textContent).toContain("цитата");
    // Инлайн-код — ВНУТРИ абзаца, а не блоком (D1 В-1): иначе он ломает строку.
    const inline = host.querySelector("p code");
    expect(inline?.textContent).toBe("код");
    expect(inline?.closest("pre")).toBeNull();
  });

  it("заголовки разметки опущены рендерером до h4–h6, h1–h3 не появляются", () => {
    const host = dom("# раз\n\n## два\n\n### три\n\n#### четыре\n\n##### пять\n\n###### шесть");
    expect(host.querySelectorAll("h1")).toHaveLength(0);
    expect(host.querySelectorAll("h2")).toHaveLength(0);
    expect(host.querySelectorAll("h3")).toHaveLength(0);
    expect([...host.querySelectorAll("h4,h5,h6")].map((node) => node.tagName)).toEqual([
      "H4",
      "H4",
      "H5",
      "H5",
      "H6",
      "H6",
    ]);
  });

  it("списки и блок кода с классом языка", () => {
    const host = dom("- раз\n- два\n  - вложенный\n\n```js\nconst a = 1;\n```");
    expect(host.querySelectorAll("ul > li").length).toBeGreaterThanOrEqual(3);
    const code = host.querySelector("pre > code");
    expect(code?.textContent).toContain("const a = 1;");
    // `class` разрешён (его ставит marked), но вид на нём не держится — только тег.
    expect(code?.getAttribute("class")).toBe("language-js");
  });

  it("сырой HTML — текстом, а не органом: <b> теряет тег, <script>/<style> исчезают", () => {
    const host = dom(
      "до <b>привет</b> после\n\n<script>alert(1)</script>\n\n<style>body{color:red}</style>",
    );
    expect(host.querySelector("b")).toBeNull();
    expect(host.querySelector("script")).toBeNull();
    expect(host.querySelector("style")).toBeNull();
    expect(host.textContent).toContain("привет");
    expect(host.textContent).toContain("до");
    expect(host.textContent).toContain("после");
    expect(host.innerHTML).not.toContain("<script");
    expect(host.innerHTML).not.toContain("style=");
  });

  it("картинка — её alt-текстом; в DOM нет ни одного <img> и ни одного data:-источника", () => {
    const host = dom("![alt текст](https://example.com/x.png)\n\n![](data:image/png;base64,AAAA)");
    expect(host.querySelector("img")).toBeNull();
    expect(host.innerHTML).not.toContain("data:image");
    expect(host.textContent).toContain("alt текст");
  });

  it("таблица — не орган, но её содержимое остаётся ТЕКСТОМ, а не пропадает", () => {
    const host = dom("| имя | итог |\n| --- | --- |\n| сборка | зелёная |");
    expect(host.querySelector("table")).toBeNull();
    expect(host.querySelector("tr,td,th")).toBeNull();
    expect(host.textContent).toContain("имя | итог");
    expect(host.textContent).toContain("сборка | зелёная");
  });

  it("ссылки: http/https остаются ссылкой в той же вкладке, прочие схемы — текстом", () => {
    const host = dom(
      [
        "[годная](https://example.com/x)",
        "[secure](http://example.com/y)",
        "[зло](javascript:alert(1))",
        "[данные](data:text/html,x)",
        "[фтп](ftp://example.com/z)",
        "[хост](//evil.example/z)",
        "[рядом](/session/1)",
      ].join(" и "),
    );
    const links = [...host.querySelectorAll("a")];
    const withHref = links.filter((node) => node.hasAttribute("href"));
    expect(withHref.map((node) => node.getAttribute("href"))).toEqual([
      "https://example.com/x",
      "http://example.com/y",
    ]);
    // Не-http схемы: элемент остаётся, но перехода нет — это обычный текст (D1 §5).
    expect(links.length).toBe(7);
    for (const link of links) {
      expect(link.hasAttribute("target"), "target не ставится — та же вкладка").toBe(false);
      expect(link.hasAttribute("title"), "title отброшен").toBe(false);
    }
  });

  it("XSS-набор: ни скриптов, ни обработчиков, ни style, ни встроенных фреймов", () => {
    const attacks = [
      '<img src=x onerror="alert(1)">',
      "<script>alert(2)</script>",
      '<svg><style><img src=x onerror="alert(3)"></style></svg>',
      '<math><mtext><table><mglyph><style><img src=x onerror="alert(4)">',
      '<iframe src="https://evil.example"></iframe>',
      '<object data="https://evil.example"></object>',
      '<embed src="https://evil.example">',
      '<form action="https://evil.example"><input name="x"></form>',
      "<template><img src=x onerror=alert(5)></template>",
      '<noscript><p title="</noscript><img src=x onerror=alert(6)>">',
      // Инлайн-сырой HTML: тег гасится, ТЕКСТ остаётся (D1 §7).
      '<span style="position:fixed;inset:0">накрыть</span>',
      '<a href="javascript:alert(7)">клик</a>',
      "# [x](javascript:alert(8))",
      "<h1>чужой заголовок</h1>",
    ];
    const host = dom(attacks.join("\n\n"));
    for (const selector of [
      "script",
      "img",
      "svg",
      "iframe",
      "object",
      "embed",
      "form",
      "input",
      "template",
      "noscript",
      "style",
      "h1",
    ]) {
      expect(host.querySelector(selector), `${selector} не должен появляться`).toBeNull();
    }
    expect(host.innerHTML).not.toMatch(/on\w+=/iu);
    expect(host.innerHTML).not.toContain("style=");
    expect(host.innerHTML).not.toContain("javascript:");
    expect(host.innerHTML).not.toContain("data:image");
    // «Накрыть» — текст инлайн-тега остаётся: сказанное не пропадает, теряется только тег/style.
    expect(host.textContent).toContain("накрыть");

    // НАЗВАННАЯ ЦЕНА (D1 §7): БЛОЧНЫЙ сырой HTML исчезает вместе с содержимым — это правило
    // владельца «нет сырого HTML», а не дефект вида; инлайн-тег текст оставляет (проверено выше).
    expect(dom('<div style="color:red">блок</div>').innerHTML.trim()).toBe("");
    expect(dom("<div>блок</div>").textContent).toBe("");
  });

  it("конфиг санитайзера назван явно: теги, атрибуты и схема ссылок", () => {
    expect(MARKUP_PURIFY.ALLOWED_TAGS).toContain("pre");
    expect(MARKUP_PURIFY.ALLOWED_TAGS).not.toContain("img");
    expect(MARKUP_PURIFY.ALLOWED_ATTR).toEqual(["href", "class"]);
    expect(MARKUP_PURIFY.FORBID_ATTR).toContain("style");
    expect(MARKUP_PURIFY.ALLOWED_URI_REGEXP.test("https://example.com/x")).toBe(true);
    expect(MARKUP_PURIFY.ALLOWED_URI_REGEXP.test("http://example.com/x")).toBe(true);
    expect(MARKUP_PURIFY.ALLOWED_URI_REGEXP.test("javascript:alert(1)")).toBe(false);
    expect(MARKUP_PURIFY.ALLOWED_URI_REGEXP.test("//evil.example/x")).toBe(false);
    expect(MARKUP_PURIFY.ALLOWED_URI_REGEXP.test("/session/1")).toBe(false);
  });

  it("кэш по тексту записи: разметка считается один раз, повторы берутся из кэша", () => {
    resetMarkupCache();
    const text = "**одинаковый** текст";
    const first = renderMarkup(text);
    expect(markupCacheSize()).toBe(1);
    const second = renderMarkup(text);
    expect(second).toBe(first);
    expect(markupCacheSize()).toBe(1);
    renderMarkup("другой текст");
    expect(markupCacheSize()).toBe(2);
    resetMarkupCache();
    expect(markupCacheSize()).toBe(0);
  });

  it("пустая реплика — пустая строка, а не «пустой абзац»", () => {
    expect(renderMarkup("")).toBe("");
    expect(renderMarkup("   \n  ")).toBe("");
  });

  it("dangerouslySetInnerHTML в пульте ровно один — и только через renderMarkup", () => {
    const screen = readFileSync(join(import.meta.dirname, "../screens/SessionScreen.tsx"), "utf8");
    const hits = [...screen.matchAll(/dangerouslySetInnerHTML/gu)];
    expect(hits, "вставка HTML — одна точка").toHaveLength(1);
    const around = screen.slice(Math.max(0, hits[0]!.index - 200), hits[0]!.index + 200);
    expect(around, "вставляется результат renderMarkup").toContain("renderMarkup(");
  });
});
