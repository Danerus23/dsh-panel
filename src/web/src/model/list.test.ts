import { describe, expect, it } from "vitest";
import { makeT } from "../words";
import {
  parseList,
  parseSession,
  stateTone,
  stateWordKey,
  toRow,
  visibleCounts,
  visibleItems,
  type SessionItem,
  type SessionList,
} from "./list";

const t = makeT({
  PhoneWebStateWaiting: "Ждёт ответа",
  PhoneWebStateRunning: "Работает",
  PhoneWebStateBlank: "Ещё не было задачи",
  PhoneWebStateDone: "Закончила",
  PhoneWebUntitled: "Без названия",
  PhoneWebInboxFormat: "В очереди: {0}",
});

function raw(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    sessionId: "session-1",
    title: "Починить калитку",
    updatedAt: 1761770000000,
    running: false,
    blank: false,
    question: false,
    stats: { turns: 3, steps: 9 },
    outline: [
      { turn: 1, seq: 10, prompt: "a", response: "старое" },
      { turn: 2, seq: 41, prompt: "b", response: "последнее" },
    ],
    inbox: { nextTurn: 0, nextStep: 1 },
    ...overrides,
  };
}

describe("строка списка", () => {
  it("читает только белый список полей", () => {
    const item = parseSession({ ...raw(), cwd: "C:\\секрет", workingDirectory: "C:\\секрет" });
    expect(item).not.toBeNull();
    expect(JSON.stringify(item)).not.toContain("секрет");
    expect(item?.turns).toBe(3);
    expect(item?.steps).toBe(9);
    expect(item?.inbox).toBe(1);
    expect(item?.outline).toBe("последнее");
  });

  it("состояние — словом, и вопрос сильнее работы", () => {
    expect(stateWordKey(parseSession(raw({ question: true, running: true }))!)).toBe(
      "PhoneWebStateWaiting",
    );
    expect(stateWordKey(parseSession(raw({ running: true }))!)).toBe("PhoneWebStateRunning");
    expect(stateWordKey(parseSession(raw({ blank: true }))!)).toBe("PhoneWebStateBlank");
    expect(stateWordKey(parseSession(raw())!)).toBe("PhoneWebStateDone");
  });

  it("без названия — «Без названия», ссылка ведёт запросом", () => {
    const row = toRow(parseSession(raw({ title: null }))!, t);
    expect(row.title).toBe("Без названия");
    expect(row.href).toBe("/?session=session-1");
  });

  it("«в очереди» показывается только когда есть что показать", () => {
    expect(toRow(parseSession(raw())!, t).inbox).toBe("В очереди: 1");
    expect(toRow(parseSession(raw({ inbox: { nextTurn: 0, nextStep: 0 } }))!, t).inbox).toBeNull();
  });

  it("лимит 50 и счёт total/shown", () => {
    const items = Array.from({ length: 60 }, (_, index) => raw({ sessionId: `s-${index}` }));
    const list = parseList({ items, total: 60, shown: 60 });
    expect(list.items).toHaveLength(50);
    expect(list.total).toBe(60);
    expect(list.shown).toBe(50);
  });

  it("строка без адреса и мусор отбрасываются, а счёт остаётся честным", () => {
    const list = parseList({ items: [raw(), { title: "без адреса" }, 42], total: 3, shown: 3 });
    expect(list.items).toHaveLength(1);
    // Всего корневых три, показать удалось одну — экран не должен врать «одна из одной».
    expect(list.total).toBe(3);
    expect(list.shown).toBe(1);
  });

  it("название режется до 200 знаков", () => {
    const item = parseSession(raw({ title: "я".repeat(500) }));
    expect(item?.title).toHaveLength(200);
  });

  it("вопрос из опроса сильнее работы: метка ставится сразу, не ждёт ответа списка", () => {
    const item = parseSession(raw({ running: true, question: false }))!;
    expect(stateWordKey(item, "session-1")).toBe("PhoneWebStateWaiting");
    expect(toRow(item, t, "session-1").state).toBe("Ждёт ответа");
    expect(stateWordKey(item, "session-other")).toBe("PhoneWebStateRunning");
  });
  it("тон состояния идёт ТЕМ ЖЕ порядком, что слово: цвет — второй носитель, слово остаётся", () => {
    expect(stateTone(parseSession(raw({ question: true, running: true }))!)).toBe("warn");
    expect(stateTone(parseSession(raw({ running: true }))!)).toBe("accent");
    expect(stateTone(parseSession(raw({ blank: true }))!)).toBe("neutral");
    expect(stateTone(parseSession(raw())!)).toBe("good");
    // Вопрос из опроса красится так же, как словом (метка не ждёт ответа списка).
    expect(stateTone(parseSession(raw({ running: true }))!, "session-1")).toBe("warn");
    // Строка несёт и слово, и тон: экран не выбирает одно вместо другого.
    const row = toRow(parseSession(raw({ blank: true }))!, t);
    expect(row.state).toBe("Ещё не было задачи");
    expect(row.tone).toBe("neutral");
  });

  it("счётчик задач в строке — число, а фраза остаётся для чтения с экрана", () => {
    const row = toRow(parseSession(raw())!, t);
    expect(row.inboxCount).toBe(1);
    expect(row.inbox).toBe("В очереди: 1");
    expect(
      toRow(parseSession(raw({ inbox: { nextTurn: 0, nextStep: 0 } }))!, t).inboxCount,
    ).toBeNull();
  });
});

describe("blank-сессии: телефон показывает как DSH (D1 §6, 11.11.2026)", () => {
  function item(overrides: Record<string, unknown> = {}): SessionItem {
    return parseSession(raw(overrides))!;
  }

  function listOf(items: SessionItem[], total = items.length, shown = items.length): SessionList {
    return { items, total, shown };
  }

  it("6 строк, из них 2 blank → 4 видимые, и «Сессий: N» = видимым", () => {
    const list = listOf([
      item({ sessionId: "s-1" }),
      item({ sessionId: "s-2" }),
      item({ sessionId: "s-3" }),
      item({ sessionId: "s-4" }),
      item({ sessionId: "s-blank-1", title: null, blank: true }),
      item({ sessionId: "s-blank-2", title: null, blank: true }),
    ]);

    expect(visibleItems(list.items)).toHaveLength(4);
    expect(visibleCounts(list)).toEqual({ count: 4, total: 4, truncated: false });
  });

  it("blank с висящим вопросом остаётся и говорит «Ждёт ответа», а не «Ещё не было задачи»", () => {
    const blankWithQuestion = item({
      sessionId: "s-question",
      title: null,
      blank: true,
      question: true,
    });
    const list = listOf([item({ sessionId: "s-1" }), blankWithQuestion]);

    expect(visibleItems(list.items)).toHaveLength(2);
    // Порядок слов уже ставит вопрос выше blank (model\list.ts) — правки не нужно.
    expect(stateWordKey(blankWithQuestion)).toBe("PhoneWebStateWaiting");
    expect(toRow(blankWithQuestion, t).state).toBe("Ждёт ответа");
  });

  it("blank из опроса (сессия вопроса известна до ответа списка) тоже остаётся", () => {
    const list = listOf([
      item({ sessionId: "s-1" }),
      item({ sessionId: "s-question", title: null, blank: true }),
      item({ sessionId: "s-blank", title: null, blank: true }),
    ]);

    expect(visibleItems(list.items, "s-question").map((row) => row.sessionId)).toEqual([
      "s-1",
      "s-question",
    ]);
    expect(visibleCounts(list, "s-question").count).toBe(2);
  });

  it("все blank → ни одной строки: экран говорит пустым состоянием", () => {
    const list = listOf([
      item({ sessionId: "s-blank-1", title: null, blank: true }),
      item({ sessionId: "s-blank-2", title: null, blank: true }),
    ]);

    expect(visibleItems(list.items)).toHaveLength(0);
    expect(visibleCounts(list).count).toBe(0);
  });

  it("обрезанная страница: хвост считается по видимым, скрытые на странице вычитаются", () => {
    const items = Array.from({ length: 6 }, (_, index) =>
      index < 2
        ? item({ sessionId: `s-blank-${index}`, title: null, blank: true })
        : item({ sessionId: `s-${index}` }),
    );
    const list = listOf(items, 60, 50);

    // 6 разобрано, 2 из них blank; всего в калитке 60, показать пытались 50 → обрезка была.
    expect(visibleCounts(list)).toEqual({ count: 4, total: 58, truncated: true });
  });
});
