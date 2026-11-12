; ==================================================
; Zeph-Iris.iss - Zeph-Iris のインストーラー（Inno Setup 6）
;   Distribution\make-dist.ps1 が、発行した exe と説明書を SourceDir にまとめてから、このスクリプトで setup.exe を作る:
;     ISCC.exe /DAppVersion=6.7.0 /DSourceDir=<まとめたフォルダー> /DOutputDir=<出力先> Installer\Zeph-Iris.iss
;   入れる場所: C:\Program Files\Zeph-Iris（設定・ログ・記録も同じフォルダーに置く。アプリは常に管理者権限で動く）
;   入れる・更新するとき: 動いている Zeph-Iris（と前の名前の ProFanController）に「終了して」の合図を送り、
;     正しく終了する（ファンは BIOS 制御に戻る）のを待ってから入れ替える
;   アンインストールするとき: アプリを正しく終了させ、「Zeph-Iris.exe --uninstall-cleanup」で自動起動・ファイアウォールの許可などを消す。
;     設定・ログ・記録を消すかは、ここで尋ねる（アプリの「お片付け」から呼ばれたときは /ZEPHIRISDATA=delete か keep で渡される）
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

#define AppName "Zeph-Iris"
#define AppExe "Zeph-Iris.exe"
#define RepoUrl "https://github.com/acdclove1234-commits/Zeph-Iris"

[Setup]
AppId={{0D723C37-A7D9-4A7E-B013-F7F1700BDBA3}
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
SetupMutex=Zeph-IrisSetup
; 入れ終わったらエクスプローラーにアイコンが変わったことを知らせる（更新でアイコンを変えても、ショートカットやタスクバーに古いアイコンが残らないように）
ChangesAssociations=yes
ShowLanguageDialog=auto
LanguageDetectionMethod=uilanguage
; 入れる前に、通信するものとプライバシーを見せる（SignPath の決まり・PRIVACY.md の要約）
InfoBeforeFile=privacy-en.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"; InfoBeforeFile: "privacy-ja.txt"
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
WillClose=Zeph-Iris is running. Setup closes it before installing. Until Zeph-Iris starts again, the fans run on the BIOS settings.
WillMigrate=The settings and records of the previous version of this app (Fanlume or ProFanController) are carried over when Zeph-Iris starts.
CloseFailed=Zeph-Iris could not be closed. Right-click the Zeph-Iris icon in the notification area of the taskbar, choose "Exit (hand back to BIOS)", and try again.
UninstallCloseAsk=Zeph-Iris is running. Exit it and uninstall?%nThe fans go back to the BIOS settings.
DeleteDataAsk=Also delete the settings, logs and records of Zeph-Iris?%n%nChoose No to keep them in the app folder, for example to install Zeph-Iris again later.
WillInstallPawnIO=PawnIO, the driver Zeph-Iris uses for fan control, is not installed. Setup installs it with the official PawnIO installer included in this setup.
InstallingPawnIO=Installing PawnIO, the driver for fan control...
japanese.WillInstallPawnIO=ファン制御に使うドライバー（PawnIO）が入っていないので、このセットアップに入っている PawnIO の公式のインストーラーでインストールします。
japanese.InstallingPawnIO=ファン制御に使うドライバー（PawnIO）をインストールしています…
german.WillInstallPawnIO=PawnIO, der Treiber, den Zeph-Iris für die Lüftersteuerung nutzt, ist nicht installiert. Das Setup installiert ihn mit dem offiziellen PawnIO-Installer, der in diesem Setup enthalten ist.
german.InstallingPawnIO=PawnIO, der Treiber für die Lüftersteuerung, wird installiert…
french.WillInstallPawnIO=PawnIO, le pilote utilisé par Zeph-Iris pour contrôler les ventilateurs, n'est pas installé. L'installation l'ajoute avec l'installeur officiel de PawnIO inclus dans ce programme d'installation.
french.InstallingPawnIO=Installation de PawnIO, le pilote de contrôle des ventilateurs…
spanish.WillInstallPawnIO=PawnIO, el controlador que Zeph-Iris usa para los ventiladores, no está instalado. El instalador lo instalará con el instalador oficial de PawnIO incluido en este instalador.
spanish.InstallingPawnIO=Instalando PawnIO, el controlador para los ventiladores…
italian.WillInstallPawnIO=PawnIO, il driver che Zeph-Iris usa per le ventole, non è installato. L'installazione lo aggiunge con il programma di installazione ufficiale di PawnIO incluso in questo setup.
italian.InstallingPawnIO=Installazione di PawnIO, il driver per le ventole…
brazilianportuguese.WillInstallPawnIO=O PawnIO, driver que o Zeph-Iris usa para controlar as ventoinhas, não está instalado. O instalador vai instalá-lo com o instalador oficial do PawnIO incluído nesta instalação.
brazilianportuguese.InstallingPawnIO=Instalando o PawnIO, o driver de controle das ventoinhas…
polish.WillInstallPawnIO=PawnIO, sterownik używany przez Zeph-Iris do sterowania wentylatorami, nie jest zainstalowany. Instalator zainstaluje go oficjalnym instalatorem PawnIO dołączonym do tej instalacji.
polish.InstallingPawnIO=Instalowanie PawnIO, sterownika do sterowania wentylatorami…
dutch.WillInstallPawnIO=PawnIO, het stuurprogramma dat Zeph-Iris voor de ventilatoren gebruikt, is niet geïnstalleerd. Setup installeert het met het officiële PawnIO-installatieprogramma dat in deze setup zit.
dutch.InstallingPawnIO=PawnIO, het stuurprogramma voor de ventilatorregeling, wordt geïnstalleerd…
turkish.WillInstallPawnIO=Zeph-Iris'in fan denetimi için kullandığı PawnIO sürücüsü yüklü değil. Kurulum onu bu kuruluma dahil olan resmi PawnIO yükleyicisiyle yükleyecek.
turkish.InstallingPawnIO=Fan denetimi sürücüsü PawnIO yükleniyor…
russian.WillInstallPawnIO=PawnIO — драйвер, который Zeph-Iris использует для управления вентиляторами, — не установлен. Программа установки поставит его с помощью официального установщика PawnIO, входящего в этот установщик.
russian.InstallingPawnIO=Установка PawnIO — драйвера для управления вентиляторами…
ukrainian.WillInstallPawnIO=PawnIO — драйвер, який Zeph-Iris використовує для керування вентиляторами, — не встановлено. Програма встановлення встановить його за допомогою офіційного інсталятора PawnIO, що входить до цього інсталятора.
ukrainian.InstallingPawnIO=Встановлення PawnIO — драйвера для керування вентиляторами…
korean.WillInstallPawnIO=Zeph-Iris가 팬 제어에 쓰는 드라이버 PawnIO가 설치되어 있지 않습니다. 이 설치 프로그램에 들어 있는 PawnIO 공식 설치 프로그램으로 설치합니다.
korean.InstallingPawnIO=팬 제어용 드라이버 PawnIO를 설치하고 있습니다…
thai.WillInstallPawnIO=ยังไม่ได้ติดตั้ง PawnIO ซึ่งเป็นไดรเวอร์ที่ Zeph-Iris ใช้ควบคุมพัดลม ตัวติดตั้งจะติดตั้งด้วยตัวติดตั้งทางการของ PawnIO ที่รวมมาในตัวติดตั้งนี้
thai.InstallingPawnIO=กำลังติดตั้ง PawnIO ไดรเวอร์สำหรับควบคุมพัดลม…

japanese.WillClose=Zeph-Iris が動いています。セットアップは、入れる前に Zeph-Iris を終了します。Zeph-Iris がまた起動するまで、ファンは BIOS の設定で回ります。
japanese.WillMigrate=このアプリの前の版（Fanlume・ProFanController）の設定と記録は、Zeph-Iris が起動するときに引き継ぎます。
japanese.CloseFailed=Zeph-Iris を終了できませんでした。タスクトレイの Zeph-Iris のアイコンを右クリックし、「終了（BIOS 制御に戻す）」を選んでから、もう一度お試しください。
japanese.UninstallCloseAsk=Zeph-Iris が動いています。終了してからアンインストールしますか？%nファンは BIOS の設定に戻ります。
japanese.DeleteDataAsk=Zeph-Iris の設定・ログ・記録も削除しますか？%n%n「いいえ」を選ぶと、アプリのフォルダーに残します（あとで入れ直すときなど）。

german.WillClose=Zeph-Iris läuft. Das Setup beendet Zeph-Iris vor der Installation. Bis Zeph-Iris wieder startet, laufen die Lüfter mit den BIOS-Einstellungen.
german.WillMigrate=Die Einstellungen und Aufzeichnungen der früheren Version dieser App (Fanlume oder ProFanController) werden beim Start von Zeph-Iris übernommen.
german.CloseFailed=Zeph-Iris konnte nicht beendet werden. Klicken Sie mit der rechten Maustaste auf das Zeph-Iris-Symbol im Infobereich der Taskleiste, wählen Sie „Beenden (zurück an BIOS)“ und versuchen Sie es erneut.
german.UninstallCloseAsk=Zeph-Iris läuft. Beenden und deinstallieren?%nDie Lüfter laufen dann wieder mit den BIOS-Einstellungen.
german.DeleteDataAsk=Auch die Einstellungen, Protokolle und Aufzeichnungen von Zeph-Iris löschen?%n%nMit „Nein“ bleiben sie im App-Ordner, zum Beispiel für eine spätere Neuinstallation.

french.WillClose=Zeph-Iris est en cours d'exécution. L'installation le ferme avant de commencer. Jusqu'au redémarrage de Zeph-Iris, les ventilateurs suivent les réglages du BIOS.
french.WillMigrate=Les réglages et enregistrements de la version précédente de cette application (Fanlume ou ProFanController) seront repris au démarrage de Zeph-Iris.
french.CloseFailed=Impossible de fermer Zeph-Iris. Faites un clic droit sur l'icône Zeph-Iris dans la zone de notification, choisissez « Quitter (rendre au BIOS) », puis réessayez.
french.UninstallCloseAsk=Zeph-Iris est en cours d'exécution. Le quitter et le désinstaller ?%nLes ventilateurs reviennent aux réglages du BIOS.
french.DeleteDataAsk=Supprimer aussi les réglages, journaux et enregistrements de Zeph-Iris ?%n%nChoisissez Non pour les garder dans le dossier de l'application, par exemple pour réinstaller Zeph-Iris plus tard.

spanish.WillClose=Zeph-Iris se está ejecutando. El instalador lo cierra antes de instalar. Hasta que Zeph-Iris vuelva a iniciarse, los ventiladores siguen la configuración de la BIOS.
spanish.WillMigrate=La configuración y el historial de la versión anterior de esta aplicación (Fanlume o ProFanController) se conservarán al iniciar Zeph-Iris.
spanish.CloseFailed=No se pudo cerrar Zeph-Iris. Haz clic derecho en el icono de Zeph-Iris en el área de notificación, elige «Salir (devolver a la BIOS)» y vuelve a intentarlo.
spanish.UninstallCloseAsk=Zeph-Iris se está ejecutando. ¿Cerrarlo y desinstalarlo?%nLos ventiladores vuelven a la configuración de la BIOS.
spanish.DeleteDataAsk=¿Eliminar también la configuración, los registros y el historial de Zeph-Iris?%n%nElige No para conservarlos en la carpeta de la aplicación, por ejemplo para volver a instalar Zeph-Iris más adelante.

italian.WillClose=Zeph-Iris è in esecuzione. L'installazione lo chiude prima di installare. Finché Zeph-Iris non si riavvia, le ventole seguono le impostazioni del BIOS.
italian.WillMigrate=Le impostazioni e le registrazioni della versione precedente di questa app (Fanlume o ProFanController) verranno riprese all'avvio di Zeph-Iris.
italian.CloseFailed=Impossibile chiudere Zeph-Iris. Fai clic con il tasto destro sull'icona di Zeph-Iris nell'area di notifica, scegli «Esci (ritorno al BIOS)» e riprova.
italian.UninstallCloseAsk=Zeph-Iris è in esecuzione. Chiuderlo e disinstallarlo?%nLe ventole tornano alle impostazioni del BIOS.
italian.DeleteDataAsk=Eliminare anche le impostazioni, i log e le registrazioni di Zeph-Iris?%n%nScegli No per conservarli nella cartella dell'app, ad esempio per reinstallare Zeph-Iris in seguito.

brazilianportuguese.WillClose=O Zeph-Iris está em execução. O instalador o fecha antes de instalar. Até o Zeph-Iris iniciar de novo, as ventoinhas seguem as configurações da BIOS.
brazilianportuguese.WillMigrate=As configurações e os registros da versão anterior deste app (Fanlume ou ProFanController) serão mantidos quando o Zeph-Iris iniciar.
brazilianportuguese.CloseFailed=Não foi possível fechar o Zeph-Iris. Clique com o botão direito no ícone do Zeph-Iris na área de notificação, escolha "Sair (devolver à BIOS)" e tente de novo.
brazilianportuguese.UninstallCloseAsk=O Zeph-Iris está em execução. Fechar e desinstalar?%nAs ventoinhas voltam às configurações da BIOS.
brazilianportuguese.DeleteDataAsk=Excluir também as configurações, os logs e os registros do Zeph-Iris?%n%nEscolha Não para mantê-los na pasta do app, por exemplo para instalar o Zeph-Iris de novo mais tarde.

polish.WillClose=Zeph-Iris jest uruchomiony. Instalator zamknie go przed instalacją. Do ponownego uruchomienia Zeph-Iris wentylatory działają według ustawień BIOS.
polish.WillMigrate=Ustawienia i zapisy poprzedniej wersji tej aplikacji (Fanlume lub ProFanController) zostaną przeniesione przy uruchomieniu Zeph-Iris.
polish.CloseFailed=Nie udało się zamknąć Zeph-Iris. Kliknij prawym przyciskiem ikonę Zeph-Iris w obszarze powiadomień, wybierz „Zakończ (oddaj BIOS)” i spróbuj ponownie.
polish.UninstallCloseAsk=Zeph-Iris jest uruchomiony. Zamknąć go i odinstalować?%nWentylatory wrócą do ustawień BIOS.
polish.DeleteDataAsk=Usunąć też ustawienia, logi i zapisy Zeph-Iris?%n%nWybierz Nie, aby zostawić je w folderze aplikacji, na przykład do ponownej instalacji Zeph-Iris.

dutch.WillClose=Zeph-Iris is actief. Setup sluit het af voordat het installeert. Tot Zeph-Iris weer start, draaien de ventilatoren op de BIOS-instellingen.
dutch.WillMigrate=De instellingen en registraties van de vorige versie van deze app (Fanlume of ProFanController) worden overgenomen wanneer Zeph-Iris start.
dutch.CloseFailed=Zeph-Iris kon niet worden afgesloten. Klik met de rechtermuisknop op het Zeph-Iris-pictogram in het systeemvak, kies "Afsluiten (terug naar BIOS)" en probeer het opnieuw.
dutch.UninstallCloseAsk=Zeph-Iris is actief. Afsluiten en verwijderen?%nDe ventilatoren gaan terug naar de BIOS-instellingen.
dutch.DeleteDataAsk=Ook de instellingen, logboeken en registraties van Zeph-Iris verwijderen?%n%nKies Nee om ze in de app-map te bewaren, bijvoorbeeld om Zeph-Iris later opnieuw te installeren.

turkish.WillClose=Zeph-Iris çalışıyor. Kurulum, yüklemeden önce Zeph-Iris'i kapatır. Zeph-Iris yeniden başlayana kadar fanlar BIOS ayarlarıyla çalışır.
turkish.WillMigrate=Bu uygulamanın önceki sürümünün (Fanlume veya ProFanController) ayarları ve kayıtları, Zeph-Iris başladığında aktarılır.
turkish.CloseFailed=Zeph-Iris kapatılamadı. Bildirim alanındaki Zeph-Iris simgesine sağ tıklayın, "Çık (BIOS'a bırak)" seçeneğini seçin ve yeniden deneyin.
turkish.UninstallCloseAsk=Zeph-Iris çalışıyor. Kapatıp kaldırılsın mı?%nFanlar BIOS ayarlarına döner.
turkish.DeleteDataAsk=Zeph-Iris'in ayarları, günlükleri ve kayıtları da silinsin mi?%n%nBunları uygulama klasöründe tutmak için Hayır'ı seçin (örneğin Zeph-Iris'i daha sonra yeniden yüklemek için).

russian.WillClose=Zeph-Iris запущен. Перед установкой программа установки закроет его. Пока Zeph-Iris снова не запустится, вентиляторы работают по настройкам BIOS.
russian.WillMigrate=Настройки и записи предыдущей версии этого приложения (Fanlume или ProFanController) будут перенесены при запуске Zeph-Iris.
russian.CloseFailed=Не удалось закрыть Zeph-Iris. Щёлкните правой кнопкой по значку Zeph-Iris в области уведомлений, выберите «Выход (вернуть управление BIOS)» и попробуйте снова.
russian.UninstallCloseAsk=Zeph-Iris запущен. Закрыть его и удалить?%nВентиляторы вернутся к настройкам BIOS.
russian.DeleteDataAsk=Удалить также настройки, журналы и записи Zeph-Iris?%n%nВыберите «Нет», чтобы оставить их в папке приложения, например чтобы потом установить Zeph-Iris снова.

ukrainian.WillClose=Zeph-Iris запущено. Перед встановленням програма встановлення закриє його. Доки Zeph-Iris знову не запуститься, вентилятори працюють за налаштуваннями BIOS.
ukrainian.WillMigrate=Налаштування й записи попередньої версії цього застосунку (Fanlume або ProFanController) буде перенесено під час запуску Zeph-Iris.
ukrainian.CloseFailed=Не вдалося закрити Zeph-Iris. Клацніть правою кнопкою значок Zeph-Iris в області сповіщень, виберіть «Вихід (повернути керування BIOS)» і спробуйте ще раз.
ukrainian.UninstallCloseAsk=Zeph-Iris запущено. Закрити його та видалити?%nВентилятори повернуться до налаштувань BIOS.
ukrainian.DeleteDataAsk=Видалити також налаштування, журнали й записи Zeph-Iris?%n%nВиберіть «Ні», щоб залишити їх у папці застосунку, наприклад щоб пізніше встановити Zeph-Iris знову.

korean.WillClose=Zeph-Iris가 실행 중입니다. 설치 프로그램이 설치 전에 Zeph-Iris를 종료합니다. Zeph-Iris가 다시 시작될 때까지 팬은 BIOS 설정으로 돌아갑니다.
korean.WillMigrate=이 앱의 이전 버전(Fanlume 또는 ProFanController)의 설정과 기록은 Zeph-Iris가 시작될 때 이어받습니다.
korean.CloseFailed=Zeph-Iris를 종료하지 못했습니다. 작업 표시줄 알림 영역의 Zeph-Iris 아이콘을 마우스 오른쪽 버튼으로 클릭하고 "종료(BIOS 제어로 되돌림)"를 선택한 뒤 다시 시도하세요.
korean.UninstallCloseAsk=Zeph-Iris가 실행 중입니다. 종료하고 제거할까요?%n팬은 BIOS 설정으로 돌아갑니다.
korean.DeleteDataAsk=Zeph-Iris의 설정, 로그, 기록도 삭제할까요?%n%n"아니요"를 선택하면 앱 폴더에 남겨 둡니다(나중에 Zeph-Iris를 다시 설치할 때 등).

thai.WillClose=Zeph-Iris กำลังทำงานอยู่ ตัวติดตั้งจะปิด Zeph-Iris ก่อนติดตั้ง จนกว่า Zeph-Iris จะเริ่มทำงานอีกครั้ง พัดลมจะหมุนตามการตั้งค่า BIOS
thai.WillMigrate=การตั้งค่าและบันทึกของเวอร์ชันก่อนหน้าของแอปนี้ (Fanlume หรือ ProFanController) จะถูกย้ายมาใช้เมื่อ Zeph-Iris เริ่มทำงาน
thai.CloseFailed=ปิด Zeph-Iris ไม่ได้ คลิกขวาที่ไอคอน Zeph-Iris ในพื้นที่แจ้งเตือน เลือก "ออก (คืนให้ BIOS)" แล้วลองอีกครั้ง
thai.UninstallCloseAsk=Zeph-Iris กำลังทำงานอยู่ ต้องการปิดและถอนการติดตั้งหรือไม่?%nพัดลมจะกลับไปใช้การตั้งค่า BIOS
thai.DeleteDataAsk=ลบการตั้งค่า บันทึกการทำงาน และบันทึกข้อมูลของ Zeph-Iris ด้วยหรือไม่?%n%nเลือก "ไม่" เพื่อเก็บไว้ในโฟลเดอร์ของแอป เช่น เพื่อติดตั้ง Zeph-Iris ใหม่ภายหลัง

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; make-dist.ps1 がまとめた exe・説明書・バッチ・ライセンスを、そのままアプリのフォルダーへ
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; PawnIO の公式のインストーラー（make-dist.ps1 が公式のリリースから取ってきて、SHA-256 を確かめたもの）。
; アプリのフォルダーには入れず、PawnIO が入っていないときだけ一時フォルダーに出して実行する（InstallPawnIo）
#ifdef PawnIoSetup
Source: "{#PawnIoSetup}"; Flags: dontcopy
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Windows のアイコンのキャッシュを作り直す（exe を入れ替えても、ショートカット・タスクバーは古いアイコンを覚えたままのことがある）
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden waituntilterminated skipifdoesntexist runascurrentuser
; Zeph-Iris.exe は管理者権限が必要（app.manifest の requireAdministrator）。postinstall の項目は、何も付けないと
; ダブルクリックで起動した元の（管理者でない）ユーザーとして CreateProcess で起動され、エラー 740 で起動できない。
; そのため runascurrentuser で、管理者になったセットアップと同じ権限で起動する（UAC の確認はもう出ない）
; ふつうに入れたとき: 最後の画面で「Zeph-Iris を起動する」（はじめての起動では、アプリのセットアップの画面が出る）
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser
; 画面を出さずに更新したとき（アプリの「更新する」から）: 動いていたなら起動し直す。
; アプリが画面を出していたとき（/ZEPHIRISSHOW）は画面を出し、そうでなければタスクトレイで
Filename: "{app}\{#AppExe}"; Parameters: "{code:RestartArguments}"; Flags: nowait skipifnotsilent runascurrentuser; Check: AppWasRunning

[Code]
const
  EVENT_MODIFY_STATE = $0002;
  AppMutexes = 'Global\Zeph-Iris.SingleInstance,Global\Fanlume.SingleInstance,Global\ProFanController.SingleInstance';
  ExitSignals = 'Global\Zeph-Iris.Exit,Global\Fanlume.Exit,Global\ProFanController.Exit';
  LegacyConfig = 'C:\Tools\ProFanController\config.json';
  FanlumeConfig = '{commonpf64}\Fanlume\config.json';
  DataSwitch = '/ZEPHIRISDATA=';

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

{ コマンドラインに Name（/ZEPHIRISSHOW など）があるか }
function HasCommandLineSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

{ 更新のあとに起動し直すときの引数 }
function RestartArguments(Param: String): String;
begin
  if HasCommandLineSwitch('/ZEPHIRISSHOW') then
    Result := ''
  else
    Result := '--minimized';
end;

{ Zeph-Iris（または前の名前の ProFanController）が動いているか: 二重起動を防ぐ印で調べる }
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
  見張りプロセスが exe を開いたままだと入れ替えられないので、Zeph-Iris.exe のプロセスがなくなるまで待つ }
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

{ ----- PawnIO（ファン制御に使うドライバー） -----
  入っていなければ、このセットアップに入れてある PawnIO の公式のインストーラー（-install -silent）で入れる。
  それでも入らなければ、アプリと同じやり方（Core/PawnIoSetup.cs の InstallArguments）で winget から入れる。
  うまくいかなくても止めない（アプリのはじめの設定の画面が、もう一度入れるか、公式サイトを案内する） }

const
  PawnIoUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO';
  PawnIoWingetArguments = 'install --id namazso.PawnIO -e --source winget --silent --accept-source-agreements --accept-package-agreements';
  PawnIoSetupArguments = '-install -silent';

var
  PawnIoNeedsRestart: Boolean;

function PawnIoInstalled(): Boolean;
begin
  Result := RegKeyExists(HKLM64, PawnIoUninstallKey) or RegKeyExists(HKLM32, PawnIoUninstallKey);
end;

{ ----- 入れる・更新する ----- }

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo;
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
  if AppRunning() then
    Result := Result + NewLine + NewLine + CustomMessage('WillClose');
  if (FileExists(LegacyConfig) or FileExists(ExpandConstant(FanlumeConfig))) and not FileExists(ExpandConstant('{app}\config.json')) then
    Result := Result + NewLine + NewLine + CustomMessage('WillMigrate');
  if not PawnIoInstalled() then
    Result := Result + NewLine + NewLine + CustomMessage('WillInstallPawnIO');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  WasRunning := AppRunning();
  if not CloseApp() then
    Result := CustomMessage('CloseFailed');
end;

{ ----- PawnIO（ファン制御に使うドライバー）を入れる ----- }

{ winget.exe は %LOCALAPPDATA%\Microsoft\WindowsApps にある（なければ PATH から探してもらう） }
function WingetPath(): String;
begin
  Result := ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe');
  if not FileExists(Result) then
    Result := 'winget.exe';
end;

procedure InstallPawnIo();
var
  ResultCode: Integer;
begin
  if PawnIoInstalled() then
  begin
    Log('PawnIO is already installed');
    Exit;
  end;
  WizardForm.StatusLabel.Caption := CustomMessage('InstallingPawnIO');
  WizardForm.ProgressGauge.Style := npbstMarquee;
  try
#ifdef PawnIoSetup
    // 1. このセットアップに入れてある公式のインストーラー（ネットがなくても入る）
    ExtractTemporaryFile('PawnIO_setup.exe');
    if Exec(ExpandConstant('{tmp}\PawnIO_setup.exe'), PawnIoSetupArguments, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      Log(Format('bundled PawnIO setup: exit code %d', [ResultCode]));
      // 3010 = 入ったが、使うには Windows の再起動が要る
      if ResultCode = 3010 then
        PawnIoNeedsRestart := True;
    end
    else
      Log(Format('bundled PawnIO setup could not be started (error %d)', [ResultCode]));
#endif
    // 2. それでも入っていなければ winget（以前の版と同じ）
    if not PawnIoInstalled() then
    begin
      if Exec(WingetPath(), PawnIoWingetArguments, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        Log(Format('winget install PawnIO: exit code %d', [ResultCode]))
      else
        Log(Format('winget could not be started (error %d)', [ResultCode]));
    end;
  finally
    WizardForm.ProgressGauge.Style := npbstNormal;
  end;
  if PawnIoInstalled() then
    Log('PawnIO is installed now')
  else
    Log('PawnIO is still missing; the app setup offers it again');
end;

{ PawnIO が「再起動が要る」と返したときは、最後に再起動をすすめる（アプリの「更新する」からの /NORESTART では聞かない） }
function NeedRestart(): Boolean;
begin
  Result := PawnIoNeedsRestart;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallPawnIo();
end;

{ ----- アンインストール ----- }

var
  DeleteData: Boolean;

{ アプリの「お片付け」から渡された答え（/ZEPHIRISDATA=delete か keep。なければ空） }
function DataChoiceFromCommandLine(): String;
var
  I, N: Integer;
  S: String;
begin
  Result := '';
  N := Length(DataSwitch);
  for I := 1 to ParamCount do
  begin
    S := ParamStr(I);
    if CompareText(Copy(S, 1, N), DataSwitch) = 0 then
      Result := Lowercase(Copy(S, N + 1, Length(S) - N));
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
