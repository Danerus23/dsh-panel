import { expect, test } from "@playwright/test";

// Сессия и живой поток: лента, черновик → готовый ответ ровно один раз, «раньше», возврат
// с фокусом на строку, пустая сессия, неоткрывшаяся сессия.
// Полоса/лист ввода: свёрнутый лист в потоке, поле — в поднятом (S3e, карточка 32a О-3).
function bar(page: import("@playwright/test").Page) {
  return page.locator(".dp-bottom");
}

test.beforeEach(async ({ request }) => {
  await request.post("/__mock/scenario", {
    data: { authed: true, lang: "ru", list: "ok", pair: "ok" },
  });
});

test("лента: человек, агент, инструмент; черновик заменяется готовым ответом", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("link", { name: /Починить калитку/ }).click();

  await expect(page).toHaveURL(/session=session-live/);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Починить калитку");
  await expect(page.getByText("Проверь сборку и скажи, что упало.")).toBeVisible();
  await expect(page.getByText("Выполняет: dotnet test")).toBeVisible();
  await expect(page.getByText("dotnet test — готово")).toBeVisible();

  // Черновик — одна строка, дописывается; готовый ответ снимает его (не две копии).
  await expect(page.locator(".dp-draft")).toHaveCount(1);
  await expect(page.locator(".dp-draft")).toContainText("Сейчас проверю");
  await expect(page.locator(".dp-draft")).toHaveCount(0, { timeout: 10_000 });
  await expect(page.getByText("Сейчас проверю логи.")).toHaveCount(1);
});

test("«К списку» возвращает и ставит фокус на ту же строку", async ({ page }) => {
  await page.goto("/");
  const row = page.getByRole("link", { name: /Починить калитку/ });
  await row.click();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Починить калитку");
  // Возврат к списку — кнопка-иконка в липкой шапке (R2), не текстовая строка-ссылка.
  await page.getByRole("button", { name: "К списку сессий" }).click();
  await expect(page.getByRole("heading", { name: "Сессии" })).toBeVisible();
  await expect(row).toBeFocused();
});

test("«раньше» догружается САМО при прокрутке вверх (кнопки нет), лента не прыгает", async ({
  page,
}) => {
  // Окно ЗАВЕДОМО ниже ленты с большим запасом (находка t12 F4): допуск автодописывания
  // 188 px делал «верх» и «низ» неразличимыми — при 640 высоты у снимка в 5 строк оставалось
  // 14 px прокрутки, экран считал себя внизу и пин добавлял 14 к якорю.
  // W10/R3 (D11 §4.2): вопрос НЕ в потоке и высоту страницы не растит; очередь ВЫКЛЮЧЕНА
  // (`queue: "empty"` — карточки нет), поэтому запас снизу = 52 и высота страницы не зависит
  // от того, что оставил предыдущий файл. Окно 140: прокрутка ~200 — выше допуска 152.
  await page.request.post("/__mock/scenario", {
    data: { older: "ok", question: "none", queue: "empty" },
  });
  // Счётчик старших страниц — состояние стенда: без сброса страница может быть уже «разобрана».
  await page.request.post("/__mock/s3-reset", { data: {} });
  await page.setViewportSize({ width: 360, height: 140 });
  await page.goto("/?session=session-many");
  await expect(page.locator(".dp-feed li").last()).toBeVisible();
  // Стек устоялся (первый ответ очереди меняет запас снизу) — иначе замер якоря спорит с пином,
  // который дёргает страницу в тот же миг (R3: в общем прогоне давало сдвиг на 200 px).
  await expect
    .poll(async () =>
      page.evaluate(() => {
        const stack = document.querySelector(".dp-bottom") as HTMLElement | null;
        const reserve = getComputedStyle(document.documentElement)
          .getPropertyValue("--dp-bottom-reserve")
          .trim();
        return stack === null
          ? ""
          : `${Math.round(stack.getBoundingClientRect().height)}px|${reserve}`;
      }),
    )
    .toMatch(/^(\d+)px\|\1px$/u);

  // Лента обязана быть минимум на две сотни px длиннее окна: меньше — и проверка ничего не проверяет.
  const room = await page.evaluate(
    () => document.documentElement.scrollHeight - window.innerHeight,
  );
  expect(room, "лента заведомо длиннее окна и выше допуска 152").toBeGreaterThanOrEqual(190);
  // D1 §4.2 (11.11.2026): догруз считается ОТ ВЕРХА ЛЕНТЫ, а не от верха документа — ставим
  // ПЕРВУЮ ЗАПИСЬ ЛЕНТЫ к верхнему краю окна (это и есть «дошёл до верха ленты»).
  await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  const feedDocTop = await page.evaluate(() => {
    const feed = document.querySelector(".dp-feed") as HTMLElement;
    return Math.round(feed.getBoundingClientRect().top + window.scrollY);
  });
  // СНИМОК ДО ВСТАВКИ — В ОДНОМ `evaluate` С ПРОКРУТКОЙ (R4): событие `scroll` браузер шлёт ПОСЛЕ
  // текущей задачи, значит всё, снятое здесь, — состояние ДО догруза. Без этого замер гонялся с
  // вставкой (в прогонах видимая строка была уже сдвинута в момент снимка: `-160 → 40`, △0/△0).
  const before = await page.evaluate((top) => {
    const snap = () => ({
      doc: Math.round(document.documentElement.scrollHeight),
      y: Math.round(window.scrollY),
      top: Math.round(
        (document.querySelector(".dp-feed li") as HTMLElement).getBoundingClientRect().top,
      ),
      text: ((document.querySelector(".dp-feed li") as HTMLElement).textContent ?? "").trim(),
    });
    const pre = snap();
    window.scrollTo(0, top - 40);
    return { pre, post: snap() };
  }, feedDocTop);
  const beforeTop = before.post.top;
  const firstText = before.post.text;

  // Догруз идёт сам: прокрутку больше НЕ трогаем, иначе сбросим якорь собственными руками.
  await expect
    .poll(
      async () =>
        page.evaluate(() => document.body.innerText.includes("Самое первое сообщение человека.")),
      {
        message: "прокрутка вверх догрузила старые сообщения",
        timeout: 10000,
      },
    )
    .toBe(true);

  // Вставка старых НЕ сдвинула то, что человек читал: якорь по первой строке (допуск 1 px, D1 §4.2).
  const after = await page.evaluate(() => ({
    doc: Math.round(document.documentElement.scrollHeight),
    y: Math.round(window.scrollY),
  }));
  const anchor = page.locator(".dp-feed li").filter({ hasText: firstText }).first();
  const afterTop = await anchor.evaluate((node) => Math.round(node.getBoundingClientRect().top));
  // R4: два утверждения — (1) документ ВЫРОС, (2) `scrollY` вырос РОВНО на столько же.
  // Вторая без первой молчала бы: если вставка успела до снимка, обе дельты — нули.
  expect(
    after.doc - before.post.doc,
    `догруз вставил старые: документ вырос на ${after.doc - before.post.doc}`,
  ).toBeGreaterThanOrEqual(100);
  expect(
    Math.abs(after.y - before.post.y - (after.doc - before.post.doc)),
    `△scrollY = △высоты документа: △y=${after.y - before.post.y}, △doc=${after.doc - before.post.doc}`,
  ).toBeLessThanOrEqual(1);
  expect(
    Math.abs(afterTop - beforeTop),
    `видимая строка осталась на месте: ${beforeTop} → ${afterTop}`,
  ).toBeLessThanOrEqual(1);
});

test("отказ догруза «раньше» — тонкая строка и «Повторить», лента не теряется", async ({
  page,
  request,
}) => {
  await request.post("/__mock/scenario", {
    // Карточка вопроса — чтобы страница была заведомо длиннее окна (W12 убрал очередь из потока).
    data: {
      authed: true,
      lang: "ru",
      list: "ok",
      pair: "ok",
      older: "fail",
      question: "single",
      questionSession: "session-many",
    },
  });
  await request.post("/__mock/s3-reset", { data: {} });
  await page.goto("/?session=session-many");
  await expect(page.locator(".dp-feed li").last()).toBeVisible();
  const alert = page.getByRole("alert").filter({ hasText: "Раньше показать не удалось" });
  // ВЗВОД подхода: первая строка ленты должна оказаться ниже порога 120 (карточка вопроса держит
  // страницу длинной — на короткой это происходило само). Потом идём к верху ленты.
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.waitForTimeout(250);
  await expect
    .poll(
      async () => {
        await page.evaluate(() => {
          const feed = document.querySelector(".dp-feed") as HTMLElement;
          window.scrollTo(0, feed.getBoundingClientRect().top + window.scrollY - 40);
        });
        return alert.count();
      },
      { message: "отказ догруза показан", timeout: 10000 },
    )
    .toBe(1);
  await expect(alert, "отказ — строкой, а не блоком").toBeVisible();
  const retry = alert.getByRole("button", { name: "Повторить" });
  await expect(retry).toBeVisible();
  const height = await alert.evaluate((node) =>
    Math.round((node as HTMLElement).getBoundingClientRect().height),
  );
  expect(height, "строка тонкая").toBeLessThan(120);
  await expect(page.locator(".dp-feed li").first(), "лента на месте").toBeVisible();

  // Повтор — успех: строка отказа исчезает, старые сообщения пришли.
  await request.post("/__mock/scenario", { data: { older: "ok" } });
  await retry.click();
  await expect(page.getByText("Самое первое сообщение человека.")).toBeVisible();
  await expect(
    page.getByRole("alert").filter({ hasText: "Раньше показать не удалось" }),
  ).toHaveCount(0);
});

test("пустая сессия: поле задачи есть, слов «нельзя» нет", async ({ page }) => {
  await page.goto("/?session=session-empty");
  await expect(page.getByText("Пока пусто")).toBeVisible();
  // S3: у пустой сессии есть путь — поле задачи в полосе (слова `PhoneWebSessionEmpty` правлены t3).
  await expect(page.getByText(/Напишите задачу — ход начнётся с неё/)).toBeVisible();
  await expect(page.getByText(/поставить нельзя/)).toHaveCount(0);
  // Поле задачи — в свёрнутом листе (S3e); кнопка отправки живёт в поднятом и погашена, пока поле пусто.
  await expect(bar(page).locator(".dp-handle")).toBeVisible();
  await bar(page).locator(".dp-handle").click();
  await expect(page.locator(".dp-sheet textarea")).toBeVisible();
  await expect(page.getByRole("button", { name: "Отправить задачу" })).toBeDisabled();
});

test("неоткрывшаяся сессия: причина словами и путь назад", async ({ page }) => {
  await page.goto("/?session=session-error");
  await expect(page.getByText("Сессия не открылась")).toBeVisible();
  // Липкая шапка с кнопкой-иконкой есть и у неоткрывшейся сессии (R2).
  await expect(page.getByRole("button", { name: "К списку сессий" })).toBeVisible();
});

test("лента как в мессенджере: своё — справа пузырём ≤85 %, ответ агента — слева без пузыря (360, 320, тёмная)", async ({
  browser,
}) => {
  for (const variant of [
    { width: 360, colorScheme: "light" as const },
    { width: 320, colorScheme: "light" as const },
    { width: 320, colorScheme: "dark" as const },
  ]) {
    const context = await browser.newContext({
      viewport: { width: variant.width, height: 720 },
      colorScheme: variant.colorScheme,
    });
    const page = await context.newPage();
    await page.goto("/?session=session-live");
    await expect(page.locator(".dp-msg.dp-user").first()).toBeVisible();

    const info = await page.evaluate(() => {
      const user = document.querySelector(".dp-msg.dp-user") as HTMLElement;
      const agent = document.querySelector(".dp-msg.dp-agent") as HTMLElement;
      const main = document.querySelector(".dp-page") as HTMLElement;
      const style = getComputedStyle(main);
      const ub = user.getBoundingClientRect();
      const ab = agent.getBoundingClientRect();
      const pb = main.getBoundingClientRect();
      const padLeft = Number.parseFloat(style.paddingLeft);
      const padRight = Number.parseFloat(style.paddingRight);
      return {
        userRight: Math.round(ub.right),
        contentRight: Math.round(pb.right - padRight),
        userLeft: Math.round(ub.left),
        contentLeft: Math.round(pb.left + padLeft),
        widthRatio: ub.width / pb.width,
        agentLeft: Math.round(ab.left),
        agentBg: getComputedStyle(agent).backgroundColor,
        userBg: getComputedStyle(user).backgroundColor,
        pageBg: style.backgroundColor,
        radius: getComputedStyle(user).borderTopLeftRadius,
        scrollWidth: document.documentElement.scrollWidth,
        innerWidth: window.innerWidth,
      };
    });

    expect(info.userRight, `${variant.width}: пузырь прижат к правому краю`).toBeLessThanOrEqual(
      info.contentRight + 2,
    );
    expect(info.userLeft, `${variant.width}: слева от пузыря есть воздух`).toBeGreaterThan(
      info.contentLeft + 8,
    );
    expect(info.widthRatio, `${variant.width}: пузырь ≤ 85 %`).toBeLessThanOrEqual(0.86);
    expect(
      Math.abs(info.agentLeft - info.contentLeft),
      `${variant.width}: агент — от левого края`,
    ).toBeLessThanOrEqual(2);
    expect(info.agentBg, `${variant.width}: у агента пузыря нет`).toBe("rgba(0, 0, 0, 0)");
    expect(info.userBg, `${variant.width}: подложка пузыря — из токенов`).not.toBe(info.pageBg);
    expect(info.radius, `${variant.width}: пузырь скруглён`).not.toBe("0px");
    expect(info.scrollWidth, `${variant.width}: без горизонтали`).toBeLessThanOrEqual(
      info.innerWidth,
    );
    await context.close();
  }
});
