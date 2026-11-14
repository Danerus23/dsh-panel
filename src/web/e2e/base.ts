// Адрес стенда — ОДНО место правды для конфига и для проверок.
//
// DEF-097 (находка приёмки t21): в `perf.spec.ts` и `s3.spec.ts` адрес `http://127.0.0.1:43217`
// был зашит строкой. Сменить порт стенда (или гонять два дерева рядом) было нельзя: проверки
// продолжали смотреть на чужой адрес и зеленели/краснели не про то. Здесь адрес собирается из
// `DSH_E2E_PORT`/`DSH_E2E_BASE_URL` — конфиг берёт тот же.

import type { Page } from "@playwright/test";

/** Порт стенда по умолчанию тот же, что был; переопределяется `DSH_E2E_PORT`. */
export const E2E_PORT = Number(process.env.DSH_E2E_PORT ?? 43217);

/** Полный адрес стенда: `DSH_E2E_BASE_URL` сильнее порта. */
export const BASE_URL = process.env.DSH_E2E_BASE_URL ?? `http://127.0.0.1:${E2E_PORT}`;

/** Свой (а не зашитый) origin страницы: им проверяются «чужие запросы» и «внешние адреса». */
export function ownOrigin(page: Page): string {
  return new URL(page.url()).origin;
}
