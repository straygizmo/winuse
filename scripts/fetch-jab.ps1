<#
.SYNOPSIS
Fetches the Java Access Bridge client DLL (WindowsAccessBridge-64.dll) from an Eclipse Temurin JRE so that winuse
can read Swing windows on a machine without a 64-bit Java, and (with -Dev) the Java runtimes the integration tests use.

.DESCRIPTION
Downloads the latest Temurin GA release of the given feature version from the Adoptium API.

  -Dest     Where the DLL goes (default: out). WindowsAccessBridge-64.dll is placed next to winuse.exe and the
            jdk.accessibility legal notices (GPLv2 + Classpath Exception) go to <Dest>\jab-legal\.
  -Version  Temurin feature version for the DLL and the development JDK (default: 21).
  -Dev      Also extract a 64-bit JDK to tools\jdk (javac for the Swing test fixture) and a 32-bit Temurin 8 JRE to
            tools\jre8-x86 (runs the fixture on a 32-bit Java 8, the typical bundled JRE). tools\ is git-ignored.

.EXAMPLE
pwsh -File scripts/fetch-jab.ps1
pwsh -File scripts/fetch-jab.ps1 -Dev
#>
[CmdletBinding()]
param(
    [string]$Dest = 'out',
    [string]$Version = '21',
    [switch]$Dev
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$Dest = if ([IO.Path]::IsPathRooted($Dest)) { $Dest } else { Join-Path $repoRoot $Dest }
$work = Join-Path ([IO.Path]::GetTempPath()) ("winuse-fetch-jab-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null

function Get-Temurin {
    param([string]$Feature, [string]$Arch, [string]$ImageType)
    $url = "https://api.adoptium.net/v3/binary/latest/$Feature/ga/windows/$Arch/$ImageType/hotspot/normal/eclipse"
    $zip = Join-Path $work "temurin-$Feature-$Arch-$ImageType.zip"
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $dir = Join-Path $work "temurin-$Feature-$Arch-$ImageType"
    Expand-Archive -Path $zip -DestinationPath $dir -Force
    # The archive has one top-level folder (e.g. jdk-21.0.4+7-jre); return it.
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

    $jre = Get-Temurin -Feature $Version -Arch 'x64' -ImageType 'jre'
    $dll = Join-Path $jre 'bin\WindowsAccessBridge-64.dll'
    if (-not (Test-Path $dll)) { throw "WindowsAccessBridge-64.dll not found in $jre" }
    New-Item -ItemType Directory -Force $Dest | Out-Null
    Copy-Item $dll (Join-Path $Dest 'WindowsAccessBridge-64.dll') -Force
    $results += (Join-Path $Dest 'WindowsAccessBridge-64.dll')
    $legal = Join-Path $jre 'legal\jdk.accessibility'
    if (Test-Path $legal) {
        $legalDest = Join-Path $Dest 'jab-legal'
        New-Item -ItemType Directory -Force $legalDest | Out-Null
        Copy-Item (Join-Path $legal '*') $legalDest -Force
        $results += $legalDest
    } else {
        Write-Warning "No legal\jdk.accessibility folder in $jre; ship the Temurin license notice yourself."
    }

    if ($Dev) {
        $jdk = Get-Temurin -Feature $Version -Arch 'x64' -ImageType 'jdk'
        Install-Tree -From $jdk -To (Join-Path $repoRoot 'tools\jdk')
        $results += (Join-Path $repoRoot 'tools\jdk\bin\javac.exe')

        $jre8 = Get-Temurin -Feature '8' -Arch 'x86' -ImageType 'jre'
        Install-Tree -From $jre8 -To (Join-Path $repoRoot 'tools\jre8-x86')
        $results += (Join-Path $repoRoot 'tools\jre8-x86\bin\java.exe')
        foreach ($f in @('bin\JavaAccessBridge-32.dll', 'lib\ext\access-bridge-32.jar')) {
            if (-not (Test-Path (Join-Path $repoRoot "tools\jre8-x86\$f"))) {
                Write-Warning "tools\jre8-x86\$f is missing: this JRE has no Java Access Bridge; the 32-bit integration tests will skip."
            }
        }
    }

    Write-Host ''
    Write-Host 'Fetched:'
    $results | ForEach-Object { Write-Host "  $_" }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
