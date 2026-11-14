// Числа телефонного пульта — В ОДНОМ МЕСТЕ, чтобы «правило → число → проверка» было одним
// утверждением, а не россыпью литералов по экранам.
//
// Источник чисел: docs\PHONE-CLIENT.md §5 (числа приёмки) и _team\250\s1s2-contract.md §5.1–5.2.
// Меняешь число здесь — меняешь и проверку, которая его сторожит.

/** Порог перечитывания списка по подсказке `stale` (S4a): не чаще одного запроса в 3 с.
 *  Таймера у списка НЕТ: список перечитывается по возврату на вкладку, по возврату из сессии
 *  и по подсказке `stale` из уже открытого `$events` (R1 §5.2). */
export const LIST_STALE_MIN_MS = 3_000;

/** 429 очереди (`PhoneQueueBusy`) — «слишком часто», а не беда: повтор ТИХИЙ, с отступом
 *  (D1 §4: человеку ни слова, состав остаётся последним известным). */
export const QUEUE_BUSY_RETRY_MS = 3_000;
export const QUEUE_BUSY_RETRY_MAX_MS = 15_000;
/** Список на телефоне — последние 50 корневых; постраничности в S1 нет. */
export const LIST_LIMIT = 50;
/** Ответ списка ≤ 256 КиБ (сторожит калитка); на клиенте — только название до 200 знаков. */
export const MAX_TITLE = 200;

/** Живая лента: окно `session/follow` и одна страница «раньше» — по 50 сообщений. */
export const PAGE_MESSAGES = 50;
/** Один текстовый блок ленты ≤ 20 000 знаков (обрезает калитка, клиент сторожит повторно). */
export const MAX_FEED_TEXT = 20_000;

/** SSE: пинг 15 с, сторож 45 с без единого события (включая пинг) — поток считается мёртвым. */
export const SSE_PING_MS = 15_000;
export const SSE_WATCHDOG_MS = 45_000;
/** Отступ переподключения: 1 → 2 → 4 → 8 → 15 с, дальше 15 с. */
export const SSE_BACKOFF_MS = [1_000, 2_000, 4_000, 8_000, 15_000] as const;

/** Скрытие вкладки: ленту закрываем, только если страница скрыта дольше 5 с. */
export const HIDDEN_CLOSE_MS = 5_000;

/** Автоскролл — только если человек был внизу: ≤ 100 px от низа — это ХВОСТ допуска;
 *  к нему прибавляется запас снизу (вердикт t10 §1д, D11 §1.2). */
export const AUTOSCROLL_SLACK_PX = 100;

// ВНИЗУ ЭКРАНА — ЗАЦЕП ШТОРКИ ВВОДА, и это ОДНО число на все состояния (`60-forms` §7: страница не
// прыгает; D11 §1.2). Запас = высота зацепа 44 + зазор 8 = 52; то же число стоит в CSS запасным путём
// (`--dp-bottom-reserve`), а ЖИВОЕ значение пишет `useBottomReserve` — высоту нижнего стека
// (вопрос → очередь → зацеп, D11 §1.1) вместе с его отбивкой сверху. Читают его `scroll-padding`,
// `padding-bottom` страницы, пара «вверх/вниз» (60 и 112 от низа) и допуск автоскролла.
export const HANDLE_HEIGHT_PX = 44;
/** Зазор между зацепом и тем, что над ним (и между поверхностями в стеке). */
export const BOTTOM_GAP_PX = 8;
/** Запасной путь запаса снизу: пока стек не измерен (и в разметке — `data-bottom-reserve`). */
export const BOTTOM_RESERVE_PX = HANDLE_HEIGHT_PX + BOTTOM_GAP_PX;

/** Ключ УБРАННОГО шарика (D11 §2.3, DEF-148): не читается вовсе, снимается при монтировании сессии. */
export const LEGACY_BALL_KEY = "dsh.phone.ball.pos";

/** Порог «до верха» для автодогона старых сообщений (R4): ближе — грузим сами, без кнопки. */
export const OLDER_TRIGGER_PX = 120;

/**
 * Запас снизу — ИЗМЕРЕННОЕ число (`--dp-bottom-reserve` на `:root`), а не константа: когда в стеке
 * появляется вопрос или очередь, лента не уходит под них (D11 §1.4, вариант А). Нет измерения
 * (сервер, тест без DOM) — запасной путь 52.
 */
export function bottomReservePx(): number {
  if (typeof document === "undefined") return BOTTOM_RESERVE_PX;
  const measured = Number.parseFloat(
    window.getComputedStyle(document.documentElement).getPropertyValue("--dp-bottom-reserve"),
  );
  return Number.isFinite(measured) ? measured : BOTTOM_RESERVE_PX;
}

/** Допуск «внизу» = запас снизу + хвост 100 px (не зависит от состояния листа). */
export function autoscrollSlackPx(): number {
  return bottomReservePx() + AUTOSCROLL_SLACK_PX;
}

/** Предел текста задачи — наш предел (движок длину не объявляет): контракт §7.3. */
export const TASK_TEXT_MAX = 4000;

/** Очередь: до 20 пунктов отдаёт калитка (контракт §5.3), превью ≤ 300 знаков режет она же. */
export const QUEUE_LIMIT = 20;
export const QUEUE_PREVIEW_MAX = 300;

/** Черновик задачи: ключи `dsh.phone.draft.*`, формат записи, запись не чаще ~1 раза в секунду. */
export const DRAFT_PREFIX = "dsh.phone.draft.";
export const DRAFT_NEW_KEY = "dsh.phone.draft.new";
export const DRAFT_VERSION = 1;
export const DRAFT_SAVE_MS = 1_000;

/** Один вопрос: ждёт 10 минут; клиент считает по remainingSeconds, а не по своим часам (S2). */
export const QUESTION_POLL_MS = 2_000;
export const QUESTION_WAIT_SECONDS = 600;
