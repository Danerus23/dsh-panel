import { expect, test, type Page } from "@playwright/test";

// W4 (D3 §1, §3, слово владельца 11.11.2026: «состояние не живое… хотелось бы анимацию»):
//
//   * состояние в шапке сессии считается ИЗ ЖИВЫХ ДАННЫХ, а не из строки списка: вопрос → «Ждёт
//     ответа», живой поток + ход → «Работает», записи/черновик → «Закончила», пусто → «Ещё не было
//     задачи»; пока поток не живой — слово из строки, кроме «Работает»;
//   * значок «Работает» вращается классами Semi (`semi-icon-spinning`, 600 мс linear infinite),
//     вращается ТОЛЬКО в шапке сессии; в списке значок статичен;
//   * `prefers-reduced-motion: reduce` гасит анимацию уже существующим общим сторожем, слово
//     остаётся; значок — `aria-hidden` и не цель нажатия.

function headerMeta(page: Page) {
  return page.locator(".dp-head-session .dp-meta");
}

function runningMark(page: Page) {
  return page.locator(".dp-head-session .dp-state-mark.dp-state-accent");
}

/** Значок «Работает»: Semi рисует ОБЁРТКУ `<span class="semi-icon">` — на ней класс вращения. */
function runningIcon(page: Page) {
  return runningMark(page).locator(".semi-icon");
}

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/scenario", {
    data: { authed: true, lang: "ru", list: "ok", pair: "ok", question: "none", queue: "ok" },
  });
});

test("шапка живая: «Работает» → «Закончила» без перезагрузки, значок крутится классами Semi", async ({
  page,
}) => {
  await page.goto("/?session=session-live");
  const meta = headerMeta(page);
  await expect(meta, "поток живой, turn без turn-end — Работает").toContainText("Работает");

  const mark = runningMark(page);
  await expect(mark).toHaveCount(1);
  const icon = runningIcon(page);
  await expect(icon, "класс вращения — из Semi").toHaveClass(/semi-icon-spinning/);

  const animation = await icon.evaluate((node) => {
    const style = getComputedStyle(node);
    return {
      name: style.animationName,
      duration: style.animationDuration,
      timing: style.animationTimingFunction,
      iteration: style.animationIterationCount,
    };
  });
  // Числа — САМОГО Semi (semi.css: `.semi-icon-spinning` = 0.6s linear infinite), своего нет.
  expect(animation).toEqual({
    name: "semi-icon-animation-rotate",
    duration: "0.6s",
    timing: "linear",
    iteration: "infinite",
  });

  // Конец хода приезжает кадром живого потока — слово меняется САМО, без перезагрузки и без
  // возврата в список (никакого page.reload() в проверке нет).
  await expect(meta, "turn-end — Закончила").toContainText("Закончила", { timeout: 8_000 });
  await expect(runningMark(page), "вращающегося значка больше нет").toHaveCount(0);
});

test("строка списка не источник: список говорит «Закончила», а в сессии идёт ход — шапка «Работает»", async ({
  page,
  request,
}) => {
  await request.post("/__mock/scenario", { data: { list: "quietRoot" } });

  await page.goto("/");
  const row = page.getByRole("link", { name: /Длинная сессия/ });
  await expect(row, "строка списка — «Закончила»").toContainText("Закончила");

  await row.click();
  await expect(page).toHaveURL(/session=session-many/u);
  await expect(headerMeta(page), "живой ход сильнее строки списка").toContainText("Работает");
});

test("в списке значок «Работает» вращается (D7), у остальных тонов — нет", async ({ page }) => {
  await page.goto("/");
  const row = page.getByRole("link", { name: /Починить калитку/ });
  await expect(row).toContainText("Работает");

  // D7 §2 А: список живой (W5), слово отстаёт ≤3 с — знак отстаёт вместе с ним.
  const icon = row.locator(".dp-state-mark.dp-state-accent .semi-icon");
  await expect(icon, "spin передан списку").toHaveClass(/semi-icon-spinning/);
  expect(await icon.evaluate((node) => getComputedStyle(node).animationName)).toBe(
    "semi-icon-animation-rotate",
  );

  // У остальных тонов (вопрос/пусто/закончила) — знак стоит.
  for (const name of ["Собрать список сессий", "Обновление панели"]) {
    const other = page.getByRole("link", { name });
    if ((await other.count()) > 0) {
      expect(
        await other
          .locator(".dp-row-meta .semi-icon")
          .first()
          .evaluate((node) => getComputedStyle(node).animationName),
      ).not.toBe("semi-icon-animation-rotate");
    }
  }
});

test("строки списка ещё нет: «Закончила»/«Ещё не было задачи» не печатаем", async ({ page }) => {
  // session-created-1 в списке по умолчанию НЕТ: живой поток пуст, но без строки «пусто» — это не
  // «Ещё не было задачи», а «последнее неизвестно». Печатать можно только то, что даёт поток.
  await page.goto("/?session=session-created-1");
  await expect(page.locator(".dp-head-session h1")).not.toContainText("Подключаюсь");
  await expect(headerMeta(page), "без строки мета молчит").toHaveCount(0);
  await page.waitForTimeout(400);
  await expect(headerMeta(page)).toHaveCount(0);
});

test("пустая лента при известной строке — «Ещё не было задачи»", async ({ page, request }) => {
  // quietRoot даёт строку session-created-1 (running:false): строка есть, лента пуста — «пусто».
  await request.post("/__mock/scenario", { data: { list: "quietRoot" } });
  await page.goto("/?session=session-created-1");
  await expect(headerMeta(page)).toContainText("Ещё не было задачи");
});

test("вопрос этой сессии — «Ждёт ответа» даже поверх живого хода", async ({ page, request }) => {
  await request.post("/__mock/scenario", {
    data: { question: "single", questionSession: "session-live" },
  });

  await page.goto("/?session=session-live");
  await expect(headerMeta(page)).toContainText("Ждёт ответа");
});

test("вращение: 360 и 320, квадрат, раскладка не двигается, цели нажатия нет", async ({
  browser,
  request,
}) => {
  for (const width of [360, 320] as const) {
    const context = await browser.newContext({ viewport: { width, height: 800 } });
    const page = await context.newPage();
    await request.post("/__mock/scenario", { data: { list: "quietRoot" } });
    await page.goto("/?session=session-many");

    const mark = runningMark(page);
    await expect(mark, `${width}: шапка «Работает»`).toHaveCount(1);

    const shape = await mark.evaluate((node) => {
      const icon = node.querySelector(".semi-icon") as HTMLElement;
      const svg = icon.querySelector("svg") as SVGSVGElement;
      const box = svg.getBoundingClientRect();
      return {
        hidden: node.getAttribute("aria-hidden"),
        round: [Math.round(box.width), Math.round(box.height)],
        offset: [icon.offsetWidth, icon.offsetHeight],
        markDisplay: getComputedStyle(node as HTMLElement).display,
        tabindex: icon.getAttribute("tabindex"),
        inTarget: icon.closest("button,a") !== null,
        markHeight: (node as HTMLElement).offsetHeight,
        metaHeight: (node.parentElement as HTMLElement).offsetHeight,
      };
    });

    expect(shape.hidden, "значок скрыт от читалки: слово рядом").toBe("true");
    expect(shape.round[0], "значок — квадрат").toBe(shape.round[1]);
    expect(shape.offset[0], "квадрат по layout (transform не меняет меру)").toBe(shape.offset[1]);
    expect(shape.markDisplay).toBe("inline-flex");
    expect(shape.tabindex, "цель нажатия не создаётся").toBeNull();
    expect(shape.inTarget, "не внутри ссылки/кнопки").toBe(false);

    // Через 150 мс (часть оборота) меры те же: вращается transform, а не размеры.
    await page.waitForTimeout(150);
    const later = await mark.evaluate((node) => ({
      markHeight: (node as HTMLElement).offsetHeight,
      metaHeight: (node.parentElement as HTMLElement).offsetHeight,
      offset: [
        (node.querySelector(".semi-icon") as HTMLElement).offsetWidth,
        (node.querySelector(".semi-icon") as HTMLElement).offsetHeight,
      ],
    }));
    expect(later, `${width}: раскладка не двигается`).toEqual({
      markHeight: shape.markHeight,
      metaHeight: shape.metaHeight,
      offset: shape.offset,
    });

    await context.close();
  }
});

test("prefers-reduced-motion: анимации нет, слово остаётся текстом", async ({
  browser,
  request,
}) => {
  const context = await browser.newContext({
    viewport: { width: 360, height: 800 },
    reducedMotion: "reduce",
  });
  const page = await context.newPage();
  await request.post("/__mock/scenario", { data: { list: "quietRoot" } });
  await page.goto("/?session=session-many");

  const meta = headerMeta(page);
  await expect(meta).toContainText("Работает");
  const icon = meta.locator(".dp-state-accent .semi-icon");
  await expect(icon, "класс Semi на месте (проп передан)").toHaveClass(/semi-icon-spinning/);

  const animation = await icon.evaluate((node) => {
    const style = getComputedStyle(node);
    return { name: style.animationName, duration: style.animationDuration };
  });
  // Общий сторож `app.css` (`* { animation: none !important }`) — единственный у Semi: своего
  // сторожа у него нет. Слово при этом остаётся: состояние понятно без движения.
  expect(animation).toEqual({ name: "none", duration: "0s" });
  await expect(meta).toContainText("Работает");

  await context.close();
});
