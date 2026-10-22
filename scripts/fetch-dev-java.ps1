<#
.SYNOPSIS
Fetches the Java runtimes the integration tests use: a 64-bit Eclipse Temurin JDK to tools\jdk (javac for the Swing
test fixture and the 64-bit test JVM) and a 32-bit Temurin 8 JRE to tools\jre8-x86 (the typical bundled JRE; the tests
also start it on its legacy Java-side bridge). tools\ is git-ignored.

.DESCRIPTION
Downloads the latest Temurin GA releases from the Adoptium API.

  -Version  Temurin feature version of the 64-bit JDK (default: 21).

.EXAMPLE
pwsh -File scripts/fetch-dev-java.ps1
#>
[CmdletBinding()]
param(
    [string]$Version = '21'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) ("winuse-fetch-java-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null

function Get-Temurin {
    param([string]$Feature, [string]$Arch, [string]$ImageType)
    $url = "https://api.adoptium.net/v3/binary/latest/$Feature/ga/windows/$Arch/$ImageType/hotspot/normal/eclipse"
    $zip = Join-Path $work "temurin-$Feature-$Arch-$ImageType.zip"
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $dir = Join-Path $work "temurin-$Feature-$Arch-$ImageType"
    Expand-Archive -Path $zip -DestinationPath $dir -Force
    # The archive has one top-level folder (e.g. jdk-21.0.4+7); return it.
    $top = Get-ChildItem $dir -Directory | Select-Object -First 1
    if (-not $top) { throw "Unexpected archive layout in $zip" }
    return $top.FullName
}

function Install-Tree {
    param([string]$From, [string]$To)
    if (Test-Path $To) { Remove-Item -Recurse -Force $To }
    New-Item -ItemType Directory -Force (Split-Path -Parent $To) | Out-Null
    Move-Item -Path $From -Destination $To
}

try {
    $results = @()

    $jdk = Get-Temurin -Feature $Version -Arch 'x64' -ImageType 'jdk'
    Install-Tree -From $jdk -To (Join-Path $repoRoot 'tools\jdk')
    $results += (Join-Path $repoRoot 'tools\jdk\bin\javac.exe')

    $jre8 = Get-Temurin -Feature '8' -Arch 'x86' -ImageType 'jre'
    Install-Tree -From $jre8 -To (Join-Path $repoRoot 'tools\jre8-x86')
    $results += (Join-Path $repoRoot 'tools\jre8-x86\bin\java.exe')
    foreach ($f in @('bin\JavaAccessBridge-32.dll', 'lib\ext\access-bridge-32.jar', 'bin\JavaAccessBridge.dll', 'lib\ext\access-bridge.jar', 'lib\ext\jaccess.jar')) {
        if (-not (Test-Path (Join-Path $repoRoot "tools\jre8-x86\$f"))) {
            Write-Warning "tools\jre8-x86\$f is missing: the 32-bit integration tests that need it will skip."
        }
    }

    Write-Host ''
    Write-Host 'Fetched:'
    $results | ForEach-Object { Write-Host "  $_" }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
