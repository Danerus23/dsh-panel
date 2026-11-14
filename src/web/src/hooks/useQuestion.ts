// Ожидающий вопрос: опрос раз в 2 с, только видимая страница, пауза пока человек пишет/выбирает.
//
// «Первый ответивший» (контракт §2.5): ответ уходит ОДИН раз; второй POST получает 403 словами и
// в движок не идёт. Проигравший узнаёт исход из этого же опроса (`taken`) — поэтому после 403
// опрос запускается немедленно, не дожидаясь такта.
//
// Ответ и отмена кладут итог локально сразу (не ждём такта), но итог ПРИВЯЗАН к событию и сессии
// последнего известного вопроса: иначе «Ответ отправлен» всплывало бы в чужой сессии.

import { useCallback, useEffect, useRef, useState } from "react";
import { ApiFailure, loadQuestion, sendAnswer, sendCancel } from "../api";
import { QUESTION_POLL_MS } from "../config";
import {
  parseQuestion,
  pollAllowed,
  type AnswerItem,
  type PendingQuestion,
  type QuestionOutcome,
} from "../model/question";
import { reloadForLogin } from "./visibility";

export type ActionResult = { ok: boolean; message: string };

type QuestionContext = { eventId: string; sessionId: string };

export type QuestionApi = {
  pending: PendingQuestion | null;
  outcome: QuestionOutcome | null;
  /** К какому событию/сессии относится итог (последний известный вопрос). */
  context: QuestionContext | null;
  error: string;
  send: (eventId: string, answers: AnswerItem[]) => Promise<ActionResult>;
  cancel: (eventId: string) => Promise<ActionResult>;
  refresh: () => void;
  suspend: (ms?: number) => void;
};

const SUSPEND_MS = 3000;

function focusInCard(): boolean {
  if (typeof document === "undefined") return false;
  const active = document.activeElement;
  return active instanceof Element && active.closest("[data-dp-question]") !== null;
}

export function useQuestion(visible: boolean, enabled: boolean): QuestionApi {
  const [pending, setPending] = useState<PendingQuestion | null>(null);
  const [outcome, setOutcome] = useState<QuestionOutcome | null>(null);
  const [context, setContext] = useState<QuestionContext | null>(null);
  const [error, setError] = useState("");
  const [nonce, setNonce] = useState(0);
  const suspendedUntil = useRef(0);
  const known = useRef<QuestionContext | null>(null);

  const remember = useCallback((next: QuestionContext) => {
    known.current = next;
    setContext(next);
  }, []);

  const refresh = useCallback(() => setNonce((value) => value + 1), []);
  const suspend = useCallback((ms: number = SUSPEND_MS) => {
    suspendedUntil.current = Date.now() + ms;
  }, []);

  useEffect(() => {
    if (!visible || !enabled) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | null = null;

    const tick = async () => {
      if (stopped) return;
      const allowed = pollAllowed({
        focusedInCard: focusInCard(),
        suspendedUntil: suspendedUntil.current,
        now: Date.now(),
      });
      if (!allowed) {
        timer = setTimeout(() => void tick(), QUESTION_POLL_MS);
        return;
      }
      try {
        const body = await loadQuestion();
        if (stopped) return;
        const parsed = parseQuestion(body);
        if (parsed !== null) {
          setError("");
          if (parsed.kind === "question") {
            setPending(parsed);
            if (known.current === null || known.current.eventId !== parsed.eventId) {
              // Новый вопрос начинает новый набор: старый итог больше не показываем.
              remember({ eventId: parsed.eventId, sessionId: parsed.sessionId });
              setOutcome((current) =>
                current !== null && current.eventId !== parsed.eventId ? null : current,
              );
            }
          } else if (parsed.kind === "none") {
            setPending(null);
          } else {
            setPending(null);
            // Итог показываем, только если он про ИЗВЕСТНЫЙ нам вопрос (иначе это чужое событие).
            if (known.current === null || known.current.eventId === parsed.eventId) {
              if (known.current === null) remember({ eventId: parsed.eventId, sessionId: "" });
              setOutcome(parsed);
            }
          }
        }
      } catch (failure) {
        if (stopped) return;
        if (failure instanceof ApiFailure && failure.unauthorized) {
          reloadForLogin();
        } else {
          setError(failure instanceof ApiFailure ? failure.message : "");
        }
      }
      if (!stopped) timer = setTimeout(() => void tick(), QUESTION_POLL_MS);
    };

    void tick();
    return () => {
      stopped = true;
      if (timer !== null) clearTimeout(timer);
    };
  }, [visible, enabled, nonce, remember]);

  const send = useCallback(
    async (eventId: string, answers: AnswerItem[]): Promise<ActionResult> => {
      try {
        const message = await sendAnswer(eventId, answers);
        setPending(null);
        setOutcome({ kind: "answered", eventId, message });
        return { ok: true, message };
      } catch (failure) {
        // 403 «устарел/чужой» — вопрос снят; перечитываем сразу, ввод не отправляем.
        if (failure instanceof ApiFailure && failure.status === 403) refresh();
        return {
          ok: false,
          message: failure instanceof ApiFailure ? failure.message : "",
        };
      }
    },
    [refresh],
  );

  const cancel = useCallback(
    async (eventId: string): Promise<ActionResult> => {
      try {
        const message = await sendCancel(eventId);
        setPending(null);
        setOutcome({ kind: "cancelled", eventId, message });
        return { ok: true, message };
      } catch (failure) {
        if (failure instanceof ApiFailure && failure.status === 403) refresh();
        return {
          ok: false,
          message: failure instanceof ApiFailure ? failure.message : "",
        };
      }
    },
    [refresh],
  );

  return { pending, outcome, context, error, send, cancel, refresh, suspend };
}
