# Builds the program for sharing: dist\GTA VI Radio Simulator\ and dist\GTA VI Radio Simulator.zip.
# Self-contained (people don't need .NET installed). Contains no game data or audio: the app reads
# everything from each user's own game files.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'GTA VI Radio Simulator'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$opts = @('-c', 'Release', '-r', 'win-x64', '--self-contained',
          '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true',
          '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '-o', $out)
foreach ($proj in 'RadioSim.Gui', 'RadioSim') {
    dotnet publish (Join-Path $root $proj) @opts
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}
Copy-Item (Join-Path $root 'RadioSim\README.txt') $out
Copy-Item (Join-Path $root 'THIRD_PARTY_NOTICES.md') $out          # bcpack-rs and silicon-rans licenses
$zip = Join-Path $dist 'GTA VI Radio Simulator.zip'
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force
Write-Host "`nShare $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
