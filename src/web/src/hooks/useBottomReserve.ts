// НИЖНИЙ СТЕК И ОДНО ИЗМЕРЕННОЕ ЧИСЛО (D11 §1.1–§1.2, DEF-145/148/149).
//
// Внизу экрана теперь стоит зацеп шторки ввода, а над ним — вопрос и очередь (W10/W12). Все трое
// живут в ОДНОМ `fixed`-стеке `.dp-bottom` (портал в `body`), и страница обязана знать, сколько
// этот стек занимает: иначе последняя реплика ленты уходит под него, а `scroll-padding` не спасает
// фокус. Отсюда одно число на все состояния — `--dp-bottom-reserve` на `:root`.
//
// Правила, которые держатся здесь:
//   * пишет ФАКТИЧЕСКУЮ высоту стека (вместе с отбивкой сверху: 44 зацепа + 8 зазора = 52), целыми
//     пикселями, и только когда она изменилась хотя бы на 1 px — иначе переменная дёргала бы
//     пересчёт при каждом кадре анимации;
//   * пересчёт — ТОЛЬКО по `ResizeObserver` и по появлению/исчезновению самого стека. Слушателей
//     прокрутки нет: R2 (дрожание шара) был ровно про них, и `90` №19 это подтверждает;
//   * `MutationObserver` смотрит ТОЛЬКО прямых детей `body` — стек живёт порталом там же, а лента
//     меняется внутри `main` и наблюдателя не будит (иначе каждый пузырь стоил бы `querySelector`);
//   * при снятии стека (лист ввода поднят — зацепа на экране нет) переменная НЕ обнуляется: пока
//     лист открыт, `:root.dp-sheet-open` считает `scroll-padding` как запас + высота листа, и
//     просевший до нуля запас уронил бы поле под лист (D11 §1.2);
//   * запасной путь на случай, когда наблюдатель ещё не ответил, — 52 px в CSS (`:root`).

import { useEffect, useState } from "react";

import { BOTTOM_RESERVE_PX } from "../config";

/** Селектор нижнего стека — ОДНО имя на весь слой (его же читают проверки). */
export const BOTTOM_STACK_SELECTOR = ".dp-bottom";

/**
 * Держит `--dp-bottom-reserve` равным высоте нижнего стека и возвращает это число в React
 * (по нему экран обновляет допуск автоскролла: запас — часть одного числа).
 */
export function useBottomReserve(): number {
  const [reserve, setReserve] = useState(BOTTOM_RESERVE_PX);

  useEffect(() => {
    const root = document.documentElement;
    let node: HTMLElement | null = null;
    let applied = -1;
    /** Кадр, в котором записывается переменная (см. ниже, почему не сразу). */
    let frame = 0;

    const apply = (): void => {
      if (node === null) return;
      const height = Math.round(node.getBoundingClientRect().height);
      if (Math.abs(height - applied) < 1) return;
      applied = height;
      // ⚠️ ЗАПИСЬ — КАДРОМ, А НЕ ИЗ КОЛБЭКА ResizeObserver: переменная меняет `padding-bottom` страницы,
      // то есть размер `body`, а `body` тоже наблюдается (пин ленты) — браузер на такую цепочку отвечает
      // глобальной ошибкой «ResizeObserver loop completed with undelivered notifications», а пульт на
      // любую глобальную ошибку показывает аварийный экран (`main.tsx`, `window.addEventListener`).
      // Один кадр задержки — и запись одна на пачку изменений.
      if (frame !== 0) window.cancelAnimationFrame(frame);
      frame = window.requestAnimationFrame(() => {
        frame = 0;
        root.style.setProperty("--dp-bottom-reserve", `${height}px`);
        setReserve(height);
      });
    };

    const observer = new ResizeObserver(apply);

    const attach = (): void => {
      const found = document.body.querySelector<HTMLElement>(BOTTOM_STACK_SELECTOR);
      if (found === node) return;
      observer.disconnect();
      node = found;
      if (node === null) return;
      observer.observe(node);
      apply();
    };

    attach();
    // Стек — прямой ребёнок `body` (портал); лист ввода снимает его с экрана и возвращает обратно.
    const mutations = new MutationObserver(attach);
    mutations.observe(document.body, { childList: true });

    return () => {
      mutations.disconnect();
      observer.disconnect();
      if (frame !== 0) window.cancelAnimationFrame(frame);
    };
  }, []);

  return reserve;
}
