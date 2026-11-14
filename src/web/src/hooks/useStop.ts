// Остановка хода (D3): «остановлено» и «уже не работает» — два разных честных исхода `200`.
//
// Контракт §4: очередь при остановке НЕ пропадает (движок отменяет с `keepInbox:true`), поэтому
// после ответа экран перечитывает очередь и говорит «в очереди N», а не «очередь очищена».
// Отказ `502` — слова двери + «Повторить», кнопка не исчезает. Дверь стопа одна (вердикт t10 §2 п. 2):
// пока видна карточка вопроса S2, строки стопа в полосе нет целиком.

import { useCallback, useRef, useState } from "react";
import { ApiFailure, sendStop } from "../api";
import { reloadForLogin } from "./visibility";
import type { T } from "../words";

export type StopOutcome =
  | { kind: "none" }
  | { kind: "stopping" }
  | { kind: "stopped"; words: string }
  | { kind: "idle"; words: string }
  | { kind: "failed"; words: string };

export type StopApi = {
  stopping: boolean;
  outcome: StopOutcome;
  stop: () => void;
  retry: () => void;
};

export function useStop(t: T, sessionId: string, onStopped: () => void): StopApi {
  const [stopping, setStopping] = useState(false);
  const [outcome, setOutcome] = useState<StopOutcome>({ kind: "none" });
  const inFlight = useRef(false);

  const stop = useCallback(() => {
    if (inFlight.current) return;
    inFlight.current = true;
    setStopping(true);
    setOutcome({ kind: "stopping" });
    void sendStop(sessionId)
      .then((answer) => {
        const words = answer.message.trim();
        if (answer.stopped) {
          setOutcome({ kind: "stopped", words: words !== "" ? words : t("PhoneStopDone") });
          onStopped();
          return;
        }
        setOutcome({ kind: "idle", words: words !== "" ? words : t("PhoneStopIdle") });
      })
      .catch((failure: unknown) => {
        if (failure instanceof ApiFailure && failure.unauthorized) {
          reloadForLogin();
          return;
        }
        const message = failure instanceof ApiFailure ? failure.message : "";
        setOutcome({
          kind: "failed",
          words:
            message.trim() !== ""
              ? message
              : t("PhoneStopFailedFormat", t("PhoneTaskReasonNoConnection")),
        });
      })
      .finally(() => {
        inFlight.current = false;
        setStopping(false);
      });
  }, [onStopped, sessionId, t]);

  return { stopping, outcome, stop, retry: stop };
}
