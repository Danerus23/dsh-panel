import { expect, test, type Page } from "@playwright/test";

// W6 (D4 + D7): движение мобильного слоя.
//
//   * смена экрана: 200 мс, проявление + 16 px по иерархии «вперёд/назад», ОДНА поверхность;
//   * порядок путей: reduce → API → запасной CSS (150 мс, 8 px, только входящий экран);
//   * фокус и история — как раньше: `finished` не ждём, лишних записей нет;
//   * лист ввода: въезд 200 мс / выезд 150 мс + скрим 150 мс (transform, не bottom);
//   * органы/очередь/строка исхода — только прозрачность 150 мс, без сдвига и высоты;
//   * знак «Работает» в СПИСКЕ вращается (D7 §2 А), у остальных тонов — нет.

type VtSpy = {
  calls: number;
  anims: { name: string; duration: number; easings: string[] }[];
};

/** Ставит наблюдателя за API до загрузки приложения (или убирает API). */
async function spyViewTransitions(page: Page, mode: "spy" | "none" | "never" = "spy") {
  await page.addInitScript((kind: string) => {
    const w = window as unknown as { __vt: VtSpy };
    w.__vt = { calls: 0, anims: [] };

    if (kind === "none") {
      Object.defineProperty(document, "startViewTransition", {
        configurable: true,
        value: undefined,
      });
      return;
    }

    const native = document.startViewTransition?.bind(document);

    if (kind === "never") {
      Object.defineProperty(document, "startViewTransition", {
        configurable: true,
        value: (callback: () => void) => {
          w.__vt.calls += 1;
          callback();
          return { finished: new Promise<void>(() => undefined) };
        },
      });
      return;
    }

    if (typeof native !== "function") return;

    Object.defineProperty(document, "startViewTransition", {
      configurable: true,
      value: (callback: () => void) => {
        w.__vt.calls += 1;
        const transition = native(callback);
        const collect = () => {
          const list = document
            .getAnimations()
            .filter((animation) =>
              ((animation.effect as KeyframeEffect | null)?.pseudoElement ?? "").includes(
                "view-transition",
              ),
            );
          if (list.length > 0) {
            w.__vt.anims = list.map((animation) => {
              const timing = animation.effect?.getTiming();
              return {
                name: (animation as unknown as { animationName?: string }).animationName ?? "",
                duration: Number(timing?.duration ?? 0),
                // У CSS-анимаций кривая стоит на кадрах, а эффект остаётся linear — читаем кадры.
                easings: ((animation.effect as KeyframeEffect | null)?.getKeyframes() ?? []).map(
                  (frame: Keyframe) => String(frame.easing),
                ),
              };
            });
            return;
          }
          requestAnimationFrame(collect);
        };
        requestAnimationFrame(collect);
        return transition;
      },
    });
  }, mode);
}

async function vt(page: Page): Promise<VtSpy> {
  await expect
    .poll(async () => page.evaluate(() => Boolean((window as unknown as { __vt?: VtSpy }).__vt)))
    .toBe(true);
  return page.evaluate(() => (window as unknown as { __vt: VtSpy }).__vt);
}

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/scenario", {
    data: { authed: true, lang: "ru", list: "ok", pair: "ok", question: "none", queue: "ok" },
  });
});

test("API: переход 200 мс, 16 px по иерархии, одна поверхность; история — только своя запись", async ({
  page,
}) => {
  await spyViewTransitions(page);
  await page.goto("/");

  const row = page.getByRole("link", { name: /Починить калитку/u });
  await expect(row).toBeVisible();
  const historyBefore = await page.evaluate(() => history.length);

  await row.click();
  await expect(page).toHaveURL(/session=session-live/u);

  const spy = await vt(page);
  expect(spy.calls, "переход через API").toBe(1);
  await expect.poll(async () => (await vt(page)).anims.length).toBeGreaterThan(0);

  const anims = (await vt(page)).anims;
  const entering = anims.find((item) => item.name === "dp-screen-in-forward");
  expect(entering, `переход вперёд: ${JSON.stringify(anims)}`).toBeTruthy();
  expect(entering!.duration).toBe(200);
  expect(
    entering!.easings.some((easing) => easing.includes("cubic-bezier(0.215, 0.61, 0.355, 1)")),
    `кривая Semi: ${JSON.stringify(entering!.easings)}`,
  ).toBe(true);

  // ОДНА поверхность: ни у шапки, ни у зацепа, ни у листа, ни у ленты своего имени перехода нет.
  expect(
    await page.evaluate(
      () =>
        [...document.querySelectorAll("*")]
          .filter((element) => element !== document.documentElement)
          .filter((element) => getComputedStyle(element).viewTransitionName !== "none").length,
    ),
    "названных поверхностей нет",
  ).toBe(0);

  // История: навигация добавила ровно одну свою запись; переход — ни одной.
  expect(await page.evaluate(() => history.length)).toBe(historyBefore + 1);

  // Назад — тот же API, обратное направление и сдвиг слева.
  await page.locator(".dp-head-back").click();
  await expect(page).toHaveURL(/\/$/u);
  await expect.poll(async () => (await vt(page)).calls).toBe(2);
  const backAnims = (await vt(page)).anims;
  expect(backAnims.some((item) => item.name === "dp-screen-in-back")).toBe(true);
});

test("API нет — запасной путь: входящий экран, 150 мс, 8 px", async ({ page }) => {
  await spyViewTransitions(page, "none");
  await page.goto("/");
  await page.getByRole("link", { name: /Починить калитку/u }).click();
  await expect(page).toHaveURL(/session=session-live/u);

  const main = page.locator("main");
  await expect(main).toHaveClass(/dp-enter/u);
  const shape = await main.evaluate((node) => {
    const style = getComputedStyle(node);
    return {
      name: style.animationName,
      duration: style.animationDuration,
      easing: style.animationTimingFunction,
      nav: document.documentElement.dataset.nav,
    };
  });
  expect(shape.name).toBe("dp-enter");
  expect(shape.duration).toBe("0.15s");
  expect(shape.easing).toBe("cubic-bezier(0.215, 0.61, 0.355, 1)");
  expect(shape.nav).toBe("forward");

  // Класс снимается по концу анимации: следующая проверка видит чистый экран.
  await expect(main).not.toHaveClass(/dp-enter/u, { timeout: 2_000 });
  // API не звали вовсе.
  expect((await vt(page)).calls).toBe(0);
});

test("reduce: ни перехода, ни класса — экран меняется сразу", async ({ browser }) => {
  const context = await browser.newContext({
    viewport: { width: 360, height: 800 },
    reducedMotion: "reduce",
  });
  const page = await context.newPage();
  await spyViewTransitions(page);
  await page.goto("/");
  await page.getByRole("link", { name: /Починить калитку/u }).click();
  await expect(page).toHaveURL(/session=session-live/u);

  expect((await vt(page)).calls, "в reduce API не вызываем").toBe(0);
  await expect(page.locator("main")).not.toHaveClass(/dp-enter/u);
  expect(await page.locator("main").evaluate((node) => getComputedStyle(node).animationName)).toBe(
    "none",
  );

  await context.close();
});

test("переход не ждёт finished: фокус и адрес встают сразу", async ({ page }) => {
  await spyViewTransitions(page, "never");
  await page.goto("/");
  await page.getByRole("link", { name: /Починить калитку/u }).click();

  // `finished` никогда не разрешится: если бы код его ждал, адрес и фокус не встали бы.
  await expect(page).toHaveURL(/session=session-live/u);
  await expect(page.locator(".dp-head-session h1")).toBeFocused({ timeout: 3_000 });
  expect((await vt(page)).calls).toBe(1);
});

test("переход не добавляет записи истории: создание задачи — replaceState", async ({ page }) => {
  await page.goto("/?new=1");
  await expect(page.locator(".dp-composer textarea")).toBeVisible();

  const before = await page.evaluate(() => history.length);
  await page.locator(".dp-composer textarea").fill("Проверь сборку");
  await page.locator(".dp-primary").click();
  await expect(page).toHaveURL(/session=/u, { timeout: 10_000 });

  // Адрес заменился, а не добавился: история та же — переход своей записи не делает.
  expect(await page.evaluate(() => history.length)).toBe(before);
});

test("лист ввода: въезд 200 мс / выезд 150 мс, скрим 150 мс, история возвращается", async ({
  page,
}) => {
  // Настоящий путь: список → сессия; тогда системная «назад» после листа ведёт на список.
  await page.goto("/");
  await page.getByRole("link", { name: /Починить калитку/u }).click();
  await expect(page).toHaveURL(/session=session-live/u);
  await expect(page.locator(".dp-handle")).toBeVisible();
  const sessionLength = await page.evaluate(() => history.length);
  const stateBefore = await page.evaluate(() => JSON.stringify(history.state));

  await page.locator(".dp-handle").click();
  const sheet = page.locator(".dp-sheet");
  await expect(sheet).toBeVisible();
  const enter = await sheet.evaluate((node) => {
    const style = getComputedStyle(node);
    return { name: style.animationName, duration: style.animationDuration };
  });
  expect(enter).toEqual({ name: "dp-sheet-in", duration: "0.2s" });
  expect(
    await page.locator(".dp-sheet-scrim").evaluate((node) => {
      const style = getComputedStyle(node);
      return { name: style.animationName, duration: style.animationDuration };
    }),
  ).toEqual({ name: "dp-scrim-in", duration: "0.15s" });

  await page.keyboard.press("Escape");
  await expect(page.locator(".dp-sheet.dp-sheet-closing")).toHaveCount(1);
  expect(
    await sheet.evaluate((node) => {
      const style = getComputedStyle(node);
      return { name: style.animationName, duration: style.animationDuration };
    }),
  ).toEqual({ name: "dp-sheet-out", duration: "0.15s" });

  await expect(sheet, "лист уехал и снят").toHaveCount(0);
  await expect(page.locator(".dp-handle")).toBeFocused();
  // Запись листа снята: мы снова на записи сессии, и история не выросла от анимации.
  expect(await page.evaluate(() => JSON.stringify(history.state))).toBe(stateBefore);
  expect(await page.evaluate(() => history.length)).toBe(sessionLength + 1);

  // Системная «назад» после закрытия ведёт на список, а не «закрывает лист ещё раз».
  await page.goBack();
  await expect(page).toHaveURL(/\/$/u);
});

test("органы и зацеп: только прозрачность 150 мс, координат не анимируем", async ({ page }) => {
  // ДЛИННАЯ лента: на короткой странице прокрутки нет вовсе, и пары «вверх» не будет (W12).
  await page.goto("/?session=session-long");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Длинная лента");
  await expect(page.locator(".dp-handle")).toBeVisible();

  // Зацеп (D11 §2.2): появляется одной прозрачностью 150 мс, `transition` нет (координат больше нет:
  // полоса прибита к низу), имени перехода тоже нет.
  expect(
    await page.locator(".dp-handle").evaluate((node) => {
      const style = getComputedStyle(node);
      return {
        transition: style.transitionDuration,
        name: style.viewTransitionName,
        animation: style.animationName,
        duration: style.animationDuration,
      };
    }),
  ).toEqual({
    transition: "0s",
    name: "none",
    animation: "dp-fade-in",
    duration: "0.15s",
  });
  // «Вверх» появляется при прокрутке: только прозрачность, на своём месте.
  await page.evaluate(() => window.scrollTo(0, 600));
  const up = page.locator(".dp-toup");
  await expect(up).toBeVisible();
  const organ = await up.evaluate((node) => {
    const style = getComputedStyle(node);
    return {
      name: style.animationName,
      duration: style.animationDuration,
      transform: style.transform,
      transition: style.transitionDuration,
      height: (node as HTMLElement).offsetHeight,
    };
  });
  expect(organ.name).toBe("dp-fade-in");
  expect(organ.duration).toBe("0.15s");
  expect(organ.transform, "с места не двигается").toBe("none");
  expect(organ.transition).toBe("0s");
  expect(organ.height).toBe(44);

  // Высота не анимируется: через 60 мс мера та же.
  await page.waitForTimeout(60);
  expect(await up.evaluate((node) => (node as HTMLElement).offsetHeight)).toBe(44);

  // Очередь — тот же вид появления.
  const queue = page.locator(".dp-queue").first();
  if ((await queue.count()) > 0) {
    expect(
      await queue.evaluate((node) => {
        const style = getComputedStyle(node);
        return { name: style.animationName, duration: style.animationDuration };
      }),
    ).toEqual({ name: "dp-fade-in", duration: "0.15s" });
  }

  // Прокрутка и прыжки — без плавности.
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).scrollBehavior)).toBe(
    "auto",
  );
});

test("reduce: у органов анимации нет, слово на месте", async ({ browser }) => {
  const context = await browser.newContext({
    viewport: { width: 360, height: 800 },
    reducedMotion: "reduce",
  });
  const page = await context.newPage();
  await page.goto("/?session=session-long");
  await expect(page.locator(".dp-handle")).toBeVisible();
  await page.evaluate(() => window.scrollTo(0, 600));

  const up = page.locator(".dp-toup");
  await expect(up).toBeVisible();
  expect(await up.evaluate((node) => getComputedStyle(node).animationName)).toBe("none");
  await expect(up).toHaveAccessibleName(/К началу разговора/u);

  await context.close();
});

test("список (D7): знак «Работает» вращается, у остальных тонов — нет", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("link", { name: /Починить калитку/u })).toBeVisible();

  const liveRow = page.getByRole("link", { name: /Починить калитку/u });
  await expect(liveRow.locator(".dp-state-mark.dp-state-accent .semi-icon")).toHaveClass(
    /semi-icon-spinning/u,
  );
  expect(
    await liveRow
      .locator(".dp-state-mark.dp-state-accent .semi-icon")
      .evaluate((node) => getComputedStyle(node).animationName),
  ).toBe("semi-icon-animation-rotate");

  for (const row of ["Собрать список сессий", "Обновление панели"]) {
    const link = page.getByRole("link", { name: row });
    if ((await link.count()) > 0) {
      expect(
        await link
          .locator(".dp-row-meta .semi-icon")
          .first()
          .evaluate((node) => getComputedStyle(node).animationName),
      ).not.toBe("semi-icon-animation-rotate");
    }
  }
});

test("список (D7): при reduce знак не вращается, слово остаётся", async ({ browser }) => {
  const context = await browser.newContext({
    viewport: { width: 360, height: 800 },
    reducedMotion: "reduce",
  });
  const page = await context.newPage();
  await page.goto("/");

  const liveRow = page.getByRole("link", { name: /Починить калитку/u });
  await expect(liveRow).toContainText("Работает");
  const icon = liveRow.locator(".dp-state-mark.dp-state-accent .semi-icon");
  await expect(icon).toHaveClass(/semi-icon-spinning/u);
  expect(await icon.evaluate((node) => getComputedStyle(node).animationName)).toBe("none");

  await context.close();
});
