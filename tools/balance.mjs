// balance.mjs — запасной путь для проверки баланса: печатает JSON, который
// разбирает DshTray. Нужен потому, что нативный TLS этой машины (Windows
// Schannel) не отдаёт учётные данные (SEC_E_NO_CREDENTIALS), а Node
// использует собственный OpenSSL и работает.
//
// Ключ читается из ~/.dsh/.credentials.yaml и никуда не печатается.
// Запуск: node balance.mjs

import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import https from "node:https";

const STORE = join(homedir(), ".dsh", ".credentials.yaml");
const TIMEOUT_MS = 20000;

function fail(message) {
  process.stdout.write(JSON.stringify({ ok: false, error: message }));
  process.exit(0);
}

function readKey() {
  let raw;
  try {
    raw = readFileSync(STORE, "utf8");
  } catch (error) {
    fail(`не удалось прочитать хранилище ${STORE}: ${error.message}`);
  }
  const match = raw.match(/^\s*DEEPSEEK_API_KEY:\s*(\S+)\s*$/m);
  if (match === null) fail(`DEEPSEEK_API_KEY не найден в ${STORE}`);
  return match[1];
}

const key = readKey();

const request = https.request(
  {
    hostname: "api.deepseek.com",
    path: "/user/balance",
    method: "GET",
    headers: { Authorization: `Bearer ${key}`, Accept: "application/json" },
    timeout: TIMEOUT_MS,
  },
  (response) => {
    let body = "";
    response.setEncoding("utf8");
    response.on("data", (chunk) => {
      body += chunk;
    });
    response.on("end", () => {
      if (response.statusCode !== 200) {
        fail(`HTTP ${response.statusCode}: ${body.slice(0, 300)}`);
        return;
      }
      try {
        process.stdout.write(JSON.stringify({ ok: true, status: response.statusCode, body: JSON.parse(body) }));
      } catch (error) {
        fail(`ответ не является JSON: ${error.message}`);
      }
    });
  },
);

request.on("timeout", () => request.destroy(new Error(`таймаут ${TIMEOUT_MS} мс`)));
request.on("error", (error) => fail(error.message));
request.end();
