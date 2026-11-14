// Очередь сессии: живые кадры `queue` из ТОГО ЖЕ SSE и первое чтение двери D4.
//
// S4a (R1 §2.1, §8): очередь приезжает кадром `queue` в уже открытом `/api/phone/live` — состав
// обновляется БЕЗ нажатия и без опроса. D4 (`GET /api/phone/queue`) остаётся первым чтением и
// путём повторения после отказа.
//
// 429 (`PhoneQueueBusy`) человеку НЕ показывается (D1 §4): это «слишком часто», а не беда.
// Состав остаётся последним известным, повтор — тихий и с отступом. Отказ (502 и пр.) —
// наоборот, словами и с «Повторить»: там человеку есть что повторить.

import { useCallback, useEffect, useRef, useState } from "react";
import { ApiFailure, loadQueue } from "../api";
import { parseQueue, parseQueueFrame, withoutItem, type QueueView } from "../model/queue";
import { QUEUE_BUSY_RETRY_MAX_MS, QUEUE_BUSY_RETRY_MS } from "../config";
import { reloadForLogin } from "./visibility";
import type { QueueFrame } from "./useLive";

export type QueueApi = {
  view: QueueView;
  /** Перечитать дверью D4 (после `200` D1/D2/D3/D5 и по кнопке «Повторить»). */
  refresh: () => void;
  /** Пункт убран: убрать его сразу, не дожидаясь ответа D4. */
  drop: (itemId: string) => void;
};

export function useQueue(sessionId: string | null, frame: QueueFrame | null): QueueApi {
  const [view, setView] = useState<QueueView>({ status: "loading" });
  const busy = useRef(false);
  const retry = useRef<ReturnType<typeof setTimeout> | null>(null);
  const attempts = useRef(0);

  const read = useCallback(async () => {
    if (sessionId === null || busy.current) return;
    busy.current = true;
    try {
      const body = await loadQueue(sessionId);
      attempts.current = 0;
      setView(parseQueue(body));
    } catch (failure) {
      if (failure instanceof ApiFailure && failure.unauthorized) {
        reloadForLogin();
        return;
      }
      if (failure instanceof ApiFailure && failure.status === 429) {
        // «Слишком часто» — человеку ни слова: вид не меняем, повтор тихий и с отступом.
        const delay = Math.min(
          QUEUE_BUSY_RETRY_MS * 2 ** attempts.current,
          QUEUE_BUSY_RETRY_MAX_MS,
        );
        attempts.current += 1;
        if (retry.current === null) {
          retry.current = setTimeout(() => {
            retry.current = null;
            void read();
          }, delay);
        }
        return;
      }
      const message = failure instanceof ApiFailure ? failure.message : "";
      setView({ status: "unreadable", message });
    } finally {
      busy.current = false;
    }
  }, [sessionId]);

  // Открыли сессию — одно чтение двери; смена сессии сбрасывает и вид, и тихий повтор.
  useEffect(() => {
    setView({ status: "loading" });
    attempts.current = 0;
    if (retry.current !== null) {
      clearTimeout(retry.current);
      retry.current = null;
    }
    void read();
    return () => {
      if (retry.current !== null) {
        clearTimeout(retry.current);
        retry.current = null;
      }
    };
  }, [read]);

  // Живой кадр `queue`: чужая/непонятная форма не стирает показанный состав.
  useEffect(() => {
    if (frame === null) return;
    const next = parseQueueFrame(frame.payload);
    if (next === null) return;
    attempts.current = 0;
    setView(next);
  }, [frame]);

  const refresh = useCallback(() => {
    void read();
  }, [read]);

  const drop = useCallback((itemId: string) => {
    setView((current) => withoutItem(current, itemId));
  }, []);

  return { view, refresh, drop };
}
