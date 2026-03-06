@echo off
setlocal

set "ROOT=%~dp0"
set "PROJECT=%ROOT%TemplateOneShotExtractor\TemplateOneShotExtractor.csproj"
set "OUT=%ROOT%TemplateOneShotExtractor\template-blueprint.json"
set "TEMPLATE=%~1"

if "%TEMPLATE%"=="" set "TEMPLATE=C:\Users\TA29225\Desktop\Component_or_Part_Specification_Template.docx"

echo ===== TEMPLATE TOOL TEST SUITE =====
echo Project: "%PROJECT%"
echo Template: "%TEMPLATE%"
echo.

echo [TEST 1] Build project
dotnet build "%PROJECT%" -c Release -v minimal >nul 2>nul
if errorlevel 1 (
  echo FAIL TEST 1: Build failed
  exit /b 11
) else (
  echo PASS TEST 1
)

echo [TEST 2] Missing args should return exit code 2
dotnet run --project "%PROJECT%" -c Release -- >nul 2>nul
set "EC=%errorlevel%"
if "%EC%"=="2" (
  echo PASS TEST 2
) else (
  echo FAIL TEST 2: Expected exit code 2, got %EC%
  exit /b 12
)

echo [TEST 3] Missing file should return exit code 3
dotnet run --project "%PROJECT%" -c Release -- --template "C:\does-not-exist\x.docx" >nul 2>nul
set "EC=%errorlevel%"
if "%EC%"=="3" (
  echo PASS TEST 3
) else (
  echo FAIL TEST 3: Expected exit code 3, got %EC%
  exit /b 13
)

echo [TEST 4] Real extraction should succeed and create JSON
if exist "%OUT%" del /f /q "%OUT%" >nul 2>nul
call "%ROOT%run-template-once.cmd" "%TEMPLATE%" "%OUT%" >nul 2>nul
set "EC=%errorlevel%"
if not "%EC%"=="0" (
  echo FAIL TEST 4: Extraction failed with exit code %EC%
  exit /b 14
)
if not exist "%OUT%" (
  echo FAIL TEST 4: Output JSON not found
  exit /b 14
)
echo PASS TEST 4

echo [TEST 5] JSON contains mandatory keys
findstr /I /C:"\"Metadata\"" "%OUT%" >nul
if errorlevel 1 (
  echo FAIL TEST 5: Missing Metadata
  exit /b 15
)
findstr /I /C:"\"Stats\"" "%OUT%" >nul
if errorlevel 1 (
  echo FAIL TEST 5: Missing Stats
  exit /b 15
)
findstr /I /C:"\"OrderedTitleList\"" "%OUT%" >nul
if errorlevel 1 (
  echo FAIL TEST 5: Missing OrderedTitleList
  exit /b 15
)
echo PASS TEST 5

echo.
echo ALL TESTS PASSED
echo Output: "%OUT%"
exit /b 0
