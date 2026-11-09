param(
    [Parameter(Mandatory)] [string] $Clang,
    [string] $Output = ''
)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent (Resolve-Path $MyInvocation.MyCommand.Path) }
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $root '..\bin\Release\net8.0-windows'
}
$Clang = (Get-Command $Clang).Source
$playfair = Join-Path $root 'playfair'
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$sources = 'hand_garble.c', 'modified_md5.c', 'omg_hax.c', 'playfair.c', 'sap_hash.c' |
ForEach-Object { Join-Path $playfair $_ }
& $Clang --target=x86_64-w64-windows-gnu -O2 -fwrapv -fno-strict-aliasing -I $playfair @sources `
(Join-Path $root 'key-helper.c') -o (Join-Path $Output 'notairplay-key.exe')
if ($LASTEXITCODE -ne 0) { throw 'FairPlay helper compilation failed.' }

& $Clang --target=x86_64-w64-windows-gnu -O2 -c (Join-Path $root 'alac.c') -o (Join-Path $root 'alac.o')
if ($LASTEXITCODE -ne 0) { throw 'ALAC compilation failed.' }

$ClangCpp = Join-Path (Split-Path $Clang) 'clang++.exe'
& $ClangCpp --target=x86_64-w64-windows-gnu -std=c++17 -O2 -shared -static -Wall -Wextra `
(Join-Path $root 'video.cpp') (Join-Path $root 'alac.o') -o (Join-Path $Output 'notairplay-video.dll') `
    -lmfplat -lmfuuid -lmf -ld3d11 -ldxgi -lole32 -loleaut32 -luuid -lwmcodecdspuuid
if ($LASTEXITCODE -ne 0) { throw 'Native video backend compilation failed.' }
