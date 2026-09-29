; dsh-panel.iss — установщик панели DSH Panel 2.0.
;
; Собирается скриптом build-installer.ps1 (он же пишет appversion.iss с версией).
; Перенесён из панели 1.x (..\dsh-tray\installer\dsh-panel.iss), но НЕ скопирован: 2.0
; самодостаточна (.NET 10 внутри неё), поэтому из установщика ушло всё, что было нужно
; прежней панели ради среды и Node:
;   * нет вложенной среды .NET Desktop Runtime (её скачивание, проверка подписи Microsoft,
;     ожидание установки, задача `dotnet`) — вместе с этим уходит половина [Code];
;   * нет задач `nodejs` и `pnpm`: движок и Node панель 2.0 ставит сама из мастера;
;   * нет страницы «вернуть копию данных при установке»: копию возвращает сама панель
;     (решение владельца 28.09.2026).
;
; ЧТО ПЕРЕНЕСЕНО ДОСЛОВНО, потому что это уроки, а не украшения:
;   * тот же AppId и та же папка установки — 2.0 встаёт ПОВЕРХ 1.x (решение владельца
;     28.09.2026): один пакет winget, один листинг, одна панель в трее;
;   * `AppMutex` НЕ задан: Inno показал бы неподавляемое окно «панель уже работает» до [Code],
;     и `winget upgrade` встал бы на окне, которого никто не закроет. Панель закрывает наш
;     `taskkill` (см. [Code]) плюс родной `CloseApplications`;
;   * все сообщения — через `SayOrLog`: в тихой установке ни одного окна;
;   * уборка МЁРТВОЙ записи автозапуска прежней генерации (`DSHPanel` ведёт на удаляемый
;     `DshTray.exe`) и ярлыка `DeepSeek Harness.lnk`, который ведёт на `DshTray.exe`;
;   * `UsePreviousTasks=no` — прежний выбор задач не возвращается: иначе тихая установка
;     вернула бы автозапуск, который человек уже снял.
;
; ⚠️ ЖИВАЯ запись 2.0 (`DSHPanel2`) не трогается никогда: её пишет и читает сама панель,
; и «отобрать автозапуск у живой копии» — не дело установщика.
;
; ⚠️ ЧЕГО ЭТОТ ФАЙЛ НЕ ЗНАЕТ: прописана ли панель в реестре верно и поднимается ли она по
; входу в Windows — это видно только на живой машине (в виртуальной, по правилам проекта).

#define AppName "DSH Panel"
#define AppPublisher "Danerus23"
#define AppExeName "DshPanel.exe"

; Имя записи автозапуска 2.0 — то же, что пишет сама панель (AutostartDecisions.ValueName).
#define AutostartValue "DSHPanel2"

; Имя записи ПРЕЖНЕЙ генерации: её надо убрать. Проверка «а не ведёт ли она на живой файл»
; написана в [Code] отдельно, и это не украшение: слепое удаление чужой записи запрещено
; правилом проекта.
#define LegacyAutostartValue "DSHPanel"

; Откуда брать файлы панели. По умолчанию — папка публикации рядом с проектом;
; можно переопределить: ISCC /DAppSource=C:\путь\к\app dsh-panel.iss
#ifndef AppSource
  #define AppSource "..\dist"
#endif

; ⚠️ И ТО ЖЕ ДЛЯ OutputDir, и это не симметрия ради красоты: до 29.09.2026 здесь стоял жёсткий
; `OutputDir=..\dist`, поэтому установщик ложился в `dist` ВСЕГДА, а ключ `-OutDir` скрипта был
; обманом — сборка сообщала «успешно», а скрипт не находил своего файла там, куда его просили.
; Скрипт передаёт `/DOutputDir=<абсолютный путь>`; эта подстраховка нужна ручному запуску ISCC,
; иначе он споткнётся об неизвестную переменную.
; ⚠️ Путь обязан быть АБСОЛЮТНЫМ: Inno разрешает относительные пути `Source:` от каталога ЭТОГО
; файла (`installer\`), а не от корня репозитория, — на этом 29.09.2026 и сорвалась первая сборка
; («No files found matching …\installer\upd-build\rel-dist\*»).
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#include "appversion.iss"

[Setup]
AppId={{7F3C6A21-9D4E-4B8A-9C1F-2E5D8B7A4C11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\DSH Panel
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest

CloseApplications=yes
RestartApplications=no
UsePreviousTasks=no
; Приглашение «This will install…» не отключается ключами /SILENT и /VERYSILENT: только
; этой директивой и ключом /SP-.
DisableStartupPrompt=yes

OutputDir={#OutputDir}
OutputBaseFilename=dsh-panel-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
; Значок установщика — ТОТ ЖЕ, что у панели: он вынимается из её exe, потому что панель
; раздаётся одним файлом и отдельного .ico рядом с ней не лежит. Свой значок у установщика
; не украшение: без него в «Программах и компонентах» и в окне установки стоит значок Inno.
;
; ⚠️ Строка найдена заново 29.09.2026: она была РУЧНОЙ правкой в дереве выпуска и в git
; не попадала вовсе (`git log -S SetupIconFile` пуст). Из-за этого установщик, собранный из
; чистого дерева, показывал значок Inno — а .ico лежал только в старом дереве.
SetupIconFile=dsh-panel.ico
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Язык установщика выбирает человек; прежний выбор не запоминаем — на новой машине он не при чём.
[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
ru.AppRunning=Панель будет закрыта на время установки — она снова поднимется сама.
ru.LegacyEntryKept=В автозапуске осталась запись прежней панели (%1), она ведёт на живой файл — не трогаю.
ru.LegacyEntryRemoved=Убрал из автозапуска мёртвую запись прежней панели.
ru.ShortcutRemoved=Убрал ярлык прежней панели.
ru.Launch=Запустить DSH Panel
ru.Autostart=Запускать при входе в Windows
ru.DesktopIcon=Ярлык на рабочем столе
ru.GroupOptional=По желанию
ru.GroupRequired=Обязательное

en.AppRunning=The panel will be closed during installation and started again afterwards.
en.LegacyEntryKept=An entry of the previous panel is still in autostart (%1) and points to a live file — leaving it alone.
en.LegacyEntryRemoved=Removed a dead autostart entry of the previous panel.
en.ShortcutRemoved=Removed a shortcut of the previous panel.
en.Launch=Start DSH Panel
en.Autostart=Start with Windows
en.DesktopIcon=Desktop shortcut
en.GroupOptional=Optional
en.GroupRequired=Required

zh.AppRunning=安装期间面板将关闭，安装完成后会重新启动。
zh.LegacyEntryKept=旧版面板的启动项（%1）仍指向一个存在的文件，已保留。
zh.LegacyEntryRemoved=已移除旧版面板失效的启动项。
zh.ShortcutRemoved=已移除旧版面板的快捷方式。
zh.Launch=启动 DSH Panel
zh.Autostart=开机时启动
zh.DesktopIcon=桌面快捷方式
zh.GroupOptional=可选
zh.GroupRequired=必需

[Tasks]
; Автозапуск — по желанию и по умолчанию СНЯТ. Так надо: тихая установка не передаёт /TASKS=,
; и отмеченная галочка означала бы, что каждое обновление само включает автозапуск — действие,
; которого никто не просил (урок 1.x, найден аудитом 23.09.2026).
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:GroupOptional}"; Flags: unchecked
Name: "autostart"; Description: "{cm:Autostart}"; GroupDescription: "{cm:GroupOptional}"; Flags: unchecked

[Files]
; status.txt и logs\* — отчёты и журналы проверок (в них состояние сервера и пути),
; settings.json — настройки конкретной машины, *.pdb — отладочные символы.
Source: "{#AppSource}\*"; DestDir: "{app}"; \
    Excludes: "status.txt,logs\*,settings.json,*.log,*.pdb"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

; Установщик кладём и рядом с панелью: тогда панель всегда знает, чем её поставили
; ({srcexe} — сам этот файл; в Source: он доступен только с флагом external).
Source: "{srcexe}"; DestDir: "{app}"; DestName: "dsh-panel-setup.exe"; \
    Flags: external ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Запись автозапуска 2.0 — под своим именем (DSHPanel2). Две строки пишут одно и то же
; значение, и это не дубль по недосмотру: условия у них РАЗНЫЕ. «Человек отметил задачу»
; читает сам Inno родным параметром Tasks, а «запись уже была» — переменную AutostartWasSet,
; снятую в InitializeSetup, через Check-функцию. Одной записью это не выражается: параметр
; Check принимает ИМЯ ФУНКЦИИ, а не переменную (проверено пробной сборкой в 1.x).
; ⚠️ БЕЗ КЛЮЧА В КОМАНДНОЙ СТРОКЕ, и это ИСПРАВЛЕННЫЙ ДЕФЕКТ первой редакции: она писала
; здесь `--tray` («чтобы панель ушла в трей»), а у панели 2.0 такого ключа НЕТ. Неизвестный ключ
; панель отвергает ГРОМКО, кодом 2 (`StartModes.Refuse`), — значит автозапуск не сработал бы
; ни разу, и при входе в Windows панель молча не поднималась бы (в 1.x ключ был, в 2.0 обычный
; запуск и есть запуск в трее). Запись ведёт на «путь без аргументов».
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "{#AutostartValue}"; ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "{#AutostartValue}"; ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Check: AutostartWasSetCheck

; Уборка записи ПРЕЖНЕЙ генерации `DSHPanel`: она ведёт на DshTray.exe, которого после
; перехода на 2.0 на машине нет, то есть это мёртвая ссылка. Убираем НЕ слепо: Check-
; функция сначала смотрит, мёртвая ли она (см. [Code], LegacyAutostartIsDeadCheck).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; \
    ValueName: "{#LegacyAutostartValue}"; Flags: deletevalue uninsdeletevalue; \
    Check: LegacyAutostartIsDeadCheck

[Run]
; Панель поднимается с правами того, кто запустил установщик, то есть обычными: иначе она
; поднимала бы сервер с правами администратора. Ключа нет намеренно (см. выше про `--tray`):
; обычный запуск окна не открывает и уходит в трей — ровно так же, как её поднимает автозапуск.
Filename: "{app}\{#AppExeName}"; Description: "{cm:Launch}"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Перед удалением закрываем работающую панель: иначе Inno не сможет удалить занятый
; DshPanel.exe и предложит перезагрузку, а значок в трее останется жить из недобитой папки.
; Данные панели лежат в %LOCALAPPDATA%\DshPanel2 и удалением файлов не затрагиваются —
; это данные человека, а не файлы программы.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExeName} /F"; \
    Flags: runhidden; RunOnceId: "StopPanelBeforeUninstall"

[UninstallDelete]
; Свою рабочую папку целиком НЕ трогаем (там настройки, состояние, история цен и копии),
; но убираем то, что восстановится само: staging обновления и распакованную сборку внутри него.
Type: filesandordirs; Name: "{localappdata}\DshPanel2\state\update"

[Code]
var
  AutostartWasSet: Boolean;
  SkipPanelStop: Boolean;

{ Строка в одинарных кавычках для PowerShell: одиночную кавычку удваиваем, иначе путь
  с апострофом (C:\Users\O'Brien\…) сломал бы команду. }
function PsQuote(const value: String): String;
var
  safe: String;
begin
  safe := value;
  StringChangeEx(safe, '''', '''''', True);
  Result := '''' + safe + '''';
end;

function RunQuery(const command: String; var output: String): Integer;
var
  code: Integer;
  tempFile: String;
  lines: TArrayOfString;
  index: Integer;
begin
  output := '';
  code := -1;
  tempFile := ExpandConstant('{tmp}\dsh-panel-query.txt');
  DeleteFile(tempFile);

  { ⚠️ ЗДЕСЬ БЫЛ ДЕФЕКТ, и он стоил двух неверных выводов подряд. Путь перенаправления
    подставлялся в кавычках, ЗАКОННЫХ ДЛЯ POWERSHELL ('…'), но команду разбирает cmd.exe, и он
    таких кавычек не знает: временный каталог установки содержит пробел
    («…\Temp\is-XXXX.tmp»), cmd резал перенаправление по пробелу, команда не выполнялась вовсе,
    а код возврата приходил ненулевым — и сторож объявлял ЖИВУЮ запись автозапуска мёртвой.
    Замерено: тот же запуск с одинарными кавычками — код 1, с двойными — код 0, совсем без
    перенаправления — код 0. Правило: в строке, которую разбирает cmd.exe, кавычки только
    двойные; PsQuote годится лишь для значений ВНУТРИ команды PowerShell. }

  if not Exec('cmd.exe', '/C ' + command + ' > "' + tempFile + '" 2>&1',
       '', SW_HIDE, ewWaitUntilTerminated, code) then
  begin
    Result := -1;
    Exit;
  end;

  if LoadStringsFromFile(tempFile, lines) then
    for index := 0 to GetArrayLength(lines) - 1 do
      output := output + Trim(lines[index]);

  DeleteFile(tempFile);
  Result := code;
end;

{ Живой ли файл, на который указывает запись автозапуска.
  Возврат: 0 — файл есть (запись живого соседа, НЕ трогать), 1 — файла нет (мёртвая ссылка),
  2 — проверить не удалось или проверять нечего.

  ⚠️ ПОЧЕМУ ЗНАЧЕНИЕ ИДЁТ ЧЕРЕЗ ОКРУЖЕНИЕ, А НЕ ТЕКСТОМ КОМАНДЫ.
  Первая редакция передавала значение записи прямо в строку `powershell -Command "…"`, и это
  был ДЕФЕКТ, найденный мутацией (запись прежней генерации, ведущая на СУЩЕСТВУЮЩИЙ файл,
  была удалена как мёртвая). Причина: значение содержит кавычки, а cmd.exe, через который идёт
  запуск, снимает внешние кавычки ДО PowerShell — в путь попадала пустая строка, а
  `Test-Path -LiteralPath ''` отвечает ошибкой привязки параметра и кодом 1. То есть сторож
  говорил «мёртвая», НЕ СУМЕВ ничего проверить, — ровно то, что запрещено правилом «путать
  „не смог проверить“ с „мёртвая“ нельзя». Проверено прогоном через cmd.exe вручную:
  «ParameterArgumentValidationErrorEmptyStringNotAllowed».

  Теперь значение уходит в переменную окружения, а в командной строке остаётся только имя этой
  переменной: значение с кавычками, `&`, `%` и пробелами вообще не участвует в разборе
  аргументов. Приём не выдуман здесь — он тот же, что в сценарии замены файлов (`UpdateScript`,
  урок про путь с `&` и `%`). }
function AutostartTargetState(const valueName: String): Integer;
var
  code: Integer;
  output, probe, probeScript, valueFile, rawValue: String;
begin
  Log('ПРОБА: tmp = «' + ExpandConstant('{tmp}') + '»');
  { Значение читаем средствами самого Inno: ноль поводов гонять его через cmd. }
  if not RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
       valueName, rawValue) or (Trim(rawValue) = '') then
  begin
    Log('Автозапуск ' + valueName + ': записи нет или она пуста — проверять нечего.');
    Result := 2;
    Exit;
  end;

  { Проверяющий сценарий и файл со значением — оба во временном каталоге установки.
    В командной строке стоят только два пути и НИ ОДНОЙ кавычки: значение записи в разборе
    аргументов не участвует вовсе, поэтому кавычки, «&», «%» и пробелы в нём безвредны. }
  probeScript := ExpandConstant('{tmp}\dsh-panel-autostart.ps1');
  valueFile := ExpandConstant('{tmp}\dsh-panel-autostart.txt');

  if (Pos(' ', probeScript) > 0) or (Pos(' ', valueFile) > 0) then
  begin
    Log('Автозапуск ' + valueName + ': во временном пути есть пробел — проверить не удалось.');
    Result := 2;
    Exit;
  end;

  probe := 'param([string]$ValueFile)' + #13#10 +
    '$v = [string](Get-Content -LiteralPath $ValueFile -Raw -ErrorAction SilentlyContinue)' + #13#10 +
    'if ([string]::IsNullOrWhiteSpace($v)) { exit 2 }' + #13#10 +
    '$p = $v.Trim()' + #13#10 +
    'if ($p.StartsWith(''"'')) { $p = $p.Substring(1) }' + #13#10 +
    '$q = $p.IndexOf(''"'')' + #13#10 +
    'if ($q -ge 0) { $p = $p.Substring(0, $q) }' + #13#10 +
    'if ([string]::IsNullOrWhiteSpace($p)) { exit 2 }' + #13#10 +
    'if (Test-Path -LiteralPath $p) { exit 0 } else { exit 1 }' + #13#10;


  if not SaveStringToFile(probeScript, probe, False) then
  begin
    Log('Автозапуск ' + valueName + ': не удалось записать ' + probeScript);
    Result := 2;
    Exit;
  end;

  if not SaveStringToFile(valueFile, rawValue, False) then
  begin
    Log('Автозапуск ' + valueName + ': не удалось записать ' + valueFile);
    Result := 2;
    Exit;
  end;

  code := RunQuery('powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    probeScript + ' ' + valueFile, output);
  Log('ПРОБА: сценарий «' + probeScript + '», значение в файле «' + valueFile + '»');

  if (code < 0) or (code > 2) then
  begin
    Log('Автозапуск ' + valueName + ': проверить не удалось (код ' + IntToStr(code) +
      '), вывод «' + output + '»');
    Result := 2;
    Exit;
  end;

  Log('Автозапуск ' + valueName + ': код проверки ' + IntToStr(code) + ', вывод «' + output + '»');
  Result := code;
end;

function AutostartValueExists(const valueName: String): Boolean;
var
  rawValue: String;
begin
  Result := RegQueryStringValue(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Run',
    valueName, rawValue) and (Trim(rawValue) <> '');
end;

{ Была ли запись автозапуска 2.0 на машине ДО установки. Обёртка нужна потому, что параметр
  Check в [Registry] принимает ИМЯ ФУНКЦИИ, а не переменную: с `Check: AutostartWasSet`
  компилятор отвечает «Required function or procedure 'AutostartWasSet' not found». }
function AutostartWasSetCheck: Boolean;
begin
  Result := AutostartWasSet;
end;

{ Убирать ли запись прежней генерации. Только МЁРТВУЮ: запись живого соседа — не наше дело,
  и правило проекта прямо запрещает отбирать автозапуск у живой копии. }
function LegacyAutostartIsDeadCheck: Boolean;
var
  state: Integer;
begin
  state := AutostartTargetState('{#LegacyAutostartValue}');

  if state = 1 then
  begin
    Result := True;
    Log('Автозапуск прежней генерации: запись ведёт на отсутствующий файл — убираю.');
  end
  else
  begin
    Result := False;
    if state = 0 then
      Log('Автозапуск прежней генерации: файл на месте — НЕ трогаю (живой сосед).');
  end;
end;

{ Ярлык прежней генерации «DeepSeek Harness.lnk» убираем ТОЛЬКО если его цель — DshTray.exe.
  Разобрать .lnk средствами Inno нечем, цель читает PowerShell через WScript.Shell.
  Рабочий стол и меню «Пуск» лежат в профиле этого же пользователя, как и ветка HKCU,
  которую правит [Registry] при PrivilegesRequired=lowest. Отказ или ненулевой код уборки
  установку НЕ срывают (чужое мы и не должны удалять), но и молчать о них нельзя — журнал. }
procedure RemoveLegacyShortcuts;
var
  paths: TArrayOfString;
  shortcutName, script, powershellPath: String;
  code: Integer;
begin
  shortcutName := 'DeepSeek Harness.lnk';

  SetArrayLength(paths, 3);
  paths[0] := ExpandConstant('{userdesktop}\') + shortcutName;
  paths[1] := ExpandConstant('{userprograms}\') + shortcutName;
  paths[2] := ExpandConstant('{group}\') + shortcutName;

  script := '$paths = @(';
  script := script + PsQuote(paths[0]) + ',' + PsQuote(paths[1]) + ',' + PsQuote(paths[2]) + '); ' +
    'foreach ($p in $paths) { if (Test-Path -LiteralPath $p) { $ok = $false; ' +
    'try { $s = (New-Object -ComObject WScript.Shell).CreateShortcut($p); ' +
    '$ok = ($s.TargetPath -like ' + PsQuote('*DshTray.exe') + ') } catch { $ok = $false }; ' +
    'if ($ok) { Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue; ' +
    'Write-Output $p } } }';

  powershellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if not FileExists(powershellPath) then
  begin
    Log('Уборка ярлыка прежней генерации: нет файла ' + powershellPath + ', беру powershell.exe из PATH');
    powershellPath := 'powershell.exe';
  end;

  if not Exec(powershellPath,
       '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + script + '"',
       '', SW_HIDE, ewWaitUntilTerminated, code) then
    Log('Уборка ярлыка прежней генерации: не удалось запустить ' + powershellPath)
  else if code <> 0 then
    Log('Уборка ярлыка прежней генерации: ' + powershellPath + ' вернул код ' + IntToStr(code));
end;

{ Ни одного окна в тихом режиме. `MsgBox` из [Code] не глушится ключом /SUPPRESSMSGBOXES
  (это сказано в справке Inno), поэтому все сообщения идут через эту процедуру. }
procedure SayOrLog(const text: String; const kind: TMsgBoxType);
begin
  if WizardSilent then
    Log('Сообщение установщика (окно подавлено, тихий режим): ' + text)
  else
    MsgBox(text, kind, MB_OK);
end;

function InitializeSetup: Boolean;
var
  index: Integer;
begin
  { Единственное место, где видно состояние реестра ДО правок установщика. Смотрим ИМЯ 2.0:
    запись прежней генерации проверяется отдельно и отдельным решением (Check-функцией). }
  AutostartWasSet := AutostartValueExists('{#AutostartValue}');

  { ⚠️ РЫЧАГ ДЛЯ ПРОГОНА: /SKIPPANELSTOP=1 отключает закрытие работающей панели.
    Он появился из случая, а не из аккуратности. Прогон установщика в песочницу (`/DIR=<своя>`)
    ВСЁ РАВНО закрывает панель человека: `taskkill /IM DshPanel.exe /F` бьёт по ИМЕНИ процесса,
    а имя у установленной панели и у песочницы одно. 29.09.2026 так и вышло — панель владельца
    была закрыта прогоном проверки, и он увидел это как «панель пропала». Установщик обязан
    закрывать панель ПЕРЕД СОБОЙ (иначе после переустановки останется жить прежняя копия),
    но прогон не имеет права гасить ЧУЖУЮ панель — это красная линия «владельцу не мешать». }
  SkipPanelStop := False;

  for index := 1 to ParamCount do
    if CompareText(Copy(ParamStr(index), 1, 15), '/SKIPPANELSTOP=') = 0 then
      SkipPanelStop := CompareText(Copy(ParamStr(index), 16, Length(ParamStr(index))), '1') = 0;

  if SkipPanelStop then
    Log('Рычаг прогона: работающая панель закрываться НЕ будет (/SKIPPANELSTOP=1)');

  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  { Страховка на случай, если панель не закрылась сама: без этого установка поверх работающей
    панели оставляет старый процесс живым, и он продолжает обслуживать трей старым кодом.
    Родной механизм Inno (CloseApplications) делает то же самое, но только когда видит
    работающий процесс через Restart Manager; свой taskkill не зависит от этого.

    ⚠️ Но у taskkill по ИМЕНИ есть обратная сторона, и она стоила закрытой панели человека
    29.09.2026: прогон установщика в песочницу закрывает не только свою копию, а ЛЮБУЮ
    работающую панель с этим именем. Поэтому прогон обязан идти с /SKIPPANELSTOP=1 — тогда
    панель человека не гасится, а установщик говорит об этом строкой в журнале. }
  if CurStep = ssInstall then
  begin
    if SkipPanelStop then
      Log('Работающая панель не закрывается: сказано /SKIPPANELSTOP=1 (так идут прогоны)')
    else
    begin
      Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExeName} /F',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(1500);
    end;
  end;

  if CurStep <> ssPostInstall then Exit;

  { Ярлык прежней генерации убираем сразу после копирования файлов: свои ярлыки установщик
    уже разложил, имена разные, чужое не задеваем. }
  RemoveLegacyShortcuts;
end;
