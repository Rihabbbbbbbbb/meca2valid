param(
    [Parameter(Mandatory = $true)]
    [string]$Template,

    [string]$Out = "template-blueprint.json"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

if (-not (Test-Path -LiteralPath $Template)) {
    throw "Template file not found: $Template"
}

Write-Host "Building standalone extractor..."
dotnet build .\TemplateOneShotExtractor\TemplateOneShotExtractor.csproj -c Release | Out-Host

Write-Host "Running one-shot template extraction..."
dotnet run --project .\TemplateOneShotExtractor\TemplateOneShotExtractor.csproj -c Release -- --template "$Template" --out "$Out" | Out-Host

$fullOut = [System.IO.Path]::GetFullPath($Out)
Write-Host "Done. Blueprint file: $fullOut"
