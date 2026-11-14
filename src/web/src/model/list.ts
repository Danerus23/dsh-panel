// Строка списка сессий: разбор ответа калитки и СЛОВА состояния.
//
// Правило выбора состояния (контракт §3.1): «Ждёт ответа» важнее «Работает» важнее
// «Ещё не было задачи» важнее «Закончила». Цвет — не единственный носитель (WCAG 1.4.1),
// поэтому состояние всегда словом.

import { MAX_TITLE, LIST_LIMIT } from "../config";
import type { T } from "../words";
import type { WordKey } from "../i18n";

export type SessionItem = {
  sessionId: string;
  title: string | null;
  updatedAt: number;
  running: boolean;
  blank: boolean;
  question: boolean;
  turns: number;
  steps: number;
  inbox: number;
  outline: string;
};

export type SessionList = {
  items: SessionItem[];
  total: number;
  shown: number;
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function text(value: unknown, max: number): string {
  if (typeof value !== "string") return "";
  const trimmed = value.trim();
  return trimmed.length > max ? trimmed.slice(0, max) : trimmed;
}

function count(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) && value > 0 ? Math.floor(value) : 0;
}

/**
 * Одна строка из ответа калитки. Поля читаются ПОИМЁННО: чего нет в белом списке — того нет
 * и в модели. Строка без `sessionId` отбрасывается (это поломка, а не сессия).
 */
export function parseSession(raw: unknown): SessionItem | null {
  if (!isRecord(raw)) return null;
  const sessionId = text(raw["sessionId"], 128);
  if (sessionId === "") return null;
  const stats = isRecord(raw["stats"]) ? raw["stats"] : {};
  const inbox = isRecord(raw["inbox"]) ? raw["inbox"] : {};
  const outlineRaw = Array.isArray(raw["outline"]) ? raw["outline"] : [];
  const lastOutline = outlineRaw.length > 0 ? outlineRaw[outlineRaw.length - 1] : null;
  const outline = isRecord(lastOutline) ? text(lastOutline["response"], 120) : "";
  const title = raw["title"] === null ? null : text(raw["title"], MAX_TITLE);
  return {
    sessionId,
    title: title === "" ? null : title,
    updatedAt: count(raw["updatedAt"]),
    running: raw["running"] === true,
    blank: raw["blank"] === true,
    question: raw["question"] === true,
    turns: count(stats["turns"]),
    steps: count(stats["steps"]),
    inbox: count(inbox["nextTurn"]) + count(inbox["nextStep"]),
    outline,
  };
}

/** Ответ целиком: `items` — как есть, но каждая строка проходит разбор и лимит 50. */
export function parseList(payload: {
  items: unknown[];
  total: number;
  shown: number;
}): SessionList {
  const items: SessionItem[] = [];
  for (const raw of payload.items.slice(0, LIST_LIMIT)) {
    const item = parseSession(raw);
    if (item !== null) items.push(item);
  }
  return {
    items,
    total: Math.max(payload.total, items.length),
    shown: Math.min(payload.shown, items.length),
  };
}

/**
 * Ключ слова состояния. Порядок важен: вопрос — сильнее работы.
 * `questionSessionId` — корневая сессия висящего вопроса из опроса (2 с): метка в списке
 * ставится по нему, а не ждёт ответа списка (10 с), иначе карточка и строка расходятся.
 */
export function stateWordKey(item: SessionItem, questionSessionId: string | null = null): WordKey {
  if (item.question || (questionSessionId !== null && questionSessionId === item.sessionId)) {
    return "PhoneWebStateWaiting";
  }
  if (item.running) return "PhoneWebStateRunning";
  if (item.blank) return "PhoneWebStateBlank";
  return "PhoneWebStateDone";
}
/** Тон состояния — цвет и значок вместе со словом (карточка 32a О-3; `10-foundations.md` §3.1).
 *  Порядок — ТОТ ЖЕ, что у слова: одна правда, две выдачи (слово и тон). */
export type StateTone = "warn" | "accent" | "neutral" | "good";

const TONE: Partial<Record<WordKey, StateTone>> = {
  PhoneWebStateWaiting: "warn",
  PhoneWebStateRunning: "accent",
  PhoneWebStateBlank: "neutral",
  PhoneWebStateDone: "good",
};

/** Тон по слову состояния — ТА ЖЕ таблица, что у `stateTone` (одна правда, две выдачи). */
export function toneForWord(key: WordKey): StateTone {
  return TONE[key] ?? "good";
}

/** Тон по тем же трём флагам, что и слово. Неизвестное слово — «закончила»: так решает слово. */
export function stateTone(item: SessionItem, questionSessionId: string | null = null): StateTone {
  return toneForWord(stateWordKey(item, questionSessionId));
}

export type ListRow = {
  item: SessionItem;
  href: string;
  title: string;
  state: string;
  /** Тон для значка состояния: слово остаётся, цвет — второй носитель. */
  tone: StateTone;
  /** Фраза для чтения с экрана («В очереди: 3») — видимое число кратно ей. */
  inbox: string | null;
  /** То же число — для видимого бейджа: счётчик приметой «строка нажимаема», а не фразой. */
  inboxCount: number | null;
};

/** Готовая строка для разметки: имя, слово состояния, «в очереди» (только когда > 0). */
export function toRow(item: SessionItem, t: T, questionSessionId: string | null = null): ListRow {
  return {
    item,
    href: `/?session=${encodeURIComponent(item.sessionId)}`,
    title: item.title ?? t("PhoneWebUntitled"),
    state: t(stateWordKey(item, questionSessionId)),
    tone: stateTone(item, questionSessionId),
    inbox: item.inbox > 0 ? t("PhoneWebInboxFormat", item.inbox) : null,
    inboxCount: item.inbox > 0 ? item.inbox : null,
  };
}

/**
 * Строки, которые телефон ПОКАЗЫВАЕТ. `blank` (сессия без единой задачи) скрывается, как в DSH
 * (слово владельца 11.11.2026); исключение — строка с висящим вопросом: вопрос не пустота (D1 §6).
 * `questionSessionId` — корневая сессия вопроса из опроса (2 с): метка не ждёт ответа списка.
 */
export function visibleItems(
  items: SessionItem[],
  questionSessionId: string | null = null,
): SessionItem[] {
  return items.filter(
    (item) =>
      !item.blank ||
      item.question ||
      (questionSessionId !== null && questionSessionId === item.sessionId),
  );
}

/**
 * Числа списка на экране: `count` — ВИДИМЫЕ строки («Сессий: N» = видимым, D1 §6), `total` —
 * `total` калитки минус скрытые на этой странице (хвост «Показаны последние N из M»).
 * `truncated` — обрезка была (`shown < total`): только она даёт право печатать хвост.
 */
export function visibleCounts(
  list: SessionList,
  questionSessionId: string | null = null,
): { count: number; total: number; truncated: boolean } {
  const shown = visibleItems(list.items, questionSessionId);
  const hidden = list.items.length - shown.length;
  return {
    count: shown.length,
    total: Math.max(list.total - hidden, shown.length),
    truncated: list.shown < list.total,
  };
}
