// ВВОД ЗАДАЧИ: зацеп шторки снизу + поднятый лист (D11 §2, слово владельца 12.11.2026: «избавиться
// полностью от шарика и перейти на всплывающую шторку снизу вызываемую зацепом и свайпом вверх»).
// ШАРИКА БОЛЬШЕ НЕТ: ни компонента, ни перетаскивания, ни записи позиции, ни клампа пары (DEF-148
// снимается вместе с шаром; DEF-126 «короткий медленный жест» — тоже — чинить нечего).
//
// Почему так, а не «поле в потоке»: лента S1 держится за живой конец (`nearBottom`,
// `scrollTo(0, scrollHeight)`), а человек формулирует задачу именно там. Свёрнутое состояние отвечает
// «я здесь» полосой-зацепом во всю ширину: промахнуться нельзя, и она же читается как край шторки.
//
// Правила, которые держатся здесь:
//   * свёрнутое состояние — `.dp-bottom` (портал в `body`): ручка 40×4 + шеврон ВВЕРХ, высота 44,
//     цель во всю ширину, имя `PhoneWebTaskTitle`, `aria-expanded`/`aria-controls`. Строка исхода —
//     `.dp-handle-note` НАД зацепом (успех и счёт гаснут за 5 с, отказ не гаснет);
//   * лист открывают ТАП и СВАЙП ВВЕРХ ≥ 24 px (порог — наше число, D11 §2.2; жест — ВТОРОЙ путь,
//     первый — видимый зацеп: скрытых жестов не заводим);
//   * лист закрывают «свернуть», свайп ВНИЗ ≥ 64 px по его верхней полосе, `Esc` и «назад»;
//     `prefers-reduced-motion` — без следования за пальцем и без анимации;
//   * высота `--dp-sheet-h` — СВОЯ переменная поднятого листа (иначе страница прыгала бы на его высоту
//     в момент раскрытия); запас же снизу (`.dp-bottom`) измеряет экран сессии — `useBottomReserve`;
//   * поднятый лист берёт `--dp-keyboard-inset` и `100dvh` (DEF-089: `bottom: 0` layout-вьюпорта
//     уводил поле под клавиатуру);
//   * фокус: раскрытие — в поле, закрытие — обратно на зацеп; страница под скримом инертна
//     (`inert` ставит экран сессии через `onSheetOpenChange`);
//   * Enter в поле — перенос строки, а не отправка (`40-forms.md` §9); черновик и его ключи не меняются.
//
// «Новая задача» (`/?new=1`) — тот же компонент В ПОТОКЕ (`mode="inline"`): там поле и есть страница,
// и прятать его под лист значило бы показать человеку пустой экран.

import {
  useEffect,
  useRef,
  useState,
  type PointerEvent as ReactPointerEvent,
  type RefObject,
} from "react";
import { createPortal } from "react-dom";
import { IconChevronDown, IconChevronUp } from "@douyinfe/semi-icons";
import { useKeyboardInset } from "../hooks/useKeyboardInset";
import { TASK_TEXT_MAX } from "../config";
import { checkTaskText } from "../model/task";
import type { DraftApi } from "../hooks/useDraft";
import type { T } from "../words";

export type ComposerStatus = {
  /** Слова строки исхода ("" — строки нет, слот пустой, но своего места не отдаёт). */
  words: string;
  /** `ok` — свершившийся факт; `quiet` — «идёт»/«уже не работает»; `danger` — отказ. */
  tone: "quiet" | "ok" | "danger";
  /** «В очереди: N» — только когда очередь прочитана; иначе строки нет. */
  inbox: number | null;
};

export type StopPanel = {
  warning: string;
  label: string;
  busy: boolean;
  onStop: () => void;
};

export type TaskComposerProps = {
  t: T;
  /** Префикс `id` полей — на экране сессии и на «Новой задаче» они разные. */
  idPrefix: string;
  /** Заголовок блока: `h1` на экране «Новая задача» (`PhoneWebTaskNew`), `h2` в сессии. */
  headingLevel: 1 | 2;
  /** Пояснение под заголовком (только экран «Новая задача» — `PhoneWebTaskNewNote`). */
  note: string | null;
  draft: DraftApi;
  sending: boolean;
  status: ComposerStatus;
  onSend: () => void;
  /**
   * Ярлык главной кнопки: `PhonePageRepeatButton`, когда текст не менялся после отказа (это ПОВТОР
   * той же попытки с тем же `requestId`), иначе `PhoneWebTaskSend`.
   */
  primaryLabel: "PhoneWebTaskSend" | "PhonePageRepeatButton";
  /** Каким словом назван блок: `PhoneWebTaskTitle` в сессии, `PhoneWebTaskNew` на новом экране. */
  headingKey: "PhoneWebTaskTitle" | "PhoneWebTaskNew";
  /** Строка стопа: только экран сессии и только пока ход идёт и нет карточки вопроса. */
  stop: StopPanel | null;
  /** Ссылка на секцию в потоке — только для `inline` («Новая задача»); в режиме шторки не нужна. */
  barRef?: RefObject<HTMLElement | null>;
  /** Заголовок блока — точка входа читалки: экран ставит сюда фокус (F-A, L4/R3). */
  headingRef?: RefObject<HTMLHeadingElement | null>;
  /** `sheet` — сессия: зацеп внизу + поднятый лист; `inline` — «Новая задача»: полоса в потоке. */
  mode?: "sheet" | "inline";
  /** Открытость поднятого листа: экран делает остальную страницу инертной. */
  onSheetOpenChange?: (open: boolean) => void;
  /** Отправка принята (D1/D2): лист закрывается сам — человеку больше нечего вводить.
   *  Отказ сюда НЕ приходит: текст и лист остаются на месте (слово владельца 11.11.2026). */
  accepted?: boolean;
};

/** Тело ввода без обвязки — одно и то же в полосе «Новой задачи» и в поднятом листе. */
type ComposerBodyProps = Omit<TaskComposerProps, "barRef" | "mode" | "onSheetOpenChange">;

/** Порог свайпа ВВЕРХ по зацепу (D11 §2.2 — нормы нет, число наше). */
const SWIPE_UP_PX = 24;
/** |dx| больше этого — жест горизонтальный (строка ленты, карусель): лист он не открывает. */
const SWIPE_AXIS_PX = 24;
/** Порог свайпа ВНИЗ по верхней полосе листа (D11 §2.2 — тоже наше число). */
const SWIPE_DOWN_PX = 64;
/** Выезд/возврат листа — одно число D4 («90» №31: орган 150 мс). */
const SHEET_MOTION_MS = 150;

/** `prefers-reduced-motion: reduce` — лист не следует за пальцем и не анимируется (D11 §2.2, `80` Ш-40). */
function reducedMotion(): boolean {
  return window.matchMedia("(prefers-reduced-motion: reduce)").matches;
}

function ComposerBody({
  t,
  idPrefix,
  headingLevel,
  headingKey,
  headingRef,
  note,
  draft,
  sending,
  status,
  onSend,
  primaryLabel,
  stop,
}: ComposerBodyProps) {
  const check = checkTaskText(draft.text, sending);
  const Heading = headingLevel === 1 ? "h1" : "h2";
  const inputId = `${idPrefix}-task`;
  const counterId = `${idPrefix}-task-counter`;
  const statusId = `${idPrefix}-task-status`;
  const reason = check.empty
    ? t("PhoneTaskEmpty")
    : check.tooLong
      ? t("PhoneTaskTooLongFormat", TASK_TEXT_MAX)
      : sending
        ? t("PhoneTaskBusy")
        : "";

  // Клавиатура телефона: лист над ней и возврат прокрутки, если браузер дёрнул её при наборе.
  // Хук живёт здесь, а не в листе: он монтируется вместе с полем и снимает переменную за собой.
  const fieldRef = useRef<HTMLTextAreaElement | null>(null);
  useKeyboardInset(fieldRef);

  // ПОЛЕ РАСТЁТ ПО СОДЕРЖИМОМУ до потолка в 6 строк (R2, слово владельца 11.11.2026: «поле узкое при
  // длинном тексте»). Потолок считается из НАСТОЯЩИХ line-height и отбивок поля, а не из магии;
  // выше потолка поле скроллится само, а лист (flex + max-height:100dvh) растёт вместе с полем,
  // и `--dp-sheet-h` пересчитывается тем же ResizeObserver, что и раньше.
  const MAX_INPUT_ROWS = 6;
  useEffect(() => {
    const field = fieldRef.current;
    if (field === null) return;
    const style = window.getComputedStyle(field);
    const line = Number.parseFloat(style.lineHeight) || 22;
    const padding =
      (Number.parseFloat(style.paddingTop) || 0) + (Number.parseFloat(style.paddingBottom) || 0);
    const border =
      (Number.parseFloat(style.borderTopWidth) || 0) +
      (Number.parseFloat(style.borderBottomWidth) || 0);
    const max = Math.round(line * MAX_INPUT_ROWS + padding + border);
    field.style.height = "auto";
    const content = field.scrollHeight + border;
    field.style.height = `${Math.min(content, max)}px`;
    field.style.overflowY = content > max ? "auto" : "hidden";
  }, [draft.text]);

  return (
    <>
      {/* Заголовок блока. В сессии (h2) он СКРЫТ визуально: блок назван подписью поля «Что сделать»,
          а лишняя строка стоит бюджета. На «Новой задаче» (h1) он ВИДИМ — это имя страницы. */}
      <Heading
        className={headingLevel === 2 ? "dp-visually-hidden" : undefined}
        id={`${idPrefix}-task-title`}
        ref={headingRef}
        // F-A (L4/R3): заголовок получает фокус программно — без `tabIndex={-1}` `focus()` на нём
        // ничего не делает (нефокусируемый элемент), и на «Новой задаче» фокус оставался в `body`.
        tabIndex={-1}
      >
        {t(headingKey)}
      </Heading>

      {note !== null ? <p className="dp-note">{note}</p> : null}

      {/* Подпись поля и счётчик — одной строкой: обе нужны ДО набора. */}
      <p className="dp-composer-meta">
        <label className="dp-composer-label" htmlFor={inputId}>
          {t("PhoneWebTaskLabel")}
        </label>
        <span className="dp-composer-counter" id={counterId}>
          {t("PhoneWebTaskCounterFormat", check.length)}
        </span>
      </p>

      {draft.restored ? (
        <p className="dp-composer-draft">
          <span>{t("PhoneWebDraftRestored")}</span>
          <button
            type="button"
            className="dp-link-button"
            onClick={() => {
              draft.discard();
              fieldRef.current?.focus();
            }}
          >
            {t("PhoneWebDraftDiscard")}
          </button>
        </p>
      ) : null}

      <textarea
        id={inputId}
        className="dp-textarea dp-composer-input"
        ref={fieldRef}
        rows={1}
        value={draft.text}
        aria-describedby={`${counterId} ${statusId}`}
        onChange={(event) => draft.setText(event.target.value)}
      />

      {/* Подписи-комментария «Текст уйдёт на компьютер» НЕТ: слово владельца 10.11.2026 —
          пояснение лишнее, карточка 32a (05a/05b) требует «строки нет». Функции понятны сами. */}

      {/* СТРОКА ЦЕНЫ И ИСХОДА — перед рядом кнопок (ремонт t14, F-1): цену стопа человек читает ДО
          нажатия (`60-states.md` §4а). Высота фиксирована, содержимое меняется. */}
      <p
        className={
          status.tone === "danger" ? "dp-composer-slot dp-composer-slot-danger" : "dp-composer-slot"
        }
        id={statusId}
        role="status"
      >
        {status.words !== "" ? (
          <span>{status.words}</span>
        ) : stop !== null ? (
          <span className="dp-composer-warning">{stop.warning}</span>
        ) : reason !== "" ? (
          <span className="dp-composer-reason">{reason}</span>
        ) : null}
        {status.inbox !== null ? (
          <>
            {" "}
            <span className="dp-composer-inbox">{t("PhoneWebInboxFormat", status.inbox)}</span>
          </>
        ) : null}
      </p>

      {/* Ряд кнопок — РОВНО ОДИН ряд: стоп (когда ход идёт) и отправка/повтор. */}
      <div className="dp-actions dp-composer-actions">
        {stop !== null ? (
          <button
            type="button"
            className="dp-quiet dp-composer-stop-button"
            aria-disabled={stop.busy}
            onClick={stop.busy ? undefined : stop.onStop}
          >
            {stop.label}
          </button>
        ) : null}
        <button
          type="button"
          className="dp-primary dp-composer-send"
          disabled={!check.canSend}
          onClick={check.canSend ? onSend : undefined}
        >
          {t(primaryLabel)}
        </button>
      </div>

      {draft.unavailable ? <p className="dp-note">{t("PhoneWebDraftUnavailable")}</p> : null}
    </>
  );
}

/**
/**
 * ЗАЦЕП ШТОРКИ (D11 §2.2, DEF-148): нижняя полоса во всю ширину — ПОСЛЕДНИЙ орган нижнего стека.
 *
 * Сам стек (`.dp-bottom`) — не его: его держит экран (`BottomStack`), чтобы над зацепом вставали
 * очередь (W12) и вопрос (W10) ОДНИМ контейнером и в порядке разметки. Зацеп отвечает только за себя.
 *
 * 44 высотой, ручка 40×4 и `IconChevronUp` 18 — «кусочек со стрелкой, что это тянется» (слово
 * владельца 12.11.2026). Видимого слова нет: имя несёт `aria-label` существующего ключа.
 * Тап и свайп ВВЕРХ ≥ 24 px открывают лист; строка исхода живёт НАД зацепом абсолютно, поэтому
 * не меняет высоту стека (запас снизу — одно измеренное число, D11 §1.2).
 *
 * Координат здесь больше нет вовсе: полоса прибита к низу (`fixed` + `--dp-vv-inset`), поэтому
 * адресная строка и клавиатура не могут увести её за край, а прокрутка не может её сдвинуть —
 * пересчитывать нечего (R2: шарик дрожал именно на пересчётах).
 */
function Handle({
  t,
  note,
  noteTone,
  controls,
  handleRef,
  onOpen,
}: {
  t: T;
  note: string;
  noteTone: string;
  /** `id` листа, который зацеп раскрывает — для `aria-controls`. */
  controls: string;
  handleRef: RefObject<HTMLButtonElement | null>;
  onOpen: () => void;
}) {
  const from = useRef<{ x: number; y: number } | null>(null);
  /** Свайп уже открыл лист: следующий за ним `click` — не второе нажатие. */
  const swiped = useRef(false);

  const down = (event: ReactPointerEvent<HTMLButtonElement>) => {
    from.current = { x: event.clientX, y: event.clientY };
    swiped.current = false;
    // Захват указателя — удобство, а не условие жеста: синтетическое событие (проверка) приходит с
    // неактивным `pointerId`, и браузер вправе отказаться — жест от этого не ломается.
    try {
      event.currentTarget.setPointerCapture(event.pointerId);
    } catch {
      // Захвата нет — pointerup всё равно придёт на тот же орган.
    }
  };

  const up = (event: ReactPointerEvent<HTMLButtonElement>) => {
    const start = from.current;
    from.current = null;
    if (start === null) return;
    const dy = event.clientY - start.y;
    const dx = event.clientX - start.x;
    // Горизонтальный жест — не наш: лист открывает только движение вверх.
    if (dy <= -SWIPE_UP_PX && Math.abs(dx) <= SWIPE_AXIS_PX) {
      swiped.current = true;
      onOpen();
    }
  };

  return (
    <>
      {note !== "" ? (
        <p
          // Строка исхода — НАД зацепом, вне потока: она не двигает ленту и не меняет запас снизу.
          className={`dp-handle-note dp-handle-note-${noteTone}`}
          role="status"
        >
          {note}
        </p>
      ) : null}
      <button
        type="button"
        className="dp-handle"
        ref={handleRef}
        aria-label={t("PhoneWebTaskTitle")}
        aria-expanded={false}
        aria-controls={controls}
        onPointerDown={down}
        onPointerUp={up}
        onPointerCancel={() => {
          from.current = null;
        }}
        onClick={() => {
          // Свайп вверх — не нажатие: лист уже открыт, второй раз его не раскрываем.
          if (swiped.current) {
            swiped.current = false;
            return;
          }
          onOpen();
        }}
      >
        <span className="dp-handle-grip" aria-hidden="true" />
        <IconChevronUp className="dp-handle-chevron" aria-hidden="true" />
      </button>
    </>
  );
}

export function TaskComposer({
  t,
  idPrefix,
  headingLevel,
  headingKey,
  headingRef,
  note,
  draft,
  sending,
  status,
  onSend,
  primaryLabel,
  stop,
  barRef,
  mode = "inline",
  onSheetOpenChange,
  accepted = false,
}: TaskComposerProps) {
  const sheetId = `${idPrefix}-sheet`;
  const [open, setOpen] = useState(false);
  /** Лист уезжает 150 мс (D4): DOM снимаем после анимации, а не мгновенно. */
  const [closing, setClosing] = useState(false);
  const closingRef = useRef(false);
  const closeTimer = useRef<number | null>(null);
  const handleRef = useRef<HTMLButtonElement | null>(null);
  const sheetRef = useRef<HTMLDivElement | null>(null);
  const pushed = useRef(false);
  /** Открыт ли лист — для СТАБИЛЬНЫХ слушателей: подписка не должна зависеть от рендера. */
  const openRef = useRef(false);
  /** Текущий колбэк экрана — в ref, чтобы слушатель не перевешивался на каждом рендере. */
  const notifyRef = useRef(onSheetOpenChange);

  const body = (
    <ComposerBody
      t={t}
      idPrefix={idPrefix}
      headingLevel={headingLevel}
      headingKey={headingKey}
      headingRef={headingRef}
      note={note}
      draft={draft}
      sending={sending}
      status={status}
      onSend={onSend}
      primaryLabel={primaryLabel}
      stop={stop}
    />
  );

  /** Вернуть фокус на зацеп: он появляется только после снятия листа — пробуем несколько кадров. */
  const focusHandle = (attempt = 0) => {
    const node = handleRef.current;

    if (node !== null) {
      node.focus();
      if (document.activeElement === node) return;
    }

    if (attempt < 10) window.setTimeout(() => focusHandle(attempt + 1), 20);
  };

  // ── СВАЙП ВНИЗ ПО ВЕРХНЕЙ ПОЛОСЕ ЛИСТА (D11 §2.2): тянем за «свернуть» — лист идёт за пальцем —
  // отпустили ниже порога — возврат 150 мс, выше — тот же выезд, что у кнопки (`closeSheet(false)`).
  // Двигается `translate` (отдельное свойство от `transform`), а не `transform`: въезд листа
  // анимирует `transform`, и общая у них одна дорога — жест спорил бы с анимацией.
  const sheetDrag = useRef<{ y: number } | null>(null);
  /** Палец сдвинулся: значит это свайп, а не нажатие «свернуть» — `click` закрывать не должен. */
  const sheetMoved = useRef(false);

  const sheetDown = (event: ReactPointerEvent<HTMLButtonElement>) => {
    sheetDrag.current = { y: event.clientY };
    sheetMoved.current = false;
    try {
      event.currentTarget.setPointerCapture(event.pointerId);
    } catch {
      // Захвата нет — движение и отпускание придут на тот же орган.
    }
  };

  const sheetMove = (event: ReactPointerEvent<HTMLButtonElement>) => {
    const start = sheetDrag.current;
    const node = sheetRef.current;
    if (start === null || node === null) return;
    const dy = Math.max(0, event.clientY - start.y);
    if (dy > 2) sheetMoved.current = true;
    // При `reduce` лист за пальцем НЕ следует (D11 §2.2, `80` Ш-40): жест закрывает сразу.
    if (reducedMotion()) return;
    node.style.transition = "none";
    node.style.translate = `0 ${dy}px`;
  };

  const sheetUp = (event: ReactPointerEvent<HTMLButtonElement>) => {
    const start = sheetDrag.current;
    sheetDrag.current = null;
    const node = sheetRef.current;
    if (start === null) return;
    const dy = Math.max(0, event.clientY - start.y);
    if (node !== null) node.style.translate = "";

    if (dy >= SWIPE_DOWN_PX) {
      closeSheet(false);
      return;
    }
    if (node === null || reducedMotion()) {
      node?.style.removeProperty("transition");
      return;
    }
    // Возврат тем же 150 мс — через `translate`, чтобы не спорить с анимацией въезда.
    node.style.transition = `translate ${SHEET_MOTION_MS}ms var(--dp-ease)`;
    node.style.translate = "0 0px";
    window.setTimeout(() => {
      if (!node.isConnected) return;
      node.style.removeProperty("transition");
      node.style.removeProperty("translate");
    }, SHEET_MOTION_MS);
  };

  const hideSheet = () => {
    if (closingRef.current) return;

    closingRef.current = true;
    openRef.current = false;
    setClosing(true);
    notifyRef.current?.(false);
    // Страница сразу возвращает свою геометрию: лист уже уезжает, но место в раскладке и
    // `scroll-padding` больше не его — иначе замеры после закрытия ловили бы полпути анимации.
    document.documentElement.classList.remove("dp-sheet-open");
    document.documentElement.style.removeProperty("--dp-sheet-h");

    // Фокус возвращаем только если он был в листе: если экран уже увёл его (очередь после
    // приёма задачи), фокус не отбираем. Зацеп появляется только после снятия листа — ждём кадр.
    closeTimer.current = window.setTimeout(() => {
      const active = document.activeElement;
      const restore =
        active === null ||
        active === document.body ||
        (sheetRef.current !== null && sheetRef.current.contains(active));

      closeTimer.current = null;
      closingRef.current = false;
      setClosing(false);
      setOpen(false);
      if (restore) window.setTimeout(focusHandle, 0);
    }, 150);
  };

  const closeSheet = (viaHistory: boolean) => {
    if (!viaHistory && pushed.current) {
      pushed.current = false;
      // Наша запись в истории снимается: «назад» после закрытия ведёт туда, куда вело раньше.
      window.history.back();
    }
    hideSheet();
  };
  const openSheet = () => {
    // Повторное открытие в момент выезда: анимацию отменяем, лист остаётся на месте.
    if (closeTimer.current !== null) {
      window.clearTimeout(closeTimer.current);
      closeTimer.current = null;
    }
    closingRef.current = false;
    setClosing(false);
    // Открыли лист — строку исхода прячем (её причина уже не важна, человек действует).
    setNoteVisible(false);
    openRef.current = true;
    setOpen(true);
    onSheetOpenChange?.(true);
    pushed.current = true;
    window.history.pushState({ dpSheet: 1 }, "");
    window.requestAnimationFrame(() => {
      sheetRef.current?.querySelector<HTMLTextAreaElement>("textarea")?.focus();
    });
  };

  // Сам колбэк экрана обновляем после каждого рендера — слушатель читает его из ref-а.
  useEffect(() => {
    notifyRef.current = onSheetOpenChange;
  }, [onSheetOpenChange]);

  // Таймер выезда листа снимается при уходе экрана: иначе `setState` после размонтирования.
  useEffect(
    () => () => {
      if (closeTimer.current !== null) window.clearTimeout(closeTimer.current);
    },
    [],
  );

  // ПРИНЯТАЯ ОТПРАВКА ЗАКРЫВАЕТ ЛИСТ САМА (слово владельца 11.11.2026: «задача ушла, а лист остался
  // открыт»). Черновик к этому моменту уже очищен (`clearOnAccepted`), человеку вводить нечего;
  // фокус уходит на шеврон. Отказ сюда НЕ приходит — лист и текст остаются на месте.
  useEffect(() => {
    if (!accepted || !openRef.current) return;
    // closeSheet читает только ref-ы и setState — старая замыканием безопасна.
    closeSheet(false);
  }, [accepted]);
  // «Назад» системы/браузера закрывает лист; Esc — тоже. Свёрнутый лист Esc не слушает.
  //⚠️ Подписка СТАБИЛЬНАЯ (пустые зависимости): App тоже слушает popstate и своим ре-рендером снимал
  // наш слушатель до того, как его вызывал браузер (L1, F-L1-1: «назад» не закрывал лист).
  useEffect(() => {
    const onPop = () => {
      if (!pushed.current) return;
      pushed.current = false;
      hideSheet();
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || !openRef.current) return;
      event.preventDefault();
      closeSheet(false);
    };
    window.addEventListener("popstate", onPop);
    window.addEventListener("keydown", onKey);
    return () => {
      window.removeEventListener("popstate", onPop);
      window.removeEventListener("keydown", onKey);
    };
    // hideSheet/closeSheet читают только ref-ы и setState — им можно жить со старой замыканием.
  }, []);

  // Высота поднятого листа — СВОЯ переменная (`--dp-sheet-h`); запас снизу меряет экран сессии.
  useEffect(() => {
    if (!open) return;
    const node = sheetRef.current;
    const root = document.documentElement;
    root.classList.add("dp-sheet-open");
    const apply = () => {
      if (node === null) return;
      root.style.setProperty("--dp-sheet-h", `${node.offsetHeight}px`);
    };
    apply();
    const observer = new ResizeObserver(apply);
    if (node !== null) observer.observe(node);
    return () => {
      observer.disconnect();
      root.classList.remove("dp-sheet-open");
      root.style.removeProperty("--dp-sheet-h");
    };
  }, [open]);

  // Фокус не должен уехать за лист: Tab идёт по кругу внутри поверхности (страница под скримом инертна).
  useEffect(() => {
    if (!open) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== "Tab") return;
      const node = sheetRef.current;
      if (node === null) return;
      const items = [
        ...node.querySelectorAll<HTMLElement>("button, textarea, a[href], input"),
      ].filter((item) => !item.hasAttribute("disabled") && item.tabIndex !== -1);
      if (items.length === 0) return;
      const first = items[0]!;
      const last = items[items.length - 1]!;
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open]);

  // Что сказано в СТРОКЕ ИСХОДА (живёт над зацепом): слово исхода, иначе счёт очереди — одной строкой.
  const summary =
    status.words !== ""
      ? status.words
      : status.inbox !== null
        ? t("PhoneWebInboxFormat", status.inbox)
        : "";

  // Строка исхода: УСПЕХ и счёт гаснут сами (~5 с) или при следующем действии; ОТКАЗ — не гаснет, пока
  // человек не откроет лист или не нажмёт что-то (`05-triggers` H-14: исчезающий отказ = человек без
  // причины). Открытие листа прячет любую строку — человек уже делает дело.
  const [noteVisible, setNoteVisible] = useState(false);
  useEffect(() => {
    if (summary === "") {
      setNoteVisible(false);
      return;
    }
    setNoteVisible(true);
    if (status.tone !== "ok" && status.tone !== "quiet") return;
    const timer = window.setTimeout(() => setNoteVisible(false), 5000);
    return () => window.clearTimeout(timer);
  }, [summary, status.tone]);
  // «Новая задача»: поле и есть страница — лист не поднимается, зацепа нет.
  if (mode === "inline") {
    return (
      <section className="dp-composer" aria-labelledby={`${idPrefix}-task-title`} ref={barRef}>
        {body}
      </section>
    );
  }

  return (
    <>
      {/* СВЁРНУТОЕ СОСТОЯНИЕ — ЗАЦЕП ШТОРКИ (D11 §2.2, слово владельца 12.11.2026): полоса во всю
          ширину с ручкой и шевроном ВВЕРХ. Имя — существующий ключ «Задача агенту» (имя поля — другое
          слово); `aria-controls` указывает на лист, который зацеп раскрывает; `aria-expanded` — ложь,
          пока листа нет. Пока лист поднят, зацепа на экране нет вовсе. */}
      {!open ? (
        <Handle
          t={t}
          note={noteVisible ? summary : ""}
          noteTone={status.tone}
          controls={sheetId}
          handleRef={handleRef}
          onOpen={openSheet}
        />
      ) : null}

      {open
        ? createPortal(
            <div className="dp-sheet-root">
              <div
                className="dp-sheet-scrim"
                aria-hidden="true"
                onClick={() => closeSheet(false)}
              />
              <div
                className={`dp-sheet${closing ? " dp-sheet-closing" : ""}`}
                id={sheetId}
                role="dialog"
                aria-modal="true"
                aria-labelledby={`${idPrefix}-task-title`}
                ref={sheetRef}
              >
                {/* ВЕРХНЯЯ ПОЛОСА ЛИСТА — «свернуть» и единственная ручка свайпа вниз (D11 §2.2):
                    отпустили выше 64 px — лист закрыт, ниже — вернулся за 150 мс. */}
                <button
                  type="button"
                  className="dp-quiet dp-icon-button dp-sheet-collapse"
                  aria-label={t("PhoneWebTaskTitle")}
                  aria-expanded={true}
                  aria-controls={sheetId}
                  onPointerDown={sheetDown}
                  onPointerMove={sheetMove}
                  onPointerUp={sheetUp}
                  onPointerCancel={() => {
                    sheetDrag.current = null;
                    const node = sheetRef.current;
                    if (node !== null) node.style.translate = "";
                  }}
                  onClick={() => {
                    // Свайп — не нажатие: если палец ехал, закрытие уже решено порогом (`sheetUp`).
                    if (sheetMoved.current) {
                      sheetMoved.current = false;
                      return;
                    }
                    closeSheet(false);
                  }}
                >
                  <IconChevronDown aria-hidden="true" />
                </button>
                {body}
              </div>
            </div>,
            document.body,
          )
        : null}
    </>
  );
}
