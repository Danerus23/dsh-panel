// check-lang.mjs — сторожок словарей интерфейса.
//
// Сверяет три словаря (lang\ru.json, lang\en.json, lang\zh.json): одинаковый ли набор
// ключей, нет ли пустых значений и не разъехались ли подстановки вида {0}. Английский
// считается образцом: он же запасной вариант, когда в языке ключа нет.
//
// Запуск:  node tools\check-lang.mjs
// Выход:   0 — всё сходится, 1 — есть расхождения (годно для сборки и CI).

import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const languages = ["ru", "en", "zh"];
const reference = "en";

const dictionaries = {};
for (const language of languages) {
  dictionaries[language] = JSON.parse(readFileSync(join(root, "lang", `${language}.json`), "utf8"));
}

let problems = 0;
const keysOf = (dictionary) => Object.keys(dictionary).sort();
const placeholders = (text) => (String(text).match(/\{\d+(?::[^}]*)?\}/g) ?? []).sort().join(",");
const referenceKeys = keysOf(dictionaries[reference]);

for (const language of languages) {
  const keys = keysOf(dictionaries[language]);

  if (language !== reference) {
    const missing = referenceKeys.filter((key) => !keys.includes(key));
    const extra = keys.filter((key) => !referenceKeys.includes(key));
    if (missing.length) {
      console.log(`${language}: нет ключей — ${missing.join(", ")}`);
      problems++;
    }
    if (extra.length) {
      console.log(`${language}: лишние ключи — ${extra.join(", ")}`);
      problems++;
    }
  }

  for (const [key, value] of Object.entries(dictionaries[language])) {
    if (typeof value !== "string" || value.trim() === "") {
      console.log(`${language}: пустое значение у ${key}`);
      problems++;
      continue;
    }

    if (language !== reference && dictionaries[reference][key] !== undefined
        && placeholders(value) !== placeholders(dictionaries[reference][key])) {
      console.log(`${language}: подстановки в ${key} не совпадают с английским `
        + `(${placeholders(value) || "нет"} против ${placeholders(dictionaries[reference][key]) || "нет"})`);
      problems++;
    }
  }
}

if (problems === 0) {
  console.log(`словари сходятся: ${referenceKeys.length} ключей × ${languages.length} языка`);
}

process.exit(problems === 0 ? 0 : 1);
