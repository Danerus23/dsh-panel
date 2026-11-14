// Вход по шести цифрам. Введённое НЕ стирается после отказа (замеренный дефект старой страницы),
// фокус после ошибки — на поле кода, кнопка гаснет на время отправки и не отправляет второй запрос.

import { useRef, useState } from "react";
import { ApiFailure, pair } from "../api";
import { joinReason, type T } from "../words";

export type LoginScreenProps = {
  t: T;
  onAuthed: () => void;
};

const CODE_LENGTH = 6;

export function LoginScreen({ t, onAuthed }: LoginScreenProps) {
  const [code, setCode] = useState("");
  const [sending, setSending] = useState(false);
  const [error, setError] = useState("");
  const field = useRef<HTMLInputElement>(null);
  const valid = new RegExp(`^\\d{${CODE_LENGTH}}$`).test(code);

  const submit = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!valid || sending) return;
    setSending(true);
    setError("");
    void pair(code)
      .then(() => onAuthed())
      .catch((failure: unknown) => {
        setError(failure instanceof ApiFailure ? failure.message : "");
        setSending(false);
        field.current?.focus();
      });
  };

  return (
    <main className="dp-page">
      <h1>{t("PhonePageTitle")}</h1>
      <p>{t("PhonePageIntro")}</p>
      <form onSubmit={submit} noValidate>
        <div className="dp-field">
          <label htmlFor="code">{t("PhonePageCodeLabel")}</label>
          <input
            ref={field}
            id="code"
            name="code"
            type="tel"
            inputMode="numeric"
            autoComplete="off"
            maxLength={CODE_LENGTH}
            value={code}
            aria-invalid={error !== ""}
            aria-describedby={error !== "" ? "code-error" : undefined}
            onChange={(event) =>
              setCode(event.target.value.replace(/\D/gu, "").slice(0, CODE_LENGTH))
            }
          />
        </div>
        {error !== "" ? (
          <p className="dp-alert" id="code-error" role="alert">
            {joinReason(t("PhonePageError"), error)}
          </p>
        ) : null}
        <div className="dp-actions">
          <button type="submit" className="dp-primary" disabled={!valid || sending}>
            {t("PhonePageSubmit")}
          </button>
        </div>
        <p className="dp-note">{t("PhoneWebBootFallback")}</p>
      </form>
    </main>
  );
}
