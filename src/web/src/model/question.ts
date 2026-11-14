// Вопрос агента для телефона (S2): разбор ответа калитки, проверка «на каждый вопрос есть ответ»
// и сборка тела `POST /answer`.
//
// Формы — контракт §2.3 и §2.5. Всё, чего в белом списке нет, сюда не попадает: карточка собирается
// из `id/question/detail/header/options/multiSelect`, а `clientId`, `agentId` и `intent` не читаются
// вовсе (их и не присылают).

export type AnswerOption = { label: string; description: string | null };

export type AskCard = {
  id: string;
  question: string;
  detail: string | null;
  header: string | null;
  options: AnswerOption[];
  multiSelect: boolean;
};

export type PendingQuestion = {
  kind: "question";
  eventId: string;
  sessionId: string;
  fromSubagent: boolean;
  cards: AskCard[];
  expiresAt: string;
  remainingSeconds: number;
};

export type OutcomeKind = "answered" | "taken" | "cancelled" | "no-answer" | "unreadable" | "none";

export type QuestionOutcome = {
  kind: OutcomeKind;
  eventId: string;
  message: string;
};

export type QuestionState = PendingQuestion | QuestionOutcome;

/** Пределы — те же, что проверяет калитка (§2.3): клиент не отправляет заведомо отказное. */
export const MAX_CARDS = 20;
export const MAX_CUSTOM = 500;
export const MAX_LABEL = 200;
export const MAX_QUESTION_TEXT = 2000;
export const MAX_OPTIONS = 20;

/** Ответ человека по ОДНОМУ вопросу карточки. */
export type AnswerValue = { selected: string[]; custom: string };

/** Тело ответа — ровно эти поля (§2.5); `custom` не шлётся, когда он пуст. */
export type AnswerItem = { id: string; selected: string[]; custom?: string };

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function text(value: unknown, max: number): string {
  if (typeof value !== "string") return "";
  const trimmed = value.trim();
  return trimmed.length > max ? trimmed.slice(0, max) : trimmed;
}

function maybeText(value: unknown, max: number): string | null {
  const result = text(value, max);
  return result === "" ? null : result;
}

function count(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) && value > 0 ? Math.floor(value) : 0;
}

function parseCard(raw: unknown): AskCard | null {
  if (!isRecord(raw)) return null;
  const id = text(raw["id"], 128);
  const question = text(raw["question"], MAX_QUESTION_TEXT);
  if (id === "" || question === "") return null;
  const optionsRaw = Array.isArray(raw["options"]) ? raw["options"] : [];
  if (optionsRaw.length > MAX_OPTIONS) return null;
  const options: AnswerOption[] = [];
  for (const optionRaw of optionsRaw) {
    if (!isRecord(optionRaw)) return null;
    const label = text(optionRaw["label"], MAX_LABEL);
    if (label === "") return null;
    options.push({ label, description: maybeText(optionRaw["description"], MAX_QUESTION_TEXT) });
  }
  return {
    id,
    question,
    detail: maybeText(raw["detail"], MAX_QUESTION_TEXT),
    header: maybeText(raw["header"], MAX_QUESTION_TEXT),
    options,
    multiSelect: raw["multiSelect"] === true,
  };
}

/**
 * Ответ двери `GET /api/phone/question` → состояние экрана.
 * `null` — ответ не разобрался вовсе (это поломка калитки или обрезанный ответ): экран скажет
 * своё «вопрос не разобрался», а не покажет полукарточку.
 */
export function parseQuestion(raw: unknown): QuestionState | null {
  if (!isRecord(raw)) return null;
  const state = typeof raw["state"] === "string" ? raw["state"] : "";
  const eventId = text(raw["eventId"], 128);
  const message = text(raw["message"], 4000);

  switch (state) {
    case "question": {
      if (eventId === "") return null;
      const sessionId = text(raw["sessionId"], 128);
      const cardsRaw = Array.isArray(raw["questions"]) ? raw["questions"] : [];
      if (cardsRaw.length === 0 || cardsRaw.length > MAX_CARDS) return null;
      const cards: AskCard[] = [];
      for (const cardRaw of cardsRaw) {
        const card = parseCard(cardRaw);
        if (card === null) return null;
        cards.push(card);
      }
      return {
        kind: "question",
        eventId,
        sessionId,
        fromSubagent: raw["fromSubagent"] === true,
        cards,
        expiresAt: text(raw["expiresAt"], 64),
        remainingSeconds: count(raw["remainingSeconds"]),
      };
    }
    case "answered":
    case "taken":
    case "cancelled":
    case "no-answer":
    case "unreadable":
      return { kind: state, eventId, message };
    case "none":
      return { kind: "none", eventId, message };
    default:
      return null;
  }
}

/** Пустой набор ответов по карточкам. */
export function emptyAnswers(cards: AskCard[]): Record<string, AnswerValue> {
  const out: Record<string, AnswerValue> = {};
  for (const card of cards) out[card.id] = { selected: [], custom: "" };
  return out;
}

/** Выбор варианта: у radio он один, у checkbox — сколько угодно (подпись повторно снимает). */
export function toggleChoice(value: AnswerValue, label: string, multiSelect: boolean): AnswerValue {
  if (multiSelect) {
    const selected = value.selected.includes(label)
      ? value.selected.filter((item) => item !== label)
      : [...value.selected, label];
    return { ...value, selected };
  }
  return { ...value, selected: [label] };
}

/** Ответ на вопрос есть: выбран вариант или написаны свои слова (не только пробелы). */
export function hasAnswer(value: AnswerValue | undefined): boolean {
  if (value === undefined) return false;
  return value.selected.length > 0 || value.custom.trim() !== "";
}

/** «Можно отправлять» — ответ есть на КАЖДЫЙ вопрос (пустой ответ движок понял бы как пропуск). */
export function canSend(cards: AskCard[], values: Record<string, AnswerValue>): boolean {
  return cards.every((card) => hasAnswer(values[card.id]));
}

/**
 * КУДА ВЕДЁТ «ДАЛЕЕ» (D11 §5.2, DEF-146): следующая НЕОТВЕЧЕННАЯ карточка ПО КРУГУ от `from`.
 *
 * Почему по кругу: человек может ответить на первую и на третью, а вторую оставить на потом —
 * «Далее» обязано довести его до второй, а не сказать «больше некуда». Сама `from` не возвращается:
 * если неотвеченна ТОЛЬКО текущая, идти некуда — `null` (кнопка гаснет, причина — `PhonePageChooseAll`).
 *
 * Чистая функция без DOM и без состояния: переход — дело карточки (`goTo`), а «куда» — модель.
 */
export function nextUnansweredIndex(
  cards: AskCard[],
  values: Record<string, AnswerValue>,
  from: number,
): number | null {
  const count = cards.length;
  if (count < 2) return null;
  const start = Math.min(Math.max(Math.trunc(from), 0), count - 1);
  for (let step = 1; step < count; step += 1) {
    const index = (start + step) % count;
    const card = cards[index];
    if (card !== undefined && !hasAnswer(values[card.id])) return index;
  }
  return null;
}

/**
 * Тело `POST /answer`: все вопросы одним вызовом, поля по белому списку.
 * `null` — хотя бы один вопрос без ответа: такой запрос не отправляется.
 */
export function buildAnswerItems(
  cards: AskCard[],
  values: Record<string, AnswerValue>,
): AnswerItem[] | null {
  if (!canSend(cards, values)) return null;
  return cards.map((card) => {
    const value = values[card.id] ?? { selected: [], custom: "" };
    const item: AnswerItem = { id: card.id, selected: value.selected.slice(0, MAX_OPTIONS) };
    const custom = value.custom.trim();
    if (custom !== "") item.custom = custom.slice(0, MAX_CUSTOM);
    return item;
  });
}

/**
 * Можно ли опрашивать вопрос прямо сейчас: не пока человек в поле карточки и не раньше, чем
 * через 3 с после последнего нажатия/ввода (иначе перерисовка мешает писать и выбирать).
 */
export function pollAllowed(options: {
  focusedInCard: boolean;
  suspendedUntil: number;
  now: number;
}): boolean {
  if (options.focusedInCard) return false;
  return options.now >= options.suspendedUntil;
}

/** Минуты ожидания из `remainingSeconds` — для слов «за N мин. ответа не было». */
export function waitMinutes(remainingSeconds: number): number {
  return Math.max(0, Math.round(remainingSeconds / 60));
}
