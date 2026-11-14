// ДВИЖЕНИЕ МОБИЛЬНОГО СЛОЯ — по правилу D4 (`_team\fixes\d4-vid.md`).
//
// Одна кривая и ДВА числа на весь пульт: смена экрана — 200 мс, проявление/исчезновение
// органа — 150 мс. Три пути браузера и всегда в этом порядке:
//   1) `prefers-reduced-motion: reduce` — мгновенно, API НЕ вызываем;
//   2) `document.startViewTransition` есть — переход через API (экран ОДНОЙ поверхностью,
//      у шапки/зацепа/листа/ленты нет своих `view-transition-name`);
//   3) API нет — запасной CSS: входящий экран проявляется и сдвигается на 8 px, 150 мс.
//
// Чего здесь НЕТ и не будет: ожидания `finished` (фокус и прокрутка встают в том же обновлении
// DOM), движения прокрутки/якоря/зацепа, записей истории (их делает вызывающий код ровно как раньше).
//
// Самоограничение (D4 §5 п. 5): если первый переход измеренно длился больше 400 мс, в этой
// сессии API больше не используем — медленное устройство превращает 200 мс в дёрганый кадр.

import { flushSync } from "react-dom";

/** Кривая Semi для появления слоя (`semi.css:15973`) — одна на весь мобильный слой. */
export const MOTION_EASE = "cubic-bezier(0.215, 0.61, 0.355, 1)";
/** Смена экрана, мс. */
export const SCREEN_MS = 200;
/** Появление/исчезновение органа, мс. */
export const FADE_MS = 150;
/** Сдвиг экрана по иерархии «вперёд/назад», px (API-путь). */
export const SCREEN_SHIFT_PX = 16;
/** Сдвиг входящего экрана в запасном пути, px. */
export const FALLBACK_SHIFT_PX = 8;
/** Потолок: переход длиннее — API в этой сессии больше не используем. */
export const API_BUDGET_MS = 400;

export type NavDirection = "forward" | "back";
export type MotionPath = "reduce" | "api" | "fallback";

type ViewTransitionLike = { finished: Promise<void> };
type DocumentWithTransitions = Document & {
  startViewTransition?: (callback: () => void) => ViewTransitionLike;
};

let apiSpent = false;

/** Путь перехода. `reduce` — всегда первый и единственный ответ на «уменьшить движение». */
export function motionPath(reduce: boolean, hasApi: boolean, spent = apiSpent): MotionPath {
  if (reduce) return "reduce";
  return hasApi && !spent ? "api" : "fallback";
}

/** «Потолок API исчерпан» — чистое правило, отдельно от часов и DOM (его и проверяет юнит). */
export function budgetSpent(measuredMs: number): boolean {
  return measuredMs > API_BUDGET_MS;
}

/** Записать замер перехода: длиннее потолка — API в этой сессии выключается навсегда. */
export function noteTransition(measuredMs: number): void {
  if (budgetSpent(measuredMs)) apiSpent = true;
}

/** Исчерпан ли бюджет (для проверок и для `motionPath`). */
export function apiBudgetSpent(): boolean {
  return apiSpent;
}

/** Сброс памяти — только для тестов: в бою флаг живёт до перезагрузки страницы. */
export function resetApiBudget(): void {
  apiSpent = false;
}

/** Просит ли человек меньше движения. Нет `matchMedia` (старый браузер) — движения не обещаем. */
export function prefersReducedMotion(): boolean {
  if (typeof window === "undefined" || typeof window.matchMedia !== "function") return true;
  return window.matchMedia("(prefers-reduced-motion: reduce)").matches;
}

/**
 * ОТМЕТИТЬ ВХОДЯЩИЙ ЭКРАН для запасного пути: класс `dp-enter` на `main` нового экрана.
 * Ставится ПОСЛЕ синхронного обновления DOM (экран уже новый), снимается по `animationend`.
 */
export function markEnteringScreen(): void {
  const main = document.querySelector("main");
  if (main === null) return;

  main.classList.add("dp-enter");
  main.addEventListener("animationend", () => main.classList.remove("dp-enter"), { once: true });
}

/**
 * СМЕНИТЬ ЭКРАН С ДВИЖЕНИЕМ. `update` — ровно то, что экран делал раньше: `pushState`/
 * `replaceState` и `setRoute` (одна запись истории, как была). Порядок путей — `motionPath`.
 * `finished` НЕ ожидается: навигация уже выполнена в колбэке, фокус и прокрутка встают сразу.
 */
export function navigate(update: () => void, direction: NavDirection): void {
  const root = document.documentElement;
  root.dataset.nav = direction;

  if (prefersReducedMotion()) {
    update();
    return;
  }

  const doc = document as DocumentWithTransitions;
  const path = motionPath(false, typeof doc.startViewTransition === "function");

  if (path === "api") {
    let ran = false;
    const started = performance.now();

    try {
      const transition = doc.startViewTransition!(() => {
        ran = true;
        flushSync(update);
      });

      // Замер — только чтобы решить судьбу API; ошибка перехода навигацию не отменяет.
      transition.finished
        .then(() => noteTransition(performance.now() - started))
        .catch(() => noteTransition(performance.now() - started));

      return;
    } catch {
      // API не смог начать переход: если обновление уже случилось — второй раз не навигируем.
      if (ran) return;
    }
  }

  flushSync(update);
  markEnteringScreen();
}
