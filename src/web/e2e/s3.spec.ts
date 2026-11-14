// S3 «Запускать и останавливать»: полоса задачи, очередь с «убрать», стоп, «Новая задача», черновик.
//
// Стенд — `e2e/server.mjs` (подставная калитка): формы ответов повторяют контракт §2–§6.
// Проверяем ПОВЕДЕНИЕ ЭКРАНА, а не живой движок (это названное ограничение S3, вердикт t10 §2 п. 14).

import { expect, test, type Page } from "@playwright/test";
import { ownOrigin } from "./base";

const WORDS = {
  send: "Отправить задачу",
  stop: "Остановить",
  remove: "Убрать",
  keep: "Оставить",
  removeYes: "Убрать задачу",
  repeat: "Повторить",
};

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/scenario", {
    data: {
      authed: true,
      lang: "ru",
      list: "ok",
      pair: "ok",
      question: "none",
      task: "ok",
      newtask: "ok",
      stop: "stopped",
      queue: "ok",
      remove: "ok",
    },
  });
  await request.post("/__mock/s3-reset", { data: {} });
});

/** Положение страницы и последней записи ленты — для проверки «набор не двигает ленту». */
async function scrollState(page: Page) {
  return page.evaluate(() => {
    const items = document.querySelectorAll(".dp-feed li");
    const last = items[items.length - 1];
    return {
      y: Math.round(window.scrollY),
      lastTop: last ? Math.round(last.getBoundingClientRect().top) : null,
    };
  });
}

async function mockState(page: Page) {
  const response = await page.request.get("/__mock/s3");
  return (await response.json()) as {
    promptCalls: number;
    newCalls: number;
    stopCalls: number;
    lastPrompt: { sessionId?: string; requestId?: string; text?: string } | null;
    lastNew: { requestId?: string; text?: string } | null;
    lastQueueSession: string;
    queue: Array<{ id: string }>;
  };
}
/** Нижний стек — всё, что висит над лентой внизу: зацеп шторки и его строка исхода (D11 §1.1). */
function bar(page: Page) {
  return page.locator(".dp-bottom");
}

/** Поднятый лист ввода: поле и кнопки живут в нём (S3e/R2). */
function sheet(page: Page) {
  return page.locator(".dp-sheet");
}

/** Открыть лист: внизу один зацеп — тап по нему (D11 §2.2, слово владельца 12.11.2026). */
async function openSheet(page: Page) {
  await page.locator(".dp-handle").click();
  await expect(sheet(page)).toBeVisible();
  // D4: лист въезжает 200 мс. Замеры и фокус — после конца движения, иначе ловят полпути.
  await expect
    .poll(async () =>
      sheet(page).evaluate(
        (node) =>
          node.getAnimations().filter((animation) => animation.playState === "running").length,
      ),
    )
    .toBeGreaterThan(0);
  await expect
    .poll(async () =>
      sheet(page).evaluate(
        (node) =>
          node.getAnimations().filter((animation) => animation.playState === "running").length,
      ),
    )
    .toBe(0);
}

/** Закрыть лист, если он открыт (после успешной отправки он закрывается сам). */
async function closeSheet(page: Page) {
  if ((await sheet(page).count()) === 0) return;
  await page.keyboard.press("Escape");
  await expect(sheet(page)).toHaveCount(0);
}

/**
 * Раскрыть док очереди (W12, D11 §3): по умолчанию он свёрнут строкой «В очереди: N» над зацепом,
 * и пунктов в DOM нет вовсе — панель растёт ВВЕРХ по нажатию на строку.
 */
async function openQueue(page: Page) {
  const row = page.locator(".dp-queue-row");
  await expect(row, "строка очереди на экране").toBeVisible();
  if ((await row.getAttribute("aria-expanded")) !== "true") await row.click();
  await expect(page.locator(".dp-queue-panel"), "панель очереди раскрыта").toBeVisible();
}

test("зацеп: нижний стек во вьюпорте, запас страницы и автоскролла — ОДНО ИЗМЕРЕННОЕ число, живой конец над зацепом", async ({
  page,
  // Очередь ПУСТА: стек — только зацеп (52), иначе к запасу прибавится её строка (W12).
  request,
}) => {
  await request.post("/__mock/scenario", { data: { queue: "empty" } });
  await page.goto("/?session=session-many");
  await expect(page.locator(".dp-feed li").last()).toBeVisible();
  // Запас ставит ResizeObserver уже после кадра — ждём сходимости, а не одного замера.
  await expect
    .poll(
      () =>
        page.evaluate(() =>
          getComputedStyle(document.documentElement).getPropertyValue("--dp-bottom-reserve").trim(),
        ),
      { message: "--dp-bottom-reserve измерен" },
    )
    .toBe("52px");

  const shape = await page.evaluate(() => {
    const stack = document.querySelector(".dp-bottom") as HTMLElement;
    const handle = document.querySelector(".dp-handle") as HTMLElement;
    const main = document.querySelector(".dp-page") as HTMLElement;
    const root = getComputedStyle(document.documentElement);
    const box = handle.getBoundingClientRect();
    const stackBox = stack.getBoundingClientRect();
    const items = document.querySelectorAll(".dp-feed li");
    const lastItem = (items[items.length - 1] as HTMLElement).getBoundingClientRect();
    return {
      stackPosition: getComputedStyle(stack).position,
      stackZ: getComputedStyle(stack).zIndex,
      stackHeight: Math.round(stackBox.height),
      handle: {
        left: Math.round(box.left),
        width: Math.round(box.width),
        height: Math.round(box.height),
        bottom: Math.round(box.bottom),
      },
      viewport: window.innerHeight,
      innerWidth: window.innerWidth,
      pagePadding: getComputedStyle(main).paddingBottom,
      scrollPadding: root.scrollPaddingBottom,
      reserve: root.getPropertyValue("--dp-bottom-reserve").trim(),
      reserveAttr: main.getAttribute("data-bottom-reserve"),
      lastBottom: Math.round(lastItem.bottom),
      lastTop: Math.round(lastItem.top),
    };
  });

  expect(shape.stackPosition, "нижний стек — fixed во вьюпорте").toBe("fixed");
  expect(shape.stackZ, "слой 10 — между парой (2) и листом (40)").toBe("10");
  expect(shape.reserve, "запас — одно ИЗМЕРЕННОЕ число").toBe("52px");
  expect(shape.stackHeight, "измерение = фактическая высота стека: 44 + 8").toBe(52);
  expect(shape.pagePadding, "страница держит запас снизу").toBe("52px");
  expect(shape.scrollPadding, "scroll-padding = он же").toBe("52px");
  expect(shape.reserveAttr, "число названо и в разметке").toBe("52");
  expect(shape.viewport - shape.handle.bottom, "зацеп прибит к низу экрана").toBe(0);
  expect(shape.handle.height, "цель зацепа — 44").toBeGreaterThanOrEqual(44);
  expect(shape.handle.width, "полоса во всю ширину — промахнуться нельзя").toBe(shape.innerWidth);
  expect(shape.lastBottom, "живой конец ленты — НАД зацепом").toBeLessThanOrEqual(
    shape.viewport - shape.stackHeight,
  );

  // Стек живёт порталом в `body`: прокручиваемый контейнер его не несёт.
  const parents = await page.evaluate(() => {
    const stack = document.querySelector(".dp-bottom") as HTMLElement;
    return {
      parent: stack.parentElement?.tagName ?? "",
      inPage: document.querySelector(".dp-page")?.contains(stack) ?? false,
    };
  });
  expect(parents.parent).toBe("BODY");
  expect(parents.inPage, "стек вне прокручиваемой страницы").toBe(false);
});

test("живой конец ленты виден НАД зацепом — после отправки и после перезагрузки", async ({
  page,
}) => {
  // ДЛИННАЯ лента: очередь ушла в НИЖНИЙ СТЕК (W12), и короткая страница перестала прокручиваться —
  // а проверка — про автодописывание и живой конец над зацепом.
  await page.goto("/?session=session-long");
  await expect(page.locator(".dp-feed li").last()).toBeVisible();

  const measure = async () =>
    page.evaluate(() => {
      const items = document.querySelectorAll(".dp-feed li");
      const last = items[items.length - 1] as HTMLElement;
      const item = last.getBoundingClientRect();
      const band = (document.querySelector(".dp-handle") as HTMLElement).getBoundingClientRect();
      return {
        lastBottom: Math.round(item.bottom),
        lastTop: Math.round(item.top),
        barTop: Math.round(band.top),
        viewport: window.innerHeight,
        scrolled: Math.round(window.scrollY),
        documentHeight: document.documentElement.scrollHeight,
      };
    });

  // Автоскролл S1 идёт кадром позже загрузки — ждём сходимости, а не одного замера.
  await expect
    .poll(async () => (await measure()).scrolled, { message: "автоскролл S1 увёл к живому концу" })
    .toBeGreaterThan(0);
  await expect
    .poll(
      async () => {
        const m = await measure();
        return m.lastBottom - (m.barTop + 1);
      },
      { message: "последняя строка ленты — над полосой" },
    )
    .toBeLessThanOrEqual(0);
  const before = await measure();
  expect(before.lastTop, "живой конец в видимой части").toBeLessThan(before.viewport);
  // Слово владельца 13.11.2026: по приёму очередь НЕ раскрывается — строка «В очереди: N» стоит
  // свёрнутой над зацепом, счёт вырос; панель человек откроет сам.
  const queueRow = page.locator(".dp-queue-row");
  const queueCount = async () =>
    Number(((await page.locator(".dp-queue-row").textContent()) ?? "").match(/\d+/u)?.[0] ?? 0);
  const countBefore = await queueCount();
  await openSheet(page);
  await sheet(page).locator("textarea").fill("Ещё задача");
  await sheet(page).getByRole("button", { name: WORDS.send }).click();
  await expect(page.locator(".dp-handle-note"), "успех — строкой над зацепом").toContainText(
    "Задача принята.",
  );
  await expect(sheet(page), "лист закрылся сам после отправки").toHaveCount(0);
  await closeSheet(page);

  await expect
    .poll(queueCount, { message: "счёт вырос — принятая задача в очереди" })
    .toBeGreaterThan(countBefore);
  await expect(queueRow).toHaveAttribute("aria-expanded", "false");
  expect(await page.locator(".dp-queue-panel").count(), "панели в DOM нет").toBe(0);
  // Принятая задача и правда в очереди — её видно, когда человек раскроет строку сам.
  await queueRow.click();
  await expect(
    page.getByText("Ещё задача", { exact: true }),
    "очередь перечитана после приёма",
  ).toBeVisible();
  await queueRow.click();
  const toLast = page.locator(".dp-tolast");
  if ((await toLast.count()) > 0) await toLast.click();
  await expect
    .poll(
      async () => {
        const afterSend = await measure();
        return afterSend.lastBottom - (afterSend.barTop + 1);
      },
      { message: "живой конец виден над полосой (сам или по «к последнему»)" },
    )
    .toBeLessThanOrEqual(0);

  await page.reload();
  await expect(page.locator(".dp-feed li").last()).toBeVisible();
  await expect
    .poll(
      async () => {
        const afterReload = await measure();
        return afterReload.lastBottom - (afterReload.barTop + 1);
      },
      { message: "после перезагрузки конец тоже над полосой" },
    )
    .toBeLessThanOrEqual(0);
});

test("стоп живёт в полосе, дверь одна; при карточке вопроса стопа нет вовсе", async ({ page }) => {
  await page.goto("/?session=session-live");
  // Свёрнутый лист кнопок не несёт — стоп живёт в поднятом листе ввода (S3e).
  await expect(page.getByRole("button", { name: WORDS.stop })).toHaveCount(0);
  await openSheet(page);
  await expect(sheet(page).getByRole("button", { name: WORDS.stop })).toHaveCount(1);
  await expect(page.getByText("Текущий ход", { exact: true })).toHaveCount(0);

  // Ход кончился — стопа нет вовсе (не «погашен»): останавливать нечего.
  await page.goto("/?session=session-idle");
  await openSheet(page);
  await expect(page.getByRole("button", { name: WORDS.stop })).toHaveCount(0);
  await closeSheet(page);
  await page.goto("/?session=session-live");
  await openSheet(page);
  await expect(sheet(page).getByText("Текущий ход остановится", { exact: false })).toBeVisible();

  const target = await sheet(page)
    .getByRole("button", { name: WORDS.stop })
    .evaluate((node) => (node as HTMLElement).offsetHeight);
  expect(target).toBeGreaterThanOrEqual(44);

  await page.request.post("/__mock/scenario", {
    data: { question: "single", questionSession: "session-live" },
  });
  await page.goto("/?session=session-live");
  await expect(page.getByText("Что делаем с упавшими тестами?")).toBeVisible();
  await openSheet(page);
  await expect(page.getByRole("button", { name: WORDS.stop })).toHaveCount(0);
  await closeSheet(page);

  await page.request.post("/__mock/scenario", { data: { question: "none" } });
});

test("остановка: ход отменён, очередь осталась и перечитана", async ({ page }) => {
  await page.goto("/?session=session-live");
  const queue = page.locator(".dp-queue");
  await expect(queue.getByText("В очереди: 2")).toBeVisible();
  await openQueue(page);
  await openSheet(page);
  await sheet(page).getByRole("button", { name: WORDS.stop }).click();
  await expect(sheet(page).getByText("Ход остановлен. Задачи в очереди остались.")).toBeVisible();
  await closeSheet(page);
  await expect(queue.getByText("В очереди: 2")).toBeVisible();
  await expect(queue.locator(".dp-queue-item")).toHaveCount(2);
  await expect(page.locator(".dp-queue .dp-error-block")).toHaveCount(0);
});

test("очередь: метки места, двухшаговое «убрать», безопасный ответ первым", async ({ page }) => {
  await page.goto("/?session=session-live");
  const queue = page.locator(".dp-queue");
  await openQueue(page);
  await expect(queue.getByRole("heading", { name: "Очередь" })).toBeVisible();
  await expect(queue.getByText("В очереди: 2")).toBeVisible();
  await expect(queue.getByText("Пойдёт в текущий ход")).toBeVisible();
  await expect(queue.getByText("Ждёт хода")).toBeVisible();
  await expect(queue.locator(".dp-queue-item")).toHaveCount(2);

  const item = queue.locator(".dp-queue-item").first();
  await item.getByRole("button", { name: WORDS.remove }).click();

  // Вопрос раскрылся НА МЕСТЕ пункта, цена названа, безопасный ответ — первый.
  const ask = item.locator(".dp-queue-ask");
  await expect(ask).toBeVisible();
  await expect(ask.getByText("Убрать задачу из очереди?")).toBeVisible();
  await expect(ask.getByText(/Вернуть её будет нельзя/)).toBeVisible();
  const buttons = ask.getByRole("button");
  await expect(buttons.nth(0)).toHaveText(WORDS.keep);
  await expect(buttons.nth(1)).toHaveText(WORDS.removeYes);
  await expect(ask.getByRole("button", { name: WORDS.keep })).toBeFocused();

  // «Оставить» — вопрос закрывается, пункт на месте, фокус вернулся на «Убрать».
  await ask.getByRole("button", { name: WORDS.keep }).click();
  await expect(ask).toHaveCount(0);
  await expect(item.getByRole("button", { name: WORDS.remove })).toBeFocused();
  await expect(queue.locator(".dp-queue-item")).toHaveCount(2);

  // «Убрать задачу» — пункт исчез, число изменилось, сказано словами.
  await item.getByRole("button", { name: WORDS.remove }).click();
  await item.getByRole("button", { name: WORDS.removeYes }).click();
  await expect(queue.getByText("Убрано из очереди.")).toBeVisible();
  await expect(queue.locator(".dp-queue-item")).toHaveCount(1);
  await expect(queue.getByText("В очереди: 1")).toBeVisible();

  const state = await mockState(page);
  expect(state.lastQueueSession).toBe("session-live");
});

test("«уже нет в очереди» — спокойный ответ, а не ошибка", async ({ page, request }) => {
  await request.post("/__mock/scenario", { data: { remove: "gone" } });
  await page.goto("/?session=session-live");
  const queue = page.locator(".dp-queue");
  await openQueue(page);
  const item = queue.locator(".dp-queue-item").first();
  await item.getByRole("button", { name: WORDS.remove }).click();
  await item.getByRole("button", { name: WORDS.removeYes }).click();

  await expect(queue.getByText("Уже нет в очереди.")).toBeVisible();
  await expect(queue.locator(".dp-error-block")).toHaveCount(0);
  await expect(queue.locator(".dp-status")).toHaveCount(1);
  await expect(queue.locator(".dp-queue-item")).toHaveCount(1);
});

test("очередь: пусто словами; «не открыта» отдельно; отказ — не пустой список", async ({
  page,
  request,
}) => {
  await request.post("/__mock/scenario", { data: { queue: "empty" } });
  await page.goto("/?session=session-live");
  // D11 §3.5 (вариант А): ноль — пустая величина. Ни строки «В очереди: 0», ни пустого блока,
  // ни слова «Очередь пуста» — блока НЕТ вовсе (ключ `PhoneWebQueueEmpty` остаётся в словарях).
  await expect(page.locator(".dp-handle"), "экран живой").toBeVisible();
  await expect(page.locator(".dp-queue")).toHaveCount(0);
  await expect(page.getByText("Очередь пуста: задача начнётся сразу после отправки.")).toHaveCount(
    0,
  );
  await expect(page.locator(".dp-queue-item")).toHaveCount(0);

  // Сессия не открыта — знать и показывать нечего: блока нет (D11 §3.2).
  await request.post("/__mock/scenario", { data: { queue: "no-live" } });
  await page.reload();
  await expect(page.locator(".dp-handle")).toBeVisible();
  await expect(page.locator(".dp-queue")).toHaveCount(0);
  await expect(page.getByText("Сессия сейчас не открыта — очередь неизвестна.")).toHaveCount(0);
  await expect(page.locator(".dp-queue-item")).toHaveCount(0);

  await request.post("/__mock/scenario", { data: { queue: "unreadable" } });
  await page.reload();
  const queue = page.locator(".dp-queue");
  await expect(queue.locator(".dp-error-block")).toContainText("Очередь не прочиталась");
  await expect(queue.getByRole("button", { name: WORDS.repeat }).first()).toBeVisible();
  // S4a: «Обновить» больше нет и в отказе — путь повторения называется словом «Повторить».
  await expect(queue.getByRole("button", { name: "Обновить" })).toHaveCount(0);
});

test("очередь: 20 пунктов, «показаны первые 20 из 45», длинный текст не обрезан вью", async ({
  page,
  request,
}) => {
  await request.post("/__mock/scenario", { data: { queue: "many" } });
  await page.goto("/?session=session-live");
  const queue = page.locator(".dp-queue");
  await openQueue(page);
  await expect(queue.locator(".dp-queue-item")).toHaveCount(20);
  await expect(queue.getByText("Показаны первые 20 из 45")).toBeVisible();
  await expect(queue.getByText("В очереди: 45")).toBeVisible();

  // Превью не режем второй раз: строка кончается на «…», и хвост виден (нет line-clamp).
  const first = queue.locator(".dp-queue-text").first();
  await expect(first).toContainText("…");
  const clipped = await first.evaluate((node) => {
    const style = getComputedStyle(node);
    return {
      clamp: style.webkitLineClamp,
      overflow: style.overflow,
      scrollHeight: node.scrollHeight,
      clientHeight: node.clientHeight,
    };
  });
  expect(clipped.clamp === "none" || clipped.clamp === "").toBe(true);
  expect(clipped.scrollHeight).toBeLessThanOrEqual(clipped.clientHeight + 1);
});

test("черновик переживает перезагрузку, сам не отправляется, снимается после «принято»", async ({
  page,
}) => {
  await page.goto("/?session=session-live");
  await openSheet(page);
  const field = sheet(page).locator("textarea");
  await field.fill("Проверь, что черновик жив");
  // Запись не чаще раза в секунду — ждём тика, чтобы перезагрузка не обогнала запись.
  await page.waitForTimeout(1_200);
  await page.reload();

  await openSheet(page);
  const restored = sheet(page).locator("textarea");
  await expect(restored).toHaveValue("Проверь, что черновик жив");
  await expect(sheet(page).getByText("Черновик восстановлен")).toBeVisible();
  const state = await mockState(page);
  expect(state.promptCalls, "сам черновик не отправляется").toBe(0);

  await sheet(page).getByRole("button", { name: WORDS.send }).click();
  await expect(page.locator(".dp-handle-note"), "успех — строкой над зацепом").toContainText(
    "Задача принята.",
  );
  await expect(sheet(page), "лист закрылся сам после отправки").toHaveCount(0);
  await openSheet(page);
  await expect(sheet(page).getByText("Черновик восстановлен")).toHaveCount(0);
  await expect(sheet(page).locator("textarea")).toHaveValue("");
  await closeSheet(page);
});

test("«Убрать черновик» снимает его сразу, фокус уходит в поле", async ({ page }) => {
  await page.goto("/?session=session-live");
  await openSheet(page);
  await sheet(page).locator("textarea").fill("Временный текст");
  await page.waitForTimeout(1_100);
  await page.reload();
  await openSheet(page);
  await expect(sheet(page).getByText("Черновик восстановлен")).toBeVisible();
  await sheet(page).getByRole("button", { name: "Убрать черновик" }).click();
  await expect(sheet(page).locator("textarea")).toHaveValue("");
  await expect(sheet(page).locator("textarea")).toBeFocused();
  await closeSheet(page);
  await page.reload();
  await expect(bar(page).getByText("Черновик восстановлен")).toHaveCount(0);
});

test("отказ не стирает текст, повтор идёт с тем же requestId, второй POST не уходит", async ({
  page,
  request,
}) => {
  await request.post("/__mock/scenario", { data: { task: "slow" } });
  await page.goto("/?session=session-live");
  await openSheet(page);
  const composer = sheet(page);
  await composer.locator("textarea").fill("Почини сборку");
  const send = composer.getByRole("button", { name: WORDS.send });
  await send.click();
  // Пока запрос в полёте, кнопка погашена и причина названа словами.
  await expect(composer.getByText("Отправка уже идёт — дождитесь ответа.")).toBeVisible();
  await expect(send).toBeDisabled();
  await expect(page.locator(".dp-handle-note"), "успех — строкой над зацепом").toContainText(
    "Задача принята.",
  );
  await expect(sheet(page), "лист закрылся сам после отправки").toHaveCount(0);
  // Отказ: сценарий переключаем на «задачу не приняли» — лист остаётся, текст цел.
  await request.post("/__mock/scenario", { data: { task: "not-taken" } });
  await openSheet(page);
  await sheet(page).locator("textarea").fill("Ещё одна задача");
  await sheet(page).getByRole("button", { name: WORDS.send }).click();
  await expect(sheet(page).getByText(/Задача не принята/)).toBeVisible();
  await expect(sheet(page).locator("textarea")).toHaveValue("Ещё одна задача");
  const failureHeight = await sheet(page).evaluate((node) => (node as HTMLElement).offsetHeight);
  const first = await mockState(page);
  expect(await sheet(page).locator("textarea").isVisible()).toBe(true);

  await sheet(page).getByRole("button", { name: "Повторить" }).click();
  await expect(sheet(page).getByText(/Задача не принята/)).toBeVisible();
  const second = await mockState(page);
  expect(second.promptCalls).toBe(first.promptCalls + 1);
  expect(second.lastPrompt?.requestId, "повтор — тот же requestId").toBe(
    first.lastPrompt?.requestId,
  );
  expect(second.lastPrompt?.text).toBe("Ещё одна задача");
});

test("«Новая задача» из списка открывает ввод, после создания человек в заведённом разговоре", async ({
  page,
}) => {
  await page.goto("/");
  const openNew = page.getByRole("button", { name: "Новая задача" });
  await expect(openNew).toBeVisible();
  await openNew.click();
  await expect(page).toHaveURL(/new=1/u);
  // Заголовок «Новая задача» — имя страницы: он ВИДИМ (находка F-A гейта t15).
  // `toBeVisible()` тут обманывал: у `.dp-visually-hidden` коробка 1×1 и `clip-path`, а Playwright
  // считает такой элемент видимым. Поэтому меряем КОРОБКУ и обрезку — погашенный h1 так не пройдёт.
  const newTitle = page.getByRole("heading", { level: 1, name: "Новая задача" });
  await expect(newTitle).toHaveCount(1);
  const titleBox = await newTitle.evaluate((node) => {
    const box = node.getBoundingClientRect();
    const style = getComputedStyle(node);
    return {
      width: Math.round(box.width),
      height: Math.round(box.height),
      clipPath: style.clipPath,
      position: style.position,
    };
  });
  expect(titleBox.width, "h1 «Новая задача» шире 1 px (не погашен)").toBeGreaterThan(1);
  expect(titleBox.height, "h1 «Новая задача» выше 1 px").toBeGreaterThan(1);
  expect(titleBox.clipPath, "у h1 нет clip-path погашения").toBe("none");
  expect(titleBox.position, "h1 не вырван из потока").not.toBe("absolute");
  // F-A (L4/R3): фокус при переходе — на ЗАГОЛОВКЕ страницы (точка входа читалки). Без `tabIndex={-1}`
  // `focus()` на h1 — пустая операция, и фокус оставался в `body`.
  expect(
    await page.evaluate(() => document.activeElement?.id ?? ""),
    "фокус на h1 «Новая задача»",
  ).toBe("dp-new-task-title");
  await expect(
    page.getByText("Создастся новая сессия на компьютере, задача уйдёт в неё."),
  ).toBeVisible();
  await expect(page.getByText("Что сделать", { exact: true })).toBeVisible();

  // «Новая задача» — полоса В ПОТОКЕ (`mode="inline"`): лист не поднимается, поле на месте.
  // «Новая задача» живёт В ПОТОКЕ (mode="inline"): поле — в секции, шара на этом экране нет.
  await page.locator(".dp-composer textarea").fill("Собери отчёт по сборке");
  await page.locator(".dp-composer").getByRole("button", { name: WORDS.send }).click();
  await expect(page).toHaveURL(/session=session-created-1/u);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Созданная сессия");
  const state = await mockState(page);
  expect(state.newCalls).toBe(1);
  expect(state.lastNew?.text).toBe("Собери отчёт по сборке");
});

test("«Новая задача»: назад — иконка 44×44 с именем, видимого слова нет, ведёт на список", async ({
  page,
}) => {
  await page.goto("/?new=1");
  await expect(page.getByRole("heading", { level: 1, name: "Новая задача" })).toBeVisible();

  // D1 §8 (11.11.2026): ТОТ ЖЕ орган, что в сессии, — иконка-кнопка 44×44; имя — в `aria-label`.
  const back = page.getByRole("link", { name: "К списку сессий" });
  await expect(back).toHaveCount(1);
  const box = (await back.boundingBox())!;
  expect(Math.round(box.width), "цель 44 в ширину").toBeGreaterThanOrEqual(44);
  expect(Math.round(box.height), "цель 44 в высоту").toBeGreaterThanOrEqual(44);
  // Видимого слова нет: текст органа пуст (иконка `aria-hidden`), поэтому в строке только имя.
  await expect(back).toHaveText("");
  await expect(back.locator("svg")).toHaveCount(1);

  // Адрес остался ссылкой (глубокая ссылка), но обычное нажатие ведёт на список.
  await expect(back).toHaveAttribute("href", "/");
  await back.click();
  await expect(page.getByRole("heading", { name: "Сессии" })).toBeVisible();
});

test("доступность числами: 320 px без прокрутки вбок, 200 %, цель 44, статус объявляется", async ({
  page,
}) => {
  await page.setViewportSize({ width: 320, height: 720 });
  await page.goto("/?session=session-live");
  await expect(page.locator(".dp-handle")).toBeVisible();
  // Крестик пункта живёт в РАСКРЫТОЙ панели (W12): раскрываем её ДО листа — под листом она цела.
  await openQueue(page);
  await openSheet(page);

  const narrow = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    innerWidth: window.innerWidth,
    send: (document.querySelector(".dp-sheet .dp-primary") as HTMLElement).offsetHeight,
    remove: (document.querySelector(".dp-queue-remove") as HTMLElement).offsetHeight,
    status:
      document.querySelectorAll(".dp-bottom [role=status]").length +
      document.querySelectorAll(".dp-sheet [role=status]").length,
  }));
  expect(narrow.scrollWidth, "320 px: без прокрутки вбок").toBeLessThanOrEqual(narrow.innerWidth);
  expect(narrow.send).toBeGreaterThanOrEqual(44);
  expect(narrow.remove).toBeGreaterThanOrEqual(44);
  expect(narrow.status).toBeGreaterThanOrEqual(1);
  expect(narrow.status).toBeGreaterThanOrEqual(1);
  // Зацеп — орган 44 высотой и во всю ширину (D11 §2.2). Сначала закрываем лист: пока он открыт, зацепа нет.
  await closeSheet(page);
  const targets = await page.evaluate(() => {
    const handle = document.querySelector(".dp-handle") as HTMLElement;
    return {
      height: handle.offsetHeight,
      width: handle.offsetWidth,
      innerWidth: window.innerWidth,
    };
  });
  expect(targets.height, "цель зацепа 44").toBeGreaterThanOrEqual(44);
  expect(targets.width, "полоса во всю ширину").toBe(targets.innerWidth);
  // 200 % — прокси `zoom: 2` (истинный text-resize Playwright не умеет): без прокрутки вбок.
  await page.evaluate(() => {
    document.documentElement.style.zoom = "2";
  });
  const zoomed = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    innerWidth: window.innerWidth,
  }));
  expect(zoomed.scrollWidth, "200 %: без прокрутки вбок").toBeLessThanOrEqual(zoomed.innerWidth);
});

test("на экране нет запрещённых полей движка и нет чужих адресов", async ({ page }) => {
  const origins = new Set<string>();
  page.on("request", (request) => origins.add(new URL(request.url()).origin));

  await page.goto("/?session=session-live");
  await expect(bar(page)).toBeVisible();
  const text = await page.innerText("body");
  for (const word of ["cwd", "clientId", "rpcId", "session/", "gateway/", "asOfSeq"]) {
    expect(text, `в тексте страницы не должно быть «${word}»`).not.toContain(word);
  }
  expect([...origins], "внешних запросов нет").toEqual([ownOrigin(page)]);
});

test("очередь: кнопки «Обновить» нет — состав приходит и живёт без нажатия", async ({
  page,
  request,
}) => {
  // S4a: в успехе перечитывать руками нечего: состав приезжает кадром `queue` из живого потока,
  // а дверь D4 — первое чтение и путь повторения после отказа.
  await request.post("/__mock/scenario", { data: { queue: "ok" } });
  await page.goto("/?session=session-live");
  await openQueue(page);
  await expect(page.locator(".dp-queue-item")).toHaveCount(2);
  await expect(page.getByRole("button", { name: "Обновить" })).toHaveCount(0);
});

test("строка исхода над шаром: успех гаснет сам, ОТКАЗ не гаснет до действия человека", async ({
  page,
  request,
}) => {
  await request.post("/__mock/scenario", {
    data: {
      authed: true,
      lang: "ru",
      list: "ok",
      pair: "ok",
      answer: "ok",
      cancel: "ok",
      task: "ok",
      queue: "ok",
      remove: "ok",
    },
  });
  await page.goto("/?session=session-idle");
  await openSheet(page);
  await sheet(page).locator("textarea").fill("Ещё задача");
  await sheet(page).getByRole("button", { name: WORDS.send }).click();

  const note = page.locator(".dp-handle-note");
  await expect(note, "успех — строкой над зацепом").toContainText("Задача принята.");
  await expect(sheet(page), "после отправки лист закрыт").toHaveCount(0);
  await expect(page.locator(".dp-handle"), "остался зацеп").toBeVisible();
  await expect(page.locator(".dp-sheet textarea"), "поля больше нет").toHaveCount(0);
  // Успех гаснет сам (~5 с): факт остаётся в очереди и ленте (`60-forms` §4).
  await expect(note).toHaveCount(0, { timeout: 9000 });

  // Отказ: лист остаётся, текст цел, отказ словами — и НЕ гаснет сам (`05-triggers` H-14).
  await request.post("/__mock/scenario", { data: { task: "not-taken" } });
  await openSheet(page);
  await sheet(page).locator("textarea").fill("Почини сборку");
  await sheet(page).getByRole("button", { name: WORDS.send }).click();
  await expect(sheet(page).getByText(/Задача не принята/)).toBeVisible();
  await expect(sheet(page).locator("textarea")).toHaveValue("Почини сборку");
  await closeSheet(page);
  const refusal = page.locator(".dp-handle-note");
  await expect(refusal, "отказ — строкой над зацепом").toContainText("не принята");
  await page.waitForTimeout(6000);
  await expect(refusal, "отказ не гаснет сам").toBeVisible();
  // Действие человека — открыл лист — прячет строку.
  await openSheet(page);
  await expect(page.locator(".dp-handle-note")).toHaveCount(0);
});

test("цена стопа стоит в разметке ДО кнопки «Остановить», а «что уходит» видно всегда", async ({
  page,
  request,
}) => {
  await request.post("/__mock/scenario", {
    data: { authed: true, lang: "ru", list: "ok", question: "none", task: "ok", stop: "stopped" },
  });

  // Пустое поле: подсказка «что уходит» видна ДО нажатия (F-2).
  await page.goto("/?session=session-idle");
  await openSheet(page);
  const body = page.locator(".dp-sheet");

  // В полосе сессии тот же компонент (h2) гасится — проверяем, что он ДЕЙСТВИТЕЛЬНО погашен:
  // коробка ≤ 1 px или `clip-path` (рат. §1.3а). Иначе видимость заголовка полосы была бы ложной.
  const barTitle = await page.evaluate(() => {
    const node = document.querySelector(".dp-sheet h2");
    if (node === null) return null;
    const box = node.getBoundingClientRect();
    const style = getComputedStyle(node);
    return { width: Math.round(box.width), clipPath: style.clipPath };
  });
  expect(barTitle, "в полосе сессии есть h2").not.toBeNull();
  expect(
    barTitle!.width <= 1 || (barTitle!.clipPath !== "none" && barTitle!.clipPath !== ""),
    `h2 полосы погашен: ширина ${barTitle!.width}, clip-path ${barTitle!.clipPath}`,
  ).toBe(true);
  await expect(body.getByText("Текст уйдёт на компьютер")).toHaveCount(0);
  await expect(body.getByText("Пустая задача не отправится", { exact: false })).toBeVisible();

  // Ход идёт: цена стопа на экране и стоит в разметке ДО кнопок.
  await page.goto("/?session=session-live");
  await openSheet(page);
  await expect(body.getByText("Текст уйдёт на компьютер")).toHaveCount(0);
  const order = await page.evaluate(() => {
    const warning = document.querySelector(".dp-composer-warning") as HTMLElement | null;
    const stop = document.querySelector(".dp-composer-stop-button") as HTMLElement | null;
    const send = document.querySelector(".dp-composer-send") as HTMLElement | null;
    if (warning === null || stop === null || send === null) return null;
    const rect = (node: HTMLElement) => {
      const box = node.getBoundingClientRect();
      return { x: Math.round(box.x), y: Math.round(box.y), w: Math.round(box.width) };
    };
    return {
      // DOCUMENT_POSITION_FOLLOWING = 4: кнопка идёт ПОСЛЕ цены в порядке разметки.
      warningBeforeStop: (warning.compareDocumentPosition(stop) & 4) !== 0,
      stopBeforeSend: (stop.compareDocumentPosition(send) & 4) !== 0,
      warningTop: Math.round(warning.getBoundingClientRect().top),
      stopTop: Math.round(stop.getBoundingClientRect().top),
      stopBox: rect(stop),
    };
  });
  expect(order, "цена, стоп и отправка на месте").not.toBeNull();
  expect(order!.warningBeforeStop, "цена стопа — ДО кнопки в разметке").toBe(true);
  expect(order!.stopBeforeSend, "стоп — до отправки").toBe(true);
  expect(order!.warningTop, "цена выше кнопки и на экране").toBeLessThan(order!.stopTop);

  // Кегль жёсткий (рат. §8 п. 1): цена стопа — 14 px, не мельче «тихого» текста.
  const fonts = await page.evaluate(() => ({
    warning: getComputedStyle(document.querySelector(".dp-composer-warning") as HTMLElement)
      .fontSize,
  }));
  expect(fonts.warning, "цена стопа — 14 px").toBe("14px");
  // Отказ отправки: ярлык главной кнопки становится «Повторить», коробка «Остановить» НЕ двигается.
  await request.post("/__mock/scenario", { data: { task: "not-taken" } });
  await body.locator("textarea").fill("Почини сборку");
  await body.getByRole("button", { name: "Отправить задачу" }).click();
  await expect(sheet(page).getByText(/Задача не принята/)).toBeVisible();
  const retry = body.getByRole("button", { name: "Повторить" });
  await expect(retry).toBeVisible();
  const afterFailure = await page.evaluate(() => {
    const stop = document.querySelector(".dp-composer-stop-button") as HTMLElement;
    const box = stop.getBoundingClientRect();
    return { x: Math.round(box.x), y: Math.round(box.y), w: Math.round(box.width) };
  });
  expect(afterFailure, "коробка «Остановить» не двигается от смены ярлыка").toEqual(order!.stopBox);

  // Любая правка текста возвращает «Отправить задачу» (это уже другая задача).
  await body.locator("textarea").fill("Почини сборку и тесты");
  await expect(body.getByRole("button", { name: "Отправить задачу" })).toBeVisible();
  await expect(body.getByRole("button", { name: "Повторить" })).toHaveCount(0);

  // После отказа СТОПА обе кнопки остаются собой: «Остановить» и «Отправить задачу».
  await request.post("/__mock/scenario", { data: { task: "ok", stop: "fail" } });
  await body.locator("textarea").fill("Задача");
  await body.getByRole("button", { name: "Остановить" }).click();
  await expect(sheet(page).getByText(/Остановить не удалось/)).toBeVisible();
  await expect(body.getByRole("button", { name: "Остановить" })).toBeVisible();
  await expect(body.getByRole("button", { name: "Отправить задачу" })).toBeVisible();
  await request.post("/__mock/scenario", { data: { stop: "stopped" } });
});

// ── Живая проверка 31.10.2026: телефон по HTTP (небезопасный контекст) ───────────────────────

test("телефон по http (нет crypto.randomUUID): «Отправить задачу» отправляет и говорит «Задача принята»", async ({
  page,
}) => {
  // Телефон открывает http://192.168.x.x:randomUUID есть ТОЛЬКО в безопасном контексте,
  // поэтому здесь его нет — ровно как на живом устройстве. Стенд на localhost этого не видел.
  await page.addInitScript(() => {
    Object.defineProperty(window.crypto, "randomUUID", { value: undefined, configurable: true });
  });
  await page.goto("/?session=session-idle");
  await openSheet(page);
  const composer = sheet(page);
  await composer.locator("textarea").fill("Почини сборку");
  await composer.getByRole("button", { name: WORDS.send }).click();

  // Решающее: нажатие НЕ молчит и POST уходит.
  await expect(page.locator(".dp-handle-note"), "успех — строкой над зацепом").toContainText(
    "Задача принята.",
  );
  const posted = await mockState(page);
  expect(posted.lastPrompt?.requestId ?? "", "requestId формы калитки назван").toMatch(
    /^[A-Za-z0-9._:-]{1,128}$/u,
  );
});

test("нет ни randomUUID, ни getRandomValues: нажатие не молчит, а говорит словами", async ({
  page,
}) => {
  await page.addInitScript(() => {
    Object.defineProperty(window.crypto, "randomUUID", { value: undefined, configurable: true });
    Object.defineProperty(window.crypto, "getRandomValues", {
      value: undefined,
      configurable: true,
    });
  });
  await page.goto("/?session=session-idle");
  await openSheet(page);
  const composer = sheet(page);
  await composer.locator("textarea").fill("Почини сборку");
  await composer.getByRole("button", { name: WORDS.send }).click();

  // Слово существующего ключа пульта (PhoneWebCrash), а не тишина и не имя ключа.
  await expect(composer.getByText("Пульт не запустился", { exact: false })).toBeVisible();
  expect((await mockState(page)).promptCalls, "ничего не отправлено").toBe(0);
});

test("набор текста не двигает ленту: лист над клавиатурой на 360 и 320 (DEF-089)", async ({
  page,
}) => {
  const widths = [
    { width: 360, height: 800 },
    { width: 320, height: 720 },
  ];
  for (const { width, height } of widths) {
    await page.setViewportSize({ width, height });
    await page.goto("/?session=session-many");
    await expect(page.locator(".dp-feed li").last()).toBeVisible();
    // DEF-089 теперь про ДВА места сразу: свёрнутый лист в потоке и поднятый над клавиатурой.
    // Сначала дождаться, что живой конец встал: иначе стартовое число — «ещё едем».
    await expect
      .poll(
        () =>
          page.evaluate(() => {
            const items = document.querySelectorAll(".dp-feed li");
            const last = items[items.length - 1] as HTMLElement;
            const band = (
              document.querySelector(".dp-handle") as HTMLElement
            ).getBoundingClientRect();
            return Math.round(last.getBoundingClientRect().bottom - band.top);
          }),
        { message: `${width}: живой конец над полосой до опыта` },
      )
      .toBeLessThanOrEqual(0);
    const start = await scrollState(page);
    await openSheet(page);
    const field = sheet(page).locator("textarea");
    // Черновик тот же (та же сессия) — чистим, иначе второй круг допишет к первому.
    await field.fill("");
    await field.click();
    // Клавиатура телефона: визуальный вьюпорт становится ниже — ровно то состояние, в котором
    // браузер прокручивал страницу к каретке (замер 31.10.2026: +300 px при 800→500).
    await page.setViewportSize({ width, height: height - 300 });
    await page.waitForTimeout(150);

    const before = await scrollState(page);
    await page.keyboard.type("Почини сбор", { delay: 40 });
    await page.waitForTimeout(300);
    const after = await scrollState(page);

    expect(after.y - before.y, `${width}: страница не сдвинулась при наборе`).toBe(0);
    expect(
      (after.lastTop ?? 0) - (before.lastTop ?? 0),
      `${width}: лента не сдвинулась при наборе`,
    ).toBe(0);

    // (2) Лист не уехал под клавиатуру: его низ — в окне, поле видно, набранное цело.
    const keyboardSafe = await page.evaluate(() => {
      const node = document.querySelector(".dp-sheet") as HTMLElement;
      const box = node.getBoundingClientRect();
      return { bottom: Math.round(box.bottom), viewport: window.innerHeight };
    });
    expect(keyboardSafe.bottom, `${width}: лист в окне над клавиатурой`).toBeLessThanOrEqual(
      keyboardSafe.viewport,
    );
    await expect(field).toHaveValue("Почини сбор");

    await page.setViewportSize({ width, height });
    await closeSheet(page);
    // Закрытие возвращает к живому концу — лента снова над полосой (и страница не уехала вверх).
    await expect
      .poll(
        () =>
          page.evaluate(() => {
            const items = document.querySelectorAll(".dp-feed li");
            const last = items[items.length - 1] as HTMLElement;
            const band = (
              document.querySelector(".dp-handle") as HTMLElement
            ).getBoundingClientRect();
            return Math.round(last.getBoundingClientRect().bottom - band.top);
          }),
        { message: `${width}: после закрытия листа живой конец снова над полосой` },
      )
      .toBeLessThanOrEqual(0);
    const end = await scrollState(page);
    expect(end.y, `${width}: закрытие не увело страницу вверх`).toBeGreaterThanOrEqual(start.y - 2);
  }
});

test("пустой ответ агента не рисуется пузырём (защита от старой калитки)", async ({ page }) => {
  // DEF-093: калитка такие записи отбрасывает, но веб обязан не рисовать пузырь и в этом случае —
  // старая калитка, чужой снимок или обрезка до пустоты не должны оставлять дыру в ленте.
  // Сессия своя (`session-empty-assistant`): флаг сценария залипал и тек в чужие проверки.
  await page.goto("/?session=session-empty-assistant");
  await expect(page.getByText("Непустой ответ агента.")).toBeVisible();

  const bubbles = await page.evaluate(() =>
    [...document.querySelectorAll(".dp-feed li.dp-msg.dp-agent")].map((node) =>
      (node.textContent ?? "").trim(),
    ),
  );
  expect(bubbles, `пузырей агента: ${JSON.stringify(bubbles)}`).not.toContain("");
  expect(
    bubbles.every((text) => text !== ""),
    "ни одного пустого пузыря",
  ).toBe(true);
});

// ── S3e: зацеп и поверхность листа (F-1…F-2), «назад» (F-L1-1) ─────────────────────────────────
// ШАРИКА НЕТ (D11 §2.3): его драг, запись позиции и кламп пары удалены вместе с `model/ball.ts` —
// новые проверки зацепа и свайпов живут в `w11-handle.spec.ts`.

test("лист ввода: страница инертна, фокус в поле, Esc возвращает его на шеврон, Tab идёт по кругу", async ({
  page,
}) => {
  await page.goto("/?session=session-live");
  const main = page.locator("main");
  const handle = page.locator(".dp-handle");
  await expect(main).not.toHaveAttribute("inert", "");

  await openSheet(page);
  await expect(main, "страница под скримом недействующая").toHaveAttribute("inert", "");
  await expect(sheet(page).locator("textarea"), "фокус в поле при раскрытии").toBeFocused();

  // Tab с последнего органа уходит на первый, Shift+Tab — обратно: фокус не покидает лист.
  // Только ДЕЙСТВУЮЩИЕ органы: у погашенной «Отправить задачу» фокуса нет, и «последний» по
  // разметке мог оказаться недоступным (найдено прогоном F-2).
  const items = sheet(page).locator(
    "button:not([disabled]), textarea:not([disabled]), a[href], input:not([disabled])",
  );
  const count = await items.count();
  expect(count).toBeGreaterThan(2);
  await items.nth(count - 1).focus();
  await page.keyboard.press("Tab");
  await expect(items.first()).toBeFocused();
  await page.keyboard.press("Shift+Tab");
  await expect(items.nth(count - 1)).toBeFocused();

  await page.keyboard.press("Escape");
  await expect(sheet(page)).toHaveCount(0);
  await expect(main).not.toHaveAttribute("inert", "");
  await expect(handle, "фокус вернулся на зацеп").toBeFocused();
});

test("«назад» закрывает поднятый лист, а без листа уводит на список сессий", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("link", { name: /Починить калитку/ }).click();
  await expect(page.getByRole("heading", { name: "Починить калитку" })).toBeVisible();

  // Лист открыт → системная «назад» (popstate) закрывает его, страница остаётся той же сессией.
  await openSheet(page);
  await expect(sheet(page)).toBeVisible();
  await page.evaluate(() => history.back());
  await expect(sheet(page), "лист закрыт по «назад»").toHaveCount(0);
  await expect(page.locator("main")).not.toHaveAttribute("inert", "");
  await expect(page.locator(".dp-handle"), "фокус на зацепе").toBeFocused();
  await expect(page).toHaveURL(/session=session-live/u);

  // Без листа «назад» уводит на список — как было.
  await page.evaluate(() => history.back());
  await expect(page.getByRole("heading", { name: "Сессии" })).toBeVisible();
});

test("зацеп не накрывает ни ленту, ни вопрос: нижний стек стоит ниже всего потока (360 и 320)", async ({
  browser,
  request,
}) => {
  for (const width of [360, 320] as const) {
    const context = await browser.newContext({ viewport: { width, height: 800 } });
    const page = await context.newPage();
    await request.post("/__mock/scenario", {
      data: {
        authed: true,
        lang: "ru",
        list: "ok",
        pair: "ok",
        answer: "ok",
        cancel: "ok",
        task: "ok",
        stop: "stopped",
        queue: "ok",
        remove: "ok",
        question: "none",
        questionSession: "session-live",
      },
    });
    await page.goto("/?session=session-live");
    await expect(page.locator(".dp-handle")).toBeVisible();
    // Крестики очереди живут в раскрытой панели (W12) — раскрываем её и смотрим пересечения.
    await openQueue(page);

    const measure = () =>
      page.evaluate(() => {
        const handle = (
          document.querySelector(".dp-handle") as HTMLElement
        ).getBoundingClientRect();
        const hits = (node: Element) => {
          const box = node.getBoundingClientRect();
          return (
            box.right > handle.left &&
            box.left < handle.right &&
            box.bottom > handle.top &&
            box.top < handle.bottom
          );
        };
        const crosses = [...document.querySelectorAll(".dp-queue-remove")];
        const choice = document.querySelector(".dp-choice");
        const note = document.querySelector(".dp-handle-note");
        const stack = (document.querySelector(".dp-bottom") as HTMLElement).getBoundingClientRect();
        return {
          handle: { left: Math.round(handle.left), top: Math.round(handle.top) },
          stackTop: Math.round(stack.top),
          crosses: crosses.length,
          crossHits: crosses.filter(hits).length,
          choiceHits: choice === null ? null : hits(choice),
          noteHits: note === null ? 0 : hits(note) ? 1 : 0,
          viewport: { w: window.innerWidth, h: window.innerHeight },
        };
      });

    const queueView = await measure();
    expect(queueView.crosses, `${width}: крестики очереди на экране есть`).toBeGreaterThan(0);
    expect(queueView.crossHits, `${width}: зацеп не накрывает ни один крестик`).toBe(0);
    expect(queueView.handle.left, `${width}: полоса зацепа — во всю ширину`).toBe(0);

    // Вопрос — карточкой в потоке: зацеп и его строка исхода стоят НИЖЕ всего потока.
    await request.post("/__mock/scenario", { data: { question: "single" } });
    await expect(page.locator(".dp-question")).toBeVisible();
    const questionView = await measure();
    expect(questionView.choiceHits, `${width}: зацеп не накрывает первый вариант`).toBe(false);
    expect(questionView.crossHits, `${width}: зацеп не накрывает крестики и при вопросе`).toBe(0);
    expect(questionView.noteHits, `${width}: строка исхода не пересекает зацеп`).toBe(0);

    await context.close();
  }
});

// ── R2 (живая проверка владельца 11.11.2026) + D11 §2.2: зацеп, растущее поле, липкая шапка ────────

test("зацеп — полоса во всю ширину с ручкой и шевроном вверх: 44 высотой, без transition, открывает лист", async ({
  page,
}) => {
  await page.goto("/?session=session-live");
  const handle = page.locator(".dp-handle");
  await expect(handle).toBeVisible();
  const info = await handle.evaluate((node) => {
    const box = node.getBoundingClientRect();
    const grip = node.querySelector(".dp-handle-grip");
    const finger = node.querySelector(".dp-handle-chevron");
    const style = getComputedStyle(node);
    return {
      w: Math.round(box.width),
      h: Math.round(box.height),
      innerWidth: window.innerWidth,
      bottom: Math.round(window.innerHeight - box.bottom),
      bg: style.backgroundColor,
      radius: style.borderTopLeftRadius,
      transition: style.transitionDuration,
      touchAction: style.touchAction,
      chevron: finger !== null,
      grip: grip === null ? null : Math.round(grip.getBoundingClientRect().width),
      gripHeight: grip === null ? null : Math.round(grip.getBoundingClientRect().height),
      name: node.getAttribute("aria-label"),
      expanded: node.getAttribute("aria-expanded"),
      controls: node.getAttribute("aria-controls"),
      text: (node.textContent ?? "").trim(),
    };
  });
  expect(info.w, "полоса во всю ширину — промахнуться нельзя").toBe(info.innerWidth);
  expect(info.h, "высота 44 + отбивка — не меньше цели").toBeGreaterThanOrEqual(44);
  expect(info.bottom, "зацеп прибит к низу экрана").toBe(0);
  expect(info.radius, "скругление 8 — край шторки").toBe("8px");
  // Фон — карточка, а не акцент: зацеп читается как край шторки, а не как кнопка.
  expect(info.bg).toBe("rgb(255, 255, 255)");
  expect(info.chevron, "шеврон «вверх» внутри").toBe(true);
  expect(info.grip, "ручка 40×4 — знак «тянется»").toBe(40);
  expect(info.gripHeight).toBe(4);
  expect(info.transition, "никакого догоняния").toBe("0s");
  expect(info.touchAction, "только на зацепе: прокручивать здесь нечего").toBe("none");
  expect(info.name, "имя из существующего ключа — «Задача агенту», а не имя поля").toBe(
    "Задача агенту",
  );
  expect(info.text, "видимого слова нет").toBe("");
  expect(info.expanded).toBe("false");
  expect(info.controls, "`aria-controls` указывает на лист").toMatch(/sheet$/u);

  await handle.click();
  await expect(sheet(page), "нажатие раскрывает лист").toBeVisible();
});

test("зацеп стоит при прокрутке и держится за нижним краем при «дыхании» адресной строки (360, 320, тёмная)", async ({
  browser,
}) => {
  for (const variant of [
    { width: 360, height: 720, colorScheme: "light" as const },
    { width: 320, height: 720, colorScheme: "light" as const },
    { width: 320, height: 720, colorScheme: "dark" as const },
  ]) {
    const context = await browser.newContext({
      viewport: { width: variant.width, height: variant.height },
      colorScheme: variant.colorScheme,
    });
    const page = await context.newPage();
    await page.goto("/?session=session-many");
    await expect(page.locator(".dp-feed li").last()).toBeVisible();
    const handle = page.locator(".dp-handle");
    await expect(handle).toBeVisible();
    const before = (await handle.boundingBox())!;
    const same = async (label: string) => {
      const now = (await handle.boundingBox())!;
      expect(
        Math.abs(now.x - before.x),
        `${variant.width}/${variant.colorScheme}: ${label} — X`,
      ).toBeLessThanOrEqual(1);
      expect(
        Math.abs(now.y - before.y),
        `${variant.width}/${variant.colorScheme}: ${label} — Y`,
      ).toBeLessThanOrEqual(1);
    };

    // Прокрутка ленты — вниз, вверх, в середину: зацеп обязан стоять (он fixed во вьюпорте).
    for (const y of [300, 1200, 0, 600]) {
      await page.evaluate((top) => window.scrollTo(0, top), y);
      await page.waitForTimeout(60);
      await same(`прокрутка ${y}`);
    }

    // «Дыхание» адресной строки (D1 §3.2): видимая высота меняется — зацеп ДЕРЖИТСЯ ЗА ВИДИМЫЙ
    // НИЗ (тот же 0 до края), а не за layout-координату. Абсолютный Y сдвигается вместе с краем.
    const edgeGap = async () =>
      page.evaluate(() => {
        const box = (document.querySelector(".dp-handle") as HTMLElement).getBoundingClientRect();
        return Math.round(window.innerHeight - box.bottom);
      });
    expect(await edgeGap(), "до дыхания — у низа").toBe(0);

    await page.setViewportSize({ width: variant.width, height: variant.height - 60 });
    const shrunk = (await handle.boundingBox())!;
    expect(Math.abs(shrunk.x - before.x), "строка свернулась — X").toBeLessThanOrEqual(1);
    // Пересчёт — в кадре (rAF): ждём сходимости, а не одного замера сразу после resize.
    await expect
      .poll(() => edgeGap(), { message: "строка свернулась — зацеп у нового низа" })
      .toBe(0);

    await page.setViewportSize({ width: variant.width, height: variant.height });
    await expect.poll(() => edgeGap(), { message: "строка вернулась — снова у низа" }).toBe(0);
    await same("строка вернулась");

    // После reload — те же числа: координат больше нет (нет ни записи позиции, ни дрейфа).
    await page.reload();
    const reloaded = (await page.locator(".dp-handle").boundingBox())!;
    expect(Math.abs(reloaded.x - before.x), "reload: X").toBeLessThanOrEqual(1);
    expect(
      Math.abs(reloaded.y - before.y),
      "reload: Y — зацеп снова у того же низа",
    ).toBeLessThanOrEqual(1);
    await context.close();
  }
});

test("поле ввода растёт по содержимому до потолка, лист растёт вместе, --dp-sheet-h пересчитан", async ({
  page,
}) => {
  await page.goto("/?session=session-live");
  await openSheet(page);
  const field = sheet(page).locator("textarea");
  const measure = () =>
    page.evaluate(() => {
      const input = document.querySelector("#dp-session-task") as HTMLTextAreaElement;
      const panel = document.querySelector(".dp-sheet") as HTMLElement;
      const root = getComputedStyle(document.documentElement);
      return {
        field: Math.round(input.getBoundingClientRect().height),
        scroll: input.scrollHeight,
        sheet: panel.offsetHeight,
        variable: root.getPropertyValue("--dp-sheet-h").trim(),
        overflowY: getComputedStyle(input).overflowY,
      };
    });

  const one = await measure();
  expect(one.overflowY, "пустое поле — без своей прокрутки").toBe("hidden");

  await field.fill("первая строка\nвторая строка\nтретья строка");
  const three = await measure();
  expect(three.field, "поле выросло на три строки").toBeGreaterThan(one.field + 20);
  expect(three.sheet, "лист вырос вместе с полем").toBeGreaterThan(one.sheet);
  // Переменная высоты ставится ResizeObserver-ом — ждём кадр-другой, а не читаем сразу.
  await expect
    .poll(async () => (await measure()).variable, "--dp-sheet-h догоняет высоту листа")
    .toBe(`${three.sheet}px`);

  // Потолок: восемь строк — поле держится на шести и скроллится само.
  await field.fill(Array.from({ length: 8 }, (_, index) => `строка ${index + 1}`).join("\n"));
  const many = await measure();
  expect(many.field, "выше потолка не растёт").toBeLessThanOrEqual(180);
  expect(many.overflowY, "дальше — своя прокрутка").toBe("auto");
  await expect.poll(async () => (await measure()).variable).toBe(`${many.sheet}px`);
  expect(many.scroll).toBeGreaterThan(many.field);
  await expect.poll(async () => (await measure()).variable).toBe(`${many.sheet}px`);

  // 4000 знаков принимаются, счётчик честный.
  await field.fill("я".repeat(4000));
  await expect(page.getByText("4000 / 4000")).toBeVisible();
});

test("шапка сессии липкая: кнопка назад видна после прокрутки и ведёт на список (320 и тёмная)", async ({
  browser,
}) => {
  for (const variant of [
    { width: 360, height: 720, colorScheme: "light" as const },
    { width: 320, height: 720, colorScheme: "dark" as const },
  ]) {
    const context = await browser.newContext({
      viewport: { width: variant.width, height: variant.height },
      colorScheme: variant.colorScheme,
    });
    const page = await context.newPage();
    await page.goto("/?session=session-many");
    await expect(page.locator(".dp-feed li").last()).toBeVisible();
    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    await page.waitForTimeout(200);

    const back = page.getByRole("button", { name: "К списку сессий" });
    await expect(back, "кнопка видна и после прокрутки").toBeVisible();
    const box = (await back.boundingBox())!;
    expect(Math.round(box.height), "цель 44").toBeGreaterThanOrEqual(44);
    expect(Math.round(box.y), "шапка держится у верха").toBeLessThan(120);
    const header = await page.evaluate(() => {
      const node = document.querySelector(".dp-head-session") as HTMLElement;
      return {
        position: getComputedStyle(node).position,
        bg: getComputedStyle(node).backgroundColor,
      };
    });
    expect(header.position).toBe("sticky");
    if (variant.colorScheme === "dark") {
      expect(header.bg, "тёмная тема: шапка цвета страницы #16161a").toBe("rgb(22, 22, 26)");
    }

    await back.click();
    await expect(page.getByRole("heading", { name: "Сессии" })).toBeVisible();
    await context.close();
  }
});

// ── R2: подписи кнопок НЕ рвутся внутри слова (живая проверка 11.11.2026: «Остановит|ь» на 320) ─────

test("подписи кнопок не рвутся внутри слова: 320, 344 и 360 — одна строка, горизонтали нет", async ({
  page,
  request,
}) => {
  const measure = () =>
    page.evaluate(() => {
      const nodes = [
        ...document.querySelectorAll(
          ".dp-sheet button, .dp-sheet .dp-primary, .dp-queue-item button, .dp-link-button",
        ),
      ] as HTMLElement[];
      return {
        scrollWidth: document.documentElement.scrollWidth,
        innerWidth: window.innerWidth,
        buttons: nodes
          .filter((node) => node.innerText.trim() !== "")
          .map((node) => ({
            text: node.innerText.trim(),
            height: Math.round(node.getBoundingClientRect().height),
            nowrap: getComputedStyle(node).whiteSpace,
            hyphens: getComputedStyle(node).hyphens,
          })),
      };
    });

  for (const width of [320, 344, 360]) {
    await page.setViewportSize({ width, height: 720 });
    await request.post("/__mock/scenario", {
      data: {
        authed: true,
        lang: "ru",
        list: "ok",
        pair: "ok",
        answer: "ok",
        cancel: "ok",
        task: "ok",
        stop: "stopped",
        queue: "ok",
        remove: "ok",
      },
    });
    await page.goto("/?session=session-live");
    await openSheet(page);
    const sheetView = await measure();
    expect(sheetView.scrollWidth, `${width}: без горизонтали`).toBeLessThanOrEqual(
      sheetView.innerWidth,
    );
    const stop = sheetView.buttons.find((button) => button.text === "Остановить");
    expect(stop, `${width}: «Остановить» на месте`).toBeTruthy();
    expect(stop!.height, `${width}: «Остановить» — одна строка`).toBeLessThanOrEqual(48);
    expect(stop!.nowrap, `${width}: подпись не рвётся`).toBe("nowrap");
    expect(stop!.hyphens, `${width}: переносов нет`).toBe("none");
    const send = sheetView.buttons.find((button) => button.text === "Отправить задачу");
    expect(send!.height, `${width}: «Отправить задачу» — одна строка`).toBeLessThanOrEqual(48);

    // Очередь: подтверждение — «Убрать задачу» и «Оставить» тоже одной строкой.
    await closeSheet(page);
    await openQueue(page);
    await page.locator(".dp-queue-remove").first().click();
    const queueView = await measure();
    for (const label of ["Убрать задачу", "Оставить"]) {
      const button = queueView.buttons.find((item) => item.text === label);
      expect(button, `${width}: «${label}» на месте`).toBeTruthy();
      expect(button!.height, `${width}: «${label}» — одна строка`).toBeLessThanOrEqual(48);
    }
    await page.getByRole("button", { name: "Оставить" }).click();
  }
});
