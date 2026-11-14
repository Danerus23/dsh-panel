// Лента сессии: нормализация записей калитки, склейка живого хода с готовым ответом
// и слияние страниц «раньше».
//
// Виды записей — ровно белый список контракта §2.2: turn, user, assistant, tool-call,
// tool-result, turn-end. Всё остальное (system/message, request/header, tool-аргументы)
// до этой модели не доходит вовсе — калитка их не отдаёт, а разбор всё равно проверяет вид.

import { MAX_FEED_TEXT } from "../config";
import type { T } from "../words";

export type TurnRecord = { type: "turn"; seq: number; time: number; turn: number };
export type UserRecord = {
  type: "user";
  seq: number;
  time: number;
  text: string;
  attachment: boolean;
};
export type AssistantRecord = { type: "assistant"; seq: number; time: number; text: string };
export type ToolCallRecord = {
  type: "tool-call";
  seq: number;
  time: number;
  name: string;
  callId: string;
};
export type ToolResultRecord = {
  type: "tool-result";
  seq: number;
  time: number;
  callId: string;
  failed: boolean;
};
export type TurnEndRecord = {
  type: "turn-end";
  seq: number;
  time: number;
  turn: number;
  ok: boolean;
  reason: string;
};

export type FeedRecord =
  TurnRecord | UserRecord | AssistantRecord | ToolCallRecord | ToolResultRecord | TurnEndRecord;

/** Недописанный ответ агента: живёт отдельно от записей и снимается, когда пришёл готовый. */
export type Draft = {
  attemptId: string;
  turn: number;
  step: number;
  startedAfterSeq: number;
  text: string;
};

export type AssistantFrame = {
  phase: "start" | "text" | "end";
  attemptId: string;
  index?: number;
  text?: string;
  outcome?: { kind?: string; eventType?: string; seq?: number };
};

export type Snapshot = {
  header: { id: string; createdAt: number; title: string | null };
  cursor: number;
  hasMore: boolean;
  records: unknown[];
  assistantDraft: Draft | null;
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function num(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

function str(value: unknown, max: number = 200): string {
  if (typeof value !== "string") return "";
  return value.length > max ? `${value.slice(0, max)}…` : value;
}

/** Текст блока ≤ 20 000 знаков: больше калитка режет сама, клиент сторожит повторно. */
function clampText(value: unknown): string {
  return typeof value === "string" ? str(value, MAX_FEED_TEXT) : "";
}

/**
 * Одна запись из потока/страницы. Незнакомый вид — `null`: лента не выдумывает строк,
 * которых не было в белом списке.
 */
export function normalizeRecord(raw: unknown): FeedRecord | null {
  if (!isRecord(raw)) return null;
  const seq = num(raw["seq"]);
  const time = num(raw["time"]);
  switch (raw["type"]) {
    case "turn":
      return { type: "turn", seq, time, turn: num(raw["turn"]) };
    case "user":
      return {
        type: "user",
        seq,
        time,
        text: clampText(raw["text"]),
        attachment: raw["attachment"] === true,
      };
    case "assistant":
      return { type: "assistant", seq, time, text: clampText(raw["text"]) };
    case "tool-call":
      return {
        type: "tool-call",
        seq,
        time,
        name: str(raw["name"], 120),
        callId: str(raw["callId"], 200),
      };
    case "tool-result":
      return {
        type: "tool-result",
        seq,
        time,
        callId: str(raw["callId"], 200),
        failed: raw["failed"] === true,
      };
    case "turn-end":
      return {
        type: "turn-end",
        seq,
        time,
        turn: num(raw["turn"]),
        ok: raw["ok"] === true,
        reason: str(raw["reason"], 40),
      };
    default:
      return null;
  }
}

/** Снимок `snapshot` (кадр SSE) → модель; мусор в нём не роняет ленту. */
export function parseSnapshot(raw: unknown): Snapshot | null {
  if (!isRecord(raw)) return null;
  const header = isRecord(raw["header"]) ? raw["header"] : {};
  const id = typeof header["id"] === "string" ? header["id"] : "";
  if (id === "") return null;
  const recordsRaw = Array.isArray(raw["records"]) ? raw["records"] : [];
  const records: unknown[] = recordsRaw;
  const draftRaw = raw["assistantDraft"];
  let assistantDraft: Draft | null = null;
  if (isRecord(draftRaw)) {
    assistantDraft = {
      attemptId: str(draftRaw["attemptId"], 200),
      turn: num(draftRaw["turn"]),
      step: num(draftRaw["step"]),
      startedAfterSeq: num(draftRaw["startedAfterSeq"]),
      text: clampText(draftRaw["text"]),
    };
  }
  return {
    header: {
      id,
      createdAt: num(header["createdAt"]),
      title: typeof header["title"] === "string" ? str(header["title"], 200) : null,
    },
    cursor: num(raw["cursor"]),
    hasMore: raw["hasMore"] === true,
    records,
    assistantDraft,
  };
}

function keyOf(record: FeedRecord): string {
  const tail =
    record.type === "tool-call" || record.type === "tool-result"
      ? record.callId
      : record.type === "turn" || record.type === "turn-end"
        ? String(record.turn)
        : "";
  return `${record.type}:${record.seq}:${tail}`;
}

function compare(left: FeedRecord, right: FeedRecord): number {
  if (left.seq !== right.seq) return left.seq - right.seq;
  return keyOf(left).localeCompare(keyOf(right));
}

/** Упорядочить по `seq`, выбросить повторы (снимок и живой кадр могут принести одно и то же). */
export function sortUnique(records: FeedRecord[]): FeedRecord[] {
  const seen = new Set<string>();
  const out: FeedRecord[] = [];
  for (const record of [...records].sort(compare)) {
    const key = keyOf(record);
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(record);
  }
  return out;
}

/** Нормализовать пачку записей, отбросив незнакомое. */
export function normalizeAll(raw: unknown[]): FeedRecord[] {
  const out: FeedRecord[] = [];
  for (const item of raw) {
    const record = normalizeRecord(item);
    if (record !== null) out.push(record);
  }
  return sortUnique(out);
}

/** Добавить живой кадр; та же запись второй раз не появляется. */
export function appendRecord(records: FeedRecord[], record: FeedRecord): FeedRecord[] {
  if (records.some((item) => keyOf(item) === keyOf(record))) return records;
  return sortUnique([...records, record]);
}

/** Черновик из снимка — это текст уже начатого ответа (только текст, без reasoning). */
export function draftText(draft: Draft | null): string {
  return draft?.text ?? "";
}

/** Готовый ответ снимает черновик: `assistant/message` новее начала попытки. */
export function draftAfterRecord(draft: Draft | null, record: FeedRecord): Draft | null {
  if (draft === null) return null;
  if (record.type === "assistant" && record.seq > draft.startedAfterSeq) return null;
  if (record.type === "turn-end") return null;
  return draft;
}

/** Живой кадр недописанного ответа: `start` открывает, `text` дописывает, `end` снимает. */
export function draftAfterFrame(draft: Draft | null, frame: AssistantFrame): Draft | null {
  if (frame.phase === "end") return null;
  if (frame.phase === "start") {
    return {
      attemptId: frame.attemptId,
      turn: 0,
      step: 0,
      startedAfterSeq: 0,
      text: draft !== null && draft.attemptId === frame.attemptId ? draft.text : "",
    };
  }
  const text = typeof frame.text === "string" ? frame.text : "";
  if (draft === null || draft.attemptId !== frame.attemptId) {
    return {
      attemptId: frame.attemptId,
      turn: 0,
      step: 0,
      startedAfterSeq: 0,
      text: clampText(text),
    };
  }
  return { ...draft, text: clampText(draft.text + text) };
}

/**
 * Стыковка загруженных ранее страниц со СВЕЖИМ снимком (догон после сна).
 * Страницы остаются, только если они строго старше снимка и не пересекаются с ним по `seq`;
 * иначе — сброс: лучше показать окно заново, чем склеить ленту с дырой.
 */
export function canStitch(older: FeedRecord[], snapshotRecords: FeedRecord[]): boolean {
  if (older.length === 0) return true;
  const snapshotFirst = snapshotRecords[0]?.seq ?? Number.POSITIVE_INFINITY;
  const olderLast = older[older.length - 1]?.seq ?? Number.NEGATIVE_INFINITY;
  return olderLast < snapshotFirst;
}

/** Страница «раньше»: старые записи перед уже показанными, без повторов. */
export function prependPage(records: FeedRecord[], page: FeedRecord[]): FeedRecord[] {
  if (page.length === 0) return records;
  return sortUnique([...page, ...records]);
}

// ── строки разметки ──────────────────────────────────────────────────────────────────────────

export type FeedRow =
  | { kind: "turn"; key: string; label: string }
  | { kind: "user"; key: string; text: string; attachment: string | null }
  | { kind: "assistant"; key: string; text: string }
  | { kind: "tool"; key: string; text: string }
  | { kind: "error"; key: string; label: string }
  | { kind: "draft"; key: string; text: string };

/**
 * Записи → строки экрана. Разделитель хода — только когда ходов больше одного; ошибка хода —
 * одна строка на ход (не на каждый кадр); имя инструмента берётся из его вызова по `callId`,
 * а исход без вызова в окне не показывается вовсе (имя выдумывать нельзя).
 */
export function toFeedRows(records: FeedRecord[], draft: Draft | null, t: T): FeedRow[] {
  const turnCount = records.filter((record) => record.type === "turn").length;
  const toolNames = new Map<string, string>();
  for (const record of records) {
    if (record.type === "tool-call" && record.callId !== "")
      toolNames.set(record.callId, record.name);
  }
  const rows: FeedRow[] = [];
  const errorTurns = new Set<number>();
  for (const record of records) {
    switch (record.type) {
      case "turn":
        if (turnCount > 1) {
          rows.push({
            kind: "turn",
            key: `turn:${record.seq}:${record.turn}`,
            label: t("PhoneWebTurnFormat", record.turn),
          });
        }
        break;
      case "user":
        rows.push({
          kind: "user",
          key: `user:${record.seq}`,
          text: record.text,
          attachment: record.attachment ? t("PhoneWebAttachmentNote") : null,
        });
        break;
      case "assistant":
        // Пустой (или из одних пробелов) ответ агента строкой ленты НЕ становится: пузырь без
        // текста — это дыра в ленте, а не сообщение (DEF-093). Калитка такие записи уже
        // отбрасывает; здесь защита на случай старой калитки, чужого снимка или обрезки до пустоты.
        if (record.text.trim() === "") break;
        rows.push({ kind: "assistant", key: `assistant:${record.seq}`, text: record.text });
        break;
      case "tool-call":
        rows.push({
          kind: "tool",
          key: `call:${record.seq}`,
          text: t("PhoneWebToolRunningFormat", record.name),
        });
        break;
      case "tool-result": {
        const name = toolNames.get(record.callId);
        if (name === undefined) break;
        rows.push({
          kind: "tool",
          key: `result:${record.seq}`,
          text: record.failed
            ? t("PhoneWebToolFailedFormat", name)
            : t("PhoneWebToolDoneFormat", name),
        });
        break;
      }
      case "turn-end":
        if (!record.ok && record.reason === "error" && !errorTurns.has(record.turn)) {
          errorTurns.add(record.turn);
          rows.push({ kind: "error", key: `error:${record.turn}`, label: t("PhoneWebTurnError") });
        }
        break;
    }
  }
  const text = draftText(draft);
  if (text !== "") rows.push({ kind: "draft", key: `draft:${draft?.attemptId ?? ""}`, text });
  return rows;
}

/**
 * Счёт сообщений в ленте — то, что человек читает как разговор:
 * реплики человека, ответы агента и ещё недописанный ответ (он же станет готовым — ровно один раз).
 * Служебные строки (разделитель хода, инструменты, ошибка хода) не считаются: «Выполняет: dotnet test»
 * — не сообщение, а ход работы (`50` §1: величина без смысла не печатается).
 */
export function countMessages(rows: FeedRow[]): number {
  let count = 0;
  for (const row of rows) {
    if (row.kind === "user" || row.kind === "assistant" || row.kind === "draft") count += 1;
  }
  return count;
}
