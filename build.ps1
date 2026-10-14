# Builds VideoSync.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK or Visual Studio required.
#   build.ps1            -> bin\VideoSync.exe
#   build.ps1 -Package   -> also dist\VideoSync-Share\ and dist\VideoSync-Share.zip (ready to copy to PCs)
param([switch]$Package)
$ErrorActionPreference = 'Stop'
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path "$fw\csc.exe")) { $fw = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319" }
$wpf = "$fw\WPF"
$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

& "$fw\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu `
    /out:"$out\VideoSync.exe" `
    /r:"$wpf\PresentationFramework.dll" /r:"$wpf\PresentationCore.dll" /r:"$wpf\WindowsBase.dll" `
    /r:"$fw\System.Xaml.dll" /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll `
    /resource:"$PSScriptRoot\src\web\client.html",client.html `
    (Get-ChildItem "$PSScriptRoot\src\*.cs").FullName
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $out\VideoSync.exe"

if ($Package) {
    $dist = Join-Path $PSScriptRoot 'dist'
    $share = Join-Path $dist 'VideoSync-Share'
    if (Test-Path $share) { Remove-Item $share -Recurse -Force }
    New-Item -ItemType Directory -Force $share | Out-Null
    Copy-Item "$out\VideoSync.exe", "$PSScriptRoot\share\*", "$PSScriptRoot\samples\sync-test.mp4" $share
    Compress-Archive -Path "$share\*" -DestinationPath "$dist\VideoSync-Share.zip" -Force
    Write-Host "Packaged $dist\VideoSync-Share.zip"
}
