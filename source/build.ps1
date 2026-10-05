param([switch]$Test, [switch]$VisualTest)
$ErrorActionPreference = 'Stop'
$sourceDirectory = $PSScriptRoot
$outputDirectory = Split-Path -Parent $sourceDirectory
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
$metadataDirectory = Join-Path $env:WINDIR 'System32\WinMetadata'
$speechAssembly = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll'
$dictionary = Join-Path $sourceDirectory 'data\offline_ecdict_core.tsv.gz'
foreach ($required in @($compiler, $speechAssembly, $dictionary, (Join-Path $metadataDirectory 'Windows.Media.winmd'))) {
    if (!(Test-Path -LiteralPath $required)) { throw "Required Windows component missing: $required" }
}
$arguments = @('/nologo', '/codepage:65001', '/warn:4', '/optimize+', '/platform:x64',
    "/win32manifest:$sourceDirectory\app.manifest", "/win32icon:$outputDirectory\assets\luma-logo.ico",
    "/resource:$dictionary,SGFloatingTranslator.OfflineEcdict",
    "/resource:$outputDirectory\assets\luma-logo.ico,SGFloatingTranslator.LumaLogo")
foreach ($assembly in @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
        'System.Net.Http.dll', 'System.Web.Extensions.dll', 'System.Security.dll', $speechAssembly,
        "$frameworkDirectory\System.Runtime.dll")) { $arguments += "/reference:$assembly" }
foreach ($metadata in @('Windows.Foundation', 'Windows.Globalization', 'Windows.Graphics', 'Windows.Media', 'Windows.Storage')) {
    $arguments += "/reference:$metadataDirectory\$metadata.winmd"
}
$sources = @('Program.cs', 'MouseOcr.cs', 'ModernUi.cs', 'AiSettingsDialog.cs', 'DeepSeek.cs', 'LocalSpeech.cs', 'DictionaryQuality.cs') |
    ForEach-Object { Join-Path $sourceDirectory $_ }
& $compiler @arguments /target:winexe "/out:$outputDirectory\LumaTranslate.exe" @sources
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
& $compiler @arguments /target:exe /main:SGFloatingTranslator.SelfTestProgram "/out:$outputDirectory\LumaTranslate.Tests.exe" @sources "$sourceDirectory\SelfTest.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
if ($Test) {
    & "$outputDirectory\LumaTranslate.Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Offline regression tests failed.' }
}
if ($VisualTest) {
    & $compiler @arguments /target:exe /main:SGFloatingTranslator.UiSmokeTest "/out:$outputDirectory\LumaTranslate.UiTests.exe" @sources "$sourceDirectory\UiSmokeTest.cs"
    if ($LASTEXITCODE -ne 0) { throw 'UI regression test build failed.' }
    & "$outputDirectory\LumaTranslate.UiTests.exe" (Join-Path $outputDirectory 'verification')
    if ($LASTEXITCODE -ne 0) { throw 'UI regression tests failed.' }
}
Write-Host "Built: $outputDirectory\LumaTranslate.exe"
