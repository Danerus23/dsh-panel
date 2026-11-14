import { expect, test } from "@playwright/test";

// Отказы страницы: без JavaScript, скрипт не загрузился, сломанный рендер, слишком много экранов.
// Ни в одном случае человек не видит белый лист.

test.describe("без JavaScript", () => {
  test.use({ javaScriptEnabled: false });

  test("слова в <noscript>, а не пустая страница", async ({ page }) => {
    await page.goto("/");
    await expect(page.getByRole("heading", { name: "Нужен JavaScript" })).toBeVisible();
    await expect(page.getByText(/Пульт работает на JavaScript/)).toBeVisible();
  });
});

test("скрипт не загрузился — остаётся подпись загрузки и путь", async ({ page }) => {
  await page.route("**/assets/*.js", (route) => route.fulfill({ status: 404, body: "" }));
  await page.goto("/");
  await expect(page.getByText("Загружаю пульт…")).toBeVisible();
  await expect(page.getByText(/Если это сообщение не исчезло/)).toBeVisible();
});

test("сломанный рендер — экран беды с «Обновить»", async ({ page }) => {
  await page.request.post("/__mock/scenario", { data: { authed: true, list: "ok" } });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Сессии" })).toBeVisible();
  await page.evaluate(() => {
    setTimeout(() => {
      throw new Error("проверка экрана ошибки");
    }, 0);
  });
  await expect(page.getByRole("heading", { name: "Не получилось" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Обновить" })).toBeVisible();
});

test("слишком много экранов — слова, а не бесконечное переподключение", async ({ page }) => {
  await page.request.post("/__mock/scenario", { data: { authed: true, list: "ok" } });
  await page.goto("/?session=session-toomany");
  await expect(page.getByText("Слишком много открытых экранов — закройте лишние.")).toBeVisible();
});
