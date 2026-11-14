// КАРТОЧКА ВОПРОСА АГЕНТА (S2, контракт §3.3) — состояние экрана сессии, не отдельный экран.
//
// Правила, которые здесь держатся разметкой, а не вниманием:
//   * ОДИН вопрос на экране + «Вопрос N из M» и стрелки (карточка 32a §4 п. 2): отвечать можно
//     не по порядку — ответы лежат локально на ВСЕ карточки, а уходит один POST /answer;
//   * `fieldset` + `legend` на показанный вопрос, подпись варианта — часть цели (≥ 44);
//   * ответ обязателен на каждый вопрос: иначе кнопка «Ответить» неактивна и рядом сказано почему
//     (это ЕДИНСТВЕННАЯ подпись-комментарий, которая остаётся, — `60-states.md` §4а);
//   * свои слова — `<label for>` + `textarea` ≤ 500; если вариантов нет вовсе — это единственный ответ;
//   * отмена — двухшаговая, «Нет, оставить» стоит ПЕРВОЙ по разметке (безопасный ответ первым);
//     фокус при раскрытии уходит на безопасный ответ, при закрытии ВОЗВРАЩАЕТСЯ на «Отменить ход»
//     (WCAG 2.2 SC 2.4.3; в очереди такой возврат уже был — теперь и здесь);
//   * ошибка отправки не стирает ввод; итог объявляется `role="status"` один раз.
//
// Чего здесь БОЛЬШЕ НЕТ (карточка 32a §4 п. 2): подписей-комментариев `header`/`detail` от движка и
// строки «вопрос подчинённого» — функции и кнопки должны быть понятны сами.

import { useId, useMemo, useRef, useState } from "react";
import { IconChevronLeft, IconChevronRight } from "@douyinfe/semi-icons";
import {
  buildAnswerItems,
  canSend,
  emptyAnswers,
  nextUnansweredIndex,
  MAX_CUSTOM,
  toggleChoice,
  type AnswerItem,
  type AnswerValue,
  type PendingQuestion,
  type QuestionOutcome,
} from "../model/question";
import type { ActionResult } from "../hooks/useQuestion";
import type { WordKey } from "../i18n";
import { joinReason, type T } from "../words";

export type QuestionCardProps = {
  t: T;
  question: PendingQuestion;
  onSend: (eventId: string, answers: AnswerItem[]) => Promise<ActionResult>;
  onCancel: (eventId: string) => Promise<ActionResult>;
  /** Пауза опроса после ввода: перерисовка не должна мешать человеку. */
  suspend: () => void;
};

export function QuestionCard({ t, question, onSend, onCancel, suspend }: QuestionCardProps) {
  const [values, setValues] = useState<Record<string, AnswerValue>>(() =>
    emptyAnswers(question.cards),
  );
  const [sending, setSending] = useState(false);
  const [error, setError] = useState("");
  const [cancelOpen, setCancelOpen] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [screen, setScreen] = useState(0);
  const baseId = useId();
  const ready = useMemo(() => canSend(question.cards, values), [question.cards, values]);

  const count = question.cards.length;
  const index = Math.min(Math.max(screen, 0), Math.max(count - 1, 0));
  const card = question.cards[index] ?? null;

  // ОДНА ГЛАВНАЯ КНОПКА (D11 §5.2, DEF-146): пока есть неотвеченные и вопросов больше одного —
  // «Далее», иначе — «Ответить». «Далее» только ВЕДЁТ к следующей неотвеченной и ничего не отправляет;
  // идти некуда (неотвеченная — только текущая) — кнопка гаснет, причина — `PhonePageChooseAll`.
  const nextIndex = useMemo(
    () => nextUnansweredIndex(question.cards, values, index),
    [question.cards, values, index],
  );
  const stepping = count > 1 && !ready;
  /** Выключена ли главная кнопка и почему (причина печатается ТОЛЬКО тогда — `60` §7). */
  const hint = !ready && (!stepping || nextIndex === null);
  const primaryLabel =
    error !== ""
      ? t("PhonePageRepeatButton")
      : stepping
        ? t("PhonePageNextButton")
        : t("PhonePageAnswerButton");
  const primaryDisabled =
    sending || cancelling || (!stepping && !ready) || (stepping && nextIndex === null);
  const legendRef = useRef<HTMLLegendElement | null>(null);
  const keepRef = useRef<HTMLButtonElement | null>(null);
  const cancelToggleRef = useRef<HTMLButtonElement | null>(null);

  const update = (id: string, next: AnswerValue) => {
    suspend();
    setValues((previous) => ({ ...previous, [id]: next }));
  };

  const goTo = (target: number) => {
    if (target < 0 || target >= count) return;
    setScreen(target);
    // Фокус — на вопрос, а не «в никуда»: читалка обязана объявить, что показано теперь.
    window.requestAnimationFrame(() => legendRef.current?.focus());
  };

  const openCancel = () => {
    suspend();
    setCancelOpen(true);
    // Безопасный ответ — первым в фокусе (как в очереди: `QueueSection`).
    window.requestAnimationFrame(() => keepRef.current?.focus());
  };

  const closeCancel = () => {
    setCancelOpen(false);
    // Возврат фокуса на кнопку, которая раскрыла подтверждение (SC 2.4.3).
    window.requestAnimationFrame(() => cancelToggleRef.current?.focus());
  };

  const submit = () => {
    if (sending || !ready) return;
    const answers = buildAnswerItems(question.cards, values);
    if (answers === null) return;
    setSending(true);
    setError("");
    void onSend(question.eventId, answers).then((result) => {
      setSending(false);
      if (!result.ok) setError(result.message);
    });
  };

  const cancelTurn = () => {
    if (cancelling) return;
    setCancelling(true);
    setError("");
    void onCancel(question.eventId).then((result) => {
      setCancelling(false);
      if (!result.ok) {
        setError(joinReason(t("PhonePageCancelFailedFormat"), result.message));
        setCancelOpen(false);
      }
    });
  };

  const value =
    card === null
      ? { selected: [], custom: "" }
      : (values[card.id] ?? { selected: [], custom: "" });
  const fieldId = `${baseId}-${index}`;

  return (
    <section
      className="dp-question"
      data-dp-question
      data-event={question.eventId}
      data-card-index={index}
      data-card-count={count}
    >
      <p className="dp-visually-hidden" role="status">
        {t("PhonePageQuestionTitle")}
      </p>
      {/* ШАПКА поверхности (D11 §4.2): заголовок и пейджер не прокручиваются — в прокрутке только
          тело с вариантами, а ряд действий стоит ВНЕ её и виден без прокрутки (`60` §7). */}
      <div className="dp-question-head">
        <h2 className="dp-question-title">{t("PhonePageQuestionTitle")}</h2>

        {count > 1 ? (
          // «Вопрос N из M» и переходы по карточкам. Ответ уходит ОДНИМ конвертом на все вопросы,
          // поэтому стрелки — порядок обхода, а не частичная отправка (R1, Д-2).
          <div className="dp-question-pager">
            <button
              type="button"
              className="dp-quiet dp-icon-button"
              disabled={index === 0}
              aria-label={t("PhonePageQuestionCountFormat", index, count)}
              onClick={() => goTo(index - 1)}
            >
              <IconChevronLeft aria-hidden="true" />
            </button>
            <p className="dp-question-count" aria-live="polite">
              {t("PhonePageQuestionCountFormat", index + 1, count)}
            </p>
            <button
              type="button"
              className="dp-quiet dp-icon-button"
              disabled={index === count - 1}
              // F-B (L4/R3): на последнем вопросе было «Вопрос 3 из 2» — `index + 2` без клампа.
              // Настоящее имя стрелок («следующий вопрос») — отдельная правка: нужны два новых слова.
              aria-label={t("PhonePageQuestionCountFormat", Math.min(index + 2, count), count)}
              onClick={() => goTo(index + 1)}
            >
              <IconChevronRight aria-hidden="true" />
            </button>
          </div>
        ) : null}
      </div>

      <div className="dp-question-body">
        {card !== null ? (
          <fieldset className="dp-ask" key={card.id}>
            <legend ref={legendRef} tabIndex={-1}>
              {card.question}
            </legend>

            {card.options.length > 0 ? (
              <div className="dp-choices">
                {card.options.map((option, optionIndex) => {
                  const inputId = `${fieldId}-option-${optionIndex}`;
                  return (
                    <label className="dp-choice" key={inputId} htmlFor={inputId}>
                      <input
                        id={inputId}
                        type={card.multiSelect ? "checkbox" : "radio"}
                        name={fieldId}
                        checked={value.selected.includes(option.label)}
                        onChange={() =>
                          update(card.id, toggleChoice(value, option.label, card.multiSelect))
                        }
                      />
                      <span>
                        {option.label}
                        {option.description !== null ? (
                          <span className="dp-choice-note">{option.description}</span>
                        ) : null}
                      </span>
                    </label>
                  );
                })}
              </div>
            ) : null}

            <div className="dp-field">
              <label htmlFor={`${fieldId}-own`}>
                {card.options.length > 0 ? t("PhonePageOwnLabel") : t("PhonePageOwnLabelRequired")}
              </label>
              <textarea
                id={`${fieldId}-own`}
                className="dp-textarea"
                rows={2}
                maxLength={MAX_CUSTOM}
                placeholder={t("PhonePageOwnPlaceholder")}
                value={value.custom}
                onChange={(event) => update(card.id, { ...value, custom: event.target.value })}
                onFocus={(event) => {
                  // R6: поле своего ответа — последнее в теле, а клавиатура отнимает у тела высоту.
                  // Chrome доводит до видимости только ПО БЛИЖАЙШЕЙ КРОМКЕ, и при поднятой клавиатуре
                  // было видно лишь верх поля. Человек печатает — значит его поле должно быть
                  // целиком: прокручиваем само тело к концу (страницу не трогаем — карточка `fixed`).
                  const body = event.currentTarget.closest<HTMLElement>(".dp-question-body");
                  if (body !== null) body.scrollTop = body.scrollHeight;
                }}
              />
            </div>
          </fieldset>
        ) : null}

        {/* Причина — ТОЛЬКО у выключенной главной кнопки (`60` §7); когда «Далее» ведёт, причины нет. */}
        {hint ? <p className="dp-note">{t("PhonePageChooseAll")}</p> : null}
        {sending ? (
          <p className="dp-note" role="status">
            {t("PhoneWebAnswerSending")}
          </p>
        ) : null}
        {error !== "" ? (
          <p className="dp-alert" role="alert">
            {joinReason(t("PhonePageAnswerFailedFormat"), error)}
          </p>
        ) : null}
      </div>

      <div className="dp-actions dp-question-actions">
        <button
          type="button"
          className="dp-primary"
          disabled={primaryDisabled}
          onClick={() => {
            // «Далее» ВЕДЁТ и не отправляет (D11 §5.2): отправка — только у «Ответить».
            if (stepping) {
              if (nextIndex !== null) goTo(nextIndex);
              return;
            }
            submit();
          }}
        >
          {primaryLabel}
        </button>
        {!cancelOpen ? (
          <button
            type="button"
            className="dp-quiet"
            ref={cancelToggleRef}
            disabled={sending || cancelling}
            onClick={openCancel}
          >
            {t("PhonePageCancelButton")}
          </button>
        ) : null}
      </div>

      {cancelOpen ? (
        <div className="dp-cancel">
          <p className="dp-note">{t("PhonePageCancelWarning")}</p>
          <div className="dp-actions">
            {/* Безопасный ответ — первым по разметке (норма банка 50 §3). */}
            <button
              type="button"
              className="dp-quiet"
              ref={keepRef}
              disabled={cancelling}
              onClick={closeCancel}
            >
              {t("PhonePageCancelNo")}
            </button>
            <button
              type="button"
              className="dp-primary dp-danger-action"
              disabled={cancelling}
              onClick={cancelTurn}
            >
              {t("PhonePageCancelYes")}
            </button>
          </div>
        </div>
      ) : null}
    </section>
  );
}

// Исходы, которые ПЕЧАТАЮТСЯ (D11 §6.2): успех из этого списка убран — «Ответ отправлен»
// больше не печатается нигде (слово владельца 12.11.2026: «вообще не нужно»). Ключи
// `PhonePageAnswered`/`PhonePageAnsweredNext` остаются в словарях, но не печатаются.
const OUTCOME_FALLBACK: Record<string, WordKey> = {
  taken: "PhonePageTaken",
  cancelled: "PhonePageCancelled",
  "no-answer": "PhonePageCancelled",
  unreadable: "PhonePageUnreadable",
};

const OUTCOME_NEXT: Partial<Record<string, WordKey>> = {
  // `answered` второй строки тоже не печатает: успеха нет вовсе (D11 §6.2).
  // `taken` больше не печатает вторую строку (D1 §7.3, 11.11.2026): новый текст `PhonePageTaken`
  // уже говорит «ничего делать не нужно», а `PhonePageTakenNext` повторял бы то же третьим утверждением.
  // Ключ остаётся в словарях и в `REUSED_WORD_KEYS` — удаление слова было бы отдельной правкой с C#.
  cancelled: "PhonePageCancelledNext",
  "no-answer": "PhonePageCancelledNext",
};

/**
 * Итог вопроса: «снят другим», «отменён», «не разобрался», «не ответили». Успеха здесь НЕТ
 * (D11 §6.2, DEF-147): карточка ушла — это и есть ответ на «ушло ли»; печатаются только
 * причины, по которым ответ НЕ ушёл, и живут они до нового вопроса (таймера нет).
 */
export function QuestionOutcomeBlock({ t, state }: { t: T; state: QuestionOutcome }) {
  if (state.kind === "none" || state.kind === "answered") return null;
  const fallback = OUTCOME_FALLBACK[state.kind] ?? "PhonePageCancelled";
  const message = state.message !== "" ? state.message : t(fallback);
  const next = OUTCOME_NEXT[state.kind];
  return (
    <section className="dp-question dp-question-done" role="status">
      <p>{message}</p>
      {next !== undefined ? <p className="dp-note">{t(next)}</p> : null}
    </section>
  );
}
