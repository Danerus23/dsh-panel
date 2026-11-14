import { describe, expect, it } from "vitest";
import {
  buildAnswerItems,
  canSend,
  emptyAnswers,
  MAX_CUSTOM,
  nextUnansweredIndex,
  parseQuestion,
  pollAllowed,
  toggleChoice,
  waitMinutes,
  type AskCard,
  type AnswerValue,
} from "./question";

function questionRaw(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    state: "question",
    eventId: "evt-1",
    sessionId: "session-1",
    fromSubagent: false,
    expiresAt: "2026-10-30T02:00:00+03:00",
    remainingSeconds: 540,
    questions: [
      {
        id: "q1",
        question: "Что делаем с упавшими тестами?",
        detail: "Два теста.",
        header: "Сборка",
        options: [
          { label: "Починить сейчас" },
          { label: "Оставить как есть", description: "Запишем в долги" },
        ],
      },
    ],
    ...overrides,
  };
}

const cards: AskCard[] = [
  {
    id: "q1",
    question: "Что делаем?",
    detail: null,
    header: null,
    options: [
      { label: "Починить", description: null },
      { label: "Отложить", description: null },
    ],
    multiSelect: false,
  },
  {
    id: "q2",
    question: "Какие проверки?",
    detail: null,
    header: null,
    options: [
      { label: "Сборка", description: null },
      { label: "Тесты", description: null },
    ],
    multiSelect: true,
  },
];

describe("состояние вопроса", () => {
  it("вопрос разбирается по белому списку: id, текст, варианты, multiple", () => {
    const parsed = parseQuestion(questionRaw());
    expect(parsed?.kind).toBe("question");
    if (parsed?.kind !== "question") return;
    expect(parsed.eventId).toBe("evt-1");
    expect(parsed.sessionId).toBe("session-1");
    expect(parsed.cards).toHaveLength(1);
    expect(parsed.cards[0]?.options[1]?.description).toBe("Запишем в долги");
    expect(parsed.cards[0]?.multiSelect).toBe(false);
  });

  it("запрещённые поля не проходят в модель", () => {
    const raw = questionRaw({ clientId: "client-secret", agentId: "agent-secret" });
    const questions = raw["questions"] as Array<Record<string, unknown>>;
    questions[0]!["intent"] = { kind: "plan-review" };
    const parsed = parseQuestion(raw);
    const text = JSON.stringify(parsed);
    expect(text).not.toContain("client-secret");
    expect(text).not.toContain("agent-secret");
    expect(text).not.toContain("plan-review");
  });

  it("multiSelect без поля — один выбор", () => {
    const parsed = parseQuestion(questionRaw());
    expect(parsed?.kind === "question" && parsed.cards[0]?.multiSelect).toBe(false);
  });

  it("итоги разбираются все: answered/taken/cancelled/no-answer/unreadable/none", () => {
    for (const kind of ["answered", "taken", "cancelled", "no-answer", "unreadable", "none"]) {
      const parsed = parseQuestion({ state: kind, eventId: "evt-1", message: "слова" });
      expect(parsed?.kind).toBe(kind);
      if (parsed !== null && parsed.kind !== "question") expect(parsed.message).toBe("слова");
    }
  });

  it("кривой ответ не превращается в полукарточку", () => {
    expect(parseQuestion(null)).toBeNull();
    expect(parseQuestion({ state: "question", eventId: "", questions: [] })).toBeNull();
    expect(parseQuestion({ state: "question", eventId: "e", questions: [] })).toBeNull();
    expect(parseQuestion({ state: "unknown" })).toBeNull();
    expect(
      parseQuestion({
        state: "question",
        eventId: "e",
        questions: [{ question: "без id" }],
      }),
    ).toBeNull();
  });

  it("больше 20 вопросов — не карточка (калитка такое не присылает)", () => {
    const questions = Array.from({ length: 21 }, (_, index) => ({
      id: `q${index}`,
      question: "вопрос",
    }));
    expect(parseQuestion({ state: "question", eventId: "e", questions })).toBeNull();
  });
});

describe("ответ", () => {
  it("radio — один выбор, checkbox — много, повтор снимает", () => {
    const single = toggleChoice({ selected: [], custom: "" }, "Починить", false);
    expect(single.selected).toEqual(["Починить"]);
    const replaced = toggleChoice(single, "Отложить", false);
    expect(replaced.selected).toEqual(["Отложить"]);

    let multi: AnswerValue = { selected: [], custom: "" };
    multi = toggleChoice(multi, "Сборка", true);
    multi = toggleChoice(multi, "Тесты", true);
    expect(multi.selected).toEqual(["Сборка", "Тесты"]);
    multi = toggleChoice(multi, "Сборка", true);
    expect(multi.selected).toEqual(["Тесты"]);
  });

  it("«Далее» ведёт к следующей НЕОТВЕЧЕННОЙ по кругу — D11 §5.2", () => {
    // Две карточки, обе без ответа: с первой «Далее» ведёт на вторую, со второй — обратно на первую.
    const empty = emptyAnswers(cards);
    expect(nextUnansweredIndex(cards, empty, 0)).toBe(1);
    expect(nextUnansweredIndex(cards, empty, 1)).toBe(0);

    // Первая отвечена: со второй идти некуда — идти можно только на неотвеченную, а она текущая.
    const first = emptyAnswers(cards);
    first["q1"] = { selected: ["Починить"], custom: "" };
    expect(nextUnansweredIndex(cards, first, 1)).toBeNull();
    // А с первой (она отвечена) — ведёт на вторую: она единственная неотвеченная.
    expect(nextUnansweredIndex(cards, first, 0)).toBe(1);

    // Три карточки: ответил на первую и третью, стою на первой — «Далее» ведёт на среднюю.
    const three: AskCard[] = [cards[0]!, cards[1]!, { ...cards[1]!, id: "q3" }];
    const partial = emptyAnswers(three);
    partial["q1"] = { selected: ["Починить"], custom: "" };
    partial["q3"] = { selected: ["Тесты"], custom: "" };
    expect(nextUnansweredIndex(three, partial, 0)).toBe(1);
    // Все отвечены — «Далее» некуда вести.
    partial["q2"] = { selected: ["Сборка"], custom: "" };
    expect(nextUnansweredIndex(three, partial, 0)).toBeNull();
    expect(nextUnansweredIndex(three, partial, 2)).toBeNull();

    // Один вопрос — «Далее» не существует (вести некуда).
    expect(nextUnansweredIndex([cards[0]!], empty, 0)).toBeNull();
    // Номер вне диапазона не ломает функцию: считается крайним.
    expect(nextUnansweredIndex(cards, empty, 99)).toBe(0);
    expect(nextUnansweredIndex(cards, empty, -5)).toBe(1);
  });
  it("ответ обязателен на КАЖДЫЙ вопрос", () => {
    const values = emptyAnswers(cards);
    expect(canSend(cards, values)).toBe(false);
    values["q1"] = { selected: ["Починить"], custom: "" };
    expect(canSend(cards, values)).toBe(false);
    values["q2"] = { selected: [], custom: "  " };
    expect(canSend(cards, values)).toBe(false);
    values["q2"] = { selected: ["Сборка"], custom: "" };
    expect(canSend(cards, values)).toBe(true);
  });

  it("свои слова без вариантов — законный ответ", () => {
    const text: AskCard[] = [
      {
        id: "q1",
        question: "Опишите",
        detail: null,
        header: null,
        options: [],
        multiSelect: false,
      },
    ];
    const values = emptyAnswers(text);
    values["q1"] = { selected: [], custom: "Сделай по-своему" };
    expect(canSend(text, values)).toBe(true);
    expect(buildAnswerItems(text, values)).toEqual([
      { id: "q1", selected: [], custom: "Сделай по-своему" },
    ]);
  });

  it("тело ответа: все вопросы одним набором, пустой custom не шлётся", () => {
    const values = emptyAnswers(cards);
    values["q1"] = { selected: ["Починить"], custom: "" };
    values["q2"] = { selected: ["Сборка", "Тесты"], custom: "и формат" };
    expect(buildAnswerItems(cards, values)).toEqual([
      { id: "q1", selected: ["Починить"] },
      { id: "q2", selected: ["Сборка", "Тесты"], custom: "и формат" },
    ]);
  });

  it("неполный набор не отправляется вовсе", () => {
    const values = emptyAnswers(cards);
    values["q1"] = { selected: ["Починить"], custom: "" };
    expect(buildAnswerItems(cards, values)).toBeNull();
  });

  it("свои слова режутся до 500 знаков — предела калитки", () => {
    const text: AskCard[] = [
      {
        id: "q1",
        question: "Опишите",
        detail: null,
        header: null,
        options: [],
        multiSelect: false,
      },
    ];
    const values = emptyAnswers(text);
    values["q1"] = { selected: [], custom: "я".repeat(700) };
    const items = buildAnswerItems(text, values);
    expect(items?.[0]?.custom).toHaveLength(MAX_CUSTOM);
  });
});

describe("пауза опроса", () => {
  it("пока человек в карточке — не опрашиваем", () => {
    expect(pollAllowed({ focusedInCard: true, suspendedUntil: 0, now: 100 })).toBe(false);
  });

  it("3 с после ввода — тишина, потом снова можно", () => {
    expect(pollAllowed({ focusedInCard: false, suspendedUntil: 5_000, now: 4_999 })).toBe(false);
    expect(pollAllowed({ focusedInCard: false, suspendedUntil: 5_000, now: 5_000 })).toBe(true);
  });

  it("минуты ожидания считаются из секунд", () => {
    expect(waitMinutes(600)).toBe(10);
    expect(waitMinutes(0)).toBe(0);
  });
});
