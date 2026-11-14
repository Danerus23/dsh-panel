// Точка входа пульта. Порядок важен:
//   1) адаптер Semi под React 19 — ДО компонентов (иначе они ищут старый корень);
//   2) стили Semi, затем наши (наши `--dp-*` и правила экрана идут последними);
//   3) тема по системе, boot из атрибута `data-boot`, и только потом рендер.
// Никакого inline-скрипта: CSP страницы запрещает `unsafe-inline`.

import "@douyinfe/semi-ui/react19-adapter";
import "./styles/semi.css";
import "./styles/app.css";

import { createRoot } from "react-dom/client";
import { App } from "./App";
import { ErrorBoundary } from "./ErrorBoundary";
import { readBoot, type Boot } from "./boot";
import { applyTheme } from "./theme";
import { makeT } from "./words";
import { ErrorScreen } from "./screens/ErrorScreen";

const container = document.getElementById("root");
const root = container === null ? null : createRoot(container);

if (container === null || root === null) {
  // Оболочка без `#root` — это поломка калитки; сказать словами, а не молчать.
  document.body.textContent = "PhoneWebCrash";
} else {
  applyTheme();
  let boot: Boot | null = null;
  try {
    boot = readBoot(container);
  } catch {
    boot = null;
  }
  if (boot === null) {
    const t = makeT({});
    root.render(
      <ErrorScreen
        t={t}
        title="PhonePageProblemTitle"
        body="PhoneWebCrash"
        action="PhoneWebRefresh"
        onAction={() => window.location.reload()}
      />,
    );
  } else {
    const t = makeT(boot.words);
    const crash = () =>
      root.render(
        <ErrorScreen
          t={t}
          title={t("PhonePageProblemTitle")}
          body={t("PhoneWebCrash")}
          action={t("PhoneWebRefresh")}
          onAction={() => window.location.reload()}
        />,
      );
    // Сбой вне React (промис, обработчик) тоже показывает экран со словами, а не белый лист.
    window.addEventListener("error", crash);
    window.addEventListener("unhandledrejection", crash);
    root.render(
      <ErrorBoundary t={t}>
        <App boot={boot} />
      </ErrorBoundary>,
    );
  }
}
