import { describe, expect, it } from "vitest";
import { formatTime, formatWord, joinReason, makeT, missingKeys } from "./words";
import { S1_WORD_KEYS } from "./i18n";

describe("слова", () => {
  it("подставляет {0} и {1}, как словари панели", () => {
    expect(formatWord("Ходов: {0}", [3])).toBe("Ходов: 3");
    expect(formatWord("{0} из {1}", [1, 2])).toBe("1 из 2");
    expect(formatWord("без чисел", [])).toBe("без чисел");
  });

  it("недостающий ключ виден ключом, а не пустой строкой", () => {
    const t = makeT({ PhoneWebListTitle: "Сессии" });
    expect(t("PhoneWebListTitle")).toBe("Сессии");
    expect(t("PhoneWebRefresh")).toBe("PhoneWebRefresh");
    expect(missingKeys({ PhoneWebListTitle: "Сессии" }, S1_WORD_KEYS)).toContain("PhoneWebRefresh");
  });

  it("пустая строка в словаре — тоже пропуск", () => {
    const t = makeT({ PhoneWebRefresh: "" });
    expect(t("PhoneWebRefresh")).toBe("PhoneWebRefresh");
  });

  it("причина без слов не оставляет висящего тире", () => {
    expect(joinReason("Не удалось войти —", "")).toBe("Не удалось войти");
    expect(joinReason("Ответ не отправлен:", "нет связи")).toBe("Ответ не отправлен: нет связи");
  });

  it("время строки — по языку телефона, мусор — пусто", () => {
    expect(formatTime(1761770000000, "ru")).not.toBe("");
    expect(formatTime(0, "ru")).toBe("");
    expect(formatTime(Number.NaN, "en")).toBe("");
  });
});
