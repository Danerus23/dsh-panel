// Клавиатура телефона и липкая полоса задачи: почему страница ездит при наборе и как это снять.
//
// ЗАМЕР 31.10.2026 (живая проверка владельца, 360×800, Edge, сессия с лентой):
//   старт                     scrollY 152, последняя запись top 498, полоса 196, окно 800
//   поле в фокусе             scrollY 152   — фокус сам по себе НИЧЕГО не двигает
//   окно 800 → 500 («клавиатура») scrollY 152 — и уменьшение окна само по себе тоже
//   после первого нажатия клавиши  scrollY 452, последняя запись top 198  ← сдвиг +300
//   300 px — ровно высота «клавиатуры» (800−500).
// Причина названа: браузер прокручивает страницу к каретке, когда визуальный вьюпорт (то, что видно
// над клавиатурой) становится ниже, а поле — липкое `bottom: 0` от LAYOUT-вьюпорта, то есть уезжает
// под клавиатуру. Автоскролл S1 тут не при чём (набор не меняет ни ленту, ни её длину), и поле не
// растёт (textarea фиксированной высоты, текст скроллится внутри).
//
// ЧТО ДЕЛАЕМ: (1) поднимаем полосу на величину клавиатуры (`--dp-keyboard-inset`) — поле остаётся
// видимым над ней; (2) если браузер всё равно дёрнул прокрутку из-за ввода, возвращаем её на место
// (якорь — позиция до ввода). Второе ограничено 200 мс после ввода, чтобы не спорить с человеком,
// который сам листает ленту.

import { useEffect, type RefObject } from "react";

/** Насколько сдвиг считается «дёрганьем», а не намеренной прокруткой. */
const JUMP_PX = 4;

/** Сколько ждём прокрутку браузера после ввода, прежде чем вернуть своё место. */
const RESTORE_MS = 200;

/**
 * Держит полосу над клавиатурой и не даёт браузеру увести ленту при наборе.
 * `fieldRef` — поле задачи: якорь прокрутки ставится на фокус, проверяется после ввода.
 */
export function useKeyboardInset(fieldRef: RefObject<HTMLTextAreaElement | null>): void {
  // (1) Величина клавиатуры → CSS-переменная. Нет `visualViewport` — нет и клавиатуры в расчёте.
  useEffect(() => {
    const viewport = window.visualViewport;
    if (viewport === null || viewport === undefined) return;

    const apply = () => {
      const inset = Math.max(
        0,
        Math.round(window.innerHeight - (viewport.height + viewport.offsetTop)),
      );
      document.documentElement.style.setProperty("--dp-keyboard-inset", `${inset}px`);
    };

    apply();
    viewport.addEventListener("resize", apply);
    viewport.addEventListener("scroll", apply);
    return () => {
      viewport.removeEventListener("resize", apply);
      viewport.removeEventListener("scroll", apply);
      document.documentElement.style.removeProperty("--dp-keyboard-inset");
    };
  }, []);

  // (2) Возврат прокрутки после ввода: браузер прокручивает страницу к каретке — лента «уезжает».
  useEffect(() => {
    const field = fieldRef.current;
    if (field === null) return;

    let anchor: number | null = null;
    let timer = 0;

    const remember = () => {
      anchor = window.scrollY;
    };

    const restore = (target: number) => {
      if (Math.abs(window.scrollY - target) > JUMP_PX) window.scrollTo(0, target);
    };

    const onInput = () => {
      if (anchor === null) anchor = window.scrollY;
      const target = anchor;
      // Браузер прокручивает и синхронно, и кадром позже — проверяем оба раза.
      requestAnimationFrame(() => restore(target));
      window.clearTimeout(timer);
      timer = window.setTimeout(() => restore(target), RESTORE_MS);
    };

    field.addEventListener("focus", remember);
    field.addEventListener("input", onInput);
    return () => {
      field.removeEventListener("focus", remember);
      field.removeEventListener("input", onInput);
      window.clearTimeout(timer);
    };
  }, [fieldRef]);
}
