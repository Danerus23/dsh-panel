import { expect, test, type APIRequestContext, type Page } from "@playwright/test";

// W12 (D11 §3, приёмка 12.11.2026, DEF-149): ОЧЕРЕДЬ — БЛОК НАД ВВОДОМ, РАСКРЫТИЕ ВВЕРХ.
//
// Слово владельца: «если там механизм как и с вопросами и прячется наверху то это не правильно»;
// два скриншота DSH: очередь — блок НАД полем ввода, свёрнутая строка «N queued messages» с
// раскрытием ВВЕРХ, внутри строки сообщений; поле ввода остаётся снизу.
//
// Что доказывается прогоном:
//   1. строка «В очереди: N» стоит В НИЖНЕМ СТЕКЕ НАД зацепом (44 высотой), а не в ленте;
//   2. раскрытие идёт ВВЕРХ: список стоит выше строки, у списка свой предел `min(40dvh,320px)` и
//      своя прокрутка, а запас снизу растёт на высоту панели (одно измеренное число, D11 §1.2);
//   3. после «Задача принята.» очередь РАСКРЫТА и фокус в её `h2`, страница при этом не прыгает;
//   4. «Убрать» работает как раньше: вопрос на месте пункта, «Оставить» первым и в фокусе;
//   5. N = 0 и «сессия не открыта» — блока НЕТ; «не прочиталась» — блок есть, с «Повторить».

const WORDS = {
  send: "Отправить задачу",
  remove: "Убрать",
  keep: "Оставить",
  removeYes: "Убрать задачу",
  repeat: "Повторить",
};

/** Сценарий стенда — один раз на тест: иначе `queue` протекает в следующие проверки. */
async function scenario(request: APIRequestContext, data: Record<string, unknown>): Promise<void> {
  await request.post("/__mock/scenario", {
    // `question: "none"` в умолчаниях — страж от протекания: проверки R3 ставят вопрос на поверхность,
    // и без сброса карточка поднимала бы стек следующих тестов до 554 вместо 106 (R3, ловилось прогоном).
    data: {
      authed: true,
      lang: "ru",
      list: "ok",
      pair: "ok",
      task: "ok",
      question: "none",
      ...data,
    },
  });
  // Очередь стенда — общая на прогон (её меняют принятые задачи): чистим, иначе состав течёт вперёд.
  await request.post("/__mock/s3-reset", { data: {} });
}

/** Свёрнутая строка очереди и её числа. */
async function queueShape(page: Page) {
  return page.evaluate(() => {
    const rect = (selector: string) => {
      const node = document.querySelector(selector) as HTMLElement | null;
      if (node === null) return null;
      const box = node.getBoundingClientRect();
      return {
        left: Math.round(box.left),
        right: Math.round(box.right),
        top: Math.round(box.top),
        bottom: Math.round(box.bottom),
        width: Math.round(box.width),
        height: Math.round(box.height),
      };
    };
    const stack = document.querySelector(".dp-bottom") as HTMLElement | null;
    const row = document.querySelector(".dp-queue-row") as HTMLElement | null;
    const list = document.querySelector(".dp-queue-list") as HTMLElement | null;
    const root = getComputedStyle(document.documentElement);
    return {
      row: rect(".dp-queue-row"),
      handle: rect(".dp-handle"),
      card: rect(".dp-queue"),
      list: rect(".dp-queue-list"),
      stack: stack === null ? null : Math.round(stack.getBoundingClientRect().height),
      reserve: root.getPropertyValue("--dp-bottom-reserve").trim(),
      pagePadding: getComputedStyle(document.querySelector(".dp-page") as HTMLElement)
        .paddingBottom,
      listOverflow: list === null ? null : getComputedStyle(list).overflowY,
      listMaxHeight: list === null ? null : getComputedStyle(list).maxHeight,
      inStack: row === null ? false : stack !== null && stack.contains(row),
      expanded: row === null ? null : row.getAttribute("aria-expanded"),
      controls: row === null ? null : row.getAttribute("aria-controls"),
      text: row === null ? "" : (row.textContent ?? "").trim(),
      chevron: row === null ? 0 : row.querySelectorAll("svg").length,
      items: document.querySelectorAll(".dp-queue-item").length,
      panelTitle: document.querySelector(".dp-queue-title") === null ? null : true,
    };
  });
}

/** Запас пересчитывается кадром (`useBottomReserve` пишет в rAF) — ждём сходимости, а не одного замера. */
async function settled(page: Page): Promise<void> {
  await expect
    .poll(
      async () => {
        const shape = await queueShape(page);
        return `${shape.reserve}|${shape.stack}`;
      },
      { message: "запас снизу = фактическая высота стека" },
    )
    .toMatch(/^(\d+)px\|\1$/u);
}

/** Отправить задачу через шторку ввода: после неё очередь обязана быть видна (D11 §3.2). */
async function sendTask(page: Page, text: string): Promise<void> {
  await page.locator(".dp-handle").click();
  await expect(page.locator(".dp-sheet")).toBeVisible();
  await page.locator(".dp-sheet textarea").fill(text);
  await page.locator(".dp-sheet").getByRole("button", { name: WORDS.send }).click();
  await expect(page.locator(".dp-handle-note")).toContainText("Задача принята.");
  await expect(page.locator(".dp-sheet"), "лист закрылся сам").toHaveCount(0);
}

test.describe("W12: очередь — блок над вводом", () => {
  // Гигиена стенда (R8b): живой кадр "add" теперь кладёт пункт в ОЧЕРЕДЬ СТЕНДА (иначе после
  // «убрали всё» кадр воскрешал бы старый состав и счёт был бы чужим). Значит после проверки
  // очередь надо вернуть к базовой — иначе лишний пункт протечёт в следующие файлы (ловилось
  // полным прогоном: `s4a` ждала 2 пункта и получала 3).
  test.afterEach(async ({ request }) => {
    await request.post("/__mock/s3-reset", { data: {} });
  });
  test("свёрнутая строка «В очереди: N» стоит над зацепом, в нижнем стеке, а не в ленте", async ({
    page,
    request,
  }) => {
    await scenario(request, { queue: "ok" });
    await page.goto("/?session=session-live");
    await expect(page.locator(".dp-queue-row")).toBeVisible();

    await settled(page);
    const shape = await queueShape(page);
    expect(shape.inStack, "строка живёт в нижнем стеке").toBe(true);
    expect(shape.text, "ровно одно утверждение — счёт словами").toBe("В очереди: 2");
    expect(shape.row!.height, "строка — цель 44").toBeGreaterThanOrEqual(44);
    expect(shape.row!.bottom, "строка стоит НАД зацепом").toBeLessThanOrEqual(shape.handle!.top);
    expect(shape.chevron, "шеврон — знак раскрытия").toBe(1);
    expect(shape.expanded, "свёрнута по умолчанию").toBe("false");
    // D13-4 (R3): у СВЁРНУТОЙ строки `aria-controls` нет — панели нет в DOM, и ссылка не должна
    // висеть в пустоту (ARIA 1.2); при раскрытии появляется (проверка ниже).
    expect(shape.controls, "`aria-controls` у свёрнутой — нет").toBeNull();
    expect(shape.items, "в свёрнутом виде пунктов нет").toBe(0);
    expect(shape.panelTitle, "и заголовка нет").toBeNull();

    // Стек = отбивка 8 + карточка 46 (строка 44 + две рамки 1) + зазор 8 + зацеп 44 = 106:
    // запас читается ИЗМЕРЕННЫМ числом, а не константой.
    expect(shape.stack, "стек = отбивка + карточка + зазор + зацеп").toBe(106);
    expect(shape.reserve, "запас снизу — измеренная высота стека").toBe("106px");
    expect(shape.pagePadding, "страница держит тот же запас").toBe("106px");
  });

  test("раскрытие идёт ВВЕРХ: список выше строки, предел min(40dvh,320px), прокрутка внутри", async ({
    page,
    request,
  }) => {
    await scenario(request, { queue: "ok" });
    await page.goto("/?session=session-live");
    const row = page.locator(".dp-queue-row");
    await expect(row).toBeVisible();

    await row.click();
    await expect(page.locator(".dp-queue-panel"), "панель раскрылась").toBeVisible();
    await expect(page.getByRole("heading", { name: "Очередь" })).toBeVisible();
    await expect(page.locator(".dp-queue-item"), "оба пункта на месте").toHaveCount(2);
    await expect(row).toHaveAttribute("aria-expanded", "true");
    // ...и только теперь ссылка на панель появляется вместе с самой панелью (D13-4).
    await expect(row).toHaveAttribute("aria-controls", /-queue-panel$/u);
    await settled(page);
    const shape = await queueShape(page);
    // ВВЕРХ: низ списка выше верха строки — панель растёт вверх, а не вниз.
    expect(shape.list!.bottom, "список стоит ВЫШЕ строки").toBeLessThanOrEqual(shape.row!.top);
    // Предел: min(40dvh, 320px) при 800 высоты = 320; прокрутка — своя.
    expect(shape.listMaxHeight, "предел списка назван").toBe("320px");
    expect(shape.listOverflow, "список прокручивается сам").toBe("auto");
    expect(shape.list!.height, "список в пределе").toBeLessThanOrEqual(320);

    // Запас вырос на высоту панели: лента не уходит под раскрытую очередь (D11 §1.2).
    expect(shape.stack, "стек вырос на панель").toBeGreaterThan(106);
    expect(shape.reserve, "запас = фактическая высота стека").toBe(`${shape.stack}px`);
    expect(shape.row!.bottom, "строка всё там же — над зацепом").toBeLessThanOrEqual(
      shape.handle!.top,
    );

    // Свернули обратно — панели нет, запас вернулся к 106.
    await row.click();
    await expect(page.locator(".dp-queue-panel")).toHaveCount(0);
    await expect
      .poll(async () => (await queueShape(page)).reserve, { message: "запас вернулся" })
      .toBe("106px");
  });

  test("после «Задача принята.» очередь СВЁРНУТА: строка над вводом, счёт вырос, панели нет", async ({
    page,
    request,
  }) => {
    await scenario(request, { queue: "ok" });
    await page.goto("/?session=session-live");
    await expect(page.locator(".dp-feed li").last()).toBeVisible();
    const row = page.locator(".dp-queue-row");
    await expect(row).toBeVisible();
    await expect(row, "до приёма — два пункта").toContainText("В очереди: 2");
    const before = await page.evaluate(() => Math.round(window.scrollY));

    await sendTask(page, "Проверь очередь после приёма");

    // СЛОВО ВЛАДЕЛЬЦА 13.11.2026: «строка свёрнута, откроет сам». По приёму экран только перечитывает
    // состав — меняется СЧЁТ в строке, а панель НЕ раскрывается и в DOM не появляется.
    await expect(row, "счёт вырос — принятая задача в очереди").toContainText("В очереди: 3");
    await expect(row).toHaveAttribute("aria-expanded", "false");
    expect(await page.locator(".dp-queue-panel").count(), "панели в DOM нет").toBe(0);
    expect(await page.locator(".dp-queue-item").count(), "и пунктов нет — свёрнуто").toBe(0);
    // Фокус не перехвачен: после закрытия листа он на зацепе (D1 §5.1), а не в заголовке панели.
    await expect(page.locator(".dp-handle"), "фокус на зацепе").toBeFocused();

    // Прыжка к блоку над лентой больше НЕТ: страница осталась там, где была (D11 §3.2).
    const after = await page.evaluate(() => Math.round(window.scrollY));
    expect(Math.abs(after - before), "страница не уехала от приёма").toBeLessThanOrEqual(2);
    // Строка выросла над зацепом — запас подрос и совпадает с фактической высотой стека (D11 §1.2).
    await settled(page);
    const place = await page.evaluate(() => {
      const stack = (document.querySelector(".dp-bottom") as HTMLElement).getBoundingClientRect();
      const rowBox = (
        document.querySelector(".dp-queue-row") as HTMLElement
      ).getBoundingClientRect();
      return {
        rowTop: Math.round(rowBox.top),
        rowBottom: Math.round(rowBox.bottom),
        viewport: window.innerHeight,
        reserve: getComputedStyle(document.documentElement)
          .getPropertyValue("--dp-bottom-reserve")
          .trim(),
        stackHeight: Math.round(stack.height * 10) / 10,
      };
    });
    expect(place.rowTop, "строка в кадре").toBeGreaterThan(0);
    expect(place.rowBottom, "и целиком").toBeLessThanOrEqual(place.viewport);
    expect(place.reserve, "запас = высота стека").toBe(`${place.stackHeight}px`);
  });

  test("«Убрать» работает: вопрос на месте пункта, безопасный ответ первым и в фокусе", async ({
    page,
    request,
  }) => {
    await scenario(request, { queue: "ok", remove: "ok" });
    await page.goto("/?session=session-live");
    await page.locator(".dp-queue-row").click();
    const item = page.locator(".dp-queue-item").first();
    await expect(item).toBeVisible();

    await item.getByRole("button", { name: WORDS.remove }).click();
    const ask = item.locator(".dp-queue-ask");
    await expect(ask, "вопрос раскрылся НА МЕСТЕ пункта").toBeVisible();
    await expect(ask.getByText("Убрать задачу из очереди?").first()).toBeVisible();
    const buttons = ask.getByRole("button");
    await expect(buttons.nth(0)).toHaveText(WORDS.keep);
    await expect(buttons.nth(1)).toHaveText(WORDS.removeYes);
    await expect(
      ask.getByRole("button", { name: WORDS.keep }),
      "безопасный ответ в фокусе",
    ).toBeFocused();

    // «Оставить» — пункт на месте, фокус вернулся на крестик.
    await ask.getByRole("button", { name: WORDS.keep }).click();
    await expect(ask).toHaveCount(0);
    await expect(item.getByRole("button", { name: WORDS.remove })).toBeFocused();
    await expect(page.locator(".dp-queue-item")).toHaveCount(2);

    // «Убрать задачу» — пункт исчез, счёт изменился, сказано словами.
    await item.getByRole("button", { name: WORDS.remove }).click();
    await item.getByRole("button", { name: WORDS.removeYes }).click();
    await expect(page.locator(".dp-queue-item")).toHaveCount(1);
    await expect(page.locator(".dp-queue-row")).toContainText("В очереди: 1");
    await expect(page.locator(".dp-queue").getByText("Убрано из очереди.")).toBeVisible();
  });

  test("пункт очереди — карточка: своя подложка, граница 1 px, радиус 8, зазор 8 (слово владельца 13.11)", async ({
    page,
    request,
  }) => {
    // «Пункты сливаются»: раньше пункт был только столбиком текста, разделённым тонкой линией, —
    // на телефоне они читались одной простынёй. Теперь каждый — карточка на своей подложке.
    await scenario(request, { queue: "ok" });

    for (const theme of ["light", "dark"] as const) {
      await page.emulateMedia({ colorScheme: theme });
      await page.goto("/?session=session-live");
      await page.locator(".dp-queue-row").click();
      await expect(page.locator(".dp-queue-panel")).toBeVisible();

      const look = await page.evaluate(() => {
        const items = [...document.querySelectorAll(".dp-queue-item")] as HTMLElement[];
        const style = getComputedStyle(items[0] as HTMLElement);
        const list = getComputedStyle(document.querySelector(".dp-queue-list") as HTMLElement);
        const boxes = items.map((item) => item.getBoundingClientRect());
        return {
          count: items.length,
          background: style.backgroundColor,
          border: `${style.borderTopWidth} ${style.borderTopStyle}`,
          radius: style.borderRadius,
          padding: style.paddingTop,
          gap: list.rowGap,
          heights: boxes.map((box) => Math.round(box.height)),
          spacing:
            boxes.length > 1 && boxes[0] !== undefined && boxes[1] !== undefined
              ? Math.round(boxes[1].top - boxes[0].bottom)
              : null,
        };
      });

      expect(look.count, `${theme}: два пункта`).toBe(2);
      expect(look.background, `${theme}: подложка пункта НЕ прозрачная`).not.toBe(
        "rgba(0, 0, 0, 0)",
      );
      expect(look.background, `${theme}: подложка есть`).not.toBe("transparent");
      expect(look.border, `${theme}: граница 1 px линией`).toBe("1px solid");
      expect(look.radius, `${theme}: радиус 8`).toBe("8px");
      expect(look.padding, `${theme}: отбивка 8`).toBe("8px");
      expect(look.gap, `${theme}: зазор между пунктами 8`).toBe("8px");
      expect(look.spacing, `${theme}: пункты не слипаются`).toBe(8);
      expect(look.heights[0], `${theme}: пункт выше своей отбивки`).toBeGreaterThan(16);
    }
    await page.emulateMedia({ colorScheme: null });
  });

  test("«Убрано из очереди.» гаснет через 5 с — панель и строка на месте (дефект живой приёмки 13.11)", async ({
    page,
    request,
  }) => {
    await scenario(request, { queue: "ok", remove: "ok" });
    await page.goto("/?session=session-live");
    const row = page.locator(".dp-queue-row");
    await row.click();
    const item = page.locator(".dp-queue-item").first();
    await expect(item).toBeVisible();
    await item.getByRole("button", { name: WORDS.remove }).click();
    await item.getByRole("button", { name: WORDS.removeYes }).click();

    const status = page.locator(".dp-queue").getByText("Убрано из очереди.");
    await expect(status, "сначала сказано словами").toBeVisible();
    expect(await page.locator(".dp-queue-item").count(), "пункт ушёл").toBe(1);

    // 5 с — столько же живёт слово успеха у строки исхода (`90` №19): факт остаётся составом
    // (пункта нет), а слово не висит над панелью вечно.
    await expect(status, "через ~5 с слово гаснет").toHaveCount(0, { timeout: 9_000 });
    // Панель и строка при этом на месте, счёт верный — гаснет только слово.
    await expect(page.locator(".dp-queue-panel")).toBeVisible();
    await expect(row).toContainText("В очереди: 1");
    expect(await page.locator(".dp-queue-item").count()).toBe(1);
    // И в состоянии его тоже нет: свернули/раскрыли — статуса не появилось снова.
    await row.click();
    await expect(page.locator(".dp-queue-panel")).toHaveCount(0);
    await row.click();
    await expect(page.locator(".dp-queue-panel")).toBeVisible();
    expect(await page.locator(".dp-queue").getByText("Убрано из очереди.").count()).toBe(0);
  });

  test("пустая очередь и «сессия не открыта» — блока НЕТ; «не прочиталась» — блок с «Повторить»", async ({
    page,
    request,
  }) => {
    await scenario(request, { queue: "empty" });
    await page.goto("/?session=session-live");
    await expect(page.locator(".dp-handle")).toBeVisible();
    // Ноль — пустая величина: ни строки, ни счётчика, ни пустой панели.
    expect(await page.locator(".dp-queue").count(), "блока нет вовсе").toBe(0);
    expect(await page.getByText("В очереди: 0").count()).toBe(0);

    await scenario(request, { queue: "no-live" });
    await page.reload();
    await expect(page.locator(".dp-handle")).toBeVisible();
    expect(await page.locator(".dp-queue").count(), "сессия не открыта — блока нет").toBe(0);

    await scenario(request, { queue: "unreadable" });
    await page.reload();
    const queue = page.locator(".dp-queue");
    await expect(queue.locator(".dp-error-block"), "факт отказа не исчезает").toContainText(
      "Очередь не прочиталась",
    );
    await expect(queue.getByRole("button", { name: WORDS.repeat })).toBeVisible();
    await expect(page.locator(".dp-queue-item")).toHaveCount(0);
  });

  test("вопрос и раскрытая очередь ОДНОВРЕМЕННО: строка видна и ≥44, шеврон на месте, действия вопроса видны", async ({
    page,
    request,
  }) => {
    // D13-2 (R3): стек упирается в предел, и раньше сжималась сама карточка очереди — вместе
    // с её строкой и шевроном (единственным органом сворачивания). Теперь сжимается СПИСОК.
    await scenario(request, { queue: "ok", question: "two", questionSession: "session-live" });

    for (const view of [
      { width: 360, height: 692 },
      { width: 320, height: 603 },
    ]) {
      await page.setViewportSize(view);
      await page.goto("/?session=session-live");
      await expect(page.locator(".dp-question"), "вопрос на поверхности").toBeVisible();
      const row = page.locator(".dp-queue-row");
      await expect(row).toBeVisible();
      await row.click();
      await expect(page.locator(".dp-queue-panel"), "очередь раскрыта").toBeVisible();
      await settled(page);

      const both = await page.evaluate(() => {
        const box = (selector: string) => {
          const node = document.querySelector(selector) as HTMLElement | null;
          return node === null ? null : node.getBoundingClientRect();
        };
        const rowBox = box(".dp-queue-row");
        const actions = box(".dp-question-actions");
        const body = box(".dp-question-body");
        const chevron = box(".dp-queue-chevron");
        return {
          viewport: `${window.innerWidth}x${window.innerHeight}`,
          rowHeight: rowBox === null ? 0 : Math.round(rowBox.height * 10) / 10,
          rowInView: rowBox !== null && rowBox.bottom <= window.innerHeight + 0.5,
          chevron: chevron === null ? null : Math.round(chevron.height),
          actionsInView: actions !== null && actions.bottom <= window.innerHeight + 0.5,
          actionsInCard: actions !== null && actions.top > 0,
          bodyHeight: body === null ? 0 : Math.round(body.height * 10) / 10,
          peek: document.querySelectorAll(".dp-queue-item").length,
        };
      });

      expect(both.rowHeight, `${both.viewport}: строка очереди цела — 44`).toBeGreaterThanOrEqual(
        44,
      );
      expect(both.rowInView, `${both.viewport}: строка не уехала за экран`).toBe(true);
      expect(both.chevron, `${both.viewport}: шеврон на месте`).toBe(18);
      expect(both.actionsInView, `${both.viewport}: действия вопроса видны`).toBe(true);
      expect(both.actionsInCard, `${both.viewport}: и стоят в своей карточке`).toBe(true);
      expect(
        both.bodyHeight,
        `${both.viewport}: у вопроса осталось место под варианты`,
      ).toBeGreaterThan(0);
      expect(both.peek, `${both.viewport}: пункты очереди на месте`).toBeGreaterThan(0);
    }
  });

  test("кольцо фокуса строки очереди — ВНУТРИ карточки (outline-offset −2)", async ({
    page,
    request,
  }) => {
    // D13-3: строка лежит вплотную к кромкам, кольцо с offset 2 обрезалось `overflow: hidden`
    // карточки — оставалась верхняя дуга. Теперь кольцо внутри кнопки.
    await scenario(request, { queue: "ok" });
    await page.goto("/?session=session-live");
    const row = page.locator(".dp-queue-row");
    await expect(row).toBeVisible();
    // Программный `focus()` на кнопке в Chromium даёт `:focus-visible` (проверено): кольцо читается.
    await row.focus();
    const ring = await row.evaluate((node) => {
      const style = getComputedStyle(node);
      const box = node.getBoundingClientRect();
      const card = (node.closest(".dp-queue") as HTMLElement).getBoundingClientRect();
      return {
        offset: style.outlineOffset,
        width: style.outlineWidth,
        inside: box.top >= card.top - 0.5 && box.bottom <= card.bottom + 0.5,
      };
    });
    expect(ring.offset, "кольцо сдвинуто ВНУТРЬ (а не наружу за обрезку)").toBe("-2px");
    expect(ring.width, "кольцо — 2 px (80-accessibility)").toBe("2px");
    expect(ring.inside, "строка внутри карточки — кольцу есть где лежать").toBe(true);
  });

  test("aria-controls строки очереди — ТОЛЬКО когда панель есть в DOM", async ({
    page,
    request,
  }) => {
    await scenario(request, { queue: "ok" });
    await page.goto("/?session=session-live");
    const row = page.locator(".dp-queue-row");
    await expect(row).toBeVisible();

    expect(await row.getAttribute("aria-controls"), "свёрнуто — ссылки нет").toBeNull();
    expect(await page.locator("[id$='-queue-panel']").count(), "и панели нет").toBe(0);

    await row.click();
    const expanded = page.locator(".dp-queue-panel");
    await expect(expanded).toBeVisible();
    const controls = await row.getAttribute("aria-controls");
    expect(controls, "раскрыто — ссылка есть").toMatch(/-queue-panel$/u);
    const id = await expanded.getAttribute("id");
    expect(controls, "и указывает ровно на эту панель").toBe(id);
  });

  // R8 (живая приёмка 13.11.2026): ручное раскрытие НЕ переживает «Задача принята» и N = 0.
  test("ручное раскрытие не переживает приём: после «Задача принята» строка СВЁРНУТА (R8)", async ({
    page,
    request,
  }) => {
    await scenario(request, { queue: "ok" });
    await page.goto("/?session=session-live");
    const row = page.locator(".dp-queue-row");
    await expect(row).toBeVisible();

    // Человек раскрыл очередь САМ (тап по строке) — и в этом состоянии отправляет задачу.
    await row.click();
    await expect(page.locator(".dp-queue-panel"), "раскрыта вручную").toBeVisible();

    await sendTask(page, "Проверь очередь после приёма");

    // Слово владельца: после КАЖДОЙ «Задача принята» строка свёрнута — состояние не переживает приём.
    await expect(row, "счёт вырос — принятая задача в очереди").toContainText("В очереди: 3");
    await expect(row, "и строка СВЁРНУТА").toHaveAttribute("aria-expanded", "false");
    expect(await page.locator(".dp-queue-panel").count(), "панели в DOM нет").toBe(0);
  });

  test("N = 0 сбрасывает раскрытие, и ручное открытие после этого снова работает (R8)", async ({
    page,
    request,
  }) => {
    await scenario(request, { queue: "ok", remove: "ok" });
    await page.goto("/?session=session-live");
    const row = page.locator(".dp-queue-row");
    await row.click();
    await expect(page.locator(".dp-queue-panel")).toBeVisible();

    // Убираем ВСЁ: 2 → 1 → 0.
    for (const left of [1, 0]) {
      const item = page.locator(".dp-queue-item").first();
      await item.getByRole("button", { name: WORDS.remove }).click();
      await item.getByRole("button", { name: WORDS.removeYes }).click();
      await expect(page.locator(".dp-queue-item")).toHaveCount(left);
    }
    // N = 0 — блока нет вовсе (D11 §3.5), а раскрытое состояние сброшено.
    await expect(page.locator(".dp-queue")).toHaveCount(0);

    await sendTask(page, "Задача после пустой очереди");
    await expect(row, "строка вернулась со счётом").toContainText("В очереди: 1");
    await expect(row, "и она СВЁРНУТА").toHaveAttribute("aria-expanded", "false");
    expect(await page.locator(".dp-queue-panel").count(), "панели в DOM нет").toBe(0);

    // И ручное открытие после этого работает — состояние не «залипло» ни в одну сторону.
    await row.click();
    await expect(page.locator(".dp-queue-panel"), "руками — открывается").toBeVisible();
    await expect(row).toHaveAttribute("aria-expanded", "true");
  });

  // R8b (V4g, мутация M2 выжила): сброс на N = 0 проверяется БЕЗ отправки задачи — иначе сброс
  // на `accepted` маскирует эффект N = 0 и мутация «убрать эффект N = 0» остаётся зелёной.
  // Пункт приносит ЖИВОЙ КАДР очереди (`queueFrame: "add"`, задержка отодвинута, чтобы успеть
  // убрать всё), никакой «Задача принята» в этом сценарии нет.
  test("N = 0 сбрасывает раскрытие и БЕЗ отправки: пункт приходит живым кадром (R8b)", async ({
    page,
    request,
  }) => {
    await scenario(request, {
      queue: "ok",
      remove: "ok",
      queueFrame: "add",
      queueFrameDelay: 5_000,
    });
    await page.goto("/?session=session-live");
    const row = page.locator(".dp-queue-row");
    await expect(row).toBeVisible();

    // (1) ручное раскрытие
    await row.click();
    await expect(page.locator(".dp-queue-panel")).toBeVisible();

    // (2) убираем всё до N = 0 — блока нет вовсе; эффект N = 0 должен снять раскрытие
    for (const left of [1, 0]) {
      const item = page.locator(".dp-queue-item").first();
      await item.getByRole("button", { name: WORDS.remove }).click();
      await item.getByRole("button", { name: WORDS.removeYes }).click();
      await expect(page.locator(".dp-queue-item")).toHaveCount(left);
    }
    await expect(page.locator(".dp-queue")).toHaveCount(0);

    // (3) ЖИВОЙ КАДР приносит новый пункт — без задачи, без «Задача принята»
    await expect(row, "строка вернулась живым кадром").toContainText("В очереди: 1", {
      timeout: 15_000,
    });

    // (4) и она СВЁРНУТА — это и есть сброс на N = 0 (без сброса здесь было бы `true`)
    await expect(row, "строка СВЁРНУТА после N = 0").toHaveAttribute("aria-expanded", "false");
    expect(await page.locator(".dp-queue-panel").count(), "панели в DOM нет").toBe(0);

    // (5) ручное открытие снова работает
    await row.click();
    await expect(page.locator(".dp-queue-panel"), "руками — открывается").toBeVisible();
    await expect(row).toHaveAttribute("aria-expanded", "true");
  });
});
