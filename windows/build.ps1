param(
    [string]$DotNetPath = 'dotnet',
    [switch]$RebuildNative,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$sourceRoot = $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $sourceRoot 'artifacts/LightClip-0.3.0-win-x64' }
$sdkVersion = & $DotNetPath --version
if ($LASTEXITCODE -ne 0 -or [int]($sdkVersion.Split('.')[0]) -lt 10) { throw '.NET SDK 10 or newer is required on the build machine.' }
$nativePath = Join-Path $sourceRoot 'LightClip.Group/native/lightclip_pairing.dll'
if ($RebuildNative) {
    if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { throw 'Rebuilding native code requires Rust MSVC, the x86_64-pc-windows-msvc target and MSVC Build Tools.' }
    $previousEncodedRustFlags = $env:CARGO_ENCODED_RUSTFLAGS
    try {
        # Cargo's encoded form preserves paths containing spaces. Keep existing
        # flags, then remove personal source locations from Rust panic strings.
        $separator = [string][char]31
        $flags = @()
        if ($previousEncodedRustFlags) {
            $flags += $previousEncodedRustFlags -split [regex]::Escape($separator)
        } elseif ($env:RUSTFLAGS) {
            $flags += $env:RUSTFLAGS.Trim() -split '\s+'
        }
        $flags += @('-C', 'target-feature=+crt-static')
        $prefixes = @($sourceRoot, $env:USERPROFILE, $env:CARGO_HOME, $env:RUSTUP_HOME) |
            Where-Object { $_ } | Select-Object -Unique
        foreach ($prefix in $prefixes) {
            $flags += "--remap-path-prefix=$prefix=."
            $forwardPrefix = $prefix.Replace('\', '/')
            if ($forwardPrefix -ne $prefix) { $flags += "--remap-path-prefix=$forwardPrefix=." }
        }
        $env:CARGO_ENCODED_RUSTFLAGS = $flags -join $separator
        & cargo build --manifest-path (Join-Path $sourceRoot 'pairing/Cargo.toml') --locked --release --target x86_64-pc-windows-msvc --target-dir (Join-Path $sourceRoot 'artifacts/native')
        if ($LASTEXITCODE -ne 0) { throw 'Native pairing build failed.' }
        New-Item -ItemType Directory -Path (Split-Path -Parent $nativePath) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $sourceRoot 'artifacts/native/x86_64-pc-windows-msvc/release/lightclip_pairing.dll') -Destination $nativePath -Force
    } finally { $env:CARGO_ENCODED_RUSTFLAGS = $previousEncodedRustFlags }
}
if (-not (Test-Path -LiteralPath $nativePath)) { throw 'The x64 native pairing DLL is missing. Build from source with -RebuildNative.' }
$nativeBytes = [IO.File]::ReadAllBytes($nativePath)
$peOffset = [BitConverter]::ToInt32($nativeBytes, 0x3c)
if ([BitConverter]::ToUInt32($nativeBytes, $peOffset) -ne 0x00004550 -or [BitConverter]::ToUInt16($nativeBytes, $peOffset + 4) -ne 0x8664) { throw 'The native pairing DLL must be Windows x64.' }
$personalPathPattern = '(?i)(?:[a-z]:[\\/](?:Users|用户)[\\/]|/Users/)'
$nativeStrings = @([Text.Encoding]::UTF8.GetString($nativeBytes), [Text.Encoding]::Unicode.GetString($nativeBytes))
if ($nativeBytes.Length -gt 1) { $nativeStrings += [Text.Encoding]::Unicode.GetString($nativeBytes, 1, $nativeBytes.Length - 1) }
if ($nativeStrings | Where-Object { $_ -match $personalPathPattern }) {
    throw 'Native DLL still contains a personal user-directory path. Rebuild with -RebuildNative; do not publish this binary.'
}
& $DotNetPath publish (Join-Path $sourceRoot 'LightClip.Group/LightClip.Group.csproj') -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
Copy-Item -LiteralPath (Join-Path $sourceRoot 'LightClip.Group/README.md') -Destination (Join-Path $OutputDirectory 'README.md') -Force
Write-Output "Ready: $OutputDirectory"
