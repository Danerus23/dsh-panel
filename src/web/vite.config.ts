// Сборка телефонного пульта: Vite 8 (Rolldown) → `src/DshPanel/WebAssets` (эта папка коммитится).
//
// Особенности, найденные пробой 29.10.2026 и закреплённые здесь:
//   * Semi 2.104.0 не отдаёт `dist/css` через карту `exports` — стили берутся относительным
//     путём (`src/styles/semi.css`), поэтому здесь ничего обходить не нужно;
//   * стили и скрипты — только свои файлы: ни шрифтов, ни картинок с чужих адресов;
//   * HTML собирает КАЛИТКА (у неё boot, язык и слова), поэтому сборка кладёт рядом `shell.html`
//     с подстановками и `assets.json` — карту имён, по которой калитка собирает страницу.

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { defineConfig, type Plugin } from "vitest/config";
import react from "@vitejs/plugin-react";

const OUT_DIR = resolve(import.meta.dirname, "../DshPanel/WebAssets");

const SHELL = `<!doctype html>
<html lang="__LANG__">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover" />
    <meta name="color-scheme" content="light dark" />
    <title>__TITLE__</title>
    <link rel="stylesheet" href="__CSS__" />
  </head>
  <body>
    <div id="root" data-boot="__BOOT__"><p>__LOADING__</p><p>__BOOT_FALLBACK__</p></div>
    <noscript>
      <h1>__NOSCRIPT_TITLE__</h1>
      <p>__NOSCRIPT_TEXT__</p>
    </noscript>
    <script type="module" src="__JS__"></script>
  </body>
</html>
`;

/**
 * Кладёт в сборку оболочку с подстановками и карту имён. Калитка подставляет язык, заголовок,
 * `boot` (экранированный), строку загрузки и слова `<noscript>` — и отдаёт готовый HTML.
 */
function shellPlugin(): Plugin {
  let outDir = OUT_DIR;

  return {
    name: "dsh-phone-shell",

    configResolved(config) {
      outDir = resolve(config.root, config.build.outDir);
    },
    generateBundle(_options, bundle) {
      let js = "";
      let css = "";
      for (const [fileName, item] of Object.entries(bundle)) {
        if (item.type === "chunk" && item.isEntry) js = `/${fileName}`;
        if (item.type === "asset" && fileName.endsWith(".css")) css = `/${fileName}`;
      }
      if (js === "") this.error("entry chunk not found — shell would reference nothing");
      this.emitFile({ type: "asset", fileName: "shell.html", source: SHELL });
      this.emitFile({
        type: "asset",
        fileName: "assets.json",
        source: `${JSON.stringify(
          {
            shell: "shell.html",
            js,
            css,
            placeholders: [
              "__LANG__",
              "__TITLE__",
              "__BOOT__",
              "__LOADING__",
              "__BOOT_FALLBACK__",
              "__NOSCRIPT_TITLE__",
              "__NOSCRIPT_TEXT__",
              "__JS__",
              "__CSS__",
            ],
          },
          null,
          2,
        )}\n`,
      });
    },

    // ⚠️ `assets.json` и `manifest.json` — ЕДИНСТВЕННЫЕ .json в сборке, и сторож кодировки
    // требует у .json CRLF (правило 12.10.2026: файл читают cmd.exe/PowerShell 5.1). Vite пишет
    // LF, поэтому здесь концы строк приводятся к CRLF ПОСЛЕ сборки — и `check:assets` сравнивает
    // уже одинаковые байты (свежая сборка проходит тот же шаг).
    closeBundle() {
      for (const name of ["assets.json", "manifest.json"]) {
        const file = resolve(outDir, name);
        if (!existsSync(file)) continue;
        writeFileSync(file, readFileSync(file, "utf8").replace(/\r?\n/g, "\r\n"));
      }
    },
  };
}

export default defineConfig({
  base: "/",
  plugins: [react(), shellPlugin()],
  build: {
    outDir: OUT_DIR,
    emptyOutDir: true,
    assetsDir: "assets",
    manifest: "manifest.json",
    sourcemap: false,
    target: "es2022",
    rollupOptions: {
      input: resolve(import.meta.dirname, "src/main.tsx"),
      output: {
        entryFileNames: "assets/[name]-[hash].js",
        chunkFileNames: "assets/[name]-[hash].js",
        assetFileNames: "assets/[name]-[hash][extname]",
      },
    },
  },
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.{ts,tsx}"],
    alias: {
      // Semi тянет lottie-web, которому в jsdom нужен canvas.
      "lottie-web": resolve(import.meta.dirname, "src/test/lottie-stub.ts"),
    },
  },
});
