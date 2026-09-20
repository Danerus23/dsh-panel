; dsh-panel.iss — установщик панели DSH Panel.
;
; Отличия от прежнего установщика (dsh-deploy\installer.iss):
;   * ставится в %LOCALAPPDATA%\Programs, права администратора не нужны;
;   * не несёт ни restore.ps1, ни архива-копии, ни чужой раскладки — только саму панель;
;   * после установки предлагает запустить мастер первой настройки (--onboard);
;   * про отсутствие .NET Desktop Runtime 8 сообщает как о факте (без выбора «устанавливать ли»):
;     установку это не останавливает, а среда доустанавливается в любой момент.
;
; Собирается скриптом build-installer.ps1 (он же генерирует appversion.iss с версией).

#define AppName "DSH Panel"
#define AppPublisher "Danerus23"
#define AppExeName "DshTray.exe"

; Среда .NET Desktop Runtime вкладывается ВНУТРЬ установщика: build-installer.ps1 скачивает
; файл (~55 МБ) в installer\redist, проверяет подпись Microsoft и передаёт сюда пути ключами
; /DRuntimeFile и /DRuntimeName. Так установка идёт без интернета и без загрузки в момент
; установки: раньше среда качалась до появления окна мастера, на экране не оставалось ничего,
; и человек решал, что установка сломалась.
; Без этих ключей установщик соберётся, но среды внутри не будет — тогда о ней только сообщаем.
#ifdef RuntimeFile
  #define RuntimeBundled
#endif

; Откуда брать файлы панели. По умолчанию — свежая публикация рядом с проектом;
; можно переопределить: ISCC /DAppSource=C:\путь\к\app dsh-panel.iss
#ifndef AppSource
  #define AppSource "..\app"
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

; Панель — постоянно живущий процесс в трее. Если она работает, установщик обязан её закрыть:
; иначе после переустановки остаётся жить прежняя копия (со старым кодом), и все нажатия
; «Запустить» уходят в неё. AppMutex называет мьютекс, который создаёт сама панель
; (Program.cs: InstanceName() + ".SingleInstance"), CloseApplications закрывает её перед
; установкой, RestartApplications=no — чтобы панель не поднялась дважды: её запускает
; последняя страница установщика.
AppMutex=Local\DshTray.SingleInstance
CloseApplications=yes
RestartApplications=no
OutputDir=..\dist
OutputBaseFilename=dsh-panel-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\dsh-tray.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Язык установщика выбирает человек: раньше он молча брал язык системы, и переключить
; его было негде. Прежний выбор не запоминаем — на новой машине он не при чём.
ShowLanguageDialog=yes
UsePreviousLanguage=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "zh"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
en.desktopicon=Create a desktop shortcut
ru.desktopicon=Создать ярлык на рабочем столе
zh.desktopicon=创建桌面快捷方式
en.autostart=Start DSH Panel when Windows starts
ru.autostart=Запускать DSH Panel при входе в Windows
zh.autostart=开机时启动 DSH Panel
en.launch=Run the first-time setup wizard
ru.launch=Запустить мастер первой настройки
zh.launch=运行首次设置向导
en.dotnetWorking=Installing the .NET Desktop Runtime 8…
ru.dotnetWorking=Устанавливаю .NET Desktop Runtime 8…
zh.dotnetWorking=正在安装 .NET Desktop Runtime 8……
en.nodeNoDotNet=Node.js is installed by the panel, and the panel needs the .NET Desktop Runtime 8, which is missing. Install the runtime first (or use the "no .NET needed" build), then install Node from the first-time wizard.
ru.nodeNoDotNet=Node.js ставит сама панель, а ей нужен .NET Desktop Runtime 8, которого нет. Поставьте среду (или возьмите сборку «без .NET»), а Node поставится из мастера первой настройки.
zh.nodeNoDotNet=Node.js 由面板安装，而面板需要 .NET Desktop Runtime 8，但本机未安装。请先安装运行时（或使用“无需 .NET”的版本），Node 可在首次设置向导中安装。
en.nodeFailed=Node.js was not installed. The first-time wizard can try again: open the panel and press "Install Node…" on step 2. Details:
ru.nodeFailed=Node.js поставить не удалось. Мастер первой настройки может попробовать снова: откройте панель и нажмите «Установить Node…» на шаге 2. Подробности:
zh.nodeFailed=Node.js 未能安装。首次设置向导可以重试：打开面板，在第 2 步点击「安装 Node…」。详情：
en.installNodeTask=Install Node.js — required to run DSH
en.groupOptional=Optional
en.groupRequired=Required components
en.tasksNote=DSH does not work without the required components: no Node.js means no engine, no .NET runtime means no panel. Administrator rights are needed: Windows will ask for permission. You can install them later as well — the panel will remind you and say where to get them.
en.installDotNetTask=Install the .NET Desktop Runtime 8 — required by the panel
ru.installNodeTask=Установить Node.js — обязателен для работы DSH
ru.groupOptional=По желанию
ru.groupRequired=Обязательные компоненты
ru.tasksNote=Без обязательных компонентов DSH не работает: без Node.js не запустится движок, без среды .NET — сама панель. Понадобятся права администратора: Windows спросит разрешение. Доустановить их можно и позже — панель напомнит и скажет, где взять.
ru.installDotNetTask=Установить .NET Desktop Runtime 8 — обязателен для панели
zh.installNodeTask=安装 Node.js —— 运行 DSH 所必需
zh.groupOptional=可选
zh.groupRequired=必需组件
zh.tasksNote=没有必需组件 DSH 无法工作：没有 Node.js 就无法启动引擎，没有 .NET 运行环境就无法启动面板。需要管理员权限：Windows 会请求授权。也可以稍后再安装——面板会提醒并告知下载位置。
zh.installDotNetTask=安装 .NET Desktop Runtime 8 —— 面板所必需
en.restoreTitle=Restore from a backup
ru.restoreTitle=Восстановление из копии
zh.restoreTitle=从备份恢复
en.restoreSub=An archive was found next to the setup program.
ru.restoreSub=Рядом с установщиком нашлась резервная копия.
zh.restoreSub=在安装程序旁边找到了备份压缩包。
en.restoreHint=The backup holds your DSH data, the model key, profiles and skills, and — in a full backup — the DSH engine, Node and the panel installer. Panel settings (port, working folder) are not restored: the first-time wizard asks for them again, because they belong to the machine. Keys come back only with the second box ticked.
ru.restoreHint=В копии лежат данные DSH, ключ модели, профили и навыки, а в полной — ещё движок DSH, Node и установщик панели. Настройки панели (порт, рабочая папка) не восстанавливаются: их заново спросит мастер — они про конкретную машину. Ключи возвращаются только со второй галочкой.
zh.restoreHint=压缩包内含 DSH 数据、模型密钥、配置文件与技能；完整备份还含 DSH 引擎、Node 和面板安装程序。面板设置（端口、工作目录）不会恢复：首次设置向导会重新询问，因为它们与具体机器有关。只有勾选第二项才会恢复密钥。
en.restoreEnable=Restore the data from this archive
ru.restoreEnable=Восстановить данные из этой копии
zh.restoreEnable=从此压缩包恢复数据
en.restoreKeys=Also restore keys and certificates (only if the archive has them — such an archive is a secret)
ru.restoreKeys=Вернуть и ключи с сертификатами (только если они есть в архиве — такой архив является секретом)
zh.restoreKeys=同时恢复密钥与证书（仅在压缩包含有它们时——此类压缩包属于机密）
en.restoreDone=The backup has been restored. Details:
ru.restoreDone=Копия восстановлена. Подробности:
zh.restoreDone=备份已恢复。详情：
en.restoreFailedTail=The restore did not finish. Details:
ru.restoreFailedTail=Восстановление не завершилось. Подробности:
zh.restoreFailedTail=恢复未完成。详情：
en.restoreFailed=Could not start the restore: the panel did not run.
ru.restoreFailed=Не удалось запустить накат: панель не запустилась.
zh.restoreFailed=无法启动恢复：面板未能运行。
en.restoreWorking=Restoring the backup
ru.restoreWorking=Восстановление из копии
zh.restoreWorking=正在从备份恢复
en.restoreWorkingHint=The panel is unpacking the archive. This can take several minutes for a full backup — the window is not frozen, please wait.
ru.restoreWorkingHint=Панель распаковывает архив. Для полной копии это может занять несколько минут — окно не зависло, подождите.
zh.restoreWorkingHint=面板正在解压压缩包。完整备份可能需要几分钟——窗口并未卡住，请稍候。
en.restoreNoDotNet=The restore needs the .NET Desktop Runtime 8, and it is not installed. Install the runtime and restore from the panel: Backups → Restore from a backup.
ru.restoreNoDotNet=Для наката нужен .NET Desktop Runtime 8, а его нет. Поставьте среду и восстановите уже из панели: «Резервные копии» → «Восстановить из копии…».
zh.restoreNoDotNet=恢复需要 .NET Desktop Runtime 8，但本机未安装。请先安装运行时，然后在面板中恢复：备份 → 从备份恢复。
en.dotnetMissing=DSH Panel needs the .NET Desktop Runtime 8, and installing it did not work.%n%nTake exactly the Windows Desktop Runtime 8 — the file windowsdesktop-runtime-8.0.x-win-x64.exe: https://dotnet.microsoft.com/download/dotnet/8.0%n%nImportant: the .NET SDK does NOT replace it — the SDK does not contain the Windows Desktop Runtime, and the panel will not start with the SDK alone.%n%nOr take the "no .NET needed" build from the release page: it carries the runtime inside and starts on any Windows x64.%n%nThis does not stop the installation: install the runtime later, and the panel will work.
ru.dotnetMissing=Для панели нужен .NET Desktop Runtime 8, и поставить его не удалось.%n%nВозьмите именно Windows Desktop Runtime 8 — файл windowsdesktop-runtime-8.0.x-win-x64.exe: https://dotnet.microsoft.com/download/dotnet/8.0%n%nВажно: .NET SDK его НЕ заменяет — в SDK нет Windows Desktop Runtime, и панель с одним SDK не запустится.%n%nЛибо возьмите сборку «без .NET» со страницы выпуска: среда лежит внутри неё, и она запустится на любой Windows x64.%n%nУстановке это не мешает: поставьте среду позже, и панель заработает.
zh.dotnetMissing=DSH Panel 需要 .NET Desktop Runtime 8，但自动安装没有成功。%n%n请安装 Windows Desktop Runtime 8——文件 windowsdesktop-runtime-8.0.x-win-x64.exe：https://dotnet.microsoft.com/download/dotnet/8.0%n%n重要：.NET SDK 不能替代它——SDK 中不含 Windows Desktop Runtime，只有 SDK 时面板无法启动。%n%n也可以从发布页下载“无需 .NET”的版本：运行环境已包含其中，可在任何 Windows x64 上运行。%n%n这不影响安装：稍后安装运行环境，面板即可使用。
en.dotnetNoSignature=The .NET runtime bundled in the setup is not confirmed by a Microsoft signature, so it was not run.%n%nDownload the Windows Desktop Runtime 8 manually: https://dotnet.microsoft.com/download/dotnet/8.0%n%nOr take the "no .NET needed" build from the release page.%n%nThis does not stop the installation: the panel will start once the runtime is present.
ru.dotnetNoSignature=Среда .NET внутри установщика не подтверждена подписью Microsoft, поэтому запускать её мы не стали.%n%nСкачайте Windows Desktop Runtime 8 вручную: https://dotnet.microsoft.com/download/dotnet/8.0%n%nЛибо возьмите сборку «без .NET» со страницы выпуска.%n%nУстановке это не мешает: как только среда появится, панель запустится.
zh.dotnetNoSignature=安装程序中内置的 .NET 运行环境未通过 Microsoft 签名验证，因此未运行它。%n%n请手动下载 Windows Desktop Runtime 8：https://dotnet.microsoft.com/download/dotnet/8.0%n%n也可以从发布页下载“无需 .NET”的版本。%n%n这不影响安装：运行环境就位后面板即可启动。

[Tasks]
; Две группы: обязательные спутники (без них не запустятся ни панель, ни DSH) и удобства.
; Группа задаётся общим GroupDescription — Inno рисует заголовок и разделитель, так что
; обязательное не смешивается с необязательным (владелец просил разделить визуально).
Name: "dotnet"; Description: "{cm:installDotNetTask}"; GroupDescription: "{cm:groupRequired}"
Name: "nodejs"; Description: "{cm:installNodeTask}"; GroupDescription: "{cm:groupRequired}"
Name: "desktopicon"; Description: "{cm:desktopicon}"; GroupDescription: "{cm:groupOptional}"; Flags: unchecked
Name: "autostart"; Description: "{cm:autostart}"; GroupDescription: "{cm:groupOptional}"; Flags: unchecked

[Files]
; status.txt и logs\* — отчёты и журналы проверок (в них состояние сервера, баланс и пути),
; settings.json — настройки конкретной машины, *.pdb — отладочные символы,
; build.txt — штамп сборки с путём на машине разработчика.
Source: "{#AppSource}\*"; DestDir: "{app}"; \
    Excludes: "status.txt,logs\*,settings.json,*.log,*.pdb,build.txt"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

; Установщик кладём и рядом с панелью ({srcexe} — сам этот файл; в Source: он доступен только
; с флагом external, иначе компилятор ищет его на диске разработчика). Тогда панель всегда
; знает, чем её поставили: полная резервная копия уносит установщик с собой, и комплект
; «архив + установщик» не теряется, даже если файл выпуска где-то потеряли.
Source: "{srcexe}"; DestDir: "{app}"; DestName: "dsh-panel-setup.exe"; \
    Flags: external ignoreversion

#ifdef RuntimeBundled
; Среда .NET внутри установщика: dontcopy — файл кладётся в пакет, но на диск не распаковывается
; заранее; достаём его только если среды на машине нет (ExtractTemporaryFile), и он уходит
; вместе с временной папкой. В папке установки среда не остаётся.
Source: "{#RuntimeFile}"; DestDir: "{tmp}"; Flags: dontcopy
#endif

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "DSHPanel"; ValueData: """{app}\{#AppExeName}"" --tray"; \
    Flags: uninsdeletevalue; Tasks: autostart
; Ту же запись панель пишет сама галочкой «Запускать при входе в Windows», поэтому при
; удалении убираем её безусловно: иначе в автозапуске останется ссылка на удалённый exe.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; \
    ValueName: "DSHPanel"; Flags: uninsdeletevalue

[Run]
; runasoriginaluser: если установщик запущен «от администратора», панель всё равно должна
; стартовать от обычного пользователя — иначе она поднимет сервер и поставит Node с правами
; администратора.
Filename: "{app}\{#AppExeName}"; Parameters: "--onboard"; Description: "{cm:launch}"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Перед удалением закрываем работающую панель: иначе Inno не сможет удалить занятый
; DshTray.exe и предложит перезагрузку, а значок в трее останется жить из недобитой папки.
; Данные панели лежат в %APPDATA%\DshPanel и удалением файлов не затрагиваются.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExeName} /F"; \
    Flags: runhidden; RunOnceId: "StopPanelBeforeUninstall"

[UninstallDelete]
; Свою рабочую папку целиком не трогаем (там состояние и обновления истории), но убираем
; то, что восстановится само: staging обновления и распакованный переносимый Node.
Type: filesandordirs; Name: "{localappdata}\DshPanel\update"
Type: filesandordirs; Name: "{localappdata}\DshPanel\node"

[Code]
{ Есть ли в этой папке установленная среда Windows Desktop 8.x. }
function HasDesktopRuntimeIn(const root: String): Boolean;
var
  found: TFindRec;
begin
  Result := False;
  if not FindFirst(AddBackslash(root) + 'Microsoft.WindowsDesktop.App\*', found) then Exit;
  try
    repeat
      if ((found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Pos('8.', found.Name) = 1) then
      begin
        Result := True;
        Exit;
      end;
    until not FindNext(found);
  finally
    FindClose(found);
  end;
end;

{ Есть ли на машине .NET Desktop Runtime 8: панель собрана под него и без него не запустится.
  В установке не отказываем — предупреждаем и подсказываем, где взять (или сборку «без .NET»).

  Проверяем прежде всего ПАПКИ установки: реестр оказался ненадёжным признаком. На приёмке
  в чистой Windows среда стояла (8.0.31 в %ProgramFiles%\dotnet\shared\
  Microsoft.WindowsDesktop.App), а ветки реестра, по которой мы её искали, не было вовсе —
  и установщик бесконечно требовал поставить среду заново. }
function HasDotNet8Desktop: Boolean;
var
  names: TArrayOfString;
  index: Integer;
  roots: TArrayOfString;
begin
  Result := False;

  SetArrayLength(roots, 2);
  { Внимание: фигурные скобки внутри комментария писать нельзя — комментарий закроется на
    первой из них. Проба показала, что commonpf — это Program Files (x86), поэтому 64-битную
    среду ищем в commonpf64: с прежним набором проверка не находила установленную среду. }
  roots[0] := ExpandConstant('{commonpf64}\dotnet\shared');
  roots[1] := ExpandConstant('{commonpf32}\dotnet\shared');
  for index := 0 to GetArrayLength(roots) - 1 do
  begin
    if HasDesktopRuntimeIn(roots[index]) then
    begin
      Result := True;
      Exit;
    end;
  end;

  { Запасной признак — реестр: так среду записывают установщики Microsoft (но не все). }
  if not RegGetSubkeyNames(HKLM64,
       'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', names) then
    Exit;

  for index := 0 to GetArrayLength(names) - 1 do
  begin
    if Pos('8.', names[index]) = 1 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  { Никаких вопросов до мастера: что ставить, человек отмечает галочками на странице задач
    (Node.js и среда .NET стоят там первыми и уже отмечены). Установка идёт в конце, когда
    окно установки видно. }
  Result := True;
end;


{ Подписана ли скачанная среда Microsoft? Ссылка на среду плавающая (aka.ms отдаёт свежий
  выпуск 8.x), поэтому вместо жёсткой контрольной суммы, которую пришлось бы обновлять с
  каждым патчем, проверяем подпись файла: она и доказывает, что запускаем именно среду
  Microsoft. Возврат 0 — подпись верна и принадлежит Microsoft; 1 — подписи нет или она
  недействительна; 2 — подписано не Microsoft. }
function HasMicrosoftSignature(const filePath: String): Boolean;
var
  code: Integer;
  quote, script, safePath: String;
begin
  Result := False;

  { Путь попадает внутрь строки PowerShell: одиночную кавычку удваиваем, иначе путь с
    апострофом (C:\Users\O'Brien\…) сломал бы команду. }
  safePath := filePath;
  StringChangeEx(safePath, '''', '''''', True);
  quote := #39;

  script := '$s = Get-AuthenticodeSignature -LiteralPath ' + quote + safePath + quote + '; ' +
            'if ($s.Status -ne ' + quote + 'Valid' + quote + ') { exit 1 }; ' +
            'if ($s.SignerCertificate.Subject -notmatch ' + quote + 'Microsoft' + quote + ') { exit 2 }; ' +
            'exit 0';

  if not Exec('powershell.exe',
       '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + script + '"',
       '', SW_HIDE, ewWaitUntilTerminated, code) then
    Exit;

  Result := code = 0;
end;

{ Ждём среду в реестре: установщик среды поднимает себя с правами администратора, поэтому
  запущенный процесс может вернуть управление раньше, чем среда действительно появилась.
  Без этого ожидания установщик сообщал «среды нет» сразу после её установки. }
function WaitDotNet8(const seconds: Integer): Boolean;
var
  waited: Integer;
begin
  waited := 0;
  while waited < seconds do
  begin
    { В Pascal Script у Exit нет параметра: значение возвращают через Result. }
    if HasDotNet8Desktop then
    begin
      Result := True;
      Exit;
    end;

    Sleep(2000);
    waited := waited + 2;
  end;

  Result := HasDotNet8Desktop;
end;

{ Ставит среду .NET из файла внутри установщика. Вызывается в конце установки: окно в этот
  момент видно, и человек понимает, что идёт работа. Права администратора среда спрашивает
  сама (UAC) — это неизбежно, она ставится для всей машины. }
procedure InstallBundledDotNet;
var
  runtimePath: String;
  code: Integer;
begin
#ifdef RuntimeBundled
  try
    ExtractTemporaryFile('{#RuntimeName}');
    runtimePath := ExpandConstant('{tmp}\{#RuntimeName}');

    if not HasMicrosoftSignature(runtimePath) then
    begin
      MsgBox(ExpandConstant('{cm:dotnetNoSignature}'), mbError, MB_OK);
      Exit;
    end;

    if WizardForm.StatusLabel <> nil then
      WizardForm.StatusLabel.Caption := ExpandConstant('{cm:dotnetWorking}');

    if Exec(runtimePath, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, code) then
    begin
      { Код 0 или 3010 (нужна перезагрузка) — установка прошла; самой среды в реестре и папках
        может ещё не быть, поэтому ждём её появления. }
      if not WaitDotNet8(240) then
        MsgBox(ExpandConstant('{cm:dotnetMissing}'), mbInformation, MB_OK);
    end
    else
      MsgBox(ExpandConstant('{cm:dotnetMissing}'), mbInformation, MB_OK);
  except
    { Распаковать не удалось (мало места во временной папке) — скажем, где взять среду. }
    MsgBox(ExpandConstant('{cm:dotnetMissing}'), mbInformation, MB_OK);
  end;
#else
  { Установщик собран без среды внутри (сборка без интернета): о ней только сообщаем. }
  MsgBox(ExpandConstant('{cm:dotnetMissing}'), mbInformation, MB_OK);
#endif
end;

{ --- восстановление из копии до первого запуска ------------------------------- }

var
  RestorePage: TInputOptionWizardPage;
  RestoreProgress: TOutputProgressWizardPage;
  RestoreArchive: String;

{ Ищем свежую резервную копию рядом с установщиком. Если её нет — шаг не показываем:
  пустой вопрос «восстановиться?» только путает. }
function FindBackupArchive: String;
var
  found: TFindRec;
  best: String;
begin
  Result := '';
  best := '';
  if not FindFirst(ExpandConstant('{src}\dsh-backup-*.zip'), found) then Exit;
  try
    repeat
      { Имя копии начинается с даты: dsh-backup-2026-09-20-175149-full.zip, поэтому
        лексикографически старшая — самая свежая. }
      if CompareText(found.Name, best) > 0 then best := found.Name;
    until not FindNext(found);
  finally
    FindClose(found);
  end;

  if best <> '' then Result := ExpandConstant('{src}\') + best;
end;

var
  TasksNote: TNewStaticText;

procedure InitializeWizard;
begin
  { Сноска под списком задач: обе галочки обязательны, но поставить их можно и позже.
    Сноска привязана к странице задач (её Surface), поэтому видна только на ней. }
  TasksNote := TNewStaticText.Create(WizardForm);
  TasksNote.Parent := WizardForm.TasksList.Parent;
  TasksNote.Left := WizardForm.TasksList.Left;
  TasksNote.Top := WizardForm.TasksList.Top + WizardForm.TasksList.Height + ScaleY(10);
  TasksNote.Width := WizardForm.TasksList.Width;
  TasksNote.Height := ScaleY(34);
  TasksNote.WordWrap := True;
  TasksNote.Font.Size := TasksNote.Font.Size - 1;
  TasksNote.Font.Color := clGrayText;
  TasksNote.Caption := ExpandConstant('{cm:tasksNote}');

  { Среду .NET здесь НЕ ставим: мастер в этот момент ещё не показан, и любая долгая работа
    выглядит как зависший или пропавший установщик. Она ставится в конце установки —
    см. InstallBundledDotNet из CurStepChanged. }

  RestoreArchive := FindBackupArchive;
  if RestoreArchive = '' then Exit;

  { Шаг встаёт сразу после выбора задач. В Inno 7 у этой функции шесть параметров,
    а подписи галочек добавляются через Add() — так их текст идёт из словаря. }
  RestorePage := CreateInputOptionPage(wpSelectTasks, ExpandConstant('{cm:restoreTitle}'),
    ExpandConstant('{cm:restoreSub}'), ExpandConstant('{cm:restoreHint}'), False, False);
  RestorePage.Add(ExpandConstant('{cm:restoreEnable}'));
  RestorePage.Add(ExpandConstant('{cm:restoreKeys}'));
  RestorePage.Values[0] := False;
  RestorePage.Values[1] := False;

  { Полный накат идёт минутами: полоса прогресса нужна, чтобы человек не считал окно зависшим.
    Страница создаётся здесь, в InitializeWizard: до неё WizardForm ещё не существует. }
  RestoreProgress := CreateOutputProgressPage(ExpandConstant('{cm:restoreWorking}'),
    ExpandConstant('{cm:restoreWorkingHint}'));
end;

{ Накат идёт после копирования файлов, но до запуска мастера: --no-settings оставляем
  намеренно — порт и рабочую папку на новой машине должен спросить мастер.
  Порт не задаём: панель возьмёт свой из настроек и остановит только СВОЙ сервер —
  чужой процесс на этом порту она не трогает. }
procedure RestoreFromArchive;
var
  reportPath, parameters: String;
  started: Boolean;
  code, index, start: Integer;
  lines: TArrayOfString;
  tail: String;
begin
  reportPath := ExpandConstant('{tmp}\restore-report.txt');
  parameters := '--restore --from "' + RestoreArchive + '" --no-safety --no-settings' +
                ' --out "' + reportPath + '"';
  if RestorePage.Values[1] then
    parameters := parameters + ' --keys';

  RestoreProgress.SetText(ExpandConstant('{cm:restoreWorkingHint}'), '');
  RestoreProgress.Show;
  try
    started := Exec(ExpandConstant('{app}\{#AppExeName}'), parameters, ExpandConstant('{app}'),
      SW_SHOW, ewWaitUntilTerminated, code);
  finally
    RestoreProgress.Hide;
  end;

  if not started then
  begin
    MsgBox(ExpandConstant('{cm:restoreFailed}'), mbError, MB_OK);
    Exit;
  end;

  tail := '';
  if LoadStringsFromFile(reportPath, lines) then
  begin
    start := GetArrayLength(lines) - 8;
    if start < 0 then start := 0;
    for index := start to GetArrayLength(lines) - 1 do
      tail := tail + lines[index] + #13#10;
  end;

  if code = 0 then
    MsgBox(ExpandConstant('{cm:restoreDone}') + #13#10#13#10 + tail, mbInformation, MB_OK)
  else
    MsgBox(ExpandConstant('{cm:restoreFailedTail}') + #13#10#13#10 + tail, mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  report, text: String;
  lines: TArrayOfString;
  index, code, ResultCode: Integer;
begin
  { Страховка на случай, если панель не закрылась сама: без этого установка поверх работающей
    панели оставляет старый процесс живым, и он продолжает обслуживать трей старым кодом. }
  if CurStep = ssInstall then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExeName} /F',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
  end;

  if CurStep <> ssPostInstall then Exit;

  { Среда .NET: она внутри установщика, и ставим её здесь — окно установки в этот момент видно,
    и человек понимает, что идёт работа. Права среда спросит сама (UAC), это неизбежно:
    она ставится для всей машины. Галочку сняли — не ставим: панель потом скажет, чего не хватает. }
  if WizardIsTaskSelected('dotnet') and (not HasDotNet8Desktop) then
    InstallBundledDotNet;

  { Дальше Node: он нужен движку, и мастеру после этого останется поставить только DSH.
    Панель ставит Node сама (ключ --install-node): она умеет и обычную установку, и разложить
    переносимую сборку, если прав администратора нет. }
  if WizardIsTaskSelected('nodejs') then
  begin
    { Среда могла ставиться только что: её установщик поднимает себя с правами администратора
      и возвращает управление раньше, чем среда появляется в реестре. Ждём её появления,
      иначе установщик ругался «Node.js ставит сама панель, а ей нужен .NET», хотя среду
      только что поставили (это и увидел владелец на приёмке). }
    if not WaitDotNet8(60) then
      MsgBox(ExpandConstant('{cm:nodeNoDotNet}'), mbInformation, MB_OK)
    else
    begin
      report := ExpandConstant('{tmp}\node-report.txt');
      if Exec(ExpandConstant('{app}\{#AppExeName}'),
           '--install-node --out "' + report + '"', ExpandConstant('{app}'),
           SW_SHOW, ewWaitUntilTerminated, code) and (code = 0) then
      begin
        { Ничего не показываем: дальше мастер сам скажет, что Node найден. }
      end
      else
      begin
        { Отчёт читаем построчно: LoadStringsFromFile есть во всех версиях Inno. }
        text := '';
        if LoadStringsFromFile(report, lines) then
        begin
          for index := 0 to GetArrayLength(lines) - 1 do
            text := text + lines[index] + #13#10;
        end;

        if text <> '' then
          MsgBox(ExpandConstant('{cm:nodeFailed}') + #13#10#13#10 + text, mbError, MB_OK)
        else
          MsgBox(ExpandConstant('{cm:nodeFailed}'), mbError, MB_OK);
      end;
    end;
  end;

  if RestorePage = nil then Exit;
  if not RestorePage.Values[0] then Exit;

  { Панель без среды выполнения не запустится, а накат делает именно она. }
  if not HasDotNet8Desktop then
  begin
    MsgBox(ExpandConstant('{cm:restoreNoDotNet}'), mbError, MB_OK);
    Exit;
  end;

  RestoreFromArchive;
end;
