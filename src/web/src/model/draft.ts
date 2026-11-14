// Черновик задачи: чистые правила записи и чтения (контракт §8.2–§8.5).
//
// Здесь только работа с хранилищем и формой записи — без React. Экранная часть (подстановка
// в пустое поле, `pageshow`, запись не чаще раза в секунду) — в `hooks/useDraft.ts`.
//
// Форма записи ровно одна: `{"v":1,"text":"…","requestId":"<uuid>","savedAt":<ms>}`. Ни cookie,
// ни `clientId`, ни путей, ни чужих имён сессий — `localStorage` читает любой скрипт этого origin
// и переживает «Отключить телефон» (§8.3).

import { DRAFT_NEW_KEY, DRAFT_PREFIX, DRAFT_VERSION } from "../config";
import { isRequestId } from "./task";

export type DraftRecord = { v: number; text: string; requestId: string; savedAt: number };

/** Ровно те три операции, которые нужны; `Storage` подходит, подделка в тестах — тоже. */
export type DraftStore = Pick<Storage, "getItem" | "setItem" | "removeItem">;

export type DraftText = { text: string; requestId: string };

/** Ключ черновика: свой для нового разговора и свой для каждой сессии (`§8.2`). */
export function draftKey(sessionId: string | null): string {
  return sessionId === null || sessionId === "" ? DRAFT_NEW_KEY : `${DRAFT_PREFIX}${sessionId}`;
}

/** Хранилище доступно? Проба пишет и сразу снимает свой ключ (приватный режим кинет исключение). */
export function draftAvailable(store: DraftStore | null): boolean {
  if (store === null) return false;
  try {
    const probe = `${DRAFT_PREFIX}diag`;
    store.setItem(probe, "1");
    store.removeItem(probe);
    return true;
  } catch {
    return false;
  }
}

/**
 * Прочитать запись. Чужая версия, битый JSON, пустой текст, `requestId` не по форме —
 * «черновика нет»; запись при этом НЕ удаляется (чужой или будущий формат — не наше дело).
 */
export function readDraft(store: DraftStore | null, key: string): DraftText | null {
  if (store === null) return null;
  let raw: string | null = null;
  try {
    raw = store.getItem(key);
  } catch {
    return null;
  }
  if (raw === null || raw === "") return null;
  let value: unknown;
  try {
    value = JSON.parse(raw);
  } catch {
    return null;
  }
  if (typeof value !== "object" || value === null) return null;
  const record = value as Record<string, unknown>;
  if (record["v"] !== DRAFT_VERSION) return null;
  const text = typeof record["text"] === "string" ? record["text"] : "";
  const requestId = typeof record["requestId"] === "string" ? record["requestId"] : "";
  if (text.trim() === "") return null;
  return { text, requestId: isRequestId(requestId) ? requestId : "" };
}

/** Записать набранное. Пустой текст — это не черновик: ключ снимается. `false` — запись не удалась. */
export function writeDraft(
  store: DraftStore | null,
  key: string,
  text: string,
  requestId: string,
  savedAt: number,
): boolean {
  if (store === null) return false;
  try {
    if (text.trim() === "") {
      store.removeItem(key);
      return true;
    }
    const record: DraftRecord = { v: DRAFT_VERSION, text, requestId, savedAt };
    store.setItem(key, JSON.stringify(record));
    return true;
  } catch {
    return false;
  }
}

/** Снять черновик («Убрать черновик», «принято», опустевшее поле). */
export function removeDraft(store: DraftStore | null, key: string): boolean {
  if (store === null) return false;
  try {
    store.removeItem(key);
    return true;
  } catch {
    return false;
  }
}
