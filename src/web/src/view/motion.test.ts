// Правило движения (D4): пути браузера, числа и самоограничение API — чистыми функциями.
// Живой переход здесь не проверяется (jsdom его не умеет) — это делает e2e.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  API_BUDGET_MS,
  FADE_MS,
  FALLBACK_SHIFT_PX,
  MOTION_EASE,
  SCREEN_MS,
  SCREEN_SHIFT_PX,
  apiBudgetSpent,
  budgetSpent,
  markEnteringScreen,
  motionPath,
  navigate,
  noteTransition,
  prefersReducedMotion,
  resetApiBudget,
} from "./motion";

describe("движение: пути и числа", () => {
  it("одна кривая и два числа — D4 §2", () => {
    expect(SCREEN_MS).toBe(200);
    expect(FADE_MS).toBe(150);
    expect(SCREEN_SHIFT_PX).toBe(16);
    expect(FALLBACK_SHIFT_PX).toBe(8);
    expect(API_BUDGET_MS).toBe(400);
    expect(MOTION_EASE).toBe("cubic-bezier(0.215, 0.61, 0.355, 1)");
  });

  it("порядок путей: reduce → API → запасной", () => {
    expect(motionPath(true, true, false)).toBe("reduce");
    expect(motionPath(true, false, false)).toBe("reduce");
    expect(motionPath(false, true, false)).toBe("api");
    expect(motionPath(false, false, false)).toBe("fallback");
    // Потолок исчерпан — API больше не берём, даже когда он есть.
    expect(motionPath(false, true, true)).toBe("fallback");
  });

  it("потолок 400 мс: ровно 400 — ещё можно, 401 — уже нет", () => {
    expect(budgetSpent(200)).toBe(false);
    expect(budgetSpent(400)).toBe(false);
    expect(budgetSpent(401)).toBe(true);
  });

  it("замер длиннее потолка выключает API в этой сессии навсегда", () => {
    resetApiBudget();
    noteTransition(300);
    expect(apiBudgetSpent()).toBe(false);
    noteTransition(450);
    expect(apiBudgetSpent()).toBe(true);
    noteTransition(150);
    expect(apiBudgetSpent(), "короткий переход не возвращает API").toBe(true);
    resetApiBudget();
    expect(apiBudgetSpent()).toBe(false);
  });
});

describe("движение: вход экрана", () => {
  /** jsdom не знает `matchMedia`: подменяем его явно — это и есть вход в наше правило. */
  function setReduced(matches: boolean): void {
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      value: vi.fn(() => ({
        matches,
        addEventListener: () => undefined,
        removeEventListener: () => undefined,
      })),
    });
  }

  beforeEach(() => {
    setReduced(false);
    document.body.innerHTML = "<main></main>";
  });

  afterEach(() => {
    document.body.innerHTML = "";
    vi.restoreAllMocks();
  });

  it("запасной путь метит `main.dp-enter` и снимает класс по концу анимации", () => {
    const main = document.querySelector("main")!;
    markEnteringScreen();
    expect(main.classList.contains("dp-enter")).toBe(true);

    main.dispatchEvent(new Event("animationend"));
    expect(main.classList.contains("dp-enter")).toBe(false);
  });

  it("navigate без API (jsdom) выполняет обновление и ставит запасной класс", () => {
    const update = vi.fn();
    navigate(update, "forward");
    expect(update).toHaveBeenCalledTimes(1);
    expect(document.querySelector("main")!.classList.contains("dp-enter")).toBe(true);
    expect(document.documentElement.dataset.nav).toBe("forward");
  });

  it("reduce: обновление есть, а класса входа нет", () => {
    setReduced(true);
    expect(prefersReducedMotion()).toBe(true);

    const update = vi.fn();
    navigate(update, "back");
    expect(update).toHaveBeenCalledTimes(1);
    expect(document.querySelector("main")!.classList.contains("dp-enter")).toBe(false);
  });

  it("ошибка API не мешает навигации: экран меняется запасным путём", () => {
    Object.defineProperty(document, "startViewTransition", {
      configurable: true,
      value: () => {
        throw new Error("переход не начался");
      },
    });

    const update = vi.fn();
    navigate(update, "forward");

    expect(update, "навигация выполнена, несмотря на отказ API").toHaveBeenCalledTimes(1);
    expect(document.querySelector("main")!.classList.contains("dp-enter")).toBe(true);
  });
});
