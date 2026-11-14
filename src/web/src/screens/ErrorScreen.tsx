// Экран беды: одна колонка, `h1` — что случилось, одна кнопка повтора. Никакого автоцикла
// перезагрузки: повтор — ручной (иначе «мигающий» экран вместо объяснения).

import type { T } from "../words";

export type ErrorScreenProps = {
  t: T;
  title: string;
  body: string;
  action?: string;
  onAction?: () => void;
  detail?: string;
};

export function ErrorScreen({ t, title, body, action, onAction, detail }: ErrorScreenProps) {
  return (
    <main className="dp-page">
      <h1>{title}</h1>
      <p>{body}</p>
      {detail !== undefined && detail !== "" ? <p className="dp-meta">{detail}</p> : null}
      {action !== undefined && onAction !== undefined ? (
        <div className="dp-actions">
          <button type="button" className="dp-primary" onClick={onAction} autoFocus>
            {action}
          </button>
        </div>
      ) : null}
      <p className="dp-device">{t("PhoneWebBootFallback")}</p>
    </main>
  );
}
