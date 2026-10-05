@echo off
setlocal
set "compiler=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%compiler%" (
 echo .NET Framework compiler not found. See README.md for requirements.
 exit /b 1
)
"%compiler%" /nologo /target:winexe /optimize+ /codepage:65001 /out:"%~dp0StreamSwitch.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "%~dp0StreamSwitch.cs" "%~dp0SelfTests.cs" "%~dp0AppButton.cs" "%~dp0ImageAssets.cs" "%~dp0ModernUi.cs" /win32icon:"%~dp0StreamSwitch.ico"
if errorlevel 1 exit /b 1
echo StreamSwitch.exe built successfully.
exit /b 0
