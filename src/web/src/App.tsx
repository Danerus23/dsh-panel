// Пульт целиком: вход → список → сессия, и всё это на ОДНОМ адресе (`/`, `?session=<id>`, `?new=1`).
//
// Маршрутов на сервере нет (контракт §2.8): глубокие ссылки, «назад» и F5 работают запросом,
// а переходы — `history.pushState`/`popstate`. Поэтому неизвестных путей здесь не бывает:
// калитка отдаёт 404 словами сама.
//
// ВОПРОС АГЕНТА ЖИВЁТ ЗДЕСЬ, а не в экране: его опрос (2 с) нужен и списку (метка строки,
// карточка без сессии), и сессии (карточка над лентой). Один опрос на пульт — один ответ
// на вопрос «что висит сейчас».
//
// S3: `?new=1` — экран «Новая задача»; после `200` D2 адрес ЗАМЕНЯЕТСЯ на `/?session=<id>`
// (`replaceState`), чтобы «назад» не вернуло к отправленной форме (вердикт t10 §2 п. 4).

import { useEffect, useMemo, useRef, useState } from "react";
import { navigate } from "./view/motion";
import type { Boot } from "./boot";
import { makeT } from "./words";
import { LoginScreen } from "./screens/LoginScreen";
import { ListScreen } from "./screens/ListScreen";
import { SessionScreen } from "./screens/SessionScreen";
import { NewTaskScreen } from "./screens/NewTaskScreen";
import { useQuestion } from "./hooks/useQuestion";
import { useVisible } from "./hooks/visibility";

type Route = { kind: "list" } | { kind: "session"; session: string } | { kind: "new" };

function readRoute(): Route {
  const params = new URLSearchParams(window.location.search);
  if (params.get("new") === "1") return { kind: "new" };
  const id = params.get("session");
  return id !== null && id !== "" ? { kind: "session", session: id } : { kind: "list" };
}

/** Тот же маршрут? Нужно `popstate`: «назад» из листа ввода не меняет экран. */
function sameRoute(left: Route, right: Route): boolean {
  if (left.kind !== right.kind) return false;
  if (left.kind === "session" && right.kind === "session") return left.session === right.session;
  return true;
}

export function App({ boot }: { boot: Boot }) {
  const t = useMemo(() => makeT(boot.words), [boot.words]);
  const [authed, setAuthed] = useState(boot.authed);
  const [route, setRoute] = useState<Route>(readRoute);
  const [focusSession, setFocusSession] = useState<string | null>(null);
  const visible = useVisible();
  const question = useQuestion(visible, authed);
  /** Маршрут на момент обработчика — чтобы `popstate` не анимировал ту же самую страницу. */
  const routeNow = useRef(route);
  routeNow.current = route;
  useEffect(() => {
    const onPop = () => {
      const next = readRoute();
      const prev = routeNow.current;

      // «Назад» из листа ввода приходит тем же маршрутом — переход не запускаем.
      if (sameRoute(prev, next)) return;

      // Переход — ТОЛЬКО вид: записи истории уже сделал браузер, мы их не трогаем.
      navigate(() => setRoute(next), next.kind === "list" ? "back" : "forward");
    };

    window.addEventListener("popstate", onPop);
    return () => window.removeEventListener("popstate", onPop);
  }, []);

  if (!authed) {
    return <LoginScreen t={t} onAuthed={() => setAuthed(true)} />;
  }

  const open = (sessionId: string) => {
    navigate(() => {
      setFocusSession(null);
      window.history.pushState({}, "", `/?session=${encodeURIComponent(sessionId)}`);
      setRoute({ kind: "session", session: sessionId });
    }, "forward");
  };

  const back = () => {
    const from = route.kind === "session" ? route.session : null;

    navigate(() => {
      window.history.pushState({}, "", "/");
      setRoute({ kind: "list" });
      setFocusSession(from);
    }, "back");
  };

  const openNew = () => {
    navigate(() => {
      window.history.pushState({}, "", "/?new=1");
      setRoute({ kind: "new" });
    }, "forward");
  };

  /** `200` D2: адрес заменяется, история не растёт — форма израсходована. */
  const created = (sessionId: string) => {
    navigate(() => {
      window.history.replaceState({}, "", `/?session=${encodeURIComponent(sessionId)}`);
      setRoute({ kind: "session", session: sessionId });
    }, "forward");
  };

  if (route.kind === "session") {
    return (
      <SessionScreen
        t={t}
        sessionId={route.session}
        onBack={back}
        question={question}
        onOpenSession={open}
      />
    );
  }

  if (route.kind === "new") {
    return <NewTaskScreen t={t} onBack={back} onCreated={created} />;
  }

  return (
    <ListScreen
      t={t}
      lang={boot.lang}
      onOpen={open}
      onNew={openNew}
      focusSessionId={focusSession}
      question={question}
    />
  );
}
