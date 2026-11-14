import { expect, test, type Page } from "@playwright/test";

// R6 (живая находка владельца 13.11.2026): ПОЛЕ СВОЕГО ОТВЕТА В КАРТОЧКЕ ВОПРОСА И КЛАВИАТУРА.
//
// Что было: `min-height: 44px` у поля — ровно одна строка; в сжатом теле карточки поле складывалось
// в неё, и после закрытия клавиатуры так и оставалось одной строкой. Карточка и стек считали потолок
// по `dvh` (layout-вьюпорт, клавиатурой не меняется), поэтому на 320×603 с клавиатурой верх карточки
// уезжал за кромку (замер до правки: top −20), а телу оставалось 0 — поле не видно вовсе.
//
// Что проверяем здесь: подставной `visualViewport` (клавиатура 300 px снизу) — поле не меньше двух
// строк, тело карточки вмещает поле, ряд действий целиком над клавиатурой, карточка в кадре; после
// «закрытия» клавиатуры ВСЁ восстанавливается до прежних чисел и шапка возвращается.
//
// Числа сняты на этой ревизии (360×800 и 320×603, клавиатура 300).

type Box = { top: number; bottom: number; height: number } | null;

type Shape = {
  viewport: string;
  inset: string;
  keyboard: string | null;
  card: Box;
  head: Box;
  body: Box;
  field: Box;
  actions: Box;
  fieldRows: number;
};

/** Подставной `visualViewport`: настоящий read-only, поэтому подменяем свойство окна целиком. */
async function installFakeViewport(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const fake = new EventTarget() as EventTarget & {
      height: number;
      offsetTop: number;
      scale: number;
      pageTop: number;
    };
    fake.height = window.innerHeight;
    fake.offsetTop = 0;
    fake.scale = 1;
    fake.pageTop = 0;
    Object.defineProperty(window, "visualViewport", { configurable: true, get: () => fake });
    (window as unknown as { __vv: typeof fake }).__vv = fake;
  });
}

/** Клавиатура поднята на `px` (или опущена при 0) — как это делает `visualViewport.resize`. */
async function setKeyboard(page: Page, px: number): Promise<void> {
  await page.evaluate((inset) => {
    const fake = (
      window as unknown as { __vv: { height: number; dispatchEvent: (e: Event) => void } }
    ).__vv;
    fake.height = window.innerHeight - inset;
    fake.dispatchEvent(new Event("resize"));
  }, px);
}

async function shape(page: Page): Promise<Shape> {
  return page.evaluate(() => {
    const box = (selector: string): { top: number; bottom: number; height: number } | null => {
      const node = document.querySelector(selector) as HTMLElement | null;
      if (node === null) return null;
      const rect = node.getBoundingClientRect();
      return {
        top: Math.round(rect.top),
        bottom: Math.round(rect.bottom),
        height: Math.round(rect.height),
      };
    };
    const field = document.querySelector(".dp-question .dp-textarea") as HTMLTextAreaElement | null;
    return {
      viewport: `${window.innerWidth}x${window.innerHeight}`,
      inset: getComputedStyle(document.documentElement).getPropertyValue("--dp-vv-inset").trim(),
      keyboard: document.documentElement.getAttribute("data-keyboard"),
      card: box(".dp-question"),
      head: box(".dp-question-head"),
      body: box(".dp-question-body"),
      field: box(".dp-question .dp-textarea"),
      actions: box(".dp-question-actions"),
      fieldRows: field === null ? 0 : field.rows,
    };
  });
}

test.beforeEach(async ({ page, request }) => {
  await request.post("/__mock/scenario", {
    data: {
      authed: true,
      lang: "ru",
      list: "ok",
      pair: "ok",
      queue: "empty",
      question: "two",
      questionSession: "session-live",
      task: "ok",
    },
  });
  await installFakeViewport(page);
});

// Гигиена стенда: сценарий вопроса — ОБЩИЙ на прогон, и без сброса он протёк бы в следующие файлы
// (`w2-pair` не задаёт `question` и получал карточку вопроса в длинной ленте — ловилось полным прогоном).
test.afterEach(async ({ request }) => {
  await request.post("/__mock/scenario", { data: { question: "none" } });
});

test("клавиатура: поле не меньше двух строк, тело вмещает поле, кнопки целиком над клавиатурой (360 и 320)", async ({
  page,
}) => {
  // Третий случай — большая клавиатура (360 из 603): тела почти нет, и поле проверяется именно
  // на «не сжаться» — оно обязано остаться в две строки, а тело — прокрутиться.
  for (const view of [
    { width: 360, height: 800, kb: 300 },
    { width: 320, height: 603, kb: 300 },
    { width: 320, height: 603, kb: 360 },
  ]) {
    await page.setViewportSize({ width: view.width, height: view.height });
    await page.goto("/?session=session-live");
    await expect(page.locator(".dp-question")).toBeVisible();
    await page.waitForTimeout(300);

    await setKeyboard(page, view.kb);
    // Человек тапает в поле — браузер подкручивает тело к нему (как на телефоне).
    await page.locator(".dp-question .dp-textarea").focus();
    await page.waitForTimeout(300);

    const kb = await shape(page);
    const keyboardTop = view.height - view.kb;
    const where = `${kb.viewport}`;

    expect(kb.inset, `${where}: клавиатура учтена`).toBe(`${view.kb}px`);
    expect(kb.keyboard, `${where}: признак клавиатуры на корне`).toBe("on");

    // 1. Поле — не меньше ДВУХ строк и оно не сжалось: rows=2 и высота ≥ 57 (2 × 19.5 + отбивка/рамки).
    expect(kb.fieldRows, `${where}: rows=2`).toBe(2);
    expect(kb.field!.height, `${where}: поле не меньше двух строк`).toBeGreaterThanOrEqual(57);
    // И правило объявлено, а не «повезло с содержимым»: поле не сжимается как flex-элемент и имеет
    // пол в две строки. Так мутация «вернуть min-height: 44px» ловится даже там, где тело ещё
    // вмещает поле (на телефоне владельца тело было меньше — поле и складывалось в строку).
    const fieldStyle = await page.evaluate(() => {
      const field = document.querySelector(".dp-question .dp-textarea") as HTMLElement;
      const style = getComputedStyle(field);
      return { minHeight: style.minHeight, shrink: style.flexShrink };
    });
    expect(fieldStyle.minHeight, `${where}: пол поля — две строки (объявлено)`).toBe("57px");
    expect(fieldStyle.shrink, `${where}: поле не сжимается`).toBe("0");

    // 2. Карточка ЦЕЛИКОМ в видимой области над клавиатурой (до правки на 320 верх был −20).
    expect(kb.card!.top, `${where}: верх карточки в кадре`).toBeGreaterThanOrEqual(0);
    expect(kb.card!.bottom, `${where}: низ карточки выше клавиатуры`).toBeLessThanOrEqual(
      keyboardTop,
    );

    // 3. Тело вмещает поле целиком (до правки на 320 тело было 0 — поле не видно вовсе).
    // Тело вмещает поле там, где для него есть место (клавиатура 300 px). При 360 из 603 места нет
    // физически: карточка ужимается, тело прокручивается, — но ПОЛЕ ВСЁ РАВНО остаётся в две строки.
    if (view.kb <= 300) {
      expect(kb.body!.height, `${where}: тело не меньше поля`).toBeGreaterThanOrEqual(
        kb.field!.height,
      );
      // Видимая часть поля: тело прокручивается к полю по фокусу, и допуск 8 px — зазор тела,
      // который остаётся над полем, когда тело всего на 8 px выше поля (320 с клавиатурой).
      const visible =
        Math.min(kb.field!.bottom, kb.body!.bottom) - Math.max(kb.field!.top, kb.body!.top);
      expect(
        visible,
        `${where}: поле видно целиком (допуск 8 — зазор тела)`,
      ).toBeGreaterThanOrEqual(kb.field!.height - 8);
    } else {
      expect(kb.body!.height, `${where}: тело ужалось, но не исчезло`).toBeGreaterThan(0);
    }

    // 4. Ряд действий — вне прокрутки и целиком выше клавиатуры: ответить можно, не закрывая её.
    expect(kb.actions!.bottom, `${where}: кнопки выше клавиатуры`).toBeLessThanOrEqual(keyboardTop);
    expect(kb.actions!.top, `${where}: кнопки в карточке`).toBeGreaterThanOrEqual(kb.card!.top);

    // 5. Пока клавиатура поднята, шапка уступает место телу (цена названа в отчёте).
    // `display: none` оставляет узел в DOM с нулевой коробкой — это и есть «уступила место».
    expect(kb.head === null || kb.head.height === 0, `${where}: шапка уступила место телу`).toBe(
      true,
    );
  }
});

test("после закрытия клавиатуры карточка, тело, поле и шапка ВОССТАНАВЛИВАЮТСЯ", async ({
  page,
}) => {
  for (const view of [
    { width: 360, height: 800 },
    { width: 320, height: 603 },
  ]) {
    await page.setViewportSize(view);
    await page.goto("/?session=session-live");
    await expect(page.locator(".dp-question")).toBeVisible();
    await page.waitForTimeout(300);
    const before = await shape(page);
    const where = `${before.viewport}`;
    expect(before.head!.height, `${where}: до клавиатуры шапка на месте`).toBeGreaterThan(0);

    await setKeyboard(page, 300);
    await page.locator(".dp-question .dp-textarea").focus();
    await page.waitForTimeout(300);
    expect((await shape(page)).keyboard, `${where}: клавиатура поднята`).toBe("on");

    await setKeyboard(page, 0);
    await page.locator(".dp-question .dp-textarea").blur();
    await page.waitForTimeout(300);
    const after = await shape(page);

    expect(after.keyboard, `${where}: признак клавиатуры снят`).toBe("off");
    expect(after.inset, `${where}: врезка вернулась к 0`).toBe("0px");
    // Восстановление — теми же числами, что были ДО клавиатуры (допуск 1 px на округление).
    expect(
      Math.abs(after.card!.height - before.card!.height),
      `${where}: карточка восстановилась`,
    ).toBeLessThanOrEqual(1);
    expect(
      Math.abs(after.body!.height - before.body!.height),
      `${where}: тело восстановилось`,
    ).toBeLessThanOrEqual(1);
    expect(
      Math.abs(after.field!.height - before.field!.height),
      `${where}: поле восстановилось`,
    ).toBeLessThanOrEqual(1);
    // И «не сжалось навсегда» абсолютно: карточка выше любого сжатого состояния.
    expect(after.card!.height, `${where}: карточка не осталась сжатой`).toBeGreaterThanOrEqual(
      view.width === 360 ? 400 : 260,
    );
    expect(after.field!.height, `${where}: поле не осталось одной строкой`).toBeGreaterThanOrEqual(
      57,
    );
    // Шапка вернулась (заголовок и пейджер снова в карточке).
    expect(after.head!.height, `${where}: шапка вернулась`).toBeGreaterThan(0);
    await expect(page.locator(".dp-question-count"), `${where}: пейджер снова виден`).toBeVisible();
  }
});
