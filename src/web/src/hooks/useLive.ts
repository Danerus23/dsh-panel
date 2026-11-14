// Живой поток сессии: снимок → кадры → недописанный ответ; «раньше» — перечитыванием.
//
// Догон после сна: поток закрывается на скрытой странице (> 5 с) и открывается заново на возврате —
// это НОВЫЙ снимок (курсора продолжения у движка нет). Загруженные ранее страницы остаются, только
// если не пересекаются со снимком по `seq`; иначе окно показывается заново, без склеенной дыры.

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ApiFailure, loadPage } from "../api";
import {
  appendRecord,
  canStitch,
  draftAfterFrame,
  draftAfterRecord,
  normalizeAll,
  normalizeRecord,
  parseSnapshot,
  prependPage,
  type AssistantFrame,
  type Draft,
  type FeedRecord,
} from "../model/records";
import { LiveStream, type LiveFailure, type LiveStatus } from "../live/sse";

export type LiveState = {
  status: LiveStatus;
  failure: LiveFailure | null;
  title: string | null;
  records: FeedRecord[];
  draft: Draft | null;
  hasMore: boolean;
  earlierLoading: boolean;
  earlierError: string;
  /** S4a: последний кадр `queue` из этого же потока (очередь живёт без нажатия). */
  queueFrame: QueueFrame | null;
  showEarlier: () => void;
  retry: () => void;
};

/** Кадр очереди и время его прихода: новая ссылка на объект — новый кадр (даже если форма та же). */
export type QueueFrame = { payload: unknown; at: number };

function liveUrl(sessionId: string): string {
  return `/api/phone/live?session=${encodeURIComponent(sessionId)}`;
}

/** Фраза обрыва: код от калитки → наши слова (машинные коды человеку не показываются). */
export function liveFailureWords(
  code: string,
): "lost" | "too-many" | "subagent" | "missing" | "other" {
  switch (code) {
    case "too-many":
      return "too-many";
    case "subagent":
      return "subagent";
    case "no-session":
    case "no-engine":
      return "missing";
    case "lost":
    case "lifetime":
    case "engine":
      return "lost";
    default:
      return "other";
  }
}

export function useLive(
  sessionId: string | null,
  visible: boolean,
  hiddenLong: boolean,
): LiveState {
  const [windowRecords, setWindowRecords] = useState<FeedRecord[]>([]);
  const [older, setOlder] = useState<FeedRecord[]>([]);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [status, setStatus] = useState<LiveStatus>("connecting");
  const [failure, setFailure] = useState<LiveFailure | null>(null);
  const [title, setTitle] = useState<string | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [earlierLoading, setEarlierLoading] = useState(false);
  const [earlierError, setEarlierError] = useState("");
  const [queueFrame, setQueueFrame] = useState<QueueFrame | null>(null);
  const stream = useRef<LiveStream | null>(null);
  const cursor = useRef(0);

  useEffect(() => {
    if (sessionId === null) return;
    setWindowRecords([]);
    setOlder([]);
    setDraft(null);
    setTitle(null);
    setHasMore(false);
    setEarlierError("");
    setStatus("connecting");
    setFailure(null);
    setQueueFrame(null);
    cursor.current = 0;

    const instance = new LiveStream(liveUrl(sessionId), {
      onSnapshot: (payload) => {
        const snapshot = parseSnapshot(payload);
        if (snapshot === null) return;
        const fresh = normalizeAll(snapshot.records);
        setWindowRecords(fresh);
        setOlder((previous) => (canStitch(previous, fresh) ? previous : []));
        setDraft(snapshot.assistantDraft);
        setTitle(snapshot.header.title);
        setHasMore(snapshot.hasMore);
        cursor.current = snapshot.cursor;
      },
      onRecord: (payload) => {
        const raw =
          typeof payload === "object" && payload !== null
            ? (payload as Record<string, unknown>)["record"]
            : null;
        const record = normalizeRecord(raw);
        if (record === null) return;
        setWindowRecords((previous) => appendRecord(previous, record));
        setDraft((previous) => draftAfterRecord(previous, record));
      },
      onAssistant: (payload) => {
        const frame = payload as AssistantFrame | null;
        if (frame === null || typeof frame !== "object" || typeof frame.phase !== "string") return;
        setDraft((previous) => draftAfterFrame(previous, frame));
      },
      onQueue: (payload) => setQueueFrame({ payload, at: Date.now() }),
      onStatus: (next, detail) => {
        setStatus(next);
        if (detail !== undefined) setFailure(detail);
        if (next === "live") setFailure(null);
      },
    });
    stream.current = instance;
    return () => {
      instance.stop();
      stream.current = null;
    };
  }, [sessionId]);

  useEffect(() => {
    const instance = stream.current;
    if (instance === null) return;
    if (visible && !hiddenLong) instance.start();
    else instance.stop();
  }, [visible, hiddenLong, sessionId]);

  const showEarlier = useCallback(() => {
    const instance = stream.current;
    if (sessionId === null || instance === null) return;
    if (earlierLoading) return;
    const oldest = older[0]?.seq ?? windowRecords[0]?.seq ?? 0;
    setEarlierLoading(true);
    setEarlierError("");
    void loadPage(sessionId, cursor.current, oldest === 0 ? null : oldest)
      .then((page) => {
        const pageRecords = normalizeAll(page.records);
        setOlder((previous) => prependPage(previous, pageRecords));
        setHasMore(page.hasMore);
      })
      .catch((error: unknown) => {
        setEarlierError(error instanceof ApiFailure ? error.message : "");
      })
      .finally(() => setEarlierLoading(false));
  }, [earlierLoading, older, windowRecords, sessionId]);

  const retry = useCallback(() => {
    stream.current?.restart();
  }, []);

  const records = useMemo(() => prependPage(older, windowRecords), [older, windowRecords]);

  return {
    status,
    failure,
    title,
    records,
    draft,
    hasMore,
    earlierLoading,
    earlierError,
    queueFrame,
    showEarlier,
    retry,
  };
}
