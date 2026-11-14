// Живой поток сессии: `EventSource` к двери `GET /api/phone/live?session=…`.
//
// Курсора «продолжить с места» у движка НЕТ (контракт §2.2): каждое переподключение даёт свежий
// `snapshot`, а догон — перечитывание, не «докрутка». Сторож 45 с без единого события (пинг тоже
// событие) закрывает мёртвый поток; отступ 1 → 2 → 4 → 8 → 15 с.

import { SSE_BACKOFF_MS, SSE_WATCHDOG_MS } from "../config";

export type LiveStatus = "connecting" | "live" | "lost" | "error" | "stopped";

export type LiveFailure = { code: string; message: string };

export type LiveHandlers = {
  onSnapshot: (payload: unknown) => void;
  onRecord: (payload: unknown) => void;
  onAssistant: (frame: unknown) => void;
  onStatus: (status: LiveStatus, failure?: LiveFailure) => void;
  /** S4a: кадр `queue` из того же живого потока (очередь без нажатия и без опроса). */
  onQueue?: (payload: unknown) => void;
  /**
   * G1: кадр-подсказка `event: stale` — «список мог измениться» (только бит, без адреса сессии
   * и без `running`). Есть только у двери `/api/phone/list/live`; у ленты такого кадра нет.
   */
  onStale?: () => void;
};

/** Что нужно от `EventSource` — только это, и тогда поток проверяется без браузера. */
export type SourceLike = {
  addEventListener: (type: string, listener: (event: MessageEvent | Event) => void) => void;
  close: () => void;
};

export type SourceFactory = (url: string) => SourceLike;

export type LiveOptions = {
  createSource?: SourceFactory;
  watchdogMs?: number;
  backoffMs?: readonly number[];
};

function isMessage(event: MessageEvent | Event): event is MessageEvent {
  return typeof (event as MessageEvent).data === "string";
}

function parse(event: MessageEvent | Event): unknown {
  if (!isMessage(event)) return null;
  try {
    return JSON.parse(event.data);
  } catch {
    return null;
  }
}

export class LiveStream {
  private source: SourceLike | null = null;
  private watchdog: ReturnType<typeof setTimeout> | null = null;
  private retry: ReturnType<typeof setTimeout> | null = null;
  private attempts = 0;
  private stopped = true;
  private status: LiveStatus = "stopped";
  private readonly createSource: SourceFactory;
  private readonly watchdogMs: number;
  private readonly backoffMs: readonly number[];

  constructor(
    private readonly url: string,
    private readonly handlers: LiveHandlers,
    options: LiveOptions = {},
  ) {
    this.createSource = options.createSource ?? ((target) => new EventSource(target));
    this.watchdogMs = options.watchdogMs ?? SSE_WATCHDOG_MS;
    this.backoffMs = options.backoffMs ?? SSE_BACKOFF_MS;
  }

  get state(): LiveStatus {
    return this.status;
  }

  start(): void {
    if (!this.stopped) return;
    this.stopped = false;
    this.attempts = 0;
    this.connect();
  }

  stop(): void {
    this.stopped = true;
    this.closeSource();
    if (this.retry !== null) {
      clearTimeout(this.retry);
      this.retry = null;
    }
    this.setStatus("stopped");
  }

  /** Ручное «Проверить»: закрыть и открыть заново, не дожидаясь отступа. */
  restart(): void {
    const wasStopped = this.stopped;
    this.closeSource();
    if (this.retry !== null) {
      clearTimeout(this.retry);
      this.retry = null;
    }
    this.attempts = 0;
    this.stopped = false;
    if (wasStopped) this.setStatus("connecting");
    this.connect();
  }

  private connect(): void {
    if (this.stopped) return;
    this.setStatus(this.attempts === 0 && this.status !== "lost" ? "connecting" : "lost");
    let source: SourceLike;
    try {
      source = this.createSource(this.url);
    } catch {
      this.scheduleRetry();
      return;
    }
    this.source = source;
    this.armWatchdog();
    for (const name of [
      "snapshot",
      "event",
      "queue",
      "assistant",
      "ping",
      "stale",
      "error",
      "end",
    ] as const) {
      source.addEventListener(name, (event) => this.onFrame(name, event));
    }
  }

  private onFrame(name: string, event: MessageEvent | Event): void {
    this.armWatchdog();
    switch (name) {
      case "snapshot": {
        this.attempts = 0;
        this.setStatus("live");
        this.handlers.onSnapshot(parse(event));
        return;
      }
      case "event":
        this.handlers.onRecord(parse(event));
        return;
      case "queue":
        this.handlers.onQueue?.(parse(event));
        return;
      case "assistant":
        this.handlers.onAssistant(parse(event));
        return;
      case "ping":
        return;
      case "error": {
        // Своё событие `error` несёт код и фразу; сетевой сбой приходит без данных.
        const data = isMessage(event) ? parse(event) : null;
        const code =
          typeof data === "object" &&
          data !== null &&
          typeof (data as Record<string, unknown>)["code"] === "string"
            ? String((data as Record<string, unknown>)["code"])
            : "lost";
        const message =
          typeof data === "object" &&
          data !== null &&
          typeof (data as Record<string, unknown>)["message"] === "string"
            ? String((data as Record<string, unknown>)["message"])
            : "";
        this.fail({ code, message }, code === "too-many");
        return;
      }
      case "stale":
        // Подсказка — живая связь, а не событие сессии: связь доказана кадром, поэтому счётчик
        // отступов сброшен, а состояние/слова не меняются (список не говорит человеку о стыке).
        this.attempts = 0;
        this.handlers.onStale?.();
        return;
      case "end":
        this.fail({ code: "lifetime", message: "" }, false);
        return;
      default:
        return;
    }
  }

  private armWatchdog(): void {
    if (this.watchdog !== null) clearTimeout(this.watchdog);
    this.watchdog = setTimeout(
      () => this.fail({ code: "lost", message: "" }, false),
      this.watchdogMs,
    );
  }

  private fail(failure: LiveFailure, permanent: boolean): void {
    this.closeSource();
    if (this.stopped) return;
    this.setStatus("error", failure);
    if (permanent) {
      this.stopped = true;
      return;
    }
    this.scheduleRetry();
  }

  private scheduleRetry(): void {
    if (this.stopped) return;
    const delay = this.backoffMs[Math.min(this.attempts, this.backoffMs.length - 1)] ?? 15_000;
    this.attempts += 1;
    this.retry = setTimeout(() => {
      this.retry = null;
      this.connect();
    }, delay);
  }

  private closeSource(): void {
    if (this.watchdog !== null) {
      clearTimeout(this.watchdog);
      this.watchdog = null;
    }
    if (this.source !== null) {
      this.source.close();
      this.source = null;
    }
  }

  private setStatus(status: LiveStatus, failure?: LiveFailure): void {
    if (this.status === status && failure === undefined) return;
    this.status = status;
    this.handlers.onStatus(status, failure);
  }
}
