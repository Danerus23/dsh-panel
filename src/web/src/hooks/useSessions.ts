// Список сессий — БЕЗ таймера (S4a, R1 §5.2).
//
// Перечитывание идёт по трём поводам, и ни один из них не «раз в 10 с»:
//   1) экран открылся (в том числе возврат из сессии — экран монтируется заново);
//   2) человек вернулся на вкладку (`visibilitychange`);
//   3) калитка сказала `stale: true` — «список мог измениться» (подсказка из уже открытого `$events`);
//   4) G1: кадр `stale` из СВОЕГО потока подсказки `/api/phone/list/live`, пока экран видим.
//      Поток молчит — список не читается вовсе; уход со экрана (скрытие/размонтирование) закрывает его.
//      ⚠️ DEF-150 (12.11.2026): подсказка работает И В СЕССИИ, а не только на списке — без неё
//      строка сессии в открытом разговоре оставалась снимком на момент открытия, и шапка не видела,
//      что подчинённая ещё работает (или уже кончила). Цена названа: в сессии раз в ≤3 с может
//      читаться список — тот же, что читает сам список; слотов двери две, экран сессии один.
//
// Подсказка — НЕ истина: события не переигрываются после обрыва, поэтому пропущенный конец хода
// сам не вернётся; истина — перечитанный `session/list`. Порог LIST_STALE_MIN_MS держит подсказку
// от превращения в опрос.
//
// «Сессий нет» — успешный пустой ответ; «список не прочитался» — отдельные слова, и последний
// известный состав остаётся на экране (прошлое не стирается отказом).

import { useCallback, useEffect, useRef, useState } from "react";
import { ApiFailure, LIST_LIVE_PATH, loadSessions } from "../api";
import { LiveStream } from "../live/sse";
import { parseList, type SessionList } from "../model/list";
import { LIST_STALE_MIN_MS } from "../config";
import { reloadForLogin } from "./visibility";

export type SessionsState = {
  list: SessionList | null;
  loading: boolean;
  refreshing: boolean;
  /** Фраза отказа калитки (пустая — экран подставит свой ключ). */
  error: string;
  /** Калитка сказала «список мог измениться»: перечитаем сами, но не чаще порога. */
  stale: boolean;
};

export type SessionsApi = SessionsState & {
  refresh: () => void;
};

/** Повод чтения: первый проход · подсказка `stale` · возврат на вкладку · путь повторения человеком. */
type ReadKind = "initial" | "stale" | "return" | "manual";

/**
 * ЭКРАН СПИСКА ЛИ. Раньше живая подсказка была только списку; с DEF-150 (12.11.2026) она работает
 * и на маршруте сессии: шапка сессии берёт из строки признак «работает подчинённая» (`row.running`,
 * агрегат W3), и без подсказки это был снимок на момент открытия. Слотов `GET /api/phone/list/live`
 * два, экран один — запас есть (третья подписка получает 429, G1).
 */

export function useSessions(visible: boolean): SessionsApi {
  const [list, setList] = useState<SessionList | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState("");
  const [stale, setStale] = useState(false);
  const busy = useRef(false);
  const lastAt = useRef(0);
  /** G1: запланированное чтение по подсказке (один запрос на порог) и «окно уже прочитано». */
  const hintTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const hintUntil = useRef(0);
  const read = useCallback(async (kind: ReadKind) => {
    if (busy.current) return;
    // Порог подсказки `stale` живёт в её эффекте (delay = 3 с от прошлого чтения):
    // здесь второго сторожа нет, иначе подсказка могла бы потеряться на границе срока.
    busy.current = true;
    lastAt.current = Date.now();
    if (kind === "manual") setRefreshing(true);
    try {
      const payload = await loadSessions();
      setList(parseList(payload));
      setError("");
      setStale(payload.stale);
      setLoading(false);
    } catch (failure) {
      setLoading(false);
      // Отказ не оставляет «stale» висеть: иначе следующий рендер ждал бы подсказку,
      // которой уже не будет.
      setStale(false);
      if (failure instanceof ApiFailure && failure.unauthorized) {
        reloadForLogin();
        return;
      }
      setError(failure instanceof ApiFailure ? failure.message : "");
    } finally {
      busy.current = false;
      if (kind === "manual") setRefreshing(false);
    }
  }, []);

  // 1) Экран открылся (и возврат из сессии: компонент монтируется заново).
  useEffect(() => {
    void read("initial");
  }, [read]);

  // 2) Человек вернулся на вкладку — перечитываем; уход со вкладки не читает вовсе.
  //    Первый рендер не считается возвратом: список уже прочитан пунктом 1.
  const wasVisible = useRef(visible);
  useEffect(() => {
    const came = visible && !wasVisible.current;
    wasVisible.current = visible;
    if (came) void read("return");
  }, [visible, read]);

  // 3) Подсказка `stale`: один запрос не чаще порога; флаг снимает калитка, выдавая ответ.
  useEffect(() => {
    if (!stale || !visible) return;
    const delay = Math.max(0, LIST_STALE_MIN_MS - (Date.now() - lastAt.current));
    const timer = setTimeout(() => void read("stale"), delay);
    return () => clearTimeout(timer);
  }, [stale, visible, list, read]);

  // 4) ЖИВАЯ ПОДСКАЗКА (G1): свой поток «список мог измениться». Кадр `stale` — только повод
  //    перечитать; истина — ответ списка. Порог LIST_STALE_MIN_MS (3 с) держит подсказку от
  //    превращения в опрос: два кадра в одном окне дают ОДНО чтение. Таймера нет — без кадров
  //    поток молчит (пинг — не повод читать). Обрыв — тихий повтор LiveStream по SSE_BACKOFF_MS,
  //    человеку ни слова: слова об обрыве есть только у живого экрана сессии, а список либо
  //    читается, либо говорит своим отказом (ApiFailure выше).
  useEffect(() => {
    // DEF-150: подсказка нужна и сессии — из этой строки шапка берёт «работает подчинённая».
    if (!visible) return;

    const hint = new LiveStream(LIST_LIVE_PATH, {
      onSnapshot: () => undefined,
      onRecord: () => undefined,
      onAssistant: () => undefined,
      onStatus: () => undefined,
      onStale: () => {
        // Один запрос на порог: пока чтение запланировано или окно не истекло — кадр мимо.
        if (hintTimer.current !== null) return;
        if (Date.now() < hintUntil.current) return;
        const delay = Math.max(0, LIST_STALE_MIN_MS - (Date.now() - lastAt.current));
        hintTimer.current = setTimeout(() => {
          hintTimer.current = null;
          hintUntil.current = Date.now() + LIST_STALE_MIN_MS;
          void read("stale");
        }, delay);
      },
    });

    hint.start();

    return () => {
      hint.stop();
      if (hintTimer.current !== null) {
        clearTimeout(hintTimer.current);
        hintTimer.current = null;
      }
    };
  }, [visible, read]);

  const refresh = useCallback(() => {
    void read("manual");
  }, [read]);

  return { list, loading, refreshing, error, stale, refresh };
}
