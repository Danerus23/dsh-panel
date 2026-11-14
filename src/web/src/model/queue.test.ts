// Очередь: разбор D4 по белому списку, лимит 20, метки места, «пусто ≠ не прочиталась ≠ не открыта».

import { describe, expect, it } from "vitest";
import { QUEUE_LIMIT } from "../config";
import {
  isAttachment,
  isTruncated,
  parseQueue,
  parseQueueItem,
  placementKey,
  withoutItem,
} from "./queue";

function item(over: Record<string, unknown> = {}) {
  return { id: "item-1", text: "первое", chars: 6, placement: "queued", ...over };
}

describe("пункт очереди", () => {
  it("id не по форме — пункт отбрасывается (это поломка, а не пункт)", () => {
    expect(parseQueueItem(item({ id: "с пробелом" }))).toBeNull();
    expect(parseQueueItem(item({ id: "" }))).toBeNull();
    expect(parseQueueItem(item())).not.toBeNull();
  });

  it("чужие поля не читаются: в модели только id, text, chars, placement", () => {
    const parsed = parseQueueItem(
      item({ source: { kind: "user" }, role: "user", content: [{ type: "text" }], rpcId: "rpc-9" }),
    );
    expect(Object.keys(parsed!).sort()).toEqual(["chars", "id", "placement", "text"]);
    expect(JSON.stringify(parsed)).not.toContain("rpc-9");
  });

  it("неизвестное место — без метки, а не выдуманная", () => {
    expect(parseQueueItem(item({ placement: "later" }))!.placement).toBeNull();
    expect(placementKey(null)).toBeNull();
    expect(placementKey("queued")).toBe("PhoneWebPlacementQueued");
    expect(placementKey("steering")).toBe("PhoneWebPlacementSteering");
  });

  it("превью не удлиняется и не режется повторно: «…» с конца видно", () => {
    const long = `${"я".repeat(299)}…`;
    expect(parseQueueItem(item({ text: long, chars: 1500 }))!.text).toBe(long);
    expect(isTruncated(parseQueueItem(item({ text: long, chars: 1500 }))!)).toBe(true);
  });

  it("пункт без текста — вложение, и его длина 0", () => {
    const attachment = parseQueueItem(item({ text: "", chars: 0 }))!;
    expect(isAttachment(attachment)).toBe(true);
    expect(isTruncated(attachment)).toBe(false);
  });
});

describe("ответ D4", () => {
  it("ok: числа и пункты, порядок — как отдала дверь (steering первый)", () => {
    const view = parseQueue({
      ok: true,
      state: "ok",
      sessionId: "session-1",
      total: 2,
      shown: 2,
      hasMore: false,
      items: [
        item({ id: "s-1", placement: "steering", text: "в текущий ход" }),
        item({ id: "q-1", placement: "queued", text: "следующим ходом" }),
      ],
    });
    expect(view.status).toBe("ok");
    if (view.status !== "ok") return;
    expect(view.items.map((entry) => entry.id)).toEqual(["s-1", "q-1"]);
    expect(view.total).toBe(2);
    expect(view.hasMore).toBe(false);
  });

  it("больше 20 пунктов не берём и честно говорим «показаны первые N из M»", () => {
    const items = Array.from({ length: 25 }, (_, index) => item({ id: `item-${index}` }));
    const view = parseQueue({ state: "ok", total: 45, shown: 25, hasMore: true, items });
    expect(view.status).toBe("ok");
    if (view.status !== "ok") return;
    expect(view.items).toHaveLength(QUEUE_LIMIT);
    expect(view.hasMore).toBe(true);
    expect(view.total).toBe(45);
  });

  it("пусто, «не открыта» и «не прочиталась» — три разных ответа", () => {
    const empty = parseQueue({ state: "ok", total: 0, shown: 0, hasMore: false, items: [] });
    expect(empty.status).toBe("ok");

    const noLive = parseQueue({ state: "no-live", message: "Сессия сейчас не открыта" });
    expect(noLive.status).toBe("no-live");
    expect(noLive.status === "no-live" && noLive.message).toBe("Сессия сейчас не открыта");

    expect(parseQueue({ state: "unreadable" }).status).toBe("unreadable");
    expect(parseQueue(null).status).toBe("unreadable");
  });

  it("ok без items — это пустая очередь, а не «не прочиталась»", () => {
    expect(parseQueue({ state: "ok", total: 0, shown: 0, hasMore: false }).status).toBe("ok");
  });
});

describe("убрали пункт (без ожидания D4)", () => {
  const view = parseQueue({
    state: "ok",
    total: 2,
    shown: 2,
    hasMore: false,
    items: [item({ id: "a" }), item({ id: "b" })],
  });

  it("пункт исчез, «в очереди» уменьшилось на один", () => {
    const next = withoutItem(view, "a");
    expect(next.status).toBe("ok");
    if (next.status !== "ok") return;
    expect(next.items.map((entry) => entry.id)).toEqual(["b"]);
    expect(next.total).toBe(1);
    expect(next.shown).toBe(1);
  });

  it("чужой id и не-ok состояние ничего не меняют", () => {
    expect(withoutItem(view, "чужой")).toBe(view);
    const noLive = parseQueue({ state: "no-live" });
    expect(withoutItem(noLive, "a")).toBe(noLive);
  });
});
