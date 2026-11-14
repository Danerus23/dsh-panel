// ЗНАЧОК СОСТОЯНИЯ — цвет и значок ВМЕСТЕ со словом (карточка 32a О-3; `10-foundations.md` §3.1).
//
// Почему так, а не «просто цветная точка»: цвет не смеет быть единственным носителем состояния
// (WCAG 1.4.1) — слово остаётся в мете рядом, а значок даёт узнавание до чтения. Четыре тона
// закреплены за состояниями и совпадают с порядком слов (`model\list.ts` — `stateWordKey`/`stateTone`).
//
// Значок — ЧАСТЬ меты, а не отдельный орган: он `aria-hidden` (слово и так читается), размеров
// ряда не меняет (`10-foundations.md` §1.11), цели нажатия не создаёт.
//
// ВРАЩЕНИЕ (D3 §3, слово владельца 11.11.2026: «кругляшок когда „работает“ не крутится… хотелось
// бы анимацию»): только у тона `accent` и только когда экран передал `spin`. Механику даёт сам
// Semi (`spin` → `semi-icon-spinning`: 600 мс linear infinite 0→360°), своего `@keyframes` нет.
// Вращается `transform`, значок — квадрат 1em: раскладка не двигается. `prefers-reduced-motion`
// гасит общий сторож `app.css`; в списке `spin` НЕ передаётся — там состояние не живое (D3 §4).

import { IconAlertCircle, IconLoading, IconRadio, IconTickCircle } from "@douyinfe/semi-icons";
import type { StateTone } from "../model/list";

const ICONS = {
  good: IconTickCircle,
  warn: IconAlertCircle,
  accent: IconLoading,
  neutral: IconRadio,
} as const;

/** `spin` — «сейчас правда идёт работа»: принимается только тоном `accent` (остальные стоят). */
export function StateMark({ tone, spin = false }: { tone: StateTone; spin?: boolean }) {
  const Icon = ICONS[tone];

  return (
    <span className={`dp-state-mark dp-state-${tone}`} aria-hidden="true">
      <Icon size="small" spin={tone === "accent" && spin} />
    </span>
  );
}
