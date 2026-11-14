import { expect, test, type Page } from "@playwright/test";

// W11 (D11 §1–§2, приёмка 12.11.2026, DEF-148): ШАРИКА НЕТ — свёрнутое состояние это зацеп.
//
// Что доказывается прогоном:
//   1. шарика нет ни в DOM, ни в переменных, ни в хранилище (ключ снят при монтировании) — и запас
//      снизу равен ФАКТИЧЕСКОЙ высоте нижнего стека (52 = 44 зацепа + 8 зазора), а не прежним 88;
//   2. тап по зацепу открывает лист (`aria-controls` указывает на него);
//   3. свайп ВВЕРХ ≥ 24 px открывает лист, а 20 px и горизонтальный жест — НЕТ (порог — наше число);
//   4. свайп ВНИЗ ≥ 64 px по верхней полосе листа и тап по «свернуть» закрывают его; 30 px — нет.
//
// Жест вверх проверяется синтетическими `pointerdown`/`pointerup` (без `click`): иначе «открылось»
// нельзя отличить от обычного нажатия, и порог остался бы непроверенным. Вниз — настоящей мышью:
// там `click` после свайпа гасится самим органом, и это тоже часть правила.

/** Потолок/пол запаса снизу по D11 §1.2: 44 зацепа + 8 зазора. */
const RESERVE = 52;

/** Открыть сессию с живой лентой и дождаться зацепа. */
async function openSession(page: Page): Promise<void> {
  await page.goto("/?session=session-live");
  await expect(page.locator(".dp-handle")).toBeVisible();
}

/** Свайп по зацепу без `click`: только pointer-события (так проверяется сам жест и его порог). */
async function swipe(page: Page, dy: number, dx = 0): Promise<void> {
  await page.evaluate(
    ({ moveY, moveX }) => {
      const node = document.querySelector(".dp-handle") as HTMLElement;
      const box = node.getBoundingClientRect();
      const x = Math.round(box.left + box.width / 2);
      const y = Math.round(box.top + box.height / 2);
      const fire = (type: string, atY: number, atX: number) =>
        node.dispatchEvent(
          new PointerEvent(type, {
            bubbles: true,
            cancelable: true,
            pointerId: 7,
            pointerType: "touch",
            isPrimary: true,
            clientX: atX,
            clientY: atY,
          }),
        );
      fire("pointerdown", y, x);
      fire("pointerup", y + moveY, x + moveX);
    },
    { moveY: dy, moveX: dx },
  );
}

type Box = { left: number; right: number; top: number; bottom: number } | null;

/** Пересечение прямоугольников: `true` — карточки накрывают друг друга. */
function overlaps(one: Box, other: Box): boolean {
  return (
    one !== null &&
    other !== null &&
    !(
      one.right <= other.left ||
      one.left >= other.right ||
      one.bottom <= other.top ||
      one.top >= other.bottom
    )
  );
}

test.describe("W11: зацеп вместо шарика", () => {
  test.beforeEach(async ({ request }) => {
    await request.post("/__mock/scenario", {
      data: {
        authed: true,
        lang: "ru",
        list: "ok",
        pair: "ok",
        question: "none",
        task: "ok",
        // Очередь ПУСТА: стек — только зацеп (44 + 8), и запас 52 читается буквально. С непустой
        // очередью в стеке стоит ещё и её строка 44 (W12) — это отдельная проверка в w12-queue.spec.
        queue: "empty",
        remove: "ok",
      },
    });
  });

  test("шарика нет: ни компонента, ни слоя, ни переменной, ни ключа в хранилище", async ({
    page,
  }) => {
    // Запись прежнего шарика лежит в хранилище ДО загрузки: экран обязан её снять (D11 §2.3).
    await page.addInitScript(() => {
      localStorage.setItem("dsh.phone.ball.pos", JSON.stringify({ v: 1, x: 1, y: 0.78 }));
    });
    await openSession(page);

    const shape = await page.evaluate(() => {
      const root = getComputedStyle(document.documentElement);
      const stack = document.querySelector(".dp-bottom") as HTMLElement;
      const handle = document.querySelector(".dp-handle") as HTMLElement;
      return {
        balls: document.querySelectorAll(".dp-input-ball").length,
        layers: document.querySelectorAll(".dp-ball-layer").length,
        grips: document.querySelectorAll(".dp-input-ball-grip").length,
        chevrons: document.querySelectorAll(".dp-input-ball-chevron").length,
        reserveVar: root.getPropertyValue("--dp-ball-reserve").trim(),
        newReserveVar: root.getPropertyValue("--dp-bottom-reserve").trim(),
        stackHeight: Math.round(stack.getBoundingClientRect().height),
        handleHeight: Math.round(handle.getBoundingClientRect().height),
        stored: localStorage.getItem("dsh.phone.ball.pos"),
      };
    });

    expect(shape.balls, "шарика нет в разметке").toBe(0);
    expect(shape.layers, "слоя шарика нет").toBe(0);
    expect(shape.grips, "ручки шарика нет").toBe(0);
    expect(shape.chevrons, "шеврона шарика нет").toBe(0);
    expect(shape.reserveVar, "переменной шарика больше нет").toBe("");
    expect(shape.stored, "ключ шарика снят при монтировании").toBeNull();

    // Запас снизу честный: измерение = фактическая высота стека, а не прежние 88 (D11 §1.2).
    expect(shape.handleHeight, "зацеп — цель 44").toBeGreaterThanOrEqual(44);
    expect(shape.stackHeight, "стек = зацеп 44 + зазор 8").toBe(RESERVE);
    expect(shape.newReserveVar, "запас снизу — измеренное число").toBe(`${RESERVE}px`);
  });

  test("видимая полоса зацепа — 44 НАД safe-area, центр выше зоны жеста; шевроны 18×18", async ({
    page,
    request,
  }) => {
    // L3 (AVD, R2): видимой полосы было 33 CSS, а не 44 — `min-height: 44px` при border-box вбирал
    // `padding-bottom: 4 + env(24)`, и центр элемента попадал в системную зону жеста «домой» (свайп
    // оттуда уводил Chrome с экрана). В Edge `env()` = 0, поэтому зона подставляется переменной
    // `--dp-safe-bottom` — тем же входом, который в бою кормит `env(safe-area-inset-bottom)`.
    await request.post("/__mock/scenario", { data: { queue: "ok" } });
    await openSession(page);
    await expect(page.locator(".dp-queue-row"), "строка очереди нужна для её шеврона").toBeVisible({
      timeout: 8_000,
    });

    const metrics = () =>
      page.evaluate(() => {
        const handle = document.querySelector(".dp-handle") as HTMLElement;
        const chevron = handle.querySelector(".dp-handle-chevron") as HTMLElement;
        const svg = chevron.querySelector("svg");
        const queueChevron = document.querySelector(".dp-queue-chevron") as HTMLElement | null;
        const style = getComputedStyle(handle);
        const safe = Number.parseFloat(style.getPropertyValue("--dp-safe-bottom")) || 0;
        const box = handle.getBoundingClientRect();
        const cbox = chevron.getBoundingClientRect();
        const sbox = svg === null ? null : svg.getBoundingClientRect();
        const round = (value: number) => Math.round(value * 10) / 10;
        return {
          safe,
          height: round(box.height),
          visible: round(box.height - safe),
          bottom: Math.round(box.bottom),
          viewport: window.innerHeight,
          centerAboveGesture: box.height / 2 > safe,
          chevron: [Math.round(cbox.width), Math.round(cbox.height)],
          svg: sbox === null ? null : [Math.round(sbox.width), Math.round(sbox.height)],
          queueChevron:
            queueChevron === null
              ? null
              : [
                  Math.round(queueChevron.getBoundingClientRect().width),
                  Math.round(queueChevron.getBoundingClientRect().height),
                ],
        };
      });

    // 1) Рабочий стол: безопасной зоны нет — видимая полоса и есть высота, ровно 44.
    const flat = await metrics();
    expect(flat.safe, "на рабочем столе зоны нет").toBe(0);
    expect(flat.height, "полоса — 44").toBe(44);
    expect(flat.visible, "видимая полоса — 44").toBe(44);
    expect(flat.bottom, "полоса прижата к низу окна").toBe(flat.viewport);

    // 2) Телефон: safe-area 24 (как на AVD) — элемент 68, ВИДИМАЯ часть по-прежнему 44,
    //    а центр (34) выше зоны жеста (24). До правки видно было 33, и центр сидел в зоне (L3 F1).
    await page.evaluate(() => {
      const handle = document.querySelector(".dp-handle") as HTMLElement;
      handle.style.setProperty("--dp-safe-bottom", "24px");
    });
    const phone = await metrics();
    expect(phone.safe, "подставили safe-area 24").toBe(24);
    expect(phone.height, "элемент 44 + 24 = 68").toBe(68);
    expect(phone.visible, "видимая полоса над зоной — 44, а не 33").toBe(44);
    expect(phone.centerAboveGesture, "центр выше зоны жеста").toBe(true);
    expect(phone.bottom, "и полоса всё та же у низа окна").toBe(phone.viewport);

    // 3) Шевроны — 18×18 у зацепа и в строке очереди (L3 F2: Semi перебивал `font-size`, было 16×16).
    expect(flat.chevron, "шеврон зацепа 18×18").toEqual([18, 18]);
    expect(flat.svg, "и сама картинка 18×18").toEqual([18, 18]);
    expect(flat.queueChevron, "шеврон строки очереди 18×18").toEqual([18, 18]);
  });

  test("строка исхода не накрывает пару «вверх/вниз»: справа она оставляет её место", async ({
    page,
  }) => {
    // ДЛИННАЯ лента + КАРТОЧКА ВОПРОСА: страница заведомо длиннее окна на много сотен пикселей —
    // пара появляется только когда лента не в низах (допуск «внизу» — 152 px).
    await page.setViewportSize({ width: 360, height: 720 });
    await page.request.post("/__mock/scenario", {
      data: { question: "single", questionSession: "session-long" },
    });
    await page.goto("/?session=session-long");
    await expect(page.locator(".dp-feed li").last()).toBeVisible();
    await expect(page.locator(".dp-question").first(), "карточка вопроса в потоке").toBeVisible();
    const room = await page.evaluate(
      () => document.documentElement.scrollHeight - window.innerHeight,
    );
    expect(room, "страница заведомо длиннее окна").toBeGreaterThan(400);
    // Пару делаем ВИДИМОЙ: уходим от живого конца (допуск «внизу» — 152 px). Если экран по какой-то
    // причине ещё не признал отъезд, проверка всё равно не станет пустой — ниже есть ГЕОМЕТРИЯ,
    // которая не зависит от прокрутки вовсе (строка исхода оставляет полосу пары справа).
    await page.evaluate(() => window.scrollTo(0, 300));
    const pairShown =
      (await page
        .locator(".dp-tolast")
        .count()
        .catch(() => 0)) > 0;

    // Отправка задачи: строка исхода «Задача принята.» появляется НАД зацепом.
    await page.locator(".dp-handle").click();
    await expect(page.locator(".dp-sheet")).toBeVisible();
    await page.locator(".dp-sheet textarea").fill("Проверь очередь");
    await page.locator(".dp-sheet").getByRole("button", { name: "Отправить задачу" }).click();
    const note = page.locator(".dp-handle-note");
    await expect(note, "успех — строкой над зацепом").toContainText("Задача принята.");

    const boxes = await page.evaluate(() => {
      const rect = (selector: string) => {
        const node = document.querySelector(selector) as HTMLElement | null;
        if (node === null) return null;
        const box = node.getBoundingClientRect();
        return {
          left: Math.round(box.left),
          right: Math.round(box.right),
          top: Math.round(box.top),
          bottom: Math.round(box.bottom),
        };
      };
      return {
        note: rect(".dp-handle-note"),
        down: rect(".dp-tolast"),
        up: rect(".dp-toup"),
        width: window.innerWidth,
        height: window.innerHeight,
        reserve: Number.parseFloat(
          getComputedStyle(document.documentElement).getPropertyValue("--dp-bottom-reserve"),
        ),
      };
    });

    expect(boxes.note, "строка исхода на месте").not.toBeNull();
    // ГЕОМЕТРИЯ (не зависит от прокрутки): полоса пары — справа 8..52. Строка исхода обязана
    // оставаться ЛЕВЕЕ её: 8 (край) + 44 (кнопка) + 8 (зазор) = 60 px от правого края.
    expect(boxes.note!.right, "строка исхода оставляет полосу пары справа").toBeLessThanOrEqual(
      boxes.width - 60,
    );
    if (boxes.down !== null) {
      expect(boxes.note!.right, "строка исхода не доходит до нижней кнопки").toBeLessThanOrEqual(
        boxes.down.left - 8,
      );
      expect(overlaps(boxes.note, boxes.down), "поверх нижней кнопки карточки нет").toBe(false);
    }
    if (boxes.up !== null) {
      expect(overlaps(boxes.note, boxes.up), "поверх верхней кнопки карточки нет").toBe(false);
    }
  });

  test("тап по зацепу открывает лист, и `aria-controls` указывает на него", async ({ page }) => {
    await openSession(page);
    const controls = await page.locator(".dp-handle").getAttribute("aria-controls");
    expect(controls, "зацеп называет свой лист").toBeTruthy();
    expect(await page.locator(".dp-handle").getAttribute("aria-expanded")).toBe("false");

    await page.locator(".dp-handle").click();
    await expect(page.locator(".dp-sheet"), "тап открыл лист").toBeVisible();
    expect(await page.locator(".dp-sheet").getAttribute("id"), "тот самый лист").toBe(controls);
    await expect(page.locator(".dp-sheet textarea"), "фокус в поле").toBeFocused();
    await expect(page.locator(".dp-handle"), "пока лист поднят, зацепа нет").toHaveCount(0);
  });

  test("свайп вверх ≥ 24 px открывает лист; 20 px и горизонтальный жест — нет", async ({
    page,
  }) => {
    await openSession(page);

    // 20 px — меньше порога: лист не открывается (и это не нажатие: `click` не приходит).
    await swipe(page, -20);
    await page.waitForTimeout(150);
    expect(await page.locator(".dp-sheet").count(), "20 px — ниже порога").toBe(0);

    // Горизонтальный жест (dy −40, dx 60): лист не открывается — жест не наш (D11 §2.2).
    await swipe(page, -40, 60);
    await page.waitForTimeout(150);
    expect(await page.locator(".dp-sheet").count(), "горизонтальный жест лист не открывает").toBe(
      0,
    );

    // 40 px вверх — открывает.
    await swipe(page, -40);
    await expect(page.locator(".dp-sheet"), "свайп вверх открыл лист").toBeVisible();

    // И настоящей мышью тоже: жест — второй путь, первый — видимый зацеп.
    await page.keyboard.press("Escape");
    await expect(page.locator(".dp-sheet")).toHaveCount(0);
    const handle = page.locator(".dp-handle");
    await expect(handle).toBeVisible();
    const box = (await handle.boundingBox())!;
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2 - 40, { steps: 8 });
    await page.mouse.up();
    await expect(page.locator(".dp-sheet"), "свайп мышью открыл лист").toBeVisible();
  });

  test("свайп вниз ≥ 64 px по верхней полосе закрывает лист; 30 px — нет; тап по «свернуть» — да", async ({
    page,
  }) => {
    await openSession(page);
    const openSheet = async () => {
      await page.locator(".dp-handle").click();
      await expect(page.locator(".dp-sheet")).toBeVisible();
      // Въезд листа — 200 мс (W6). Жест по ЕЩЁ ДВИЖУЩЕМУСЯ листу ловит анимацию, и drag тонет:
      // без этой паузы проверка была плавающей в общем прогоне (R3, ловилось на полном e2e).
      await page.waitForTimeout(250);
    };
    const dragDown = async (distance: number) => {
      const grip = page.locator(".dp-sheet-collapse");
      const box = (await grip.boundingBox())!;
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.mouse.down();
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2 + distance, {
        steps: 10,
      });
      await page.mouse.up();
    };

    await openSheet();
    // Половина порога: лист остаётся (и `click` после свайпа его не закрывает — это свайп, не нажатие).
    await dragDown(30);
    await page.waitForTimeout(250);
    await expect(page.locator(".dp-sheet"), "30 px — ниже порога, лист на месте").toBeVisible();
    await expect(page.locator(".dp-sheet-collapse"), "лист вернулся на место").toHaveCSS(
      "translate",
      "none",
    );

    // Порог: 80 px — лист закрывается тем же выездом 150 мс.
    await dragDown(80);
    await expect(page.locator(".dp-sheet"), "свайп вниз закрыл лист").toHaveCount(0);
    await expect(page.locator(".dp-handle"), "зацеп вернулся, и фокус на нём").toBeFocused();

    // Тап по «свернуть» закрывает лист, как и раньше.
    await openSheet();
    await page.locator(".dp-sheet-collapse").click();
    await expect(page.locator(".dp-sheet"), "тап по «свернуть» закрыл лист").toHaveCount(0);
  });
});
