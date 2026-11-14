import { expect, test, type Page } from "@playwright/test";

// Верх ленты и очередь после приёма (D1 §4–§5, приёмка 11.11.2026).
//
//   * верх ДОСТИЖИМ: с живого конца прокрутка вверх доходит до карточки вопроса и очереди, они
//     видны целиком и остаются на месте — догруз старых больше не считается от верха документа;
//   * догруз считается ОТ ВЕРХА ЛЕНТЫ: одна страница за подход, якорь держит видимую строку;
//   * страница короче окна: старые добираются сами, кадр за кадром;
//   * после «Задача принята» показана «Очередь» (по её ответу), живой конец достижим «к последнему».

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/scenario", {
    data: {
      authed: true,
      lang: "ru",
      list: "ok",
      pair: "ok",
      question: "none",
      queue: "ok",
      queueFrame: "none",
      // Догруз — своё у каждого теста: сценарий стенда СЛИВАЕТСЯ с прежним, иначе `older: "pages"`
      // протёк бы в следующие файлы (лента стоит того же стенда).
      older: "ok",
    },
  });
  // Счётчик старших страниц — тоже состояние стенда: страниц всего две, их «разбирают» тесты
  // догруза. Сброс делает каждый файл независимым от порядка.
  await request.post("/__mock/s3-reset", { data: {} });
});

async function mockState(page: Page): Promise<{ pageCalls: number }> {
  const response = await page.request.get("/__mock/s3");
  return (await response.json()) as { pageCalls: number };
}

async function scrollToFeedTop(page: Page): Promise<number> {
  // Прокрутка и замер — ОДНИМ проходом: вставка старой страницы может приехать через кадр, и
  // замер «после» вернул бы уже НОВУЮ первую строку (якорь тогда не проверить).
  return page.evaluate(() => {
    const feed = document.querySelector(".dp-feed") as HTMLElement;
    const top = feed.getBoundingClientRect().top + window.scrollY;
    window.scrollTo(0, top - 40);
    return Math.round(
      (document.querySelector(".dp-feed li") as HTMLElement).getBoundingClientRect().top,
    );
  });
}

async function firstFeedRowTop(page: Page): Promise<number> {
  return page.evaluate(() =>
    Math.round((document.querySelector(".dp-feed li") as HTMLElement).getBoundingClientRect().top),
  );
}

test("верх достижим: вопрос и очередь на месте, страница вниз сама не уезжает", async ({
  page,
  request,
}) => {
  await request.post("/__mock/scenario", {
    data: {
      authed: true,
      lang: "ru",
      list: "ok",
      pair: "ok",
      question: "single",
      questionSession: "session-long",
      queue: "ok",
    },
  });
  // ДЛИННАЯ лента: очередь ушла из потока в нижний стек (W12), и короткая страница перестала
  // прокручиваться — а проверка про то, что верх ДОСТИЖИМ и читается без догруза.
  await page.setViewportSize({ width: 360, height: 900 });
  await page.goto("/?session=session-long");
  await expect(page.locator(".dp-feed li").last()).toBeVisible();
  await expect(page.locator(".dp-question").first()).toContainText(
    "Что делаем с упавшими тестами?",
  );
  // Очередь больше НЕ блок над лентой (D11 §3.3): она внизу, строкой «В очереди: N».
  await expect(page.locator(".dp-queue-row")).toBeVisible();

  const room = await page.evaluate(
    () => document.documentElement.scrollHeight - window.innerHeight,
  );
  expect(room, "страница заведомо длиннее окна — есть куда листать").toBeGreaterThan(100);

  // Правило короткой страницы (D1 §4.2) — СВОЁ и законное: пока вопрос ещё не пришёл опросом,
  // страница могла быть короче окна, и стенд мог сам догрузить страницу. К верху это отношения
  // не имеет: важно, что ЧТЕНИЕ ВЕРХА не добавляет НИ ОДНОЙ страницы (иначе верх — движущаяся цель).
  await page.waitForTimeout(300);
  const before = (await mockState(page)).pageCalls;

  // С живого конца — вверх до самого верха: карточка вопроса и очередь обязаны быть видны ЦЕЛИКОМ.
  await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  await page.waitForTimeout(150);
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.waitForTimeout(700);

  const atTop = await page.evaluate(() => {
    const head = document.querySelector(".dp-head-session") as HTMLElement;
    const header = head === null ? null : head.getBoundingClientRect();
    const card = (document.querySelector(".dp-question") as HTMLElement).getBoundingClientRect();
    const row = (document.querySelector(".dp-queue-row") as HTMLElement).getBoundingClientRect();
    const stack = (document.querySelector(".dp-bottom") as HTMLElement).getBoundingClientRect();
    return {
      y: Math.round(window.scrollY),
      headBottom: header === null ? 0 : Math.round(header.bottom),
      cardTop: Math.round(card.top),
      cardBottom: Math.round(card.bottom),
      rowTop: Math.round(row.top),
      rowBottom: Math.round(row.bottom),
      stackTop: Math.round(stack.top),
      inFlow: document.querySelectorAll(".dp-page .dp-queue").length,
      viewport: window.innerHeight,
    };
  });

  expect(atTop.y, "прокрутка вверх дошла до scrollY = 0").toBe(0);
  expect(atTop.cardTop, "карточка вопроса видна целиком (сверху)").toBeGreaterThanOrEqual(
    atTop.headBottom,
  );
  expect(atTop.cardBottom, "карточка вопроса видна целиком (снизу)").toBeLessThanOrEqual(
    atTop.viewport,
  );
  // Очередь — в НИЖНЕМ СТЕКЕ (D11 §1.1): в потоке её нет ни на пиксель, а на экране она есть
  // и стоит НИЖЕ карточки вопроса — там, куда смотрит большой палец, а не «где-то наверху».
  expect(atTop.inFlow, "очереди в потоке страницы нет").toBe(0);
  expect(atTop.rowTop, "строка очереди в кадре").toBeLessThan(atTop.viewport);
  expect(atTop.rowBottom, "и целиком").toBeLessThanOrEqual(atTop.viewport);
  expect(atTop.rowTop, "очередь стоит НИЖЕ карточки вопроса").toBeGreaterThan(atTop.cardBottom);
  expect(atTop.stackTop, "стек у нижней кромки — растёт вверх").toBeLessThan(atTop.viewport);
  expect((await mockState(page)).pageCalls, "чтение верха — без единой новой страницы").toBe(
    before,
  );

  // Повторный подход к верху не отбрасывает страницу вниз (старый якорь по высоте документа делал это).
  await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  await page.waitForTimeout(150);
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.waitForTimeout(700);
  expect(
    await page.evaluate(() => Math.round(window.scrollY)),
    "повторный подход — снова верх",
  ).toBe(0);
  expect((await mockState(page)).pageCalls, "и повторный подход — без догруза").toBe(before);
});

test("догруз от верха ЛЕНТЫ: одна страница за подход, якорь держит видимую строку", async ({
  page,
  request,
}) => {
  // Карточка вопроса держит страницу заметно длиннее окна 400: без неё (и без очереди в потоке)
  // верх ленты и низ почти неразличимы — а проверка ровно про «верх ленты vs живoй конец».
  await request.post("/__mock/scenario", {
    data: { older: "pages", question: "single", questionSession: "session-many" },
  });
  // Окно 360: карточка вопроса + лента дают запас прокрутки больше порога 120 — верх ленты
  // и живой конец различимы, и взвод «подхода» срабатывает.
  await page.setViewportSize({ width: 360, height: 360 });
  await page.goto("/?session=session-many");
  await expect(page.locator(".dp-feed li").last()).toBeVisible();
  await expect(page.locator(".dp-question").first(), "карточка вопроса в потоке").toBeVisible();
  // Правило «страница короче окна» (D1 §4.2) — СВОЁ и законное: на короткой странице стенд может
  // догрузить старые САМ, ещё до нашего замера (очередь теперь в нижнем стеке, и страница стала короче).
  // Поэтому считаем подходы ОТ УСТОЯВШЕГОСЯ числа: «чтение верха не добавляет страниц».
  await page.waitForTimeout(400);
  const base = (await mockState(page)).pageCalls;
  const rowsBefore = await page.locator(".dp-feed li").count();
  // Взвод: уйти ОТ верха ленты так, чтобы первая строка стояла ниже порога 120 (после самодогруза
  // короткой страницы взвод уже израсходован). Прокрутка — НЕ в самый низ: на длинной странице
  // верх ленты уезжает за кромку, и порог не достигается.
  await page.evaluate(() => {
    const feed = document.querySelector(".dp-feed") as HTMLElement;
    window.scrollTo(0, Math.max(0, feed.getBoundingClientRect().top + window.scrollY - 200));
  });
  await expect
    .poll(async () => firstFeedRowTop(page), { message: "первая строка ниже порога — взвод готов" })
    .toBeGreaterThan(120);
  // Подход: ставим ПЕРВУЮ СТРОКУ ленты к верхнему краю и ЗАПОМИНАЕМ, какая это строка — якорь
  // проверяется по НЕЙ, а не по «известному тексту»: до подхода страница могла уже быть догружена
  // сама (правило короткой страницы).
  const approach = await page.evaluate(() => {
    const feed = document.querySelector(".dp-feed") as HTMLElement;
    const first = feed.firstElementChild as HTMLElement;
    window.scrollTo(0, feed.getBoundingClientRect().top + window.scrollY - 40);
    return {
      top: Math.round(first.getBoundingClientRect().top),
      text: (first.textContent ?? "").trim(),
    };
  });
  const beforeTop = approach.top;
  expect(beforeTop, "первая запись ленты у верхнего края").toBeLessThanOrEqual(120);
  await expect
    .poll(async () => page.locator(".dp-feed li").count(), {
      message: "подход к верху догрузил старые",
    })
    .toBeGreaterThan(rowsBefore);
  expect((await mockState(page)).pageCalls, "одна страница за подход").toBe(base + 1);
  // Якорь: строка, которая была у верхнего края, осталась на месте (±1 px, D1 §4.2).
  const anchored = await page
    .locator(".dp-feed li")
    .filter({ hasText: approach.text })
    .first()
    .evaluate((node) => Math.round(node.getBoundingClientRect().top));
  expect(Math.abs(anchored - beforeTop), "видимая строка не сдвинулась").toBeLessThanOrEqual(1);

  // Подход кончился: вторая страница сама не приезжает (иначе верх снова стал бы движущейся целью).
  await page.waitForTimeout(800);
  expect((await mockState(page)).pageCalls, "вторая страница без нового подхода не грузится").toBe(
    base + 1,
  );
  // Вставка сверху — это НОВАЯ первая строка (старая страница) ВЫШЕ якоря. Число пикселей тут не
  // годится: при запасе снизу 52 (D11 §1.2) документ на 36 px короче прежнего, и край окна
  // оставляет новую строку ровно на кромке (2 px) — «выше окна» стало бы зависеть от 2 px вёрстки.
  const inserted = await page.evaluate((anchorText) => {
    const rows = [...document.querySelectorAll(".dp-feed li")] as HTMLElement[];
    const anchor = rows.find((row) => (row.textContent ?? "").trim() === anchorText);
    return {
      count: rows.length,
      firstText: (rows[0]!.textContent ?? "").trim().slice(0, 80),
      firstTop: Math.round(rows[0]!.getBoundingClientRect().top),
      anchorTop: anchor === undefined ? null : Math.round(anchor.getBoundingClientRect().top),
    };
  }, approach.text);
  expect(inserted.count, "строк стало больше — страница вставилась сверху").toBeGreaterThan(5);
  // Новая первая строка — старáя (из догруженной страницы) и НЕ та, что была на кромке.
  expect(inserted.firstText, "первая строка — старáя, из догруженной страницы").toContain("Стар");
  expect(inserted.firstText, "и это не прежняя первая строка").not.toBe(approach.text.slice(0, 80));
  expect(inserted.anchorTop, "якорь на месте").not.toBeNull();
  expect(
    inserted.firstTop,
    "новая первая строка стоит ВЫШЕ якоря — это и есть вставка сверху",
  ).toBeLessThan(inserted.anchorTop!);

  // НОВЫЙ подход — это снова уход от верха и возврат; страницу он добавит только если старшие ЕЩЁ ЕСТЬ
  // (`hasMore` на стенде кончается после двух страниц, а первую могла забрать самодогрузка короткой
  // страницы). Поэтому проверяем НЕ число страниц, а правило: за один подход — не больше одной.
  const callsAfterFirst = (await mockState(page)).pageCalls;
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.waitForTimeout(200);
  expect(
    await firstFeedRowTop(page),
    "первая запись ушла ниже порога — взвод восстановлен",
  ).toBeGreaterThan(120);
  await scrollToFeedTop(page);
  await page.waitForTimeout(800);
  const callsAfterSecond = (await mockState(page)).pageCalls;
  expect(callsAfterSecond, "новый подход добавляет не больше одной страницы").toBeLessThanOrEqual(
    callsAfterFirst + 1,
  );
});

test("страница короче окна: старые добираются сами, кадр за кадром", async ({ page, request }) => {
  await request.post("/__mock/scenario", { data: { older: "pages" } });
  await page.setViewportSize({ width: 360, height: 600 });
  await page.goto("/?session=session-short");
  await expect(page.getByText("Короткая лента: одна запись.")).toBeVisible();

  // Прокрутки нет — «дойти до верха ленты» нельзя: правило по высоте документа добирает само.
  await expect(page.getByText("Старое сообщение человека, страница 1.")).toBeVisible();
  await expect(page.getByText("Старое сообщение агента, страница 2.")).toBeVisible();
  expect((await mockState(page)).pageCalls, "две страницы, пока не кончились старые").toBe(2);
  // Человек ничего не прокручивал: старые приехали сами, по высоте документа (D1 §4.2).
});

test("после «Задача принята» очередь показана, живой конец достижим «к последнему»", async ({
  page,
}) => {
  // ДЛИННАЯ лента: очередь теперь в НИЖНЕМ СТЕКЕ (W12), страница стала короче — на короткой
  // сессии прокрутки нет вовсе, и «к последнему» нечего догонять.
  await page.goto("/?session=session-long");
  await expect(page.locator(".dp-feed li").last()).toBeVisible();
  // Счёт ДО приёма: на этой сессии очередь не пустая, поэтому сверяем РОСТ числа, а не константу.
  const queueCount = async () =>
    Number(((await page.locator(".dp-queue-row").textContent()) ?? "").match(/\d+/u)?.[0] ?? 0);
  const countBefore = await queueCount();
  await page.locator(".dp-handle").click();
  await expect(page.locator(".dp-sheet")).toBeVisible();
  await page.locator(".dp-sheet textarea").fill("Проверь очередь");
  await page.locator(".dp-sheet").getByRole("button", { name: "Отправить задачу" }).click();

  await expect(page.locator(".dp-handle-note"), "успех — строкой над зацепом").toContainText(
    "Задача принята.",
  );
  await expect(page.locator(".dp-sheet"), "лист закрылся сам").toHaveCount(0);

  // Слово владельца 13.11.2026: по приёму очередь НЕ раскрывается — строка «В очереди: N» стоит
  // свёрнутой над зацепом, и счёт вырос; панель человек откроет сам.
  const queueRow = page.locator(".dp-queue-row");
  await expect
    .poll(queueCount, { message: "счёт вырос — принятая задача в очереди" })
    .toBeGreaterThan(countBefore);
  await expect(queueRow).toHaveAttribute("aria-expanded", "false");
  expect(await page.locator(".dp-queue-panel").count(), "панели в DOM нет").toBe(0);
  // Принятая задача и правда в очереди — её видно, когда человек раскроет строку сам.
  await queueRow.click();
  await expect(
    page.locator(".dp-queue-item").filter({ hasText: "Проверь очередь" }).first(),
    "очередь перечитана после приёма",
  ).toBeVisible();
  await queueRow.click();

  // Прыжка нет — человек ОСТАЛСЯ у живого конца: лента видна над стеком, орган «к последнему» не нужен
  // (он появляется только когда лента уехала), и страница не отброшена. Путь к живому концу — сама лента.
  const feed = await page.evaluate(() => {
    const rows = document.querySelectorAll(".dp-feed li");
    const last = rows[rows.length - 1] as HTMLElement;
    const stack = (document.querySelector(".dp-bottom") as HTMLElement).getBoundingClientRect();
    const doc = document.documentElement;
    return {
      lastBottom: Math.round(last.getBoundingClientRect().bottom),
      lastTop: Math.round(last.getBoundingClientRect().top),
      stackTop: Math.round(stack.top),
      rest: Math.round(doc.scrollHeight - window.scrollY - window.innerHeight),
    };
  });
  expect(feed.lastBottom, "живой конец не ушёл под стек").toBeLessThanOrEqual(feed.stackTop + 1);
  expect(feed.lastTop, "и остался в кадре — прыжка не было").toBeGreaterThan(0);
  expect(feed.rest, "человек у живого конца — «к последнему» не нужен").toBeLessThanOrEqual(152);
});
