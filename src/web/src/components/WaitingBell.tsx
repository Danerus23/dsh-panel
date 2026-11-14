// Колокольчик «Ждут ответа» и его панель (S4a, слова и правила — `_team\s4a\d1-slova-vid.md`).
//
// Счёт честно 0/1: слот вопроса в калите ОДИН (`PhoneGate.cs:497`), второй вопрос заменяет первый
// с отменой хода. Счёт — про «ждут СЕЙЧАС», а не про «непрочитано»: ответил — счёт упал до 0.
//
// Правила вида (D1 §4):
//   * кнопка 44×44, значок `aria-hidden`, цифра в значке только при счёте > 0 («0» не печатается);
//   * слова счёта — в доступном имени кнопки И в `role="status"` (не `aria-live`: это не срочно);
//   * панель не `role="dialog"`, ловушки фокуса нет, скрима нет; Escape и клик вне закрывают;
//   * открыли — фокус в первую строку (при пустой панели — в заголовок);
//   * Escape возвращает фокус на колокольчик, клик вне фокус не крадёт.
//
// Вопрос без сессии не теряется (D1 §3.3): панель показывает строку-кнопку «Другой агент ждёт
// ответа → Открыть», которая ведёт к карточке вверху списка — сессии, куда вести, просто нет.

import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { IconBell, IconChevronRight } from "@douyinfe/semi-icons";
import type { T } from "../words";

const PANEL_ID = "dp-waiting-panel";
const TITLE_ID = "dp-waiting-title";
/** Обрывок вопроса одной строкой: полный текст — в карточке (D1 §4, `90` №17). */
const SNIPPET_MAX = 60;

export type WaitingBellProps = {
  t: T;
  /** 0 или 1: больше одного вопроса слот калитки не держит. */
  count: number;
  /** Корень вопроса; `null` — вопрос без сессии (карточка без строки). */
  sessionId: string | null;
  sessionTitle: string | null;
  questionText: string;
  onOpenSession: (sessionId: string) => void;
  onLocateCard: () => void;
};

function snippet(text: string): string {
  const trimmed = text.trim().replace(/\s+/gu, " ");
  if (trimmed === "") return "";
  return trimmed.length > SNIPPET_MAX ? `${trimmed.slice(0, SNIPPET_MAX - 1).trimEnd()}…` : trimmed;
}

export function WaitingBell({
  t,
  count,
  sessionId,
  sessionTitle,
  questionText,
  onOpenSession,
  onLocateCard,
}: WaitingBellProps) {
  const [open, setOpen] = useState(false);
  const [announced, setAnnounced] = useState("");
  const button = useRef<HTMLButtonElement | null>(null);
  const panel = useRef<HTMLDivElement | null>(null);
  const first = useRef<HTMLElement | null>(null);
  const seen = useRef(count);

  // Счёт изменился — говорим читалке. Первый рендер молчит: счёт уже назван именем кнопки.
  useEffect(() => {
    if (seen.current === count) return;
    seen.current = count;
    setAnnounced(count > 0 ? t("PhoneWebWaitingCountFormat", count) : t("PhoneWebWaitingEmpty"));
  }, [count, t]);

  // Открыли панель — фокус в первую строку; пустая панель отдаёт фокус заголовку.
  useEffect(() => {
    if (!open) return;
    const target = first.current ?? panel.current?.querySelector<HTMLElement>("h2") ?? null;
    target?.focus();
  }, [open]);

  // Клик вне панели закрывает; фокус остаётся там, куда нажали (`60` §8).
  useEffect(() => {
    if (!open) return;
    const onDown = (event: PointerEvent) => {
      const node = event.target as Node | null;
      if (node === null) return;
      if (panel.current?.contains(node) === true) return;
      if (button.current?.contains(node) === true) return;
      setOpen(false);
    };
    document.addEventListener("pointerdown", onDown);
    return () => document.removeEventListener("pointerdown", onDown);
  }, [open]);

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== "Escape" || !open) return;
    event.stopPropagation();
    setOpen(false);
    button.current?.focus();
  };

  const close = () => setOpen(false);
  const text = snippet(questionText);
  const label = count > 0 ? t("PhoneWebWaitingCountFormat", count) : t("PhoneWebWaitingTitle");

  return (
    <div className="dp-waiting" onKeyDown={onKeyDown}>
      <button
        type="button"
        ref={button}
        className="dp-quiet dp-icon-button dp-waiting-button"
        aria-label={label}
        aria-expanded={open}
        aria-controls={PANEL_ID}
        onClick={() => setOpen((value) => !value)}
      >
        <IconBell aria-hidden="true" />
        {count > 0 ? (
          <span className="dp-badge dp-waiting-badge" aria-hidden="true">
            {count}
          </span>
        ) : null}
      </button>

      {open ? (
        <div className="dp-waiting-panel" id={PANEL_ID} ref={panel} aria-labelledby={TITLE_ID}>
          <h2 className="dp-waiting-title" id={TITLE_ID} tabIndex={-1}>
            {t("PhoneWebWaitingTitle")}
          </h2>

          {count === 0 ? (
            <p className="dp-note dp-waiting-empty">{t("PhoneWebWaitingEmpty")}</p>
          ) : sessionId !== null ? (
            // Строка сессии — НАСТОЯЩАЯ ссылка: обычное нажатие ведёт без перезагрузки,
            // Ctrl/Cmd/Shift — браузер открывает новую вкладку (D1 §3.4).
            <a
              className="dp-waiting-row"
              href={`/?session=${encodeURIComponent(sessionId)}`}
              ref={(node) => {
                first.current = node;
              }}
              onClick={(event) => {
                if (event.metaKey || event.ctrlKey || event.shiftKey) return;
                event.preventDefault();
                close();
                onOpenSession(sessionId);
              }}
            >
              <span className="dp-waiting-main">
                <span className="dp-waiting-name">{sessionTitle ?? t("PhoneWebUntitled")}</span>
                <span className="dp-waiting-text">
                  {text !== "" ? text : t("PhoneWebStateWaiting")}
                </span>
              </span>
              <IconChevronRight className="dp-row-chevron" aria-hidden="true" />
            </a>
          ) : (
            // Вопрос без сессии: вести некуда — кнопка к карточке вверху списка (D1 §3.3).
            <button
              type="button"
              className="dp-waiting-row"
              ref={(node) => {
                first.current = node;
              }}
              onClick={() => {
                close();
                onLocateCard();
              }}
            >
              <span className="dp-waiting-main">
                <span className="dp-waiting-name">{t("PhoneWebQuestionElsewhere")}</span>
                <span className="dp-waiting-text">{t("PhoneWebOpen")}</span>
              </span>
              <IconChevronRight className="dp-row-chevron" aria-hidden="true" />
            </button>
          )}
        </div>
      ) : null}

      <span className="dp-visually-hidden" role="status">
        {announced}
      </span>
    </div>
  );
}
