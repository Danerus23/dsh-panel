import { expect, test, type APIRequestContext, type Page } from "@playwright/test";

// D16 (13.11.2026, R7): АДАПТИВ ШИРОКОГО ЭКРАНА — ЦИФРЫ, А НЕ КАРТИНКА.
//
// Правило (…/`_team/fixes/d16-adaptive.md` §3): ШИРИНА ПОВЕРХНОСТИ живёт в px и тянется за экраном
// до 720, ДЛИНА СТРОКИ живёт в `ch` и не тянется. Ни media query, ни JS-пересчёта ширин нет:
// поворот/разворот пересчитывает сам CSS, без перезагрузки.
//
// Числа §3.1/§6 (рамки border-box): страница = min(vw, 720); карточка вопроса и очередь =
// min(vw − 16, 720); лист и зацеп = min(vw, 720) по центру; текст внутри ≤ 60ch; пара «вверх/вниз»
// стоит у края КОЛОНКИ: right = max(8, (vw − 720)/2 + 8); горизонтальной прокрутки нет.
//
// Телефоны 320/360 — обещание §3.2: ни одного пикселя против базы (страница 320/360, карточка
// 304/344, лист 320/360, пара 8).
//
// Проверки 1–11 — те же строки, что в §6, и каждая умеет падать (имена красных — в отчёте R7):
//  1) страница; 2) карточка; 3) карточка = странице на широком; 4) очередь; 5) текст ≤ 60ch;
//  6) пузырь; 7) лист и зацеп; 8) пара; 9) xScroll; 10) живое изменение ширины; 11) база 320/360.

/** Высота окна для всех замеров: предмет правила — ширина, высота зафиксирована. */
const HEIGHT = 720;

/** Потолок поверхности (наше число; нормы для планшетов в банке нет, §2/§3). */
const MAX = 720;

/** Матрица §3.1: [вьюпорт, ожидаемое число] — по одной строке на ширину. */
const PAGE_WIDTHS: ReadonlyArray<readonly [number, number]> = [
  [320, 320],
  [360, 360],
  [673, 673],
  [853, 720],
  [1024, 720],
  [1280, 720],
];
const SURFACE_WIDTHS: ReadonlyArray<readonly [number, number]> = [
  [320, 304],
  [360, 344],
  [673, 657],
  [853, 720],
  [1024, 720],
  [1280, 720],
];
const PAIR_WIDTHS: ReadonlyArray<readonly [number, number]> = [
  [320, 8],
  [360, 8],
  [673, 8],
  [853, 74.5],
  [1024, 160],
  [1280, 288],
];

/** Длинный текст для проверки предела строки: короткие подписи стенда до 60ch не дотягиваются. */
const LONG_TEXT =
  "Длинная строка для замера предела длины строки: она обязана переноситься по словам и не " +
  "растягивать поверхность шире шестидесяти знаков даже на планшете. ".repeat(3);

/** Сценарий стенда: вопрос на поверхности, очередь, пара и задача — всё, что меряет §6. */
async function scenario(request: APIRequestContext): Promise<void> {
  await request.post("/__mock/scenario", {
    data: {
      authed: true,
      lang: "ru",
      list: "ok",
      pair: "ok",
      question: "single",
      questionSession: "session-long",
      task: "ok",
      queue: "ok",
      remove: "ok",
    },
  });
  await request.post("/__mock/s3-reset", { data: {} });
}

/** Открыть длинную сессию: карточка вопроса, очередь и пара живут на одном экране. */
async function openSession(page: Page): Promise<void> {
  await page.goto("/?session=session-long");
  await expect(page.locator(".dp-feed li").last()).toBeVisible();
}

/** Дождаться кадра после смены ширины: CSS пересчитан, разметка не перезагружена. */
async function settle(page: Page): Promise<void> {
  await page.evaluate(
    () =>
      new Promise<void>((resolve) => {
        requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
      }),
  );
}

type BaseShape = {
  innerWidth: number;
  page: number;
  card: number | null;
  queue: number | null;
  handle: number | null;
  feed: number | null;
  xScroll: number;
};

/** Ширины поверхностей, которые видны всегда (лист и пара — отдельными заходами). */
async function measureBase(page: Page): Promise<BaseShape> {
  return page.evaluate(() => {
    const width = (selector: string): number | null => {
      const node = document.querySelector(selector);
      return node === null ? null : node.getBoundingClientRect().width;
    };
    return {
      innerWidth: window.innerWidth,
      page: width(".dp-page") ?? 0,
      card: width(".dp-bottom .dp-question"),
      queue: width(".dp-queue"),
      handle: width(".dp-handle"),
      feed: width(".dp-feed"),
      xScroll: document.documentElement.scrollWidth - window.innerWidth,
    };
  });
}

type SheetShape = { width: number; left: number; right: number; innerWidth: number };

/** Лист ввода: открыть зацепом, замерить рамку, закрыть Escape (дальше меряется страница). */
async function measureSheet(page: Page): Promise<SheetShape> {
  await page.locator(".dp-handle").click();
  await expect(page.locator(".dp-sheet")).toBeVisible();
  const shape = await page.evaluate(() => {
    const node = document.querySelector(".dp-sheet") as HTMLElement | null;
    if (node === null) throw new Error("лист не открылся");
    const box = node.getBoundingClientRect();
    return {
      width: box.width,
      left: box.left,
      right: box.right,
      innerWidth: window.innerWidth,
    };
  });
  await page.keyboard.press("Escape");
  await expect(page.locator(".dp-sheet")).toHaveCount(0);
  return shape;
}

/**
 * Уехать от живого конца — только тогда экран рисует пару «вверх/вниз» (§6 п. 8).
 * Своя прокрутка экрана держит окно 150 мс, поэтому попытки повторяются (как в tolast.spec).
 */
async function leaveLiveEnd(page: Page): Promise<void> {
  await page.waitForTimeout(200);
  for (let attempt = 0; attempt < 6; attempt += 1) {
    await page.evaluate((offset) => window.scrollTo(0, offset), attempt);
    if ((await page.locator(".dp-tolast").count()) > 0) return;
    await page.waitForTimeout(150);
  }
  await expect(page.locator(".dp-tolast"), "уехал выше — пара есть").toBeVisible();
}

/** `right` пары: расстояние от правого края ВИДИМОЙ области до кнопки. */
async function pairRight(page: Page): Promise<number> {
  const right = await page.evaluate(() => {
    const node = document.querySelector(".dp-tolast");
    return node === null ? null : window.innerWidth - node.getBoundingClientRect().right;
  });
  expect(right, "пара «вверх/вниз» на экране").not.toBeNull();
  return right ?? Number.NaN;
}

/** Проверка «±1» — допуск §6: дробные ширины после border-box не обязаны быть целыми. */
function near(actual: number, expected: number, message: string): void {
  expect(
    Math.abs(actual - expected),
    `${message}: получено ${actual}, ждали ${expected}`,
  ).toBeLessThanOrEqual(1);
}

test.describe("D16: адаптив широкого экрана (R7)", () => {
  test.beforeEach(async ({ request }) => {
    await scenario(request);
  });

  test("1. страница = min(vw, 720): 320/360/673/720/720/720", async ({ page }) => {
    await page.setViewportSize({ width: 320, height: HEIGHT });
    await openSession(page);
    for (const [width, expected] of PAGE_WIDTHS) {
      await page.setViewportSize({ width, height: HEIGHT });
      await settle(page);
      const shape = await measureBase(page);
      near(shape.page, expected, `${width}: страница`);
      expect(shape.page, `${width}: страница не шире потолка`).toBeLessThanOrEqual(MAX + 1);
    }
  });

  test("2. карточка вопроса = min(vw−16, 720): 304/344/657/720/720/720", async ({ page }) => {
    await page.setViewportSize({ width: 320, height: HEIGHT });
    await openSession(page);
    for (const [width, expected] of SURFACE_WIDTHS) {
      await page.setViewportSize({ width, height: HEIGHT });
      await settle(page);
      const shape = await measureBase(page);
      expect(shape.card, `${width}: карточка вопроса на экране`).not.toBeNull();
      near(shape.card ?? 0, expected, `${width}: карточка вопроса`);
    }
  });

  test("3. при vw ≥ 736 карточка равна странице ±1 (обе 720)", async ({ page }) => {
    await page.setViewportSize({ width: 853, height: HEIGHT });
    await openSession(page);
    for (const width of [853, 1024, 1280]) {
      await page.setViewportSize({ width, height: HEIGHT });
      await settle(page);
      const shape = await measureBase(page);
      near(shape.card ?? 0, shape.page, `${width}: карточка = странице`);
    }
  });

  test("4. очередь = min(vw−16, 720): 304/344/657/720/720/720", async ({ page }) => {
    await page.setViewportSize({ width: 320, height: HEIGHT });
    await openSession(page);
    await expect(page.locator(".dp-queue")).toBeVisible();
    for (const [width, expected] of SURFACE_WIDTHS) {
      await page.setViewportSize({ width, height: HEIGHT });
      await settle(page);
      const shape = await measureBase(page);
      expect(shape.queue, `${width}: карточка очереди на экране`).not.toBeNull();
      near(shape.queue ?? 0, expected, `${width}: очередь`);
    }
  });

  test("5. текст ≤ 60ch в .dp-md, .dp-ask legend, .dp-queue-text, .dp-choice span на 673/853/1024", async ({
    page,
  }) => {
    await page.setViewportSize({ width: 673, height: HEIGHT });
    await openSession(page);
    // Очередь раскрыть: у пункта есть свой текстовый блок.
    await page.locator(".dp-queue-row").click();
    await expect(page.locator(".dp-queue-panel")).toBeVisible();

    for (const width of [673, 853, 1024]) {
      await page.setViewportSize({ width, height: HEIGHT });
      await settle(page);
      // Короткие подписи стенда до 60ch не дотягиваются — подставляем длинный текст, иначе предел
      // нечем проверить (проверка, которая не может упасть, ничего не стоит).
      await page.evaluate((long) => {
        const text = document.querySelector(".dp-md");
        if (text !== null) text.textContent = long;
        const legend = document.querySelector(".dp-ask legend");
        if (legend !== null) legend.textContent = long;
        const choice = document.querySelector(".dp-choice span");
        if (choice !== null) choice.textContent = long;
      }, LONG_TEXT);

      for (const selector of [".dp-md", ".dp-ask legend", ".dp-queue-text", ".dp-choice span"]) {
        const shape = await page.evaluate((target) => {
          const node = document.querySelector(target) as HTMLElement | null;
          if (node === null) return null;
          // Пробный `<span>1ch</span>`: ширина знака «0» в шрифте САМОГО блока (так же считает CSS `ch`).
          const probe = document.createElement("span");
          probe.textContent = "0";
          probe.style.display = "inline-block";
          probe.style.whiteSpace = "pre";
          node.appendChild(probe);
          const ch = probe.getBoundingClientRect().width;
          probe.remove();
          return { width: node.getBoundingClientRect().width, ch };
        }, selector);
        expect(shape, `${width}: блок ${selector} на экране`).not.toBeNull();
        const width60 = (shape?.ch ?? 0) * 60;
        expect(
          shape?.width ?? Number.POSITIVE_INFINITY,
          `${width}: ${selector} ≤ 60ch (${width60.toFixed(2)} px)`,
        ).toBeLessThanOrEqual(width60 + 1);
        near(shape?.width ?? 0, width60, `${width}: ${selector} держит ровно 60ch`);
      }
    }
  });

  test("6. пузырь .dp-msg.dp-user ≤ min(85 % колонки, 60ch) на 673/853/1024", async ({ page }) => {
    await page.setViewportSize({ width: 673, height: HEIGHT });
    await openSession(page);
    for (const width of [673, 853, 1024]) {
      await page.setViewportSize({ width, height: HEIGHT });
      await settle(page);
      const shape = await page.evaluate((long) => {
        const node = document.querySelector(".dp-msg.dp-user") as HTMLElement | null;
        const feed = document.querySelector(".dp-feed") as HTMLElement | null;
        if (node === null || feed === null) return null;
        node.textContent = long;
        const probe = document.createElement("span");
        probe.textContent = "0";
        probe.style.display = "inline-block";
        probe.style.whiteSpace = "pre";
        node.appendChild(probe);
        const ch = probe.getBoundingClientRect().width;
        probe.remove();
        return {
          width: node.getBoundingClientRect().width,
          ch,
          feed: feed.getBoundingClientRect().width,
        };
      }, LONG_TEXT);
      expect(shape, `${width}: пузырь на экране`).not.toBeNull();
      const limit = Math.min(0.85 * (shape?.feed ?? 0), 60 * (shape?.ch ?? 0));
      expect(
        shape?.width ?? Number.POSITIVE_INFINITY,
        `${width}: пузырь ≤ min(85 %, 60ch) = ${limit.toFixed(2)} px`,
      ).toBeLessThanOrEqual(limit + 1);
      near(shape?.width ?? 0, limit, `${width}: пузырь упирается ровно в предел`);
    }
  });

  test("7. лист и зацеп = min(vw, 720), центрованы; на 673 — во всю ширину", async ({ page }) => {
    await page.setViewportSize({ width: 320, height: HEIGHT });
    await openSession(page);
    for (const [width, expected] of PAGE_WIDTHS) {
      await page.setViewportSize({ width, height: HEIGHT });
      await settle(page);
      const base = await measureBase(page);
      near(base.handle ?? 0, expected, `${width}: зацеп`);
      const sheet = await measureSheet(page);
      near(sheet.width, expected, `${width}: лист`);
      near(sheet.left, (width - expected) / 2, `${width}: лист по центру`);
      near(sheet.right, (width + expected) / 2, `${width}: лист по центру справа`);
      if (width <= MAX) {
        near(sheet.left, 0, `${width}: лист от левого края (телефон)`);
        near(sheet.width, width, `${width}: лист во всю ширину (телефон)`);
      }
    }
  });

  test("8. пара right = max(8, (vw−720)/2+8): 8/8/8/74,5/160/288", async ({ page }) => {
    await page.setViewportSize({ width: 320, height: HEIGHT });
    await openSession(page);
    for (const [width, expected] of PAIR_WIDTHS) {
      await page.setViewportSize({ width, height: HEIGHT });
      await leaveLiveEnd(page);
      near(await pairRight(page), expected, `${width}: right пары`);
    }
  });

  test("9. горизонтальной прокрутки нет: scrollWidth − innerWidth = 0 на всех ширинах (и с листом)", async ({
    page,
  }) => {
    await page.setViewportSize({ width: 320, height: HEIGHT });
    await openSession(page);
    for (const [width] of PAGE_WIDTHS) {
      await page.setViewportSize({ width, height: HEIGHT });
      await settle(page);
      const closed = await measureBase(page);
      expect(closed.xScroll, `${width}: страница вбок не едет`).toBe(0);
      // Лист — самая широкая поверхность: с ним прокрутка тоже обязана остаться нулевой
      // (`width: 720px` вместо `max-width` на 673 даёт xScroll > 0 именно здесь).
      await page.locator(".dp-handle").click();
      await expect(page.locator(".dp-sheet")).toBeVisible();
      const open = await measureBase(page);
      expect(open.xScroll, `${width}: с поднятым листом страница вбок не едет`).toBe(0);
      await page.keyboard.press("Escape");
      await expect(page.locator(".dp-sheet")).toHaveCount(0);
    }
  });

  test("10. живое изменение 320→673→853→1024→320 в одной странице: числа верны, navigation entries=1", async ({
    page,
  }) => {
    await page.setViewportSize({ width: 320, height: HEIGHT });
    await openSession(page);
    // [вьюпорт, страница, карточка, right пары] — те же числа, что в §3.1.
    const steps: ReadonlyArray<readonly [number, number, number, number]> = [
      [320, 320, 304, 8],
      [673, 673, 657, 8],
      [853, 720, 720, 74.5],
      [1024, 720, 720, 160],
      [320, 320, 304, 8],
    ];
    for (const [width, pageWidth, cardWidth, right] of steps) {
      await page.setViewportSize({ width, height: HEIGHT });
      // Перезагрузки при смене ширины быть не может: масштабирование делает только CSS.
      // Мутация «location.reload() на resize» даёт `type: "reload"` — проверка краснеет.
      await page.waitForTimeout(250);
      const nav = await page.evaluate(() => {
        const entries = performance.getEntriesByType("navigation");
        const first = entries[0] as PerformanceNavigationTiming | undefined;
        return { count: entries.length, type: first?.type ?? "none" };
      });
      expect(nav.count, `${width}: живое изменение — одна запись навигации`).toBe(1);
      expect(nav.type, `${width}: живое изменение — без перезагрузки (type)`).toBe("navigate");
      await settle(page);
      const shape = await measureBase(page);
      near(shape.page, pageWidth, `${width}: страница после живого изменения`);
      near(shape.card ?? 0, cardWidth, `${width}: карточка после живого изменения`);
      near(shape.handle ?? 0, pageWidth, `${width}: зацеп после живого изменения`);
      const sheet = await measureSheet(page);
      near(sheet.width, pageWidth, `${width}: лист после живого изменения`);
      near(sheet.left, (width - pageWidth) / 2, `${width}: лист по центру после живого изменения`);
      await leaveLiveEnd(page);
      near(await pairRight(page), right, `${width}: пара после живого изменения`);
      const xScroll = await page.evaluate(
        () => document.documentElement.scrollWidth - window.innerWidth,
      );
      expect(xScroll, `${width}: вбок не поехало`).toBe(0);
    }
  });

  test("11. 320/360 не изменились против базы: страница 320/360, карточка 304/344, лист 320/360, пара 8", async ({
    page,
  }) => {
    await page.setViewportSize({ width: 320, height: HEIGHT });
    await openSession(page);
    const base: ReadonlyArray<readonly [number, number, number]> = [
      [320, 320, 304],
      [360, 360, 344],
    ];
    for (const [width, pageWidth, cardWidth] of base) {
      await page.setViewportSize({ width, height: HEIGHT });
      await settle(page);
      const shape = await measureBase(page);
      near(shape.page, pageWidth, `${width}: страница по базе`);
      near(shape.card ?? 0, cardWidth, `${width}: карточка по базе`);
      near(shape.handle ?? 0, pageWidth, `${width}: зацеп по базе`);
      const sheet = await measureSheet(page);
      near(sheet.width, pageWidth, `${width}: лист по базе`);
      await leaveLiveEnd(page);
      near(await pairRight(page), 8, `${width}: пара по базе (8)`);
      expect(shape.xScroll, `${width}: база — без горизонтали`).toBe(0);
    }
  });

  test("12. строка исхода следует за парой: 320/360 right 60, 853/1024 right 126,5/212, зазор 8", async ({
    page,
  }) => {
    await page.setViewportSize({ width: 320, height: HEIGHT });
    await openSession(page);
    const cases: ReadonlyArray<readonly [number, number]> = [
      [320, 60],
      [360, 60],
      [853, 126.5],
      [1024, 212],
    ];
    for (const [width, expectedRight] of cases) {
      await page.setViewportSize({ width, height: HEIGHT });
      await settle(page);
      await leaveLiveEnd(page);
      // Задача через лист: успех печатается строкой НАД зацепом (D11 §2.4).
      await page.locator(".dp-handle").click();
      await expect(page.locator(".dp-sheet")).toBeVisible();
      await page.locator(".dp-sheet textarea").fill("Проверь строку исхода");
      await page.locator(".dp-sheet").getByRole("button", { name: "Отправить задачу" }).click();
      await expect(page.locator(".dp-handle-note")).toContainText("Задача принята.");
      // После отправки экран мог уйти к живому концу — пара нужна в кадре: она сосед строки.
      await leaveLiveEnd(page);
      const boxes = await page.evaluate(() => {
        const rect = (selector: string) => {
          const node = document.querySelector(selector) as HTMLElement | null;
          if (node === null) return null;
          const box = node.getBoundingClientRect();
          return { left: box.left, right: box.right, top: box.top, bottom: box.bottom };
        };
        return {
          note: rect(".dp-handle-note"),
          pair: rect(".dp-tolast"),
          innerWidth: window.innerWidth,
        };
      });
      expect(boxes.note, `${width}: строка исхода на экране`).not.toBeNull();
      expect(boxes.pair, `${width}: пара на экране`).not.toBeNull();
      const note = boxes.note!;
      const pair = boxes.pair!;
      // right строки = right пары + 52 (44 кнопки + 8 зазора): 8+52=60 на телефоне, 74,5/160+52 — на широком.
      near(boxes.innerWidth - note.right, expectedRight, `${width}: right строки исхода`);
      near(pair.left - note.right, 8, `${width}: зазор до пары`);
      const overlaps =
        note.left < pair.right &&
        pair.left < note.right &&
        note.top < pair.bottom &&
        pair.top < note.bottom;
      expect(overlaps, `${width}: строка исхода не накрывает пару`).toBe(false);
    }
  });
});
