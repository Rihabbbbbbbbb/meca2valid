param(
    [Parameter(Mandatory = $true)]
    [string]$UserDoc,

    [Parameter(Mandatory = $true)]
    [string]$TemplateDoc,

    [Parameter(Mandatory = $true)]
    [string]$GuideDoc
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectDir

foreach ($doc in @($UserDoc, $TemplateDoc, $GuideDoc)) {
    if (-not (Test-Path $doc)) {
        Write-Host "ERROR: Missing file: $doc" -ForegroundColor Red
        exit 1
    }
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

Write-Host "Running comparison..."
dotnet $dllPath --user $UserDoc --template $TemplateDoc --guide $GuideDoc
$runExit = $LASTEXITCODE

$tracePath = Join-Path $projectDir "$outPath\run-trace.log"
$reportPath = Join-Path $projectDir "$outPath\comparison-report.json"

if (Test-Path $tracePath) {
    Write-Host "Trace file:   $tracePath" -ForegroundColor Green
}

if (Test-Path $reportPath) {
    Write-Host "Report file:  $reportPath" -ForegroundColor Green
}

if ($runExit -ne 0) {
    Write-Host "Run failed with exit code $runExit" -ForegroundColor Red
    exit $runExit
}

Write-Host "Done." -ForegroundColor Green