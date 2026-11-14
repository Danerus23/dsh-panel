// БЛОК «ОЧЕРЕДЬ» — ДОК НАД ЗАЦЕПОМ ВВОДА (D11 §3, DEF-149, слово владельца 12.11.2026:
// «если там механизм как и с вопросами и прячется наверху то это не правильно» + два скриншота DSH).
//
// Что изменилось против прежнего блока в потоке над лентой:
//   * это НЕ секция ленты, а карточка НИЖНЕГО СТЕКА (`.dp-bottom`): свёрнутая строка «В очереди: N»
//     высотой 44 стоит над зацепом шторки, под ней — ничего (D11 §1.1, §3.2);
//   * раскрытие — ВВЕРХ: список стоит в разметке ДО строки, поэтому панель растёт вверх и ПОДНИМАЕТ
//     всё, что выше (вопрос), а не накрывает его; своего z-index внутри стека нет ни у кого;
//   * N = 0 → блока НЕТ вовсе (ноль — пустая величина, `50` §1); `no-live` → блока нет; `unreadable` →
//     строка отказа + «Повторить» (факт не исчезает, `60` §4);
//   * прежнее двухшаговое «Убрать» сохранено целиком: вопрос раскрывается НА МЕСТЕ пункта, безопасное
//     «Оставить» первым, фокус на нём, возврат фокуса на крестик;
//   * список живёт в пределе `min(40dvh, 320px)` со своей прокруткой (`overscroll-behavior: contain`):
//     раскрытая очередь не съедает экран и не тянет за собой страницу;
//   * высота меняется МГНОВЕННО (анимация высоты погнала бы ленту), появление блока — только
//     прозрачность 150 мс (`90` №31). Запас снизу при этом честно растёт: его измеряет
//     `useBottomReserve` по фактической высоте стека (D11 §1.2).
//
// Слова — существующие: `PhoneWebInboxFormat` («В очереди: N») в строке, `PhoneWebQueueTitle`
// («Очередь») в `h2` раскрытой панели. Новых ключей W12 не заводит.

import { useEffect, useRef, type ReactNode } from "react";
import { IconChevronDown, IconChevronUp, IconClose } from "@douyinfe/semi-icons";
import { isAttachment, placementKey, type QueueView } from "../model/queue";
import type { RemoveApi } from "../hooks/useQueueRemove";
import type { QueueApi } from "../hooks/useQueue";
import type { T } from "../words";

export type QueueSectionProps = {
  t: T;
  queue: QueueApi;
  remove: RemoveApi;
  idPrefix: string;
  /** Раскрыта ли панель. Состоянием владеет экран; открывает и закрывает её только человек. */
  open: boolean;
  onOpenChange: (open: boolean) => void;
};

function QueueNote({ children }: { children: ReactNode }) {
  return <p className="dp-note">{children}</p>;
}

export function QueueSection({
  t,
  queue,
  remove,
  idPrefix,
  open,
  onOpenChange,
}: QueueSectionProps) {
  const view: QueueView = queue.view;
  const titleId = `${idPrefix}-queue-title`;
  const panelId = `${idPrefix}-queue-panel`;
  const keep = useRef<HTMLButtonElement | null>(null);
  const buttons = useRef(new Map<string, HTMLButtonElement>());
  const opened = remove.asking;

  // Раскрыли вопрос — фокус на безопасном ответе (кнопка, на которой стояли, исчезла).
  useEffect(() => {
    if (opened !== null) keep.current?.focus();
  }, [opened]);

  const keepAndReturn = () => {
    const id = remove.asking;
    remove.keep();
    if (id !== null) buttons.current.get(id)?.focus();
  };

  const count = view.status === "ok" ? view.total : null;
  const items = view.status === "ok" ? view.items : [];
  // Раскрытие — состояние экрана, НО отказ чтения раскрывать нечего: `unreadable` показывает факт и
  // «Повторить» сразу, иначе человек не увидел бы ни строки, ни двери (D11 §3.2).
  const expanded = view.status === "unreadable" ? true : open && count !== null;

  // Ноль — пустая величина: строку «В очереди: 0» не печатаем, блока нет вовсе (`50` §1).
  // Сессия не открыта — знать нечего, показывать нечего (ключ `PhoneQueueNoLive` остаётся в словарях).
  if (view.status === "no-live") return null;
  if (view.status === "loading") return null;
  if (view.status === "ok" && count === 0) return null;

  return (
    <section
      className="dp-queue"
      aria-labelledby={expanded ? titleId : undefined}
      data-queue-status={view.status}
      data-queue-open={expanded ? "true" : "false"}
    >
      {expanded ? (
        <div className="dp-queue-panel" id={panelId}>
          <h2 className="dp-queue-title" id={titleId} tabIndex={-1}>
            {t("PhoneWebQueueTitle")}
          </h2>

          {view.status === "unreadable" ? (
            <div className="dp-error-block" role="alert">
              <p>{view.message !== "" ? view.message : t("PhoneQueueUnreadable")}</p>
              <div className="dp-actions">
                <button type="button" className="dp-quiet" onClick={queue.refresh}>
                  {t("PhonePageRepeatButton")}
                </button>
              </div>
            </div>
          ) : null}

          {view.status === "ok" && items.length > 0 ? (
            <ul className="dp-queue-list">
              {items.map((item) => {
                const placeKey = placementKey(item.placement);
                const busy = remove.removing === item.id;
                const asking = remove.asking === item.id;
                return (
                  <li className="dp-queue-item" key={item.id} data-item={item.id}>
                    <p className="dp-queue-text">
                      {isAttachment(item) ? t("PhoneWebAttachmentNote") : item.text}
                    </p>
                    <div className="dp-queue-meta">
                      {placeKey !== null ? (
                        <span className="dp-queue-placement">{t(placeKey)}</span>
                      ) : null}
                      <button
                        type="button"
                        className="dp-quiet dp-icon-button dp-queue-remove"
                        ref={(node) => {
                          if (node === null) buttons.current.delete(item.id);
                          else buttons.current.set(item.id, node);
                        }}
                        aria-disabled={busy}
                        aria-expanded={asking}
                        // Иконка-крестик, а СЛОВО — имя для чтения с экрана (карточка 32a §4 п. 3).
                        aria-label={t("PhoneWebQueueRemove")}
                        onClick={busy ? undefined : () => remove.ask(item.id)}
                      >
                        <IconClose aria-hidden="true" />
                      </button>
                    </div>

                    {asking ? (
                      <div className="dp-queue-ask">
                        <p className="dp-queue-ask-title">{t("PhoneWebQueueRemoveAsk")}</p>
                        <QueueNote>{t("PhoneWebQueueRemoveCost")}</QueueNote>
                        <div className="dp-actions">
                          <button
                            type="button"
                            className="dp-quiet"
                            ref={keep}
                            onClick={busy ? undefined : keepAndReturn}
                          >
                            {t("PhoneWebQueueKeep")}
                          </button>
                          <button
                            type="button"
                            className="dp-danger-action"
                            aria-disabled={busy}
                            onClick={busy ? undefined : () => remove.confirm(item.id)}
                          >
                            {t("PhoneWebQueueRemoveYes")}
                          </button>
                        </div>
                      </div>
                    ) : null}
                  </li>
                );
              })}
            </ul>
          ) : null}

          {/* Строка «Показаны первые N из M» — ТОЛЬКО когда показано меньше целого (D2 F-06,
              `50` §1: один счёт — одна формулировка; при `shown == total` объявлять частичное нельзя). */}
          {view.status === "ok" && view.shown < view.total ? (
            <QueueNote>{t("PhoneWebQueueMoreFormat", view.shown, view.total)}</QueueNote>
          ) : null}

          {remove.outcome.kind === "removed" || remove.outcome.kind === "gone" ? (
            <p className="dp-status" role="status">
              {remove.outcome.words}
            </p>
          ) : null}
          {remove.outcome.kind === "failed" ? (
            <div className="dp-error-block" role="alert">
              <p>{remove.outcome.words}</p>
              <div className="dp-actions">
                <button type="button" className="dp-quiet" onClick={remove.retry}>
                  {t("PhonePageRepeatButton")}
                </button>
              </div>
            </div>
          ) : null}
        </div>
      ) : null}

      {/* СВЁРНУТАЯ СТРОКА — единственный видимый орган очереди: «В очереди: N» (существующий ключ)
          и шеврон. Высота 44, шеврон справа, счёт `tabular-nums` (цифры не пляшут при смене числа). */}
      {count !== null ? (
        <button
          type="button"
          className="dp-queue-row"
          aria-expanded={expanded}
          // D13-4 (R3): `aria-controls` — ТОЛЬКО когда панель есть в DOM. У свёрнутой строки он
          // висел на несуществующем id (ARIA 1.2: ссылка обязана указывать на существующий элемент).
          aria-controls={expanded ? panelId : undefined}
          onClick={() => onOpenChange(!open)}
        >
          <span className="dp-queue-count">{t("PhoneWebInboxFormat", count)}</span>
          {expanded ? (
            <IconChevronDown className="dp-queue-chevron" aria-hidden="true" />
          ) : (
            <IconChevronUp className="dp-queue-chevron" aria-hidden="true" />
          )}
        </button>
      ) : null}
    </section>
  );
}
