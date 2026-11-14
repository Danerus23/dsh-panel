// Правила задачи: предел 4000, пустое, снимок попытки, `requestId`, исходы.

import { describe, expect, it } from "vitest";
import { TASK_TEXT_MAX } from "../config";
import {
  acceptedOutcome,
  attemptProblem,
  checkTaskText,
  failedOutcome,
  isRequestId,
  mintRequestId,
  problemWordKey,
} from "./task";

describe("текст задачи", () => {
  it("пустой и из одних пробелов отправлять нечего", () => {
    expect(checkTaskText("").canSend).toBe(false);
    expect(checkTaskText("   \n  ").canSend).toBe(false);
    expect(checkTaskText("   \n  ").empty).toBe(true);
  });

  it("текст уходит без крайних пробелов — как считает калитка", () => {
    const check = checkTaskText("  починить сборку  ");
    expect(check.text).toBe("починить сборку");
    expect(check.length).toBe(15);
    expect(check.canSend).toBe(true);
  });

  it("предел 4000: ровно 4000 можно, 4001 — нет и не режем молча", () => {
    const atLimit = checkTaskText("я".repeat(TASK_TEXT_MAX));
    expect(atLimit.tooLong).toBe(false);
    expect(atLimit.canSend).toBe(true);

    const over = checkTaskText("я".repeat(TASK_TEXT_MAX + 1));
    expect(over.tooLong).toBe(true);
    expect(over.canSend).toBe(false);
    expect(over.text.length).toBe(TASK_TEXT_MAX + 1);
  });

  it("в полёте второе нажатие не проходит", () => {
    expect(checkTaskText("задача", true).canSend).toBe(false);
  });
});

describe("requestId", () => {
  it("чеканится по форме калитки и не повторяется", () => {
    const first = mintRequestId();
    const second = mintRequestId();
    expect(isRequestId(first)).toBe(true);
    expect(first).not.toBe(second);
  });

  it("БЕЗ crypto.randomUUID (телефон по http — небезопасный контекст) чеканится через getRandomValues", () => {
    // Живая проверка 31.10.2026: на http://192.168.x.x randomUUID нет вовсе — и отправка молчала.
    const real = globalThis.crypto.randomUUID;
    Object.defineProperty(globalThis.crypto, "randomUUID", {
      value: undefined,
      configurable: true,
    });
    try {
      const id = mintRequestId();
      expect(id, "не пустая строка").not.toBe("");
      expect(isRequestId(id), `форма калитки: ${id}`).toBe(true);
      // RFC 4122 v4: версия и вариант на своих местах — как у randomUUID.
      expect(id[14]).toBe("4");
      expect(["8", "9", "a", "b"]).toContain(id[19]);
      expect(mintRequestId()).not.toBe(id);
    } finally {
      Object.defineProperty(globalThis.crypto, "randomUUID", { value: real, configurable: true });
    }
  });

  it("нет ни randomUUID, ни getRandomValues — пустая строка, и это НЕ тишина", () => {
    const source = globalThis.crypto as unknown as Record<string, unknown>;
    const realUUID = source.randomUUID;
    const realRandom = source.getRandomValues;
    Object.defineProperty(source, "randomUUID", { value: undefined, configurable: true });
    Object.defineProperty(source, "getRandomValues", { value: undefined, configurable: true });
    try {
      expect(mintRequestId(), "чеканить нечем").toBe("");
      // Экран обязан СКАЗАТЬ, а не молчать: у попытки есть проблема, и у неё есть слова.
      const check = checkTaskText("Почини сборку");
      expect(attemptProblem(check, ""), "пустой requestId — проблема попытки").toBe("no-id");
      expect(attemptProblem(check, "не форма"), "чужая форма — тоже").toBe("no-id");
      expect(problemWordKey("no-id"), "слова существующего ключа пульта").toBe("PhoneWebCrash");
    } finally {
      Object.defineProperty(source, "randomUUID", { value: realUUID, configurable: true });
      Object.defineProperty(source, "getRandomValues", { value: realRandom, configurable: true });
    }
  });

  it("пусто и сверх предела решаются до чеканки id", () => {
    expect(attemptProblem(checkTaskText("   "), "id-1")).toBe("empty");
    expect(attemptProblem(checkTaskText("x".repeat(TASK_TEXT_MAX + 1)), "id-1")).toBe("tooLong");
    expect(attemptProblem(checkTaskText("Почини сборку"), "id-1")).toBeNull();
  });

  it("чужая форма не принимается", () => {
    expect(isRequestId("")).toBe(false);
    expect(isRequestId("с пробелом")).toBe(false);
    expect(isRequestId("x".repeat(129))).toBe(false);
    expect(isRequestId("OK-1_2:3.4")).toBe(true);
  });
});

describe("исходы", () => {
  it("принято — только явный исход, слова двери сохраняются", () => {
    expect(acceptedOutcome("Задача принята.", false)).toEqual({
      kind: "accepted",
      words: "Задача принята.",
      fallbackKey: "PhoneTaskAccepted",
    });
    expect(acceptedOutcome("", true)).toEqual({
      kind: "created",
      words: "",
      fallbackKey: "PhoneTaskNewAccepted",
    });
  });

  it("отказ — одна фраза, без кода и без оценки", () => {
    const outcome = failedOutcome(
      "Задача не принята: Сервер DSH не ответил. Попробуйте ещё раз.",
      "почини",
    );
    expect(outcome.kind).toBe("failed");
    expect(JSON.stringify(outcome)).not.toContain("502");
  });
});
