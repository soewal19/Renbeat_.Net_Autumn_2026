#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs all RoomBooking tests.

.DESCRIPTION
    Builds the solution and runs unit, integration, and concurrency tests.
    Docker must be running for SQL Server integration and concurrency tests.

.EXAMPLE
    .\run-tests.ps1
#>

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$solutionFile = Join-Path $projectRoot "RoomBooking.slnx"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Running all RoomBooking tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Checking Docker..." -ForegroundColor Yellow
try {
    $dockerStatus = docker ps 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Docker is not running or is unavailable." -ForegroundColor Red
        Write-Host "Integration and concurrency tests require Docker." -ForegroundColor Red
        Write-Host "Start Docker and try again." -ForegroundColor Red
        exit 1
    }
    Write-Host "Docker is running." -ForegroundColor Green
}
catch {
    Write-Host "ERROR: Docker was not found. Install Docker Desktop." -ForegroundColor Red
    exit 1
}
Write-Host ""

Write-Host "Building the solution..." -ForegroundColor Yellow
$buildResult = dotnet build $solutionFile --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Build failed." -ForegroundColor Red
    exit 1
}
Write-Host "Build completed successfully." -ForegroundColor Green
Write-Host ""

Write-Host "Running all tests..." -ForegroundColor Yellow
Write-Host "This may take a few minutes." -ForegroundColor Gray
Write-Host ""

$testResult = dotnet test $solutionFile --no-build

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
if ($LASTEXITCODE -eq 0) {
    Write-Host "All tests passed." -ForegroundColor Green
} else {
    Write-Host "One or more tests failed." -ForegroundColor Red
    Write-Host "Exit code: $LASTEXITCODE" -ForegroundColor Red
}
Write-Host "========================================" -ForegroundColor Cyan

exit $LASTEXITCODE
