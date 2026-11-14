// Загрузочные данные страницы (`boot`) — их кладёт калитка в атрибут `data-boot` элемента
// `#root` (контракт §2.8). Никакого inline-скрипта нет: политика CSP запрещает `unsafe-inline`.
//
// Слова пульта живут В СЛОВАРЯХ ПАНЕЛИ (`Localization\{ru,en,zh}.json`) и приходят сюда же;
// своего словаря у веба нет. Поэтому `words` — просто карта ключ → строка.

export const LANGS = ["ru", "en", "zh"] as const;
export type Lang = (typeof LANGS)[number];

export type Boot = {
  lang: Lang;
  authed: boolean;
  device: string;
  words: Record<string, string>;
};

export class BootError extends Error {}

function isLang(value: unknown): value is Lang {
  return typeof value === "string" && (LANGS as readonly string[]).includes(value);
}

/** Разбирает `data-boot`. Кривой атрибут — это поломка калитки, а не «пустая страница». */
export function parseBoot(raw: string | null | undefined): Boot {
  if (!raw || raw.trim() === "") throw new BootError("data-boot is empty");
  let value: unknown;
  try {
    value = JSON.parse(raw);
  } catch {
    throw new BootError("data-boot is not JSON");
  }
  if (typeof value !== "object" || value === null)
    throw new BootError("data-boot is not an object");
  const record = value as Record<string, unknown>;
  if (!isLang(record["lang"])) throw new BootError("data-boot.lang is not ru|en|zh");
  if (typeof record["authed"] !== "boolean") throw new BootError("data-boot.authed is not boolean");
  if (typeof record["device"] !== "string") throw new BootError("data-boot.device is not a string");
  const source = record["words"];
  if (typeof source !== "object" || source === null)
    throw new BootError("data-boot.words is not an object");
  const words: Record<string, string> = {};
  for (const [key, text] of Object.entries(source as Record<string, unknown>)) {
    if (typeof text === "string") words[key] = text;
  }
  return { lang: record["lang"], authed: record["authed"], device: record["device"], words };
}

/** Читает boot из документа. Отдельно — чтобы это можно было проверить без браузера. */
export function readBoot(root: HTMLElement | null): Boot {
  return parseBoot(root?.dataset["boot"]);
}
