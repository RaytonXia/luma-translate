@echo off
setlocal
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "SPEECH=C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll"
set "OUT=%~dp0.."
set "DICT=%~dp0data\offline_ecdict_core.tsv.gz"
set "META=C:\Windows\System32\WinMetadata"
set "FX=C:\Windows\Microsoft.NET\Framework64\v4.0.30319"

if not exist "%CSC%" (
  echo .NET Framework C# compiler not found.
  exit /b 1
)
if not exist "%DICT%" (
  echo Offline dictionary resource not found: %DICT%
  exit /b 1
)
if not exist "%META%\Windows.Media.winmd" (
  echo Windows local OCR metadata was not found.
  exit /b 1
)

"%CSC%" /nologo /codepage:65001 /warn:4 /optimize+ /target:winexe /platform:x64 /win32manifest:"%~dp0app.manifest" /win32icon:"%OUT%\assets\luma-logo.ico" /resource:"%DICT%",SGFloatingTranslator.OfflineEcdict /resource:"%OUT%\assets\luma-logo.ico",SGFloatingTranslator.LumaLogo /out:"%OUT%\SGFloatingTranslator.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll /reference:"%SPEECH%" /reference:"%FX%\System.Runtime.dll" /reference:"%META%\Windows.Foundation.winmd" /reference:"%META%\Windows.Globalization.winmd" /reference:"%META%\Windows.Graphics.winmd" /reference:"%META%\Windows.Media.winmd" /reference:"%META%\Windows.Storage.winmd" "%~dp0Program.cs" "%~dp0MouseOcr.cs" "%~dp0ModernUi.cs" "%~dp0AiSettingsDialog.cs" "%~dp0DeepSeek.cs" "%~dp0LocalSpeech.cs"
if errorlevel 1 (
  echo.
  echo BUILD FAILED - please screenshot this window and open an issue on GitHub.
  pause
  exit /b 1
)

"%CSC%" /nologo /codepage:65001 /warn:4 /optimize+ /target:exe /platform:x64 /win32manifest:"%~dp0app.manifest" /resource:"%DICT%",SGFloatingTranslator.OfflineEcdict /resource:"%OUT%\assets\luma-logo.ico",SGFloatingTranslator.LumaLogo /main:SGFloatingTranslator.SelfTestProgram /out:"%OUT%\SGFloatingTranslator.Tests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll /reference:"%SPEECH%" /reference:"%FX%\System.Runtime.dll" /reference:"%META%\Windows.Foundation.winmd" /reference:"%META%\Windows.Globalization.winmd" /reference:"%META%\Windows.Graphics.winmd" /reference:"%META%\Windows.Media.winmd" /reference:"%META%\Windows.Storage.winmd" "%~dp0Program.cs" "%~dp0MouseOcr.cs" "%~dp0ModernUi.cs" "%~dp0AiSettingsDialog.cs" "%~dp0DeepSeek.cs" "%~dp0LocalSpeech.cs" "%~dp0SelfTest.cs"
if errorlevel 1 (
  echo.
  echo BUILD FAILED - please screenshot this window and open an issue on GitHub.
  pause
  exit /b 1
)

echo Build complete: %OUT%\SGFloatingTranslator.exe
start "" "%OUT%\SGFloatingTranslator.exe"
endlocal
