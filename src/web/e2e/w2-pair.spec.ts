import { expect, test, type Page } from "@playwright/test";

// W2 (D1 §1–§3, приёмка 11.11.2026) + D11 (12.11.2026): пара «вверх/вниз» над НИЖНИМ СТЕКОМ.
//
//   * «вниз» стоит на 60 от видимого низа (запас 52 + зазор 8), «вверх» — над ней (112), 44×44,
//     имя `PhoneWebToTop` («К началу разговора»), видимого слова и счёта нет; порядок разметки = видимому;
//   * «вверх» видна только при scrollY > 0, ведёт к началу разговора и ставит фокус в h1;
//   * ШАРИКА БОЛЬШЕ НЕТ (DEF-148): внизу зацеп шторки 44 высотой, пара стоит ВЫШЕ него, обе кнопки
//     нажимаемы (`elementFromPoint`), а ключ `dsh.phone.ball.pos` не читается и снимается;
//   * зона кнопок — пересечение layout и visual вьюпорта: клавиатура/адресная строка поднимают низ.
//
// `visualViewport` в Playwright не меняется отдельно от layout, поэтому в двух проверках он
// подменён управляемым объектом ДО загрузки страницы (`page.addInitScript`); это единственный
// способ увидеть числа здесь — на настоящем телефоне их снимает L1.

const LOWER_BOTTOM = 60;
const UPPER_BOTTOM = 112;

// ОЧЕРЕДЬ ПУСТАЯ: числа 60/112 считаны для стека «только зацеп» (52). С непустой очередью в стеке
// стоит ещё её строка 44 + зазор 8 (W12), и пара поднимается на те же 52 — это отдельная проверка
// ниже («запас — измеренный: строка очереди поднимает пару ровно на свою высоту»).
test.beforeEach(async ({ request }) => {
  await request.post("/__mock/scenario", {
    data: { authed: true, lang: "ru", list: "ok", pair: "ok", queue: "empty", question: "none" },
  });
});

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
  await page.waitForTimeout(250);
}

/** Уехать от живого конца: тогда видны ОБЕ кнопки пары (нижняя — по порогу допуска). */
async function leaveLiveEnd(page: Page): Promise<void> {
  await page.waitForTimeout(200);
  for (let attempt = 0; attempt < 6; attempt += 1) {
    await page.evaluate((offset) => window.scrollTo(0, offset), attempt);
    if ((await page.locator(".dp-tolast").count()) > 0) return;
    await page.waitForTimeout(150);
  }
  await expect(page.locator(".dp-tolast"), "уехал выше — нижняя кнопка есть").toBeVisible();
}

/** Длинная лента, оба органа пары видны: ушли от низа и от самого верха (scrollY > 0). */
async function showPair(page: Page): Promise<void> {
  await openLong(page);
  await leaveLiveEnd(page);
  await page.evaluate(() => window.scrollTo(0, 300));
  await expect(page.locator(".dp-toup"), "верхняя видна (scrollY > 0)").toBeVisible();
  await expect(page.locator(".dp-tolast"), "нижняя видна (не у низа)").toBeVisible();
}

/** Видимая область подменяется до загрузки: дальше её двигает сама проверка. */
async function useFakeVisualViewport(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const fake = {
      offsetLeft: 0,
      offsetTop: 0,
      width: window.innerWidth,
      height: window.innerHeight,
    };
    const listeners = new Set<() => void>();
    const scrolls = new Set<() => void>();
    const fresh = () => ({
      ...fake,
      addEventListener: (type: string, listener: () => void) => {
        if (type === "resize") listeners.add(listener);
        if (type === "scroll") scrolls.add(listener);
      },
      removeEventListener: (type: string, listener: () => void) => {
        if (type === "resize") listeners.delete(listener);
        if (type === "scroll") scrolls.delete(listener);
      },
    });
    Object.defineProperty(window, "visualViewport", { configurable: true, get: fresh });
    (
      window as unknown as {
        __vv: { set: (patch: Partial<typeof fake>, fire?: boolean) => void; scroll: () => void };
      }
    ).__vv = {
      set: (patch, fire = true) => {
        Object.assign(fake, patch);
        if (fire) for (const listener of [...listeners]) listener();
      },
      // Сдвиг видимой области (без изменения размера) — это `scroll` у visualViewport, не `resize`.
      scroll: () => {
        for (const listener of [...scrolls]) listener();
      },
    };
  });
}

async function setFakeViewport(
  page: Page,
  patch: { offsetLeft?: number; offsetTop?: number; width?: number; height?: number },
  fire = true,
): Promise<void> {
  await page.evaluate(
    ({ values, withEvent }) => {
      (
        window as unknown as {
          __vv: { set: (patch: typeof values, fire?: boolean) => void };
        }
      ).__vv.set(values, withEvent);
    },
    { values: patch, withEvent: fire },
  );
}

async function vvInset(page: Page): Promise<string> {
  return page.evaluate(() =>
    getComputedStyle(document.documentElement).getPropertyValue("--dp-vv-inset").trim(),
  );
}

/** Низ органов от низа окна; нет органа — −1 (нижняя/верхняя видны не всегда). */
async function bottoms(page: Page): Promise<{ down: number; up: number; handle: number }> {
  return page.evaluate(() => {
    const measure = (selector: string): number => {
      const node = document.querySelector(selector) as HTMLElement | null;
      return node === null
        ? -1
        : Math.round(window.innerHeight - node.getBoundingClientRect().bottom);
    };
    return {
      down: measure(".dp-tolast"),
      up: measure(".dp-toup"),
      handle: measure(".dp-handle"),
    };
  });
}

test("пара: «вниз» на 60 от низа, «вверх» 44×44 над ней (112), выше зацепа — без слова и счёта", async ({
  page,
}) => {
  await showPair(page);
  await expect(
    page.locator(".dp-tolast"),
    "нижняя видна — человек выше живого конца",
  ).toBeVisible();

  const info = await page.evaluate(() => {
    const up = document.querySelector(".dp-toup") as HTMLElement;
    const down = document.querySelector(".dp-tolast") as HTMLElement;
    const upBox = up.getBoundingClientRect();
    const downBox = down.getBoundingClientRect();
    const feed = document.querySelector(".dp-feed") as HTMLElement;
    const stack = document.querySelector(".dp-bottom") as HTMLElement;
    const before = (one: Element, other: Element) =>
      (one.compareDocumentPosition(other) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
    const shape = (node: HTMLElement, box: DOMRect) => ({
      width: Math.round(box.width),
      height: Math.round(box.height),
      right: Math.round(window.innerWidth - box.right),
      bottom: Math.round(window.innerHeight - box.bottom),
      cssBottom: getComputedStyle(node).bottom,
      z: getComputedStyle(node).zIndex,
      transition: getComputedStyle(node).transitionDuration,
      name: node.getAttribute("aria-label"),
      text: (node.textContent ?? "").trim(),
      svg: node.querySelectorAll("svg").length,
      badges: node.querySelectorAll(".dp-badge").length,
      status: node.querySelectorAll('[role="status"]').length,
    });
    return {
      up: shape(up, upBox),
      down: shape(down, downBox),
      stack: {
        top: Math.round(stack.getBoundingClientRect().top),
        z: getComputedStyle(stack).zIndex,
      },
      downTop: Math.round(downBox.top),
      upBeforeDown: before(up, down),
      afterFeed: before(feed, up),
      beforeStack: before(up, stack),
    };
  });

  // Числа D11 §1.3: запас снизу 52, нижняя — запас + 8, верхняя — запас + 60.
  expect(info.down.width, "нижняя — цель 44").toBeGreaterThanOrEqual(44);
  expect(info.down.height).toBeGreaterThanOrEqual(44);
  expect(info.down.right, "нижняя: справа 8").toBe(8);
  expect(info.down.bottom, "нижняя: 60 от низа").toBe(LOWER_BOTTOM);
  expect(info.down.cssBottom, "нижняя: bottom = запас + 8").toBe(`${LOWER_BOTTOM}px`);

  expect(info.up.width, "верхняя — 44").toBe(44);
  expect(info.up.height, "верхняя — 44").toBe(44);
  expect(info.up.right, "верхняя: справа 8").toBe(8);
  expect(info.up.bottom, "верхняя: 112 от низа").toBe(UPPER_BOTTOM);
  expect(info.up.cssBottom, "верхняя: bottom = запас + 60").toBe(`${UPPER_BOTTOM}px`);
  expect(info.up.bottom - info.down.bottom, "верхняя ровно над нижней: 44 + зазор 8").toBe(52);
  expect(info.up.name, "имя верхней — слово словаря").toBe("К началу разговора");
  expect(info.up.text, "видимого слова нет").toBe("");
  expect(info.up.svg, "знак — иконка").toBe(1);
  expect(info.up.badges, "счёта у верхней нет").toBe(0);
  expect(info.up.status, "status-объявления у верхней нет").toBe(0);
  expect(info.up.z, "слой 2 — ниже нижнего стека (10) и листа (40)").toBe("2");
  expect(info.up.transition, "движения нет").toBe("0s");
  expect(info.upBeforeDown, "порядок разметки: верхняя → нижняя").toBe(true);
  expect(info.afterFeed, "обе — после ленты").toBe(true);

  // Пара стоит ВЫШЕ нижнего стека (D11 §1.1: порядок задаёт разметка, перекрывать нечего).
  expect(info.beforeStack, "обе — в разметке перед нижним стеком").toBe(true);
  expect(info.stack.z, "слой стека — 10").toBe("10");
  expect(info.downTop, "низ нижней кнопки выше верха зацепа").toBeLessThan(info.stack.top);
});

test("«вверх» видна при scrollY > 0, ведёт к началу разговора и ставит фокус в h1 — движения нет", async ({
  page,
}) => {
  await openLong(page);
  await leaveLiveEnd(page);
  await page.evaluate(() => window.scrollTo(0, 600));
  const up = page.getByRole("button", { name: "К началу разговора" });
  await expect(up, "прокрутили вниз — верхняя появилась").toBeVisible();

  const motion = await page.evaluate(() => ({
    transition: getComputedStyle(document.querySelector(".dp-toup") as HTMLElement)
      .transitionDuration,
    behavior: getComputedStyle(document.documentElement).scrollBehavior,
  }));
  expect(motion.transition, "никакой анимации").toBe("0s");
  expect(motion.behavior, "прыжок мгновенный, не smooth").toBe("auto");

  await up.click();
  await expect
    .poll(() => page.evaluate(() => Math.round(window.scrollY)), {
      message: "прокрутка в самое начало",
    })
    .toBe(0);
  await expect(page.getByRole("heading", { level: 1 }), "фокус — в заголовке").toBeFocused();
  await expect(page.locator(".dp-toup"), "на верху верхняя исчезает (scrollY = 0)").toHaveCount(0);
});

test("пара выше зацепа: зацеп не накрывает кнопки, обе нажимаемы", async ({ page }) => {
  await showPair(page);

  const hits = await page.evaluate(() => {
    const inside = (node: Element | null, parent: Element) =>
      node !== null && (node === parent || parent.contains(node));
    const up = document.querySelector(".dp-toup") as HTMLElement;
    const down = document.querySelector(".dp-tolast") as HTMLElement;
    const handle = document.querySelector(".dp-handle") as HTMLElement;
    const center = (node: HTMLElement) => {
      const box = node.getBoundingClientRect();
      return { x: box.left + box.width / 2, y: box.top + box.height / 2 };
    };
    const overlaps = (one: DOMRect, other: DOMRect) =>
      !(
        one.right <= other.left ||
        one.left >= other.right ||
        one.bottom <= other.top ||
        one.top >= other.bottom
      );
    const upBox = up.getBoundingClientRect();
    const downBox = down.getBoundingClientRect();
    const handleBox = handle.getBoundingClientRect();
    return {
      up: inside(document.elementFromPoint(center(up).x, center(up).y), up),
      down: inside(document.elementFromPoint(center(down).x, center(down).y), down),
      handleOverDown: overlaps(handleBox, downBox),
      handleOverUp: overlaps(handleBox, upBox),
      handleHeight: Math.round(handleBox.height),
    };
  });

  expect(hits.up, "верхняя кнопка нажимаема").toBe(true);
  expect(hits.down, "нижняя кнопка нажимаема").toBe(true);
  expect(hits.handleOverDown, "зацеп и нижняя не пересекаются").toBe(false);
  expect(hits.handleOverUp, "зацеп и верхняя не пересекаются").toBe(false);
  expect(hits.handleHeight, "зацеп — цель 44").toBeGreaterThanOrEqual(44);
});

test("запись убранного шарика не читается и снимается при монтировании", async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem("dsh.phone.ball.pos", JSON.stringify({ v: 1, x: 1, y: 0.78 }));
  });
  await page.goto("/?session=session-live");
  await expect(page.locator(".dp-handle")).toBeVisible();
  // Ключ снят при монтировании экрана (D11 §2.3): позиции шарика больше нет ни в хранилище, ни в DOM.
  expect(await page.evaluate(() => localStorage.getItem("dsh.phone.ball.pos"))).toBeNull();
  expect(await page.locator(".dp-input-ball").count(), "шарика нет").toBe(0);
  expect(await page.locator(".dp-ball-layer").count(), "слоя шарика нет").toBe(0);
});

test("запас — ИЗМЕРЕННЫЙ: строка очереди поднимает пару ровно на свою высоту", async ({
  page,
  request,
}) => {
  // Пустая очередь — стек 52, пара на 60/112.
  await showPair(page);
  const empty = await bottoms(page);
  expect(empty.down, "без очереди — 60").toBe(LOWER_BOTTOM);
  expect(empty.up, "без очереди — 112").toBe(UPPER_BOTTOM);

  // Появилась очередь (W12): в стеке встала её карточка — строка 44 + две рамки 1 + зазор 8 = +54.
  await request.post("/__mock/scenario", { data: { queue: "ok" } });
  await page.reload();
  await showPair(page);
  await expect(page.locator(".dp-queue-row"), "строка очереди в стеке").toBeVisible();
  await expect
    .poll(
      () =>
        page.evaluate(() =>
          getComputedStyle(document.documentElement).getPropertyValue("--dp-bottom-reserve").trim(),
        ),
      { message: "запас вырос на карточку очереди" },
    )
    .toBe("106px");
  const withQueue = await bottoms(page);
  expect(withQueue.down - empty.down, "нижняя поднялась ровно на карточку очереди").toBe(54);
  expect(withQueue.up - empty.up, "верхняя — вместе с ней").toBe(54);
  expect(withQueue.handle, "зацеп всё равно прибит к низу").toBe(0);
});

test("visualViewport: кнопки держатся за видимый низ, прокрутка их не двигает", async ({
  page,
}) => {
  await useFakeVisualViewport(page);
  await showPair(page);
  const before = await bottoms(page);
  expect(before.down, "без клавиатуры — 60").toBe(LOWER_BOTTOM);
  expect(before.up, "без клавиатуры — 112").toBe(UPPER_BOTTOM);
  expect(before.handle, "зацеп прибит к низу").toBe(0);

  // «Клавиатура»: видимая высота меньше layout на 300 — низ поднимается на 300.
  await setFakeViewport(page, { height: await page.evaluate(() => window.innerHeight - 300) });
  await expect.poll(() => vvInset(page), { message: "--dp-vv-inset = 300px" }).toBe("300px");
  await expect
    .poll(async () => (await bottoms(page)).down, { message: "нижняя поднялась с видимым низом" })
    .toBe(LOWER_BOTTOM + 300);
  const withKeyboard = await bottoms(page);
  expect(withKeyboard.up, "верхняя тоже поднялась").toBe(UPPER_BOTTOM + 300);
  expect(withKeyboard.handle, "зацеп — над видимым низом").toBe(300);

  // Прокрутка ленты: и зацеп, и кнопки стоят относительно экрана (ни одного слушателя прокрутки).
  const handleTop = await page.evaluate(() =>
    Math.round((document.querySelector(".dp-handle") as HTMLElement).getBoundingClientRect().top),
  );
  await page.evaluate(() => window.scrollTo(0, 300));
  await page.waitForTimeout(120);
  expect(
    await page.evaluate(() =>
      Math.round((document.querySelector(".dp-handle") as HTMLElement).getBoundingClientRect().top),
    ),
    "прокрутка не двигает зацеп",
  ).toBe(handleTop);
  expect((await bottoms(page)).down, "и кнопку не двигает").toBe(LOWER_BOTTOM + 300);

  // «Клавиатура закрылась»: низ возвращается — кнопки и зацеп снова на прежних числах.
  await setFakeViewport(page, {
    height: await page.evaluate(() => window.innerHeight),
  });
  await expect.poll(() => vvInset(page), { message: "inset вернулся в 0" }).toBe("0px");
  const restored = await bottoms(page);
  expect(restored.down).toBe(LOWER_BOTTOM);
  expect(restored.up).toBe(UPPER_BOTTOM);
  expect(restored.handle).toBe(0);
});

test("после окна ввода пара и зацеп стоят на НОВОМ видимом низу", async ({ page }) => {
  await useFakeVisualViewport(page);
  await page.goto("/?session=session-live");
  await expect(page.locator(".dp-handle")).toBeVisible();
  expect((await bottoms(page)).handle, "до листа — у низа").toBe(0);

  // Поднимаем лист, «показываем клавиатуру», закрываем лист: зацеп появляется на новой зоне.
  await page.locator(".dp-handle").click();
  await expect(page.locator(".dp-sheet")).toBeVisible();
  await setFakeViewport(page, { height: await page.evaluate(() => window.innerHeight - 300) });
  await page.keyboard.press("Escape");
  await expect(page.locator(".dp-sheet")).toHaveCount(0);
  await expect(page.locator(".dp-handle")).toBeVisible();
  await expect
    .poll(async () => (await bottoms(page)).handle, { message: "зацеп у НОВОГО видимого низа" })
    .toBe(300);
  // Верх стека и верх зацепа не разошлись: пара и зацеп поднялись одним низом.
  const geometry = await page.evaluate(() => ({
    stackTop: Math.round(
      (document.querySelector(".dp-bottom") as HTMLElement).getBoundingClientRect().top,
    ),
    handleTop: Math.round(
      (document.querySelector(".dp-handle") as HTMLElement).getBoundingClientRect().top,
    ),
  }));
  expect(geometry.stackTop, "верх стека = верх зацепа (у стека только отбивка 8)").toBe(
    geometry.handleTop - 8,
  );
});
