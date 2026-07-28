@echo off
setlocal
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "VB=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Microsoft.VisualBasic.dll"

if "%~2"=="" (
  echo Usage: build_dictionary.cmd ^<ecdict.csv^> ^<offline_ecdict_core.tsv.gz^>
  exit /b 2
)

"%CSC%" /nologo /codepage:65001 /warn:4 /optimize+ /target:exe /out:"%TEMP%\SGFT.BuildOfflineDictionary.exe" /reference:System.dll /reference:System.Core.dll /reference:"%VB%" "%~dp0BuildOfflineDictionary.cs"
if errorlevel 1 exit /b 1
"%TEMP%\SGFT.BuildOfflineDictionary.exe" "%~1" "%~2"
exit /b %ERRORLEVEL%
