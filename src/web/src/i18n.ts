// Список слов, которые зовёт веб. Это НЕ словарь: тексты живут в словарях панели
// (`src\DshPanel\Localization\{ru,en,zh}.json`) и приезжают в `boot.words`.
//
// Здесь только ключи — чтобы опечатка в имени ключа падала на типах (`tsc`), а паритет трёх
// словарей проверялся прогоном (`i18n.test.ts`): ключ, которого нет хотя бы в одном языке, —
// красная проверка, а не пустая строка на экране.

/** Новые ключи S1+S2 (контракт §3.7) — их добавляют в словари панели задачи калитки. */
export const NEW_WORD_KEYS = [
  "PhoneWebListTitle",
  "PhoneWebListCountFormat",
  "PhoneWebListShownFormat",
  "PhoneWebListEmptyTitle",
  "PhoneWebListEmpty",
  "PhoneWebStateWaiting",
  "PhoneWebStateRunning",
  "PhoneWebStateDone",
  "PhoneWebStateBlank",
  "PhoneWebUntitled",
  "PhoneWebInboxFormat",
  "PhoneWebStatsFormat",
  "PhoneWebBackToList",
  "PhoneWebRefresh",
  "PhoneWebSessionMissingTitle",
  "PhoneWebSessionMissing",
  "PhoneWebSessionEmptyTitle",
  "PhoneWebSessionEmpty",
  "PhoneWebLiveConnecting",
  "PhoneWebLiveLost",
  "PhoneWebShowEarlier",
  "PhoneWebOlderFailed",
  "PhoneWebTurnFormat",
  "PhoneWebTurnError",
  "PhoneWebToolRunningFormat",
  "PhoneWebToolDoneFormat",
  "PhoneWebToolFailedFormat",
  "PhoneWebAttachmentNote",
  "PhoneWebQuestionElsewhere",
  "PhoneWebOpen",
  "PhoneWebFromSubagent",
  "PhoneWebAnswerSending",
  "PhoneWebNoScriptTitle",
  "PhoneWebNoScript",
  "PhoneWebBootFallback",
  "PhoneWebLoading",
  "PhoneWebCrash",
  "PhoneWebListUnreadable",
  "PhoneWebTooManyScreens",
  "PhoneWebSubagentRefused",
  "PhonePageCancelYes",
  "PhonePageCancelNo",
  // S3 «Запускать и останавливать» — 22 экранных ключа паспорта `s3-screens.md` §6 (слова — §4.1
  // `s3-words.md`). Порядок — паспортный; имена не переименовываются: они уже в словарях панели.
  "PhoneWebTaskTitle",
  "PhoneWebTaskLabel",
  "PhoneWebTaskHint",
  "PhoneWebTaskCounterFormat",
  "PhoneWebTaskSend",
  "PhoneWebTaskNew",
  "PhoneWebTaskNewNote",
  "PhoneWebDraftRestored",
  "PhoneWebDraftDiscard",
  "PhoneWebDraftUnavailable",
  "PhoneWebQueueTitle",
  "PhoneWebQueueEmpty",
  "PhoneWebQueueMoreFormat",
  "PhoneWebQueueRemove",
  "PhoneWebQueueRemoveAsk",
  "PhoneWebQueueRemoveCost",
  "PhoneWebQueueKeep",
  "PhoneWebQueueRemoveYes",
  "PhoneWebPlacementQueued",
  "PhoneWebPlacementSteering",
  "PhoneWebStop",
  "PhoneWebStopWarning",
  // S4a (D1, `_team\s4a\d1-slova-vid.md` §2): слово владельца «Переподключиться», панель
  // «Ждут ответа», её пустое состояние и доступное имя счёта. Одним изменением с C# и словарями.
  "PhoneWebReconnect",
  "PhoneWebWaitingTitle",
  "PhoneWebWaitingEmpty",
  "PhoneWebWaitingCountFormat",
  // Хвост S4a (P2 `_team\s4a-tail\p2-slova.md` §2): орган «к последнему сообщению» — имя при счёте 0
  // и имя со счётом. Два, а не один: «0» не печатается, а формат с {0} при нуле сказал бы «новых: 0».
  "PhoneWebToLast",
  "PhoneWebToLastCountFormat",
  // 11.11.2026 (D1 §1, §9): доступное имя ОДНОГО нового органа пары «вверх/вниз» — верхней
  // кнопки «к началу разговора». Видимого слова нет, новых слов больше не заводится.
  "PhoneWebToTop",
  // 12.11.2026 (D11 §5.2/§7.1, DEF-146): ОДНО новое слово многошагового вопроса — подпись главной
  // кнопки, пока есть неотвеченные карточки: «Далее» / «Next» / «下一步». Когда отвечено всё,
  // кнопка снова называет отправку (`PhonePageAnswerButton` — существующий ключ).
  "PhonePageNextButton",
] as const;

/** Существующие ключи панели, которые пульт переиспользует БЕЗ правки текстов (контракт §3.7). */
export const REUSED_WORD_KEYS = [
  "PhonePageTitle",
  "PhonePageIntro",
  "PhonePageCodeLabel",
  "PhonePageSubmit",
  "PhonePageError",
  "PhonePageDeviceFormat",
  "PhonePageDeviceFallback",
  "PhonePageQuestionTitle",
  "PhonePageProblemTitle",
  "PhonePageCheckButton",
  "PhonePageRepeatButton",
  "PhonePageCancelButton",
  "PhonePageCancelWarning",
  "PhonePageAnswerButton",
  "PhonePageChooseAll",
  // «Вопрос {0} из {1}» — ключ уже есть в трёх словарях панели; раньше его печатала только
  // серверная страница (`PhonePage.cs`), теперь его показывает и пейджер карточки (S3e).
  "PhonePageQuestionCountFormat",
  "PhonePageOwnLabel",
  "PhonePageOwnPlaceholder",
  "PhonePageAnswered",
  "PhonePageTaken",
  "PhonePageCancelled",
  "PhonePageUnreadable",
  "PhonePageWaitingTitle",
  "PhonePageAnsweredNext",
  "PhonePageTakenNext",
  "PhonePageCancelledNext",
  "PhonePageAnswerFailedFormat",
  "PhonePageCancelFailedFormat",
  "PhoneGateQuestionNoAnswerFormat",
  "PhoneGateNoQuestion",
  "PhoneGateStaleQuestion",
  "PhoneGateAnswerNotTaken",
  "PhoneGateNoChannel",
  // S3: ЭТИ фразы печатает калитка (ключи контракта §10 — их имена не меняются). Экран
  // повторяет их только тогда, когда ответ пришёл без `message` (обрыв, таймаут), — чтобы
  // «принято/остановлено/уже нет» не зависело от того, дошла ли строка. Новыми они не считаются.
  "PhoneTaskAccepted",
  "PhoneTaskNewAccepted",
  "PhoneTaskBusy",
  "PhoneTaskEmpty",
  "PhoneTaskNotTakenFormat",
  "PhoneTaskReasonNoConnection",
  "PhoneStopDone",
  "PhoneStopIdle",
  "PhoneStopFailedFormat",
  "PhoneQueueRemoved",
  "PhoneQueueAlreadyGone",
  "PhoneQueueRemoveFailedFormat",
  "PhoneQueueNoLive",
  "PhoneQueueBusy",
  "PhoneQueueUnreadable",
  "PhoneTaskTooLongFormat",
  "PhoneTaskNewFailedFormat",
] as const;

/**
 * Ключ, добавленный ПОСЛЕ контракта — словом капитана (25.10.2026 → 30.10.2026): метка
 * обязательного «своего ответа», когда вариантов у вопроса нет. В контрактные 42 не входит:
 * это наша добавка, и она нарочно отделена, чтобы счёт 42 оставался проверяемым.
 */
export const EXTRA_WORD_KEYS = ["PhonePageOwnLabelRequired"] as const;

export const WORD_KEYS = [...NEW_WORD_KEYS, ...EXTRA_WORD_KEYS, ...REUSED_WORD_KEYS] as const;
export type WordKey = (typeof WORD_KEYS)[number];

/** Ключи, которые обязан показать S1. S2 добавит свои — они уже в списке выше. */
export const S1_WORD_KEYS = [
  "PhonePageTitle",
  "PhonePageIntro",
  "PhonePageCodeLabel",
  "PhonePageSubmit",
  "PhonePageError",
  "PhonePageDeviceFormat",
  "PhonePageDeviceFallback",
  "PhonePageProblemTitle",
  "PhonePageQuestionTitle",
  "PhoneWebListTitle",
  "PhoneWebListCountFormat",
  "PhoneWebListShownFormat",
  "PhoneWebListEmptyTitle",
  "PhoneWebListEmpty",
  "PhoneWebStateWaiting",
  "PhoneWebStateRunning",
  "PhoneWebStateDone",
  "PhoneWebStateBlank",
  "PhoneWebUntitled",
  "PhoneWebInboxFormat",
  "PhoneWebStatsFormat",
  "PhoneWebBackToList",
  "PhoneWebRefresh",
  "PhoneWebSessionMissingTitle",
  "PhoneWebSessionMissing",
  "PhoneWebSessionEmptyTitle",
  "PhoneWebSessionEmpty",
  "PhoneWebLiveConnecting",
  "PhoneWebLiveLost",
  "PhoneWebShowEarlier",
  "PhoneWebOlderFailed",
  "PhoneWebTurnFormat",
  "PhoneWebTurnError",
  "PhoneWebToolRunningFormat",
  "PhoneWebToolDoneFormat",
  "PhoneWebToolFailedFormat",
  "PhoneWebAttachmentNote",
  "PhoneWebOpen",
  "PhoneWebNoScriptTitle",
  "PhoneWebNoScript",
  "PhoneWebBootFallback",
  "PhoneWebLoading",
  "PhoneWebCrash",
  "PhoneWebListUnreadable",
  "PhoneWebTooManyScreens",
  "PhoneWebSubagentRefused",
] as const satisfies readonly WordKey[];
