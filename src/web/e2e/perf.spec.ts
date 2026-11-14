import { expect, test } from "@playwright/test";
import { BASE_URL } from "./base";

// Замеры страницы: свои/чужие запросы, TTFB и FCP по петле, 200 % (перекомпоновка).
// Числа — лабораторные, на петле; полевыми они не объявляются.

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/scenario", {
    data: { authed: true, lang: "ru", list: "ok", pair: "ok" },
  });
});

test("внешних запросов 0, своих — не больше 6", async ({ page }) => {
  const urls: string[] = [];
  page.on("request", (request) => urls.push(request.url()));
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Сессии" })).toBeVisible();

  const own = urls.filter((url) => url.startsWith(`${BASE_URL}/`));
  const foreign = urls.filter((url) => !url.startsWith(`${BASE_URL}/`));
  expect(foreign, `чужие адреса: ${foreign.join(", ")}`).toEqual([]);
  expect(own.length).toBeGreaterThanOrEqual(3);
  // 5 → 6 (W5/G1): список держит ОДНУ SSE-связь `GET /api/phone/list/live` — это новый повод
  // перечитывания (слово владельца 11.11.2026), а не лишний запрос. Было 5: документ, css, js,
  // вопрос и список. Связь одна и висит, а не опрашивает: число поводов не растёт.
  expect(own.length).toBeLessThanOrEqual(6);
});

test("TTFB ≤ 100 мс и FCP ≤ 1 с по петле", async ({ page }) => {
  await page.goto("/", { waitUntil: "load" });
  await expect(page.getByRole("heading", { name: "Сессии" })).toBeVisible();
  // ЗАПИСЬ PAINT БРАУЗЕР ОТДАЁТ НА КАДР ПОЗЖЕ, а теперь на списке висит SSE-связь: если прочитать
  // сразу, записи ещё нет (Infinity), хотя FCP на самом деле ≤ 200 мс. Ждём запись (≤2 с), порог
  // не снижаем.
  await expect
    .poll(async () => page.evaluate(() => performance.getEntriesByType("paint").length), {
      timeout: 2_000,
    })
    .toBeGreaterThan(0);

  const numbers = await page.evaluate(() => {
    const navigation = performance.getEntriesByType("navigation")[0] as PerformanceNavigationTiming;
    const paint = performance.getEntriesByName("first-contentful-paint")[0];
    return {
      ttfb: navigation.responseStart - navigation.requestStart,
      fcp: paint?.startTime ?? Number.POSITIVE_INFINITY,
    };
  });
  test.info().annotations.push({ type: "ttfb_ms", description: String(Math.round(numbers.ttfb)) });
  test.info().annotations.push({ type: "fcp_ms", description: String(Math.round(numbers.fcp)) });
  expect(numbers.ttfb).toBeLessThanOrEqual(100);
  expect(numbers.fcp).toBeLessThanOrEqual(1000);
});

test("200 %: текст не теряется и не уводит вбок", async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 800 });
  await page.goto("/");
  await expect(page.getByRole("link", { name: /Починить калитку/ })).toBeVisible();
  // Прокси браузерного масштаба 200 %: `zoom` пересчитывает раскладку так же, как масштаб
  // страницы, и показывает, что текст переносится, а не режется.
  await page.evaluate(() => {
    document.documentElement.style.zoom = "2";
  });
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow).toBeLessThanOrEqual(1);
  await expect(page.getByRole("link", { name: /Починить калитку/ })).toBeVisible();
  await page.evaluate(() => {
    document.documentElement.style.zoom = "";
  });
});
