import { expect, test, type Page } from "@playwright/test";

// ХВОСТ S4a: орган «к последнему сообщению» (P2 §3) и заголовок экрана беды (F-07, P2 §4).
//
// Что доказывается прогоном (P2 §6, П-1…П-8):
//   * орган виден ТОЛЬКО когда лента не внизу, и порог — тот же, что у автодописывания;
//   * 44×44, справа над зацепом, зацеп и текст не перекрыты, слои не перевёрнуты;
//   * счёт — только при > 0 и только для сообщений, приехавших пока человек был выше;
//   * нажатие возвращает живой конец, автодописывание снова работает, фокус уходит в заголовок;
//   * имя — слово словаря (три языка), видимого слова на кнопке нет, цифра скрыта от читалки;
//   * F-07: h1 называет беду, а «Без названия» остаётся только открытой безымянной сессии.
//
// Стенд: `session-long` — длинная лента без догруза (иначе прокрутка вверх сама подгружала бы старые),
// `POST /__mock/push` — «новые сообщения, пока человек листал выше» по команде проверки, а не по таймеру.

const TO_LAST = "К последнему сообщению";
const TO_LAST_NEW = "К последнему сообщению, новых: ";
/** Допуск автоскролла из `config.ts` (`autoscrollSlackPx` = 52 + 100) — им же меряется «не внизу». */
const SLACK = 152;

function organ(page: Page) {
  return page.locator(".dp-tolast");
}

function statusLine(page: Page) {
  return page.locator('.dp-visually-hidden[role="status"]');
}

/** Остаток до низа страницы — то же число, по которому решает экран. */
async function restToBottom(page: Page): Promise<number> {
  return page.evaluate(() =>
    Math.round(document.documentElement.scrollHeight - window.scrollY - window.innerHeight),
  );
}

/** Открыть длинную ленту и дождаться, что экран САМ встал на живой конец. */
async function openLong(page: Page): Promise<void> {
  await page.goto("/?session=session-long");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Длинная лента");
  await expect(page.locator(".dp-feed li").last()).toBeVisible();
  await expect
    .poll(() => restToBottom(page), { message: "лента встала на живой конец" })
    .toBeLessThanOrEqual(2);
  // Лента обязана быть длиннее окна: иначе «прокрутить вверх» некуда и проверка ничего не проверит.
  const room = await page.evaluate(
    () => document.documentElement.scrollHeight - window.innerHeight,
  );
  expect(room, "лента длиннее окна").toBeGreaterThan(400);
  // Своя прокрутка к живому концу — ОКНО 150 мс, в котором «мы внизу» не пересчитывается
  // (`ownScrollUntil` в SessionScreen): даём ему закрыться, иначе первая ручная прокрутка будет проглочена.
  await page.waitForTimeout(250);
}

/**
 * Уехать от живого конца — то, что делает человек пальцем.
 *
 * Своя прокрутка экрана к живому концу держит окно 150 мс (`ownScrollUntil`), в котором ручная
 * прокрутка НЕ пересчитывает «мы внизу»: это правило продукта, а не помеха. Поэтому прокрутка
 * повторяется, пока орган не появится, — иначе проверка зависела бы от того, успел ли закрыться зазор.
 */
async function leaveLiveEnd(page: Page): Promise<void> {
  await page.waitForTimeout(200);
  for (let attempt = 0; attempt < 6; attempt += 1) {
    // Каждый заход сдвигает позицию хоть на пиксель: `scrollTo` в ТУ ЖЕ точку события не рождает,
    // а проглоченная окном 150 мс прокрутка иначе так и осталась бы незамеченной.
    await page.evaluate((offset) => window.scrollTo(0, offset), attempt);
    if ((await organ(page).count()) > 0) return;
    await page.waitForTimeout(150);
  }
  await expect(organ(page), "уехал выше — орган есть").toBeVisible();
}

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/scenario", {
    // Очередь ПУСТА: числа органа считаны для стека «только зацеп» (52) — с доком очереди (W12)
    // стек выше на её строку, и орган встал бы на 114. Это проверка пары, а не очереди.
    data: { authed: true, lang: "ru", list: "ok", pair: "ok", queue: "empty" },
  });
});

test("орган виден ТОЛЬКО когда лента не внизу; порог — допуск автодописывания (360 и 320)", async ({
  browser,
}) => {
  for (const width of [360, 320]) {
    const context = await browser.newContext({ viewport: { width, height: 640 } });
    const page = await context.newPage();
    await openLong(page);

    // Живой конец — органа нет вовсе (не «невидим», а не существует: его нет и в обходе).
    await expect(organ(page), `${width}: внизу органа нет`).toHaveCount(0);

    // Уехали выше — орган появился (та же прокрутка, что у человека); дальше меряется сам порог.
    await leaveLiveEnd(page);
    await expect(organ(page), `${width}: уехал выше — орган есть`).toBeVisible();

    // Хвост допуска: пока человек в пределах 152 px от низа, лента ещё следует — органа нет.
    await page.evaluate(
      (slack) =>
        window.scrollTo(
          0,
          document.documentElement.scrollHeight - window.innerHeight - (slack - 68),
        ),
      SLACK,
    );
    await expect(organ(page), `${width}: внутри допуска органа нет`).toHaveCount(0);

    // Выше порога — лента перестала следовать, орган есть.
    await page.evaluate(
      (slack) =>
        window.scrollTo(
          0,
          document.documentElement.scrollHeight - window.innerHeight - (slack + 60),
        ),
      SLACK,
    );
    await expect(organ(page), `${width}: уехал выше порога — орган есть`).toBeVisible();

    // Вернулся вниз — орган исчез (одно состояние, а не два правила).
    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    await expect(organ(page), `${width}: вернулся вниз — органа нет`).toHaveCount(0);
    await context.close();
  }
});

// Имя проверки не обещает больше механизма (находка t4 F2): «текст не перекрыт» этой проверкой
// не меряется — заход на колонку и его цена измерены отдельной проверкой ниже (цена — P2 §5).
test("орган: 44×44, справа 8, над зацепом (60 от низа), зацеп не перекрыт, слой ниже стека", async ({
  page,
}) => {
  await openLong(page);
  await leaveLiveEnd(page);
  await expect(organ(page)).toBeVisible();
  const info = await page.evaluate(() => {
    const button = document.querySelector(".dp-tolast") as HTMLElement;
    const handle = document.querySelector(".dp-handle") as HTMLElement | null;
    const box = button.getBoundingClientRect();
    const handleBox = handle === null ? null : handle.getBoundingClientRect();
    // Внизу экрана — НИЖНИЙ СТЕК с зацепом шторки (D11 §1.1): он и есть последний орган страницы.
    const stack = document.querySelector(".dp-bottom") as HTMLElement | null;
    return {
      width: Math.round(box.width),
      height: Math.round(box.height),
      right: Math.round(window.innerWidth - box.right),
      bottom: Math.round(window.innerHeight - box.bottom),
      handleTop: handleBox === null ? null : Math.round(handleBox.top),
      overlap:
        handleBox === null
          ? false
          : !(
              box.right <= handleBox.left ||
              box.left >= handleBox.right ||
              box.bottom <= handleBox.top ||
              box.top >= handleBox.bottom
            ),
      zIndex: getComputedStyle(button).zIndex,
      stackZ: stack === null ? null : getComputedStyle(stack).zIndex,
      // В разметке орган стоит после ленты и перед нижним стеком: обход совпадает с видимым.
      afterFeed:
        document.querySelector(".dp-feed")?.compareDocumentPosition(button) !== undefined &&
        (document.querySelector(".dp-feed")!.compareDocumentPosition(button) &
          Node.DOCUMENT_POSITION_FOLLOWING) !==
          0,
      beforeStack:
        stack !== null &&
        (button.compareDocumentPosition(stack) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0,
    };
  });

  expect(info.width, "цель 44 по ширине").toBeGreaterThanOrEqual(44);
  expect(info.height, "цель 44 по высоте").toBeGreaterThanOrEqual(44);
  expect(info.right, "справа 8 — тот же зазор, что у пары").toBe(8);
  // 60 = --dp-bottom-reserve (52) + 8: над зацепом, но не в его полосе.
  expect(info.bottom, "стоит над зацепом и его зазором").toBe(60);
  expect(info.overlap, "зацеп и орган не пересекаются").toBe(false);
  expect(info.handleTop, "зацеп на месте").not.toBeNull();
  expect(info.zIndex, "слой 2 — ниже нижнего стека (10) и листа (40)").toBe("2");
  expect(info.stackZ, "стек — 10").toBe("10");
  expect(info.afterFeed, "орган в разметке после ленты").toBe(true);
  expect(info.beforeStack, "орган в разметке перед нижним стеком (обход совпадает с видимым)").toBe(
    true,
  );
});

test("место: заход на правый край колонки — ровно названная в P2 цена 36 px, не больше (360 и 320)", async ({
  browser,
}) => {
  for (const width of [360, 320]) {
    const context = await browser.newContext({ viewport: { width, height: 640 } });
    const page = await context.newPage();
    await openLong(page);
    await leaveLiveEnd(page);
    const overlap = await page.evaluate(() => {
      const button = document.querySelector(".dp-tolast") as HTMLElement;
      const pageNode = document.querySelector(".dp-page") as HTMLElement;
      const padRight = Number.parseFloat(getComputedStyle(pageNode).paddingRight);
      const contentRight = pageNode.getBoundingClientRect().right - padRight;
      return Math.round(contentRight - button.getBoundingClientRect().left);
    });
    // Цена плавающего органа (P2 §5), названная, а не случайная: 44 (цель) + 8 (зазор) − 16 (отбивка)
    // = 36 px правого края ленты. Компенсация тоже проверена выше: у живого конца органа нет вовсе,
    // поэтому последнее сообщение — то, ради которого нажимают, — под ним не оказывается никогда.
    expect(overlap, `${width}: заход на колонку — 36 px, не больше`).toBeLessThanOrEqual(36);
    expect(overlap, `${width}: орган правда над колонкой, а не в пустом поле`).toBeGreaterThan(30);
    await context.close();
  }
});

test("счёт новых: только при > 0 и только для сообщений, приехавших пока человек был не внизу; рост молчит", async ({
  page,
}) => {
  await openLong(page);
  await leaveLiveEnd(page);
  await expect(organ(page)).toBeVisible();

  // Счёт 0 — значка нет вовсе, имя — просто имя органа («0» не печатается).
  await expect(organ(page).locator(".dp-badge"), "при 0 значка нет").toHaveCount(0);
  await expect(organ(page)).toHaveAttribute("aria-label", TO_LAST);
  await expect(statusLine(page), "при 0 объявлять нечего").toBeEmpty();

  // Приехали два сообщения, пока человек был выше: значок 2, слова — один раз.
  await page.request.post("/__mock/push", { data: { count: 2 } });
  await expect(organ(page).locator(".dp-badge")).toHaveText("2");
  await expect(organ(page)).toHaveAttribute("aria-label", `${TO_LAST_NEW}2`);
  await expect(statusLine(page), "счёт объявлен словами").toHaveText(`${TO_LAST_NEW}2`);

  // Рост счёта: значок вырос, а объявление осталось одно и то же (читалку не будим каждым приростом).
  await page.request.post("/__mock/push", { data: { count: 1 } });
  await expect(organ(page).locator(".dp-badge")).toHaveText("3");
  await expect(organ(page)).toHaveAttribute("aria-label", `${TO_LAST_NEW}3`);
  await expect(statusLine(page), "рост счёта молчит").toHaveText(`${TO_LAST_NEW}2`);

  // Служебные строки в счёт не идут: три сообщения — это три, а не «три плюс инструменты».
  expect(
    await page.locator(".dp-feed .dp-tool").count(),
    "в ленте есть строки инструментов",
  ).toBeGreaterThan(0);

  // Возврат вниз: орган исчез, счёт и объявление погасли.
  await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  await expect(organ(page)).toHaveCount(0);
  await expect(statusLine(page), "возврат вниз строку очищает").toBeEmpty();
});

test("догруз старой страницы не считается новым: значок пуст, объявление молчит; живое — «1»", async ({
  page,
}) => {
  // Состояние, в котором дефект t4 F1 возможен: лента отдана с `hasMore: true`, человек уехал выше,
  // и экран САМ догружает старые (R4). Старая страница прибавляет СТРОКИ сверху — это не «новые».
  // Снимок и живое сообщение подаём сами (route), чтобы порядок был известен: стенд `session-long`
  // нарочно отдаёт `hasMore: false`, а у `session-many` нет двери живого сообщения.
  const records = Array.from({ length: 20 }, (_, index) => ({
    type: index % 2 === 0 ? "user" : "assistant",
    seq: 41 + index,
    time: 1761770100000 + index,
    text: index % 2 === 0 ? `Реплика ${index}.` : `Ответ ${index}.`,
  }));
  const snapshot = {
    header: { id: "session-many", createdAt: 1761770000000, title: "Длинная лента" },
    cursor: 60,
    hasMore: true,
    records,
    assistantDraft: null,
  };
  const live = {
    record: { type: "assistant", seq: 61, time: 1761770200000, text: "Живое после догруза." },
  };
  const sse = (event: string, data: unknown) =>
    `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
  const headers = {
    "Content-Type": "text/event-stream; charset=utf-8",
    "Cache-Control": "no-store",
  };

  let connections = 0;
  // Разрешение на живое сообщение — в объекте: поле читается и снаружи, и из обработчика.
  const gate: { release: (() => void) | null } = { release: null };
  await page.route("**/api/phone/live**", async (route) => {
    connections += 1;
    if (connections === 1) {
      await route.fulfill({ status: 200, headers, body: sse("snapshot", snapshot) });
      return;
    }
    // Второе соединение держим ОТКРЫТЫМ и молчащим: живое сообщение придёт по команде проверки.
    await new Promise<void>((resolve) => {
      gate.release = resolve;
    });
    await route.fulfill({ status: 200, headers, body: sse("event", live) });
  });

  // Старшая страница — ЯВНО `older: "ok"` и счётчик страниц с нуля: иначе проверка зависит от
  // того, что оставили предыдущие файлы (стенд общий на прогон, W12).
  await page.request.post("/__mock/scenario", {
    data: { older: "ok", question: "none" },
  });
  await page.request.post("/__mock/s3-reset", { data: {} });

  await page.goto("/?session=session-many");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Длинная лента");
  await expect(page.locator(".dp-feed li").last()).toBeVisible();
  await expect
    .poll(() => restToBottom(page), { message: "лента встала на живой конец" })
    .toBeLessThanOrEqual(2);
  await page.waitForTimeout(250);

  // Человек уехал выше — орган есть, счёт пуст.
  await leaveLiveEnd(page);
  await expect(organ(page)).toBeVisible();
  await expect(organ(page).locator(".dp-badge"), "до догруза значка нет").toHaveCount(0);

  // Догруз идёт сам: долистали до ВЕРХА ЛЕНТЫ (D1 §4.2, 11.11.2026) — экран просит старую страницу
  // у калитки. Верх документа для этого больше не годится: над лентой стоят очередь и шапка.
  await page.evaluate(() => {
    const feed = document.querySelector(".dp-feed") as HTMLElement;
    window.scrollTo(0, feed.getBoundingClientRect().top + window.scrollY - 40);
  });
  await expect
    .poll(
      async () =>
        page.evaluate(() => document.body.innerText.includes("Самое первое сообщение человека.")),
      {
        message: "старая страница легла сверху",
        timeout: 10_000,
      },
    )
    .toBe(true);

  // Догруженные сверху СТАРЫЕ сообщения — не «новые»: значок пуст, объявление молчит.
  await expect(organ(page), "орган остался").toBeVisible();
  await expect(organ(page).locator(".dp-badge"), "догруз не даёт значка").toHaveCount(0);
  await expect(organ(page)).toHaveAttribute("aria-label", TO_LAST);
  await expect(statusLine(page), "догруз не объявляется").toBeEmpty();

  // А вот настоящее живое сообщение — считается: значок «1» и слова один раз.
  await expect
    .poll(() => connections, { message: "поток переподключился", timeout: 10_000 })
    .toBeGreaterThanOrEqual(2);
  gate.release?.();
  await expect(page.getByText("Живое после догруза.")).toBeVisible();
  await expect(organ(page).locator(".dp-badge"), "живое — один").toHaveText("1");
  await expect(organ(page)).toHaveAttribute("aria-label", `${TO_LAST_NEW}1`);
  await expect(statusLine(page), "живое объявлено").toHaveText(`${TO_LAST_NEW}1`);
});
test("нажатие: живой конец, автодописывание снова работает, фокус в заголовке, счёт гаснет", async ({
  page,
}) => {
  await openLong(page);
  await leaveLiveEnd(page);
  const button = organ(page);
  await expect(button).toBeVisible();
  await page.request.post("/__mock/push", { data: { count: 2 } });
  await expect(button.locator(".dp-badge")).toHaveText("2");

  await button.click();

  // Орган исчез, лента стоит на живом конце, фокус ушёл на h1 (а не потерялся вместе с кнопкой).
  await expect(button).toHaveCount(0);
  await expect
    .poll(() => restToBottom(page), { message: "прыжок к живому концу" })
    .toBeLessThanOrEqual(2);
  await expect(page.getByRole("heading", { level: 1 })).toBeFocused();
  await expect(statusLine(page), "счёт погас").toBeEmpty();

  // Автодописывание ВКЛЮЧЕНО снова: новое сообщение держит низ, орган не появляется.
  await page.request.post("/__mock/push", { data: { count: 1 } });
  await expect(page.getByText("Новое сообщение 52.")).toBeVisible();
  await expect
    .poll(() => restToBottom(page), { message: "лента снова следует за новыми" })
    .toBeLessThanOrEqual(2);
  await expect(organ(page), "у живого конца органа нет").toHaveCount(0);
});

test("движения нет: прыжок мгновенный, при reduced motion выключать нечего (360)", async ({
  browser,
}) => {
  const context = await browser.newContext({
    viewport: { width: 360, height: 640 },
    reducedMotion: "reduce",
  });
  const page = await context.newPage();
  await openLong(page);
  await leaveLiveEnd(page);
  const button = organ(page);
  await expect(button).toBeVisible();

  const motion = await page.evaluate(() => {
    const buttonNode = document.querySelector(".dp-tolast") as HTMLElement;
    const style = getComputedStyle(buttonNode);
    return {
      transition: style.transitionDuration,
      animation: style.animationName,
      scrollBehavior: getComputedStyle(document.documentElement).scrollBehavior,
    };
  });
  expect(motion.transition, "переходов нет вовсе").toBe("0s");
  expect(motion.animation, "анимаций нет вовсе").toBe("none");
  expect(motion.scrollBehavior, "плавной прокрутки нет").toBe("auto");

  await button.click();
  // Прыжок — в тот же миг, без кадров «догоняния»: остаток до низа нулевой сразу после нажатия.
  expect(await restToBottom(page), "прыжок мгновенный").toBeLessThanOrEqual(2);
  await context.close();
});

test("имя — слово словаря на трёх языках, видимого слова нет, значок скрыт от читалки", async ({
  browser,
  request,
}) => {
  const names = {
    ru: "К последнему сообщению",
    en: "Jump to the latest message",
    zh: "跳到最新消息",
  } as const;

  for (const lang of ["ru", "en", "zh"] as const) {
    await request.post("/__mock/scenario", {
      data: { authed: true, lang, list: "ok", pair: "ok" },
    });
    const context = await browser.newContext({ viewport: { width: 360, height: 640 } });
    const page = await context.newPage();
    // Тот же путь, что у остальных проверок: экран сам встаёт на живой конец, затем человек
    // (проверка) уезжает выше — с тем же окном 150 мс на свою прокрутку.
    await openLong(page);
    await leaveLiveEnd(page);

    const button = page.getByRole("button", { name: names[lang] });
    await expect(button, `${lang}: имя — строка словаря, а не имя ключа`).toBeVisible();
    const info = await button.evaluate((node) => ({
      text: (node.textContent ?? "").trim(),
      hasTitle: node.hasAttribute("title"),
      iconHidden: node.querySelector("svg")?.getAttribute("aria-hidden") ?? null,
    }));
    expect(info.text, `${lang}: видимого слова на кнопке нет`).toBe("");
    expect(info.hasTitle, `${lang}: подсказки-дубля имени нет`).toBe(false);
    expect(info.iconHidden, `${lang}: значок скрыт от читалки, имя несёт кнопка`).toBe("true");
    await context.close();
  }
});

test("F-07: заголовок экрана беды называет беду, а не «Без названия»; слово — одно", async ({
  page,
}) => {
  const h1 = page.getByRole("heading", { level: 1 });

  // Сессии нет (финальный отказ `missing`): h1 — слово беды, в блоке — ОБЪЯСНЕНИЕ, а не повтор.
  await page.goto("/?session=session-error");
  await expect(h1).toHaveText("Сессия не открылась");
  await expect(page.getByText("Сессия не открылась"), "слово беды печатается один раз").toHaveCount(
    1,
  );
  await expect(
    page.getByText("Сессия не найдена или её нельзя открыть с телефона. Посмотрите на компьютер."),
  ).toBeVisible();
  await expect(page.getByText("Без названия"), "«Без названия» на беде — неправда").toHaveCount(0);

  // Постоянный отказ «слишком много экранов»: заголовок тот же, причина — в блоке и с действием.
  await page.goto("/?session=session-toomany");
  await expect(h1).toHaveText("Сессия не открылась");
  await expect(page.getByText("Слишком много открытых экранов — закройте лишние.")).toHaveCount(1);
  await expect(page.getByText("Без названия")).toHaveCount(0);

  // Открытая сессия с известным именем — заголовок по имени (ступень 1 остаётся).
  await page.goto("/?session=session-live");
  await expect(h1).toHaveText("Починить калитку");
});

test("F-07: пока подключаюсь — «Подключаюсь к сессии…» одной строкой, а не дважды", async ({
  page,
}) => {
  await page.goto("/?session=session-silent");
  const h1 = page.getByRole("heading", { level: 1 });
  await expect(h1).toHaveText("Подключаюсь к сессии…");
  // Одна и та же фраза не печатается вторым ответом под шапкой (`60` §9).
  await expect(page.getByText("Подключаюсь к сессии")).toHaveCount(1);
  await expect(page.getByText("Без названия")).toHaveCount(0);
});
