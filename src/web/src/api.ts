// Двери калитки, которыми пользуется S1. Ни одного имени движка: телефон видит только эти адреса
// (контракт §2). Ответ читается как JSON; машинные коды человеку не показываются — калитка кладёт
// готовую фразу в `message`, а экран показывает её как есть.

export type SessionsPayload = {
  items: unknown[];
  total: number;
  shown: number;
  /** S4a: подсказка калитки «список мог измениться» (переигровки событий нет — R1 §5.2). */
  stale: boolean;
};

export type PagePayload = {
  records: unknown[];
  hasMore: boolean;
};

export class ApiFailure extends Error {
  readonly status: number;
  /** `true` — вход истёк (401): экран входа, а не «ошибка списка». */
  readonly unauthorized: boolean;

  constructor(status: number, message: string) {
    super(message);
    this.status = status;
    this.unauthorized = status === 401;
  }
}

const DEFAULT_TIMEOUT_MS = 12_000;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

async function readJson(response: Response): Promise<Record<string, unknown> | null> {
  const type = response.headers.get("content-type") ?? "";
  if (!type.toLowerCase().includes("application/json")) return null;
  try {
    const value: unknown = await response.json();
    return isRecord(value) ? value : null;
  } catch {
    return null;
  }
}

/** Фраза отказа: слова калитки, иначе пусто — экран подставит свой ключ вместо кода. */
function wordsOf(body: Record<string, unknown> | null): string {
  const message = body?.["message"];
  return typeof message === "string" ? message : "";
}

async function call(
  path: string,
  init: RequestInit,
  timeoutMs: number = DEFAULT_TIMEOUT_MS,
): Promise<{ response: Response; body: Record<string, unknown> | null }> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(path, {
      ...init,
      credentials: "same-origin",
      cache: "no-store",
      headers: {
        "Content-Type": "application/json",
        Accept: "application/json",
        ...(init.headers ?? {}),
      },
      signal: controller.signal,
    });
    const body = await readJson(response);
    return { response, body };
  } catch {
    // Сеть/таймаут: слов от калитки нет — экран скажет своё, а не машинное «network».
    throw new ApiFailure(0, "");
  } finally {
    clearTimeout(timer);
  }
}

function failed(response: Response, body: Record<string, unknown> | null): ApiFailure {
  return new ApiFailure(response.status, wordsOf(body));
}

function okOf(body: Record<string, unknown> | null): boolean {
  return body?.["ok"] === true;
}

function numberField(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) && value > 0 ? value : 0;
}

// ── вход ─────────────────────────────────────────────────────────────────────────────────────
/** `POST /pair` — шесть цифр. Успех только по `200` и `{ok:true}`; фраза отказа — словами. */
export async function pair(code: string): Promise<string> {
  const { response, body } = await call("/pair", {
    method: "POST",
    body: JSON.stringify({ code }),
  });
  if (!response.ok || !okOf(body)) throw failed(response, body);
  return wordsOf(body);
}

// ── список ───────────────────────────────────────────────────────────────────────────────────
/** G1: дверь-подсказка «список мог измениться» — SSE, только кадры `stale` (без аттрибутов сессии). */
export const LIST_LIVE_PATH = "/api/phone/list/live";

/** `GET /api/phone/sessions` — только корневые, лимит 50, `{items,total,shown,stale}`. */
export async function loadSessions(): Promise<SessionsPayload> {
  const { response, body } = await call("/api/phone/sessions", { method: "GET" });
  if (!response.ok || body === null || !okOf(body) || !Array.isArray(body["items"]))
    throw failed(response, body);
  const items = body["items"] as unknown[];
  return {
    items,
    total: Math.max(numberField(body["total"]), items.length),
    shown: Math.max(numberField(body["shown"]), items.length),
    stale: body["stale"] === true,
  };
}

// ── догон «раньше» ───────────────────────────────────────────────────────────────────────────
/** `POST /api/session/page` — та же нормализованная запись, что и у потока (контракт §2.4). */
export async function loadPage(
  sessionId: string,
  throughSeq: number,
  beforeSeq: number | null,
): Promise<PagePayload> {
  const request: Record<string, unknown> = {
    address: { kind: "session", sessionId },
    throughSeq,
    maxMessages: 50,
  };
  if (beforeSeq !== null) request["beforeSeq"] = beforeSeq;
  const { response, body } = await call("/api/session/page", {
    method: "POST",
    body: JSON.stringify({ args: { request } }),
  });
  if (!response.ok || body === null || !okOf(body) || !Array.isArray(body["records"]))
    throw failed(response, body);
  return { records: body["records"] as unknown[], hasMore: body["hasMore"] === true };
}

// ── вопрос агента ───────────────────────────────────────────────────────────────
/** `GET /api/phone/question` — состояние вопроса. Это СОСТОЯНИЕ, а не ошибка: `none` — законный ответ. */
export async function loadQuestion(): Promise<Record<string, unknown>> {
  const { response, body } = await call("/api/phone/question", { method: "GET" });
  if (!response.ok || body === null) throw failed(response, body);
  return body;
}

/** `POST /answer` — все ответы одним вызовом; успех только по `200` и `{ok:true}`. */
export async function sendAnswer(eventId: string, answers: unknown[]): Promise<string> {
  const { response, body } = await call("/answer", {
    method: "POST",
    body: JSON.stringify({ eventId, answers }),
  });
  if (!response.ok || !okOf(body)) throw failed(response, body);
  return wordsOf(body);
}

/** `POST /cancel` — отмена хода; JSON-ответ (не 303/HTML), поэтому «отменено» решает конверт. */
export async function sendCancel(eventId: string): Promise<string> {
  const { response, body } = await call("/cancel", {
    method: "POST",
    body: JSON.stringify({ eventId }),
  });
  if (!response.ok || !okOf(body)) throw failed(response, body);
  return wordsOf(body);
}

// ── S3: задача, стоп, очередь ────────────────────────────────────────────────────────────────
//
// Пять дверей (контракт §2–§6) отвечают ГОТОВОЙ фразой в `message` — экран показывает её как
// есть и своих слов под код не придумывает. Сроки клиента — с запасом к срокам дверей
// (D1/D3/D5 12 с, D2 22 с, D4 3,5 с): ждать дольше двери нет смысла — ответа уже не будет.

export const PROMPT_TIMEOUT_MS = 14_000;
export const NEW_TASK_TIMEOUT_MS = 24_000;
export const STOP_TIMEOUT_MS = 14_000;
export const QUEUE_TIMEOUT_MS = 8_000;

/** `200` D1/D2: `accepted` — только при `true`; `requestId` — эхо того, что отправляли. */
export type TaskAnswer = {
  sessionId: string;
  requestId: string;
  repeated: boolean;
  message: string;
};

/** `200` D3: два честных исхода — `stopped` и `idle` (второй — не ошибка). */
export type StopAnswer = { stopped: boolean; state: string; message: string };

/** `200` D5: `removed` и `gone` одинаково успешны (контракт §6.3). */
export type RemoveAnswer = { state: string; message: string };

function stringField(body: Record<string, unknown> | null, name: string): string {
  const value = body?.[name];
  return typeof value === "string" ? value : "";
}

/** `POST /api/phone/prompt` — задача в СУЩЕСТВУЮЩУЮ сессию. Успех — только `200` и `accepted`.
 *  `requestId` идёт тот же, что в черновике: на нём держится безопасный повтор (§7.1). */
export async function sendPrompt(
  sessionId: string,
  requestId: string,
  text: string,
): Promise<TaskAnswer> {
  const { response, body } = await call(
    "/api/phone/prompt",
    { method: "POST", body: JSON.stringify({ sessionId, requestId, text }) },
    PROMPT_TIMEOUT_MS,
  );
  if (!response.ok || body === null || !okOf(body) || body["accepted"] !== true)
    throw failed(response, body);
  return {
    sessionId: stringField(body, "sessionId") || sessionId,
    requestId: stringField(body, "requestId") || requestId,
    repeated: false,
    message: stringField(body, "message"),
  };
}

/** `POST /api/phone/new` — новый разговор и задача в него. `repeated` — повтор из карты калитки
 *  (второй разговор не создавался); человеку это не показывается, нужно проверкам. */
export async function sendNewTask(requestId: string, text: string): Promise<TaskAnswer> {
  const { response, body } = await call(
    "/api/phone/new",
    { method: "POST", body: JSON.stringify({ requestId, text }) },
    NEW_TASK_TIMEOUT_MS,
  );
  if (!response.ok || body === null || !okOf(body) || body["accepted"] !== true)
    throw failed(response, body);
  return {
    sessionId: stringField(body, "sessionId"),
    requestId: stringField(body, "requestId") || requestId,
    repeated: body["repeated"] === true,
    message: stringField(body, "message"),
  };
}

/** `POST /api/phone/stop` — отменить текущий ход. `stopped:false` — «уже не работает», не отказ. */
export async function sendStop(sessionId: string): Promise<StopAnswer> {
  const { response, body } = await call(
    "/api/phone/stop",
    { method: "POST", body: JSON.stringify({ sessionId }) },
    STOP_TIMEOUT_MS,
  );
  if (!response.ok || body === null || !okOf(body)) throw failed(response, body);
  return {
    stopped: body["stopped"] === true,
    state: stringField(body, "state"),
    message: stringField(body, "message"),
  };
}

/** `GET /api/phone/queue?session=…` — очередь одной сессии. Разбор по белому списку — в модели. */
export async function loadQueue(sessionId: string): Promise<Record<string, unknown>> {
  const { response, body } = await call(
    `/api/phone/queue?session=${encodeURIComponent(sessionId)}`,
    { method: "GET" },
    QUEUE_TIMEOUT_MS,
  );
  if (!response.ok || body === null || !okOf(body)) throw failed(response, body);
  return body;
}

/** `POST /api/phone/queue/remove` — убрать ОДИН пункт. Повтор идемпотентен по исходу (§6.3). */
export async function removeQueueItem(sessionId: string, itemId: string): Promise<RemoveAnswer> {
  const { response, body } = await call("/api/phone/queue/remove", {
    method: "POST",
    body: JSON.stringify({ sessionId, itemId }),
  });
  if (!response.ok || body === null || !okOf(body)) throw failed(response, body);
  return { state: stringField(body, "state"), message: stringField(body, "message") };
}
