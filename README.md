# Fanlume

**English** | [日本語](README.ja.md)

> Up to version 6.6.1 this app was called **ProFanController**. Settings and records carry over to Fanlume (see [Moving from ProFanController](#moving-from-profancontroller)).

A fan control and monitoring tool for Windows. It drives the motherboard's fan headers from temperature curves and, together with OpenRGB, lights the PC's LEDs (temperature, rainbow, music sync and more).

> **The user interface is available in 18 languages**: English, Japanese, Chinese (Simplified and Traditional), Korean, Russian, Ukrainian, German, French, Spanish, Italian, Portuguese (Brazil), Polish, Dutch, Turkish, Vietnamese, Thai and Indonesian. It follows the Windows display language; to choose another one, go to Settings → Appearance → Language (it switches right away, without restarting the app). The original text is Japanese and the other languages are new translations, so some wording may read unnaturally; corrections are welcome (see [Translations](#translations)).

> This is a personal project. Wrong fan or pump control can let parts get too hot. Use it at your own risk (see the "no warranty" clause in the LICENSE).

## Features

- **Fan control**: temperature curves, target temperature, fixed duty, following another fan. Several sensors per curve, hysteresis, ramp-up/down rates, and resonance bands to skip
- **Auto-tune**: loads the PC, measures, and builds the quietest curve that still holds your target temperature
- **Safety**: emergency full speed (also watches the CPU and GPU temperatures when no curve uses them), filtering of bad sensor values and stuck sensors, pump speed watch, a watchdog process (sets safe fan speeds and restarts the app if it crashes), and handing the fans back to the BIOS on exit and sleep
- **Monitoring and diagnostics**: dashboard, history, electricity estimate, fan wear and dust checks that compare like with like (room temperature and fan-curve changes are taken into account), and a flight recorder for incidents
- **LEDs (with OpenRGB)**: temperature, rainbow, breathing, static colour and music sync (kick and snare detection, beat tracking, a dozen styles), per device and per zone. Music sync can listen to one app only (for example Spotify). PNY ARGB Epic-X graphics cards are written directly, without OpenRGB
- **Other**: automatic profile switching, a phone view on the same network, ntfy / Discord notifications, tray icon and start at logon, and a user interface in 18 languages
- **Optional**: an AI cooling advisor using a local [Ollama](https://ollama.com/) model (off by default; turn it on at the top of the Diagnostics page; your data stays on the PC)

## Requirements

- A Windows 10 / 11 (64-bit) desktop PC
- [PawnIO](https://pawnio.eu/) (the driver used for fan control; required by LibreHardwareMonitor 0.9.6 and later). The app can install it for you
- Administrator rights (needed for the Super I/O chip; Windows asks for confirmation at start)
- For LEDs: [OpenRGB](https://openrgb.org/) running with its SDK server turned on

Laptops and motherboards without fan control support are not supported.

## Verified environment (beta)

The app is developed and tested on this PC. This is an example that has been checked, not a list of supported hardware.

| Part | Model |
|---|---|
| OS | Windows 11 Pro (64-bit) |
| Motherboard | ASRock B860 Steel Legend WiFi (Super I/O: Nuvoton NCT6686D) |
| CPU | Intel Core Ultra 5 245KF |
| Graphics card | PNY GeForce RTX 5070 Ti 16GB ARGB Epic-X OC |

Other motherboards and graphics cards work as long as [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (fans and temperatures) and OpenRGB (LEDs) support them, but behaviour can differ with each model's specifics and sensor layout. If something does not work, please report it with your hardware models.

What happens with unsupported or unchecked parts:

- **No fan headers found**: the fan list is empty and the app only monitors temperatures and load. Nothing is written to the fans; the BIOS stays in control
- **PawnIO missing**: fan headers and some temperatures (such as the CPU) cannot be read. It can be installed from the setup screen
- **Writes do not take effect** (another program is using the chip, for example): every write is read back; if it does not stick, the app writes again and tells you
- **A fan stops, or a sensor fails or freezes**: a sensor with bad values sets its fans to full speed; a sensor that disappears sets a safe fixed speed. If the CPU or GPU reaches the emergency temperature, all fans go to full speed
- **Direct LED writes to graphics cards**: only for the model above. Other cards are lit through OpenRGB (you can try unchecked models under Lighting → Advanced)
- **Graphics card fan control**: off by default (only when you turn it on in Settings)

## Getting started (installer)

1. Download `Fanlume-<version>-setup.exe` from [Releases](../../releases) and run it. It installs to `C:\Program Files\Fanlume` and adds a Start menu entry. The installer is not code-signed, so Windows may show "Windows protected your PC": press "More info" → "Run anyway"
2. Leave "Launch Fanlume" ticked on the last page. If PawnIO is not installed, the first setup screen installs it (with winget) and restarts the app
   (if that fails, right-click `1_PawnIO をインストール.bat` ("install PawnIO") in the app folder → Run as administrator, or use the installer from the [official site](https://pawnio.eu/))
3. Follow the setup to name your fan headers

See `README_en.txt` in the app folder (in this repository under `Distribution/`) for details. To update, run the newer installer: it closes the running Fanlume properly (the fans go back to the BIOS settings), replaces the files and can start it again; settings and records are kept.

To stop using the app, uninstall it from Windows Settings → Apps → Installed apps (it closes Fanlume, removes the logon start and the firewall rule, and asks whether to delete settings and records), or use Settings → Safety & backup → Uninstall (clean up) in the app, which also offers to uninstall PawnIO and then runs the uninstaller.

### Moving from ProFanController

Run the Fanlume installer. It closes a running ProFanController properly first. When Fanlume starts for the first time, it copies the settings and records from `C:\Tools\ProFanController`, and moves the logon start and the firewall rule for the phone view to the new name. The old folder is left as it is; delete it once you have checked Fanlume works. Settings files exported by ProFanController (`*.profan.json`) can still be imported.

## Building from source

Requires the .NET 8 SDK. Building the installer also needs [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).

```powershell
# Build and run (Release)
dotnet build -c Release
# Logic tests
dotnet run -c Release --project Tests/LogicTests
# Installer (publishes one exe including the .NET runtime, adds the contents of Distribution, and builds Installer/Fanlume.iss)
powershell -ExecutionPolicy Bypass -File Distribution/make-dist.ps1
```

`Properties/PublishProfiles` has two publish profiles:

- `Portable`: one exe including the .NET runtime (for distribution; used by make-dist.ps1)
- `SingleExe`: a small exe for PCs that have the .NET 8 Desktop Runtime

The `config.json` at the top is a development sample (copied to the build output only, not into the release).

## Repository layout

| Path | Contents |
|---|---|
| `Core/` | fan control engine, sensors, settings, OpenRGB communication, LED rendering (`Core/Rgb`) |
| `Audio/`, `Core/Audio/` | music sync: audio capture and analysis (bands, kicks, tempo) |
| `UI/`, `MainWindow*` | user interface (WPF) |
| `Tests/LogicTests/` | logic tests (run without the UI or hardware) |
| `Distribution/` | guides (Japanese and English), a batch file and licences installed with the app, and the script that builds the installer |
| `Installer/` | the Inno Setup script for the installer (`Fanlume.iss`) |
| `Assets/Lang/` | translation tables for the user interface (one file per language) and the list of source texts (`keys.tsv`) |
| `tools/` | development tools (for example `FontSubset`, which trims the bundled font, and `LocTool`, which lists the texts to translate; not part of the app) |

## Translations

The texts on screen are written in Japanese in the source code and looked up in `Assets/Lang/<language>.txt` (for example `en.txt`, `de.txt`). Each line holds the hash of the Japanese text, a tab and the translation; a line break is written as `\n`, and placeholders such as `{0}` or `{1:F0}` must be kept as they are. `keys.tsv` lists every source text with its hash and where it is used.

- To fix a translation, edit the line in the language file. The logic tests (`Tests/LogicTests`, test `TestTranslationTables`) check that every line belongs to a current text and keeps its placeholders.
- After changing texts in the code, run `dotnet run --project tools/LocTool -- extract` to update `keys.tsv`, and `-- missing <language>` to list what still needs translating.
- Texts with no translation are shown in Japanese, so a missing line never breaks the app.

## License

Fanlume is under the [MIT License](LICENSE). Licences of the components it uses (LibreHardwareMonitorLib: MPL-2.0; NAudio, QRCoder, .NET: MIT; fonts: SIL Open Font License 1.1; and others) are listed in [CREDITS.txt](CREDITS.txt). The installer is built with Inno Setup. OpenRGB, PawnIO and Ollama are not included in this repository; install them separately.
