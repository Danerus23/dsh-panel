// «Ход идёт?» — по событиям `turn`/`turn-end` ленты (контракт §5.8).
// От этого зависит, видна ли строка стопа: «Остановить» появляется только когда есть что останавливать.

import { describe, expect, it } from "vitest";
import { isRunActive, sessionState, turnEvents } from "./run";
import { parseSession, type SessionItem } from "./list";
import type { FeedRecord } from "./records";

const T = 1_761_770_000_000;

function turn(seq: number, number: number): FeedRecord {
  return { type: "turn", seq, time: T, turn: number };
}

function end(seq: number, number: number): FeedRecord {
  return { type: "turn-end", seq, time: T, turn: number, ok: true, reason: "completed" };
}

const user: FeedRecord = { type: "user", seq: 2, time: T, text: "почини", attachment: false };

describe("ход идёт", () => {
  it("последний turn без turn-end — ход идёт", () => {
    expect(isRunActive([turn(1, 1), end(2, 1), turn(3, 2), user], null)).toBe(true);
  });

  it("последний turn-end — ход кончился, стоп не нужен", () => {
    expect(isRunActive([turn(1, 1), end(2, 1)], null)).toBe(false);
  });

  it("пустая лента: решает живой черновик ответа, иначе — запасной признак списка", () => {
    expect(isRunActive([], null, false)).toBe(false);
    expect(
      isRunActive([], { text: "сейчас", startedAfterSeq: 0, turn: 2, step: 0, attemptId: "a" }),
    ).toBe(true);
    expect(isRunActive([], null, true)).toBe(true);
  });

  it("событий хода в ленте — по ним экран перечитывает очередь", () => {
    expect(turnEvents([turn(1, 1), user, end(3, 1), turn(4, 2)])).toBe(3);
    expect(turnEvents([user])).toBe(0);
  });
});

// Состояние шапки сессии — ИЗ ЖИВЫХ данных (D3 §1.2): строка списка не источник.
describe("состояние шапки — живое", () => {
  function item(overrides: Record<string, unknown> = {}): SessionItem {
    return parseSession({
      sessionId: "s-1",
      title: "Работа",
      updatedAt: 1_761_770_000_000,
      running: false,
      blank: false,
      question: false,
      stats: { turns: 3, steps: 4 },
      outline: [],
      inbox: { nextTurn: 0, nextStep: 0 },
      ...overrides,
    })!;
  }

  const base = {
    row: item(),
    streamLive: true,
    pendingHere: false,
    runActive: false,
    // DEF-150: строка молчит о работе — по умолчанию «Работает» только от своего хода.
    rootRunning: false,
    hasFeed: true,
  };

  it("вопрос — всегда «Ждёт ответа», даже без строки и без живого потока", () => {
    const waiting = { key: "PhoneWebStateWaiting", tone: "warn" };

    expect(sessionState({ ...base, pendingHere: true, row: null, streamLive: false })).toEqual(
      waiting,
    );
    expect(sessionState({ ...base, pendingHere: true, runActive: true })).toEqual(waiting);
  });

  it("живой поток: ход идёт → Работает; есть записи → Закончила; пусто → Ещё не было задачи", () => {
    expect(sessionState({ ...base, runActive: true })).toEqual({
      key: "PhoneWebStateRunning",
      tone: "accent",
    });
    expect(sessionState({ ...base, hasFeed: true })).toEqual({
      key: "PhoneWebStateDone",
      tone: "good",
    });
    expect(sessionState({ ...base, hasFeed: false })).toEqual({
      key: "PhoneWebStateBlank",
      tone: "neutral",
    });
  });
  // Строки нет — «Закончила»/«Ещё не было задачи» не печатаем: это последнее известное из списка.
  expect(sessionState({ ...base, row: null, hasFeed: true })).toBeNull();
  expect(sessionState({ ...base, row: null, hasFeed: false })).toBeNull();

  it("строка списка НЕ источник: живой поток сильнее её слова в обе стороны", () => {
    // Список говорит «Закончила», а в ленте идёт ход — шапка «Работает».
    expect(sessionState({ ...base, row: item({ running: false }), runActive: true })?.key).toBe(
      "PhoneWebStateRunning",
    );
    // ⚠️ DEF-150 (12.11.2026): раньше здесь ожидалось «Закончила» — именно это и было дефектом.
    // Список говорит «Работает» (работает подчинённая), свой ход кончился — шапка обязана
    // повторять «Работает»: строка знает агрегат «свой флаг ИЛИ подчинённая».
    expect(
      sessionState({
        ...base,
        row: item({ running: true }),
        runActive: false,
        rootRunning: true,
        hasFeed: true,
      })?.key,
    ).toBe("PhoneWebStateRunning");
  });

  it("DEF-150: «Работает» = свой ход ИЛИ работающая подчинённая; вопрос сильнее; мёртвый поток — нет", () => {
    const running = { key: "PhoneWebStateRunning", tone: "accent" };

    // Свой ход кончился (`turn-end` в ленте), строка говорит «работа идёт» → «Работает».
    expect(sessionState({ ...base, runActive: false, rootRunning: true })).toEqual(running);
    // Свой ход идёт, строка молчит → «Работает» (прежнее правило W4 не сломано).
    expect(sessionState({ ...base, runActive: true, rootRunning: false })).toEqual(running);
    // Ни того, ни другого → «Закончила».
    expect(
      sessionState({ ...base, runActive: false, rootRunning: false, hasFeed: true })?.key,
    ).toBe("PhoneWebStateDone");
    // Ничего не было вовсе → «Ещё не было задачи».
    expect(
      sessionState({ ...base, runActive: false, rootRunning: false, hasFeed: false })?.key,
    ).toBe("PhoneWebStateBlank");
    // Вопрос сильнее любого признака хода.
    expect(
      sessionState({ ...base, pendingHere: true, runActive: true, rootRunning: true })?.key,
    ).toBe("PhoneWebStateWaiting");
    // Не живой поток: «Работает» не печатаем ДАЖЕ если строка говорит «работа идёт» (D3 §1.2).
    expect(
      sessionState({
        ...base,
        streamLive: false,
        row: item({ running: true }),
        runActive: false,
        rootRunning: true,
      }),
    ).toBeNull();
    // и «Закончила» — по слову строки, когда она говорит «закончила».
    expect(
      sessionState({
        ...base,
        streamLive: false,
        row: item({ running: false }),
        rootRunning: false,
      })?.key,
    ).toBe("PhoneWebStateDone");
  });

  it("пока поток не живой — слово из строки, КРОМЕ «Работает»; строки нет — молчим", () => {
    const offline = { ...base, streamLive: false };

    expect(sessionState({ ...offline, row: item() })?.key).toBe("PhoneWebStateDone");
    // «Работает» не обещаем без живого потока — это слово о текущем процессе.
    expect(sessionState({ ...offline, row: item({ running: true }) })).toBeNull();
    expect(sessionState({ ...offline, row: null })).toBeNull();
    // Вопрос из строки (не по опросу) — слово строки, это не запрещено.
    expect(sessionState({ ...offline, row: item({ question: true }) })?.key).toBe(
      "PhoneWebStateWaiting",
    );
  });

  it("черновик в живом признаке не даёт кадр «Закончила» между ответом и первым куском", () => {
    const draft = { text: "сейчас", startedAfterSeq: 0, turn: 2, step: 0, attemptId: "a" };
    // Первый ход: в окне ещё нет ни turn, ни turn-end — решает черновик (иначе кадр «Закончила»).
    const active = isRunActive([], draft, false);

    expect(active).toBe(true);
    expect(sessionState({ ...base, runActive: active })?.key).toBe("PhoneWebStateRunning");
  });
});
