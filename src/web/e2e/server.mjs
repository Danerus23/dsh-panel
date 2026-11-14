// Подставная калитка для e2e: НЕ продукт, а стенд. Отдаёт собранную оболочку из `WebAssets`,
// статику `/assets/*`, двери S1 с выдуманными сессиями и SSE-поток с заранее разыгранными кадрами.
//
// Зачем свой стенд: живая калитка поднимает движок и трогает данные владельца, а проверять надо
// ПОВЕДЕНИЕ ЭКРАНА (дедупликация черновика, догон, состояния). Двери повторяют контракт §2.

import http from "node:http";
import { createReadStream, existsSync, readFileSync } from "node:fs";
import { extname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = fileURLToPath(new URL(".", import.meta.url));
const WEB = resolve(HERE, "../../DshPanel/WebAssets");
const DICT = resolve(HERE, "../../DshPanel/Localization");
const PORT = Number(process.env.PORT ?? 43217);

const overlay = JSON.parse(readFileSync(join(HERE, "fixtures/words-overlay.json"), "utf8"));
const panel = {
  ru: JSON.parse(readFileSync(join(DICT, "ru.json"), "utf8")),
  en: JSON.parse(readFileSync(join(DICT, "en.json"), "utf8")),
  zh: JSON.parse(readFileSync(join(DICT, "zh.json"), "utf8")),
};
// Стенд говорит СЛОВАМИ ПАНЕЛИ; фикстура — запасной путь для ключей, которых в панели ещё нет.
// `WORDS_FROM_PANEL=1` — проверка «живая страница не показывает имена ключей»: берём ТОЛЬКО панель.
const panelOnly = process.env.WORDS_FROM_PANEL === "1";
const WORDS = panelOnly
  ? { ru: panel.ru, en: panel.en, zh: panel.zh }
  : {
      ru: { ...panel.ru, ...overlay.ru },
      en: { ...panel.en, ...overlay.en },
      zh: { ...panel.zh, ...overlay.zh },
    };

const shell = readFileSync(join(WEB, "shell.html"), "utf8");
const assets = JSON.parse(readFileSync(join(WEB, "assets.json"), "utf8"));

const MIME = {
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".woff2": "font/woff2",
  ".map": "application/json",
  ".html": "text/html; charset=utf-8",
};

/** Состояние стенда. Тест меняет его через `POST /__mock/scenario`. */
let scenario = {
  authed: true,
  lang: "ru",
  list: "ok",
  pair: "ok",
  question: "none",
  answer: "ok",
  cancel: "ok",
  // S3: двери задачи, стопа и очереди (контракт §2–§6)
  task: "ok",
  newtask: "ok",
  stop: "stopped",
  queue: "ok",
  remove: "ok",
  // W5: живой список — одна строка меняет слово по подсказке; поток подсказки может рваться.
  w5Running: true,
  // W14 (DEF-150): строка `session-live` говорит «работает», пока его живой ход не кончился
  // (`turn-end` ниже снимает флаг — как это делает настоящая калитка, `PhoneLive.cs`).
  liveRunning: true,
  listLive: "ok",
};

/** Последнее тело `POST /answer` — чтобы проверка видела, что ушло, а не только что показалось. */
let lastAnswer = null;

/** S3: тела и счётчики дверей — проверка видит, что ушло, а не только что показалось. */
let lastPrompt = null;
let lastNew = null;
let promptCalls = 0;
let newCalls = 0;
let stopCalls = 0;
let lastQueueSession = "";
/** S4a: счётчики чтения списка и очереди — «таймера нет» и «живой кадр не опрос» доказываются числом. */
let listCalls = 0;
let queueCalls = 0;
/** W10 (§5.2): сколько раз стучали в `POST /answer` — «Далее» обязано НЕ отправлять, и это число. */
let answerCalls = 0;
/** S4a: одноразовая подсказка `stale` — калитка «снимает флаг, выдавая ответ» (R1 §5.2). */
let stalePending = 0;
/** S4a: НОМЕР ЗАХОДА в поток `session-retry` — первый отказ постоянный, второй живой. */
let liveSeq = 0;
/** S4a/D2: номер захода в поток `session-lost-many` — длинный снимок отдаётся только первый раз. */
let lostSeq = 0;
/** Догруз старых (D1 §4.2): сколько страниц уже отдано и сколько раз просили — «одна за подход». */
let pageCalls = 0;
let olderPage = 0;
/** Хвост S4a: живой поток `session-long` — в него льёт `POST /__mock/push` («новые, пока человек листал»). */
let longStream = null;
/** W5: живые подсказки списка — поток `GET /api/phone/list/live` и счётчики открытий/закрытий. */
const listLiveStreams = new Set();
let listLiveOpens = 0;
let listLiveCloses = 0;
/** Хвост S4a: номер записи для этих сообщений; сбрасывается новым сценарием (как все счётчики). */
let pushSeq = 50;
/** S4a: имя события `queue` в живом потоке и его форма — как у `GET /api/phone/queue`. */

/** Очередь стенда: её меняют D1 и D5, её читает D4. */
let queueItems = [
  {
    id: "item-steering",
    text: "Сначала посмотри журнал сборки.",
    chars: 31,
    placement: "steering",
  },
  {
    id: "item-queued",
    text: "Потом почини два упавших теста.",
    chars: 32,
    placement: "queued",
  },
];

function queueBody(lang) {
  const sessionId = lastQueueSession || "session-live";
  const base = { ok: true, sessionId };
  if (scenario.queue === "no-live")
    return {
      status: 200,
      body: {
        ...base,
        state: "no-live",
        total: 0,
        shown: 0,
        hasMore: false,
        items: [],
        message: WORDS[lang].PhoneQueueNoLive,
      },
    };
  if (scenario.queue === "unreadable")
    return { status: 502, body: { ok: false, message: WORDS[lang].PhoneQueueUnreadable } };
  if (scenario.queue === "busy")
    return { status: 429, body: { ok: false, message: WORDS[lang].PhoneQueueBusy } };
  if (scenario.queue === "empty")
    return {
      status: 200,
      body: { ...base, state: "ok", total: 0, shown: 0, hasMore: false, items: [] },
    };
  if (scenario.queue === "many") {
    const long = `${"Длинный пункт очереди. ".repeat(12)}конец`;
    const items = Array.from({ length: 20 }, (_, index) => ({
      id: `item-many-${index}`,
      text: index === 0 ? `${long.slice(0, 299)}…` : `Пункт ${index + 1}`,
      chars: index === 0 ? long.length : 8,
      placement: index % 2 === 0 ? "steering" : "queued",
    }));
    return {
      status: 200,
      body: { ...base, state: "ok", total: 45, shown: 20, hasMore: true, items },
    };
  }
  if (scenario.queue === "manyEqual") {
    // S4a, D2 F-06: `hasMore: true` при `shown == total` — строка «Показаны первые N из N» не должна печататься.
    const items = queueItems.map((item, index) => ({ ...item, id: `item-eq-${index}` }));
    return {
      status: 200,
      body: {
        ...base,
        state: "ok",
        total: items.length,
        shown: items.length,
        hasMore: true,
        items,
      },
    };
  }
  return {
    status: 200,
    body: {
      ...base,
      state: "ok",
      total: queueItems.length,
      shown: queueItems.length,
      hasMore: false,
      items: queueItems,
    },
  };
}
const SESSION_LIVE = {
  sessionId: "session-live",
  title: "Починить калитку",
  updatedAt: 1761770000000,
  running: true,
  blank: false,
  question: false,
  stats: { turns: 2, steps: 5 },
  outline: [
    { turn: 2, seq: 41, prompt: "Проверь сборку", response: "Сборка прошла. Упали два теста." },
  ],
  inbox: { nextTurn: 0, nextStep: 0 },
};
const SESSION_QUESTION = {
  sessionId: "session-question",
  title: "Собрать список сессий",
  updatedAt: 1761769000000,
  running: false,
  blank: false,
  question: true,
  stats: { turns: 4, steps: 12 },
  outline: [{ turn: 4, seq: 30, prompt: "Что дальше?", response: "Нужен ваш выбор." }],
  inbox: { nextTurn: 0, nextStep: 0 },
};
const SESSION_BLANK = {
  sessionId: "session-blank",
  title: null,
  updatedAt: 1761768000000,
  running: false,
  blank: true,
  question: false,
  stats: { turns: 0, steps: 0 },
  outline: [],
  inbox: { nextTurn: 0, nextStep: 0 },
};
const SESSION_DONE = {
  sessionId: "session-done",
  title: "Обновление панели",
  updatedAt: 1761767000000,
  running: false,
  blank: false,
  question: false,
  stats: { turns: 7, steps: 20 },
  outline: [{ turn: 7, seq: 99, prompt: "Собери", response: "Готово." }],
  inbox: { nextTurn: 0, nextStep: 2 },
};

/** Второй blank: на телефоне 6 строк, из них 2 пустые — видимых 4 (D1 §6, приёмка 11.11.2026). */
const SESSION_BLANK2 = {
  sessionId: "session-blank-2",
  title: null,
  updatedAt: 1761766000000,
  running: false,
  blank: true,
  question: false,
  stats: { turns: 0, steps: 0 },
  outline: [],
  inbox: { nextTurn: 0, nextStep: 0 },
};

/** Шестая строка — обычная сессия: вместе с live, question и done их четыре ВИДИМЫХ из шести. */
const SESSION_DONE2 = {
  sessionId: "session-done-2",
  title: "Проверка сборки",
  updatedAt: 1761766000000,
  running: false,
  blank: false,
  question: false,
  stats: { turns: 5, steps: 11 },
  outline: [{ turn: 5, seq: 60, prompt: "Проверь", response: "Готово." }],
  inbox: { nextTurn: 0, nextStep: 0 },
};

function listBody(lang) {
  listCalls += 1;
  const stale = stalePending > 0 || scenario.stale === true;
  if (stalePending > 0) stalePending -= 1;
  if (scenario.list === "error")
    return { status: 502, body: { ok: false, message: WORDS[lang].PhoneWebListUnreadable } };
  if (scenario.list === "empty")
    return { status: 200, body: { ok: true, stale, total: 0, shown: 0, items: [] } };
  // W5: одна строка, меняющая слово по подсказке (running → false без возврата на вкладку).
  if (scenario.list === "w5") {
    const items = [
      {
        ...SESSION_LIVE,
        sessionId: "session-w5",
        title: "Живой список",
        running: scenario.w5Running !== false,
      },
    ];
    return { status: 200, body: { ok: true, stale, total: 1, shown: 1, items } };
  }
  // W14 (DEF-150): строка сессии ГОВОРИТ «Работает» (работает подчинённая), а лента кончается
  // `turn-end`. Раньше шапка печатала «Закончила» — ровно жалоба владельца 12.11.2026.
  // `running` переключается `POST /__mock/w5 {running}`: тест видит, что шапка гаснет сама.
  if (scenario.list === "headLive") {
    const items = [
      {
        ...SESSION_DONE,
        sessionId: "session-idle",
        title: "Спокойная сессия",
        running: scenario.w5Running !== false,
      },
    ];
    return { status: 200, body: { ok: true, stale, total: 1, shown: 1, items } };
  }
  if (scenario.list === "many") {
    const items = [];
    for (let i = 0; i < 50; i += 1) {
      items.push({ ...SESSION_DONE, sessionId: `session-many-${i}`, title: `Сессия ${i + 1}` });
    }
    return { status: 200, body: { ok: true, stale, total: 60, shown: 50, items } };
  }
  // D1 §6: blank с ВИСЯЩИМ ВОПРОСОМ остаётся и говорит «Ждёт ответа» — вопрос не пустота.
  if (scenario.list === "blankQuestion") {
    const items = [
      SESSION_LIVE,
      {
        ...SESSION_BLANK,
        sessionId: "session-blank-question",
        question: true,
        updatedAt: 1761769000000,
      },
      SESSION_BLANK2,
      SESSION_QUESTION,
    ];
    return {
      status: 200,
      body: { ok: true, stale, total: items.length, shown: items.length, items },
    };
  }
  // D1 §6: все строки пустые → существующее пустое состояние, а не пустой список.
  if (scenario.list === "allBlank") {
    const items = [SESSION_BLANK, SESSION_BLANK2];
    return {
      status: 200,
      body: { ok: true, stale, total: items.length, shown: items.length, items },
    };
  }
  // D3/W4: строки списка расходятся с живым потоком — источник шапки живой.
  // session-many: строка «Закончила», а в сессии идёт ход. session-created-1: строка есть, лента пуста.
  if (scenario.list === "quietRoot") {
    const items = [
      { ...SESSION_DONE2, sessionId: "session-many", title: "Длинная сессия" },
      { ...SESSION_DONE2, sessionId: "session-created-1", title: "Созданная сессия" },
    ];
    return {
      status: 200,
      body: { ok: true, stale, total: items.length, shown: items.length, items },
    };
  }
  // 6 строк, из них 2 blank (слово владельца 11.11.2026): видимых 4, и «Сессий: 4».
  const items = [
    // W14: флаг строки живой сессии — из состояния стенда, а не зашит «true» (иначе после
    // `turn-end` строка продолжала бы говорить «Работает» и шапка никогда бы не гасла).
    { ...SESSION_LIVE, running: scenario.liveRunning !== false },
    SESSION_QUESTION,
    SESSION_BLANK,
    SESSION_BLANK2,
    SESSION_DONE,
    SESSION_DONE2,
  ];
  return {
    status: 200,
    body: { ok: true, stale, total: items.length, shown: items.length, items },
  };
}

const T = 1761770100000;
const FEED = [
  { type: "turn", seq: 1, time: T, turn: 1 },
  { type: "user", seq: 2, time: T, text: "Проверь сборку и скажи, что упало." },
  { type: "assistant", seq: 3, time: T, text: "Сборка прошла. Упали два теста в наборе калитки." },
  { type: "tool-call", seq: 4, time: T, name: "dotnet test", callId: "call-1" },
  { type: "tool-result", seq: 5, time: T, callId: "call-1", failed: false },
  { type: "assistant", seq: 6, time: T, text: "Сейчас смотрю журнал." },
];

// S3c: реплика агента с разметкой и с КАЖДОЙ границей владельца в одном тексте —
// разрешённое (абзац, заголовок, список, код, цитата, ссылка) и запрещённое (сырой HTML, картинка,
// таблица, чужие схемы). Так одна проверка видит и то, и другое рядом.
const MD_LONG_CODE = `const veryLongLine = "${"0123456789".repeat(14)}"; // строка длиннее окна телефона`;
const MD_RICH = [
  "## Итог проверки",
  "",
  "Сборка **прошла**, но *два* теста упали — см. `dp-gate.test.ts`.",
  "",
  "- пункт раз",
  "- пункт два",
  "  - вложенный",
  "",
  "> Цитата из журнала.",
  "",
  "```js",
  MD_LONG_CODE,
  "```",
  "",
  "| проверка | итог |",
  "| --- | --- |",
  "| сборка | зелёная |",
  "",
  "Картинка: ![схема сборки](https://example.com/scheme.png)",
  "",
  "Сырой HTML: <b>жирный тегом</b>, <script>alert(1)</script>, <img src=x onerror=alert(2)>",
  "",
  "Ссылки: [годная](https://example.com/x), [злая](javascript:alert(3)), [рядом](/session/1).",
].join("\n");

const OLDER = [
  { type: "turn", seq: 1, time: T - 100, turn: 1 },
  { type: "user", seq: 2, time: T - 99, text: "Самое первое сообщение человека." },
  { type: "assistant", seq: 3, time: T - 98, text: "Самое первое сообщение агента." },
];

function readBody(req, done) {
  let raw = "";
  req.on("data", (chunk) => {
    raw += chunk;
  });
  req.on("end", () => done(raw));
}

/** Ответ двери вопроса по сценарию: форма та же, что у калитки (контракт §2.3). */
function questionBody(words) {
  const sessionId = scenario.questionSession ?? "session-live";
  const base = {
    eventId: "evt-1",
    sessionId,
    fromSubagent: scenario.fromSubagent === true,
    expiresAt: new Date(Date.now() + 600_000).toISOString(),
    remainingSeconds: 600,
  };
  const single = [
    {
      id: "q1",
      question: "Что делаем с упавшими тестами?",
      detail: "Два теста в наборе калитки.",
      header: "Сборка",
      options: [
        { label: "Починить сейчас" },
        { label: "Оставить как есть", description: "Запишем в долги" },
        { label: "Сначала посмотреть журнал" },
      ],
    },
  ];

  switch (scenario.question) {
    case "single":
      return { state: "question", ...base, questions: single };
    case "multi":
      return {
        state: "question",
        ...base,
        questions: [
          {
            id: "q1",
            question: "Какие проверки запустить?",
            options: [{ label: "Сборка" }, { label: "Тесты" }, { label: "Формат" }],
            multiSelect: true,
          },
        ],
      };
    case "two":
      return {
        state: "question",
        ...base,
        questions: [
          single[0],
          {
            id: "q2",
            question: "Какие проверки?",
            options: [{ label: "Сборка" }, { label: "Тесты" }],
            multiSelect: true,
          },
        ],
      };
    case "nooptions":
      return {
        state: "question",
        ...base,
        questions: [{ id: "q1", question: "Опишите словами, что сделать." }],
      };
    case "fallback":
      return { state: "question", ...base, sessionId: "", questions: single };
    case "taken":
      return { state: "taken", eventId: "evt-1", message: words.PhonePageTaken };
    case "unreadable":
      return { state: "unreadable", eventId: "evt-1", message: words.PhonePageUnreadable };
    case "no-answer":
      return {
        state: "no-answer",
        eventId: "evt-1",
        message: String(words.PhoneGateQuestionNoAnswerFormat).replace("{0}", "10"),
      };
    case "answered":
      return { state: "answered", eventId: "evt-1", message: words.PhonePageAnswered };
    case "cancelled":
      return { state: "cancelled", eventId: "evt-1", message: words.PhonePageCancelled };
    default:
      return { state: "none", message: words.PhoneGateNoQuestionNow };
  }
}

function json(res, status, body) {
  const text = JSON.stringify(body);
  res.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff",
  });
  res.end(text);
}

function html(res, body) {
  res.writeHead(200, {
    "Content-Type": "text/html; charset=utf-8",
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff",
  });
  res.end(body);
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

function page(lang) {
  const words = WORDS[lang];
  const boot = {
    lang,
    authed: scenario.authed,
    device: "телефон",
    words,
  };
  return shell
    .replaceAll("__LANG__", lang)
    .replaceAll("__TITLE__", escapeHtml(words.PhonePageTitle ?? ""))
    .replaceAll("__BOOT__", escapeHtml(JSON.stringify(boot)))
    .replaceAll("__LOADING__", escapeHtml(words.PhoneWebLoading ?? ""))
    .replaceAll("__BOOT_FALLBACK__", escapeHtml(words.PhoneWebBootFallback ?? ""))
    .replaceAll("__NOSCRIPT_TITLE__", escapeHtml(words.PhoneWebNoScriptTitle ?? ""))
    .replaceAll("__NOSCRIPT_TEXT__", escapeHtml(words.PhoneWebNoScript ?? ""))
    .replaceAll("__JS__", assets.js)
    .replaceAll("__CSS__", assets.css);
}

function sse(res, lang) {
  res.writeHead(200, {
    "Content-Type": "text/event-stream; charset=utf-8",
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff",
    Connection: "keep-alive",
  });
  const send = (event, data) => {
    if (res.writableEnded) return;
    res.write(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`);
  };
  const timers = [];
  const later = (ms, fn) => timers.push(setTimeout(fn, ms));
  const stop = () => {
    for (const timer of timers) clearTimeout(timer);
    timers.length = 0;
  };
  res.on("close", stop);
  return { send, later, stop };
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url ?? "/", `http://127.0.0.1:${PORT}`);
  const lang = scenario.lang;
  const words = WORDS[lang];

  if (url.pathname === "/health") {
    json(res, 200, { ok: true });
    return;
  }

  if (url.pathname === "/__mock/scenario" && req.method === "POST") {
    let raw = "";
    req.on("data", (chunk) => {
      raw += chunk;
    });
    req.on("end", () => {
      try {
        const patch = JSON.parse(raw);
        scenario = { ...scenario, ...patch };
        // Новый сценарий — чистые счётчики: проверка числа запросов не наследует чужие.
        listCalls = 0;
        queueCalls = 0;
        stalePending = patch.staleOnce === true ? 1 : 0;
        liveSeq = 0;
        lostSeq = 0;
        pageCalls = 0;
        olderPage = 0;
        pushSeq = 50;
        // W5-флаг живёт В СЦЕНАРИИ (`scenario.w5Running`), отдельной переменной его нет; здесь
        // стояло `w5Running = true` — в strict-модуле это ReferenceError ПОСЛЕ применения патча,
        // и стенд отвечал 400 при живом патче (находка L1 F3).
        scenario.w5Running = true;
        // W14: строка живой сессии — снова «работает»: иначе `turn-end` прошлого теста оставил бы
        // шапку следующего в «Закончила» (тот же класс дефекта, что `olderPage` в W12).
        scenario.liveRunning = patch.liveRunning !== false;
        listLiveOpens = 0;
        listLiveCloses = 0;
        json(res, 200, { ok: true });
      } catch {
        json(res, 400, { ok: false, message: "bad scenario" });
      }
    });
    return;
  }

  if (url.pathname === "/") {
    html(res, page(lang));
    return;
  }

  if (url.pathname.startsWith("/assets/")) {
    const name = url.pathname.slice("/assets/".length);
    if (name.includes("..") || name.includes("\\") || name.includes("\0")) {
      json(res, 404, { ok: false, message: "not found" });
      return;
    }
    const file = join(WEB, "assets", name);
    if (!existsSync(file)) {
      json(res, 404, { ok: false, message: "not found" });
      return;
    }
    res.writeHead(200, {
      "Content-Type": MIME[extname(name)] ?? "application/octet-stream",
      "Cache-Control": "public, max-age=31536000, immutable",
    });
    createReadStream(file).pipe(res);
    return;
  }

  if (url.pathname === "/api/phone/sessions") {
    const { status, body } = listBody(lang);
    json(res, status, body);
    return;
  }

  if (url.pathname === "/api/phone/question") {
    json(res, 200, questionBody(words));
    return;
  }

  if (url.pathname === "/__mock/last-answer" && req.method === "GET") {
    json(res, 200, { ok: true, body: lastAnswer });
    return;
  }

  // Хвост S4a: «новые сообщения, пока человек был не внизу» — проверка не ждёт таймера стенда,
  // а САМА решает, когда пришли новые: телу `{ count }`, по умолчанию — два.
  if (url.pathname === "/__mock/push" && req.method === "POST") {
    readBody(req, (raw) => {
      let count = 2;
      try {
        const body = JSON.parse(raw);
        if (Number.isFinite(Number(body.count))) count = Math.max(1, Number(body.count));
      } catch {
        // пустое тело — шлём два, как договорено в проверке
      }
      if (longStream === null) {
        json(res, 409, { ok: false, message: "нет живого потока session-long" });
        return;
      }
      for (let i = 0; i < count; i += 1) {
        longStream.send("event", {
          record: { type: "assistant", seq: pushSeq, time: T, text: `Новое сообщение ${pushSeq}.` },
        });
        pushSeq += 1;
      }
      json(res, 200, { ok: true, last: pushSeq - 1 });
    });
    return;
  }

  // W5: дверь-подсказка `GET /api/phone/list/live` — SSE, только кадры `stale` (как G1).
  if (url.pathname === "/api/phone/list/live") {
    const stream = sse(res, lang);
    listLiveStreams.add(stream);
    listLiveOpens += 1;
    res.on("close", () => {
      listLiveStreams.delete(stream);
      listLiveCloses += 1;
    });
    // Сценарий `listLive: "error"`: первый заход рвётся кадром `error` — клиент обязан тихо
    // переподключиться (второй заход уже нормальный), а человеку не сказать ни слова.
    if (scenario.listLive === "error" && listLiveOpens === 1) {
      setTimeout(() => {
        stream.send("error", { code: "lost" });
        res.end();
      }, 150);
    }
    return;
  }

  // W5: подсказка «список мог измениться» — тест сам решает, когда её послать.
  if (url.pathname === "/__mock/list-hint" && req.method === "POST") {
    readBody(req, (raw) => {
      let count = 1;
      try {
        const body = JSON.parse(raw);
        if (Number.isFinite(Number(body.count))) count = Math.max(1, Number(body.count));
      } catch {
        // пустое тело — один кадр
      }
      let sent = 0;
      for (let i = 0; i < count; i += 1)
        for (const stream of listLiveStreams) {
          stream.send("stale", {});
          sent += 1;
        }
      json(res, 200, { ok: true, sent, streams: listLiveStreams.size });
    });
    return;
  }

  // W5: переключить строку списка «Работает ↔ Закончила» (без подсказки — её шлёт list-hint).
  if (url.pathname === "/__mock/w5" && req.method === "POST") {
    readBody(req, (raw) => {
      try {
        const body = JSON.parse(raw);
        if (typeof body.running === "boolean") scenario.w5Running = body.running;
      } catch {
        // пустое тело — ничего не меняем
      }
      json(res, 200, { ok: true, running: scenario.w5Running !== false });
    });
    return;
  }

  if (url.pathname === "/api/session/page") {
    pageCalls += 1;
    // R4: отказ догруза «раньше» — сценарий older: "fail".
    if (scenario.older === "fail") {
      json(res, 503, { ok: false, message: "связь с кабинетом управления не отвечает" });
      return;
    }
    // D1 §4.2: страницы старше снимка session-many — по 2 записи, hasMore у первой.
    if (scenario.older === "pages") {
      olderPage += 1;
      const page = olderPage;
      if (page <= 2) {
        const base = page === 1 ? 30 : 20;
        json(res, 200, {
          ok: true,
          hasMore: page < 2,
          records: [
            {
              type: "user",
              seq: base + 1,
              time: T - base,
              text: `Старое сообщение человека, страница ${page}.`,
            },
            {
              type: "assistant",
              seq: base + 2,
              time: T - base,
              text: `Старое сообщение агента, страница ${page}.`,
            },
          ],
        });
        return;
      }
    }
    json(res, 200, { ok: true, records: OLDER, hasMore: false });
    return;
  }

  if (url.pathname === "/pair" && req.method === "POST") {
    if (scenario.pair === "bad") {
      json(res, 403, { ok: false, message: "Код не подошёл — панель показывает новый." });
      return;
    }
    json(res, 200, { ok: true, message: "" });
    return;
  }

  if (url.pathname === "/answer" && req.method === "POST") {
    readBody(req, (raw) => {
      // Считаем ЛЮБОЙ заход в дверь ответа — и удачный, и отказный: «Далее» не должна давать ни одного.
      answerCalls += 1;
      if (scenario.answer === "stale") {
        json(res, 403, { ok: false, message: words.PhoneGateStaleQuestion });
        return;
      }
      if (scenario.answer === "not-taken") {
        json(res, 502, { ok: false, message: words.PhoneGateAnswerNotTaken });
        return;
      }
      try {
        lastAnswer = JSON.parse(raw);
      } catch {
        lastAnswer = { raw };
      }
      scenario.question = "answered";
      json(res, 200, { ok: true, message: words.PhonePageAnswered });
    });
    return;
  }

  if (url.pathname === "/cancel" && req.method === "POST") {
    readBody(req, () => {
      if (scenario.cancel === "fail") {
        json(res, 502, {
          ok: false,
          message: String(words.PhonePageCancelFailedFormat).replace("{0}", "движок молчит"),
        });
        return;
      }
      scenario.question = "cancelled";
      json(res, 200, { ok: true, message: words.PhonePageCancelled });
    });
    return;
  }

  if (url.pathname === "/ask") {
    html(
      res,
      `<!doctype html><html lang="${lang}"><body><h1>${escapeHtml(words.PhonePageQuestionTitle)}</h1></body></html>`,
    );
    return;
  }

  if (url.pathname === "/api/phone/live") {
    const id = url.searchParams.get("session") ?? "";
    const stream = sse(res, lang);
    // Хвост S4a: ДЛИННАЯ лента для органа «к последнему» — страница выше окна, догруза НЕТ
    // (иначе прокрутка вверх сама подгружала бы старые и мешала бы мерить порог появления).
    if (id === "session-long") {
      const records = Array.from({ length: 4 }).flatMap((_, page) =>
        FEED.map((record) => ({ ...record, seq: record.seq + page * 10, time: T + page * 10 })),
      );
      stream.send("snapshot", {
        header: { id, createdAt: T, title: "Длинная лента" },
        cursor: 40,
        hasMore: false,
        records,
        assistantDraft: null,
      });
      // В этот поток льёт `POST /__mock/push` — «новые, пока человек листал выше».
      longStream = stream;
      res.on("close", () => {
        if (longStream === stream) longStream = null;
      });
      return;
    }
    // Хвост S4a: поток ОТКРЫТ и МОЛЧИТ — экран остаётся в «подключаюсь» (F-07, одна строка).
    if (id === "session-silent") return;
    // S3c: разметка реплики агента и её границы — своя сессия, чтобы не мешать остальным проверкам.
    if (id === "session-markdown") {
      stream.send("snapshot", {
        header: { id, createdAt: T, title: "Разметка ленты" },
        cursor: 6,
        hasMore: false,
        records: [
          { type: "user", seq: 1, time: T, text: "**что это** — мои слова, не разметка" },
          { type: "assistant", seq: 2, time: T, text: MD_RICH },
        ],
        // Черновик — СЫРОЙ текст (D1 §2): разметка появляется, когда реплика стала записью.
        assistantDraft: {
          attemptId: "attempt-md",
          turn: 1,
          step: 0,
          startedAfterSeq: 2,
          text: "**черновик** ещё пишется",
        },
      });
      return;
    }
    if (id === "session-error") {
      stream.send("error", { code: "no-session", message: words.PhoneWebSessionMissing });
      res.end();
      return;
    }
    if (id === "session-toomany") {
      stream.send("error", { code: "too-many", message: words.PhoneWebTooManyScreens });
      res.end();
      return;
    }
    if (id === "session-empty") {
      stream.send("snapshot", {
        header: { id, createdAt: T, title: "Пустая сессия" },
        cursor: 0,
        hasMore: false,
        records: [],
        assistantDraft: null,
      });
      return;
    }
    if (id === "session-idle") {
      // Ход кончился: стоп не нужен, полоса в базовом виде (для бюджета высоты).
      stream.send("snapshot", {
        header: { id, createdAt: T, title: "Спокойная сессия" },
        cursor: 8,
        hasMore: false,
        records: [
          ...FEED,
          { type: "turn-end", seq: 7, time: T, turn: 1, ok: true, reason: "completed" },
        ],
        assistantDraft: null,
      });
      return;
    }
    if (id === "session-created-1") {
      stream.send("snapshot", {
        header: { id, createdAt: T, title: "Созданная сессия" },
        cursor: 0,
        hasMore: false,
        records: [],
        assistantDraft: null,
      });
      return;
    }

    if (id === "session-empty-assistant") {
      // DEF-093: старая/чужая калитка присылает ПУСТОЙ ответ агента — пузыря быть не должно,
      // а непустой рядом виден. Отдельная сессия: состояние не протекает в другие проверки.
      stream.send("snapshot", {
        header: { id, createdAt: T, title: "Пустые ответы агента" },
        cursor: 6,
        hasMore: false,
        records: FEED,
        assistantDraft: null,
      });
      stream.later(300, () => {
        stream.send("event", { record: { type: "assistant", seq: 7, time: T, text: "" } });
        stream.send("event", { record: { type: "assistant", seq: 9, time: T, text: "   " } });
        stream.send("event", {
          record: { type: "assistant", seq: 10, time: T, text: "Непустой ответ агента." },
        });
        stream.send("event", {
          record: { type: "turn-end", seq: 11, time: T, turn: 2, ok: true, reason: "completed" },
        });
      });
      return;
    }
    // D1 §4.2: страница КОРОЧЕ ОКНА — прокрутки нет, старые добираются САМИ, кадр за кадром.
    if (id === "session-short") {
      stream.send("snapshot", {
        header: { id, createdAt: T, title: "Короткая сессия" },
        cursor: 41,
        hasMore: true,
        records: [{ type: "assistant", seq: 41, time: T, text: "Короткая лента: одна запись." }],
        assistantDraft: null,
      });
      return;
    }
    if (id.startsWith("session-many")) {
      stream.send("snapshot", {
        header: { id, createdAt: T, title: "Длинная сессия" },
        cursor: 40,
        hasMore: true,
        records: FEED.map((record) => ({ ...record, seq: record.seq + 40 })),
        assistantDraft: null,
      });
      return;
    }
    if (id === "session-retry") {
      // S4a: первый заход — ПОСТОЯННЫЙ отказ (автоповтора нет), второй — живой поток.
      // Так видно, что «Переподключиться» действительно зовёт повтор, а не просто стоит.
      liveSeq += 1;
      if (liveSeq === 1) {
        stream.send("error", { code: "too-many", message: words.PhoneWebTooManyScreens });
        res.end();
        return;
      }
      stream.send("snapshot", {
        header: { id, createdAt: T, title: "Починённая связь" },
        cursor: 0,
        hasMore: false,
        records: [],
        assistantDraft: null,
      });
      return;
    }
    if (id === "session-lost") {
      // S4a/D2 F-04: НЕ финальный обрыв (код `lost`, автоповтор есть) — на экране должна быть
      // ровно одна строка состояния, без одновременного «Подключаюсь…».
      stream.send("error", { code: "lost", message: "" });
      return;
    }
    if (id === "session-lost-many") {
      // То же, но с ДЛИННОЙ лентой: так проверяется, что «Переподключиться» живёт в липкой шапке
      // и видна человеку, который долистал до низа, а не только в конце страницы.
      // Снимок отдаётся ОДИН раз: на повторных заходах только обрыв, иначе статус мигал бы
      // «живой ↔ обрыв» и кнопка исчезала бы из проверки.
      lostSeq += 1;
      if (lostSeq === 1) {
        const many = Array.from({ length: 4 }).flatMap((_, page) =>
          FEED.map((record) => ({ ...record, seq: record.seq + page * 10 })),
        );
        stream.send("snapshot", {
          header: { id, createdAt: T, title: "Оборванная связь" },
          cursor: 46,
          hasMore: false,
          records: many,
          assistantDraft: null,
        });
        stream.later(200, () => stream.send("error", { code: "lost", message: "" }));
        return;
      }
      stream.send("error", { code: "lost", message: "" });
      return;
    }

    // Живой ход: снимок → черновик (start/text) → готовый ответ (event) → конец хода.
    stream.send("snapshot", {
      header: { id, createdAt: T, title: "Починить калитку" },
      cursor: 6,
      hasMore: false,
      records: FEED,
      assistantDraft: null,
    });

    // S4a: очередь приезжает кадром `queue` ИЗ ЭТОГО ЖЕ потока — форма та же, что у D4.
    // Кадр при открытии + (по сценарию `queueFrame: "add"`) ещё один через `queueFrameDelay` мс
    // (по умолчанию 2400): проверка видит, что состав вырос БЕЗ нажатия и без опроса.
    // ⚠️ Кадр — производное от ТОГО ЖЕ ответа двери D4: иначе стенд соврал бы про сценарии
    // no-live/unreadable/busy/many (и проверки ловили бы не тот вид). Числа берём из ответа.
    const sendQueue = (extra) => {
      // R8b: кадр собирается из ТЕКУЩЕГО состава очереди стенда (ответ D4 на ЭТОТ миг) плюс
      // `extra`, но в саму очередь НИЧЕГО не пишется: иначе живой кадр оставлял бы пункт в стенде
      // и счёт протекал бы в следующие проверки (ловилось `s4a`: ожидали 2 пункта, получали 3).
      const answer = queueBody(lang);
      const base = answer.status === 200 && answer.body.state === "ok" ? answer.body : null;
      if (base === null) return;
      const items = extra === null ? base.items : [...base.items, extra];
      stream.send("queue", {
        ok: true,
        state: "ok",
        sessionId: id,
        total: Math.max(Number(base.total) || 0, items.length),
        shown: items.length,
        hasMore: base.hasMore === true || extra !== null,
        items,
      });
    };
    sendQueue(null);
    if (scenario.queueFrame === "add") {
      // Задержка настраивается (`queueFrameDelay`): проверке нужно успеть убрать всё до кадра.
      const when = Number(scenario.queueFrameDelay) > 0 ? Number(scenario.queueFrameDelay) : 2400;
      stream.later(when, () =>
        sendQueue({
          id: "item-live",
          text: "Пункт из живого кадра.",
          chars: 22,
          placement: "queued",
        }),
      );
    }
    if (scenario.steady === true) return; // ход «идёт» без turn-end — для проверок стопа
    stream.later(700, () => {
      stream.send("assistant", { phase: "start", attemptId: "attempt-1", index: 0 });
      stream.send("assistant", {
        phase: "text",
        attemptId: "attempt-1",
        index: 0,
        text: "Сейчас проверю ",
      });
      stream.send("assistant", { phase: "text", attemptId: "attempt-1", index: 1, text: "логи." });
    });
    stream.later(1800, () => {
      stream.send("event", {
        record: { type: "assistant", seq: 7, time: T, text: "Сейчас проверю логи." },
      });
      stream.send("event", {
        record: { type: "turn-end", seq: 8, time: T, turn: 2, ok: true, reason: "completed" },
      });
      // W14 (DEF-150): ход корня кончился — настоящая калитка в этот же миг перестаёт помечать
      // строку как «работает» и шлёт подсказку списка. Стенд делает то же самое: иначе шапка
      // честно держала бы «Работает» вечно (строка осталась бы снимком на момент открытия).
      scenario.liveRunning = false;
      for (const live of listLiveStreams) live.send("stale", {});
    });
    return;
  }

  // ── S3: пять дверей (формы ответов — контракт §2.3, §3.3, §4.3, §5.3, §6.3) ──────────────

  // W5: числа живого списка — «одно чтение на порог», «поток закрыт после ухода».
  if (url.pathname === "/__mock/w5" && req.method === "GET") {
    json(res, 200, {
      ok: true,
      listCalls,
      listLiveOpens,
      listLiveCloses,
      listLiveOpen: listLiveStreams.size,
    });
    return;
  }

  if (url.pathname === "/__mock/s3" && req.method === "GET") {
    json(res, 200, {
      ok: true,
      promptCalls,
      newCalls,
      stopCalls,
      lastPrompt,
      lastNew,
      lastQueueSession,
      queue: queueItems,
      listCalls,
      queueCalls,
      pageCalls,
      answerCalls,
    });
    return;
  }

  if (url.pathname === "/__mock/s3-reset" && req.method === "POST") {
    promptCalls = 0;
    newCalls = 0;
    stopCalls = 0;
    lastPrompt = null;
    lastNew = null;
    answerCalls = 0;
    // W10: тело последнего `POST /answer` — тоже состояние стенда: без сброса «„Далее“ не
    // отправляла» проверялось бы против тела СОСЕДНЕГО теста.
    lastAnswer = null;
    // W12: счётчик старших страниц — тоже состояние стенда. Без сброса проверки догруза
    // зависели бы от порядка файлов: страниц всего две, и их «разбирали» предыдущие тесты.
    olderPage = 0;
    queueItems = [
      {
        id: "item-steering",
        text: "Сначала посмотри журнал сборки.",
        chars: 31,
        placement: "steering",
      },
      {
        id: "item-queued",
        text: "Потом почини два упавших теста.",
        chars: 32,
        placement: "queued",
      },
    ];
    json(res, 200, { ok: true });
    return;
  }

  if (url.pathname === "/api/phone/prompt" && req.method === "POST") {
    readBody(req, (raw) => {
      promptCalls += 1;
      try {
        lastPrompt = JSON.parse(raw);
      } catch {
        lastPrompt = { raw };
      }
      const reply = () => {
        if (scenario.task === "busy") {
          json(res, 403, { ok: false, message: words.PhoneTaskBusy });
          return;
        }
        if (scenario.task === "not-taken") {
          json(res, 502, {
            ok: false,
            message: String(words.PhoneTaskNotTakenFormat).replace(
              "{0}",
              words.PhoneTaskReasonNoConnection,
            ),
          });
          return;
        }
        const text = typeof lastPrompt?.text === "string" ? lastPrompt.text : "";
        queueItems = [
          ...queueItems,
          { id: `item-accepted-${promptCalls}`, text, chars: text.length, placement: "steering" },
        ];
        json(res, 200, {
          ok: true,
          accepted: true,
          sessionId: lastPrompt?.sessionId ?? "session-live",
          requestId: lastPrompt?.requestId ?? "",
          message: words.PhoneTaskAccepted,
        });
      };
      // `slow` — дверь думает 1,2 с: проверка двойного нажатия видит, что второй POST не ушёл.
      if (scenario.task === "slow") setTimeout(reply, 1_200);
      else reply();
    });
    return;
  }

  if (url.pathname === "/api/phone/new" && req.method === "POST") {
    readBody(req, (raw) => {
      newCalls += 1;
      try {
        lastNew = JSON.parse(raw);
      } catch {
        lastNew = { raw };
      }
      if (scenario.newtask === "fail") {
        json(res, 502, {
          ok: false,
          message: String(words.PhoneTaskNewFailedFormat).replace(
            "{0}",
            words.PhoneTaskReasonNoConnection,
          ),
        });
        return;
      }
      json(res, 200, {
        ok: true,
        accepted: true,
        sessionId: "session-created-1",
        requestId: lastNew?.requestId ?? "",
        repeated: false,
        message: words.PhoneTaskNewAccepted,
      });
    });
    return;
  }

  if (url.pathname === "/api/phone/stop" && req.method === "POST") {
    readBody(req, () => {
      stopCalls += 1;
      if (scenario.stop === "fail") {
        json(res, 502, {
          ok: false,
          message: String(words.PhoneStopFailedFormat).replace(
            "{0}",
            words.PhoneTaskReasonNoConnection,
          ),
        });
        return;
      }
      if (scenario.stop === "idle") {
        json(res, 200, { ok: true, stopped: false, state: "idle", message: words.PhoneStopIdle });
        return;
      }
      json(res, 200, { ok: true, stopped: true, state: "stopped", message: words.PhoneStopDone });
    });
    return;
  }

  if (url.pathname === "/api/phone/queue" && req.method === "GET") {
    if (scenario.queue === "slow") {
      // Чтение «идёт» 900 мс: проверка видит, что экран не теряет состав в это время.
      setTimeout(() => {
        queueCalls += 1;
        const answer = queueBody(lang);
        json(res, answer.status, answer.body);
      }, 900);
      return;
    }
    queueCalls += 1;
    lastQueueSession = url.searchParams.get("session") ?? "";
    const { status, body } = queueBody(lang);
    json(res, status, body);
    return;
  }

  if (url.pathname === "/api/phone/queue/remove" && req.method === "POST") {
    readBody(req, (raw) => {
      let parsed = null;
      try {
        parsed = JSON.parse(raw);
      } catch {
        parsed = null;
      }
      const itemId = parsed?.itemId ?? "";
      if (scenario.remove === "gone") {
        queueItems = queueItems.filter((item) => item.id !== itemId);
        json(res, 200, { ok: true, state: "gone", message: words.PhoneQueueAlreadyGone });
        return;
      }
      if (scenario.remove === "fail") {
        json(res, 502, {
          ok: false,
          message: String(words.PhoneQueueRemoveFailedFormat).replace(
            "{0}",
            words.PhoneTaskReasonNoConnection,
          ),
        });
        return;
      }
      if (!queueItems.some((item) => item.id === itemId)) {
        json(res, 200, { ok: true, state: "gone", message: words.PhoneQueueAlreadyGone });
        return;
      }
      queueItems = queueItems.filter((item) => item.id !== itemId);
      json(res, 200, { ok: true, state: "removed", message: words.PhoneQueueRemoved });
    });
    return;
  }

  json(res, 404, { ok: false, message: "not found" });
});

// Занятый порт — громкий отказ: иначе Playwright подхватил бы ЧУЖОЙ стенд и проверки соврали бы
// (DEF-096, находка приёмки t21). Код возврата ≠ 0 виден и в конфиге, и в ручном запуске.
server.on("error", (error) => {
  if (error.code === "EADDRINUSE") {
    process.stderr.write(
      `стенд: порт 127.0.0.1:${PORT} уже занят чужим процессом — остановите его или задайте DSH_E2E_PORT\n`,
    );
  } else {
    process.stderr.write(`стенд не поднялся: ${String(error)}\n`);
  }
  process.exit(1);
});

server.listen(PORT, "127.0.0.1", () => {
  process.stdout.write(`mock gate on http://127.0.0.1:${PORT}\n`);
});
