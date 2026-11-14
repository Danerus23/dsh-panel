// Тема: светлая/тёмная ПО СИСТЕМЕ (`prefers-color-scheme`), без своей кнопки.
//
// Цвета — наши смысловые переменные `--dp-*` (значения в `styles/tokens.css`), а Semi получает
// `theme-mode="dark"` на <body>, чтобы его компоненты взяли свои тёмные значения. Значения токенов
// в CSS — единственный источник: их же читает проверка контраста (`contrast.test.ts`), поэтому
// «поменяли цвет и забыли про контраст» падает прогоном.

export type Theme = "light" | "dark";

export function preferredTheme(): Theme {
  if (typeof window.matchMedia !== "function") return "light";
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

/** Ставит тему на <html> (наш токен) и <body> (Semi). Возвращает функцию снятия подписки. */
export function applyTheme(theme: Theme = preferredTheme()): () => void {
  const root = document.documentElement;
  const body = document.body;
  const set = (value: Theme) => {
    root.dataset["dpTheme"] = value;
    body.setAttribute("theme-mode", value);
  };
  set(theme);
  if (typeof window.matchMedia !== "function") return () => undefined;
  const query = window.matchMedia("(prefers-color-scheme: dark)");
  const onChange = (event: MediaQueryListEvent) => set(event.matches ? "dark" : "light");
  query.addEventListener("change", onChange);
  return () => query.removeEventListener("change", onChange);
}
