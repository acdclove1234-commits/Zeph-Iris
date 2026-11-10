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


■ What's new in v6.10.3
  - Music sync: "Sparkle" now brightens on the beat predicted from the tempo (it used to wait for a detected kick,
    so it lagged a little and missed beats where no kick was found), with a stronger beat pulse.
  - Hi-hat sparkles, toned down too far in 6.9.1, are partly back (moderate: 70% brightness, at least 0.12 s apart).
  - Installer: the official installer of PawnIO, the fan control driver, is now included. On PCs without PawnIO it
    is installed without the internet (winget remains the fallback). PawnIO's licence (GPL) and the location of its
    source code are in the licenses folder.
  - Monitor: the "Memory (RAM)" tile showed memory including virtual memory (the page file), e.g. "43.6 / 53 GB"
    on a 32 GB PC. It now shows physical memory only.

■ What's new in v6.10.2
  - Lighting: removed the faint flicker left in Lightning, Waves and the other new effects. Dim LEDs no longer
    alternate between neighbouring values; each keeps the value it last sent until the brightness clearly changes.
  - Report a problem: Settings [設定] -> General -> "Report a problem…" makes a zip on the desktop with the logs,
    settings and hardware list (user name, PC name, folder names, email and IP addresses, and the phone
    notification address and passcode are hidden) and opens the GitHub report form with the version and hardware
    filled in. The app sends nothing by itself.

■ What's new in v6.10.1
  - Lighting: removed the flicker from Lightning, Waves, Birdsong, Fire, Aurora, Meteor and Heartbeat. Dark areas
    are now smoothed like the other effects (except near black) instead of jumping in steps, and Heartbeat and
    Aurora no longer blink off and on at low brightness. Fire flickers a little more slowly.

■ What's new in v6.10.0
  - Lighting: seven new effects: Lightning, Waves, Birdsong, Fire, Aurora, Meteor and Heartbeat. All follow the
    speed setting; Waves and Heartbeat use the chosen colour. They can also be chosen per device and per zone (for
    example, only the keyboard as Meteor). Lightning and meteors strike on every device at the same moment.
    Graphics cards show a still picture, as with the rainbow (turn on "Animate rainbow and breathing too" to let
    them move).
  - Safety: re-asserting full speed wrote a value just over 100% (100.001%); it now stays within 100% (fan speed
    was not affected).
  - Quality: tests now run automatically on GitHub (build, over 2,000 tests, the translation check and a check
    that opens every page), plus a six-hour fan control run and a long run of every effect.

■ What's new in v6.9.1
  - Music sync: "Sparkle" no longer over-reacts to hi-hats. The brightness, number and spacing of hi-hat sparkles
    follow the intensity (moderate: 55% brightness, at least 0.16 s apart; calmer still with "Reduce flashing").
    The treble parts of "Parts play their roles" no longer flash on every closely spaced hi-hat either.
  - Lighting: a "Fans" setting per zone splits fans daisy-chained on one header (channel) so each fan can have
    its own colour and light mode (Lian Li UNI HUB channels, fans on a splitter cable).
  - The lighting page explains why fans on a Lian Li UNI HUB in MB Sync cannot have separate colours, and how to
    let OpenRGB drive the hub directly.

■ What's new in v6.9.0
  - Lighting: each device and each zone (motherboard header) can have its own colour and light mode (solid,
    rainbow, breathing, temperature, off). Example: graphics card solid red, motherboard white, only the radiator
    fans in rainbow, everything else in music sync. Lighting [ライティング] -> Devices: "This device's colour"; zones
    under "Open color matching and per-zone settings".
  - Fixed: the sync test's LEDs could stay dark when the audio source was set to one app (such as Spotify),
    because the app's own click was not captured. During the test the whole output is captured.
  - Devices page: devices without a photo are looked up by model on Wikimedia Commons (freely licensed photos),
    and only photos that clearly show the same product are used automatically, with the author and licence shown.
    Only the device names are sent; it can be turned off at the top of the page.
  - The image button can now find a photo online or paste a copied image: right-click an image in your browser ->
    "Copy image", then paste.
  - Illustrations of devices without a photo show the maker's colour and name.

■ What's new in v6.8.2
  - Electricity cost: the power supply's losses are now included (Settings: "Power supply efficiency", 88% by
    default). Before, they were left out, so the estimate read 10-20% below a wall-outlet measurement; with the
    same settings it now reads a little over 10% higher than before.
  - Settings [設定] -> Notifications & integrations -> Electricity cost: calibrate with a plug-in power meter. Enter
    the reading at idle and press "Record" to work out the power of everything besides the CPU and GPU; record
    under load too (during a game, for example) to work out the power supply efficiency. "Clear calibration"
    goes back to the starting estimate.
  - The report page says "an estimate within about ±20%" before calibration and shows the calibration date after.

■ What's new in v6.8.1
  - Safety: with a fan curve on an SSD temperature, playing music or copying files could make the app report
    "the temperature sensor is stuck" and run every fan at 100%. The "load changed but the temperature did not"
    check now only uses CPU and GPU load, where load moves the temperature within minutes (an SSD's load is
    read/write activity, and its temperature moves in 1 °C steps).
  - Lighting: the live spectrum shows 7 gridlines instead of 3 (display only; audio analysis is unchanged).

■ What's new in v6.8.0
  - New name: Fanlume is now called Zeph-Iris (another service already uses the name Fanlume).
  - It installs to C:\Program Files\Zeph-Iris. When Zeph-Iris starts for the first time, it takes the settings and
    records of Fanlume (or ProFanController), moves the start-at-logon entry and the firewall rule for the phone
    view to the new name, and then uninstalls Fanlume while keeping its settings and records (so the old name
    leaves Installed apps).
  - The "Update" button in Fanlume 6.7.1-6.7.3 does not find the new name, so run the 6.8.0 installer yourself
    once. From 6.8.0 on, "Update" works again.
  - Exported settings files are now *.zeph-iris.json (older *.fanlume.json and *.profan.json files still load).
  - Thanks: the About section in Settings and the licenses folder now thank the open-source projects Zeph-Iris
    is built on.

■ What's new in v6.7.3
  - Installer: if PawnIO (the fan control driver) is missing, the installer installs it from the official winget
    source (needs the internet; if that fails, the app's setup can still install it as before).
  - Updating from inside the app: the installer left in the temp folder by each update (about 74 MB) is deleted
    on the next start. For 14 days after an update, Settings [設定] -> General [一般] -> Updates [アップデート] ->
    "Go back to the previous version" fetches the previous installer from GitHub, checks it the same way and
    goes back. If a just-updated version closes unexpectedly, the notice bar offers this too.
  - Diagnostics (cooling): the "Room temperature sensor" can be chosen (automatic, none, or any temperature
    sensor). If the motherboard's "System" sensor measures the board rather than the room, choose none or a
    sensor closer to the room for a better check on days without idle time.
  - The "Open the releases page" button now looks like the other buttons.

■ What's new in v6.7.2
  - Installer: with "Launch Zeph-Iris" ticked on the last page, the installer could show "CreateProcess error:
    code 740" and Zeph-Iris did not start (when the installer was started by double-clicking). It starts now.

■ What's new in v6.7.1
  - Updating from inside the app: Settings [設定] -> General [一般] -> Updates [アップデート] now has "Check for
    updates" and "Update". "Update" downloads the new installer, checks that it matches the file published on
    GitHub (size, SHA-256 and version), and replaces the app without showing any screens, then starts it again
    (the fans run on the BIOS settings for about 10-20 seconds).
  - With "Check for new versions automatically" on, the app asks GitHub at most once a day and tells you when a
    new version is out (only the app's name and version are sent; you can turn it off).
  - This works from 6.7.1 on. To go from 6.7.0 to 6.7.1, run the installer yourself.
  - Diagnostics (CPU / GPU cooling): days without idle time (CPU power high all day) were compared with values
    worked out a different way, so it could say cooling got worse when it had not. Only like is compared now.
  - The Settings tabs stay at the top while you scroll. The sidebar and the mini widget show the name Zeph-Iris,
    and the history CSV and exported settings files are named Zeph-Iris too.

■ What's new in v6.7.0
  - New name: ProFanController is now called Fanlume (now Zeph-Iris). Settings and records are carried over.
  - Installer: Fanlume now comes as an installer (Fanlume-6.7.0-setup.exe). It installs to
    C:\Program Files\Fanlume, adds a Start menu entry, and can be removed from Installed apps in Windows.
    To update, run the newer installer: it closes the running Zeph-Iris properly first and can start it again
    afterwards; settings, logs and records stay. 2_更新.bat (update) is gone.
  - Moving from ProFanController: the installer closes a running ProFanController properly (the fans go back
    to the BIOS settings). When Zeph-Iris starts for the first time, it takes the settings and records from
    C:\Tools\ProFanController and moves the start-at-logon entry and the firewall rule for the phone view to
    the new name. You can delete C:\Tools\ProFanController afterwards.
  - Exported settings files are now *.fanlume.json (older *.profan.json files still load).

■ What's new in v6.6.1
  - First run: started from Downloads, the Desktop or inside the zip, the app now copies itself to
    C:\Tools\ProFanController and starts again from there (before, it only showed a "move it" notice
    and might not work properly). Settings used at the old place are carried over. Placed anywhere
    else, it runs in place as before. The setup wizard installs PawnIO by itself when it is missing
    (once; if that fails, the buttons and the official-site link are shown as before).
  - Languages: the chosen language now reaches every part of the screen (the sidebar, page headers
    and many buttons stayed Japanese before), and it switches right away, without restarting the app.
    Fan control, LEDs and music sync keep running.
  - Music sync: very quiet sounds (for example faint highs in a bass-heavy song) no longer flash as
    hi-hats or snares. A higher Beat sensitivity [反応の感度] brings the detail back.
  - Music sync: the spectrum and the "Spectrum" style use the 10 bands of a common graphic equalizer
    (31 Hz-16 kHz), with the frequency under each bar. The other styles flash at the same moments as
    before.
  - Safety: if the app ends unexpectedly and the watchdog fails to set the safe fan speed (for example
    when the exe was replaced while running), it still restarts the app. Before, the watchdog could stop
    too and leave the fans at their last speed.

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
  1. Double-click Zeph-Iris-<version>-setup.exe (the installer) -> "Yes" (administrator confirmation).
     - If "Windows protected your PC" appears, press "More info" -> "Run anyway"
       (shown because this is a personal app without a code-signing certificate).
     - "Next" -> "Install" installs it to C:\Program Files\Zeph-Iris (with a Start menu entry).
     - Leave "Launch Zeph-Iris" ticked on the last page and press "Finish" to start it
       (you can delete the installer afterwards).

  2. The first page of the setup wizard checks the fan control driver (PawnIO).
     - If PawnIO is missing, the installation starts by itself, and the app restarts when it is done
       (installed with winget, which comes with Windows; needs the internet and takes up to a few
       minutes; the wizard opens again after the restart).
     - If a PC restart is required, restart the PC and start Zeph-Iris from the Start menu.
     - If it fails, right-click "1_PawnIO をインストール.bat" (= install PawnIO) in the app folder
       (C:\Program Files\Zeph-Iris) -> "Run as administrator", or get the installer from https://pawnio.eu/

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
  records     ... records around incidents (CSV)    reports ... AI advisor answers
  images      ... images you replaced on the Devices page


* This is a personal project. Use it at your own risk.
