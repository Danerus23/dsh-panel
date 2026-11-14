// Отправка задачи: одна дверь на экран, одно нажатие — одна попытка.
//
// Контракт §7.1–§7.2: защёлка «одна отправка в полёте» у калитки (вторая → `403 PhoneTaskBusy`),
// а экран обязан не посылать второй POST сам: кнопка гаснет на всё время запроса, повтор безопасен
// тем же `requestId`. Отказ НЕ стирает текст (GOV.UK «never clear entered fields», `40-forms.md` §7).

import { useCallback, useRef, useState } from "react";
import { ApiFailure, sendNewTask, sendPrompt, type TaskAnswer } from "../api";
import {
  acceptedOutcome,
  attemptProblem,
  checkTaskText,
  failedOutcome,
  problemWordKey,
  type TaskOutcome,
} from "../model/task";
import type { DraftApi } from "./useDraft";
import { reloadForLogin } from "./visibility";
import type { T } from "../words";

export type TaskTarget = { kind: "session"; sessionId: string } | { kind: "new" };

export type TaskApi = {
  sending: boolean;
  outcome: TaskOutcome;
  /** Отправить набранное. Второе нажатие в полёте ничего не делает. */
  send: () => void;
  /** Снять прошлую строку исхода: новое действие (стоп) говорит своё, а не старое. */
  reset: () => void;
  /** «Повторить» после отказа: тот же текст, тот же `requestId` — второй задачи не будет. */
  retry: () => void;
};

export function useTask(
  t: T,
  draft: DraftApi,
  target: TaskTarget,
  onAccepted: (answer: TaskAnswer) => void,
): TaskApi {
  const [sending, setSending] = useState(false);
  const [outcome, setOutcome] = useState<TaskOutcome>({ kind: "none" });
  const inFlight = useRef(false);

  const accept = useCallback(
    (answer: TaskAnswer) => {
      draft.clearOnAccepted(answer.requestId);
      onAccepted(answer);
    },
    [draft, onAccepted],
  );

  const send = useCallback(() => {
    if (inFlight.current) return;
    const attempt = draft.takeAttempt();
    const check = checkTaskText(attempt.text, false);
    const problem = attemptProblem(check, attempt.requestId);
    if (problem !== null) {
      // Пусто/сверх предела — причина уже стоит у кнопки, молчим. А «нечем» (нет `crypto.randomUUID`
      // и `getRandomValues`: телефон по http — небезопасный контекст) — это НЕ тишина: живая проверка
      // 31.10.2026 показала, что молчащее нажатие человек читает как «кнопка не работает». Говорим
      // словами существующего ключа (`PhoneTaskBadRequest`), нового ключа не заводим.
      if (problem === "no-id") {
        setOutcome(failedOutcome(t(problemWordKey(problem)), attempt.text));
      } else {
        setOutcome({ kind: "none" });
      }
      return;
    }
    inFlight.current = true;
    setSending(true);
    setOutcome({ kind: "sending" });

    const request =
      target.kind === "session"
        ? sendPrompt(target.sessionId, attempt.requestId, check.text)
        : sendNewTask(attempt.requestId, check.text);

    void request
      .then((answer) => {
        setOutcome(acceptedOutcome(answer.message, target.kind === "new"));
        accept(answer);
      })
      .catch((failure: unknown) => {
        if (failure instanceof ApiFailure && failure.unauthorized) {
          reloadForLogin();
          return;
        }
        const message = failure instanceof ApiFailure ? failure.message : "";
        // Причина — словами, без кода: свои слова двери, иначе «связи нет» (это правда обрыва).
        const words =
          message.trim() !== ""
            ? message
            : t("PhoneTaskNotTakenFormat", t("PhoneTaskReasonNoConnection"));
        setOutcome(failedOutcome(words, check.text));
      })
      .finally(() => {
        inFlight.current = false;
        setSending(false);
      });
  }, [accept, draft, t, target]);

  const reset = useCallback(() => setOutcome({ kind: "none" }), []);

  return { sending, outcome, send, retry: send, reset };
}
