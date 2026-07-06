<#
  Publishes Flow Terminal as a Windows x64 application (multi-file folder).
  Run on Windows with the .NET 8 SDK installed:
      pwsh build/publish.ps1                 # framework-dependent (needs .NET 8 Desktop Runtime)
      pwsh build/publish.ps1 -SelfContained  # self-contained (no runtime needed; larger)
  Output: artifacts/publish/  (zip the WHOLE folder to distribute)

  NOTE: WPF must NOT be published as a single file — single-file bundling breaks
  WPF's font cache (TypeInitializationException in MS.Internal.FontCache.MajorLanguages).
  This script intentionally produces a multi-file folder.
#>
param(
    [switch]$SelfContained,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root "artifacts/publish"

$common = @(
    "src/FlowTerminal.App/FlowTerminal.App.csproj",
    "-c", $Configuration,
    "-r", "win-x64",
    "-o", $out,
    "/p:PublishSingleFile=false",
    "/p:DebugType=None",
    "/p:DebugSymbols=false",
    "/p:Version=0.50.1"
)

if ($SelfContained) {
    dotnet publish @common --self-contained true
} else {
    dotnet publish @common --self-contained false
}

Write-Host "Published (multi-file) to $out" -ForegroundColor Green
Write-Host "Distribute by zipping the entire folder. Run: $out/FlowTerminal.exe" -ForegroundColor Green
