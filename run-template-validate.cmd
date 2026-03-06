@echo off
setlocal

if "%~1"=="" (
  echo Usage:
  echo   run-template-validate.cmd "C:\path\template-blueprint.json" "C:\path\user.docx" ["C:\path\validation-report.json"] [pivotLevel] ["C:\path\semantic-mapping.json"]
  exit /b 2
)

if "%~2"=="" (
  echo ERROR: Missing user document path.
  echo Usage:
  echo   run-template-validate.cmd "C:\path\template-blueprint.json" "C:\path\user.docx" ["C:\path\validation-report.json"] [pivotLevel] ["C:\path\semantic-mapping.json"]
  exit /b 2
)

set "BLUEPRINT=%~1"
set "USERDOC=%~2"
set "OUT=%~3"
set "PIVOT=%~4"
set "MAPPING=%~5"

if "%OUT%"=="" set "OUT=%~dp0TemplateOneShotExtractor\validation-report.json"
if "%PIVOT%"=="" set "PIVOT=2"

if not exist "%BLUEPRINT%" (
  echo ERROR: Blueprint file not found: "%BLUEPRINT%"
  exit /b 3
)

if not exist "%USERDOC%" (
  echo ERROR: User file not found: "%USERDOC%"
  exit /b 3
)

echo [1/2] Building TemplateOneShotExtractor...
dotnet build "%~dp0TemplateOneShotExtractor\TemplateOneShotExtractor.csproj" -c Release -v minimal
if errorlevel 1 exit /b %errorlevel%

echo [2/2] Validating user document against template contract...
if "%MAPPING%"=="" (
  dotnet run --project "%~dp0TemplateOneShotExtractor\TemplateOneShotExtractor.csproj" -c Release -- --validate --templateBlueprint "%BLUEPRINT%" --user "%USERDOC%" --out "%OUT%" --pivotLevel %PIVOT%
) else (
  dotnet run --project "%~dp0TemplateOneShotExtractor\TemplateOneShotExtractor.csproj" -c Release -- --validate --templateBlueprint "%BLUEPRINT%" --user "%USERDOC%" --mapping "%MAPPING%" --out "%OUT%" --pivotLevel %PIVOT%
)
set "EC=%errorlevel%"

if "%EC%"=="0" (
  echo SUCCESS: "%OUT%"
  exit /b 0
)

if "%EC%"=="6" (
  echo VALIDATION RESULT: FAIL contract - see report "%OUT%"
  exit /b 6
)

echo ERROR: Validation execution failed with exit code %EC%
exit /b %EC%
