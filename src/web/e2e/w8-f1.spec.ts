import { expect, test, type Page } from "@playwright/test";

// W8 (ремонт F1, находка L1 на AVD) — ПЕРЕПИСАНО В W12 (D11 §3.2, 12.11.2026).
//
// Что было: после «Задача принята» экран ПРЫГАЛ к блоку «Очередь» над лентой, и находка L1 состояла
// в том, что автоскролл и ResizeObserver отменяли этот прыжок (h2 уезжал за шапку).
// Что стало: прыжка НЕТ вовсе — блок очереди стоит в нижнем стеке, над зацепом ввода, и по приёму
// просто РАСКРЫВАЕТСЯ (список растёт вверх, страница остаётся на месте). Значит проверка
// переворачивается: после приёма страница не смеет сдвинуться ни на пиксель, а очередь обязана быть
// раскрыта и держать фокус в своём `h2`.
//
// Числа — из той же трассировки L1 (360×692) и D11 §1.2: шапка 90, стек у низа (запас = измеренная
// высота: 104 со строкой очереди), фокус — в `.dp-queue-title`.

const VIEW = { width: 360, height: 692 };

type Measure = {
  y: number;
  h2Top: number | null;
  h2Bottom: number | null;
  itemTop: number | null;
  itemBottom: number | null;
  rowBottom: number | null;
  handleTop: number | null;
  headBottom: number | null;
  reserve: string;
  stackHeight: number | null;
  viewport: number;
  h2Focused: boolean;
  expanded: string | null;
  /** Верх последней реплики ленты: по нему видно, что читаемое НЕ уехало. */
  lastTop: number | null;
};

async function measure(page: Page): Promise<Measure> {
  return page.evaluate(() => {
    const box = (selector: string) => {
      const node = document.querySelector(selector) as HTMLElement | null;
      return node === null ? null : node.getBoundingClientRect();
    };
    const h2 = document.querySelector(".dp-queue-title") as HTMLElement | null;
    const item = box(".dp-queue-item");
    const head = box(".dp-head-session");
    const row = box(".dp-queue-row");
    const handle = box(".dp-handle");
    const stack = box(".dp-bottom");
    const rows = document.querySelectorAll(".dp-feed li");
    const last = rows[rows.length - 1] as HTMLElement | undefined;
    return {
      y: Math.round(window.scrollY),
      h2Top: h2 === null ? null : Math.round(h2.getBoundingClientRect().top),
      h2Bottom: h2 === null ? null : Math.round(h2.getBoundingClientRect().bottom),
      itemTop: item === null ? null : Math.round(item.top),
      itemBottom: item === null ? null : Math.round(item.bottom),
      rowBottom: row === null ? null : Math.round(row.bottom),
      handleTop: handle === null ? null : Math.round(handle.top),
      headBottom: head === null ? null : Math.round(head.bottom),
      reserve: getComputedStyle(document.documentElement)
        .getPropertyValue("--dp-bottom-reserve")
        .trim(),
      stackHeight: stack === null ? null : Math.round(stack.height),
      viewport: window.innerHeight,
      h2Focused: h2 !== null && document.activeElement === h2,
      expanded: document.querySelector(".dp-queue-row")?.getAttribute("aria-expanded") ?? null,
      lastTop: last === undefined ? null : Math.round(last.getBoundingClientRect().top),
    };
  });
}

async function sendTask(page: Page, words: string, text: string): Promise<void> {
  await page.locator(".dp-handle").click();
  const sheet = page.locator(".dp-sheet");
  await expect(sheet).toBeVisible();
  await sheet.locator("textarea").fill(text);
  await sheet.getByRole("button", { name: words }).click();
}

test.beforeEach(async ({ request }) => {
  // W10 (D11 §4.2): вопрос теперь ПОВЕРХНОСТЬ в нижнем стеке и высоту страницы НЕ растит, поэтому
  // прокрутку даёт САМА лента — длинная сессия (в W12 короткая страница держалась карточкой вопроса).
  await request.post("/__mock/scenario", {
    data: {
      authed: true,
      lang: "ru",
      list: "ok",
      pair: "ok",
      question: "none",
      queue: "ok",
    },
  });
});

test("приём НЕ раскрывает очередь (слово владельца 13.11), и ни автоскролл, ни ResizeObserver не двигают страницу", async ({
  page,
  request,
}) => {
  // Очередь отвечает с задержкой (стенд `queue: "slow"`): перечитывание состава случается ПОСЛЕ закрытия
  // листа — ровно тот миг, в который прежний экран прыгал к блоку и его отменяли пин и наблюдатель роста.
  await request.post("/__mock/scenario", { data: { queue: "slow" } });
  await page.setViewportSize(VIEW);
  await page.goto("/?session=session-long");
  await expect(page.locator(".dp-feed li").last()).toBeVisible();
  await expect.poll(() => page.evaluate(() => Math.round(window.scrollY))).toBeGreaterThan(0);
  await page.waitForTimeout(250);

  await sendTask(page, "Отправить задачу", "Проверь очередь после приёма");
  await expect(page.locator(".dp-handle-note")).toContainText("Задача принята.");

  // СЛОВО ВЛАДЕЛЬЦА 13.11.2026: по приёму строка очереди остаётся СВЁРНУТОЙ — панели в DOM нет,
  // заголовка нет, фокуса в нём нет: человек откроет сам. Фокус — на зацепе (его вернул лист).
  await expect
    .poll(async () => (await measure(page)).expanded, { message: "очередь свёрнута по приёму" })
    .toBe("false");
  expect(await page.locator(".dp-queue-panel").count(), "панели в DOM нет").toBe(0);
  await expect(page.locator(".dp-handle"), "фокус на зацепе").toBeFocused();

  // Запас = фактическая высота стека (строка + зацеп): одно измеренное число (D11 §1.2).
  await expect
    .poll(
      async () => {
        const m = await measure(page);
        return `${m.reserve}|${m.stackHeight}`;
      },
      { message: "запас = высота стека" },
    )
    .toMatch(/^(\d+)px\|\1$/u);
  const after = await measure(page);
  expect(after.h2Top, "заголовка панели нет").toBeNull();
  expect(after.h2Focused, "фокус не в заголовке").toBe(false);
  // Строка — в стеке у низа, целиком в кадре.
  expect(after.rowBottom!, "строка очереди над зацепом").toBeLessThanOrEqual(
    after.handleTop ?? after.viewport,
  );
  expect(after.reserve, "запас = высота стека").toBe(`${after.stackHeight}px`);

  // ПРЫЖКА НЕТ: доказательство — не «нулевая разница scrollY» (лист ввода сам по себе двигает страницу),
  // а то, что видно в кадре: живой конец ленты остался на экране, а строка очереди — внизу, в стеке.
  await expect
    .poll(
      async () => {
        const now = await page.evaluate(() => {
          const rows = document.querySelectorAll(".dp-feed li");
          const last = rows[rows.length - 1] as HTMLElement;
          const stack = (
            document.querySelector(".dp-bottom") as HTMLElement
          ).getBoundingClientRect();
          return {
            lastBottom: Math.round(last.getBoundingClientRect().bottom),
            stackTop: Math.round(stack.top),
          };
        });
        return now.lastBottom - now.stackTop;
      },
      { message: "живой конец ленты виден НАД стеком" },
    )
    .toBeLessThanOrEqual(1);
  const first = await page.evaluate(() => {
    const rows = document.querySelectorAll(".dp-feed li");
    const last = rows[rows.length - 1] as HTMLElement;
    return Math.round(last.getBoundingClientRect().top);
  });
  expect(first, "и не ушёл за верхнюю кромку — прыжка к блоку нет").toBeGreaterThan(0);

  // Поздний рост страницы — то же, что дорисовавшийся пункт или новая лента: наблюдатель роста видит
  // новый размер тела. Строка очереди (она в `fixed`-стеке) не смещается ни на пиксель, панель не
  // раскрывается сама, а живой конец ленты не уходит ПОД стек (D1 §1.3).
  await page.evaluate(() => {
    const grow = document.createElement("div");
    grow.id = "w8-grow";
    grow.style.height = "240px";
    grow.setAttribute("aria-hidden", "true");
    document.body.appendChild(grow);
  });

  for (let step = 0; step < 8; step += 1) {
    await page.waitForTimeout(250);
    const now = await measure(page);
    expect(now.expanded, `очередь не раскрылась сама (шаг ${step})`).toBe("false");
    expect(now.rowBottom, `строка на месте (шаг ${step})`).toBeLessThanOrEqual(
      now.handleTop ?? now.viewport,
    );
    expect(
      Math.abs((now.rowBottom ?? 0) - (after.rowBottom ?? 0)),
      `строка не сместилась (шаг ${step})`,
    ).toBeLessThanOrEqual(1);
    const feedNow = await page.evaluate(() => {
      const rows = document.querySelectorAll(".dp-feed li");
      const last = rows[rows.length - 1] as HTMLElement;
      const stack = (document.querySelector(".dp-bottom") as HTMLElement).getBoundingClientRect();
      return {
        lastBottom: Math.round(last.getBoundingClientRect().bottom),
        stackTop: Math.round(stack.top),
      };
    });
    expect(feedNow.lastBottom, `живой конец не ушёл под стек (шаг ${step})`).toBeLessThanOrEqual(
      feedNow.stackTop + 1,
    );
  }
});

test("раскрытая очередь видна целиком и у низа: и со страницы наверху, и у живого конца", async ({
  page,
}) => {
  await page.setViewportSize(VIEW);
  await page.goto("/?session=session-long");
  await expect(page.locator(".dp-feed li").last()).toBeVisible();
  await expect.poll(() => page.evaluate(() => Math.round(window.scrollY))).toBeGreaterThan(0);
  await page.waitForTimeout(400);

  // Со страницы наверху: раскрываем строку — панель растёт вверх и остаётся в кадре.
  await page.evaluate(() => window.scrollTo(0, 0));
  await expect.poll(async () => (await measure(page)).y, { message: "вернулись наверх" }).toBe(0);
  await page.locator(".dp-queue-row").click();
  await expect(page.locator(".dp-queue-panel")).toBeVisible();
  const top = await measure(page);
  expect(top.h2Top!, "заголовок в кадре и на наверху страницы").toBeGreaterThan(0);
  expect(top.itemBottom!, "первый пункт в кадре").toBeLessThanOrEqual(top.viewport);
  expect(top.rowBottom!, "строка — над зацепом").toBeLessThanOrEqual(top.handleTop ?? top.viewport);
  await page.locator(".dp-queue-row").click();
  await expect(page.locator(".dp-queue-panel")).toHaveCount(0);

  // У живого конца: раскрытие не должно вытолкнуть живой конец из кадра.
  await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  await page.waitForTimeout(300);
  await page.locator(".dp-queue-row").click();
  await expect(page.locator(".dp-queue-panel")).toBeVisible();
  const bottom = await measure(page);
  expect(bottom.h2Top!, "и здесь заголовок в кадре").toBeGreaterThan(0);
  expect(bottom.rowBottom!, "строка над зацепом").toBeLessThanOrEqual(
    bottom.handleTop ?? bottom.viewport,
  );
  // Запас вырос — страница стала длиннее, но низ остался низом: живой конец не ушёл под стек.
  await expect
    .poll(async () => (await measure(page)).reserve, { message: "запас = высота раскрытого стека" })
    .toBe(`${bottom.stackHeight}px`);
});
