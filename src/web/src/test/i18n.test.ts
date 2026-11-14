// Паритет трёх словарей и честность ключей.
//
// Своего словаря веб НЕ имеет (контракт §3.7): тексты живут в `Localization\{ru,en,zh}.json`.
// Эта проверка ловит четыре беды:
//   1) ключ, который зовёт экран, но которого нет в согласованном списке (опечатка);
//   2) ключ, который есть не во всех трёх языках (паритет) — тогда живая страница покажет ИМЯ КЛЮЧА;
//   3) повтор ключа в одном файле (JSON.parse прячет дубль, человек видит не то слово);
//   4) расхождение подстановок `{0}` по языкам (падение `string.Format` прямо в окне).

import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { EXTRA_WORD_KEYS, NEW_WORD_KEYS, S1_WORD_KEYS, WORD_KEYS } from "../i18n";

const SRC = join(import.meta.dirname, "..");
const DICT = join(import.meta.dirname, "../../../DshPanel/Localization");
const OVERLAY = join(SRC, "../e2e/fixtures/words-overlay.json");
const LANGS = ["ru", "en", "zh"] as const;

type Dict = Record<string, string>;

function readJson(path: string): Dict {
  return JSON.parse(readFileSync(path, "utf8")) as Dict;
}

const panel = {
  ru: readJson(join(DICT, "ru.json")),
  en: readJson(join(DICT, "en.json")),
  zh: readJson(join(DICT, "zh.json")),
};
const overlay = readJson(OVERLAY) as unknown as Record<(typeof LANGS)[number], Dict>;
const overlayTexts = readJson(OVERLAY) as unknown as Record<(typeof LANGS)[number], Dict>;

function sourceFiles(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) out.push(...sourceFiles(full));
    else if (/\.(ts|tsx)$/u.test(full) && !/\.test\./u.test(full)) out.push(full);
  }
  return out;
}

function placeholders(text: string): string {
  return [...new Set([...text.matchAll(/\{(\d+)\}/gu)].map((match) => match[1]))].sort().join(",");
}

describe("слова и ключи", () => {
  // 42 ключа S1+S2 + 22 экранных S3 (паспорт `s3-screens.md` §6) + 4 слова S4a (D1) + 2 слова хвоста S4a
  // (P2) + 1 добавка капитана + 1 слово fix-захода 11.11.2026 (`PhoneWebToTop`, D1 §9)
  // + 1 слово W10 12.11.2026 (`PhonePageNextButton`, D11 §5.2/§7.1).
  it("новых ключей ровно 72 (71 + «Далее» W10), повторов нет", () => {
    expect(NEW_WORD_KEYS).toHaveLength(72);
    expect(EXTRA_WORD_KEYS).toHaveLength(1);
    expect(new Set(WORD_KEYS).size).toBe(WORD_KEYS.length);
  });

  it("каждый ключ, который зовёт экран, есть в согласованном списке", () => {
    const used = new Set<string>();
    for (const file of sourceFiles(SRC)) {
      const text = readFileSync(file, "utf8");
      for (const match of text.matchAll(/\bt\("(Phone[A-Za-z]+)"/gu)) used.add(match[1]!);
    }
    expect(used.size).toBeGreaterThan(10);
    const unknown = [...used].filter((key) => !(WORD_KEYS as readonly string[]).includes(key));
    expect(unknown, `неизвестные ключи: ${unknown.join(", ")}`).toEqual([]);
  });

  it("КАЖДЫЙ ключ веба есть в трёх словарях панели и непуст — значит имени ключа на экране не будет", () => {
    for (const key of WORD_KEYS) {
      for (const lang of LANGS) {
        const text = panel[lang][key];
        expect(typeof text, `${lang}.${key} отсутствует`).toBe("string");
        expect(text, `${lang}.${key} пуст`).not.toBe("");
      }
    }
  });

  it("ключи S1 входят в общий список (ни один экран S1 не может разойтись с проверкой)", () => {
    for (const key of S1_WORD_KEYS) {
      expect(WORD_KEYS as readonly string[]).toContain(key);
    }
  });

  it("в файлах словарей нет повторного ключа", () => {
    for (const lang of LANGS) {
      const text = readFileSync(join(DICT, `${lang}.json`), "utf8");
      // Ключ свойства — строка сразу после `{` или `,` (в файле есть две записи в одной строке,
      // поэтому `^` не годится; разделители ловят и их и не задевают текст значения).
      const keys = [...text.matchAll(/(?:^|[,{]\s*)"((?:[^"\\]|\\.)+)"\s*:/gmu)].map(
        (match) => match[1]!,
      );
      const seen = new Set<string>();
      const duplicates = keys.filter((key) => {
        if (seen.has(key)) return true;
        seen.add(key);
        return false;
      });
      expect(duplicates, `${lang}: повтор ключей ${duplicates.join(", ")}`).toEqual([]);
      expect(
        seen.size,
        `${lang}: разобрано ключей ${seen.size}, в объекте ${Object.keys(panel[lang]).length}`,
      ).toBe(Object.keys(panel[lang]).length);
    }
  });

  it("подстановки {0}… не расходятся по языкам", () => {
    for (const key of WORD_KEYS) {
      const forms = LANGS.map((lang) => placeholders(panel[lang][key] ?? ""));
      expect(new Set(forms).size, `${key}: ${forms.join(" | ")}`).toBe(1);
    }
  });

  it("фикстура e2e: те же 70 ключей и паритет трёх языков", () => {
    for (const lang of LANGS) {
      const keys = Object.keys(overlayTexts[lang]).sort();
      expect(keys).toEqual([...NEW_WORD_KEYS].sort());
      for (const key of NEW_WORD_KEYS) {
        expect(overlayTexts[lang][key]!.length, `${lang}.${key} пуст`).toBeGreaterThan(0);
      }
    }
    expect(overlay.ru["PhoneWebListTitle"]).toBe("Сессии");
  });

  it("фикстура совпадает со словарём панели — стенд говорит те же слова, что живая страница", () => {
    for (const lang of LANGS) {
      for (const key of NEW_WORD_KEYS) {
        expect(panel[lang][key], `${lang}.${key} разошёлся с фикстурой`).toBe(overlay[lang][key]);
      }
    }
  });
});
