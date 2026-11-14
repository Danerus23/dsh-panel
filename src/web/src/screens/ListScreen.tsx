// Экран «Список сессий» (`/`). Одна колонка, строки-ссылки высотой ≥ 44, состояние — словом.
//
// Состояния (контракт §3.1): пусто · идёт (скелет, без спиннера) · ошибка · успех (обновляется сам) ·
// частичный («Показаны последние 50 из N») · «не прочиталось». Таймера и кнопки «Обновить»
// в успехе НЕТ (S4a): список перечитывается по возврату на вкладку, по возврату из сессии
// и по подсказке `stale` из уже открытого `$events` (R1 §5.2).
// S4a: в шапке — колокольчик «Ждут ответа» (счёт 0/1 — слот вопроса один, D1 §3).
//
// S2: строка сессии с висящим вопросом помечается «Ждёт ответа» ПО ОПРОСУ вопроса (2 с), а вопрос,
// который не удалось привязать к корневой сессии, показывается карточкой вверху списка.

import { useEffect } from "react";
import { IconChevronRight } from "@douyinfe/semi-icons";
import { useSessions } from "../hooks/useSessions";
import { useVisible } from "../hooks/visibility";
import { toRow, visibleCounts, visibleItems } from "../model/list";
import { QuestionCard } from "../components/QuestionCard";
import { StateMark } from "../components/StateMark";
import { WaitingBell } from "../components/WaitingBell";
import type { QuestionApi } from "../hooks/useQuestion";
import { formatTime, joinReason, type T } from "../words";
import type { Lang } from "../boot";

export type ListScreenProps = {
  t: T;
  lang: Lang;
  onOpen: (sessionId: string) => void;
  /** S3: «Новая задача» — переход на `/?new=1` (клиентский, сервер не зовётся). */
  onNew: () => void;
  /** Строка, на которую вернуть фокус после «К списку» (контракт §3.1). */
  focusSessionId: string | null;
  question: QuestionApi;
};

export function ListScreen({ t, lang, onOpen, onNew, focusSessionId, question }: ListScreenProps) {
  const visible = useVisible();
  const sessions = useSessions(visible);
  const pending = question.pending;
  const questionSessionId = pending !== null && pending.sessionId !== "" ? pending.sessionId : null;
  // Blank-сессии телефон не показывает, как DSH (D1 §6, 11.11.2026); вопрос — не пустота:
  // строка с висящим вопросом остаётся. Числа ниже считаются по ВИДИМЫМ строкам, а не по total.
  const rows =
    sessions.list === null
      ? []
      : visibleItems(sessions.list.items, questionSessionId).map((item) =>
          toRow(item, t, questionSessionId),
        );
  const counts = sessions.list === null ? null : visibleCounts(sessions.list, questionSessionId);

  // Колокольчик: счёт 0/1 от слота калитки, название строки — из списка, если оно там есть.
  const bell = {
    count: pending === null ? 0 : 1,
    sessionId: questionSessionId,
    sessionTitle:
      questionSessionId === null
        ? null
        : (sessions.list?.items.find((item) => item.sessionId === questionSessionId)?.title ??
          null),
    questionText: pending === null ? "" : (pending.cards[0]?.question ?? ""),
  };

  /** Вопрос без сессии: вести некуда — панель ведёт к карточке вверху списка (D1 §3.3). */
  const locateCard = () => {
    const card = document.querySelector<HTMLElement>("[data-dp-question]");
    if (card === null) return;
    card.scrollIntoView({ block: "center" });
    card
      .querySelector<HTMLElement>(
        "input, textarea, button, a[href], [tabindex]:not([tabindex='-1'])",
      )
      ?.focus();
  };

  // Возврат из сессии: фокус на ту же строку (контракт §3.1); строки больше нет — на заголовок.
  useEffect(() => {
    if (focusSessionId === null) return;
    const target = document.querySelector<HTMLElement>(`[data-session="${focusSessionId}"]`);
    (target ?? document.querySelector<HTMLElement>("h1"))?.focus();
  }, [focusSessionId, rows.length]);

  // ИСХОДОВ ВОПРОСА В СПИСКЕ НЕТ ВОВСЕ (D11 §6.2, DEF-147): ни успеха, ни «снят», ни
  // «отменён». Слово владельца 12.11.2026: «всплывающее окно в списке … вообще не нужно».
  // Карточка вопроса без сессии осталась — она не исход, а сам вопрос.

  return (
    <main className="dp-page">
      <header className="dp-head">
        <h1>{t("PhoneWebListTitle")}</h1>
        <div className="dp-head-row">
          {sessions.list !== null ? (
            <p className="dp-meta">{t("PhoneWebListCountFormat", counts?.count ?? 0)}</p>
          ) : null}
          <WaitingBell
            t={t}
            count={bell.count}
            sessionId={bell.sessionId}
            sessionTitle={bell.sessionTitle}
            questionText={bell.questionText}
            onOpenSession={onOpen}
            onLocateCard={locateCard}
          />
        </div>
        {counts !== null && counts.count > 0 && counts.truncated ? (
          <p className="dp-note">{t("PhoneWebListShownFormat", counts.count, counts.total)}</p>
        ) : null}
      </header>

      {/* S3: одна строка действия над списком — дверь есть всегда (и в пустом списке, и в отказе:
          заведение разговора от чтения списка не зависит, паспорт §3.1). */}
      <div className="dp-actions dp-list-new">
        <button type="button" className="dp-primary" onClick={onNew}>
          {t("PhoneWebTaskNew")}
        </button>
      </div>

      {pending !== null && pending.sessionId === "" ? (
        <QuestionCard
          key={pending.eventId}
          t={t}
          question={pending}
          onSend={question.send}
          onCancel={question.cancel}
          suspend={question.suspend}
        />
      ) : null}

      {sessions.error !== "" || (sessions.list === null && !sessions.loading) ? (
        <div className="dp-error-block" role="alert">
          <p>{joinReason(t("PhoneWebListUnreadable"), sessions.error)}</p>
          <div className="dp-actions">
            <button type="button" className="dp-primary" onClick={sessions.refresh}>
              {t("PhonePageRepeatButton")}
            </button>
          </div>
        </div>
      ) : null}

      {sessions.loading && sessions.list === null ? (
        <ul className="dp-skeleton" aria-hidden="true">
          <li />
          <li />
          <li />
        </ul>
      ) : null}

      {sessions.list !== null && rows.length === 0 ? (
        <div className="dp-empty">
          <h2>{t("PhoneWebListEmptyTitle")}</h2>
          <p>{t("PhoneWebListEmpty")}</p>
        </div>
      ) : null}

      {rows.length > 0 ? (
        <ul className="dp-list">
          {rows.map((row) => (
            <li key={row.item.sessionId}>
              <a
                className="dp-row"
                href={row.href}
                data-session={row.item.sessionId}
                onClick={(event) => {
                  // Ссылка остаётся ссылкой (глубокая ссылка, «открыть в новой вкладке»),
                  // но обычное нажатие ведёт без перезагрузки страницы.
                  if (event.metaKey || event.ctrlKey || event.shiftKey) return;
                  event.preventDefault();
                  onOpen(row.item.sessionId);
                }}
              >
                <span className="dp-row-main">
                  <span className="dp-row-title">{row.title}</span>
                  {/* Состояние: значок + СЛОВО (цвет — второй носитель, WCAG 1.4.1). */}
                  <span className="dp-row-meta">
                    {/* D7: знак вращается там же, где слово говорит «Работает» (тон `accent`) —
                        и в списке тоже: список живой (W5), слово отстаёт ≤3 с, знак вместе с ним. */}
                    <StateMark tone={row.tone} spin={row.tone === "accent"} />
                    <span>{row.state}</span>
                    {row.item.updatedAt > 0 ? (
                      <span>· {formatTime(row.item.updatedAt, lang)}</span>
                    ) : null}
                  </span>
                </span>
                {/* Справа — примета нажимаемости ДО касания (А-1): число задач и стрелка перехода. */}
                <span className="dp-row-side">
                  {row.inboxCount !== null ? (
                    <>
                      <span className="dp-badge" aria-hidden="true">
                        {row.inboxCount}
                      </span>
                      <span className="dp-visually-hidden">{row.inbox}</span>
                    </>
                  ) : null}
                  <IconChevronRight className="dp-row-chevron" aria-hidden="true" />
                </span>
              </a>
            </li>
          ))}
        </ul>
      ) : null}
    </main>
  );
}
