// Черновик задачи на телефоне (контракт §8): что человек набрал — не теряется при перезагрузке,
// сне экрана и возврате на вкладку.
//
// Правила, которые здесь исполняются:
//   * ключи `dsh.phone.draft.new` и `dsh.phone.draft.<sessionId>`; в значении только
//     `{v, text, requestId, savedAt}` — ни cookie, ни путей, ни чужих имён (`§8.3`, `model/draft.ts`);
//   * подставляем ТОЛЬКО в пустое поле: набранное никогда не затираем (`§8.5`, `40-forms.md` §7);
//   * автоотправки нет и намёка на неё нет;
//   * снимаем ТОЛЬКО по `200` с тем же `requestId` (или кнопкой «Убрать черновик»), авто-очистки
//     по времени нет (`§8.4`);
//   * запись — не чаще ~1 раза в секунду; уход со страницы дописывает последнее.

import { useCallback, useEffect, useRef, useState } from "react";
import { DRAFT_SAVE_MS } from "../config";
import {
  draftAvailable,
  readDraft,
  removeDraft,
  writeDraft,
  type DraftStore,
} from "../model/draft";
import { isRequestId, mintRequestId } from "../model/task";

export type DraftApi = {
  text: string;
  setText: (value: string) => void;
  /** `requestId` текущей попытки ("" — попытки ещё не было). */
  requestId: string;
  /** Черновик был подставлен при открытии — повод сказать «Черновик восстановлен». */
  restored: boolean;
  /** Хранилище недоступно: «черновик не сохранится» — один раз за визит. */
  unavailable: boolean;
  /** «Убрать черновик»: снимается сразу, без вопроса. */
  discard: () => void;
  /** Снимок попытки: текст и `requestId` (чеканится при первом обращении). */
  takeAttempt: () => { text: string; requestId: string };
  /** `200` с этим `requestId` — снять тот черновик, который отправляли. */
  clearOnAccepted: (acceptedRequestId: string) => void;
};

function storage(): DraftStore | null {
  try {
    return window.localStorage;
  } catch {
    return null;
  }
}

/**
 * `key` — ключ черновика: `dsh.phone.draft.new` или `dsh.phone.draft.<sessionId>` (контракт §8.2).
 */
export function useDraft(key: string): DraftApi {
  const [text, setText] = useState("");
  const [requestId, setRequestId] = useState("");
  const [restored, setRestored] = useState(false);
  const [unavailable, setUnavailable] = useState(false);

  const store = useRef<DraftStore | null>(null);
  const textRef = useRef("");
  const idRef = useRef("");
  /** Текст последней попытки: правка после отказа — это НОВАЯ задача, значит новый `requestId`. */
  const attemptText = useRef<string | null>(null);
  const timer = useRef<number | null>(null);

  const writable = useCallback((): DraftStore | null => {
    if (store.current === null) store.current = storage();
    return store.current;
  }, []);

  const write = useCallback(
    (value: string) => {
      const target = writable();
      if (!draftAvailable(target)) {
        setUnavailable(true);
        return;
      }
      if (!writeDraft(target, key, value, idRef.current, Date.now())) setUnavailable(true);
    },
    [key, writable],
  );

  const flush = useCallback(() => {
    if (timer.current !== null) {
      window.clearTimeout(timer.current);
      timer.current = null;
    }
    write(textRef.current);
  }, [write]);

  /** Подстановка: только в пустое поле — набранное не затираем. */
  const restore = useCallback(() => {
    if (textRef.current !== "") return;
    const target = writable();
    if (!draftAvailable(target)) return;
    const record = readDraft(target, key);
    if (record === null) return;
    textRef.current = record.text;
    idRef.current = record.requestId;
    setText(record.text);
    setRequestId(record.requestId);
    setRestored(true);
  }, [key, writable]);

  useEffect(() => {
    textRef.current = "";
    idRef.current = "";
    attemptText.current = null;
    setText("");
    setRequestId("");
    setRestored(false);
    restore();
  }, [restore]);

  // Возврат из сна/из кэша страницы: поле пустое — черновик подставляется, иначе не трогаем.
  useEffect(() => {
    const onShow = () => restore();
    const onHide = () => flush();
    window.addEventListener("pageshow", onShow);
    window.addEventListener("pagehide", onHide);
    document.addEventListener("visibilitychange", onHide);
    return () => {
      window.removeEventListener("pageshow", onShow);
      window.removeEventListener("pagehide", onHide);
      document.removeEventListener("visibilitychange", onHide);
      flush();
    };
  }, [restore, flush]);

  const setDraftText = useCallback(
    (value: string) => {
      textRef.current = value;
      setText(value);
      // Правка после неудачной попытки — другая задача: старый `requestId` к ней не относится.
      if (attemptText.current !== null && value.trim() !== attemptText.current) {
        attemptText.current = null;
        idRef.current = mintRequestId();
        setRequestId(idRef.current);
      }
      if (value.trim() === "") setRestored(false);
      if (timer.current === null) timer.current = window.setTimeout(flush, DRAFT_SAVE_MS);
    },
    [flush],
  );

  const takeAttempt = useCallback(() => {
    if (idRef.current === "" || !isRequestId(idRef.current)) {
      idRef.current = mintRequestId();
      setRequestId(idRef.current);
    }
    attemptText.current = textRef.current.trim();
    return { text: textRef.current, requestId: idRef.current };
  }, []);

  const discard = useCallback(() => {
    textRef.current = "";
    attemptText.current = null;
    idRef.current = "";
    setText("");
    setRequestId("");
    setRestored(false);
    if (!removeDraft(writable(), key)) setUnavailable(true);
  }, [key, writable]);

  const clearOnAccepted = useCallback(
    (acceptedRequestId: string) => {
      // Снимаем только свой черновик: если человек уже набрал новое (другой `requestId`), оно цело.
      if (acceptedRequestId !== "" && idRef.current !== acceptedRequestId) return;
      textRef.current = "";
      attemptText.current = null;
      idRef.current = "";
      setText("");
      setRequestId("");
      setRestored(false);
      if (!removeDraft(writable(), key)) setUnavailable(true);
    },
    [key, writable],
  );

  return {
    text,
    setText: setDraftText,
    requestId,
    restored,
    unavailable,
    discard,
    takeAttempt,
    clearOnAccepted,
  };
}
