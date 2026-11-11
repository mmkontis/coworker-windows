# Coworker Windows - Kill, Clean, Build & Run
$ErrorActionPreference = "SilentlyContinue"

Write-Host "Stopping Coworker..." -ForegroundColor Yellow
Get-Process -Name "Coworker" | Stop-Process -Force 2>$null
Start-Sleep -Milliseconds 500

Write-Host "Cleaning old build..." -ForegroundColor Yellow
Remove-Item -Recurse -Force "bin" 2>$null
Remove-Item -Recurse -Force "obj" 2>$null

Write-Host "Building..." -ForegroundColor Cyan
dotnet build -p:Platform=x64 -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED" -ForegroundColor Red
    exit 1
}

Write-Host "Launching Coworker..." -ForegroundColor Green
Start-Process "bin\x64\Debug\net9.0-windows10.0.22621.0\Coworker.exe"
Write-Host "Done." -ForegroundColor Green
