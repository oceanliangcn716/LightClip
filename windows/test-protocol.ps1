param([string]$DotNetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
foreach ($name in @('Interop', 'PairInterop', 'StreamInterop', 'StreamExtraChecks')) {
    & $DotNetPath build (Join-Path $PSScriptRoot "tests/$name/$name.csproj") -c Release
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $name" }
}
function Invoke-Fixture([string]$Name, [string]$Mode) {
    $fixturePath = Join-Path $PSScriptRoot "tests/$Name/bin/Release/net10.0/$Name.dll"
    if ($Mode) { & $DotNetPath $fixturePath $Mode } else { & $DotNetPath $fixturePath }
    if ($LASTEXITCODE -ne 0) { throw "Fixture failed: $Name $Mode" }
}
Invoke-Fixture 'PairInterop' 'native'
foreach ($mode in @('native', 'text', 'negative')) { Invoke-Fixture 'StreamInterop' $mode }
Invoke-Fixture 'StreamExtraChecks' ''
Write-Output 'PASS: packaged protocol fixtures. No system clipboard access or real pairing credentials.'
# Interop is compiled for the isolated GroupTests harness. Its default mode
# expects a synthetic receiver, so it is intentionally not run against port 49287.
