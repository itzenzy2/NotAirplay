param(
    [Parameter(Mandatory)] [string] $Clang,
    [switch] $Test
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build NotAirplay.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    & .\native\Build-Native.ps1 -Clang $Clang
    if ($Test) {
        dotnet run --project tests/NotAirplay.Tests.csproj -c Release -- tests/fixtures --native
        if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
    }
} finally { Pop-Location }
