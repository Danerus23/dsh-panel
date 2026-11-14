import { defineConfig } from "@playwright/test";
import { BASE_URL, E2E_PORT } from "./e2e/base";

// Браузер НЕ скачиваем: Playwright берёт уже установленный Edge каналом `msedge`.
// Стенд — своя подставная калитка (`e2e/server.mjs`): живой движок и данные владельца для
// проверки экрана не нужны и трогать их нельзя.
//
// DEF-096 (находка приёмки t21): `reuseExistingServer: true` ПОДХВАТЫВАЛ чужой стенд, уже занявший
// порт (другое дерево, другой билд) — и мутации выглядели зелёными, хотя проверялся чужой код.
// Теперь переиспользования нет: прогон поднимает СВОЙ стенд, а если порт занят — падает громко
// (Playwright: «http://127.0.0.1:43217 is already used», код возврата ≠ 0), а не подхватывает чужое.
export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  workers: 1,
  reporter: [["list"]],
  timeout: 30_000,
  use: {
    channel: "msedge",
    headless: true,
    viewport: { width: 360, height: 800 },
    baseURL: BASE_URL,
    trace: "off",
  },
  webServer: {
    command: "node e2e/server.mjs",
    url: `${BASE_URL}/health`,
    env: { PORT: String(E2E_PORT) },
    reuseExistingServer: false,
    timeout: 30_000,
  },
});
