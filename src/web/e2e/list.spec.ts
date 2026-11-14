import { expect, test } from "@playwright/test";
import { BASE_URL } from "./base";

// Список сессий: имена, состояния СЛОВАМИ, счёт, «показаны последние N из M», пусто, ошибка,
// 320 px без горизонтальной прокрутки, тёмная тема по системе, скрытые blank-сессии (D1 §6).
test.beforeEach(async ({ request }) => {
  await request.post("/__mock/scenario", {
    data: { authed: true, lang: "ru", list: "ok", pair: "ok" },
  });
});

test("строки: название, состояние словом, «в очереди», счёт", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Сессии" })).toBeVisible();
  await expect(page.getByText("Сессий: 4")).toBeVisible();

  const live = page.getByRole("link", { name: /Починить калитку/ });
  await expect(live).toContainText("Работает");
  await expect(page.getByRole("link", { name: /Собрать список сессий/ })).toContainText(
    "Ждёт ответа",
  );
  // D1 §6 (11.11.2026): blank-сессии телефон НЕ показывает, как DSH, — в счёт они не входят.
  await expect(page.getByRole("link")).toHaveCount(4);
  await expect(page.getByRole("link", { name: /Без названия/ })).toHaveCount(0);
  await expect(page.getByText("Ещё не было задачи")).toHaveCount(0);
  const done = page.getByRole("link", { name: /Обновление панели/ });
  await expect(done).toContainText("Закончила");
  // Счётчик задач — ЧИСЛО (карточка 32a 01a), фраза осталась для чтения с экрана.
  await expect(done.locator(".dp-badge")).toHaveText("2");
  await expect(done.locator(".dp-visually-hidden")).toHaveText("В очереди: 2");
  // Признак нажимаемости ДО касания: стрелка перехода (А-1) — и она не шумит в читалке.
  await expect(done.locator(".dp-row-chevron")).toHaveCount(1);
  await expect(done.locator(".dp-row-chevron")).toHaveAttribute("aria-hidden", "true");
  // Состояние: значок и СЛОВО вместе (О-3), цвет — второй носитель.
  await expect(done.locator(".dp-state-mark.dp-state-good")).toHaveCount(1);
  await expect(live.locator(".dp-state-mark.dp-state-accent")).toHaveCount(1);
  await expect(
    page
      .getByRole("link", { name: /Собрать список сессий/ })
      .locator(".dp-state-mark.dp-state-warn"),
  ).toHaveCount(1);
  // Строки «Вы вошли как телефон» на странице больше нет (карточка 32a §4 п. 1).
  await expect(page.locator(".dp-device")).toHaveCount(0);
});

test("blank с висящим вопросом остаётся и говорит «Ждёт ответа»", async ({ page, request }) => {
  await request.post("/__mock/scenario", { data: { list: "blankQuestion" } });
  await page.goto("/");
  await expect(page.getByText("Сессий: 3")).toBeVisible();
  const blank = page.getByRole("link", { name: /Без названия/ });
  await expect(blank, "вопрос — не пустота: строка остаётся").toHaveCount(1);
  await expect(blank).toContainText("Ждёт ответа");
  await expect(page.getByText("Ещё не было задачи")).toHaveCount(0);
});

test("все строки пустые: пустое состояние, а не пустой список", async ({ page, request }) => {
  await request.post("/__mock/scenario", { data: { list: "allBlank" } });
  await page.goto("/");
  await expect(page.getByText("Сессий: 0")).toBeVisible();
  await expect(page.getByText("Сессий нет")).toBeVisible();
  await expect(page.getByText(/Как только на компьютере появится сессия/)).toBeVisible();
  await expect(page.getByRole("link")).toHaveCount(0);
});

test("лимит: «Показаны последние 50 из 60»", async ({ page, request }) => {
  await request.post("/__mock/scenario", { data: { list: "many" } });
  await page.goto("/");
  await expect(page.getByText("Показаны последние 50 из 60")).toBeVisible();
  await expect(page.getByRole("link")).toHaveCount(50);
});

test("пусто: «Сессий нет» и объяснение, а не пустой экран", async ({ page, request }) => {
  await request.post("/__mock/scenario", { data: { list: "empty" } });
  await page.goto("/");
  await expect(page.getByText("Сессий нет")).toBeVisible();
  await expect(page.getByText(/Как только на компьютере появится сессия/)).toBeVisible();
});

test("ошибка списка: слова и «Повторить», а не «сессий нет» и не «Обновить»", async ({
  page,
  request,
}) => {
  await request.post("/__mock/scenario", { data: { list: "error" } });
  await page.goto("/");
  const alert = page.getByRole("alert");
  await expect(alert).toContainText("Список сессий не прочитался");
  // S4a: в успехе кнопки нет вовсе, в отказе путь повторения остаётся — и он называется словом.
  await expect(alert.getByRole("button", { name: "Повторить" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Обновить" })).toHaveCount(0);
  await expect(page.getByText("Сессий нет")).toHaveCount(0);
});

test("320 px: колонка не уводит вбок", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 800 });
  await page.goto("/");
  await expect(page.getByRole("link", { name: /Починить калитку/ })).toBeVisible();
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow).toBeLessThanOrEqual(1);
});

test("тёмная тема по системе, а не кнопкой", async ({ browser }) => {
  const context = await browser.newContext({
    colorScheme: "dark",
    viewport: { width: 360, height: 800 },
    baseURL: BASE_URL,
  });
  const page = await context.newPage();
  await page.goto("/");
  await expect(page.locator("html")).toHaveAttribute("data-dp-theme", "dark");
  await expect(page.locator("body")).toHaveAttribute("theme-mode", "dark");
  const background = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
  expect(background).toBe("rgb(22, 22, 26)");
  await context.close();
});
