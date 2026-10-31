param(
    [string]$DotnetExe = "dotnet",
    [switch]$SkipTests,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    $version = & $DotnetExe --version
    if (-not $version) { throw "dotnet not found. Install the .NET 8 SDK or pass -DotnetExe <path>." }
    $major = [int]($version.Split('.')[0])
    if ($major -lt 8) { throw "Fotonizer requires the .NET 8 SDK, but found $version." }

    Write-Host "==> restore" -ForegroundColor Cyan
    & $DotnetExe restore Fotonizer.sln
    if ($LASTEXITCODE -ne 0) { throw "restore failed" }

    Write-Host "==> build" -ForegroundColor Cyan
    & $DotnetExe build Fotonizer.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "build failed" }

    if (-not $SkipTests) {
        Write-Host "==> test" -ForegroundColor Cyan
        & $DotnetExe test tests/Fotonizer.Tests/Fotonizer.Tests.csproj -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "tests failed" }
    }

    if (-not $SkipPublish) {
        Write-Host "==> publish" -ForegroundColor Cyan
        $out = Join-Path $root "publish/Fotonizer-win-x64"
        $running = Get-Process Fotonizer -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($out, [StringComparison]::OrdinalIgnoreCase) }
        if ($running) {
            throw "Fotonizer is running from $out. Exit it from the tray menu before building."
        }
        Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
        & $DotnetExe publish src/Fotonizer.App/Fotonizer.App.csproj -c Release -r win-x64 --self-contained true -o $out
        if ($LASTEXITCODE -ne 0) { throw "publish failed" }

        Copy-Item README.md, LICENSE, THIRD-PARTY-NOTICES.md $out -Force
        Copy-Item docs (Join-Path $out "docs") -Recurse -Force
        $zip = Join-Path $root "publish/Fotonizer-win-x64.zip"
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal
        Write-Host "==> done: $zip" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
