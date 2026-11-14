import { expect, test, type APIRequestContext, type Page } from "@playwright/test";

// W24 (живая находка владельца 14.11.2026): в повторно открытом листе висели «Задача принята.
// В очереди: 0», хотя в этой сессии человек ничего не отправлял.
//
// Два слоя: (а) ПОКАЗ — лист показывал строку исхода и счёт «0» (D11 §3.5: ноль — пустая величина,
// его не печатаем); слова успеха — строка НАД зацепом на ~5 с, а не в листе; (б) КОРЕНЬ — экран
// сессии не пересоздаётся при смене sessionId, и исход отправки прошлой сессии переезжал в новую.
//
// Что доказывается прогоном:
//   1. после приёма и повторного открытия листа в `.dp-composer-slot` нет «Задача принята.» и
//      нет «В очереди: 0»; счёт при N > 0 остаётся;
//   2. строка «Задача принята.» над зацепом по-прежнему появляется сразу после приёма (5 с);
//   3. A → B (через «Другой агент ждёт ответа → Открыть» — это переход БЕЗ перезагрузки, экран
//      живёт с новым sessionId): успех и «В очереди: 0» в B не переезжают;
//   4. отказ в A в B не переезжает: ни слов отказа, ни «Повторить».
//
// Мутации, которые обязаны ронять эти проверки: `inbox = total` (0), слова accepted при открытом
// листе, отсутствие сброса исхода при смене sessionId — имена и числа в отчёте.

const WORDS = {
  send: "Отправить задачу",
  accepted: "Задача принята.",
  repeat: "Повторить",
  failure: "Задача не принята",
  inboxZero: "В очереди: 0",
};

/** Сценарий стенда: по умолчанию всё открыто, вопрос — «нет» (не протекает между тестами). */
async function scenario(
  request: APIRequestContext,
  data: Record<string, unknown> = {},
): Promise<void> {
  await request.post("/__mock/scenario", {
    data: {
      authed: true,
      lang: "ru",
      list: "ok",
      pair: "ok",
      question: "none",
      task: "ok",
      queue: "ok",
      ...data,
    },
  });
  await request.post("/__mock/s3-reset", { data: {} });
}

/** Открыть лист ввода зацепом. */
async function openSheet(page: Page): Promise<void> {
  await page.locator(".dp-handle").click();
  await expect(page.locator(".dp-sheet")).toBeVisible();
}

/** Закрыть лист Escape (кнопка «свернуть» — тот же путь, это не предмет проверки). */
async function closeSheet(page: Page): Promise<void> {
  await page.keyboard.press("Escape");
  await expect(page.locator(".dp-sheet")).toHaveCount(0);
}

/** Открыть сессию и дождаться ленты. */
async function openSession(page: Page, sessionId: string): Promise<void> {
  await page.goto(`/?session=${sessionId}`);
  await expect(page.locator(".dp-feed li").last()).toBeVisible();
}

/** Текст строки полосы: там живут слова исхода и счёт («В очереди: N»). */
async function slotText(page: Page): Promise<string> {
  return (await page.locator(".dp-composer-slot").textContent()) ?? "";
}

/** Перейти в другую сессию ЖИВЫМ путём (без перезагрузки): ссылка «Открыть» у чужого вопроса. */
async function openForeignSession(page: Page): Promise<void> {
  await expect(page.getByText("Другой агент ждёт ответа")).toBeVisible();
  await page.getByRole("link", { name: "Открыть", exact: true }).click();
  // Адрес и карточка вопроса (теперь он — вопрос ЭТОЙ сессии) доказывают: экран живёт с новым id,
  // перезагрузки не было.
  await expect(page).toHaveURL(/[?&]session=session-question/u);
  await expect(page.locator(".dp-question"), "вопрос этой сессии — карточка").toBeVisible();
}

/** Длинная лента не нужна: берём обычную сессию и её живой конец. */
const SESSION = "session-live";

test.describe("W24: исход отправки не висит в листе и не переезжает в другую сессию", () => {
  test.afterEach(async ({ request }) => {
    // Стенд общий на прогон: возвращаем умолчания, иначе вопрос/пустая очередь/отказ текут
    // в следующие файлы (сценарий стенда глобальный; `s3-reset` внутри заодно чистит состав).
    await scenario(request);
  });

  test("1. после приёма повторно открытый лист: нет «Задача принята.» и «В очереди: 0», счёт при N>0 виден", async ({
    page,
    request,
  }) => {
    await scenario(request, { queue: "ok" });
    await openSession(page, SESSION);
    await expect(page.locator(".dp-queue-row")).toContainText("В очереди: 2");

    await openSheet(page);
    await page.locator(".dp-sheet textarea").fill("Проверь строку исхода");
    await page.locator(".dp-sheet").getByRole("button", { name: WORDS.send }).click();

    // Успех — строкой НАД зацепом (это правило не меняется), лист закрылся сам.
    await expect(page.locator(".dp-handle-note"), "успех — над зацепом").toContainText(
      WORDS.accepted,
    );
    await expect(page.locator(".dp-sheet"), "лист закрылся сам после приёма").toHaveCount(0);

    // Повторное открытие листа: успех в листе не висит, нуля нет.
    await openSheet(page);
    const text = await slotText(page);
    expect(text, "в листе нет «Задача принята.»").not.toContain(WORDS.accepted);
    expect(text, "в листе нет «В очереди: 0»").not.toContain(WORDS.inboxZero);
    // Счёт при N > 0 остаётся: принятая задача и правда в очереди (2 → 3).
    await expect(page.locator(".dp-composer-slot"), "счёт при N > 0 виден").toContainText(
      /В очереди: [1-9]\d*/u,
    );

    // Закрыли лист — прошлый успех над зацепом не воскресает (он уже был показан один раз).
    await closeSheet(page);
    const note = page.locator(".dp-handle-note");
    if ((await note.count()) > 0) {
      const noteText = (await note.textContent()) ?? "";
      expect(noteText, "успех не вернулся после листа").not.toContain(WORDS.accepted);
    }
  });

  test("2. смена сессии (вопрос другого агента): успех и «В очереди: 0» не переезжают в B", async ({
    page,
    request,
  }) => {
    // Вопрос ждёт в ДРУГОЙ сессии: переход «Открыть» меняет sessionId без перезагрузки —
    // ровно тот путь, на котором живой экран перевозил исход прошлой отправки.
    await scenario(request, { question: "single", questionSession: "session-question" });
    await openSession(page, SESSION);

    await openSheet(page);
    await page.locator(".dp-sheet textarea").fill("Почини сборку");
    await page.locator(".dp-sheet").getByRole("button", { name: WORDS.send }).click();
    await expect(page.locator(".dp-handle-note"), "успех — над зацепом").toContainText(
      WORDS.accepted,
    );

    // В сессии B очередь пуста: именно «В очереди: 0» и печаталось в находке владельца.
    await request.post("/__mock/scenario", { data: { queue: "empty" } });
    await openForeignSession(page);

    // B: над зацепом пусто. Считаем СРАЗУ (без ожидания до 5 с): иначе успех успел бы погаснуть
    // сам, и проверка не поймала бы переезд исхода.
    const earlyNote = await page.locator(".dp-handle-note").count();
    expect(earlyNote, "сразу после перехода в B строки успеха нет").toBe(0);
    await openSheet(page);
    const text = await slotText(page);
    expect(text, "в B нет «Задача принята.»").not.toContain(WORDS.accepted);
    expect(text, "в B нет «В очереди: 0»").not.toContain(WORDS.inboxZero);
    expect(text, "в B нет иного счёта — очередь пуста").not.toContain("В очереди");
  });

  test("3. отказ в A не переезжает в B: слов отказа и «Повторить» там нет", async ({
    page,
    request,
  }) => {
    await scenario(request, {
      question: "single",
      questionSession: "session-question",
      task: "not-taken",
    });
    await openSession(page, SESSION);

    await openSheet(page);
    await page.locator(".dp-sheet textarea").fill("Почини сборку");
    await page.locator(".dp-sheet").getByRole("button", { name: WORDS.send }).click();
    // Отказ: лист остаётся, слова и «Повторить» на месте (это правило фикс не трогает).
    await expect(page.locator(".dp-composer-slot")).toContainText(WORDS.failure);
    await expect(
      page.locator(".dp-sheet").getByRole("button", { name: WORDS.repeat }),
    ).toBeVisible();
    await closeSheet(page);

    await openForeignSession(page);

    // B: отказа нет — ни словами, ни ярлыком повтора.
    const note = page.locator(".dp-handle-note");
    if ((await note.count()) > 0) {
      expect((await note.textContent()) ?? "", "в B над зацепом нет отказа").not.toContain(
        WORDS.failure,
      );
    }
    await openSheet(page);
    expect(await slotText(page), "в B нет слов отказа").not.toContain(WORDS.failure);
    await expect(
      page.locator(".dp-sheet").getByRole("button", { name: WORDS.send }),
      "в B главная кнопка снова «Отправить задачу»",
    ).toBeVisible();
    await expect(
      page.locator(".dp-sheet").getByRole("button", { name: WORDS.repeat }),
      "и «Повторить» нет",
    ).toHaveCount(0);
  });

  test("4. при N > 0 счёт «В очереди: N» виден, при N = 0 — не печатается", async ({
    page,
    request,
  }) => {
    // Стоп даёт исход, у которого есть счёт: строка показывает слова исхода и «В очереди: 2».
    await scenario(request, { queue: "ok", stop: "stopped" });
    await openSession(page, SESSION);
    await expect(page.locator(".dp-queue-row")).toContainText("В очереди: 2");
    await openSheet(page);
    await page.locator(".dp-sheet").getByRole("button", { name: "Остановить" }).click();
    await expect(page.locator(".dp-composer-slot"), "счёт в листе при N > 0").toContainText(
      "В очереди: 2",
    );
    expect(await slotText(page), "нуля нет").not.toContain("В очереди: 0");

    // Ноль — пустая величина: пустая очередь ПРИ ИСХОДЕ (стоп) не печатает «В очереди: 0».
    await request.post("/__mock/scenario", { data: { queue: "empty" } });
    await page.locator(".dp-sheet").getByRole("button", { name: "Остановить" }).click();
    // Дожидаемся именно ПУСТОЙ очереди: иначе «счёта нет» прошло бы на миг «останавливаю…».
    await expect(page.locator(".dp-queue-row"), "очередь пуста — строки нет").toHaveCount(0);
    await expect(
      page.locator(".dp-composer-slot"),
      "счёт при N = 0 не печатается",
    ).not.toContainText("В очереди");
    await closeSheet(page);
  });
});
