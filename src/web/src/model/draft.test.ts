// Черновик: форма записи, «свой» и «новый» ключ, чужая версия, снятие по `requestId`.

import { describe, expect, it } from "vitest";
import { DRAFT_NEW_KEY, DRAFT_VERSION } from "../config";
import {
  draftAvailable,
  draftKey,
  readDraft,
  removeDraft,
  writeDraft,
  type DraftStore,
} from "./draft";

/** Подделка `localStorage`: ровно три операции, как у настоящего. */
function fakeStore(): DraftStore & { map: Map<string, string> } {
  const map = new Map<string, string>();
  return {
    map,
    getItem: (key: string) => map.get(key) ?? null,
    setItem: (key: string, value: string) => {
      map.set(key, value);
    },
    removeItem: (key: string) => {
      map.delete(key);
    },
  };
}

describe("ключи черновика", () => {
  it("у нового разговора свой ключ, у сессии — свой", () => {
    expect(draftKey(null)).toBe(DRAFT_NEW_KEY);
    expect(draftKey("")).toBe(DRAFT_NEW_KEY);
    expect(draftKey("session-abc")).toBe("dsh.phone.draft.session-abc");
    expect(DRAFT_NEW_KEY).toBe("dsh.phone.draft.new");
  });
});

describe("запись и чтение", () => {
  it("набранное переживает перезагрузку: текст и requestId на месте", () => {
    const store = fakeStore();
    expect(writeDraft(store, draftKey("session-1"), "починить сборку", "req-1", 1_000)).toBe(true);
    expect(readDraft(store, draftKey("session-1"))).toEqual({
      text: "починить сборку",
      requestId: "req-1",
    });
  });

  it("в записи только v, text, requestId, savedAt — ни путей, ни id входа", () => {
    const store = fakeStore();
    writeDraft(store, DRAFT_NEW_KEY, "задача", "req-1", 1_000);
    const raw = store.map.get(DRAFT_NEW_KEY)!;
    expect(JSON.parse(raw)).toEqual({
      v: DRAFT_VERSION,
      text: "задача",
      requestId: "req-1",
      savedAt: 1_000,
    });
    expect(raw).not.toContain("cwd");
    expect(raw).not.toContain("token");
  });

  it("пустое поле — это не черновик: ключ снимается", () => {
    const store = fakeStore();
    writeDraft(store, DRAFT_NEW_KEY, "задача", "req-1", 1_000);
    writeDraft(store, DRAFT_NEW_KEY, "   ", "req-1", 2_000);
    expect(store.map.has(DRAFT_NEW_KEY)).toBe(false);
    expect(readDraft(store, DRAFT_NEW_KEY)).toBeNull();
  });

  it("чужая версия и битый JSON — «черновика нет», запись НЕ стирается", () => {
    const store = fakeStore();
    store.map.set(DRAFT_NEW_KEY, JSON.stringify({ v: 99, text: "из будущего" }));
    expect(readDraft(store, DRAFT_NEW_KEY)).toBeNull();
    expect(store.map.has(DRAFT_NEW_KEY)).toBe(true);

    store.map.set(DRAFT_NEW_KEY, "{не json");
    expect(readDraft(store, DRAFT_NEW_KEY)).toBeNull();
    expect(store.map.has(DRAFT_NEW_KEY)).toBe(true);
  });

  it("requestId не по форме — текст подставляем, повтор безопасным не будет", () => {
    const store = fakeStore();
    store.map.set(DRAFT_NEW_KEY, JSON.stringify({ v: 1, text: "задача", requestId: "плохой id" }));
    expect(readDraft(store, DRAFT_NEW_KEY)).toEqual({ text: "задача", requestId: "" });
  });

  it("«Убрать черновик» снимает запись сразу", () => {
    const store = fakeStore();
    writeDraft(store, DRAFT_NEW_KEY, "задача", "req-1", 1_000);
    expect(removeDraft(store, DRAFT_NEW_KEY)).toBe(true);
    expect(readDraft(store, DRAFT_NEW_KEY)).toBeNull();
  });
});

describe("доступность хранилища", () => {
  it("приватный режим: запись кидает — «черновик не сохранится», поле работает", () => {
    const broken: DraftStore = {
      getItem: () => null,
      setItem: () => {
        throw new Error("QuotaExceededError");
      },
      removeItem: () => {
        throw new Error("QuotaExceededError");
      },
    };
    expect(draftAvailable(broken)).toBe(false);
    expect(writeDraft(broken, DRAFT_NEW_KEY, "задача", "req-1", 1_000)).toBe(false);
    expect(draftAvailable(fakeStore())).toBe(true);
    expect(draftAvailable(null)).toBe(false);
  });
});
