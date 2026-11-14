// Кадры для отчёта роли: снимает собранную страницу со стенда `e2e/server.mjs`.
// НЕ проверка — доказательство «как это выглядит» (что кадром НЕ доказывается — в отчёте).
//
//   node scripts/shots.mjs [папка]
//
// По умолчанию кадры идут в `_team/250/shots` (вне продукта).
//
// Ремонт t14 (F-3): черновик сеется ЧЕРЕЗ КОНТЕКСТ (`context.addInitScript`) — иначе к моменту
// снимка localStorage пуст и кадр «черновик восстановлен» показывает пустое поле. И у каждого
// кадра свой сценарий стенда, иначе два файла выходят одним и тем же (F-4).

import { spawn } from "node:child_process";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { chromium } from "@playwright/test";

const HERE = resolve(import.meta.dirname, "..");
const OUT = resolve(process.argv[2] ?? resolve(HERE, "../../../_team/250/shots"));
const PORT = 43218;
const BASE = `http://127.0.0.1:${PORT}`;

mkdirSync(OUT, { recursive: true });

const server = spawn(process.execPath, [resolve(HERE, "e2e/server.mjs")], {
  cwd: HERE,
  env: { ...process.env, PORT: String(PORT) },
  stdio: "ignore",
});

async function waitForGate() {
  for (let i = 0; i < 60; i += 1) {
    try {
      const response = await fetch(`${BASE}/health`);
      if (response.ok) return;
    } catch {
      // стенд ещё поднимается
    }
    await new Promise((done) => setTimeout(done, 100));
  }
  throw new Error("стенд не поднялся");
}

async function scenario(patch) {
  await fetch(`${BASE}/__mock/scenario`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(patch),
  });
}

/** Тело черновика — то же, что пишет `useDraft`: `{v,text,requestId,savedAt}` (контракт §8.2). */
const DRAFT_VERSION = 1;
const DRAFT_REQUEST_ID = "11111111-1111-4111-8111-111111111111";

const browser = await chromium.launch({ channel: "msedge" });
try {
  await waitForGate();

  const shoot = async (name, options) => {
    const context = await browser.newContext({
      viewport: options.viewport,
      colorScheme: options.scheme,
    });
    // Сев ДО первой навигации: иначе поле окажется пустым (F-3).
    if (options.draft) {
      await context.addInitScript(
        ([key, text, version, requestId]) => {
          window.localStorage.setItem(
            key,
            JSON.stringify({ v: version, text, requestId, savedAt: Date.now() }),
          );
        },
        [options.draft.key, options.draft.text, DRAFT_VERSION, DRAFT_REQUEST_ID],
      );
    }
    const page = await context.newPage();
    for (const patch of options.scenario ?? []) await scenario(patch);
    await page.goto(`${BASE}${options.path}`);
    await page.waitForTimeout(options.wait ?? 1200);
    if (options.after) await options.after(page);
    await page.screenshot({
      path: resolve(OUT, `${name}.png`),
      fullPage: options.fullPage ?? true,
    });
    await context.close();
    process.stdout.write(`${name}.png\n`);
  };

  // ── S1/S2 ──────────────────────────────────────────────────────────────────────────────────
  await shoot("list-360-light", {
    path: "/",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    scenario: [{ authed: true, list: "ok" }],
  });
  await shoot("list-360-dark", {
    path: "/",
    viewport: { width: 360, height: 800 },
    scheme: "dark",
    scenario: [{ authed: true, list: "ok" }],
  });
  await shoot("list-320-light", {
    path: "/",
    viewport: { width: 320, height: 800 },
    scheme: "light",
  });
  await shoot("session-360-light", {
    path: "/?session=session-live",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 2600,
  });
  await shoot("session-360-dark", {
    path: "/?session=session-live",
    viewport: { width: 360, height: 800 },
    scheme: "dark",
    wait: 2600,
  });
  await shoot("login-360-light", {
    path: "/",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    scenario: [{ authed: false }],
  });
  await shoot("list-error-360", {
    path: "/",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    scenario: [{ authed: true, list: "error" }],
  });
  await shoot("session-empty-360", {
    path: "/?session=session-empty",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1500,
  });
  await shoot("question-single-360-light", {
    path: "/?session=session-live",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1600,
    scenario: [{ authed: true, question: "single", questionSession: "session-live" }],
  });
  await shoot("question-single-360-dark", {
    path: "/?session=session-live",
    viewport: { width: 360, height: 800 },
    scheme: "dark",
    wait: 1600,
  });
  await shoot("list-question-fallback-360", {
    path: "/",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1600,
    scenario: [{ authed: true, list: "ok", question: "fallback" }],
  });

  // ── S3: ПОЛОСА ЗАДАЧИ — все состояния (ремонт t14: спокойно/набор/ход/отказ/принято) ────────
  const idle = {
    authed: true,
    question: "none",
    task: "ok",
    stop: "stopped",
    queue: "ok",
    steady: true,
  };

  await shoot("s3-composer-360-light", {
    path: "/?session=session-idle",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1000,
    scenario: [idle],
  });
  await shoot("s3-composer-360-dark", {
    path: "/?session=session-idle",
    viewport: { width: 360, height: 800 },
    scheme: "dark",
    wait: 1000,
  });
  await shoot("s3-composer-320-light", {
    path: "/?session=session-idle",
    viewport: { width: 320, height: 720 },
    scheme: "light",
    wait: 1000,
  });
  await shoot("s3-composer-typing-360", {
    path: "/?session=session-idle",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1000,
    after: async (page) => {
      await page.locator(".dp-composer textarea").fill("Почини два упавших теста и пришли отчёт");
      await page.waitForTimeout(300);
    },
  });
  await shoot("s3-composer-stop-360", {
    path: "/?session=session-live",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1200,
    scenario: [idle],
  });
  await shoot("s3-composer-stop-320", {
    path: "/?session=session-live",
    viewport: { width: 320, height: 720 },
    scheme: "light",
    wait: 1200,
  });
  await shoot("s3-composer-error-360", {
    path: "/?session=session-live",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1200,
    scenario: [{ ...idle, task: "not-taken" }],
    after: async (page) => {
      await page.locator(".dp-composer textarea").fill("Почини два упавших теста");
      await page.locator(".dp-composer .dp-primary").click();
      await page.waitForTimeout(700);
    },
  });
  await shoot("s3-composer-accepted-360", {
    path: "/?session=session-live",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1200,
    scenario: [idle],
    after: async (page) => {
      await page.locator(".dp-composer textarea").fill("Почини два упавших теста");
      await page.locator(".dp-composer .dp-primary").click();
      await page.waitForTimeout(700);
    },
  });

  // ── S3: ОЧЕРЕДЬ — без идущего хода, подтверждение, частично, пусто, не открыта ─────────────
  await shoot("s3-queue-360", {
    path: "/?session=session-idle",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1000,
    scenario: [idle],
  });
  await shoot("s3-queue-confirm-360", {
    path: "/?session=session-idle",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1000,
    after: async (page) => {
      await page.locator(".dp-queue-item").first().getByRole("button", { name: "Убрать" }).click();
      await page.waitForTimeout(300);
    },
  });
  await shoot("s3-queue-more-360", {
    path: "/?session=session-idle",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1000,
    scenario: [{ ...idle, queue: "many" }],
  });
  await shoot("s3-queue-empty-360", {
    path: "/?session=session-idle",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1000,
    scenario: [{ ...idle, queue: "empty" }],
  });
  await shoot("s3-queue-nolive-360", {
    path: "/?session=session-idle",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1000,
    scenario: [{ ...idle, queue: "no-live" }],
  });

  // ── S3: черновик, новая задача, живой конец над полосой ──────────────────────────────────
  await shoot("s3-draft-restored-360", {
    path: "/?session=session-idle",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1200,
    scenario: [idle],
    draft: { key: "dsh.phone.draft.session-idle", text: "Проверь, что черновик жив" },
  });
  await shoot("s3-draft-restored-320", {
    path: "/?session=session-idle",
    viewport: { width: 320, height: 720 },
    scheme: "light",
    wait: 1200,
    draft: { key: "dsh.phone.draft.session-idle", text: "Проверь, что черновик жив" },
  });
  await shoot("s3-new-task-360", {
    path: "/?new=1",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1000,
    scenario: [idle],
  });
  await shoot("s3-live-end-360", {
    path: "/?session=session-many",
    viewport: { width: 360, height: 800 },
    scheme: "light",
    wait: 1800,
    fullPage: false,
    scenario: [idle],
  });
} finally {
  await browser.close();
  server.kill();
}
