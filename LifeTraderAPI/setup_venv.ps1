$ErrorActionPreference = "Stop"
Write-Host "=== Deep Blue VENV Setup ===" -ForegroundColor Cyan

$VenvDir = ".venv"
$PythonExe = "$VenvDir\Scripts\python.exe"
$PipExe = "$VenvDir\Scripts\pip.exe"

# 1. Create Virtual Environment
if (-not (Test-Path $VenvDir)) {
    Write-Host "[1/4] Creating virtual environment..." -ForegroundColor Yellow
    python -m venv $VenvDir
} else {
    Write-Host "[1/4] Virtual environment already exists." -ForegroundColor Green
}

# 2. Upgrade Pip
Write-Host "[2/4] Upgrading pip..." -ForegroundColor Yellow
& $PythonExe -m pip install --upgrade pip | Out-Null

# 3. Install Requirements
if (Test-Path "requirements.txt") {
    Write-Host "[3/4] Installing requirements.txt (This may take a minute)..." -ForegroundColor Yellow
    & $PipExe install -r requirements.txt
} else {
    Write-Host "[3/4] ERROR: requirements.txt not found!" -ForegroundColor Red
    exit 1
}

# 4. Build OpenBB Extensions
Write-Host "[4/4] Pre-building OpenBB extensions..." -ForegroundColor Yellow
if (Test-Path "$VenvDir\Scripts\openbb-build.exe") {
    & "$VenvDir\Scripts\openbb-build.exe"
} else {
    & $PythonExe -c "import openbb; print('OpenBB imported successfully.')" 2>$null
}

Write-Host "=== Setup Complete! ===" -ForegroundColor Green
Write-Host "You are cleared to start the server." -ForegroundColor Magenta