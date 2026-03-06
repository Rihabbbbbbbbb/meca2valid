param(
    [Parameter(Mandatory = $true)]
    [string]$DocPath
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectDir

if (-not (Test-Path $DocPath)) {
    Write-Host "ERROR: DOCX not found: $DocPath" -ForegroundColor Red
    exit 1
}

$stamp = Get-Date -Format yyyyMMddHHmmss
$outPath = "bin\iso\$stamp\"
$objPath = "obj\iso\$stamp\"

Write-Host "Building..."
dotnet build -v minimal -p:OutputPath=$outPath -p:IntermediateOutputPath=$objPath -p:UseAppHost=false
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

$dllPath = Join-Path $projectDir "$outPath\WordOpenXmlTest.dll"
if (-not (Test-Path $dllPath)) {
    Write-Host "ERROR: Built DLL not found: $dllPath" -ForegroundColor Red
    exit 1
}

Write-Host "Running parser..."
dotnet $dllPath $DocPath
$runExit = $LASTEXITCODE

$tracePath = Join-Path $projectDir "$outPath\run-trace.log"
if (Test-Path $tracePath) {
    Write-Host "Trace file: $tracePath" -ForegroundColor Green
}

$jsonPath = Join-Path $projectDir "$outPath\sections.json"
if (Test-Path $jsonPath) {
    Write-Host "JSON file:  $jsonPath" -ForegroundColor Green
}

if ($runExit -ne 0) {
    Write-Host "Run failed with exit code $runExit" -ForegroundColor Red
    exit $runExit
}

Write-Host "Done." -ForegroundColor Green