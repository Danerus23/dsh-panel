// Задача агенту: можно ли её отправить, что показывает счётчик, каким `requestId` она помечена
// и что человек читает после ответа двери.
//
// Правила — контракт §7.1, §7.3 и паспорт `s3-screens.md` §3.2–§3.5:
//   * текст после `Trim()` — 1…4000; сверх предела НЕ режем молча, а говорим словами;
//   * `requestId` чеканит телефон (`crypto.randomUUID()`), на нём держится безопасный повтор;
//   * успех — только `200` с `accepted:true`; «уже не работает» — состояние, а не отказ.

import { TASK_TEXT_MAX } from "../config";

/** Что показывает счётчик и можно ли нажимать. `length` — по `Trim()`, как считает калитка. */
export type TaskCheck = {
  /** Текст, который уйдёт в дверь (уже без крайних пробелов). */
  text: string;
  length: number;
  empty: boolean;
  tooLong: boolean;
  canSend: boolean;
};

/** Одно правило на оба экрана: пусто · сверх предела · отправка уже идёт. */
export function checkTaskText(raw: string, sending = false): TaskCheck {
  const text = raw.trim();
  const length = text.length;
  const empty = length === 0;
  const tooLong = length > TASK_TEXT_MAX;
  return { text, length, empty, tooLong, canSend: !empty && !tooLong && !sending };
}

/** Форма `requestId`/`sessionId` у калитки (`PhoneLiveFrames.ValidSession`): `[A-Za-z0-9._:-]` 1…128. */
const ID_PATTERN = /^[A-Za-z0-9._:-]{1,128}$/u;

export function isRequestId(value: string): boolean {
  return ID_PATTERN.test(value);
}

/**
 * 16 случайных байт → UUID v4 строкой. Только `[0-9a-f-]`: форма калитки (`isRequestId`) выполнена,
 * а версия/вариант проставлены по RFC 4122 — как у `crypto.randomUUID()`.
 */
function uuidFromBytes(bytes: Uint8Array): string {
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0"));
  // Версия 4 (случайная) и вариант 10xx — те же разряды, что ставит браузер.
  hex[6] = ((Number.parseInt(hex[6]!, 16) & 0x0f) | 0x40).toString(16).padStart(2, "0");
  hex[8] = ((Number.parseInt(hex[8]!, 16) & 0x3f) | 0x80).toString(16).padStart(2, "0");
  return [
    hex.slice(0, 4).join(""),
    hex.slice(4, 6).join(""),
    hex.slice(6, 8).join(""),
    hex.slice(8, 10).join(""),
    hex.slice(10, 16).join(""),
  ].join("-");
}

/**
 * `requestId` одной попытки (контракт §7.1).
 *
 * **Порядок путей — по доступности, а не по вкусу:** `crypto.randomUUID()` есть только в безопасном
 * контексте (https и localhost). Телефон открывает пульт по `http://192.168.x.x` — там этого метода
 * НЕТ, и живая проверка 31.10.2026 кончилась тем, что «Отправить задачу» молчала. Запасной путь —
 * `crypto.getRandomValues()`: он доступен и в небезопасном контексте, а идентификатор из него выходит
 * той же формы и той же случайности.
 *
 * Пустая строка — честный «не смог»: вызывающий обязан СКАЗАТЬ это словами, а не молчать
 * (`attemptProblem` ниже).
 */
export function mintRequestId(): string {
  const source = globalThis.crypto;
  if (typeof source?.randomUUID === "function") return source.randomUUID();
  if (typeof source?.getRandomValues === "function") {
    try {
      return uuidFromBytes(source.getRandomValues(new Uint8Array(16)));
    } catch {
      return "";
    }
  }
  return "";
}

/** Почему попытку нельзя отправить: пусто · сверх предела · нет `requestId` (нет crypto). */
export type AttemptProblem = "empty" | "tooLong" | "no-id";

/**
 * Одно правило на обе двери: что мешает отправить. `no-id` — не «тишина»: экран обязан сказать
 * словами (существующий ключ `PhoneTaskBadRequest`: «Панель не смогла принять действие…»).
 */
export function attemptProblem(check: TaskCheck, requestId: string): AttemptProblem | null {
  if (check.empty) return "empty";
  if (check.tooLong) return "tooLong";
  if (requestId === "" || !isRequestId(requestId)) return "no-id";
  return null;
}

/**
 * Слова отказа для проблемы попытки — существующие ключи, без новых.
 *
 * Для `no-id` берём `PhoneWebCrash`: «Пульт не запустился. Обновите страницу; если не поможет —
 * подойдите к компьютеру.» — единственный ключ пульта, который в этом состоянии называет верный
 * ПУТЬ (обновить / на компьютер). Честная фраза про «браузер не дал случайных чисел» — это
 * `PhoneTaskBadRequest` калитки, но её нет в словах пульта (добавить может только C#, вне зоны t20).
 * Неточность первого тезиса названа в отчёте t20.
 */
export function problemWordKey(
  problem: AttemptProblem,
): "PhoneTaskEmpty" | "PhoneTaskTooLongFormat" | "PhoneWebCrash" {
  if (problem === "empty") return "PhoneTaskEmpty";
  if (problem === "tooLong") return "PhoneTaskTooLongFormat";
  return "PhoneWebCrash";
}

/**
 * Исход отправки для экрана. `words` — фраза калитки как есть (она уже на языке телефона);
 * `fallbackKey` — что сказать, если дверь фразу не прислала.
 */
export type TaskOutcome =
  | { kind: "none" }
  | { kind: "sending" }
  | { kind: "accepted"; words: string; fallbackKey: "PhoneTaskAccepted" }
  | { kind: "created"; words: string; fallbackKey: "PhoneTaskNewAccepted" }
  | { kind: "failed"; words: string; text: string }
  | { kind: "gone" };

/** «Принято» — это исход `200`: и только он. `words` из ответа, `fallbackKey` — запасные слова. */
export function acceptedOutcome(words: string, created: boolean): TaskOutcome {
  if (created) return { kind: "created", words, fallbackKey: "PhoneTaskNewAccepted" };
  return { kind: "accepted", words, fallbackKey: "PhoneTaskAccepted" };
}

/**
 * Отказ: фраза «что случилось + что делать» словами двери; своей причины не придумываем.
 * `text` — текст попытки: если человек его не менял, кнопка отправки становится «Повторить»
 * (тот же `requestId` — второй задачи не будет, контракт §7.1).
 */
export function failedOutcome(words: string, text: string): TaskOutcome {
  return { kind: "failed", words, text };
}
