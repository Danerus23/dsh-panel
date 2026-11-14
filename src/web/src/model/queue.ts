// Очередь одной сессии (D4): разбор ответа по БЕЛОМУ СПИСКУ полей и слова метки места.
//
// Контракт §5.2–§5.3: телефону уезжают только `id`, `text` (превью ≤ 300, уже порезано калиткой),
// `chars`, `placement` и числа `total/shown/hasMore`; `source`, `role`, `content`, `asOfSeq`,
// `projections` не читаются вовсе — их нет в модели, значит они не могут попасть на экран.
//
// Три ответа, которые нельзя путать (`60-states.md` §0): «пусто» · «не прочиталась» · «не открыта».

import { QUEUE_LIMIT, QUEUE_PREVIEW_MAX } from "../config";
import type { WordKey } from "../i18n";

/** `placement` из двери: `next-turn` → `queued`, `next-step` → `steering` (контракт §5.2). */
export type Placement = "queued" | "steering";

export type QueueItem = {
  id: string;
  /** Превью текста человека; для пункта-вложения — пустая строка. */
  text: string;
  /** Полная длина текста: по ней видно, что превью — обрывок. */
  chars: number;
  /** Неизвестное место — `null`: метки нет, а не выдуманная (паспорт §8 п. 8). */
  placement: Placement | null;
};

export type QueueView =
  | { status: "loading" }
  | { status: "ok"; items: QueueItem[]; total: number; shown: number; hasMore: boolean }
  | { status: "no-live"; message: string }
  | { status: "unreadable"; message: string };

const ID_PATTERN = /^[A-Za-z0-9._:-]{1,128}$/u;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function count(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) && value > 0 ? Math.floor(value) : 0;
}

function words(value: unknown): string {
  return typeof value === "string" ? value.trim() : "";
}

function placementOf(value: unknown): Placement | null {
  return value === "queued" || value === "steering" ? value : null;
}

/** Превью не удлиняем и не режем повторно: калитка уже дала ≤ 300 знаков с «…» на конце. */
function preview(value: unknown): string {
  if (typeof value !== "string") return "";
  if (value.length <= QUEUE_PREVIEW_MAX + 1) return value;
  return `${value.slice(0, QUEUE_PREVIEW_MAX - 1)}…`;
}

/** Один пункт: `id` не по форме — пункт отбрасывается (это поломка, а не пункт). */
export function parseQueueItem(raw: unknown): QueueItem | null {
  if (!isRecord(raw)) return null;
  const id = typeof raw["id"] === "string" ? raw["id"] : "";
  if (!ID_PATTERN.test(id)) return null;
  return {
    id,
    text: preview(raw["text"]),
    chars: count(raw["chars"]),
    placement: placementOf(raw["placement"]),
  };
}

/**
 * Ответ D4 целиком. `state` различает успех и «сессии не открыта» — второго пустого списка нет.
 * Порядок пунктов — как отдала дверь (сначала `steering`); модель его не переставляет.
 */
export function parseQueue(body: unknown): QueueView {
  if (!isRecord(body)) return { status: "unreadable", message: "" };

  const state = body["state"];
  const message = words(body["message"]);

  if (state === "no-live") return { status: "no-live", message };
  if (state !== "ok") return { status: "unreadable", message };

  const raw = Array.isArray(body["items"]) ? body["items"] : [];
  const items: QueueItem[] = [];
  for (const entry of raw.slice(0, QUEUE_LIMIT)) {
    const item = parseQueueItem(entry);
    if (item !== null) items.push(item);
  }
  const total = Math.max(count(body["total"]), items.length);
  const shown = Math.min(count(body["shown"]) || items.length, items.length);
  return {
    status: "ok",
    items,
    total,
    shown,
    hasMore: body["hasMore"] === true || total > shown,
  };
}

/**
 * Кадр `queue` из живого SSE (S4a): та же форма, что у `GET /api/phone/queue` (R1 §2.1).
 * Разбираем ТОЛЬКО успех и «сессия не открыта»: чужая/непонятная форма — не повод стирать
 * уже показанный состав (`null` — экран не меняется).
 */
export function parseQueueFrame(payload: unknown): QueueView | null {
  if (!isRecord(payload)) return null;
  const state = payload["state"];
  if (state === "no-live") return parseQueue(payload);
  if (state === "ok" || (state === undefined && Array.isArray(payload["items"]))) {
    return parseQueue({ ...payload, state: "ok" });
  }
  return null;
}
/** Ключ метки места; `null` — метки нет (неизвестное место не называем). */
export function placementKey(placement: Placement | null): WordKey | null {
  if (placement === "steering") return "PhoneWebPlacementSteering";
  if (placement === "queued") return "PhoneWebPlacementQueued";
  return null;
}

/** Пункт без текста — вложение: вместо превью экран говорит, что смотреть его на компьютере. */
export function isAttachment(item: QueueItem): boolean {
  return item.text.trim() === "";
}

/** Превью короче полного текста: человек видит обрывок и обязан видеть, что это обрывок. */
export function isTruncated(item: QueueItem): boolean {
  return item.chars > item.text.length;
}

/** Убрать пункт из уже прочитанной очереди, не дожидаясь D4. Числа не «примерно», а −1. */
export function withoutItem(view: QueueView, itemId: string): QueueView {
  if (view.status !== "ok") return view;
  const items = view.items.filter((item) => item.id !== itemId);
  if (items.length === view.items.length) return view;
  return {
    ...view,
    items,
    total: Math.max(view.total - 1, 0),
    shown: Math.max(Math.min(view.shown, view.items.length) - 1, items.length),
  };
}
