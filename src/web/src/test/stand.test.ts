// Сторож стенда и адреса: две находки приёмки t21 не должны вернуться молча.
//
// DEF-096: `reuseExistingServer: true` подхватывал ЧУЖОЙ стенд, занявший порт (другое дерево,
// другой билд) — и мутации выглядели зелёными, хотя проверялся чужой код. Ловится чтением конфига:
// сторож краснеет ровно тогда, когда переиспользование вернули.
//
// DEF-097: адрес стенда был зашит строкой `127.0.0.1:43217` в `perf.spec.ts` и `s3.spec.ts` —
// сменить порт или гонять два дерева рядом было нельзя. Адрес теперь один, в `e2e/base.ts`.

import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

const ROOT = join(import.meta.dirname, "..", "..");

function read(relative: string): string {
  return readFileSync(join(ROOT, relative), "utf8");
}

describe("стенд e2e", () => {
  it("не переиспользует чужой стенд, а падает громко (DEF-096)", () => {
    const config = read("playwright.config.ts");

    // Смотрим ДЕЙСТВУЮЩУЮ настройку, а не комментарий о ней: строки-комментарии отбрасываем.
    const code = config
      .split(/\r?\n/)
      .filter((line) => !line.trimStart().startsWith("//") && !line.trimStart().startsWith("*"))
      .join("\n");

    expect(code, "reuseExistingServer обязан быть false").toContain("reuseExistingServer: false");
    expect(code, "true вернул бы ложные зелёные").not.toContain("reuseExistingServer: true");
  });

  it("адрес стенда живёт в одном месте, а не в проверках (DEF-097)", () => {
    const base = read("e2e/base.ts");
    expect(base, "порт по умолчанию — в base.ts").toContain("DSH_E2E_PORT");
    expect(base, "адрес по умолчанию — в base.ts").toContain("DSH_E2E_BASE_URL");

    const specs = readdirSync(join(ROOT, "e2e")).filter(
      (name) => name.endsWith(".spec.ts") && name !== "base.ts",
    );
    expect(specs.length, "проверки e2e на месте").toBeGreaterThan(5);

    const hardcoded = specs.filter((name) => read(join("e2e", name)).includes("127.0.0.1:43217"));
    expect(hardcoded, `зашитый адрес вернулся в: ${hardcoded.join(", ")}`).toEqual([]);
  });
});
