# ==================================================
# make-dist.ps1 - 配布用のインストーラー（Zeph-Iris-<版>-setup.exe）を作る
#   名前は英数字だけにする（GitHub のリリースは、ファイル名の日本語を「.」に置き換えるため）
#   1. .NET ランタイムごと exe 1 つに発行する（Portable の発行プロファイル。.NET が入っていない PC でも動く）
#   2. この Distribution フォルダーの説明書・バッチ・ライセンスと一緒に、1 つのフォルダーへまとめる
#   3. Inno Setup（ISCC.exe）で Installer\Zeph-Iris.iss から setup.exe を作り、リポジトリの一番上に置く（設定・ログ・記録は入れない）
# 使い方（リポジトリの一番上で）: powershell -ExecutionPolicy Bypass -File Distribution\make-dist.ps1
#   Inno Setup 6 が必要（winget install JRSoftware.InnoSetup）。別の場所に入れたときは、環境変数 ISCC に ISCC.exe の場所を入れる
#   署名つきで作るとき（.github/workflows/release.yml）: -PublishOnly <フォルダー> で exe だけを発行し、
#     署名済みの exe を -Exe <exe> で渡して setup.exe を作る（-OutputDir で置き場所を変えられる）
# ==================================================
param([string]$PublishOnly, [string]$Exe, [string]$OutputDir)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'ProFanController.csproj'
$script = Join-Path $root 'Installer\Zeph-Iris.iss'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'ProFanController.csproj に <Version> がありません' }

if ($PublishOnly) {
    Write-Host "Zeph-Iris $version の exe だけを発行しています..." -ForegroundColor Cyan
    dotnet publish $project -p:PublishProfile=Portable "-p:PublishDir=$PublishOnly\" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish に失敗しました' }
    exit 0
}
if (-not $OutputDir) { $OutputDir = $root }

$iscc = @($env:ISCC,
          (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
          (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
          (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')) |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 の ISCC.exe が見つかりません（winget install JRSoftware.InnoSetup で入れてください）' }

$work = Join-Path ([IO.Path]::GetTempPath()) "Zeph-Iris_dist_$([Guid]::NewGuid().ToString('N'))"
$publish = Join-Path $work 'publish'
$package = Join-Path $work 'Zeph-Iris'
try {
    if ($Exe) {
        $publish = Split-Path (Resolve-Path $Exe) -Parent
    } else {
        Write-Host "Zeph-Iris $version を発行しています..." -ForegroundColor Cyan
        dotnet publish $project -p:PublishProfile=Portable "-p:PublishDir=$publish\" --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish に失敗しました' }
    }

    New-Item -ItemType Directory -Force $package | Out-Null
    Copy-Item (Join-Path $publish 'Zeph-Iris.exe') $package
    # このスクリプト自身は入れない
    Get-ChildItem $PSScriptRoot | Where-Object { $_.Name -ne 'make-dist.ps1' } | Copy-Item -Destination $package -Recurse

    # ゲームの記録に使う Intel の PresentMon（MIT License・Intel の署名つき）を、公式のリリースから取ってくる。
    # 版と SHA-256 を決めておき、違えば止める。アプリのフォルダーの PresentMon\PresentMon.exe に置く（ライセンスは licenses\PresentMon-LICENSE.txt）
    $pmVersion = '2.6.0'
    $pmSha256 = 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF'
    $pmCache = Join-Path $root "obj\PresentMon\$pmVersion\PresentMon.exe"
    if (-not (Test-Path $pmCache) -or (Get-FileHash $pmCache -Algorithm SHA256).Hash -ne $pmSha256) {
        Write-Host "PresentMon $pmVersion を公式のリリースから取ってきています..." -ForegroundColor Cyan
        New-Item -ItemType Directory -Force (Split-Path $pmCache -Parent) | Out-Null
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/GameTechDev/PresentMon/releases/download/v$pmVersion/PresentMon-$pmVersion-x64.exe" -OutFile $pmCache
        $got = (Get-FileHash $pmCache -Algorithm SHA256).Hash
        if ($got -ne $pmSha256) {
            Remove-Item $pmCache -Force
            throw "PresentMon の SHA-256 が違います（$got）。公式のリリースが変わっていないか確かめてください"
        }
    }
    New-Item -ItemType Directory -Force (Join-Path $package 'PresentMon') | Out-Null
    Copy-Item $pmCache (Join-Path $package 'PresentMon\PresentMon.exe')

    # PawnIO の公式のインストーラーを、公式のリリースから取ってくる（版と SHA-256 を決めておき、違えば止める）。
    # setup.exe に入れるだけで、アプリのフォルダーには置かない。取ってきたものは obj\PawnIO\<版> に残し、次からはそれを使う
    $pawnVersion = '2.2.0'
    $pawnSha256 = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
    $pawnDir = Join-Path $root "obj\PawnIO\$pawnVersion"
    $pawnSetup = Join-Path $pawnDir 'PawnIO_setup.exe'
    if (-not (Test-Path $pawnSetup) -or (Get-FileHash $pawnSetup -Algorithm SHA256).Hash -ne $pawnSha256) {
        Write-Host "PawnIO $pawnVersion の公式のインストーラーを取ってきています..." -ForegroundColor Cyan
        New-Item -ItemType Directory -Force $pawnDir | Out-Null
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/namazso/PawnIO.Setup/releases/download/$pawnVersion/PawnIO_setup.exe" -OutFile $pawnSetup
        $got = (Get-FileHash $pawnSetup -Algorithm SHA256).Hash
        if ($got -ne $pawnSha256) {
            Remove-Item $pawnSetup -Force
            throw "PawnIO_setup.exe の SHA-256 が違います（$got）。公式のリリースが変わっていないか確かめてください"
        }
    }

    $setup = Join-Path $OutputDir "Zeph-Iris-$version-setup.exe"
    if (Test-Path $setup) { Remove-Item $setup }
    Write-Host "インストーラーを作っています（$iscc）..." -ForegroundColor Cyan
    & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$package" "/DOutputDir=$OutputDir" "/DPawnIoSetup=$pawnSetup" $script
    if ($LASTEXITCODE -ne 0) { throw "ISCC.exe に失敗しました（終了コード $LASTEXITCODE）" }
    if (-not (Test-Path $setup)) { throw "インストーラーができていません: $setup" }
    Write-Host "できました: $setup" -ForegroundColor Green
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
