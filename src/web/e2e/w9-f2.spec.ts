import { readFileSync } from "node:fs";
import { expect, test, type Page } from "@playwright/test";

// W9 (находки L1 F2/F3, 12.11.2026).
// F2: «назад» на «Новой задаче» — тот же орган 44×44, что в шапке сессии. `.dp-head` — flex-колонка
//     со `stretch`, и ссылка без своей ширины растягивалась на всю ширину (L1: 328×44 при 360 и
//     288×44 при 320). Проверка мерит bbox, а не «на глаз».
// F3: `POST /__mock/scenario` отвечал 400, хотя патч применял: в сбросе стояло `w5Running = true;`
//     без объявления — в strict-модуле это ReferenceError ПОСЛЕ применения патча (отсюда 400 при
//     живом патче). Проверка: 200 + патч виден в ответе двери; плюс статическая проверка, что
//     сброс не присваивает необъявленные имена.

type Box = { width: number; height: number; x: number; y: number };

async function box(page: Page, selector: string): Promise<Box> {
  const rect = await page.locator(selector).first().boundingBox();
  if (rect === null) throw new Error(`нет bbox у ${selector}`);
  return {
    width: Math.round(rect.width),
    height: Math.round(rect.height),
    x: Math.round(rect.x),
    y: Math.round(rect.y),
  };
}

test("«Новая задача»: назад — ровно 44×44 на 360 и 320, имя и переход на список", async ({
  page,
}) => {
  await page.goto("/?new=1");
  const back = page.locator(".dp-head-back");
  await expect(back).toBeVisible();

  for (const width of [360, 320]) {
    await page.setViewportSize({ width, height: 692 });
    const b = await box(page, ".dp-head-back");
    expect(b, `назад на «Новой задаче» при ${width}`).toMatchObject({ width: 44, height: 44 });
    // Своего видимого слова у органа нет — имя несёт только aria-label (D1 §8).
    expect((await back.innerText()).trim(), "видимого текста нет").toBe("");
    await expect(back).toHaveAttribute("aria-label", "К списку сессий");
  }

  // Переход — на список (адрес «/», обычное нажатие без перезагрузки).
  await page.setViewportSize({ width: 360, height: 692 });
  await back.click();
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Сессии");
});

test("сессия: «назад» 44×44 на 360 и 320, шапка на месте", async ({ page }) => {
  // ДЛИННАЯ лента: проверке нужна настоящая прокрутка (липкая шапка липнет только в прокрутке).
  await page.goto("/?session=session-long");
  const head = page.locator(".dp-head-session");
  await expect(head).toBeVisible();
  await expect(page.locator(".dp-feed li").last()).toBeVisible();
  await expect(page.locator(".dp-head-back")).toBeVisible();
  for (const width of [360, 320]) {
    await page.setViewportSize({ width, height: 692 });
    const b = await box(page, ".dp-head-back");
    expect(b, `назад в сессии при ${width}`).toMatchObject({ width: 44, height: 44 });
    const header = await box(page, ".dp-head-session");
    // До прокрутки шапка стоит в потоке (верх страницы = отступ 16), после прокрутки — липнет к 0.
    expect(header.y, "шапка на своём месте").toBeLessThanOrEqual(17);
    expect(header.height).toBeGreaterThanOrEqual(44);
    await page.evaluate(() => window.scrollTo(0, 200));
    await expect
      .poll(async () => (await box(page, ".dp-head-session")).y, {
        message: "шапка липнет к верху",
      })
      .toBeLessThanOrEqual(1);
  }
});

test("стенд: POST /__mock/scenario — 200 и патч применён", async ({ request }) => {
  const response = await request.post("/__mock/scenario", { data: { list: "empty" } });
  expect(response.status(), "сценарий отвечает 200, а не 400").toBe(200);
  expect(await response.json()).toEqual({ ok: true });

  // Патч виден двери: список пуст (list: "empty").
  const list = await request.get("/api/phone/sessions");
  expect(list.status()).toBe(200);
  const body = (await list.json()) as { items: unknown[]; total: number };
  expect(body.items, "патч list: empty применён").toEqual([]);
  expect(body.total).toBe(0);
});

test("стенд: сброс сценария не присваивает необъявленные имена", () => {
  const source = readFileSync(new URL("./server.mjs", import.meta.url), "utf8");
  const start = source.indexOf("scenario = { ...scenario, ...patch };");
  expect(start, "нашёлся сброс сценария").toBeGreaterThan(0);
  const block = source.slice(start, source.indexOf("json(res, 200, { ok: true });", start));
  // Присваивания вида `listCalls = 0;` (не свойства `scenario.x = ...`) — имена сброса.
  const names = [...block.matchAll(/^\s{6,}([A-Za-z_$][\w$]*)\s*=/gm)].map((m) => m[1]);
  expect(names.length, "сброс что-то сбрасывает").toBeGreaterThan(3);
  const undeclared = names.filter(
    (name) => !new RegExp(`\\b(?:let|const|var)\\s+${name}\\b`).test(source),
  );
  // w5Running — свойство сценария (`scenario.w5Running`), а не отдельная переменная: отдельного
  // имени в модуле нет, и присваивание ему падало ПОСЛЕ применения патча (418-строка L1 F3).
  expect(undeclared, `необъявленные имена сброса: ${undeclared.join(", ")}`).toEqual([]);
});
