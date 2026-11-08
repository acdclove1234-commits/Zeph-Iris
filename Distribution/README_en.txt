ProFanController (fan control tool)
===================================

ProFanController controls the motherboard's fan headers from temperatures.
It also monitors temperatures and load, runs cooling tests, lights LEDs with OpenRGB,
and can be checked from a phone.

* The user interface is available in 18 languages, including English. It follows the Windows
  display language; to choose another one, go to Settings [設定] -> Appearance [見た目] ->
  Language [表示言語] (it switches right away, without restarting the app).
  Below, screen and button names are given in English with the Japanese label in brackets,
  for example: Settings [設定].


■ What's new in v6.6.0
  - Languages: the user interface is now available in 18 languages (English, Japanese, Chinese
    Simplified and Traditional, Korean, Russian, Ukrainian, German, French, Spanish, Italian,
    Portuguese (Brazil), Polish, Dutch, Turkish, Vietnamese, Thai, Indonesian). The phone view and
    the AI advisor's answers follow the same language. The translations are new, so some wording
    may read unnaturally. This English guide is included, and the updater and the PawnIO installer
    show English messages.
  - Lighting [ライティング]: Undo and Redo on the Lighting page (also Ctrl+Z / Ctrl+Y). "Reset to
    defaults" can be undone too; the integration switch, lights off and the connection are not.
  - Fans: the emergency full speed now always watches the CPU temperature and the GPU core and
    hot spot, even when no curve uses them (for example when the radiator fans follow the coolant only).
  - Music sync: can listen to one app only (for example Spotify), so other sounds and notification
    sounds do not trigger the lights. Better beat tracking (on 30 recorded songs the lights followed the
    beat 44% -> 66% of the time, and flashed on the wrong beat 6% -> 4% of the time), twice as many snares
    found, melodies and high notes glow in the "beat colour" style, new per-part and solid-colour styles,
    "lights off" that keeps the control running, and "reset to defaults" for the reaction values.
  - Music sync, timing:
    - The song's tempo is remembered, so the beat comes back quickly after a busy passage; a beat
      grid that stays shifted by the same amount is corrected.
    - Slow songs (about 80-90 BPM) flash at double speed less often (on 31 recorded songs, time
      at double speed 9.9% -> 4.5%, flashes between beats 13.0% -> 10.4%). In exchange, fast songs
      (about 150-160 BPM) sometimes flash on every other beat; those flashes stay on the beat.
    - Snares reach the lights 11 ms sooner.
    - With the Windows sound format at 96 kHz, 192 kHz and so on, beats and snares are found as
      well as at 44.1 / 48 kHz (snares were missed and some songs locked at double tempo).
    - The output device kind (onboard, USB, HDMI / DisplayPort, Bluetooth, virtual) is shown on
      the Lighting page. For Bluetooth, HDMI and virtual devices it suggests an Audio delay
      [音の遅れの補正] that you can apply with one button. When audio software such as Nahimic, Dolby,
      DTS, SteelSeries Sonar or Voicemeeter is running, it names it and points to the Sync test
      [同期テスト].
  - Diagnostics [診断]: the fan dust and wear check moved here and was made more accurate (room
    temperature and fan-curve changes no longer look like dust; sudden and gradual changes are told apart).
    The AI cooling advisor is now an option, off by default (switch at the top of Diagnostics).
  - PawnIO can be installed from inside the app; a clean uninstall was added; the updater checks the
    version and origin of the zip and rolls back on failure; the watchdog process and the bundled font
    are lighter.
  For older changes, see はじめにお読みください.txt (Japanese).


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
  1. Double-click ProFanController.exe -> "Yes" (administrator confirmation).
     - If "Windows protected your PC" appears, press "More info" -> "Run anyway"
       (shown because this is a personal app without a code-signing certificate).
     - When opened from Downloads, the Desktop or inside the zip, the app installs itself to
       C:\Tools\ProFanController and starts again from there (you can delete the downloaded files
       afterwards). Placed anywhere else (for example D:\Apps), it runs where it is.

  2. The first page of the setup wizard checks the fan control driver (PawnIO).
     - If PawnIO is missing, the installation starts by itself, and the app restarts when it is done
       (installed with winget, which comes with Windows; needs the internet and takes up to a few
       minutes; the wizard opens again after the restart).
     - If a PC restart is required, restart the PC and start ProFanController.exe.
     - If it fails, right-click "1_PawnIO をインストール.bat" (= install PawnIO) in this folder ->
       "Run as administrator", or get the installer from https://pawnio.eu/

  3. Follow the wizard.
     - "Test" [テスト] on each fan header runs it at full speed for 8 seconds.
       Watch which fan speeds up and give it a name and a type (fan / pump / not used).
     - At the end, three profiles are made for you: Silent / Standard / Performance.


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
    the app. That is why Task Manager shows two ProFanController processes (one is the watchdog).
  - Before sleep the BIOS takes over; control resumes after waking up.
  - When a part such as the GPU is replaced and its sensors change, the app offers to map the old sensors to the
    new ones at start (until then, curves that use a missing sensor run at a safe 60%).
  - An AIO pump never goes below 40%; if its speed drops, it goes to full speed and you are notified.
  - Settings (config.json) are backed up to the backups folder on every change and restored
    automatically at start if they are damaged.
  - If something seems wrong, press "Pause (back to BIOS)" [一時停止（BIOS に戻す）] at the bottom left
    to hand the fans back to the BIOS at once.


■ Updating to a new version
  1. Save the new zip, still zipped, to your Downloads folder.
  2. Double-click "2_更新.bat" (= update) in this folder -> "Yes".
     - The running app is closed (fans back to BIOS), the files are replaced, and the app starts again.
     - Settings (config.json), logs and records (data / records / backups folders) are kept.
     - Before replacing, it shows the version in the zip. With an internet connection it also checks that
       the zip is identical to the official GitHub release (if not, it asks whether to continue).
     - The previous files are kept in the backups folder. If replacing fails, or the new version exits right
       away, the previous version is restored automatically.


■ Uninstalling
  1. Settings [設定] -> "Safety & backup" [安全・バックアップ] -> "Uninstall (clean up)" [アンインストール（お片付け）].
     - The steps are listed first. Check them, then press the button to clean up and exit:
       - It checks that the fans are back under BIOS control (if not, it stops and changes nothing).
       - It stops the lighting control and removes the start-at-logon entry, the firewall rule for the
         phone view, and so on.
       - Only if you choose: delete settings, logs and records / uninstall PawnIO
         (both are kept otherwise; other programs such as FanControl may use PawnIO).
     - The app then exits (the watchdog exits too and does not restart it).
  2. Explorer opens this folder; delete the whole folder
     (the app cannot delete itself while running, so this last step is manual).
  * "Exit" from the tray alone leaves the start-at-logon entry in place.


■ Troubleshooting
  - "PawnIO (the fan control driver) was not found"
    -> Settings [設定] -> "Safety & backup" [安全・バックアップ] -> "Fan control driver (PawnIO)" ->
       "Install PawnIO", then "Restart the app"
       (if that fails, run "1_PawnIO をインストール.bat" as administrator and restart the PC)
  - The fan list is empty -> the motherboard may not be supported.
    (The app then only monitors temperatures and load and writes nothing to the fans; the BIOS stays in control.)
  - With ASRock A-Tuning / ASUS Armoury Crate / MSI Center or another fan control program installed,
    the two fight and the speeds are unstable. Turn off its fan control or uninstall it.
  - Logs: the logs folder in this folder.
  - Settings got messed up -> Settings -> "Safety & backup" -> "Restore from backup" [バックアップから戻す].
  - To report a problem, include your motherboard, CPU and graphics card models and the logs folder.


■ Files created in this folder
  config.json ... settings        backups ... automatic setting backups
  logs        ... logs            data    ... per-minute records and energy (up to 180 days)
  records     ... records around incidents (CSV)    reports ... AI advisor answers
  images      ... images you replaced on the Devices page


* This is a personal project. Use it at your own risk.
