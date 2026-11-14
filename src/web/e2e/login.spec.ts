import { expect, test } from "@playwright/test";

// Вход по шести цифрам: кнопка не активна без шести цифр, отказ НЕ стирает набранное,
// фокус возвращается к полю, повтор с верным кодом ведёт в список.

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/scenario", {
    data: { authed: true, lang: "ru", list: "ok", pair: "ok" },
  });
});

test("шесть цифр: отказ сохраняет введённое, верный код открывает список", async ({
  page,
  request,
}) => {
  await request.post("/__mock/scenario", { data: { authed: false, pair: "bad" } });
  await page.goto("/");

  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Телефон панели DSH");
  const field = page.getByLabel("Код из шести цифр");
  const submit = page.getByRole("button", { name: "Войти" });
  await expect(submit).toBeDisabled();

  await field.fill("12345");
  await expect(submit).toBeDisabled();

  await field.fill("123456");
  await expect(submit).toBeEnabled();
  await submit.click();

  const alert = page.getByRole("alert");
  await expect(alert).toContainText("Код не подошёл");
  await expect(field).toHaveValue("123456");
  await expect(field).toBeFocused();

  await request.post("/__mock/scenario", { data: { pair: "ok" } });
  await submit.click();
  await expect(page.getByRole("heading", { name: "Сессии" })).toBeVisible();
});
