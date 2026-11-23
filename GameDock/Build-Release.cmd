@echo off
setlocal
pushd "%~dp0"
set "FX=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FX%\csc.exe" set "FX=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319"
set "CSC=%FX%\csc.exe"
set "COMMON=/nologo /target:winexe /optimize+ /codepage:65001 /win32icon:GameDock-White.ico"
set "GAME_REFS=/reference:"%FX%\System.IO.Compression.dll" /reference:"%FX%\Microsoft.CSharp.dll" /reference:"%FX%\System.Drawing.dll" /reference:"%FX%\System.Windows.Forms.dll" /reference:"%FX%\System.Xaml.dll" /reference:"%FX%\WPF\PresentationCore.dll" /reference:"%FX%\WPF\PresentationFramework.dll" /reference:"%FX%\WPF\WindowsBase.dll" /reference:"%FX%\System.Web.Extensions.dll" /reference:"%FX%\System.Xml.Linq.dll""
set "CHECKS=%TEMP%\GameDock-checks-%RANDOM%-%RANDOM%.exe"
"%CSC%" /nologo /optimize+ /codepage:65001 /out:"%CHECKS%" %GAME_REFS% /resource:..\LICENSE,GameDock.LICENSE /resource:Minecraft.png,GameDock.Minecraft.png ProjectInfo.cs MinecraftLauncher.cs SettingsPages.cs /main:DiscoveryChecks GameDock.cs DockPins.cs GameScan.cs GameArtwork.cs DiscoveryChecks.cs
if errorlevel 1 exit /b 1
"%CHECKS%"
set "CHECK_RESULT=%ERRORLEVEL%"
if "%GITHUB_ACTIONS%"=="true" if "%CHECK_RESULT%"=="0" (
    "%CHECKS%" --online-icons
    if errorlevel 1 echo ::warning::Live icon lookup unavailable. See the publisher check above.
)
del /q "%CHECKS%"
if not "%CHECK_RESULT%"=="0" exit /b %CHECK_RESULT%
"%CSC%" %COMMON% /out:GameDock.exe /resource:GameDock-White.ico,GameDock.WhiteGlyph.ico %GAME_REFS% /resource:..\LICENSE,GameDock.LICENSE /resource:Minecraft.png,GameDock.Minecraft.png ProjectInfo.cs MinecraftLauncher.cs SettingsPages.cs GameArtwork.cs GameScan.cs DockPins.cs GameDock.cs
if errorlevel 1 exit /b 1
"%CSC%" %COMMON% /out:"..\GameDock-Setup.exe" /resource:GameDock.exe,GameDock.exe /reference:"%FX%\Microsoft.CSharp.dll" /reference:"%FX%\System.Drawing.dll" /reference:"%FX%\System.Windows.Forms.dll" Distribution.cs
if errorlevel 1 exit /b 1
if not exist "..\Releases\" mkdir "..\Releases" || exit /b 1
powershell.exe -NoProfile -Command "$ErrorActionPreference='Stop'; $setup=Get-Item '..\GameDock-Setup.exe'; $version=([version]$setup.VersionInfo.FileVersion).ToString(3); Copy-Item -LiteralPath $setup.FullName -Destination ('..\Releases\GameDock-Setup-'+$version+'.exe') -Force; Copy-Item -LiteralPath $setup.FullName -Destination '..\Releases\GameDock-Setup.exe' -Force"
exit /b %errorlevel%
