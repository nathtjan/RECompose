# Builds the native engine, stages it for .NET, then builds and tests the app.
# Usage: .\build.ps1 [-Configuration Debug|Release]   (the .NET configuration; the engine is always Release)
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$engineBuild = Join-Path $root 'build/engine'

function Invoke-Step([string]$name, [scriptblock]$step) {
    Write-Host "==> $name" -ForegroundColor Cyan
    & $step
    if ($LASTEXITCODE -ne 0) { throw "$name failed (exit code $LASTEXITCODE)" }
}

Invoke-Step 'Configure engine' { cmake -S (Join-Path $root 'engine') -B $engineBuild -DCMAKE_BUILD_TYPE=Release }
Invoke-Step 'Build engine'     { cmake --build $engineBuild --config Release --parallel }
Invoke-Step 'Test engine'      { ctest --test-dir $engineBuild -C Release --output-on-failure }
Invoke-Step 'Stage engine'     { cmake --install $engineBuild --config Release --component engine --prefix (Join-Path $root 'artifacts/native') }
Invoke-Step 'Build app'        { dotnet build (Join-Path $root 'RECompose.slnx') -c $Configuration }
Invoke-Step 'Test app'         { dotnet test --solution (Join-Path $root 'RECompose.slnx') -c $Configuration --no-build }

Write-Host ''
Write-Host "Done. Run the app with: dotnet run --project src/RECompose.App -c $Configuration"
