@echo off
setlocal
pushd "%~dp0"
set "FX=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FX%\csc.exe" set "FX=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319"
set "CSC=%FX%\csc.exe"
set "COMMON=/nologo /target:winexe /optimize+ /codepage:65001 /win32icon:GameDock-White.ico"
"%CSC%" %COMMON% /out:GameDock.exe /resource:GameDock-White.ico,GameDock.WhiteGlyph.ico /reference:"%FX%\Microsoft.CSharp.dll" /reference:"%FX%\System.Drawing.dll" /reference:"%FX%\System.Windows.Forms.dll" /reference:"%FX%\System.Xaml.dll" /reference:"%FX%\WPF\PresentationCore.dll" /reference:"%FX%\WPF\PresentationFramework.dll" /reference:"%FX%\WPF\WindowsBase.dll" /reference:"%FX%\System.Web.Extensions.dll" /reference:"%FX%\System.Xml.Linq.dll" GameArtwork.cs GameScan.cs GameDock.cs
if errorlevel 1 exit /b 1
"%CSC%" %COMMON% /out:"..\GameDock-Setup.exe" /resource:GameDock.exe,GameDock.exe /reference:"%FX%\Microsoft.CSharp.dll" /reference:"%FX%\System.Drawing.dll" /reference:"%FX%\System.Windows.Forms.dll" Distribution.cs
if errorlevel 1 exit /b 1
if not exist "..\Releases\" mkdir "..\Releases" || exit /b 1
powershell.exe -NoProfile -Command "$ErrorActionPreference='Stop'; $setup=Get-Item '..\GameDock-Setup.exe'; $version=([version]$setup.VersionInfo.FileVersion).ToString(3); Copy-Item -LiteralPath $setup.FullName -Destination ('..\Releases\GameDock-Setup-'+$version+'.exe') -Force; Copy-Item -LiteralPath $setup.FullName -Destination '..\Releases\GameDock-Setup.exe' -Force"
exit /b %errorlevel%
