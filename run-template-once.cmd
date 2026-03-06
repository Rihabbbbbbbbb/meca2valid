@echo off
setlocal

if "%~1"=="" (
  echo Usage:
  echo   run-template-once.cmd "C:\path\Template.docx" "C:\path\template-blueprint.json"
  exit /b 2
)

set "TEMPLATE=%~1"
set "OUT=%~2"
if "%OUT%"=="" set "OUT=%~dp0TemplateOneShotExtractor\template-blueprint.json"

if not exist "%TEMPLATE%" (
  echo ERROR: Template file not found: "%TEMPLATE%"
  exit /b 3
)

echo %TEMPLATE% | findstr /I /C:"http://_vscodecontentref_" >nul
if not errorlevel 1 (
  echo ERROR: Tu as colle un lien VS Code. Mets un vrai chemin .docx.
  exit /b 4
)

echo [1/2] Building TemplateOneShotExtractor...
dotnet build "%~dp0TemplateOneShotExtractor\TemplateOneShotExtractor.csproj" -c Release -v minimal
if errorlevel 1 exit /b %errorlevel%

echo [2/2] Extracting template blueprint...
dotnet run --project "%~dp0TemplateOneShotExtractor\TemplateOneShotExtractor.csproj" -c Release -- --template "%TEMPLATE%" --out "%OUT%"
if errorlevel 1 exit /b %errorlevel%

echo SUCCESS: "%OUT%"
exit /b 0
