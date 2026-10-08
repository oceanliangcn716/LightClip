param([ValidateSet('x64', 'arm64')][string]$Architecture = 'x64')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $target = if ($Architecture -eq 'arm64') { 'aarch64-pc-windows-msvc' } else { 'x86_64-pc-windows-msvc' }
    if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
        throw 'The build machine needs the official Rust MSVC toolchain. End users do not need Rust.'
    }
    & cargo build --locked --release --target $target
    if ($LASTEXITCODE -ne 0) { throw 'Native pairing library build failed.' }
    $output = Join-Path $PSScriptRoot '..\windows\native'
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    Copy-Item (Join-Path $PSScriptRoot "target\$target\release\lightclip_pairing.dll") (Join-Path $output 'lightclip_pairing.dll') -Force
    Write-Output 'Built the native pairing library for the selected architecture.'
} finally { Pop-Location }
