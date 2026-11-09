@echo off
chcp 65001 >nul
echo Installing PawnIO, the fan control driver...
echo PawnIO（ファン制御用ドライバ）をインストールします...
echo.
net session >nul 2>&1
if errorlevel 1 (
  echo Right-click this file and choose "Run as administrator".
  echo このファイルを右クリックして「管理者として実行」してください。
  pause
  exit /b 1
)
winget install --id namazso.PawnIO -e --accept-source-agreements --accept-package-agreements
if errorlevel 1 (
  echo.
  echo Installing with winget failed. The browser opens https://pawnio.eu/ - get the installer there and run it.
  echo winget でのインストールに失敗しました。
  echo ブラウザで https://pawnio.eu/ を開くので、インストーラーを入手して実行してください。
  start https://pawnio.eu/
) else (
  echo.
  echo Installed. Start Zeph-Iris.
  echo インストールが完了しました。Zeph-Iris を起動してください。
)
pause
