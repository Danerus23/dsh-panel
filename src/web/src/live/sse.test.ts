import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { LiveStream, type SourceLike } from "./sse";

class FakeSource implements SourceLike {
  listeners = new Map<string, Array<(event: MessageEvent | Event) => void>>();
  closed = false;

  addEventListener(type: string, listener: (event: MessageEvent | Event) => void): void {
    const list = this.listeners.get(type) ?? [];
    list.push(listener);
    this.listeners.set(type, list);
  }

  close(): void {
    this.closed = true;
  }

  emit(type: string, data?: unknown): void {
    const event =
      data === undefined ? new Event(type) : new MessageEvent(type, { data: JSON.stringify(data) });
    for (const listener of this.listeners.get(type) ?? []) listener(event);
  }
}

function harness() {
  const sources: FakeSource[] = [];
  const statuses: string[] = [];
  const snapshots: unknown[] = [];
  let stales = 0;
  const stream = new LiveStream(
    "/api/phone/live?session=s",
    {
      onSnapshot: (payload) => snapshots.push(payload),
      onRecord: () => undefined,
      onAssistant: () => undefined,
      onStatus: (status) => statuses.push(status),
      onStale: () => {
        stales += 1;
      },
    },
    {
      createSource: () => {
        const source = new FakeSource();
        sources.push(source);
        return source;
      },
    },
  );
  return { stream, sources, statuses, snapshots, stales: () => stales };
}

describe("живой поток", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("снимок переводит поток в «живой» и сбрасывает отступ", () => {
    const { stream, sources, statuses, snapshots } = harness();
    stream.start();
    expect(sources).toHaveLength(1);
    expect(statuses).toContain("connecting");
    sources[0]!.emit("snapshot", { cursor: 3 });
    expect(stream.state).toBe("live");
    expect(snapshots).toHaveLength(1);
    expect(statuses).toContain("live");
  });

  it("сторож 45 с закрывает мёртвый поток и открывает новый", async () => {
    const { stream, sources } = harness();
    stream.start();
    await vi.advanceTimersByTimeAsync(45_000);
    expect(sources[0]!.closed).toBe(true);
    expect(stream.state).toBe("error");
    await vi.advanceTimersByTimeAsync(1_000);
    expect(sources).toHaveLength(2);
    expect(stream.state).toBe("lost");
  });

  it("отступ растёт 1 → 2 → 4 с, а не долбит сразу", async () => {
    const { stream, sources } = harness();
    stream.start();
    await vi.advanceTimersByTimeAsync(45_000);
    expect(sources).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(sources).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(45_000);
    expect(sources[1]!.closed).toBe(true);
    await vi.advanceTimersByTimeAsync(1_999);
    expect(sources).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(1);
    expect(sources).toHaveLength(3);
  });

  it("«слишком много экранов» — окончательный отказ, без круга переподключений", async () => {
    const { stream, sources } = harness();
    stream.start();
    sources[0]!.emit("error", { code: "too-many", message: "много" });
    expect(stream.state).toBe("error");
    await vi.advanceTimersByTimeAsync(120_000);
    expect(sources).toHaveLength(1);
  });

  it("конец жизни потока (30 мин) — переподключение со свежим снимком", async () => {
    const { stream, sources } = harness();
    stream.start();
    sources[0]!.emit("end", { reason: "lifetime" });
    expect(sources[0]!.closed).toBe(true);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(sources).toHaveLength(2);
  });

  it("stop снимает и поток, и отступ", async () => {
    const { stream, sources, statuses } = harness();
    stream.start();
    stream.stop();
    expect(sources[0]!.closed).toBe(true);
    expect(statuses).toContain("stopped");
    await vi.advanceTimersByTimeAsync(60_000);
    expect(sources).toHaveLength(1);
  });
  it("кадр stale (G1) зовёт обработчик, не меняет состояние и сбрасывает отступ", async () => {
    const { stream, sources, statuses, stales } = harness();
    stream.start();
    // Смерть потока — отступ 1 с; после него подсказка должна снова открыть за 1 с, а не за 2.
    await vi.advanceTimersByTimeAsync(45_000);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(sources).toHaveLength(2);

    sources[1]!.emit("stale", {});
    expect(stales()).toBe(1);
    expect(stream.state).toBe("lost");
    expect(statuses).not.toContain("live");

    await vi.advanceTimersByTimeAsync(45_000);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(sources).toHaveLength(3);
  });

  it("stale без обработчика — не исключение: лента такой кадр игнорирует", async () => {
    const sources: FakeSource[] = [];
    const stream = new LiveStream(
      "/api/phone/live?session=s",
      {
        onSnapshot: () => undefined,
        onRecord: () => undefined,
        onAssistant: () => undefined,
        onStatus: () => undefined,
      },
      {
        createSource: () => {
          const source = new FakeSource();
          sources.push(source);
          return source;
        },
      },
    );
    stream.start();
    expect(() => sources[0]!.emit("stale", {})).not.toThrow();
  });
});
