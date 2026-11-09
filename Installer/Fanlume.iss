; ==================================================
; Fanlume.iss - Fanlume のインストーラー（Inno Setup 6）
;   Distribution\make-dist.ps1 が、発行した exe と説明書を SourceDir にまとめてから、このスクリプトで setup.exe を作る:
;     ISCC.exe /DAppVersion=6.7.0 /DSourceDir=<まとめたフォルダー> /DOutputDir=<出力先> Installer\Fanlume.iss
;   入れる場所: C:\Program Files\Fanlume（設定・ログ・記録も同じフォルダーに置く。アプリは常に管理者権限で動く）
;   入れる・更新するとき: 動いている Fanlume（と前の名前の ProFanController）に「終了して」の合図を送り、
;     正しく終了する（ファンは BIOS 制御に戻る）のを待ってから入れ替える
;   アンインストールするとき: アプリを正しく終了させ、「Fanlume.exe --uninstall-cleanup」で自動起動・ファイアウォールの許可などを消す。
;     設定・ログ・記録を消すかは、ここで尋ねる（アプリの「お片付け」から呼ばれたときは /FANLUMEDATA=delete か keep で渡される）
; Dependencies: Inno Setup 6 (Inno Setup License)
; See CREDITS.txt for full license details.
; ==================================================

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir is required (Distribution\make-dist.ps1 passes it)
#endif
#ifndef OutputDir
  #define OutputDir ".."
#endif

#define AppName "Fanlume"
#define AppExe "Fanlume.exe"
#define RepoUrl "https://github.com/acdclove1234-commits/Fanlume"

[Setup]
AppId={{6D16AD73-005A-4A6E-95CA-46D885E2EF1E}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
DefaultDirName={autopf}\{#AppName}
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; 動いているアプリは、下の [Code] で正しく終了させる（Windows の再起動マネージャーに閉じさせると、ファンが BIOS 制御に戻らないことがある）
CloseApplications=no
RestartApplications=no
SetupMutex=FanlumeSetup
ShowLanguageDialog=auto
LanguageDetectionMethod=uilanguage

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "thai"; MessagesFile: "compiler:Languages\Thai.isl"

[CustomMessages]
WillClose=Fanlume is running. Setup closes it before installing. Until Fanlume starts again, the fans run on the BIOS settings.
WillMigrate=The settings and records of ProFanController (the previous name of this app) in C:\Tools\ProFanController are carried over when Fanlume starts.
CloseFailed=Fanlume could not be closed. Right-click the Fanlume icon in the notification area of the taskbar, choose "Exit (hand back to BIOS)", and try again.
UninstallCloseAsk=Fanlume is running. Exit it and uninstall?%nThe fans go back to the BIOS settings.
DeleteDataAsk=Also delete the settings, logs and records of Fanlume?%n%nChoose No to keep them in the app folder, for example to install Fanlume again later.

japanese.WillClose=Fanlume が動いています。セットアップは、入れる前に Fanlume を終了します。Fanlume がまた起動するまで、ファンは BIOS の設定で回ります。
japanese.WillMigrate=前の名前（ProFanController）で C:\Tools\ProFanController に入れていた設定と記録は、Fanlume が起動するときに引き継ぎます。
japanese.CloseFailed=Fanlume を終了できませんでした。タスクトレイの Fanlume のアイコンを右クリックし、「終了（BIOS 制御に戻す）」を選んでから、もう一度お試しください。
japanese.UninstallCloseAsk=Fanlume が動いています。終了してからアンインストールしますか？%nファンは BIOS の設定に戻ります。
japanese.DeleteDataAsk=Fanlume の設定・ログ・記録も削除しますか？%n%n「いいえ」を選ぶと、アプリのフォルダーに残します（あとで入れ直すときなど）。

german.WillClose=Fanlume läuft. Das Setup beendet Fanlume vor der Installation. Bis Fanlume wieder startet, laufen die Lüfter mit den BIOS-Einstellungen.
german.WillMigrate=Die Einstellungen und Aufzeichnungen von ProFanController (dem früheren Namen dieser App) in C:\Tools\ProFanController werden beim Start von Fanlume übernommen.
german.CloseFailed=Fanlume konnte nicht beendet werden. Klicken Sie mit der rechten Maustaste auf das Fanlume-Symbol im Infobereich der Taskleiste, wählen Sie „Beenden (zurück an BIOS)“ und versuchen Sie es erneut.
german.UninstallCloseAsk=Fanlume läuft. Beenden und deinstallieren?%nDie Lüfter laufen dann wieder mit den BIOS-Einstellungen.
german.DeleteDataAsk=Auch die Einstellungen, Protokolle und Aufzeichnungen von Fanlume löschen?%n%nMit „Nein“ bleiben sie im App-Ordner, zum Beispiel für eine spätere Neuinstallation.

french.WillClose=Fanlume est en cours d'exécution. L'installation le ferme avant de commencer. Jusqu'au redémarrage de Fanlume, les ventilateurs suivent les réglages du BIOS.
french.WillMigrate=Les réglages et enregistrements de ProFanController (l'ancien nom de cette application) dans C:\Tools\ProFanController seront repris au démarrage de Fanlume.
french.CloseFailed=Impossible de fermer Fanlume. Faites un clic droit sur l'icône Fanlume dans la zone de notification, choisissez « Quitter (rendre au BIOS) », puis réessayez.
french.UninstallCloseAsk=Fanlume est en cours d'exécution. Le quitter et le désinstaller ?%nLes ventilateurs reviennent aux réglages du BIOS.
french.DeleteDataAsk=Supprimer aussi les réglages, journaux et enregistrements de Fanlume ?%n%nChoisissez Non pour les garder dans le dossier de l'application, par exemple pour réinstaller Fanlume plus tard.

spanish.WillClose=Fanlume se está ejecutando. El instalador lo cierra antes de instalar. Hasta que Fanlume vuelva a iniciarse, los ventiladores siguen la configuración de la BIOS.
spanish.WillMigrate=La configuración y el historial de ProFanController (el nombre anterior de esta aplicación) en C:\Tools\ProFanController se conservarán al iniciar Fanlume.
spanish.CloseFailed=No se pudo cerrar Fanlume. Haz clic derecho en el icono de Fanlume en el área de notificación, elige «Salir (devolver a la BIOS)» y vuelve a intentarlo.
spanish.UninstallCloseAsk=Fanlume se está ejecutando. ¿Cerrarlo y desinstalarlo?%nLos ventiladores vuelven a la configuración de la BIOS.
spanish.DeleteDataAsk=¿Eliminar también la configuración, los registros y el historial de Fanlume?%n%nElige No para conservarlos en la carpeta de la aplicación, por ejemplo para volver a instalar Fanlume más adelante.

italian.WillClose=Fanlume è in esecuzione. L'installazione lo chiude prima di installare. Finché Fanlume non si riavvia, le ventole seguono le impostazioni del BIOS.
italian.WillMigrate=Le impostazioni e le registrazioni di ProFanController (il nome precedente di questa app) in C:\Tools\ProFanController verranno riprese all'avvio di Fanlume.
italian.CloseFailed=Impossibile chiudere Fanlume. Fai clic con il tasto destro sull'icona di Fanlume nell'area di notifica, scegli «Esci (ritorno al BIOS)» e riprova.
italian.UninstallCloseAsk=Fanlume è in esecuzione. Chiuderlo e disinstallarlo?%nLe ventole tornano alle impostazioni del BIOS.
italian.DeleteDataAsk=Eliminare anche le impostazioni, i log e le registrazioni di Fanlume?%n%nScegli No per conservarli nella cartella dell'app, ad esempio per reinstallare Fanlume in seguito.

brazilianportuguese.WillClose=O Fanlume está em execução. O instalador o fecha antes de instalar. Até o Fanlume iniciar de novo, as ventoinhas seguem as configurações da BIOS.
brazilianportuguese.WillMigrate=As configurações e os registros do ProFanController (o nome anterior deste app) em C:\Tools\ProFanController serão mantidos quando o Fanlume iniciar.
brazilianportuguese.CloseFailed=Não foi possível fechar o Fanlume. Clique com o botão direito no ícone do Fanlume na área de notificação, escolha "Sair (devolver à BIOS)" e tente de novo.
brazilianportuguese.UninstallCloseAsk=O Fanlume está em execução. Fechar e desinstalar?%nAs ventoinhas voltam às configurações da BIOS.
brazilianportuguese.DeleteDataAsk=Excluir também as configurações, os logs e os registros do Fanlume?%n%nEscolha Não para mantê-los na pasta do app, por exemplo para instalar o Fanlume de novo mais tarde.

polish.WillClose=Fanlume jest uruchomiony. Instalator zamknie go przed instalacją. Do ponownego uruchomienia Fanlume wentylatory działają według ustawień BIOS.
polish.WillMigrate=Ustawienia i zapisy ProFanController (poprzednia nazwa tej aplikacji) z C:\Tools\ProFanController zostaną przeniesione przy uruchomieniu Fanlume.
polish.CloseFailed=Nie udało się zamknąć Fanlume. Kliknij prawym przyciskiem ikonę Fanlume w obszarze powiadomień, wybierz „Zakończ (oddaj BIOS)” i spróbuj ponownie.
polish.UninstallCloseAsk=Fanlume jest uruchomiony. Zamknąć go i odinstalować?%nWentylatory wrócą do ustawień BIOS.
polish.DeleteDataAsk=Usunąć też ustawienia, logi i zapisy Fanlume?%n%nWybierz Nie, aby zostawić je w folderze aplikacji, na przykład do ponownej instalacji Fanlume.

dutch.WillClose=Fanlume is actief. Setup sluit het af voordat het installeert. Tot Fanlume weer start, draaien de ventilatoren op de BIOS-instellingen.
dutch.WillMigrate=De instellingen en registraties van ProFanController (de vorige naam van deze app) in C:\Tools\ProFanController worden overgenomen wanneer Fanlume start.
dutch.CloseFailed=Fanlume kon niet worden afgesloten. Klik met de rechtermuisknop op het Fanlume-pictogram in het systeemvak, kies "Afsluiten (terug naar BIOS)" en probeer het opnieuw.
dutch.UninstallCloseAsk=Fanlume is actief. Afsluiten en verwijderen?%nDe ventilatoren gaan terug naar de BIOS-instellingen.
dutch.DeleteDataAsk=Ook de instellingen, logboeken en registraties van Fanlume verwijderen?%n%nKies Nee om ze in de app-map te bewaren, bijvoorbeeld om Fanlume later opnieuw te installeren.

turkish.WillClose=Fanlume çalışıyor. Kurulum, yüklemeden önce Fanlume'u kapatır. Fanlume yeniden başlayana kadar fanlar BIOS ayarlarıyla çalışır.
turkish.WillMigrate=ProFanController'ın (bu uygulamanın önceki adı) C:\Tools\ProFanController içindeki ayarları ve kayıtları, Fanlume başladığında aktarılır.
turkish.CloseFailed=Fanlume kapatılamadı. Bildirim alanındaki Fanlume simgesine sağ tıklayın, "Çık (BIOS'a bırak)" seçeneğini seçin ve yeniden deneyin.
turkish.UninstallCloseAsk=Fanlume çalışıyor. Kapatıp kaldırılsın mı?%nFanlar BIOS ayarlarına döner.
turkish.DeleteDataAsk=Fanlume'un ayarları, günlükleri ve kayıtları da silinsin mi?%n%nBunları uygulama klasöründe tutmak için Hayır'ı seçin (örneğin Fanlume'u daha sonra yeniden yüklemek için).

russian.WillClose=Fanlume запущен. Перед установкой программа установки закроет его. Пока Fanlume снова не запустится, вентиляторы работают по настройкам BIOS.
russian.WillMigrate=Настройки и записи ProFanController (прежнее название этого приложения) из C:\Tools\ProFanController будут перенесены при запуске Fanlume.
russian.CloseFailed=Не удалось закрыть Fanlume. Щёлкните правой кнопкой по значку Fanlume в области уведомлений, выберите «Выход (вернуть управление BIOS)» и попробуйте снова.
russian.UninstallCloseAsk=Fanlume запущен. Закрыть его и удалить?%nВентиляторы вернутся к настройкам BIOS.
russian.DeleteDataAsk=Удалить также настройки, журналы и записи Fanlume?%n%nВыберите «Нет», чтобы оставить их в папке приложения, например чтобы потом установить Fanlume снова.

ukrainian.WillClose=Fanlume запущено. Перед встановленням програма встановлення закриє його. Доки Fanlume знову не запуститься, вентилятори працюють за налаштуваннями BIOS.
ukrainian.WillMigrate=Налаштування й записи ProFanController (попередня назва цього застосунку) з C:\Tools\ProFanController буде перенесено під час запуску Fanlume.
ukrainian.CloseFailed=Не вдалося закрити Fanlume. Клацніть правою кнопкою значок Fanlume в області сповіщень, виберіть «Вихід (повернути керування BIOS)» і спробуйте ще раз.
ukrainian.UninstallCloseAsk=Fanlume запущено. Закрити його та видалити?%nВентилятори повернуться до налаштувань BIOS.
ukrainian.DeleteDataAsk=Видалити також налаштування, журнали й записи Fanlume?%n%nВиберіть «Ні», щоб залишити їх у папці застосунку, наприклад щоб пізніше встановити Fanlume знову.

korean.WillClose=Fanlume이 실행 중입니다. 설치 프로그램이 설치 전에 Fanlume을 종료합니다. Fanlume이 다시 시작될 때까지 팬은 BIOS 설정으로 돌아갑니다.
korean.WillMigrate=이 앱의 이전 이름인 ProFanController로 C:\Tools\ProFanController에 저장된 설정과 기록은 Fanlume이 시작될 때 이어받습니다.
korean.CloseFailed=Fanlume을 종료하지 못했습니다. 작업 표시줄 알림 영역의 Fanlume 아이콘을 마우스 오른쪽 버튼으로 클릭하고 "종료(BIOS 제어로 되돌림)"를 선택한 뒤 다시 시도하세요.
korean.UninstallCloseAsk=Fanlume이 실행 중입니다. 종료하고 제거할까요?%n팬은 BIOS 설정으로 돌아갑니다.
korean.DeleteDataAsk=Fanlume의 설정, 로그, 기록도 삭제할까요?%n%n"아니요"를 선택하면 앱 폴더에 남겨 둡니다(나중에 Fanlume을 다시 설치할 때 등).

thai.WillClose=Fanlume กำลังทำงานอยู่ ตัวติดตั้งจะปิด Fanlume ก่อนติดตั้ง จนกว่า Fanlume จะเริ่มทำงานอีกครั้ง พัดลมจะหมุนตามการตั้งค่า BIOS
thai.WillMigrate=การตั้งค่าและบันทึกของ ProFanController (ชื่อเดิมของแอปนี้) ใน C:\Tools\ProFanController จะถูกย้ายมาใช้เมื่อ Fanlume เริ่มทำงาน
thai.CloseFailed=ปิด Fanlume ไม่ได้ คลิกขวาที่ไอคอน Fanlume ในพื้นที่แจ้งเตือน เลือก "ออก (คืนให้ BIOS)" แล้วลองอีกครั้ง
thai.UninstallCloseAsk=Fanlume กำลังทำงานอยู่ ต้องการปิดและถอนการติดตั้งหรือไม่?%nพัดลมจะกลับไปใช้การตั้งค่า BIOS
thai.DeleteDataAsk=ลบการตั้งค่า บันทึกการทำงาน และบันทึกข้อมูลของ Fanlume ด้วยหรือไม่?%n%nเลือก "ไม่" เพื่อเก็บไว้ในโฟลเดอร์ของแอป เช่น เพื่อติดตั้ง Fanlume ใหม่ภายหลัง

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; make-dist.ps1 がまとめた exe・説明書・バッチ・ライセンスを、そのままアプリのフォルダーへ
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; ふつうに入れたとき: 最後の画面で「Fanlume を起動する」（はじめての起動では、アプリのセットアップの画面が出る）
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; 画面を出さずに更新したとき: 動いていたなら、タスクトレイで起動し直す
Filename: "{app}\{#AppExe}"; Parameters: "--minimized"; Flags: nowait skipifnotsilent; Check: AppWasRunning

[Code]
const
  EVENT_MODIFY_STATE = $0002;
  AppMutexes = 'Global\Fanlume.SingleInstance,Global\ProFanController.SingleInstance';
  ExitSignals = 'Global\Fanlume.Exit,Global\ProFanController.Exit';
  LegacyConfig = 'C:\Tools\ProFanController\config.json';

var
  WasRunning: Boolean;

function OpenEvent(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

function AppWasRunning(): Boolean;
begin
  Result := WasRunning;
end;

{ Fanlume（または前の名前の ProFanController）が動いているか: 二重起動を防ぐ印で調べる }
function AppRunning(): Boolean;
begin
  Result := CheckForMutexes(AppMutexes);
end;

{ 同じ名前の exe のプロセスの数（見張りプロセスも数える。数えられなければ 0） }
function ProcessCount(const ExeName: String): Integer;
var
  Locator, Service, Items: Variant;
begin
  Result := 0;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Items := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = "' + ExeName + '"');
    Result := Items.Count;
  except
    Result := 0;
  end;
end;

procedure SignalExit(const Name: String);
var
  H: THandle;
begin
  H := OpenEvent(EVENT_MODIFY_STATE, False, Name);
  if H <> 0 then
  begin
    SetEvent(H);
    CloseHandle(H);
  end;
end;

{ 動いているアプリに「終了して」の合図を送り、正しく終了する（ファンを BIOS 制御に戻す）のを最大 40 秒待つ。
  見張りプロセスが exe を開いたままだと入れ替えられないので、Fanlume.exe のプロセスがなくなるまで待つ }
function CloseApp(): Boolean;
var
  I: Integer;
  Names: TStringList;
begin
  if AppRunning() then
  begin
    Names := TStringList.Create;
    try
      Names.CommaText := ExitSignals;
      for I := 0 to Names.Count - 1 do
        SignalExit(Names[I]);
    finally
      Names.Free;
    end;
  end;
  for I := 1 to 160 do
  begin
    if (not AppRunning()) and (ProcessCount('{#AppExe}') = 0) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(250);
  end;
  Result := (not AppRunning()) and (ProcessCount('{#AppExe}') = 0);
end;

{ ----- 入れる・更新する ----- }

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo;
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
  if AppRunning() then
    Result := Result + NewLine + NewLine + CustomMessage('WillClose');
  if FileExists(LegacyConfig) and not FileExists(ExpandConstant('{app}\config.json')) then
    Result := Result + NewLine + NewLine + CustomMessage('WillMigrate');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  WasRunning := AppRunning();
  if not CloseApp() then
    Result := CustomMessage('CloseFailed');
end;

{ ----- アンインストール ----- }

var
  DeleteData: Boolean;

{ アプリの「お片付け」から渡された答え（/FANLUMEDATA=delete か keep。なければ空） }
function DataChoiceFromCommandLine(): String;
var
  I: Integer;
  S: String;
begin
  Result := '';
  for I := 1 to ParamCount do
  begin
    S := ParamStr(I);
    if CompareText(Copy(S, 1, 13), '/FANLUMEDATA=') = 0 then
      Result := Lowercase(Copy(S, 14, Length(S) - 13));
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if not AppRunning() then
    Exit;
  if SuppressibleMsgBox(CustomMessage('UninstallCloseAsk'), mbConfirmation, MB_YESNO, IDYES) <> IDYES then
  begin
    Result := False;
    Exit;
  end;
  if not CloseApp() then
  begin
    SuppressibleMsgBox(CustomMessage('CloseFailed'), mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Choice, Args: String;
  ResultCode: Integer;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  Choice := DataChoiceFromCommandLine();
  if Choice = 'delete' then
    DeleteData := True
  else if Choice = 'keep' then
    DeleteData := False
  else
    DeleteData := SuppressibleMsgBox(CustomMessage('DeleteDataAsk'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
  { 見張りプロセスなどが残っていれば、終わるまで待つ（アプリが Windows に登録したものを消す前に） }
  CloseApp();
  Args := '--uninstall-cleanup';
  if DeleteData then
    Args := Args + ' --delete-data';
  Exec(ExpandConstant('{app}\{#AppExe}'), Args, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
