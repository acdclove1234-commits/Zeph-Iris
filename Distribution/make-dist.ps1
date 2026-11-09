# ==================================================
# make-dist.ps1 - 配布用のインストーラー（Fanlume-<版>-setup.exe）を作る
#   名前は英数字だけにする（GitHub のリリースは、ファイル名の日本語を「.」に置き換えるため）
#   1. .NET ランタイムごと exe 1 つに発行する（Portable の発行プロファイル。.NET が入っていない PC でも動く）
#   2. この Distribution フォルダーの説明書・バッチ・ライセンスと一緒に、1 つのフォルダーへまとめる
#   3. Inno Setup（ISCC.exe）で Installer\Fanlume.iss から setup.exe を作り、リポジトリの一番上に置く（設定・ログ・記録は入れない）
# 使い方（リポジトリの一番上で）: powershell -ExecutionPolicy Bypass -File Distribution\make-dist.ps1
#   Inno Setup 6 が必要（winget install JRSoftware.InnoSetup）。別の場所に入れたときは、環境変数 ISCC に ISCC.exe の場所を入れる
# ==================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'ProFanController.csproj'
$script = Join-Path $root 'Installer\Fanlume.iss'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'ProFanController.csproj に <Version> がありません' }

$iscc = @($env:ISCC,
          (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
          (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
          (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')) |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 の ISCC.exe が見つかりません（winget install JRSoftware.InnoSetup で入れてください）' }

$work = Join-Path ([IO.Path]::GetTempPath()) "Fanlume_dist_$([Guid]::NewGuid().ToString('N'))"
$publish = Join-Path $work 'publish'
$package = Join-Path $work 'Fanlume'
try {
    Write-Host "Fanlume $version を発行しています..." -ForegroundColor Cyan
    dotnet publish $project -p:PublishProfile=Portable "-p:PublishDir=$publish\" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish に失敗しました' }

    New-Item -ItemType Directory -Force $package | Out-Null
    Copy-Item (Join-Path $publish 'Fanlume.exe') $package
    # このスクリプト自身は入れない
    Get-ChildItem $PSScriptRoot | Where-Object { $_.Name -ne 'make-dist.ps1' } | Copy-Item -Destination $package -Recurse

    $setup = Join-Path $root "Fanlume-$version-setup.exe"
    if (Test-Path $setup) { Remove-Item $setup }
    Write-Host "インストーラーを作っています（$iscc）..." -ForegroundColor Cyan
    & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$package" "/DOutputDir=$root" $script
    if ($LASTEXITCODE -ne 0) { throw "ISCC.exe に失敗しました（終了コード $LASTEXITCODE）" }
    if (-not (Test-Path $setup)) { throw "インストーラーができていません: $setup" }
    Write-Host "できました: $setup" -ForegroundColor Green
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
