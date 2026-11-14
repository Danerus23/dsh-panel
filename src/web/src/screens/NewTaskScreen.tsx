// Экран «Новая задача» (`/?new=1`) — паспорт §3.2.
//
// Одна колонка, одна дверь (D2), никакого выбора папки: движок берёт свою рабочую папку, а телефону
// путь не уезжает никогда (контракт §3.2). После `200` с `sessionId` история ЗАМЕНЯЕТСЯ на
// `/?session=<id>` — отправленная форма израсходована, «назад» ведёт за неё, а не в неё
// (названное отступление вердикта t10 §2 п. 4; на отказе URL, текст и черновик целы).

import { useEffect, useRef, useState } from "react";
import { IconChevronLeft } from "@douyinfe/semi-icons";
import { DRAFT_NEW_KEY } from "../config";
import { useDraft } from "../hooks/useDraft";
import { useTask } from "../hooks/useTask";
import { TaskComposer, type ComposerStatus } from "../components/TaskComposer";
import type { T } from "../words";

export type NewTaskScreenProps = {
  t: T;
  onBack: () => void;
  /** `200` с `sessionId`: экран заменяет адрес и открывает заведённый разговор. */
  onCreated: (sessionId: string) => void;
};

export function NewTaskScreen({ t, onBack, onCreated }: NewTaskScreenProps) {
  const heading = useRef<HTMLHeadingElement>(null);
  const bar = useRef<HTMLElement | null>(null);
  const draft = useDraft(DRAFT_NEW_KEY);
  const [noSession, setNoSession] = useState(false);
  const created = useRef(false);

  const task = useTask(t, draft, { kind: "new" }, (answer) => {
    if (answer.sessionId === "") {
      // Дверь сказала «принято», но сессии не назвала — открывать нечего, говорим словами.
      setNoSession(true);
      return;
    }
    if (created.current) return;
    created.current = true;
    onCreated(answer.sessionId);
  });

  useEffect(() => {
    heading.current?.focus();
  }, []);

  const primaryLabel =
    (task.outcome.kind === "failed" && task.outcome.text === draft.text.trim()) || noSession
      ? ("PhonePageRepeatButton" as const)
      : ("PhoneWebTaskSend" as const);

  const status: ComposerStatus = (() => {
    if (noSession)
      return {
        words: t("PhoneTaskNewFailedFormat", t("PhoneTaskReasonNoConnection")),
        tone: "danger",
        inbox: null,
      };
    if (task.outcome.kind === "sending") return { words: "", tone: "quiet", inbox: null };
    if (task.outcome.kind === "created")
      return { words: task.outcome.words, tone: "ok", inbox: null };
    if (task.outcome.kind === "failed")
      return { words: task.outcome.words, tone: "danger", inbox: null };
    return { words: "", tone: "quiet", inbox: null };
  })();

  return (
    <main className="dp-page dp-page-composer">
      {/* «Назад» — ТОТ ЖЕ орган, что в сессии (D1 §8, слово владельца 11.11.2026): иконка-кнопка
          44×44, видимого слова нет — имя несёт `aria-label` (`PhoneWebBackToList`). Адрес `href="/"`
          остаётся ссылкой (глубокая ссылка, «открыть в новой вкладке»), а обычное нажатие ведёт
          без перезагрузки — как у строк списка. */}
      <header className="dp-head">
        <a
          className="dp-quiet dp-icon-button dp-head-back"
          href="/"
          aria-label={t("PhoneWebBackToList")}
          onClick={(event) => {
            if (event.metaKey || event.ctrlKey || event.shiftKey) return;
            event.preventDefault();
            onBack();
          }}
        >
          <IconChevronLeft aria-hidden="true" />
        </a>
      </header>

      <TaskComposer
        t={t}
        idPrefix="dp-new"
        headingLevel={1}
        headingKey="PhoneWebTaskNew"
        headingRef={heading}
        note={t("PhoneWebTaskNewNote")}
        draft={draft}
        sending={task.sending}
        status={status}
        onSend={task.send}
        primaryLabel={primaryLabel}
        stop={null}
        barRef={bar}
      />
    </main>
  );
}
