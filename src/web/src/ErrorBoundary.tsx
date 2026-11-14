// Граница ошибок React: сломанный рендер показывает экран со словами и кнопкой «Обновить»,
// а не белый лист (правило роли: «при выбранном React экран ошибки обязателен»).

import { Component, type ErrorInfo, type ReactNode } from "react";
import { ErrorScreen } from "./screens/ErrorScreen";
import type { T } from "./words";

type Props = { t: T; children: ReactNode };
type State = { failed: boolean };

export class ErrorBoundary extends Component<Props, State> {
  state: State = { failed: false };

  static getDerivedStateFromError(): State {
    return { failed: true };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    // В журнал — только факт и имя компонента: без текста данных человека.
    console.error("panel web crash", error.name, info.componentStack?.split("\n")[1] ?? "");
  }

  render(): ReactNode {
    if (!this.state.failed) return this.props.children;
    return (
      <ErrorScreen
        t={this.props.t}
        title={this.props.t("PhonePageProblemTitle")}
        body={this.props.t("PhoneWebCrash")}
        action={this.props.t("PhoneWebRefresh")}
        onAction={() => window.location.reload()}
      />
    );
  }
}
