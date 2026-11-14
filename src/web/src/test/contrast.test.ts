// Контраст считается ФОРМУЛОЙ из `styles/tokens.css`, а не на глаз (WCAG 2.2 SC 1.4.3/1.4.11).
// Тест читает сам файл токенов: поменяли цвет — проверка падает или показывает число.

import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

type Rgb = { r: number; g: number; b: number; a: number };

const TOKENS = readFileSync(join(import.meta.dirname, "../styles/tokens.css"), "utf8");

/** Разбор блоков токенов: базовый светлый, тёмный в `@media`, и два блока `data-dp-theme`. */
function blocks(): Record<"light" | "dark" | "lightAttr" | "darkAttr", Map<string, string>> {
  const out = {
    light: new Map<string, string>(),
    dark: new Map<string, string>(),
    lightAttr: new Map<string, string>(),
    darkAttr: new Map<string, string>(),
  };
  let target: Map<string, string> | null = out.light;
  let inMedia = false;
  for (const line of TOKENS.split("\n")) {
    if (line.startsWith("@media")) inMedia = true;
    if (line.trim() === ":root {") target = inMedia ? out.dark : out.light;
    if (line.includes(':root[data-dp-theme="light"]')) target = out.lightAttr;
    if (line.includes(':root[data-dp-theme="dark"]')) target = out.darkAttr;
    const match = /^\s+(--dp-[a-z0-9-]+):\s*([^;]+);/u.exec(line);
    if (match !== null && target !== null) target.set(match[1]!, match[2]!.trim());
  }
  return out;
}

function parse(value: string): Rgb {
  const hex = /^#([0-9a-f]{6})$/iu.exec(value);
  if (hex !== null) {
    const number = Number.parseInt(hex[1]!, 16);
    return { r: (number >> 16) & 255, g: (number >> 8) & 255, b: number & 255, a: 1 };
  }
  const rgba = /^rgba?\(([^)]+)\)$/u.exec(value);
  if (rgba !== null) {
    const parts = rgba[1]!.split(",").map((part) => Number.parseFloat(part.trim()));
    return { r: parts[0]!, g: parts[1]!, b: parts[2]!, a: parts[3] ?? 1 };
  }
  throw new Error(`не разобран цвет: ${value}`);
}

function channel(value: number): number {
  const normalized = value / 255;
  return normalized <= 0.04045 ? normalized / 12.92 : ((normalized + 0.055) / 1.055) ** 2.4;
}

function luminance(color: Rgb): number {
  return 0.2126 * channel(color.r) + 0.7152 * channel(color.g) + 0.0722 * channel(color.b);
}

function over(color: Rgb, base: Rgb): Rgb {
  if (color.a >= 1) return color;
  const blend = (front: number, back: number) => front * color.a + back * (1 - color.a);
  return { r: blend(color.r, base.r), g: blend(color.g, base.g), b: blend(color.b, base.b), a: 1 };
}

function ratio(foreground: Rgb, background: Rgb): number {
  const back = background.a < 1 ? over(background, { r: 255, g: 255, b: 255, a: 1 }) : background;
  const front = over(foreground, back);
  const light = Math.max(luminance(front), luminance(back));
  const dark = Math.min(luminance(front), luminance(back));
  return (light + 0.05) / (dark + 0.05);
}

const PAIRS: Array<{ name: string; fg: string; bg: string; min: number; over?: string }> = [
  { name: "основной текст на странице", fg: "ink", bg: "page", min: 4.5 },
  { name: "основной текст на карточке", fg: "ink", bg: "card", min: 4.5 },
  { name: "основной текст в плашке человека", fg: "ink", bg: "user", min: 4.5 },
  { name: "тихий текст на странице", fg: "ink-2", bg: "page", min: 4.5 },
  { name: "тихий текст на карточке", fg: "ink-2", bg: "card", min: 4.5 },
  { name: "текст на главной кнопке", fg: "accent-ink", bg: "accent", min: 4.5 },
  { name: "ссылка на странице", fg: "accent", bg: "page", min: 4.5 },
  { name: "ошибка на карточке", fg: "danger", bg: "card", min: 4.5 },
  { name: "ошибка на своей плашке", fg: "danger", bg: "danger-wash", min: 4.5 },
  { name: "текст на кнопке отмены", fg: "danger-ink", bg: "danger", min: 4.5 },
  // Плашки S3: «принято» и «остановлено» стоят на `accent-wash`, а «Убрать» — слово действия
  // в цвете акцента. Без этих двух пар новые плашки прошли бы молча (находка t2 §5.4).
  { name: "текст в плашке акцента", fg: "ink", bg: "accent-wash", min: 4.5 },
  { name: "действие-ссылка в плашке акцента", fg: "accent", bg: "accent-wash", min: 4.5 },
  { name: "предупреждение на карточке", fg: "warn", bg: "card", min: 4.5 },
  { name: "граница органа на карточке", fg: "line", bg: "card", min: 3 },
  // Плавающий орган «к последнему» — ПЕРВЫЙ в пульте, у кого рамка стоит на СТРАНИЦЕ, а не на карточке
  // (P2 §3.2): пара line/page — 5,07 светлая и 5,54 тёмная, пол 3:1.
  { name: "граница органа на странице", fg: "line", bg: "page", min: 3 },
  { name: "контур фокуса на карточке", fg: "focus", bg: "card", min: 3 },
  // Тон «хорошо» (S3e): состояние «закончила» — слово в мете и значок в круге. Пара «текст» —
  // 4,5:1, значок-графика — 3:1 (WCAG 2.2 SC 1.4.3/1.4.11). `accent` — значок «работает».
  { name: "текст «хорошо» на странице", fg: "good", bg: "page", min: 4.5 },
  { name: "текст «хорошо» на карточке", fg: "good", bg: "card", min: 4.5 },
  { name: "значок состояния «хорошо» на карточке", fg: "good", bg: "card", min: 3 },
  { name: "значок «работает» на карточке", fg: "accent", bg: "card", min: 3 },
  // Разметка реплики (S3c, D1 В-8): подложка кода — токен `line-soft`, а он `rgba`, то есть
  // полупрозрачный. Поэтому пары для него считаются СМЕШЕНИЕМ с тем, на чём он лежит (`page`),
  // и проверяются в обеих темах: иначе тест сравнивал бы не то, что видит человек.
  { name: "текст кода на подложке кода", fg: "ink", bg: "line-soft", over: "page", min: 4.5 },
  { name: "текст цитаты на странице", fg: "ink-2", bg: "page", min: 4.5 },
  { name: "граница цитаты на странице", fg: "line", bg: "page", min: 3 },
];

describe("контраст токенов", () => {
  const parsed = blocks();

  it("токены разобраны из файла, а не выдуманы тестом", () => {
    for (const map of [parsed.light, parsed.dark, parsed.lightAttr, parsed.darkAttr]) {
      expect(map.size).toBeGreaterThanOrEqual(14);
    }
  });

  it("светлая тема задана одинаково в базовом блоке и в блоке по системе", () => {
    expect([...parsed.lightAttr.entries()].sort()).toEqual([...parsed.light.entries()].sort());
    expect([...parsed.darkAttr.entries()].sort()).toEqual([...parsed.dark.entries()].sort());
  });

  for (const [theme, map] of [
    ["светлая", parsed.light],
    ["тёмная", parsed.dark],
  ] as const) {
    it(`${theme} тема проходит пороги 4,5:1 и 3:1`, () => {
      for (const pair of PAIRS) {
        const fg = map.get(`--dp-${pair.fg}`);
        const bg = map.get(`--dp-${pair.bg}`);
        expect(fg, `нет токена --dp-${pair.fg}`).toBeDefined();
        expect(bg, `нет токена --dp-${pair.bg}`).toBeDefined();
        // Полупрозрачную подложку сначала кладём на её основание — только так число будет правдой.
        const under = pair.over === undefined ? undefined : map.get(`--dp-${pair.over}`);
        if (pair.over !== undefined) expect(under, `нет токена --dp-${pair.over}`).toBeDefined();
        const base = under === undefined ? parse(bg!) : over(parse(bg!), parse(under));
        const value = ratio(parse(fg!), base);
        expect(value, `${pair.name}: ${value.toFixed(2)}`).toBeGreaterThanOrEqual(pair.min);
      }
    });
  }
});
