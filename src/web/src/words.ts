// Слова экрана: ключ → строка из словарей панели (пришли в `boot.words`) + подстановки `{0}`.
//
// Своего словаря у веба НЕТ (контракт §3.7): недостающий ключ — это видимая поломка калитки,
// поэтому вместо пустой строки показывается сам ключ, а проверка `i18n.test.ts` требует, чтобы
// каждый ключ, который экран зовёт, входил в согласованный список и был непуст на трёх языках.

import type { Lang } from "./boot";
import { WORD_KEYS, type WordKey } from "./i18n";

export type Words = Record<string, string>;
export type T = (key: WordKey, ...args: Array<string | number>) => string;

/** Подстановка `{0}`, `{1}` — как в словарях панели (`string.Format`). */
export function formatWord(template: string, args: Array<string | number>): string {
  let out = template;
  for (let i = 0; i < args.length; i += 1) out = out.split(`{${i}}`).join(String(args[i]));
  return out;
}

/**
 * «Причина после двоеточия/тире», когда причина неизвестна: у словарей панели есть шаблоны вида
 * «Ответ не отправлен: {0}» — с пустой причиной висящее тире выглядело бы поломкой.
 */
export function joinReason(prefix: string, reason: string): string {
  const tail = reason.trim();
  if (tail !== "") return `${prefix} ${tail}`.trim();
  return prefix.replace(/[\s—–:-]+$/u, "");
}

/**
 * Переводчик. Ключи, которых в словаре нет, возвращаются ИМЕНЕМ КЛЮЧА: пустая строка на экране
 * сказала бы «тут ничего нет», а ключ сразу называет виновника — и валит `i18n.test.ts`.
 */
export function makeT(words: Words): T {
  return (key, ...args) => {
    const text = words[key];
    if (typeof text !== "string" || text === "") return key;
    return args.length === 0 ? text : formatWord(text, args);
  };
}

/** Ключ, которого нет в словаре, — это не «нет слов», а названная поломка. */
export function missingKeys(words: Words, keys: readonly string[] = WORD_KEYS): string[] {
  return keys.filter((key) => typeof words[key] !== "string" || words[key] === "");
}

const LOCALES: Record<Lang, string> = { ru: "ru-RU", en: "en-GB", zh: "zh-CN" };

/** Дата/время строки списка — `Intl` на языке телефона, а не своя верстка числа. */
export function formatTime(ms: number, lang: Lang): string {
  if (!Number.isFinite(ms) || ms <= 0) return "";
  try {
    return new Intl.DateTimeFormat(LOCALES[lang], {
      day: "2-digit",
      month: "short",
      hour: "2-digit",
      minute: "2-digit",
    }).format(new Date(ms));
  } catch {
    return "";
  }
}

export { WORD_KEYS };
export type { WordKey };
