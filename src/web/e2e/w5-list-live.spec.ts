import { expect, test, type APIRequestContext } from "@playwright/test";

// W5 (G1 + D3 §4): живой список на телефоне.
//
//   * пока список видим, клиент держит СВОЙ поток `GET /api/phone/list/live`; кадр `stale` —
//     повод перечитать список, но не чаще LIST_STALE_MIN_MS = 3 с (два кадра в одном окне дают
//     ОДНО чтение), а без кадров список не читается вовсе (таймера нет);
//   * уход со списка (скрытие вкладки или переход в сессию) закрывает поток СПИСКА; с W14
//     (DEF-150) сессия держит СВОЙ поток того же вида — шапке нужна свежая строка («работает
//     подчинённая»). Поэтому после перехода открыт ровно ОДИН поток, а не ноль.
//   * обрыв потока — тихий повтор по SSE_BACKOFF_MS (1 → 2 → 4 …), человеку ни слова: список
//     остаётся на экране, слов об обрыве нет.

async function stats(request: APIRequestContext) {
  const response = await request.get("/__mock/w5");
  return (await response.json()) as {
    listCalls: number;
    listLiveOpens: number;
    listLiveCloses: number;
    listLiveOpen: number;
  };
}

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/scenario", { data: { list: "w5" } });
});

test("строка списка меняет слово без возврата и перезагрузки", async ({ page, request }) => {
  await page.goto("/");

  const row = page.getByRole("link", { name: /Живой список/u });
  await expect(row).toContainText("Работает");
  await expect.poll(async () => (await stats(request)).listLiveOpen).toBe(1);
  expect((await stats(request)).listCalls, "первое чтение — одно").toBe(1);

  // Список на калитке стал другим; подсказка приходит потоком — БЕЗ возврата на вкладку.
  await request.post("/__mock/w5", { data: { running: false } });
  await request.post("/__mock/list-hint");

  await expect(row, "слово сменилось по кадру stale").toContainText("Закончила", {
    timeout: 5_000,
  });
  // Перезагрузки не было: адрес тот же, а чтений ровно два — первое и одно по подсказке.
  await expect(page).toHaveURL(/\/$/u);
  expect((await stats(request)).listCalls).toBe(2);
});

test("таймера нет, а два кадра за 500 мс дают одно перечитывание", async ({ page, request }) => {
  await page.goto("/");
  await expect(page.getByRole("link", { name: /Живой список/u })).toBeVisible();

  // Без кадров поток молчит — и список НЕ читается: старого «раз в 10 с» нет.
  await page.waitForTimeout(4_000);
  expect((await stats(request)).listCalls, "без кадров — ни одного лишнего чтения").toBe(1);

  await request.post("/__mock/list-hint");
  await page.waitForTimeout(500);
  await request.post("/__mock/list-hint");

  await expect.poll(async () => (await stats(request)).listCalls, { timeout: 5_000 }).toBe(2);
  await page.waitForTimeout(1_000);
  expect((await stats(request)).listCalls, "второй кадр в том же окне — мимо").toBe(2);
});

test("уход со списка закрывает поток подсказки", async ({ page, request }) => {
  await page.goto("/");
  const row = page.getByRole("link", { name: /Живой список/u });
  await expect(row).toBeVisible();
  await expect.poll(async () => (await stats(request)).listLiveOpen, { timeout: 3_000 }).toBe(1);

  await row.click();
  await expect(page).toHaveURL(/session=session-w5/u);
  // DEF-150: поток СПИСКА закрыт (это и проверяем), а сессия открыла СВОЙ — на экране ровно один.
  await expect
    .poll(async () => (await stats(request)).listLiveCloses, { timeout: 5_000 })
    .toBeGreaterThanOrEqual(1);
  await expect.poll(async () => (await stats(request)).listLiveOpen, { timeout: 5_000 }).toBe(1);
});

test("скрытие вкладки закрывает поток, возврат — открывает снова", async ({ page, request }) => {
  await page.goto("/");
  await expect(page.getByRole("link", { name: /Живой список/u })).toBeVisible();
  await expect.poll(async () => (await stats(request)).listLiveOpen, { timeout: 3_000 }).toBe(1);

  await page.evaluate(() => {
    Object.defineProperty(document, "hidden", { configurable: true, get: () => true });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await expect.poll(async () => (await stats(request)).listLiveOpen, { timeout: 3_000 }).toBe(0);

  await page.evaluate(() => {
    Object.defineProperty(document, "hidden", { configurable: true, get: () => false });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await expect.poll(async () => (await stats(request)).listLiveOpen, { timeout: 3_000 }).toBe(1);
});

test("обрыв потока — тихий повтор, человеку ни слова", async ({ page, request }) => {
  await request.post("/__mock/scenario", { data: { listLive: "error" } });
  await page.goto("/");

  const row = page.getByRole("link", { name: /Живой список/u });
  await expect(row).toBeVisible();

  // Первый заход стенд рвёт кадром `error` — LiveStream повторяет по SSE_BACKOFF_MS (1 с).
  await expect
    .poll(async () => (await stats(request)).listLiveOpens, { timeout: 6_000 })
    .toBeGreaterThan(1);

  // Слов об обрыве нет: список не «ругается», он просто показывает последнее прочитанное.
  await expect(page.getByText(/не удалось|не прочитался/u)).toHaveCount(0);
  await expect(row).toBeVisible();
});
