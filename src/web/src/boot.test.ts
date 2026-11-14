import { describe, expect, it } from "vitest";
import { BootError, parseBoot } from "./boot";

describe("boot", () => {
  it("читает язык, вход, устройство и слова", () => {
    const boot = parseBoot(
      JSON.stringify({ lang: "zh", authed: true, device: "телефон", words: { A: "слово" } }),
    );
    expect(boot.lang).toBe("zh");
    expect(boot.authed).toBe(true);
    expect(boot.device).toBe("телефон");
    expect(boot.words["A"]).toBe("слово");
  });

  it("без атрибута — ошибка, а не пустая страница", () => {
    expect(() => parseBoot(null)).toThrow(BootError);
    expect(() => parseBoot("")).toThrow(BootError);
  });

  it("кривой JSON и чужой язык отвергаются", () => {
    expect(() => parseBoot("{oops")).toThrow(BootError);
    expect(() =>
      parseBoot(JSON.stringify({ lang: "de", authed: true, device: "x", words: {} })),
    ).toThrow(BootError);
  });

  it("нестроковые слова отбрасываются, а не ломают экран", () => {
    const boot = parseBoot(
      JSON.stringify({ lang: "ru", authed: false, device: "", words: { A: "ok", B: 5, C: null } }),
    );
    expect(boot.words).toEqual({ A: "ok" });
    expect(boot.authed).toBe(false);
  });
});
