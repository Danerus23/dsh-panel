// Видимость страницы: пока вкладка скрыта — опросы стоят; лента закрывается, только если
// страница скрыта дольше 5 с (короткий переход в другое приложение не рвёт поток зря).

import { useEffect, useRef, useState } from "react";
import { HIDDEN_CLOSE_MS } from "../config";

export function useVisible(): boolean {
  const [visible, setVisible] = useState(() => !document.hidden);
  useEffect(() => {
    const onChange = () => setVisible(!document.hidden);
    document.addEventListener("visibilitychange", onChange);
    return () => document.removeEventListener("visibilitychange", onChange);
  }, []);
  return visible;
}

/**
 * `true`, если страница скрыта дольше `delayMs`. Возврат на страницу сбрасывает признак.
 * Так «скрыто > 5 с» становится одним булевым значением, а не россыпью таймеров по экранам.
 */
export function useHiddenLong(delayMs: number = HIDDEN_CLOSE_MS): boolean {
  const visible = useVisible();
  const [hiddenLong, setHiddenLong] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    if (timer.current !== null) {
      clearTimeout(timer.current);
      timer.current = null;
    }
    if (visible) {
      setHiddenLong(false);
      return;
    }
    timer.current = setTimeout(() => setHiddenLong(true), delayMs);
    return () => {
      if (timer.current !== null) clearTimeout(timer.current);
      timer.current = null;
    };
  }, [visible, delayMs]);

  return hiddenLong;
}

/** Перезагрузка страницы один раз: вход истёк — калитка отдаст экран входа, а не белый лист. */
let reloaded = false;
export function reloadForLogin(): void {
  if (reloaded) return;
  reloaded = true;
  window.location.reload();
}
