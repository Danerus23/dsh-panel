// ЧИСЛА НИЗА — ОДНИМ утверждением: «меняешь число — меняешь и проверку».
//
// Вердикт t10 §1.3д: `AUTOSCROLL_SLACK_PX` — это НЕ 100, а запас снизу + 100. Иначе после первого же
// нового сообщения лента перестаёт считаться «низом», автоскролл выключается и живой конец уползает
// под полосу. Мутация «вернуть 100» обязана красить этот файл.
//
// D11 §1.2 (12.11.2026): шар убран — внизу зацеп 44, запас 52 (44 + зазор 8), а живое значение
// приходит измеренным (`--dp-bottom-reserve` пишет `useBottomReserve`). Без измерения (jsdom, тест без
// DOM) функция обязана брать ровно запасной путь 52, а не «примерно столько».

import { describe, expect, it } from "vitest";
import {
  AUTOSCROLL_SLACK_PX,
  BOTTOM_GAP_PX,
  BOTTOM_RESERVE_PX,
  DRAFT_NEW_KEY,
  HANDLE_HEIGHT_PX,
  LEGACY_BALL_KEY,
  TASK_TEXT_MAX,
  QUEUE_LIMIT,
  autoscrollSlackPx,
  bottomReservePx,
} from "../config";

describe("числа S3", () => {
  it("допуск автоскролла = запас снизу + 100, и это ОДНО число на все состояния", () => {
    expect(AUTOSCROLL_SLACK_PX).toBe(100);
    expect(HANDLE_HEIGHT_PX, "цель пальца у зацепа").toBe(44);
    expect(BOTTOM_GAP_PX, "зазор между зацепом и тем, что над ним").toBe(8);
    expect(BOTTOM_RESERVE_PX, "запас = 44 зацепа + 8 зазора").toBe(52);
    // jsdom без таблицы стилей: измерения нет — работает запасной путь.
    expect(bottomReservePx()).toBe(52);
    expect(autoscrollSlackPx()).toBe(152);
  });

  it("запас снизу читается ИЗМЕРЕННЫМ числом, а не константой", () => {
    const root = document.documentElement;
    root.style.setProperty("--dp-bottom-reserve", "148px");
    try {
      expect(bottomReservePx(), "живое значение сильнее запасного пути").toBe(148);
      expect(autoscrollSlackPx()).toBe(248);
    } finally {
      root.style.removeProperty("--dp-bottom-reserve");
    }
  });

  it("ключ убранного шарика назван и не читается никем, кроме снятия", () => {
    expect(LEGACY_BALL_KEY).toBe("dsh.phone.ball.pos");
  });

  it("предел текста, лимит очереди и ключ нового черновика — названы числом", () => {
    expect(TASK_TEXT_MAX).toBe(4000);
    expect(QUEUE_LIMIT).toBe(20);
    expect(DRAFT_NEW_KEY).toBe("dsh.phone.draft.new");
  });
});
