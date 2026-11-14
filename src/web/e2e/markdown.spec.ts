import { expect, test, type Page } from "@playwright/test";
import { BASE_URL } from "./base";

// S3c «РАЗМЕТКА ЛЕНТЫ»: что человек видит в реплике агента и что в DOM не попадает никогда.
//
// Границы владельца (решение 31.10.2026): нет сырого HTML, нет `style=`, нет картинок, нет таблиц,
// ссылки только http/https и в ТОЙ ЖЕ вкладке. Правила вида — D1 (`_team\s3c\d1-vid.md`).
// Разметка — ТОЛЬКО у агента: своя реплика и черновик остаются сырым текстом.

function reply(page: Page) {
  return page.locator(".dp-msg.dp-agent").first();
}

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/scenario", {
    data: { authed: true, lang: "ru", list: "ok", pair: "ok" },
  });
  await request.post("/__mock/scenario", { data: { question: "none" } });
});

test("разметка видна у агента: заголовок h4, жирный, курсив, инлайн-код, список, цитата, код", async ({
  page,
}) => {
  await page.goto("/?session=session-markdown");
  const agent = reply(page);
  await expect(agent).toBeVisible();

  // `##` → h4: заголовки опущены рендерером, h1–h3 в реплике не появляются (D1 §3).
  await expect(agent.locator("h4")).toHaveText("Итог проверки");
  await expect(agent.locator("h1, h2, h3")).toHaveCount(0);

  await expect(agent.locator("strong")).toHaveText("прошла");
  await expect(agent.locator("em")).toHaveText("два");
  // Инлайн-код живёт ВНУТРИ абзаца, а не блоком.
  const inline = agent.locator("p code").first();
  await expect(inline).toHaveText("dp-gate.test.ts");
  await expect(inline.locator("xpath=..").locator("pre")).toHaveCount(0);

  await expect(agent.locator("ul > li").first()).toContainText("пункт раз");
  await expect(agent.locator("blockquote")).toContainText("Цитата из журнала");
  await expect(agent.locator("pre > code")).toContainText("veryLongLine");
});

test("своя реплика и черновик — СЫРОЙ текст: `**` видно как есть", async ({ page }) => {
  await page.goto("/?session=session-markdown");
  const own = page.locator(".dp-msg.dp-user").first();
  await expect(own).toContainText("**что это**");
  await expect(own.locator("strong")).toHaveCount(0);

  const draft = page.locator(".dp-draft").first();
  await expect(draft).toContainText("**черновик** ещё пишется");
  await expect(draft.locator("strong")).toHaveCount(0);
});

test("границы в DOM: ни картинки, ни таблицы, ни style=, ни script; ссылки только http(s) и та же вкладка", async ({
  page,
}) => {
  // Свой origin — ИЗ КОНФИГА, а не из `page.url()`: первый запрос (сама страница) уходит,
  // когда адрес ещё `about:blank`, и тогда свой же документ попадал бы в «внешние».
  const own = new URL(BASE_URL).origin;
  const external: string[] = [];
  page.on("request", (request) => {
    const url = new URL(request.url());
    if (url.origin !== own) external.push(request.url());
  });

  await page.goto("/?session=session-markdown");
  const agent = reply(page);
  await expect(agent.locator("pre")).toHaveCount(1);

  const audit = await agent.evaluate((node) => {
    const links = [...node.querySelectorAll("a")];
    return {
      img: node.querySelectorAll("img").length,
      table: node.querySelectorAll("table").length,
      styleAttr: node.querySelectorAll("[style]").length,
      script: node.querySelectorAll("script").length,
      hrefs: links
        .map((link) => link.getAttribute("href"))
        .filter((href): href is string => href !== null),
      targets: links.filter((link) => link.hasAttribute("target")).length,
      titles: links.filter((link) => link.hasAttribute("title")).length,
      // Тексты, которые обязаны остаться: сказанное не пропадает (D1 §7).
      text: node.textContent ?? "",
      html: node.innerHTML,
    };
  });

  expect(audit.img, "картинок нет — их режет код, а не CSP").toBe(0);
  expect(audit.table, "таблица — не орган").toBe(0);
  expect(audit.styleAttr, "ни одного style=").toBe(0);
  expect(audit.script, "скриптов нет").toBe(0);
  expect(audit.html).not.toContain("javascript:");
  expect(audit.hrefs, "ссылка только http(s), не-схемы остались текстом").toEqual([
    "https://example.com/x",
  ]);
  expect(audit.targets, "target не ставится — та же вкладка (D1 §6)").toBe(0);
  expect(audit.titles, "title отброшен (D1 §5)").toBe(0);

  // Содержимое, которое обязано остаться ТЕКСТОМ.
  expect(audit.text).toContain("жирный тегом"); // инлайн-тег: тег снят, текст на месте
  expect(audit.text).toContain("схема сборки"); // картинка → её alt-текст
  expect(audit.text).toContain("проверка | итог"); // таблица → строки текстом
  expect(audit.text).toContain("сборка | зелёная");
  expect(audit.text).toContain("злая"); // текст не-http ссылки остался
  expect(external, "внешних запросов ноль").toEqual([]);
});

test("блок кода прокручивается вбок сам, страница вбок не едет (360 и 320)", async ({
  browser,
}) => {
  for (const width of [360, 320]) {
    const context = await browser.newContext({ viewport: { width, height: 640 } });
    const page = await context.newPage();
    await page.goto("/?session=session-markdown");
    await expect(page.locator(".dp-md pre").first()).toBeVisible();

    const info = await page.evaluate(() => {
      const pre = document.querySelector(".dp-md pre") as HTMLElement;
      const code = pre.querySelector("code") as HTMLElement;
      const style = getComputedStyle(pre);
      return {
        pageScrollWidth: document.documentElement.scrollWidth,
        pageClientWidth: document.documentElement.clientWidth,
        preScrolls: pre.scrollWidth > pre.clientWidth,
        preWraps: pre.scrollHeight > pre.clientHeight + 1,
        overflowX: style.overflowX,
        codeWhiteSpace: getComputedStyle(code).whiteSpace,
        codeFontSize: getComputedStyle(code).fontSize,
        tabIndex: pre.getAttribute("tabindex"),
      };
    });

    expect(info.pageScrollWidth, `${width}: страница вбок не едет`).toBeLessThanOrEqual(
      info.pageClientWidth + 1,
    );
    expect(info.preScrolls, `${width}: блок кода прокручивается вбок`).toBe(true);
    expect(info.overflowX, `${width}: прокрутка — свойство блока`).toBe("auto");
    expect(info.preWraps, `${width}: внутри блока нет второй вертикальной полосы`).toBe(false);
    expect(info.codeWhiteSpace, `${width}: код не переносится по словам`).toBe("pre");
    expect(info.codeFontSize, `${width}: кегль кода 14`).toBe("14px");
    // Клавиатурную доступность даёт сам браузер (D1 §4 п. 6) — пока `tabindex` не нужен.
    expect(info.tabIndex, `${width}: tabindex в разметке пока не ставим`).toBeNull();
    await context.close();
  }
});

test("ссылка http(s) нажимается и ведёт в той же вкладке, а не-схема — обычный текст", async ({
  page,
}) => {
  await page.goto("/?session=session-markdown");
  const agent = reply(page);
  const good = agent.locator('a[href="https://example.com/x"]');
  await expect(good).toHaveText("годная");
  const goodStyle = await good.evaluate((node) => {
    const style = getComputedStyle(node);
    return { color: style.color, decoration: style.textDecorationLine };
  });
  expect(goodStyle.decoration).toContain("underline");

  const bad = agent.locator("a", { hasText: "злая" });
  const badInfo = await bad.evaluate((node) => ({
    hasHref: node.hasAttribute("href"),
    decoration: getComputedStyle(node).textDecorationLine,
  }));
  expect(badInfo.hasHref, "не-http схема осталась без href").toBe(false);
  expect(badInfo.decoration, "и без подчёркивания — это текст, а не орган").toBe("none");
});
