import { expect, test, type APIRequestContext, type Page } from "@playwright/test";

// S2 «Отвечать»: карточка вопроса внутри сессии — варианты, свои слова, ответ полным набором,
// двухшаговая отмена, «снят другим», устаревший вопрос, неразобранный, карточка без сессии.

async function openSession(
  page: Page,
  request: APIRequestContext,
  patch: Record<string, unknown> = {},
) {
  await request.post("/__mock/scenario", {
    data: {
      authed: true,
      lang: "ru",
      list: "ok",
      question: "none",
      questionSession: "session-live",
      answer: "ok",
      cancel: "ok",
      ...patch,
    },
  });
  await page.goto("/?session=session-live");
}

/** Состояние стенда: счётчики и тела дверей (в т.ч. `answerCalls` — «Далее» не отправляет). */
async function mockState(request: APIRequestContext) {
  return (await request.get("/__mock/s3")).json() as Promise<{ answerCalls: number }>;
}

test("ответ полным набором: radio + checkbox + свои слова, один POST", async ({
  page,
  request,
}) => {
  await openSession(page, request, { question: "two" });
  await request.post("/__mock/s3-reset", { data: {} });

  const card = page.locator(".dp-question");
  await expect(card.getByRole("heading", { name: "Агент ждёт ответа" })).toBeVisible();
  await expect(card.getByText("Что делаем с упавшими тестами?")).toBeVisible();
  // «Вопрос N из M» и стрелки (карточка 32a §4 п. 2): карточка одна на экране.
  await expect(card.locator(".dp-question-count")).toHaveText("Вопрос 1 из 2");
  await expect(card.locator(".dp-question-pager button")).toHaveCount(2);
  // Подписей-комментариев (header/detail) больше нет: функции и кнопки понятны сами.
  await expect(card.getByText("Два теста в наборе калитки.")).toHaveCount(0);

  // Пока есть неотвеченные — главная кнопка зовётся «Далее» и ВЕДЁТ, а не отправляет (D11 §5.2).
  const next = page.getByRole("button", { name: "Далее" });
  await expect(next).toBeEnabled();
  await expect(page.getByRole("button", { name: "Ответить" })).toHaveCount(0);
  // Причина печатается только у ВЫКЛЮЧЕННОЙ главной кнопки (`60` §7) — здесь она не нужна.
  await expect(page.getByText("Выберите ответ на каждый вопрос.")).toHaveCount(0);
  await page.getByRole("radio", { name: "Починить сейчас" }).check();
  // Отвечать можно не по порядку: вперёд — и назад тем же способом.
  await card.getByRole("button", { name: "Вопрос 2 из 2" }).click();
  await expect(card.locator(".dp-question-count")).toHaveText("Вопрос 2 из 2");
  await page.getByRole("checkbox", { name: "Сборка" }).check();
  await page.getByRole("checkbox", { name: "Тесты" }).check();
  await card.locator("textarea").fill("и формат");
  await card.getByRole("button", { name: "Вопрос 1 из 2" }).click();
  await expect(page.getByRole("radio", { name: "Починить сейчас" })).toBeChecked();
  await card.getByRole("button", { name: "Вопрос 2 из 2" }).click();
  // Ответы есть на КАЖДЫЙ — и кнопка снова называет отправку, и она доступна.
  const send = page.getByRole("button", { name: "Ответить" });
  await expect(page.getByRole("button", { name: "Далее" })).toHaveCount(0);
  await expect(send).toBeEnabled();
  await send.click();

  // УСПЕХ НЕ ПЕЧАТАЕТСЯ (D11 §6.2, DEF-147): карточка ушла — это и есть ответ на «ушло ли».
  await expect(page.getByText("Ответ отправлен."), "окна успеха нет").toHaveCount(0);
  await expect(page.locator(".dp-question-done"), "блока исхода нет").toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Агент ждёт ответа" })).toHaveCount(0);

  const last = await (await request.get("/__mock/last-answer")).json();
  expect(last.body.eventId).toBe("evt-1");
  expect(last.body.answers).toHaveLength(2);
  expect(last.body.answers[0]).toEqual({ id: "q1", selected: ["Починить сейчас"] });
  expect(last.body.answers[1].id).toBe("q2");
  expect([...last.body.answers[1].selected].sort()).toEqual(["Сборка", "Тесты"]);
  expect(last.body.answers[1].custom).toBe("и формат");
});

// D13-1 (R3): на 320 тело вопроса было 40 px — варианты не видны; ряд действий не влезал
// (88 + 8 + 186 = 282 против 278) и уходил на вторую строку, отнимая у тела ещё 52.
test("320: тело вопроса ≥100, кнопки В ОДИН РЯД, первый вариант виден — D13-1", async ({
  page,
  request,
}) => {
  await page.setViewportSize({ width: 320, height: 603 });
  await openSession(page, request, { question: "two" });
  await expect(page.locator(".dp-question")).toBeVisible();

  const measure = () =>
    page.evaluate(() => {
      const rect = (selector: string) => {
        const node = document.querySelector(selector) as HTMLElement | null;
        return node === null ? null : node.getBoundingClientRect();
      };
      const body = document.querySelector(".dp-question-body") as HTMLElement;
      const actions = document.querySelector(".dp-question-actions") as HTMLElement;
      const buttons = [...actions.querySelectorAll("button")];
      const choice = rect(".dp-choice");
      const bodyBox = body.getBoundingClientRect();
      const style = getComputedStyle(actions);
      return {
        body: Math.round(bodyBox.height * 10) / 10,
        actionsHeight: Math.round(actions.getBoundingClientRect().height * 10) / 10,
        tops: buttons.map((button) => Math.round(button.getBoundingClientRect().top)),
        borderTop: style.borderTopWidth,
        paddingTop: style.paddingTop,
        choiceTop: choice === null ? null : Math.round(choice.top),
        choiceBottom: choice === null ? null : Math.round(choice.bottom),
        bodyTop: Math.round(bodyBox.top),
        bodyBottom: Math.round(bodyBox.bottom),
      };
    });

  const small = await measure();
  expect(small.body, "тело вопроса ≥ 100 (было 40,4)").toBeGreaterThanOrEqual(100);
  expect(small.tops.length, "две кнопки в ряду действий").toBe(2);
  expect(small.tops[0], "кнопки стоят в ОДИН ряд").toBe(small.tops[1]);
  expect(small.actionsHeight, "ряд действий — одна строка, не две").toBeLessThanOrEqual(60);
  expect(small.choiceTop, "первый вариант начинается в теле").toBeGreaterThanOrEqual(
    small.bodyTop - 0.5,
  );
  expect(small.choiceBottom, "и виден целиком").toBeLessThanOrEqual(small.bodyBottom + 0.5);
  // F-D/D13-8: ряд действий отделён ЛИНИЕЙ — обрезка тела читается как прокрутка, а не как обрыв.
  expect(small.borderTop, "граница ряда действий названа линией").toBe("1px");
  expect(small.paddingTop, "и отбивкой 8").toBe("8px");

  // На 360 карточка не поменялась: тело в пределе, кнопки в один ряд, вариант виден.
  await page.setViewportSize({ width: 360, height: 692 });
  const big = await measure();
  expect(big.body, "на 360 тело такое же и больше 100").toBeGreaterThanOrEqual(100);
  expect(big.tops[0], "на 360 — тоже одна строка").toBe(big.tops[1]);
  expect(big.choiceBottom, "и первый вариант виден").toBeLessThanOrEqual(big.bodyBottom + 0.5);
});

test("последний вопрос: стрелка «вперёд» не называет несуществующий вопрос — F-B", async ({
  page,
  request,
}) => {
  // L4/R3: на последнем вопросе было «Вопрос 3 из 2» — `index + 2` без клампа.
  await openSession(page, request, { question: "two" });
  const card = page.locator(".dp-question");
  await card.getByRole("button", { name: "Вопрос 2 из 2" }).click();
  await expect(card.locator(".dp-question-count")).toHaveText("Вопрос 2 из 2");

  const forward = card.locator(".dp-question-pager button").last();
  await expect(forward).toBeDisabled();
  expect(await forward.getAttribute("aria-label"), "имя не обещает третьего вопроса").toBe(
    "Вопрос 2 из 2",
  );
});

test("два вопроса: «Далее» ведёт ко второму неотвеченному и ставит фокус в legend — D11 §5.2", async ({
  page,
  request,
}) => {
  await openSession(page, request, { question: "two" });
  await request.post("/__mock/s3-reset", { data: {} });
  const card = page.locator(".dp-question");

  // Стоим на первой, ответа нет ни на одну: главная кнопка — «Далее» И ОНА РАБОТАЕТ.
  await expect(card.locator(".dp-question-count")).toHaveText("Вопрос 1 из 2");
  const next = page.getByRole("button", { name: "Далее" });
  await expect(next).toBeEnabled();
  await next.click();

  // Дальше — ВТОРОЙ вопрос, и фокус — в его legend: читалка объявит, что показано теперь.
  await expect(card.locator(".dp-question-count")).toHaveText("Вопрос 2 из 2");
  await expect(card.locator("legend")).toHaveText("Какие проверки?");
  await expect(card.locator("legend"), "фокус — в legend нового вопроса").toBeFocused();

  // «Далее» НЕ отправляет: в дверь ответа не стучали ни разу.
  expect((await mockState(request)).answerCalls, "«Далее» ведёт, а не отправляет").toBe(0);
  const last = await (await request.get("/__mock/last-answer")).json();
  expect(last.body, "тела ответа тоже нет").toBeNull();
});

test("после всех ответов главная кнопка — «Ответить», и только она отправляет", async ({
  page,
  request,
}) => {
  await openSession(page, request, { question: "two" });
  await request.post("/__mock/s3-reset", { data: {} });
  const card = page.locator(".dp-question");

  // Одна карточка отвечена, вторая нет — главная всё ещё «Далее».
  await page.getByRole("radio", { name: "Починить сейчас" }).check();
  await expect(page.getByRole("button", { name: "Далее" })).toBeEnabled();
  await expect(page.getByRole("button", { name: "Ответить" })).toHaveCount(0);
  await page.getByRole("button", { name: "Далее" }).click();
  await page.getByRole("checkbox", { name: "Сборка" }).check();

  // Отвечено всё — имя меняется на «Ответить», и эта самая кнопка и отправляет.
  const send = page.getByRole("button", { name: "Ответить" });
  await expect(send, "все ответы есть — кнопка зовётся «Ответить»").toBeEnabled();
  await expect(page.getByRole("button", { name: "Далее" })).toHaveCount(0);
  await send.click();
  // Молчание — правило (D11 §6.2): успех не печатается, есть только факт ушедшей карточки.
  await expect(page.getByText("Ответ отправлен."), "окна успеха нет").toHaveCount(0);
  await expect(page.locator(".dp-question-done")).toHaveCount(0);
  expect((await mockState(request)).answerCalls, "ровно один POST за ответ").toBe(1);
  const last = await (await request.get("/__mock/last-answer")).json();
  expect(last.body.answers).toHaveLength(2);
});

test("возврат к вопросу меняет ответ: уходит последний выбор, а не первый", async ({
  page,
  request,
}) => {
  await openSession(page, request, { question: "two" });
  await request.post("/__mock/s3-reset", { data: {} });
  const card = page.locator(".dp-question");

  await page.getByRole("radio", { name: "Починить сейчас" }).check();
  await page.getByRole("button", { name: "Далее" }).click();
  await page.getByRole("checkbox", { name: "Сборка" }).check();

  // Возврат на первую карточку и ИЗМЕНЕНИЕ ответа: прежний выбор снят.
  await card.getByRole("button", { name: "Вопрос 1 из 2" }).click();
  await expect(page.getByRole("radio", { name: "Починить сейчас" })).toBeChecked();
  await page.getByRole("radio", { name: "Оставить как есть" }).check();
  await expect(page.getByRole("radio", { name: "Починить сейчас" })).not.toBeChecked();

  // И на второй — тоже: вместо «Сборки» выбираем «Тесты». Идём СТРЕЛКОЙ: обе карточки уже
  // отвечены, и главная кнопка уже называет отправку — «Далее» в этом состоянии нет.
  await card.getByRole("button", { name: "Вопрос 2 из 2" }).click();
  await page.getByRole("checkbox", { name: "Тесты" }).check();
  await page.getByRole("checkbox", { name: "Сборка" }).uncheck();
  await page.getByRole("button", { name: "Ответить" }).click();

  const last = await (await request.get("/__mock/last-answer")).json();
  expect(last.body.answers[0], "первый вопрос — изменённый ответ").toEqual({
    id: "q1",
    selected: ["Оставить как есть"],
  });
  expect(last.body.answers[1].selected, "второй — только «Тесты»").toEqual(["Тесты"]);
});

test("поверхность над лентой: нижний стек, непрозрачная, с тенью, свой предел и прокрутка", async ({
  page,
  request,
}) => {
  // Окно ниже 700 px — работают числа `@media` (D11 §4.2): min(45dvh, 420px) = 189 px при 420.
  await page.setViewportSize({ width: 360, height: 420 });
  await openSession(page, request, { question: "two" });
  const card = page.locator(".dp-question");
  await expect(card).toBeVisible();

  const view = await page.evaluate(() => {
    const node = document.querySelector(".dp-question") as HTMLElement;
    const stack = node.parentElement as HTMLElement;
    const style = getComputedStyle(node);
    const body = node.querySelector(".dp-question-body") as HTMLElement;
    const head = node.querySelector(".dp-question-head") as HTMLElement;
    const actions = node.querySelector(".dp-question-actions") as HTMLElement;
    const handle = document.querySelector(".dp-handle") as HTMLElement | null;
    const box = node.getBoundingClientRect();
    const actionBox = actions.getBoundingClientRect();
    return {
      inStack: stack !== null && stack.classList.contains("dp-bottom"),
      stackPosition: stack === null ? "" : getComputedStyle(stack).position,
      inFeed: node.closest(".dp-feed") !== null,
      background: style.backgroundColor,
      shadow: style.boxShadow,
      radius: style.borderTopLeftRadius,
      maxHeight: style.maxHeight,
      height: Math.round(box.height),
      column: style.flexDirection,
      bodyOverflow: getComputedStyle(body).overflowY,
      bodyScrolls: body.scrollHeight > body.clientHeight,
      headOverflow: getComputedStyle(head).overflowY,
      actionsOverflow: getComputedStyle(actions).overflowY,
      actionInside: actionBox.bottom <= box.bottom + 0.5 && actionBox.top >= box.top - 0.5,
      aboveHandle: handle !== null && box.bottom <= handle.getBoundingClientRect().top + 0.5,
      mainInert: document.querySelector("main")?.hasAttribute("inert") ?? false,
      sheets: document.querySelectorAll(".dp-sheet").length,
      scrims: document.querySelectorAll(".dp-sheet-scrim").length,
    };
  });

  expect(view.inStack, "вопрос — в нижнем стеке").toBe(true);
  expect(view.stackPosition, "стек — fixed у низа").toBe("fixed");
  expect(view.inFeed, "в ленте карточки больше нет — она поверхность").toBe(false);
  expect(view.background, "фон непрозрачный: под ним текст ленты").not.toBe("rgba(0, 0, 0, 0)");
  expect(view.shadow, "тень у поверхности").not.toBe("none");
  expect(view.radius, "радиус 8 — из принятого ряда 10 §3.4").toBe("8px");
  expect(view.maxHeight, "предел высоты по D11 §4.2").toBe("189px");
  expect(view.height, "карточка не выше предела").toBeLessThanOrEqual(189);
  expect(view.column, "колонка: голова → тело → действия").toBe("column");
  expect(view.bodyOverflow, "тело прокручивается своё").toBe("auto");
  expect(view.bodyScrolls, "в тесном окне тело действительно прокручивается").toBe(true);
  expect(view.headOverflow, "голова не прокручивается").toBe("visible");
  expect(view.actionsOverflow, "ряд действий не прокручивается").toBe("visible");
  expect(view.actionInside, "действие видно без прокрутки").toBe(true);
  expect(view.aboveHandle, "поверхность стоит над зацепом").toBe(true);
  expect(view.mainInert, "модальности нет: лента не заперта").toBe(false);
  expect(view.sheets, "листа поверх вопроса нет").toBe(0);
  expect(view.scrims, "скрима нет").toBe(0);
});

test("предел высоты вопроса: на 800 — не больше 440, действия видны; на 700 — медиазапрос", async ({
  page,
  request,
}) => {
  // V4/F2 (R1): базовое правило D11 §4.2 — `min(55dvh, 480px)`. При 800 это 440; в V4 карточка
  // выросла до 508, потому что в CSS стояло `max-height: none`. Проверка падает, если `none` вернуть.
  await page.setViewportSize({ width: 360, height: 800 });
  await openSession(page, request, { question: "two" });
  await expect(page.locator(".dp-question")).toBeVisible();

  const surface = () =>
    page.evaluate(() => {
      const node = document.querySelector(".dp-question") as HTMLElement;
      const body = node.querySelector(".dp-question-body") as HTMLElement;
      const actions = node.querySelector(".dp-question-actions") as HTMLElement;
      const box = node.getBoundingClientRect();
      const actionBox = actions.getBoundingClientRect();
      return {
        maxHeight: getComputedStyle(node).maxHeight,
        height: Math.round(box.height),
        viewportHeight: window.innerHeight,
        bodyOverflow: getComputedStyle(body).overflowY,
        actionInside: actionBox.bottom <= box.bottom + 0.5 && actionBox.top >= box.top - 0.5,
        actionVisible: actionBox.bottom <= window.innerHeight && actionBox.top >= 0,
      };
    });

  const big = await surface();
  expect(big.viewportHeight, "окно ровно 800 — медиазапрос не участвует").toBe(800);
  expect(big.maxHeight, "база: min(55dvh, 480px) при 800 = 440").toBe("440px");
  expect(big.height, "карточка не выше предела (до правки было 508)").toBeLessThanOrEqual(440);
  expect(big.bodyOverflow, "тело — своя прокрутка").toBe("auto");
  expect(big.actionInside, "ряд действий внутри карточки").toBe(true);
  expect(big.actionVisible, "и виден без прокрутки").toBe(true);

  // На 700 включается медиазапрос (`max-height: 700px`): min(45dvh, 420px) = 315.
  await page.setViewportSize({ width: 360, height: 700 });
  const small = await surface();
  expect(small.viewportHeight).toBe(700);
  expect(small.maxHeight, "медиазапрос на 700: min(45dvh, 420px)").toBe("315px");
  expect(small.height, "и высота карточки под ним").toBeLessThanOrEqual(315);
});

test("разметка вопросов: fieldset/legend на каждый, multiSelect — checkbox", async ({
  page,
  request,
}) => {
  await openSession(page, request, { question: "two" });
  const fieldsets = page.locator(".dp-question fieldset");
  await expect(fieldsets).toHaveCount(1);
  await expect(fieldsets.first().locator("legend")).toHaveText("Что делаем с упавшими тестами?");
  await expect(page.getByRole("radio")).toHaveCount(3);
  await expect(page.getByLabel("Свой ответ (необязательно)", { exact: true })).toHaveCount(1);

  // Вторая карточка — по стрелке: checkbox вместо radio, поле то же и всё ещё одно.
  await page.locator(".dp-question").getByRole("button", { name: "Вопрос 2 из 2" }).click();
  await expect(fieldsets).toHaveCount(1);
  await expect(fieldsets.first().locator("legend")).toHaveText("Какие проверки?");
  await expect(page.getByRole("checkbox")).toHaveCount(2);
  await expect(page.getByLabel("Свой ответ (необязательно)", { exact: true })).toHaveCount(1);
});

test("вопрос без вариантов: свои слова обязательны и уходят как ответ", async ({
  page,
  request,
}) => {
  await openSession(page, request, { question: "nooptions" });
  const send = page.getByRole("button", { name: "Ответить" });
  await expect(send).toBeDisabled();
  // Вариантов нет — метка БЕЗ «необязательно»: ответ здесь обязателен.
  await page.getByLabel("Свой ответ", { exact: true }).fill("Сделай по-своему");
  await expect(send).toBeEnabled();
  await send.click();
  await expect(page.getByText("Ответ отправлен."), "успех не печатается").toHaveCount(0);
  await expect(page.locator(".dp-question-done")).toHaveCount(0);
  const last = await (await request.get("/__mock/last-answer")).json();
  expect(last.body.answers[0]).toEqual({ id: "q1", selected: [], custom: "Сделай по-своему" });
});

test("отмена двухшаговая: сначала предупреждение, безопасный ответ первым", async ({
  page,
  request,
}) => {
  await openSession(page, request, { question: "single" });
  await page.getByRole("button", { name: "Отменить ход агента" }).click();
  await expect(page.getByText(/Отмена остановит текущий ход агента/)).toBeVisible();
  // WCAG 2.2 SC 2.4.3: раскрытие подтверждения ставит фокус на безопасный ответ.
  await expect(page.getByRole("button", { name: "Нет, оставить" })).toBeFocused();
  const buttons = page.locator(".dp-cancel button");
  await expect(buttons.first()).toHaveText("Нет, оставить");
  await buttons.first().click();
  await expect(page.getByText(/Отмена остановит текущий ход агента/)).toHaveCount(0);
  // …а закрытие ВОЗВРАЩАЕТ фокус на ту кнопку, которая раскрыла подтверждение.
  await expect(page.getByRole("button", { name: "Отменить ход агента" })).toBeFocused();

  await page.getByRole("button", { name: "Отменить ход агента" }).click();
  await page.getByRole("button", { name: "Да, отменить ход" }).click();
  await expect(page.getByText("Ход агента отменён: он больше не ждёт ответа.")).toBeVisible();
});

test("снят другим: карточка заменяется итогом, кнопок ответа нет", async ({ page, request }) => {
  // Настоящая последовательность (D11 §6.2): вопрос БЫЛ в этой сессии, потом его ответили на
  // компьютере. Итог «снят» печатается В СЕССИИ, на месте поверхности вопроса, и не гаснет;
  // в списке исходов нет вовсе — поэтому проверка идёт именно в сессии.
  await openSession(page, request, { question: "single" });
  await expect(page.getByRole("heading", { name: "Агент ждёт ответа" })).toBeVisible();
  await request.post("/__mock/scenario", { data: { question: "taken" } });

  // D1 §7 (11.11.2026): слово владельца — спокойное и точное; второй строки (PhonePageTakenNext) нет.
  await expect(
    page.getByText("Вопрос уже отвечен на компьютере — ничего делать не нужно."),
  ).toBeVisible({ timeout: 10_000 });
  await expect(page.getByText(/Что дальше — ничего: вопрос закрыт/)).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Ответить" })).toHaveCount(0);
  // Итог стоит НА МЕСТЕ поверхности вопроса — в нижнем стеке, а не в ленте.
  await expect(page.locator(".dp-bottom .dp-question-done")).toHaveCount(1);
});

test("устаревший вопрос: причина словами, ввод сохранён", async ({ page, request }) => {
  await openSession(page, request, { question: "single", answer: "stale" });
  await page.getByRole("radio", { name: "Починить сейчас" }).check();
  await page.getByRole("button", { name: "Ответить" }).click();
  const alert = page.getByRole("alert");
  await expect(alert).toContainText("Ответ не отправлен");
  await expect(alert).toContainText("уже снят");
  await expect(page.getByRole("radio", { name: "Починить сейчас" })).toBeChecked();
  await expect(page.getByRole("button", { name: "Повторить" })).toBeVisible();
});

test("неразобранный вопрос: ни карточки, ни кнопок — и НИКАКОГО исхода в списке", async ({
  page,
  request,
}) => {
  await openSession(page, request, { question: "unreadable" });
  // D11 §6.2 (DEF-147): исходы со списка убраны ВОВСЕ — и этот тоже. Человек видит, что вопроса
  // больше нет; окна «что-то случилось» не остаётся (его и жаловался владелец).
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Агент ждёт ответа" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Ответить" })).toHaveCount(0);
  await expect(page.locator(".dp-question-done"), "блока исхода в списке нет").toHaveCount(0);
  await expect(page.getByText(/Вопрос пришёл, но разобрать его панель не смогла/)).toHaveCount(0);
});

test("предел 10 минут: карточки нет, исход в списке не печатается", async ({ page, request }) => {
  await openSession(page, request, { question: "no-answer" });
  await page.goto("/");
  await expect(page.getByRole("button", { name: "Ответить" })).toHaveCount(0);
  // Список исходов не показывает (D11 §6.2) — и «ответа не было» тоже.
  await expect(page.locator(".dp-question-done")).toHaveCount(0);
  await expect(page.getByText(/за 10 мин\. ответа не было/)).toHaveCount(0);
});

test("вопрос без сессии: карточка вверху списка, ответ уходит", async ({ page, request }) => {
  await page.request.post("/__mock/scenario", {
    data: { authed: true, list: "ok", question: "fallback", answer: "ok" },
  });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Агент ждёт ответа" })).toBeVisible();
  await page.getByRole("radio", { name: "Починить сейчас" }).check();
  await page.getByRole("button", { name: "Ответить" }).click();
  // DEF-147 дословно: окно «ответ отправлен» в списке больше НЕ висит — его нет совсем.
  await expect(page.getByText("Ответ отправлен."), "окна успеха в списке нет").toHaveCount(0);
  await expect(page.locator(".dp-question-done"), "блока исхода в списке нет").toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Агент ждёт ответа" })).toHaveCount(0);
});

test("на списке нет блока исхода — ни успеха, ни «снят другим» (D11 §6.2)", async ({
  page,
  request,
}) => {
  // Ровно случай из слова владельца: ответили на карточку В СПИСКЕ — и окно статуса висело там вечно.
  await request.post("/__mock/scenario", {
    data: { authed: true, list: "ok", question: "fallback", answer: "ok" },
  });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Агент ждёт ответа" })).toBeVisible();
  await page.getByRole("radio", { name: "Починить сейчас" }).check();
  await page.getByRole("button", { name: "Ответить" }).click();
  // Карточки нет — и НИКАКОГО блока исхода: ни `.dp-question-done`, ни слов успеха.
  await expect(page.getByRole("heading", { name: "Агент ждёт ответа" })).toHaveCount(0);
  await expect(page.locator(".dp-question-done"), "блок исхода в списке не рендерится").toHaveCount(
    0,
  );
  await expect(page.getByText("Ответ отправлен.")).toHaveCount(0);

  // «Снят другим» и «отменён» тоже не печатаются. Ждём БОЛЬШЕ ОДНОГО ТАКТА ОПРОСА (2 с): иначе
  // проверка пройдёт до того, как исход вообще будет получен, и возвращённый рендер не поймает.
  await request.post("/__mock/scenario", { data: { question: "taken" } });
  await page.waitForTimeout(2_500);
  await expect(page.locator(".dp-question-done"), "«снят другим» тоже нет").toHaveCount(0);
  await request.post("/__mock/scenario", { data: { question: "cancelled" } });
  await page.waitForTimeout(2_500);
  await expect(page.locator(".dp-question-done"), "«отменён» тоже нет").toHaveCount(0);
  await expect(page.getByRole("button", { name: "Ответить" })).toHaveCount(0);
});

test("вопрос другой сессии: строка со ссылкой, а не карточка", async ({ page, request }) => {
  await openSession(page, request, { question: "single", questionSession: "session-question" });
  await expect(page.getByText("Другой агент ждёт ответа")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Агент ждёт ответа" })).toHaveCount(0);
  await page.getByRole("link", { name: "Открыть" }).click();
  await expect(page).toHaveURL(/session=session-question/);
});

test("список: строка с вопросом помечена «Ждёт ответа» по опросу", async ({ page, request }) => {
  await page.request.post("/__mock/scenario", {
    data: { authed: true, list: "ok", question: "single", questionSession: "session-done" },
  });
  await page.goto("/");
  await expect(page.getByRole("link", { name: /Обновление панели/ })).toContainText("Ждёт ответа");
});

test("ссылки на старый /ask в новом пульте нет", async ({ page, request }) => {
  await openSession(page, request, { question: "single" });
  await expect(page.locator('a[href="/ask"]')).toHaveCount(0);
});

test("цели карточки ≥ 44 px, поле — с подписью", async ({ page, request }) => {
  await openSession(page, request, { question: "single" });
  const label = page.locator(".dp-choice").first();
  const box = await label.boundingBox();
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
  for (const name of ["Ответить", "Отменить ход агента"]) {
    const target = page.getByRole("button", { name });
    const metrics = await target.boundingBox();
    expect(metrics?.height ?? 0, name).toBeGreaterThanOrEqual(44);
  }
  await expect(page.getByLabel("Свой ответ (необязательно)")).toBeVisible();
});

test("поднятый лист ввода запирает НИЖНИЙ СТЕК: вопрос и очередь инертны, фокус — только в листе", async ({
  page,
  request,
}) => {
  // Стек — портал в `body`, поэтому `inert` на `<main>` его НЕ накрывает (S4a/D1: «страница под
  // скримом инертна»). С W10 в стеке стоит ФОРМА вопроса — и без `inert` она остаётся в дереве
  // доступности под скримом: читалка читает то, чего человек не видит.
  await openSession(page, request, { question: "single" });
  await expect(page.locator(".dp-question")).toBeVisible();
  const stackInert = () =>
    page.evaluate(
      () => (document.querySelector(".dp-bottom") as HTMLElement | null)?.inert ?? false,
    );
  expect(await stackInert(), "до листа стек живой").toBe(false);

  // Поднимаем лист по зацепу.
  await page.locator(".dp-handle").click();
  await expect(page.locator(".dp-sheet")).toBeVisible();
  await expect(page.locator("main"), "страница под скримом инертна").toHaveAttribute("inert", "");
  expect(await stackInert(), "и стек тоже инертен").toBe(true);

  // Программный фокус на поле вопроса НЕ переводит фокус: активный элемент остаётся в листе.
  const jumped = await page.evaluate(() => {
    const field = document.querySelector<HTMLElement>(".dp-question textarea, .dp-question input");
    field?.focus();
    const active = document.activeElement;
    return active !== null && active.closest(".dp-bottom") !== null;
  });
  expect(jumped, "фокус не уходит в запертый стек").toBe(false);
  expect(await page.evaluate(() => document.activeElement?.closest(".dp-sheet") !== null)).toBe(
    true,
  );

  // Закрыли лист — стек снова живой, зацеп снова фокусируем.
  await page.locator(".dp-sheet-scrim").click({ position: { x: 10, y: 10 } });
  await expect(page.locator(".dp-sheet")).toHaveCount(0);
  expect(await stackInert(), "после закрытия — снят").toBe(false);
  await page.locator(".dp-handle").focus();
  expect(
    await page.evaluate(() => document.activeElement?.classList.contains("dp-handle") ?? false),
  ).toBe(true);
});
