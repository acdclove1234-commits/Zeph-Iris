Zeph-Iris (fan control tool, called ProFanController up to 6.6.1 and Fanlume from 6.7.0 to 6.7.3)
===================================

Zeph-Iris controls the motherboard's fan headers from temperatures.
It also monitors temperatures and load, runs cooling tests, lights LEDs with OpenRGB,
and can be checked from a phone. The name comes from Zephyr, the west wind (the fans),
and Iris, the rainbow (the LEDs).

* The user interface is available in 18 languages, including English. It follows the Windows
  display language; to choose another one, go to Settings [設定] -> Appearance [見た目] ->
  Language [表示言語] (it switches right away, without restarting the app).
  Below, screen and button names are given in English with the Japanese label in brackets,
  for example: Settings [設定].


■ What's new
  - Changes in each version are in CHANGELOG_en.txt in the same folder (newest first).


■ Requirements
  - A Windows 10 / 11 (64-bit) desktop PC
  - Fan control works with most ASRock / ASUS / MSI / Gigabyte motherboards (laptops are not supported)
  - No .NET installation needed (it is included in the exe)

  Verified environment (the PC used for development; an example that was checked, not a support list):
    Windows 11 Pro, ASRock B860 Steel Legend WiFi (Nuvoton NCT6686D), Intel Core Ultra 5 245KF,
    PNY GeForce RTX 5070 Ti 16GB ARGB Epic-X OC
  Other parts work if LibreHardwareMonitor (fans, temperatures) and OpenRGB (LEDs) support them,
  but behaviour can differ by model and sensor layout. Direct LED writes to graphics cards are used for
  the model above only; other cards are lit through OpenRGB.


■ First-time setup (once)
  1. Double-click Zeph-Iris-<version>-setup.exe (the installer) -> "Yes" (administrator confirmation).
     - If the browser warns about the download, choose to keep the file. You can compare the file with the
       SHA-256 shown on the release page.
     - If "Windows protected your PC" appears, press "More info" -> "Run anyway"
       (shown because this is a personal app without a code-signing certificate).
     - "Next" -> "Install" installs it to C:\Program Files\Zeph-Iris (with a Start menu entry).
     - Setup also installs PawnIO, the signed driver used to read sensors and set fan speeds
       (included in the installer, no internet needed).
     - Leave "Launch Zeph-Iris" ticked on the last page and press "Finish" to start it
       (you can delete the installer afterwards).
     - If setup asks you to restart Windows, restart, then start Zeph-Iris from the Start menu.
     - If antivirus software blocks Zeph-Iris or PawnIO, report it to the antivirus maker as a false
       positive instead of turning protection off.

  2. The setup wizard opens by itself. Its first page checks the driver (PawnIO).
     - Only if PawnIO is still missing, the wizard installs it again with winget (comes with Windows;
       needs the internet and takes up to a few minutes), and the app restarts when it is done.
     - If it fails, right-click "1_PawnIO をインストール.bat" (= install PawnIO) in the app folder
       (C:\Program Files\Zeph-Iris) -> "Run as administrator" (also uses winget), or get the installer
       from https://pawnio.eu/
     - If no fan headers are found (laptops, motherboards not supported yet, or other fan software
       holding them), the wizard says so; you can still finish and use temperature monitoring.

  3. Follow the wizard.
     - "Test" [テスト] on each fan header runs it at full speed for 8 seconds.
       Watch which fan speeds up and give it a name and a type (fan / pump / not used).
     - On the last page, leave "Start with Windows (recommended)" on. If it is off, the fans go back to
       BIOS control after a restart until you open the app.
     - At the end, three profiles are made for you: Silent / Standard / Performance.

  4. (Recommended) Run "Auto setup" [自動セットアップ] once (about 20-30 minutes). It measures your PC and
     makes the quietest curves that still keep it cool; you check the result before it is applied.


■ Everyday use
  - Closing the window with x keeps the app running in the tray (the blue-purple fan icon, bottom right).
  - To quit completely: right-click the tray icon -> "Exit" [終了].
    (On exit, the fans go back to the BIOS settings automatically.)
  - To start with Windows: Settings [設定] -> "Start automatically at Windows logon" [Windows ログオン時に自動で起動する].
  - Full speed while gaming: Ctrl + Alt + B (5 minutes); cancel with Ctrl + Alt + N.


■ Safety
  - If a temperature sensor reports bad values, or a temperature passes its limit, the fans go to full speed.
  - One-off spikes (read errors) are ignored, and sensors that freeze are treated as faulty.
  - When the app exits or stops, the fans go back to the BIOS fan settings.
  - If the app is killed or freezes, the watchdog process sets the fans to a safe speed (70%) and restarts
    the app. That is why Task Manager shows two Zeph-Iris processes (one is the watchdog).
  - Before sleep the BIOS takes over; control resumes after waking up.
  - When a part such as the GPU is replaced and its sensors change, the app offers to map the old sensors to the
    new ones at start (until then, curves that use a missing sensor run at a safe 60%).
  - An AIO pump never goes below 40%; if its speed drops, it goes to full speed and you are notified.
  - Settings (config.json) are backed up to the backups folder on every change and restored
    automatically at start if they are damaged.
  - If something seems wrong, press "Pause (back to BIOS)" [一時停止（BIOS に戻す）] at the bottom left
    to hand the fans back to the BIOS at once.


■ Updating to a new version
  The easy way (from 6.7.1 on):
    Settings [設定] -> General [一般] -> Updates [アップデート] -> "Check for updates" -> "Update".
    When a new version is out, the notice bar also shows an "Update" button.
    The installer is checked, the app is replaced without any screens, and it starts again after 10-20 seconds.

  By hand:
  1. Download the newer Zeph-Iris-<version>-setup.exe from the GitHub releases page:
     https://github.com/acdclove1234-commits/Zeph-Iris/releases
  2. Double-click it -> "Yes" -> "Next" -> "Install".
     - The running Zeph-Iris is closed properly first (fans back to BIOS), then the files are replaced.
       Choose "Launch Zeph-Iris" on the last page to start it again right away.
     - Settings (config.json), logs and records (data / records / backups folders) are kept.
     - If Zeph-Iris cannot be closed, the installer says so and stops. Right-click the tray icon ->
       "Exit (hand back to BIOS)", then run the installer again.


  If the new version does not start properly (from 6.15.0 on):
    Before updating, the current exe and settings are kept in C:\Program Files\Zeph-Iris\previous.
    If the new version has not started controlling the fans after 8 minutes, the previous version and
    settings are put back and started automatically.
    To go back by hand, double-click C:\Program Files\Zeph-Iris\previous\ZephIrisRecovery.exe -> "Yes".
    (This works even when the app does not start, and without an internet connection.)


■ Uninstalling
  Use either of these:
  A. Windows Settings -> Apps -> Installed apps -> "..." next to Zeph-Iris -> Uninstall.
     - If Zeph-Iris is running, it is closed properly (fans back to BIOS control); the start-at-logon entry,
       the firewall rule for the phone view and so on are removed, and then the app is deleted.
     - It asks whether to delete the settings, logs and records too ("No" keeps them in the app folder).
  B. Settings [設定] -> "Safety & backup" [安全・バックアップ] -> "Uninstall (clean up)" [アンインストール（お片付け）].
     - The steps are listed first. Check them, then press the button to clean up and exit:
       - It checks that the fans are back under BIOS control (if not, it stops and changes nothing).
       - It stops the lighting control and removes the start-at-logon entry, the firewall rule for the
         phone view, and so on.
       - Only if you choose: delete settings, logs and records / uninstall PawnIO
         (both are kept otherwise; other programs such as FanControl may use PawnIO).
     - When you close it, the app exits and the uninstaller then removes the app (it also leaves
       Installed apps).
  * "Exit" from the tray alone leaves the start-at-logon entry in place.
  * Only B can uninstall PawnIO.


■ Troubleshooting
  - "PawnIO (the fan control driver) was not found"
    -> Settings [設定] -> "Safety & backup" [安全・バックアップ] -> "Fan control driver (PawnIO)" ->
       "Install PawnIO", then "Restart the app"
       (if that fails, run "1_PawnIO をインストール.bat" as administrator and restart the PC)
  - The fan list is empty -> the motherboard may not be supported.
    (The app then only monitors temperatures and load and writes nothing to the fans; the BIOS stays in control.)
  - With ASRock A-Tuning / ASUS Armoury Crate / MSI Center or another fan control program installed,
    the two fight and the speeds are unstable. Turn off its fan control or uninstall it.
  - Logs: the logs folder in the app folder (C:\Program Files\Zeph-Iris).
  - Settings got messed up -> Settings -> "Safety & backup" -> "Restore from backup" [バックアップから戻す].
  - To report a problem, include your motherboard, CPU and graphics card models and the logs folder.


■ Files created in the app folder (C:\Program Files\Zeph-Iris)
  config.json ... settings        backups ... automatic setting backups
  logs        ... logs            data    ... per-minute records and energy (up to 180 days)
  previous    ... the version before the last update, to go back (ZephIrisRecovery.exe)
  records     ... records around incidents (CSV)    reports ... AI advisor answers
  images      ... images you replaced on the Devices page


* This is a personal project. Use it at your own risk.
