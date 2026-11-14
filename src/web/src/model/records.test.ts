import { describe, expect, it } from "vitest";
import {
  appendRecord,
  canStitch,
  draftAfterFrame,
  draftAfterRecord,
  normalizeAll,
  normalizeRecord,
  parseSnapshot,
  prependPage,
  countMessages,
  toFeedRows,
  type Draft,
} from "./records";
import { makeT } from "../words";

const t = makeT({
  PhoneWebTurnFormat: "Ход {0}",
  PhoneWebTurnError: "Ход закончился ошибкой. Посмотрите на компьютер.",
  PhoneWebToolRunningFormat: "Выполняет: {0}",
  PhoneWebToolDoneFormat: "{0} — готово",
  PhoneWebToolFailedFormat: "{0} — не вышло",
  PhoneWebAttachmentNote: "Вложение — посмотрите на компьютере",
});

const snapshotRaw = {
  header: { id: "session-1", createdAt: 1, title: "Починить калитку" },
  cursor: 6,
  hasMore: true,
  assistantDraft: { attemptId: "a", turn: 2, step: 1, startedAfterSeq: 6, text: "уже пишу" },
  records: [
    { type: "turn", seq: 1, time: 1, turn: 1 },
    { type: "user", seq: 2, time: 2, text: "привет" },
    { type: "assistant", seq: 3, time: 3, text: "ответ" },
    { type: "system/message", seq: 4, time: 4, text: "СЛУЖЕБНОЕ" },
    { type: "tool-call", seq: 5, time: 5, name: "dotnet test", callId: "c1" },
    { type: "tool-result", seq: 6, time: 6, callId: "c1", failed: true },
  ],
};

describe("записи ленты", () => {
  it("снимок читается: заголовок, курсор, черновик", () => {
    const snapshot = parseSnapshot(snapshotRaw);
    expect(snapshot?.header.title).toBe("Починить калитку");
    expect(snapshot?.cursor).toBe(6);
    expect(snapshot?.hasMore).toBe(true);
    expect(snapshot?.assistantDraft?.text).toBe("уже пишу");
  });

  it("служебные виды записей не проходят в ленту", () => {
    const records = normalizeAll(snapshotRaw.records);
    expect(records.map((record) => record.type)).toEqual([
      "turn",
      "user",
      "assistant",
      "tool-call",
      "tool-result",
    ]);
    expect(JSON.stringify(records)).not.toContain("СЛУЖЕБНОЕ");
  });

  it("пустой ответ агента не становится строкой ленты", () => {
    // DEF-093: пузырь без текста — дыра в ленте. Калитка такие записи отбрасывает, веб — тоже.
    const rows = toFeedRows(
      normalizeAll([
        { type: "turn", seq: 1, time: 1, turn: 1 },
        { type: "assistant", seq: 2, time: 2, text: "" },
        { type: "assistant", seq: 3, time: 3, text: "   " },
        { type: "assistant", seq: 4, time: 4, text: "Сейчас проверю логи." },
      ]),
      null,
      t,
    );
    const assistant = rows.filter((row) => row.kind === "assistant");
    expect(assistant.map((row) => (row.kind === "assistant" ? row.text : ""))).toEqual([
      "Сейчас проверю логи.",
    ]);
    expect(assistant.map((row) => row.key)).toEqual(["assistant:4"]);
  });

  it("незнакомый вид, мусор и пустой снимок — null", () => {
    expect(normalizeRecord({ type: "request/header", seq: 1 })).toBeNull();
    expect(normalizeRecord("строка")).toBeNull();
    expect(parseSnapshot({ header: {}, records: [] })).toBeNull();
  });

  it("строки экрана: разделитель хода только при нескольких ходах", () => {
    const oneTurn = toFeedRows(normalizeAll(snapshotRaw.records), null, t);
    expect(oneTurn.some((row) => row.kind === "turn")).toBe(false);

    const twoTurns = normalizeAll([
      ...snapshotRaw.records,
      { type: "turn", seq: 7, time: 7, turn: 2 },
    ]);
    expect(toFeedRows(twoTurns, null, t).some((row) => row.kind === "turn")).toBe(true);
  });

  it("имя инструмента берётся из вызова, исход без вызова не показывается", () => {
    const rows = toFeedRows(normalizeAll(snapshotRaw.records), null, t);
    expect(rows.some((row) => row.kind === "tool")).toBe(true);
    expect(JSON.stringify(rows)).toContain("dotnet test — не вышло");

    const orphan = normalizeAll([
      { type: "tool-result", seq: 1, time: 1, callId: "нет", failed: false },
    ]);
    expect(toFeedRows(orphan, null, t)).toHaveLength(0);
  });

  it("ошибка хода — одна строка на ход, а не на кадр", () => {
    const records = normalizeAll([
      { type: "turn", seq: 1, time: 1, turn: 1 },
      { type: "turn-end", seq: 2, time: 2, turn: 1, ok: false, reason: "error" },
      { type: "turn-end", seq: 3, time: 3, turn: 1, ok: false, reason: "error" },
      { type: "turn-end", seq: 4, time: 4, turn: 2, ok: true, reason: "completed" },
    ]);
    const rows = toFeedRows(records, null, t);
    expect(rows.filter((row) => row.kind === "error")).toHaveLength(1);
  });

  it("черновик снимается готовым ответом, а не живёт второй строкой", () => {
    const draft: Draft = {
      attemptId: "a",
      turn: 2,
      step: 1,
      startedAfterSeq: 6,
      text: "Сейчас проверю логи.",
    };
    const durable = normalizeRecord({
      type: "assistant",
      seq: 7,
      time: 7,
      text: "Сейчас проверю логи.",
    });
    expect(durable).not.toBeNull();
    expect(draftAfterRecord(draft, durable!)).toBeNull();
    // Старый ответ (seq ≤ начала попытки) черновик не снимает.
    const older = normalizeRecord({ type: "assistant", seq: 5, time: 5, text: "старое" });
    expect(draftAfterRecord(draft, older!)).not.toBeNull();
  });

  it("живой кадр дописывает черновик, конец — снимает", () => {
    const started = draftAfterFrame(null, { phase: "start", attemptId: "a" });
    const first = draftAfterFrame(started, { phase: "text", attemptId: "a", text: "раз " });
    const second = draftAfterFrame(first, { phase: "text", attemptId: "a", text: "два" });
    expect(second?.text).toBe("раз два");
    expect(draftAfterFrame(second, { phase: "end", attemptId: "a" })).toBeNull();
  });

  it("одна и та же запись не появляется дважды", () => {
    const records = normalizeAll(snapshotRaw.records);
    const again = normalizeRecord({ type: "user", seq: 2, time: 2, text: "привет" });
    expect(appendRecord(records, again!)).toHaveLength(records.length);
  });

  it("догон: страницы остаются только без пересечения по seq", () => {
    const older = normalizeAll([{ type: "user", seq: 1, time: 1, text: "старое" }]);
    const fresh = normalizeAll([{ type: "user", seq: 5, time: 5, text: "новое" }]);
    expect(canStitch(older, fresh)).toBe(true);
    expect(canStitch(normalizeAll([{ type: "user", seq: 9, time: 9, text: "x" }]), fresh)).toBe(
      false,
    );
    expect(prependPage(fresh, older).map((record) => record.seq)).toEqual([1, 5]);
  });

  it("длинный текст режется до 20 000 знаков с многоточием", () => {
    const long = normalizeRecord({ type: "assistant", seq: 1, time: 1, text: "я".repeat(25_000) });
    expect(long?.type).toBe("assistant");
    if (long?.type === "assistant") {
      expect(long.text.length).toBeLessThanOrEqual(20_001);
      expect(long.text.endsWith("…")).toBe(true);
    }
  });
  it("счёт «новых» считает сообщения, а не служебные строки", () => {
    const rows = toFeedRows(
      normalizeAll([
        { type: "turn", seq: 1, time: 1, turn: 1 },
        { type: "user", seq: 2, time: 2, text: "привет" },
        { type: "assistant", seq: 3, time: 3, text: "ответ" },
        { type: "tool-call", seq: 4, time: 4, name: "dotnet test", callId: "c1" },
        { type: "tool-result", seq: 5, time: 5, callId: "c1", failed: false },
        { type: "turn-end", seq: 6, time: 6, turn: 1, ok: false, reason: "error" },
      ]),
      null,
      t,
    );
    // 5 строк: человек, ответ, вызов и исход инструмента, ошибка хода; сообщений — два:
    // инструмент и ход ошибки — не разговор.
    expect(rows).toHaveLength(5);
    expect(countMessages(rows)).toBe(2);

    // Недописанный ответ — тоже сообщение, и он же не считается вторым, когда станет готовым.
    const withDraft = toFeedRows(
      normalizeAll([]),
      { attemptId: "a", turn: 0, step: 0, startedAfterSeq: 0, text: "пишу" },
      t,
    );
    expect(countMessages(withDraft)).toBe(1);
  });
});
