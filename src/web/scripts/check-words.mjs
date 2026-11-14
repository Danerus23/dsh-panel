// `check:words` — «живая страница не показывает имена ключей».
//
// Стенд поднимается в режиме `WORDS_FROM_PANEL=1`: слова берутся ТОЛЬКО из словарей панели
// (`src/DshPanel/Localization/*.json`), без фикстуры. Скрипт открывает экраны S1+S2 на трёх
// языках и ищет в тексте страницы имена ключей (`Phone[A-Za-z]+`). Ноль находок + настоящие
// слова на месте = доказательство, что имени ключа человек не увидит.
//
// Это НЕ продукт: подставная калитка (`e2e/server.mjs`) и Edge каналом, как у e2e.

import { spawn } from "node:child_process";
import { resolve } from "node:path";
import { chromium } from "@playwright/test";

const HERE = resolve(import.meta.dirname, "..");
const PORT = Number(process.env.PORT ?? 43231);
const BASE = `http://127.0.0.1:${PORT}`;
const LANGS = ["ru", "en", "zh"];

const WORDS = {
  ru: { sessions: "Сессии", question: "Агент ждёт ответа", required: "Свой ответ" },
  en: { sessions: "Sessions", question: "Waiting", required: "Your answer" },
  zh: { sessions: "会话", question: "等待", required: "自己的回答" },
};

const server = spawn(process.execPath, [resolve(HERE, "e2e/server.mjs")], {
  cwd: HERE,
  env: { ...process.env, PORT: String(PORT), WORDS_FROM_PANEL: "1" },
  stdio: "ignore",
});

async function waitForGate() {
  for (let i = 0; i < 80; i += 1) {
    try {
      const response = await fetch(`${BASE}/health`);
      if (response.ok) return;
    } catch {
      // стенд поднимается
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

const report = { lang: {}, keyNames: [], words: {}, ok: false };

const browser = await chromium.launch({ channel: "msedge" });
try {
  await waitForGate();
  for (const lang of LANGS) {
    const context = await browser.newContext({ viewport: { width: 360, height: 800 } });
    const page = await context.newPage();
    const texts = [];
    for (const step of [
      { question: "none", path: "/" },
      { question: "single", questionSession: "session-live", path: "/?session=session-live" },
      { question: "nooptions", questionSession: "session-live", path: "/?session=session-live" },
      { question: "fallback", path: "/" },
    ]) {
      await scenario({
        authed: true,
        lang,
        list: "ok",
        pair: "ok",
        answer: "ok",
        cancel: "ok",
        ...step,
      });
      await page.goto(`${BASE}${step.path}`);
      await page.waitForTimeout(900);
      texts.push(await page.locator("body").innerText());
    }
    await context.close();

    const joined = texts.join("\n");
    const tokens = [...new Set([...joined.matchAll(/\bPhone[A-Za-z]{3,}\b/g)].map((m) => m[0]))];
    report.lang[lang] = { screens: texts.length, keyNames: tokens.length, tokens };
    report.words[lang] = {
      sessions: joined.includes(WORDS[lang].sessions),
      question: joined.includes(WORDS[lang].question),
      required: joined.includes(WORDS[lang].required),
    };
  }
} finally {
  await browser.close();
  server.kill();
}

report.keyNames = report.lang.ru.tokens;
report.ok =
  LANGS.every((lang) => report.lang[lang].keyNames === 0) &&
  LANGS.every((lang) => report.words[lang].sessions && report.words[lang].question);

console.log(JSON.stringify(report, null, 2));
process.exit(report.ok ? 0 : 1);
