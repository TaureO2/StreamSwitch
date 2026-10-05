$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$appRoot = $PSScriptRoot
& $compiler /nologo /target:winexe /optimize+ /codepage:65001 /out:"$appRoot\StreamSwitch.Studio.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "$appRoot\StreamSwitch.cs" "$appRoot\SelfTests.cs" "$appRoot\AppButton.cs" "$appRoot\ImageAssets.cs" "$appRoot\ModernUi.cs" /win32icon:"$appRoot\StreamSwitch.ico"
if ($LASTEXITCODE -ne 0) { throw 'La compilación ha fallado.' }
Write-Output 'StreamSwitch.Studio.exe creado.'
