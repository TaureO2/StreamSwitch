@echo off
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /out:"%~dp0StreamSwitch.Studio.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "%~dp0StreamSwitch.cs" "%~dp0SelfTests.cs" "%~dp0AppButton.cs" "%~dp0ImageAssets.cs" "%~dp0ModernUi.cs" /win32icon:"%~dp0StreamSwitch.ico"
exit /b %errorlevel%
