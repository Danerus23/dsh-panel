// Экран «Сессия и живой поток» (`/?session=<id>`). Вопрос — СОСТОЯНИЕ этого экрана (S2),
// отдельного экрана вопроса в новом пульте нет.
//
// Состояния (контракт §3.2): пусто · идёт (подключаюсь) · ошибка (сессия не открылась) · успех ·
// занято («Показать раньше» гаснет) · частичный (hasMore → кнопка, не бесконечная лента) ·
// «не прочиталось» · нет связи (строка «связь оборвалась», лента НЕ стирается).
//
// S2: карточка вопроса стоит НАД лентой (после «Показать раньше» — порядок обхода Tab по контракту
// §3.2), вопрос чужой сессии — строкой со ссылкой, итог ответа — на месте карточки.
//
// S3 (вердикт t10 §1.3 — ГЛАВНЕЕ паспорта там, где расходится). Порядок блоков потока:
// шапка → «Показать раньше» → карточка вопроса (итог) → «Очередь» → лента → ПОЛОСА ЗАДАЧИ.
// Полоса — последний ребёнок `.dp-page`, `position: sticky; bottom: 0` ВНУТРИ потока: лента S1
// держится за живой конец, и главное действие S3 обязано быть там же, вне прокрутки (`40-forms.md` §7).
// «Остановить» живёт в той же полосе, отдельного блока «Текущий ход» нет, и дверь стопа одна:
// пока видна карточка вопроса S2, строки стопа нет целиком (у вопроса своя дверь отмены хода).

import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { IconArrowDown, IconArrowUp, IconChevronLeft } from "@douyinfe/semi-icons";
import { useHiddenLong, useVisible } from "../hooks/visibility";
import { useVisualViewportInset } from "../hooks/useVisualViewportInset";
import { useSessions } from "../hooks/useSessions";
import { liveFailureWords, useLive } from "../hooks/useLive";
import { useDraft } from "../hooks/useDraft";
import { useQueue } from "../hooks/useQueue";
import { useQueueRemove } from "../hooks/useQueueRemove";
import { useStop, type StopOutcome } from "../hooks/useStop";
import { useTask } from "../hooks/useTask";
import { useBottomReserve } from "../hooks/useBottomReserve";
import type { QuestionApi } from "../hooks/useQuestion";
import { QuestionCard, QuestionOutcomeBlock } from "../components/QuestionCard";
import { TaskComposer, type ComposerStatus, type StopPanel } from "../components/TaskComposer";
import { BottomStack } from "../components/BottomStack";
import { QueueSection } from "../components/QueueSection";
import { StateMark } from "../components/StateMark";
import { countMessages, toFeedRows } from "../model/records";
import { renderMarkup } from "../markup/markdown";
import { isRunActive, sessionState, turnEvents } from "../model/run";
import type { TaskOutcome } from "../model/task";
import { joinReason, type T } from "../words";
import {
  BOTTOM_RESERVE_PX,
  DRAFT_PREFIX,
  LEGACY_BALL_KEY,
  OLDER_TRIGGER_PX,
  autoscrollSlackPx,
} from "../config";

/**
 * `seq` строки ленты из её ключа: `user:12` → 12; у черновика (`draft:<attemptId>`) `seq` нет — `null`.
 *
 * Нужен, чтобы «новые» считались по НОМЕРУ записи, а не по числу строк: догруз старой страницы
 * прибавляет строки СВЕРХУ и новых сообщений не приносит (находка t4 F1).
 */
function rowSeq(key: string): number | null {
  const tail = key.split(":")[1] ?? "";
  return /^\d+$/u.test(tail) ? Number.parseInt(tail, 10) : null;
}

export type SessionScreenProps = {
  t: T;
  sessionId: string;
  onBack: () => void;
  question: QuestionApi;
  onOpenSession: (sessionId: string) => void;
};

export function SessionScreen({
  t,
  sessionId,
  onBack,
  question,
  onOpenSession,
}: SessionScreenProps) {
  const visible = useVisible();
  const hiddenLong = useHiddenLong();
  const sessions = useSessions(visible);
  const live = useLive(sessionId, visible, hiddenLong);
  // `--dp-vv-inset` — один «низ» для зацепа и пары «вверх/вниз» (D1 §3.2): `bottom` кнопок считается
  // от ВИДИМОЙ области, а не от layout-вьюпорта. Пересчёт — без слушателей прокрутки.
  useVisualViewportInset();
  // Запас снизу — ИЗМЕРЕННОЕ число (D11 §1.2): высота нижнего стека `.dp-bottom` (вопрос → очередь →
  // зацеп) вместе с его отбивкой. По нему живут `scroll-padding`, `padding-bottom` страницы, пара и
  // допуск автоскролла; без него лента уезжала бы под зацеп, а вопрос закрывал бы последнюю реплику.
  const reserve = useBottomReserve();
  const heading = useRef<HTMLHeadingElement>(null);
  const stick = useRef(true);
  /** Допуск автоскролла: запас снизу + хвост — одно число, пересчитывается вместе с запасом. */
  const slack = useRef(autoscrollSlackPx());
  useEffect(() => {
    slack.current = autoscrollSlackPx();
  }, [reserve]);
  // ШАРИК УБРАН (D11 §2.3, DEF-148): ключ его позиции больше не читается и снимается при монтировании
  // экрана — иначе запись жила бы в хранилище вечно. Отказ хранилища (приватный режим, квота) — молча:
  // снимать нечего, а читать этот ключ всё равно никто не будет.
  useEffect(() => {
    try {
      window.localStorage.removeItem(LEGACY_BALL_KEY);
    } catch {
      // Хранилище недоступно — не поломка: позиции шарика больше нет.
    }
  }, []);
  /** Поднятый лист ввода: страница под скримом инертна (фокус и читалка — только в листе). */
  const [sheetOpen, setSheetOpen] = useState(false);
  /** Лист поднят: лента не читается, и её «мы внизу» не пересчитывается прокруткой листа. */
  const sheetUp = useRef(false);
  /**
   * Успех отправки показан ОДИН раз: строка «Задача принята.» живёт над зацепом ~5 с, а открытие
   * листа «тратит» его — после закрытия та же строка не возвращается (живая находка 14.11.2026:
   * в повторно открытом листе висели и успех, и «В очереди: 0»).
   */
  const acceptedSpent = useRef(false);
  /** Исход отправки для СТАБИЛЬНОГО обработчика листа: подписка не зависит от рендера. */
  const taskOutcomeRef = useRef<TaskOutcome>({ kind: "none" });

  // ── хвост S4a: орган «к последнему сообщению» (P2 §3) ─────────────────────────────────────
  /** Лента не внизу — только тогда орган виден (порог = допуск автоскролла, P2 §3.3). */
  const [away, setAway] = useState(false);
  /** «Начало разговора» ушло за верх: только тогда видна ВЕРХНЯЯ кнопка пары (D1 §1.2, порог 0). */
  const [scrolled, setScrolled] = useState(false);
  /** Сколько сообщений пришло, пока человек был выше живого конца; 0 — значка нет. */
  const [newCount, setNewCount] = useState(0);
  /** Одно объявление читалке за отъезд: рост счёта молчит (P2 §3.5, SC 4.1.3). */
  const [announced, setAnnounced] = useState("");
  /** Текущее «мы не внизу» без ожидания рендера: решение принимает обработчик прокрутки. */
  const awayRef = useRef(false);
  /** Самый новый `seq` в миг отъезда — от него считается «новых» (а не от числа строк). */
  const awaySeq = useRef(0);
  /** Черновик, который уже был при отъезде: он не «новый» — он был на экране. */
  const awayDraftKey = useRef<string | null>(null);
  /** Снимок «самый новый seq» и ключ черновика на каждый рендер — читаются в обработчике прокрутки. */
  const newestSeq = useRef(0);
  const draftKey = useRef<string | null>(null);
  const announcedOnce = useRef(false);

  // Закрыли лист — вернулись к живому концу, если человек и был внизу (лист его не сдвигал).
  // ⚠️ `useCallback` без зависимостей: стабильная ссылка нужна, чтобы подписки в TaskComposer не
  // перевешивались на каждом рендере (L1, F-L1-1: из-за этого «назад» не закрывал лист).
  const sheetChanged = useCallback((open: boolean) => {
    sheetUp.current = open;
    if (open && taskOutcomeRef.current.kind === "accepted") acceptedSpent.current = true;
    setSheetOpen(open);
    if (!open && stick.current) {
      window.requestAnimationFrame(() => {
        // Решение «были внизу» принято ДО кадра; если за этот кадр человек ушёл из ленты (прыжок
        // к «Очереди» после приёма), пин не имеет права тащить его назад вниз (D1 §5.1).
        if (!stick.current) return;
        window.scrollTo(0, document.documentElement.scrollHeight);
      });
    }
  }, []);

  const row = sessions.list?.items.find((item) => item.sessionId === sessionId) ?? null;
  const title = live.title ?? row?.title ?? null;
  const rows = useMemo(
    () => toFeedRows(live.records, live.draft, t),
    [live.records, live.draft, t],
  );
  const empty = live.status === "live" && rows.length === 0;
  // Снимок базы «новых» на каждый рендер: обработчик прокрутки читает его в миг отъезда (ниже).
  // Самое новое — последняя запись: лента отсортирована по `seq` (`sortUnique`).
  newestSeq.current =
    live.records.length > 0 ? (live.records[live.records.length - 1]?.seq ?? 0) : 0;
  draftKey.current = live.draft === null ? null : `draft:${live.draft.attemptId}`;

  // Вопрос этой сессии; вопрос чужой — строкой со ссылкой (отдельного экрана вопроса нет).
  const pending = question.pending;
  const pendingHere = pending !== null && pending.sessionId === sessionId;
  const pendingElsewhere = pending !== null && pending.sessionId !== "" && !pendingHere;
  const outcomeHere =
    !pendingHere &&
    question.outcome !== null &&
    question.outcome.kind !== "none" &&
    question.context !== null &&
    question.context.sessionId === sessionId
      ? question.outcome
      : null;

  // ── S3: задача, стоп, очередь ────────────────────────────────────────────────────────────────
  const draft = useDraft(`${DRAFT_PREFIX}${sessionId}`);
  const queue = useQueue(sessionId, live.queueFrame);
  const refreshQueue = useRef(queue.refresh);
  refreshQueue.current = queue.refresh;

  const task = useTask(t, draft, { kind: "session", sessionId }, () => refreshQueue.current());
  const remove = useQueueRemove(t, sessionId, (itemId) => {
    queue.drop(itemId);
    refreshQueue.current();
  });
  const stop = useStop(t, sessionId, () => refreshQueue.current());

  // ИСХОД ПРОШЛОЙ СЕССИИ не печатается в новой (живая находка 14.11.2026). `task.reset()` в эффекте
  // [sessionId] ниже гасит исход отправки; у `useStop` своего reset нет вовсе, поэтому оба исхода
  // привязаны к сессии ещё и сравнением по ссылке: объект, живший в миг смены, в новой сессии не
  // показывается ни кадра, а новый исход в текущей сессии — новый объект — показывается как обычно.
  const seenOutcome = useRef({ sessionId, task: task.outcome, stop: stop.outcome });
  if (seenOutcome.current.sessionId !== sessionId) {
    seenOutcome.current = { sessionId, task: task.outcome, stop: stop.outcome };
  }
  const taskOutcome: TaskOutcome =
    task.outcome === seenOutcome.current.task ? { kind: "none" } : task.outcome;
  const stopOutcome: StopOutcome =
    stop.outcome === seenOutcome.current.stop ? { kind: "none" } : stop.outcome;
  /** Для СТАБИЛЬНОГО обработчика листа: открытие листа читает уже привязанный к сессии исход. */
  taskOutcomeRef.current = taskOutcome;

  // Новый исход — новый показ: «трата» успеха относится только к прошлому приёму.
  useEffect(() => {
    if (task.outcome.kind !== "accepted") acceptedSpent.current = false;
  }, [task.outcome.kind]);

  // «Есть что останавливать»: ход идёт по событиям ленты; запасной признак — список (до 10 с).
  const runActive = isRunActive(live.records, live.draft, row?.running === true);
  const stopVisible = runActive && !pendingHere;

  // СОСТОЯНИЕ ШАПКИ — ИЗ ЖИВЫХ ДАННЫХ (D3 §1, слово владельца 11.11.2026: «состояние не живое»).
  // Строка списка — только последнее известное и только пока поток не живой; `runActive` — ТОТ ЖЕ
  // вызов `isRunActive`, что у кнопки «Остановить»: предикат не дублируется.
  const state = sessionState({
    row,
    streamLive: live.status === "live",
    pendingHere,
    runActive,
    // DEF-150: строка списка знает про работающую ПОДЧИНЕННУЮ (агрегат W3: «свой флаг ИЛИ
    // подчинённая»), а лента — только про свой ход. Без этого шапка говорила «Закончила»,
    // пока работа шла. «Остановить» (stopVisible выше) по-прежнему смотрит только на свой ход.
    rootRunning: row?.running === true,
    hasFeed: live.records.length > 0 || live.draft !== null,
  });

  // Очередь перечитывается по событиям, БЕЗ таймера (контракт §5.8): `turn/start` и `turn/end`.
  const turns = useMemo(() => turnEvents(live.records), [live.records]);
  useEffect(() => {
    if (turns === 0) return;
    refreshQueue.current();
  }, [turns]);

  function nearBottom(): boolean {
    const doc = document.documentElement;
    return doc.scrollHeight - window.scrollY - window.innerHeight <= slack.current;
  }

  // Своя прокрутка к живому концу не должна пересчитывать «человек внизу»: иначе рост страницы
  // в тот же кадр даёт «мы не внизу» и лента застревает под полосой (плавало в прогонах).
  const ownScrollUntil = useRef(0);
  /**
   * Старшие страницы в полёте: пин НЕ тянет страницу вниз, пока идёт вставка (R4, V4c §6).
   *
   * Почему: `stick` («следуй за живым концом») — флаг, а не факт, и его обновляет только обработчик
   * прокрутки. Пока действует окно `ownScrollUntil` (150 мс после нашей же прокрутки), чужая
   * прокрутка не учитывается — и `stick` остаётся старым. Тогда отложенный кадр пина
   * (`requestAnimationFrame` из эффекта роста) срабатывает УЖЕ после вставки старших, уводит
   * страницу к низу новой высоты — и компенсация якоря (наш `scrollBy`) тонет: человек, читавший
   * верх, оказывается внизу (V4c: △y 382 при △doc 200).
   */
  const olderInFlight = useRef(false);
  const pinToBottom = () => {
    if (olderInFlight.current) return;
    ownScrollUntil.current = performance.now() + 150;
    window.scrollTo(0, document.documentElement.scrollHeight);
  };

  /**
   * Лента уехала от живого конца (или вернулась к нему) — ОДНО состояние на два действия:
   * видимость органа и счёт новых. Порог — тот же, что у автодописывания (`autoscrollSlackPx`),
   * иначе экран говорил бы два разных «низ» (P2 §3.3).
   */
  function atEdge(next: boolean) {
    if (awayRef.current === next) return;
    awayRef.current = next;
    // База — САМЫЙ НОВЫЙ seq в миг отъезда (P2 §3.4), а не число строк: иначе догруз старой
    // страницы сверху читался бы как приход новых (находка t4 F1).
    awaySeq.current = newestSeq.current;
    awayDraftKey.current = draftKey.current;
    announcedOnce.current = false;
    setAnnounced("");
    setNewCount(0);
    setAway(next);
  }

  /**
   * Прыжок к живому концу (P2 §3.5): мгновенно и без анимации, автодописывание снова включено.
   * Фокус уходит на `h1`: он `tabIndex={-1}` и липкий, поэтому человек не остаётся без фокуса,
   * когда орган исчезает вместе с нажатой кнопкой (`60` §7).
   */
  const jumpToLast = () => {
    stick.current = true;
    atEdge(false);
    pinToBottom();
    heading.current?.focus({ preventScroll: true });
  };

  /**
   * Прыжок в НАЧАЛО разговора (D1 §1.2, слово владельца 11.11.2026): шапка + карточка вопроса/итог +
   * очередь + первая запись. Мгновенно (`scrollTo`), без smooth; старых НЕ догружает (это правило
   * ленты, §4). Фокус — в `h1`: нажатая кнопка исчезает, человек не остаётся без фокуса (`60` §7).
   */
  const jumpToTop = () => {
    window.scrollTo(0, 0);
    setScrolled(false);
    heading.current?.focus({ preventScroll: true });
  };

  // ── «Задача принята» → ОЧЕРЕДЬ РАСКРЫТА (D11 §3.2, слово владельца 12.11.2026) ────────────────
  // Слово владельца: «после „Задача принята“ очередь видна» (п.1 предыдущего захода). Но прыжка к блоку
  // над лентой больше НЕТ — блок сам стоит внизу, над зацепом (D11 §1.1): страница не двигается,
  // блок просто РАСКРЫВАЕТСЯ и фокус идёт в его заголовок.
  /** Раскрыта ли панель очереди: открывает и закрывает ТОЛЬКО человек (слово владельца 13.11). */
  const [queueOpen, setQueueOpen] = useState(false);
  /** Когда очередь ответила и когда пришло «принято»: раскрываем только по СВЕЖЕМУ составу. */
  const queueAnsweredAt = useRef(0);
  const acceptedAt = useRef(0);
  const queueRevealed = useRef(false);
  /** Окно тишины для прокрутки: старшие страницы сверху (иначе якорь спорит с пином). */
  const quietUntil = useRef(0);

  // Каждый НОВЫЙ вид очереди — ответ: объект `view` новый на каждый ответ (чтение D4 или кадр).
  useEffect(() => {
    if (queue.view.status === "ok") queueAnsweredAt.current = Date.now();
  }, [queue.view]);

  useEffect(() => {
    if (taskOutcome.kind !== "accepted") {
      acceptedAt.current = 0;
      queueRevealed.current = false;
      return;
    }
    if (acceptedAt.current === 0) acceptedAt.current = Date.now();
  }, [taskOutcome.kind]);

  // R8: N = 0 — панели нет вовсе (D11 §3.5), и раскрытое состояние не должно «ждать» следующего пункта:
  // иначе после новой задачи строка появилась бы уже раскрытой. Сбрасываем только сам факт раскрытия —
  // ручное раскрытие на ЖИВОЙ очереди не трогаем (эффект реагирует только на total = 0).
  useEffect(() => {
    if (queue.view.status === "ok" && queue.view.total === 0) setQueueOpen(false);
  }, [queue.view]);
  useEffect(() => {
    // Очередь ещё не ответила — сравнивать нечего: раскрылось бы старым составом. Ждём НОВЫЙ ответ
    // на перечитывание, которое экран сделал по `accepted` (`refreshQueue` в `useTask`).
    if (acceptedAt.current === 0 || queueRevealed.current) return;
    if (queue.view.status !== "ok" || queueAnsweredAt.current < acceptedAt.current) return;
    queueRevealed.current = true;
    // R8 (живая приёмка 13.11.2026): после КАЖДОЙ «Задача принята» строка СВЁРНУТА, даже если
    // человек раскрыл её вручную перед отправкой. Слово владельца: «откроет сам». Иначе состояние
    // `queueOpen` переживало приём (и N=0), и панель появлялась раскрытой уже после следующей задачи.
    setQueueOpen(false);

    // СЛОВО ВЛАДЕЛЬЦА 13.11.2026: очередь после «Задача принята» НЕ раскрывается — «строка свёрнута,
    // откроет сам». Поэтому здесь только держим живой конец: строка «В очереди: N» встала над зацепом,
    // запас низа вырос, и без пина последняя реплика ушла бы под стек (D11 §1.2). Фокус не
    // перехватываем: после закрытия листа его вернул зацеп (D1 §5.1).
    // N = 0 — расти нечему: строки нет, слов «Задача принята.» хватает (они уже сказаны исходом).
    if (queue.view.total === 0) return;
    const keepTail = () => {
      if (stick.current && !sheetUp.current) pinToBottom();
    };
    // Три попытки — запас пишется кадром (`useBottomReserve`).
    for (const delay of [0, 80, 240]) window.setTimeout(keepTail, delay);
  }, [queue.view]);
  // Вставка старых сообщений сверху — ОКНО ТИШИНЫ для прокрутки: её поправит якорь, а не пин.
  // Без этого человек, читающий ВЕРХ короткой страницы, уезжал вниз без своего действия
  // (находка прогона t4: страница всего на 43 px выше окна — «верх» и «низ» неразличимы).
  /** «Совсем внизу» — для роста блока НАД лентой: допуск автоскролла (запас + 100) здесь не годится. */
  function atVeryBottom(): boolean {
    const doc = document.documentElement;
    return doc.scrollHeight - window.scrollY - window.innerHeight <= 24;
  }
  // РАНЬШЕ — вариант B (R4, слово владельца 11.11.2026): кнопки нет, старые сообщения догружаются
  // САМИ, когда человек долистал до верха. Пока запрос идёт — второй не уходит; после отказа ждём
  // человека («Повторить»), иначе каждое касание верха зацикливало бы сеть. Прокрутку не слушаем,
  // пока лист поднят: там страница инертна и строка исхода не должна дёргать историю.
  /**
   * ЯКОРЬ ВСТАВКИ СТАРШИХ (R4, V4c §6): высота ленты ДО запроса и (запасной путь) первая строка.
   *
   * Почему высота, а не только строка: прежний путь сравнивал `rect.top` того же УЗЛА, и если React
   * пересоздавал строки при вставке (`row.isConnected === false`), эффект молча выходил — документ
   * вырастал на 200, а `scrollY` нет: видимая строка уезжала ровно на высоту батча. Ворота V4c
   * воспроизвели это 1 раз из 105 и доказали числами (`docH 404→604`, `scrollY 82→282` в норме).
   */
  const olderAnchor = useRef<{ row: HTMLElement; top: number; feedHeight: number } | null>(null);
  /** Один подход к верху ленты — одна страница: взвод снимается, пока первая запись не уйдёт ниже порога. */
  const olderArmed = useRef(true);
  /** Сама лента: её ВЕРХ — точка догруза, а не верх документа (D1 §4.2, 11.11.2026). */
  const feedRef = useRef<HTMLOListElement | null>(null);
  const feedFirst = (): HTMLElement | null => {
    const feed = feedRef.current;
    return feed === null ? null : ((feed.firstElementChild as HTMLElement | null) ?? null);
  };
  /** Верх ленты `null`, если строк нет: тогда условие верха неприменимо (работает правило по высоте). */
  const feedTopNow = (): { top: number; height: number } | null => {
    const first = feedFirst();
    if (first === null) return null;
    const rect = first.getBoundingClientRect();
    return { top: rect.top, height: rect.height };
  };

  useEffect(() => {
    /**
     * Одна страница старых. Якорь — ПЕРВАЯ СТРОКА (её ключ-узел и `rect.top`), а не высота документа:
     * высота не гарантирует неподвижности, когда над лентой в тот же миг выросла очередь (D1 §4.2).
     * Окно тишины закрывает пин и наблюдатель роста, чтобы вставка не дёрнула страницу.
     */
    const startOlder = (): boolean => {
      if (!live.hasMore || live.earlierLoading || live.earlierError !== "") return false;
      const first = feedFirst();
      if (first !== null) {
        olderAnchor.current = {
          row: first,
          top: first.getBoundingClientRect().top,
          feedHeight: feedRef.current?.getBoundingClientRect().height ?? 0,
        };
      }
      olderArmed.current = false;
      olderInFlight.current = true;
      quietUntil.current = performance.now() + 1_500;
      live.showEarlier();
      return true;
    };
    /**
     * Триггер — верх ЛЕНТЫ: грузим, только когда первая запись стоит у верхнего края окна, и ровно
     * одну страницу за подход. Пока карточка вопроса и очередь в поле зрения, `feedTop` заведомо
     * больше порога — верх читают спокойно, вниз страницу не отбрасывает (D1 §4.2–§4.3).
     */
    const onScroll = () => {
      if (sheetUp.current) return;
      const first = feedTopNow();
      if (first === null) return;
      if (first.top > OLDER_TRIGGER_PX) {
        olderArmed.current = true;
        return;
      }
      // Первая запись должна быть У ВЕРХНЕГО КРАЯ, а не «где-то выше окна»: иначе прыжок в конец
      // длинной ленты (top сильно отрицательный) грузил бы старые на каждом касании низа (D1 §4.2).
      if (first.top + first.height <= 0) return;
      if (!olderArmed.current) return;
      startOlder();
    };
    window.addEventListener("scroll", onScroll, { passive: true });
    // Страница короче окна: «дойти до верха ленты» нельзя — добираем сами, кадр за кадром,
    // пока страница не станет прокручиваемой или старые не кончатся (D1 §4.2). Если строк нет
    // вовсе, условие верха неприменимо — работает только это правило по высоте документа.
    const fill = () => {
      if (document.documentElement.scrollHeight > window.innerHeight + 1) return;
      startOlder();
    };
    const frame = window.requestAnimationFrame(fill);
    return () => {
      window.removeEventListener("scroll", onScroll);
      window.cancelAnimationFrame(frame);
    };
  }, [live.hasMore, live.earlierLoading, live.earlierError, live.showEarlier]);
  // Вставка старых НЕ двигает то, что человек читает (D1 §4.2). R4: компенсируем ФАКТИЧЕСКУЮ дельту
  // высоты ЛЕНТЫ — она и есть высота вставленного батча (старые приезжают только СВЕРХУ).
  // Строка — запасной путь: если высота не изменилась, а видимая строка сдвинулась (вставка той же
  // высоты), или если узел строки пересоздан и его `rect` больше ничего не значит.
  // Авто-привязка браузера выключена (`.dp-feed { overflow-anchor: none }`), поэтому компенсация
  // ровно одна: своей нет — нет и чужой (V4c §6: чужая молча пропадала).
  useLayoutEffect(() => {
    const before = olderAnchor.current;
    if (before === null) return;
    const feed = feedRef.current;
    const height = feed === null ? before.feedHeight : feed.getBoundingClientRect().height;
    const growth = height - before.feedHeight;

    // ПЕРВИЧНЫЙ признак — сама видимая строка: она и есть обещание D1 §4.2. Компенсация по ней
    // ИДЕМПОТЕНТНА: после сдвига её `top` возвращается к прежнему, и повторный запуск ничего не
    // делает (а двойная компенсация по высоте давала −200 в прогонах).
    let compensated = false;
    if (before.row.isConnected && feed !== null && feed.firstElementChild !== before.row) {
      const delta = before.row.getBoundingClientRect().top - before.top;
      if (Math.abs(delta) > 1) {
        window.scrollBy(0, delta);
        quietUntil.current = performance.now() + 1_500;
        compensated = true;
      }
    } else if (!before.row.isConnected && Math.abs(growth) > 1) {
      // ЗАПАСНОЙ путь — только когда узла строки уже нет (React пересоздал строки, V4c §6):
      // тогда единственный измеримый след вставки — высота самой ленты.
      window.scrollBy(0, growth);
      quietUntil.current = performance.now() + 1_500;
      compensated = true;
    }
    if (compensated) before.feedHeight = height;

    // Якорь живёт до конца запроса: пока грузим — ждём следующих кусков, после — снимаем.
    // И только теперь снова разрешаем пину тянуть вниз (`olderInFlight`).
    if (!live.earlierLoading) {
      olderInFlight.current = false;
      olderAnchor.current = null;
    }
  }, [rows.length, live.earlierLoading]);

  useEffect(() => {
    olderArmed.current = true;
    olderInFlight.current = false;
    olderAnchor.current = null;
    setScrolled(window.scrollY > 0);
    // Новая сессия — новый исход: успех/отказ отправки прошлой сессии не переезжает сюда
    // (живая находка 14.11.2026); у `useStop` своего reset нет — его гасит `seenOutcome` выше.
    task.reset();
    heading.current?.focus();
  }, [sessionId]);

  // Автоскролл — только если человек был внизу; иначе позиция не меняется.
  // Здесь же живёт орган «к последнему»: «не внизу» — то же состояние, что «лента перестала следовать».
  useEffect(() => {
    const onScroll = () => {
      // Верхняя кнопка пары живёт по своему порогу (scrollY > 0); считаем ДО раннего выхода, чтобы
      // своя прокрутка (пин к живому концу) тоже её показывала/убирала.
      setScrolled(window.scrollY > 0);
      // Окно тишины после прыжка к очереди: своя прокрутка не смеет снова взвести `stick`,
      // иначе RO-пин вернёт страницу в низ (находка L1 F1).
      if (sheetUp.current || performance.now() < ownScrollUntil.current) return;
      if (performance.now() < quietUntil.current) return;
      const atBottom = nearBottom();
      stick.current = atBottom;
      atEdge(!atBottom);
    };
    window.addEventListener("scroll", onScroll, { passive: true });
    return () => window.removeEventListener("scroll", onScroll);
  }, []);

  // «Новое» — запись НОВЕЕ того, что было у человека в миг отъезда от живого конца (P2 §3.4).
  // Считаем по `seq`, а не по числу строк: догруженная сверху старая страница прибавляет строки,
  // но счётчик обязан молчать (находка t4 F1). Слова о счёте — один раз за отъезд (`60` §9).
  useEffect(() => {
    if (!away) return;
    const fresh = rows.filter((row) => {
      const seq = rowSeq(row.key);
      // Черновик `seq` не имеет: он новее, только если его не было на экране при отъезде.
      return seq === null ? row.key !== awayDraftKey.current : seq > awaySeq.current;
    });
    const count = countMessages(fresh);
    setNewCount(count);
    if (count > 0 && !announcedOnce.current) {
      announcedOnce.current = true;
      setAnnounced(t("PhoneWebToLastCountFormat", count));
    }
  }, [away, rows, t]);

  // Живой конец держится за низ и тогда, когда вырос блок НАД лентой (очередь: приняли задачу,
  // убрали пункт, перечитали после стопа), иначе последняя строка уезжает под полосу.
  // Но рост блока НАД лентой держит низ только у САМОГО низа: иначе человек, читающий верх,
  // уезжает вниз от того, что приехала очередь (находка прогона t4).
  const queueTotal = queue.view.status === "ok" ? queue.view.total : -1;
  const tailSeq = useRef(0);
  useEffect(() => {
    if (performance.now() < quietUntil.current) return;
    const tail = live.records.length > 0 ? (live.records[live.records.length - 1]?.seq ?? 0) : 0;
    const grew = tail !== tailSeq.current;
    tailSeq.current = tail;
    if (grew ? !stick.current : !atVeryBottom()) return;
    // В кадре, а не в том же проходе: очередь и лента успевают встать, и цель прокрутки — НОВАЯ высота.
    const frame = window.requestAnimationFrame(pinToBottom);
    return () => window.cancelAnimationFrame(frame);
  }, [rows.length, live.draft?.text, queueTotal, live.records]);

  // Пока человек внизу, любой рост страницы (очередь пришла позже, кадр встал выше, нижний стек
  // вырос) держит его внизу: иначе последняя строка уезжает под стек без единого события прокрутки.
  // Следим И за фактическим размером тела (RO), И за ИЗМЕРЕННЫМ запасом (D11 §1.2): запас пишется
  // кадром, и одного RO на теле может не хватить, если стек вырос в тот же миг.
  useEffect(() => {
    const observer = new ResizeObserver(() => {
      if (!stick.current || sheetUp.current) return;
      // В окне тишины (только что вставили старые сверху) прокрутку не трогаем.
      if (performance.now() < quietUntil.current) return;
      pinToBottom();
    });
    observer.observe(document.body);
    return () => observer.disconnect();
  }, []);

  // Рост НИЖНЕГО СТЕКА (раскрылась очередь) — тоже рост страницы: если человек у живого конца,
  // держим его там же. `sheetUp` — пока лист поднят, страница инертна и прокрутка не наша.
  useEffect(() => {
    if (!stick.current || sheetUp.current) return;
    if (performance.now() < quietUntil.current) return;
    const frame = window.requestAnimationFrame(pinToBottom);
    return () => window.cancelAnimationFrame(frame);
  }, [reserve]);

  // Строка исхода в полосе: сначала «идёт/принято/отказ» отправки, потом стоп.
  const status: ComposerStatus = (() => {
    // D11 §3.5: ноль — пустая величина, «В очереди: 0» не печатается (живая находка 14.11.2026).
    const inbox = queue.view.status === "ok" && queue.view.total > 0 ? queue.view.total : null;
    if (taskOutcome.kind === "sending")
      return { words: t("PhoneTaskBusy"), tone: "quiet", inbox: null };
    // «Задача принята.» — строка НАД зацепом (~5 с). В ОТКРЫТОМ листе её нет: человек уже действует,
    // а вчерашний успех в листе — та самая висящая строка (живая находка 14.11.2026).
    if (taskOutcome.kind === "accepted")
      return {
        words: sheetOpen || acceptedSpent.current ? "" : taskOutcome.words,
        tone: "ok",
        inbox,
      };
    if (taskOutcome.kind === "failed")
      return { words: taskOutcome.words, tone: "danger", inbox: null };
    if (stopOutcome.kind === "stopped") return { words: stopOutcome.words, tone: "ok", inbox };
    if (stopOutcome.kind === "idle") return { words: stopOutcome.words, tone: "quiet", inbox };
    if (stopOutcome.kind === "failed")
      return { words: stopOutcome.words, tone: "danger", inbox: null };
    return { words: "", tone: "quiet", inbox: null };
  })();

  // «Повторить» вместо «Отправить задачу» — только если текст НЕ менялся после отказа:
  // это та же попытка с тем же requestId (контракт §7.1), а не новая задача.
  const primaryLabel =
    taskOutcome.kind === "failed" && taskOutcome.text === draft.text.trim()
      ? ("PhonePageRepeatButton" as const)
      : ("PhoneWebTaskSend" as const);

  const stopPanel: StopPanel | null = stopVisible
    ? {
        warning: t("PhoneWebStopWarning"),
        label: t("PhoneWebStop"),
        busy: stop.stopping,
        onStop: () => {
          task.reset();
          stop.stop();
        },
      }
    : null;

  const failure = live.failure;
  const failureKind = failure === null ? "none" : liveFailureWords(failure.code);
  /**
   * ФИНАЛЬНЫЙ отказ: лента сама не откроется (постоянный «too-many», сессия не открылась,
   * подчинённая сессия). Его блок и есть ЕДИНСТВЕННЫЙ ответ экрана (D2 F-04).
   */
  const finalFailure =
    failureKind === "too-many" || failureKind === "missing" || failureKind === "subagent";
  /**
   * Одно состояние — один ответ (60 §0, §7; D2 F-04):
   * при финальном отказе строки обрыва НЕТ (её место занял блок), а «Подключаюсь…» печатается
   * только пока это правда.
   */
  const connectionLost = !finalFailure && (live.status === "error" || live.status === "lost");
  const showConnecting = live.status === "connecting" && !connectionLost && !finalFailure;
  /**
   * F-07: ЗАГОЛОВОК ЭКРАНА НАЗЫВАЕТ ТО, ЧТО СЛУЧИЛОСЬ (P2 §4). Лестница из четырёх ступеней:
   * имя → «Сессия не открылась» (финальный отказ и обрыв: имени нет и лента не открылась) →
   * «Подключаюсь к сессии…» (имени ещё нет, связь не потеряна) → «Без названия» — и только
   * тогда, когда сессия ОТКРЫТА и имени у неё правда нет: в списке это слово честно, в беде — нет.
   */
  const headingText =
    title !== null
      ? title
      : finalFailure || connectionLost
        ? t("PhoneWebSessionMissingTitle")
        : live.status === "live"
          ? t("PhoneWebUntitled")
          : t("PhoneWebLiveConnecting");
  /** Имя органа «к последнему»: при счёте > 0 — со словами о новых, при 0 — просто имя (P2 §3.5). */
  const toLastLabel = newCount > 0 ? t("PhoneWebToLastCountFormat", newCount) : t("PhoneWebToLast");
  return (
    <main
      className="dp-page dp-page-composer"
      // Поднятый лист ввода делает страницу инертной: скрим и лист живут в портале, фокус — только в листе.
      inert={sheetOpen}
      data-bottom-reserve={BOTTOM_RESERVE_PX}
    >
      {/* ЛИПКАЯ ШАПКА сессии (R2, слово владельца 11.11.2026): возврат к списку — иконка-кнопка 44×44,
          видна ВСЕГДА, текстовая строка-ссылка убрана. */}
      <header className="dp-head dp-head-session">
        <div className="dp-head-session-row">
          <button
            type="button"
            className="dp-quiet dp-icon-button dp-head-back"
            aria-label={t("PhoneWebBackToList")}
            onClick={onBack}
          >
            <IconChevronLeft aria-hidden="true" />
          </button>
          <h1 ref={heading} tabIndex={-1}>
            {headingText}
          </h1>
        </div>
        {/* Метёка — только когда строка сессии ИЗВЕСТНА: при row === null «Подключаюсь…» и строка
            обрыва стояли рядом — два ответа на один вопрос (D2 F-04). */}
        {/* Мета — только когда состояние ЕСТЬ: пока поток не живой и строки нет, молчим (D3 §1.2). */}
        {state !== null ? (
          <p className="dp-meta">
            <StateMark tone={state.tone} spin={state.key === "PhoneWebStateRunning"} />
            {t(state.key)}
            {row !== null && row.turns > 0 ? ` · ${t("PhoneWebStatsFormat", row.turns)}` : ""}
          </p>
        ) : null}
        {/* Строка «Подключаюсь…» нужна только там, где h1 несёт ИМЯ: когда это слово уже в h1,
            вторая такая же строка была бы вторым ответом на один вопрос (P2 §4, `60` §9). */}
        {showConnecting && title !== null ? (
          <p className="dp-note">{t("PhoneWebLiveConnecting")}</p>
        ) : null}
        {showConnecting && row !== null && row.outline !== "" ? (
          <p className="dp-note">{row.outline}</p>
        ) : null}
        {/* Путь повторения — СРАЗУ под словами обрыва, в липкой шапке (D2 F-05): на длинной ленте
            он виден без прокрутки, а в конце страницы его нет вовсе. */}
        {connectionLost ? (
          <>
            <p className="dp-status" role="status">
              {t("PhoneWebLiveLost")}
            </p>
            <div className="dp-head-actions">
              <button type="button" className="dp-quiet dp-reconnect" onClick={live.retry}>
                {t("PhoneWebReconnect")}
              </button>
            </div>
          </>
        ) : null}
      </header>

      {/* Финальный отказ — ОДИН ответ: его блок, его слова и единственный путь (D2 F-04, F-05). */}
      {failureKind === "too-many" ? (
        <div className="dp-error-block" role="alert">
          <p>{t("PhoneWebTooManyScreens")}</p>
          <div className="dp-actions">
            <button type="button" className="dp-quiet dp-reconnect" onClick={live.retry}>
              {t("PhoneWebReconnect")}
            </button>
          </div>
        </div>
      ) : null}

      {failureKind === "subagent" || failureKind === "missing" ? (
        // Первая строка — ОБЪЯСНЕНИЕ, а не повтор заголовка: слово «Сессия не открылась» уже стоит
        // в h1, и вторая такая же строка была бы двумя ответами на один вопрос (P2 §4, `60` §9).
        <div className="dp-error-block" role="alert">
          <p>
            {failureKind === "subagent"
              ? t("PhoneWebSubagentRefused")
              : t("PhoneWebSessionMissing")}
          </p>
          <div className="dp-actions">
            <button type="button" className="dp-quiet dp-reconnect" onClick={live.retry}>
              {t("PhoneWebReconnect")}
            </button>
          </div>
        </div>
      ) : null}

      {/* Кнопки «Показать раньше» больше нет (R4, вариант B): старые сообщения догружаются сами
          при прокрутке вверх — эффект ниже. */}

      {/* Вопрос больше не секция ленты (D11 §4.2, DEF-145): он — ПОВЕРХНОСТЬ над лентой в нижнем
          стеке, первым сверху (над очередью и зацепом). Здесь его нет вовсе — иначе человек снова
          искал бы его в тексте ленты. */}

      {pendingElsewhere && pending !== null ? (
        <p className="dp-status" role="status">
          {t("PhoneWebQuestionElsewhere")}{" "}
          <a
            className="dp-link"
            href={`/?session=${encodeURIComponent(pending.sessionId)}`}
            onClick={(event) => {
              event.preventDefault();
              onOpenSession(pending.sessionId);
            }}
          >
            {t("PhoneWebOpen")}
          </a>
        </p>
      ) : null}

      {failureKind === "other" && failure !== null && failure.message !== "" ? (
        <p className="dp-note">{joinReason(t("PhoneWebSessionMissing"), failure.message)}</p>
      ) : null}
      {/* Отказ догона — ТОНКАЯ строка + «Повторить» (R4): слова из существующих ключей
          (`PhoneWebOlderFailed` + `PhonePageRepeatButton`), новых слов нет. */}
      {live.earlierError !== "" ? (
        <p className="dp-note dp-earlier-error" role="alert">
          <span>{joinReason(t("PhoneWebOlderFailed"), live.earlierError)}</span>{" "}
          <button type="button" className="dp-link-button" onClick={live.showEarlier}>
            {t("PhonePageRepeatButton")}
          </button>
        </p>
      ) : null}

      {live.records.length === 0 && live.status === "connecting" ? (
        <ul className="dp-skeleton" aria-hidden="true">
          <li />
          <li />
        </ul>
      ) : null}

      {empty ? (
        <div className="dp-empty">
          <h2>{t("PhoneWebSessionEmptyTitle")}</h2>
          <p>{t("PhoneWebSessionEmpty")}</p>
        </div>
      ) : null}

      {/* Очередь больше НЕ секция ленты (D11 §3.3): её место — нижний стек, между вопросом и зацепом.
          Здесь, в потоке, её нет вовсе — иначе человек снова искал бы её «где-то наверху». */}
      {rows.length > 0 ? (
        <ol className="dp-feed" ref={feedRef}>
          {rows.map((rowItem) => {
            switch (rowItem.kind) {
              case "turn":
                return (
                  <li key={rowItem.key} className="dp-turn">
                    {rowItem.label}
                  </li>
                );
              case "user":
                return (
                  <li key={rowItem.key} className="dp-msg dp-user">
                    {rowItem.text}
                    {rowItem.attachment !== null ? (
                      <p className="dp-attachment">{rowItem.attachment}</p>
                    ) : null}
                  </li>
                );
              case "assistant":
                // РАЗМЕТКА — только здесь (S3c, слово владельца 31.10.2026): абзацы, заголовки
                // (опущены до h4–h6 рендерером), списки, код, цитаты и ссылки http/https.
                // Недостоверная строка в DOM не идёт: renderMarkup = render → sanitize → set.
                return (
                  <li key={rowItem.key} className="dp-msg dp-agent">
                    <div
                      className="dp-md"
                      dangerouslySetInnerHTML={{ __html: renderMarkup(rowItem.text) }}
                    />
                  </li>
                );
              case "draft":
                return (
                  <li key={rowItem.key} className="dp-msg dp-agent dp-draft">
                    {rowItem.text}
                  </li>
                );
              case "tool":
                return (
                  <li key={rowItem.key} className="dp-tool">
                    {rowItem.text}
                  </li>
                );
              case "error":
                return (
                  <li key={rowItem.key} className="dp-error" role="alert">
                    {rowItem.label}
                  </li>
                );
              default:
                return null;
            }
          })}
        </ol>
      ) : null}

      {/* «К последнему сообщению» (хвост S4a, P2 §3): орган виден только когда лента не внизу,
          и исчезает у живого конца — поэтому последнее сообщение под ним не оказывается никогда.
          Видимого слова на кнопке нет: имя несёт иконка, счёт — знак и слова в имени.
          В разметке — после ленты и перед полосой задачи: порядок обхода совпадает с видимым. */}
      {/* Пара «вверх/вниз» (D1 §1.2, слово владельца 11.11.2026): НИЖНЯЯ не сдвинута (96/8,
          прежнее место), ВЕРХНЯЯ — над ней (148/8, +60). Обе после ленты и перед полосой задачи,
          порядок разметки = видимому: верхняя → нижняя. Видимого слова нет, счёта у верхней нет,
          `role="status` тоже: ничего не приехало — объявлять нечего. */}
      {scrolled ? (
        <button
          type="button"
          className="dp-toup"
          aria-label={t("PhoneWebToTop")}
          onClick={jumpToTop}
        >
          <IconArrowUp aria-hidden="true" />
        </button>
      ) : null}
      {away ? (
        <button type="button" className="dp-tolast" aria-label={toLastLabel} onClick={jumpToLast}>
          <IconArrowDown aria-hidden="true" />
          {newCount > 0 ? (
            <span className="dp-badge dp-tolast-badge" aria-hidden="true">
              {newCount > 99 ? "99+" : newCount}
            </span>
          ) : null}
        </button>
      ) : null}
      {/* Счёт словами — один раз за отъезд: цифра в значке `aria-hidden`, без этих слов читалка
          сказала бы только имя кнопки и человек не узнал бы, что там что-то новое (SC 4.1.3). */}
      <span className="dp-visually-hidden" role="status">
        {announced}
      </span>

      {/* Кнопки в конце страницы НЕТ (D2 F-05): путь повторения живёт только рядом со словами
          обрыва (липкая шапка) и в блоке финального отказа. */}

      {/* ── S3: НИЖНИЙ СТЕК (D11 §1.1): вопрос → очередь → зацеп шторки. ─────────────────────────────
          Портал в `body` (`BottomStack`), `fixed` у низа, z 10 — между парой (2) и листом (40).
          Порядок задаёт РАЗМЕТКА, а не z-index: раскрытие очереди ПОДНИМАЕТ то, что выше, а не
          накрывает — поэтому очередь стоит между вопросом (W10) и зацепом (W11).
          Пока лист ввода поднят, стек лежит ПОД ним (z 40 > 10), `main` инертен — и САМ СТЕК тоже
          (`inert={sheetOpen}`): он портал в `body`, `inert` на `main` его не накрывает, а внутри с W10
          живёт форма вопроса. Иначе читалка читала бы под скримом то, чего человек не видит. */}
      <BottomStack inert={sheetOpen}>
        {/* Вопрос — ПОВЕРХНОСТЬ первым сверху (D11 §4.2): он виден всегда, но занимает не больше
            `min(55dvh,480px)` и не отбирает ленту — та прокручивается за ним. */}
        {pendingHere && pending !== null ? (
          <QuestionCard
            key={pending.eventId}
            t={t}
            question={pending}
            onSend={question.send}
            onCancel={question.cancel}
            suspend={question.suspend}
          />
        ) : null}
        {/* Итог вопроса — на том же месте, где стояла карточка (контракт §3.2). */}
        {outcomeHere !== null ? <QuestionOutcomeBlock t={t} state={outcomeHere} /> : null}
        <QueueSection
          t={t}
          queue={queue}
          remove={remove}
          idPrefix={sessionId}
          open={queueOpen}
          onOpenChange={setQueueOpen}
        />
        <TaskComposer
          t={t}
          idPrefix="dp-session"
          headingLevel={2}
          headingKey="PhoneWebTaskTitle"
          note={null}
          draft={draft}
          sending={task.sending}
          status={status}
          onSend={task.send}
          primaryLabel={primaryLabel}
          stop={stopPanel}
          mode="sheet"
          onSheetOpenChange={sheetChanged}
          accepted={taskOutcome.kind === "accepted"}
        />
      </BottomStack>
    </main>
  );
}
