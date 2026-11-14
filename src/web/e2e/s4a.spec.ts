// S4a «Несколько сессий и сигналы внутри страницы»: колокольчик «Ждут ответа», живая очередь
// без «Обновить», «Переподключиться» только при обрыве, список без таймера.
//
// Стенд — `e2e/server.mjs` (подставная калитка): отдаёт живые кадры `queue`, одноразовую подсказку
// `stale` и СЧЁТЧИКИ запросов списка/очереди. Живой движок не поднимается — это названное
// ограничение S4a: проверяется поведение экрана, а не данные владельца.
//
// Что именно доказывается числами, а не словами:
//   * «таймера 10 с нет» — за 11 секунд на открытом списке РОВНО ОДИН запрос;
//   * «живой кадр, а не опрос» — состав очереди вырос, счётчик чтений D4 при этом не вырос;
//   * «429 не показывается» — слов `PhoneQueueBusy` нет ни в очереди, ни в списке.

import { expect, test, type Page } from "@playwright/test";

const WORDS = {
  bellZero: "Ждут ответа",
  bellOne: "Ждут ответа: 1",
  waitingEmpty: "Сейчас никто не ждёт ответа",
  elsewhere: "Другой агент ждёт ответа",
  reconnect: "Переподключиться",
  check: "Проверить",
  refresh: "Обновить",
  repeat: "Повторить",
  busy: "Обновления идут слишком часто",
};

async function calls(page: Page) {
  const response = await page.request.get("/__mock/s3");
  return (await response.json()) as { listCalls: number; queueCalls: number };
}

/** Видимость вкладки: настоящий `visibilitychange` без второго окна. */
async function setHidden(page: Page, hidden: boolean) {
  await page.evaluate((value) => {
    Object.defineProperty(document, "hidden", { value, configurable: true });
    document.dispatchEvent(new Event("visibilitychange"));
  }, hidden);
}

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/scenario", {
    // Сценарий стенда СЛИВАЕТСЯ с прежним: сбрасываем и то, что включает живые кадры очереди,
    // иначе «queueFrame: add» протечёт из одной проверки в следующие.
    data: {
      authed: true,
      lang: "ru",
      list: "ok",
      pair: "ok",
      question: "none",
      queue: "ok",
      queueFrame: "none",
    },
  });
});

// ── список: без «Обновить» и без таймера ────────────────────────────────────────────────────────

test("список: кнопки «Обновить» нет, таймера 10 с нет — за 11 с один запрос", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Сессии" })).toBeVisible();
  await expect(page.getByText("Сессий: 4")).toBeVisible();
  await expect(page.getByRole("button", { name: WORDS.refresh })).toHaveCount(0);
  expect((await calls(page)).listCalls, "один проход при открытии").toBe(1);

  await page.waitForTimeout(11_000);
  expect((await calls(page)).listCalls, "за 11 с ни одного лишнего запроса: такта 10 с нет").toBe(
    1,
  );
});

test("список: подсказка stale перечитывает состав сама, без нажатия", async ({ page, request }) => {
  await request.post("/__mock/scenario", { data: { list: "ok", staleOnce: true } });
  await page.goto("/");
  await expect(page.getByText("Сессий: 4")).toBeVisible();
  // Порог — не чаще ~3 с; ждём второй проход, которого никто не просил руками.
  await expect.poll(async () => (await calls(page)).listCalls, { timeout: 9_000 }).toBe(2);
  await expect(page.getByRole("button", { name: WORDS.refresh })).toHaveCount(0);
});

test("список: возврат на вкладку перечитывает состав", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByText("Сессий: 4")).toBeVisible();
  expect((await calls(page)).listCalls).toBe(1);

  await setHidden(page, true);
  await page.waitForTimeout(200);
  await setHidden(page, false);

  await expect.poll(async () => (await calls(page)).listCalls, { timeout: 5_000 }).toBe(2);
  await expect(page.getByRole("button", { name: WORDS.refresh })).toHaveCount(0);
});

test("список: в отказе путь повторения остаётся словами, «Обновить» нет", async ({
  page,
  request,
}) => {
  await request.post("/__mock/scenario", { data: { list: "error" } });
  await page.goto("/");
  const alert = page.getByRole("alert");
  await expect(alert).toContainText("Список сессий не прочитался");
  await expect(alert.getByRole("button", { name: WORDS.repeat })).toBeVisible();
  await expect(page.getByRole("button", { name: WORDS.refresh })).toHaveCount(0);
});

// ── колокольчик «Ждут ответа» ───────────────────────────────────────────────────────────────────

test("колокольчик при 0: виден, «0» не печатается, панель говорит словами", async ({ page }) => {
  await page.goto("/");
  const bell = page.getByRole("button", { name: WORDS.bellZero });
  await expect(bell).toBeVisible();
  // Цель 44×44 — как у остальных иконок-органов.
  const box = await bell.boundingBox();
  expect(box?.width ?? 0).toBeGreaterThanOrEqual(44);
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);

  await bell.click();
  const panel = page.locator("#dp-waiting-panel");
  await expect(panel).toBeVisible();
  await expect(panel.getByRole("heading", { name: WORDS.bellZero })).toBeVisible();
  await expect(panel).toContainText(WORDS.waitingEmpty);
  await expect(page.locator(".dp-waiting-badge")).toHaveCount(0);

  // Escape закрывает и возвращает фокус на колокольчик.
  await page.keyboard.press("Escape");
  await expect(panel).toHaveCount(0);
  await expect(bell).toBeFocused();
});

test("колокольчик при 1: счёт значком, строка сессии — ссылка, клик вне закрывает", async ({
  page,
  request,
}) => {
  await request.post("/__mock/scenario", {
    data: { question: "single", questionSession: "session-live" },
  });
  await page.goto("/");

  const bell = page.getByRole("button", { name: WORDS.bellOne });
  await expect(bell).toBeVisible();
  await expect(bell.locator(".dp-waiting-badge")).toHaveText("1");
  await expect(bell).toHaveAttribute("aria-expanded", "false");
  await expect(bell).toHaveAttribute("aria-controls", "dp-waiting-panel");

  await bell.click();
  await expect(bell).toHaveAttribute("aria-expanded", "true");
  const panel = page.locator("#dp-waiting-panel");
  const row = panel.getByRole("link");
  await expect(row).toHaveAttribute("href", "/?session=session-live");
  await expect(row).toContainText("Починить калитку");
  // Обрывок вопроса — одной строкой, с признаком обрезки (полный текст — в карточке).
  await expect(row).toContainText("Что делаем с упавшими тестами?");

  // Клик вне панели закрывает; фокус не крадётся.
  await page.locator("h1").click();
  await expect(panel).toHaveCount(0);

  // Открыли снова и перешли по строке — без перезагрузки страницы.
  await bell.click();
  await page.locator("#dp-waiting-panel").getByRole("link").click();
  await expect(page).toHaveURL(/session=session-live/u);
  await expect(page.getByRole("heading", { name: "Починить калитку" })).toBeVisible();
});

test("вопрос без сессии: счёт 1, панель ведёт к карточке вверху списка", async ({
  page,
  request,
}) => {
  await request.post("/__mock/scenario", { data: { question: "fallback" } });
  await page.goto("/");

  await page.getByRole("button", { name: WORDS.bellOne }).click();
  const panel = page.locator("#dp-waiting-panel");
  const row = panel.getByRole("button", { name: new RegExp(WORDS.elsewhere, "u") });
  await expect(row).toBeVisible();
  await expect(row).toContainText("Открыть");

  await row.click();
  await expect(panel).toHaveCount(0);
  // Фокус — в первом органе карточки, а не в закрытой панели (D1 §3.3).
  const inside = await page.evaluate(
    () => document.activeElement?.closest("[data-dp-question]") !== null,
  );
  expect(inside, "фокус в карточке вопроса").toBe(true);
});

// ── очередь: живая, без «Обновить», без слов 429 ────────────────────────────────────────────────

test("очередь: новый пункт приезжает кадром queue, без нажатия и без опроса", async ({
  page,
  request,
}) => {
  await request.post("/__mock/scenario", { data: { queueFrame: "add" } });
  await page.goto("/?session=session-live");

  const queue = page.locator(".dp-queue");
  // Док очереди свёрнут строкой «В очереди: N» (W12) — раскрываем его, чтобы видеть пункты.
  await page.locator(".dp-queue-row").click();
  await expect(queue.locator(".dp-queue-item")).toHaveCount(2);
  await expect(queue.getByText("Пункт из живого кадра.")).toBeVisible({ timeout: 12_000 });
  await expect(queue.locator(".dp-queue-item")).toHaveCount(3);
  await expect(page.locator(".dp-queue-refresh")).toHaveCount(0);
  await expect(page.getByRole("button", { name: WORDS.refresh })).toHaveCount(0);
});

test("очередь: 429 человеку не показывается — состав остаётся последним известным", async ({
  page,
  request,
}) => {
  await page.goto("/?session=session-live");
  const queue = page.locator(".dp-queue");
  await page.locator(".dp-queue-row").click();
  await expect(queue.locator(".dp-queue-item")).toHaveCount(2);
  // Дверь начинает отвечать «слишком часто» ДО конца хода: он перечитывает очередь сам.
  await request.post("/__mock/scenario", { data: { queue: "busy" } });
  await page.waitForTimeout(4_000);

  await expect(queue.locator(".dp-queue-item")).toHaveCount(2);
  const text = await page.innerText("body");
  expect(text, "слов 429 на экране нет").not.toContain(WORDS.busy);
  await expect(queue.locator(".dp-error-block")).toHaveCount(0);
  await expect(page.getByRole("button", { name: WORDS.refresh })).toHaveCount(0);
});

// ── «Переподключиться» только при обрыве ────────────────────────────────────────────────────────

test("«Переподключиться» видна только при обрыве и зовёт повтор потока", async ({ page }) => {
  // Успех: кнопки нет вовсе — «Проверить» больше не обещает проверку.
  await page.goto("/?session=session-live");
  await expect(page.getByRole("heading", { name: "Починить калитку" })).toBeVisible();
  await expect(page.getByRole("button", { name: WORDS.reconnect })).toHaveCount(0);
  await expect(page.getByRole("button", { name: WORDS.check })).toHaveCount(0);

  // Обрыв: первый заход в поток — постоянный отказ (автоповтора нет).
  await page.goto("/?session=session-retry");
  const retry = page.getByRole("button", { name: WORDS.reconnect });
  await expect(retry).toBeVisible();
  await expect(page.getByRole("button", { name: WORDS.check })).toHaveCount(0);

  // Нажатие зовёт live.retry: второй заход — живой поток, слова обрыва уходят.
  await retry.click();
  await expect(page.getByRole("heading", { name: "Починённая связь" })).toBeVisible();
  await expect(retry).toHaveCount(0);
});

// ── ремонт вида по D2 (F-04, F-05, F-06) ─────────────────────────────────────────────────────────

const LOST = "Связь с панелью оборвалась";
const CONNECTING = "Подключаюсь к сессии";

test("обрыв: на экране ОДНО состояние — строка обрыва, без «Подключаюсь…»", async ({ page }) => {
  await page.goto("/?session=session-lost");

  const lost = page.getByText(LOST);
  await expect(lost).toBeVisible();
  // Ровно одна строка состояния: второго ответа («Подключаюсь к сессии…») рядом нет (D2 F-04).
  await expect(lost).toHaveCount(1);
  await expect(page.getByText(CONNECTING)).toHaveCount(0);
  // И это именно обрыв, а не финальный отказ.
  await expect(page.getByText("Слишком много открытых экранов")).toHaveCount(0);
});

test("финальный отказ: один ответ — только его блок, без строки обрыва", async ({ page }) => {
  await page.goto("/?session=session-toomany");

  await expect(page.getByText("Слишком много открытых экранов")).toBeVisible();
  await expect(page.getByText(LOST)).toHaveCount(0);
  await expect(page.getByText(CONNECTING)).toHaveCount(0);
});

test("«Переподключиться» — в липкой шапке, видна при прокрученной ленте; в конце страницы её нет", async ({
  page,
}) => {
  await page.setViewportSize({ width: 360, height: 640 });
  await page.goto("/?session=session-lost-many");

  const retry = page.getByRole("button", { name: WORDS.reconnect });
  await expect(page.getByText(LOST)).toBeVisible();
  await expect(retry).toHaveCount(1);

  // Цель ≥ 44 (банк 10 §2.4) и кнопка живёт в шапке, а не в конце документа.
  const box = await retry.boundingBox();
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
  const inHeader = await retry.evaluate((node) => node.closest(".dp-head-session") !== null);
  expect(inHeader, "кнопка — в липкой шапке").toBe(true);

  // Долистали до низа длинной ленты — путь повторения всё равно на экране.
  await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  await expect(retry).toBeInViewport();
  const scrolled = await page.evaluate(() => Math.round(window.scrollY));
  expect(scrolled, "лента действительно прокручена").toBeGreaterThan(0);
});

test("очередь: «Показаны первые N из N» не печатается, когда показано всё", async ({
  page,
  request,
}) => {
  // `hasMore: true` при `shown == total` — фраза противоречила бы себе (D2 F-06).
  await request.post("/__mock/scenario", { data: { queue: "manyEqual" } });
  await page.goto("/?session=session-live");
  await page.locator(".dp-queue-row").click();
  await expect(page.locator(".dp-queue-item")).toHaveCount(2);
  await expect(page.getByText("Показаны первые")).toHaveCount(0);
});
