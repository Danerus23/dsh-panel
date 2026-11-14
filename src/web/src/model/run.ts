// Запись ленты в «ход идёт?» — по событиям `turn`/`turn-end` живого потока (контракт §5.8).
//
// Это признак для строки стопа: «Остановить» видна только тогда, когда есть что останавливать.
// Запасной признак — `running` из списка; он отстаёт до следующего перечитывания списка (таймера
// у списка нет с S4a, `useSessions.ts`), и нужен только тогда, когда в окне ленты ещё нет
// ни одного `turn` (пустая или очень короткая сессия).

import type { Draft, FeedRecord } from "./records";
import { stateTone, stateWordKey, toneForWord, type SessionItem, type StateTone } from "./list";
import type { WordKey } from "../i18n";

export function isRunActive(
  records: FeedRecord[],
  draft: Draft | null,
  fallbackRunning = false,
): boolean {
  for (let index = records.length - 1; index >= 0; index -= 1) {
    const record = records[index]!;
    if (record.type === "turn") return true;
    if (record.type === "turn-end") return false;
  }
  return draft !== null || fallbackRunning;
}

/** Сколько событий хода в ленте — по изменению этого числа экран перечитывает очередь. */
export function turnEvents(records: FeedRecord[]): number {
  let count = 0;
  for (const record of records)
    if (record.type === "turn" || record.type === "turn-end") count += 1;
  return count;
}

/** Слово и тон состояния в шапке сессии. `null` — не печатать мету вовсе. */
export type SessionState = { key: WordKey; tone: StateTone };

/**
 * СОСТОЯНИЕ ШАПКИ СЕССИИ — ИЗ ЖИВЫХ ДАННЫХ (D3 §1.2, слово владельца 11.11.2026: «состояние
 * не живое»). Строка списка — только ПОСЛЕДНЕЕ ИЗВЕСТНОЕ, и только пока поток не живой: список
 * читается тремя поводами без таймера (монтирование, возврат на вкладку, `stale`), поэтому его
 * слово в открытом разговоре устаревает.
 *
 * Порядок — таблица D3: вопрос → ход идёт → есть записи/черновик → пусто; пока поток не живой —
 * слово из строки, КРОМЕ «Работает» (обещать анимацией текущий процесс, которого не видим, нельзя).
 * `runActive` — ТОТ ЖЕ вызов `isRunActive`, что у кнопки «Остановить»: предикат не дублируется.
 *
 * ⚠️ DEF-150 (слово владельца 12.11.2026): «в списке работает, а в сессии „закончила“». Причина —
 * ход корня кончился (`turn-end` в ленте), а подчинённая ещё работает; агрегат «корень ИЛИ
 * подчинённая» живёт только в строке списка (`PhoneLive.cs`, W3). Поэтому у живого потока есть
 * ВТОРОЙ признак — `rootRunning` (это `row.running`): «Работает», если свой ход идёт ИЛИ строка
 * говорит, что работа идёт. Вопрос — по-прежнему сильнее всего. «Остановить» этот признак НЕ
 * берёт: с телефона останавливаем только собственный ход.
 */
export function sessionState(input: {
  row: SessionItem | null;
  streamLive: boolean;
  pendingHere: boolean;
  runActive: boolean;
  /** Строка списка говорит «работа идёт» (свой флаг ИЛИ работающая подчинённая — агрегат W3). */
  rootRunning: boolean;
  /** Есть ли в ленте записи или черновик: «Закончила» против «Ещё не было задачи». */
  hasFeed: boolean;
}): SessionState | null {
  const { row, streamLive, pendingHere, runActive, rootRunning, hasFeed } = input;

  if (pendingHere)
    return { key: "PhoneWebStateWaiting", tone: toneForWord("PhoneWebStateWaiting") };

  if (!streamLive) {
    // Пока поток не живой — говорим последнее прочитанное, но НЕ «Работает»: это единственное
    // слово о текущем процессе (и единственное, которое мы анимируем). Строки ещё нет — молчим.
    if (row === null) return null;

    const key = stateWordKey(row);

    return key === "PhoneWebStateRunning" ? null : { key, tone: stateTone(row) };
  }

  // Свой ход ИЛИ работающая подчинённая (DEF-150): второе видно только в строке списка, и она
  // обновляется живой подсказкой (`useSessions.ts`) — поэтому «Работает» гаснет за ≤3 с.
  if (runActive || rootRunning)
    return { key: "PhoneWebStateRunning", tone: toneForWord("PhoneWebStateRunning") };
  // Строки списка ещё нет — «Закончила»/«Ещё не было задачи» не печатаем: это последнее известное
  // из списка, а не живой факт (D3 §1.2, п. 5 и оговорка). Ждать/работать строку не требуют.
  if (row === null) return null;
  if (hasFeed) return { key: "PhoneWebStateDone", tone: toneForWord("PhoneWebStateDone") };

  return { key: "PhoneWebStateBlank", tone: toneForWord("PhoneWebStateBlank") };
}
