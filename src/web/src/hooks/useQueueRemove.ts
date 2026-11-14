// «Убрать» один пункт очереди (D5): вопрос на месте пункта, безопасный ответ первым.
//
// Контракт §6 и паспорт §3.4: не модальное окно, «раскрывается на месте»; одновременно открыто
// не больше одного подтверждения; «Убрано» и «Уже нет в очереди» — ОДИН спокойный вид, не ошибка
// (пункта нет — человек хотел, чтобы его не было). Отказ `502` оставляет пункт и подтверждение.

import { useCallback, useEffect, useRef, useState } from "react";
/** Сколько живёт слово успеха: 5 с — как у строки исхода (`90` №19). */
const OUTCOME_FADE_MS = 5_000;
import { ApiFailure, removeQueueItem } from "../api";
import { reloadForLogin } from "./visibility";
import type { T } from "../words";

export type RemoveState = {
  /** Пункт, у которого раскрыт вопрос (только один). */
  asking: string | null;
  /** Пункт, который сейчас убирается (обе кнопки погашены). */
  removing: string | null;
  /** Итог последнего удаления — словами; ошибка отличается видом, не словом «ошибка». */
  outcome:
    | { kind: "none" }
    | { kind: "removed"; words: string }
    | { kind: "gone"; words: string }
    | { kind: "failed"; words: string; itemId: string };
};

export type RemoveApi = RemoveState & {
  ask: (itemId: string) => void;
  keep: () => void;
  confirm: (itemId: string) => void;
  retry: () => void;
};

export function useQueueRemove(
  t: T,
  sessionId: string,
  onRemoved: (itemId: string) => void,
): RemoveApi {
  const [asking, setAsking] = useState<string | null>(null);
  const [removing, setRemoving] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<RemoveState["outcome"]>({ kind: "none" });
  const inFlight = useRef(false);
  /** Таймер гашения успеха — один на крючок; снимается при размонтировании и новом итоге. */
  const fadeTimer = useRef<number | null>(null);
  const clearFade = useCallback(() => {
    if (fadeTimer.current !== null) {
      window.clearTimeout(fadeTimer.current);
      fadeTimer.current = null;
    }
  }, []);
  useEffect(() => clearFade, [clearFade]);
  /**
   * Показать итог удаления. УСПЕХ ГАСНЕТ через 5 с (D11 §2.4, `90` №19): факт остаётся в составе
   * очереди (пункт исчез), а слово не висит над панелью вечно (дефект живой приёмки 13.11.2026).
   * Ошибка НЕ гаснет: в ней живёт «Повторить», и человек сам решает, когда её закрыть.
   */
  const showOutcome = useCallback(
    (next: RemoveState["outcome"]) => {
      clearFade();
      setOutcome(next);
      if (next.kind === "removed" || next.kind === "gone") {
        fadeTimer.current = window.setTimeout(() => {
          fadeTimer.current = null;
          setOutcome({ kind: "none" });
        }, OUTCOME_FADE_MS);
      }
    },
    [clearFade],
  );
  /** Вопрос на месте пункта; второй вопрос закрывает первый (паспорт §3.4, п. 4). */
  const ask = useCallback(
    (itemId: string) => {
      clearFade();
      setAsking(itemId);
      setOutcome({ kind: "none" });
    },
    [clearFade],
  );

  const keep = useCallback(() => {
    setAsking(null);
  }, []);

  const remove = useCallback(
    (itemId: string) => {
      if (inFlight.current) return;
      inFlight.current = true;
      setRemoving(itemId);
      void removeQueueItem(sessionId, itemId)
        .then((answer) => {
          setAsking(null);
          const words = answer.message.trim();
          if (answer.state === "gone") {
            showOutcome({ kind: "gone", words: words !== "" ? words : t("PhoneQueueAlreadyGone") });
          } else {
            showOutcome({ kind: "removed", words: words !== "" ? words : t("PhoneQueueRemoved") });
          }
          onRemoved(itemId);
        })
        .catch((failure: unknown) => {
          if (failure instanceof ApiFailure && failure.unauthorized) {
            reloadForLogin();
            return;
          }
          const message = failure instanceof ApiFailure ? failure.message : "";
          showOutcome({
            kind: "failed",
            itemId,
            words:
              message.trim() !== ""
                ? message
                : t("PhoneQueueRemoveFailedFormat", t("PhoneTaskReasonNoConnection")),
          });
        })
        .finally(() => {
          inFlight.current = false;
          setRemoving(null);
        });
    },
    [onRemoved, sessionId, showOutcome, t],
  );

  const retry = useCallback(() => {
    if (outcome.kind === "failed") remove(outcome.itemId);
  }, [outcome, remove]);

  return { asking, removing, outcome, ask, keep, confirm: remove, retry };
}
