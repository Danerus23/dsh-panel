import { expect, test, type Page } from "@playwright/test";

// W14 (DEF-150, слово владельца 12.11.2026): «в списке вижу, что ты работаешь, крутится шарик т.к.
// подчинённые работают, а если открыть сессию — написано „закончила“». То есть живое состояние
// работало только в списке.
//
// Корень (разбор `_team\fixes\def-150.md`): шапка считала ход ТОЛЬКО по записям ленты
// (`isRunActive`: последний `turn-end` → false), а агрегат «работает корень ИЛИ подчинённая» жил
// только в строке списка (`PhoneLive.cs`, W3). Плюс строка в сессии не обновлялась: живая подсказка
// списка была выключена на маршруте сессии.
//
// Что проверяется здесь:
//   (а) лента кончается `turn-end`, строка говорит «Работает» → шапка **«Работает»** (было «Закончила»);
//   (б) `POST /__mock/w5 {running:false}` + `POST /__mock/list-hint` → шапка **«Закончила»**
//       БЕЗ перезагрузки (подсказка списка работает и в сессии);
//   (в) строка «Закончила», а в ленте идёт ход → шапка **«Работает»** (W4 не сломан).

function headerMeta(page: Page) {
  return page.locator(".dp-head-session .dp-meta");
}

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/scenario", {
    data: {
      authed: true,
      lang: "ru",
      list: "headLive",
      pair: "ok",
      question: "none",
      queue: "ok",
      w5Running: true,
    },
  });
  await request.post("/__mock/s3-reset", { data: {} });
});

test("(а) лента кончилась, а подчинённая работает — шапка «Работает», а не «Закончила»", async ({
  page,
}) => {
  // `session-idle`: живая лента с последней записью `turn-end` (свой ход кончился), а строка списка
  // говорит «Работает» — это и есть агрегат W3 «работает корень ИЛИ подчинённая».
  await page.goto("/?session=session-idle");
  const meta = headerMeta(page);

  await expect(meta, "строка говорит «Работает» — шапка обязана повторять").toContainText(
    "Работает",
    {
      timeout: 10_000,
    },
  );
  // Значок вращения — тот же, что у своего хода: отдельного «подчинённая работает» нет.
  await expect(page.locator(".dp-head-session .dp-state-mark.dp-state-accent")).toHaveCount(1);
  // И это не «Закончила»: проверка падает, если вернуть прежнее правило (шапка смотрит только ленту).
  await expect(meta).not.toContainText("Закончила");
});

test("(б) работа кончилась — шапка гаснет САМА, без перезагрузки: подсказка списка работает в сессии", async ({
  page,
  request,
}) => {
  await page.goto("/?session=session-idle");
  const meta = headerMeta(page);
  await expect(meta, "сначала — «Работает»").toContainText("Работает", { timeout: 10_000 });

  // Строка списка больше не «работает» (подчинённая кончила) — и калитка шлёт подсказку списка.
  const switched = await request.post("/__mock/w5", { data: { running: false } });
  expect((await switched.json()).running, "стенд переключил строку").toBe(false);
  const hinted = await request.post("/__mock/list-hint", { data: { count: 1 } });
  expect((await hinted.json()).sent, "подсказка ушла в живой поток списка").toBeGreaterThan(0);

  // Шапка меняется без перезагрузки: строка перечитана по подсказке (порог 3 с, без таймера).
  await expect(meta, "работа кончилась — «Закончила»").toContainText("Закончила", {
    timeout: 8_000,
  });
  await expect(meta).not.toContainText("Работает");
});

test("(в) строка «Закончила», а в ленте идёт ход — шапка «Работает» (W4 не сломан)", async ({
  page,
  request,
}) => {
  // Другая строка списка: `session-many` — «Закончила», в ленте живой ход (сценарий `quietRoot`).
  await request.post("/__mock/scenario", { data: { list: "quietRoot" } });
  await page.goto("/?session=session-many");
  const meta = headerMeta(page);

  await expect(meta, "живой ход сильнее слова строки").toContainText("Работает", {
    timeout: 8_000,
  });
  await expect(meta).not.toContainText("Закончила");
});

test("(г) работает подчинённая, а свой ход кончился — «Остановить» НЕ появляется", async ({
  page,
}) => {
  // «Остановить» остаётся про СВОЙ ход (`runActive`): то, что строка говорит «работает»
  // (подчинённая), не даёт права гасить её с телефона — этого мы и не обещали (D3 §1.2).
  await page.goto("/?session=session-idle");
  await expect(headerMeta(page), "шапка — «Работает»").toContainText("Работает", {
    timeout: 10_000,
  });

  await page.locator(".dp-handle").click();
  const sheet = page.locator(".dp-sheet");
  await expect(sheet).toBeVisible();
  await expect(
    sheet.getByRole("button", { name: "Остановить" }),
    "чужой ход с телефона не останавливаем",
  ).toHaveCount(0);
});
