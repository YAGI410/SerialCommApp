# SerialCom をビルド・配置して起動する（Release / x64）
#   使い方: powershell -ExecutionPolicy Bypass -File .\run.ps1
#   -NoBuild を付けるとビルドせずに起動だけ行う
#
# Debug 構成（CoreCLR）はデバッガーなしで起動すると即終了するため、Release（.NET Native）で配置する
param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$packageName = '37027319-22fe-44ad-b346-c4c10305a8c3'
$layout = "$root\SerialCom\bin\x64\Release\AppX"

if (-not $NoBuild) {
    Get-Process SerialCom -ErrorAction SilentlyContinue | Stop-Process -Force

    # 別の場所から登録済みだと devenv が確認ダイアログを出して配置が取り消されるので、先に外す
    $installed = Get-AppxPackage -Name $packageName
    if ($installed -and $installed.InstallLocation -ne $layout) {
        $installed | Remove-AppxPackage
    }

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vs = & $vswhere -latest -property installationPath
    & "$vs\Common7\IDE\devenv.com" "$root\SerialCom.sln" -deploy 'Release|x64' | Select-String '展開:|Deploy:|error'
    if ($LASTEXITCODE -ne 0) { throw '配置に失敗しました' }
}

$pfn = (Get-AppxPackage -Name $packageName).PackageFamilyName
if (-not $pfn) { throw 'アプリが登録されていません。-NoBuild なしで実行してください' }
Start-Process "shell:AppsFolder\$pfn!App"
