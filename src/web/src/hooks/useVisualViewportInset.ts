// ВИДИМАЯ ОБЛАСТЬ (visualViewport) ДЛЯ ОРГАНОВ, ПРИБИТЫХ К НИЗУ: шар и пара «вверх/вниз».
//
// Находка 5 приёмки 11.11.2026: шар был прибит к LAYOUT-вьюпорту, а адресная строка на телефоне
// меняет видимую область — шар «сдвигался» относительно края экрана. R2 до этого убрал дрожание
// тем, что пересчитывал вьюпорт только при смене ширины; здесь механизм другой (D1 §3.2):
//
//   * величина `--dp-vv-inset` = max(0, innerHeight − (vv.offsetTop + vv.height)) — то же число
//     кладёт `useKeyboardInset` в `--dp-keyboard-inset` для листа; второго «низа» не заводим;
//   * пересчёт ТОЛЬКО по `vv.resize`, повороту и смене ширины окна; `window.scroll` и
//     `visualViewport.scroll` НЕ слушаем — иначе дрожание R2 вернётся тем же путём;
//   * не чаще кадра (`requestAnimationFrame`), и переменная пишется только если число изменилось
//     хотя бы на 1 px (`Math.round`): подпиксельное «дыхание» не двигает органы;
//   * нет `visualViewport` вовсе — переменная = 0px, поведение прежнее (layout-вьюпорт).

import { useEffect } from "react";

/** Имя переменной — одно на все органы, которые держатся за видимый низ (D1 §3.2). */
export const VV_INSET_VAR = "--dp-vv-inset";
/**
 * Признак «клавиатура поднята» на корне документа (R6). Порог, а не «больше нуля»: адресная строка
 * тоже даёт врезку, но маленькую (десятки px), а клавиатура — сотни. По этому признаку карточка
 * вопроса уступает шапку телу, чтобы поле своего ответа было видно ЦЕЛИКОМ (см. `app.css`).
 */
export const KEYBOARD_ATTR = "data-keyboard";
const KEYBOARD_MIN_PX = 120;

/** Величина «низа под видимой областью» в layout-пикселях; 0 без `visualViewport`. */
export function visualViewportInset(): number {
  const viewport = window.visualViewport;
  if (viewport === null || viewport === undefined) return 0;
  return Math.max(0, Math.round(window.innerHeight - (viewport.offsetTop + viewport.height)));
}

/**
 * Держит `--dp-vv-inset` на корне документа, пока экран смонтирован; за собой убирает.
 * Вызывать на экране, где живут пара и шар (`SessionScreen`).
 */
export function useVisualViewportInset(): void {
  useEffect(() => {
    const viewport = window.visualViewport;
    const root = document.documentElement;
    let frame = 0;

    const apply = () => {
      frame = 0;
      const inset = visualViewportInset();
      const next = `${inset}px`;
      const keyboard = inset >= KEYBOARD_MIN_PX ? "on" : "off";
      if (root.getAttribute(KEYBOARD_ATTR) !== keyboard) {
        root.setAttribute(KEYBOARD_ATTR, keyboard);
      }
      if (root.style.getPropertyValue(VV_INSET_VAR) === next) return;
      root.style.setProperty(VV_INSET_VAR, next);
    };
    const schedule = () => {
      // Один пересчёт на кадр: адресная строка и клавиатура шлют resize пачками.
      if (frame === 0) frame = window.requestAnimationFrame(apply);
    };

    apply();
    viewport?.addEventListener("resize", schedule);
    window.addEventListener("orientationchange", schedule);
    window.addEventListener("resize", schedule);
    return () => {
      viewport?.removeEventListener("resize", schedule);
      window.removeEventListener("orientationchange", schedule);
      window.removeEventListener("resize", schedule);
      if (frame !== 0) window.cancelAnimationFrame(frame);
      root.style.removeProperty(VV_INSET_VAR);
      root.removeAttribute(KEYBOARD_ATTR);
    };
  }, []);
}
