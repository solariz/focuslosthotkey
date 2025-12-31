@echo off
setlocal

REM =====================================================
REM Extract APP_VERSION from Program.cs
REM =====================================================

powershell -NoProfile -Command ^
  "$v = (Get-Content Program.cs | Select-String 'APP_VERSION\s*=\s*\"').Line; ^
   if (-not $v) { exit 1 }; ^
   $v -replace '.*APP_VERSION\s*=\s*\"([^\"]+)\".*','$1'" ^
  > .version.tmp

if errorlevel 1 (
    echo ERROR: Could not read APP_VERSION from Program.cs
    del .version.tmp 2>nul
    exit /b 1
)

set /p APP_VERSION=<.version.tmp
del .version.tmp

if "%APP_VERSION%"=="" (
    echo ERROR: APP_VERSION is empty
    exit /b 1
)

echo Detected version: %APP_VERSION%

REM =====================================================
REM Configuration (csproj is source of truth)
REM =====================================================

set PROJECT_FILE=focuslosthotkey.csproj
set CONFIG=Release
set RUNTIME=win-x64
set FRAMEWORK=net8.0-windows

set PUBLISH_DIR=bin\%CONFIG%\%FRAMEWORK%\%RUNTIME%\publish
set OUTPUT_DIR=dist%APP_VERSION%
set ZIP_NAME=focuslosthotkey-release-%APP_VERSION%.zip

set APP_EXE=focuslosthotkey.exe
set APP_INI=focuslosthotkey.ini

REM =====================================================
REM Clean previous output
REM =====================================================

echo Cleaning output...
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
mkdir "%OUTPUT_DIR%"

if exist "%ZIP_NAME%" del "%ZIP_NAME%"

REM =====================================================
REM Publish (minimal, trust csproj)
REM =====================================================

echo Publishing application...
dotnet publish "%PROJECT_FILE%" -c %CONFIG% -r %RUNTIME%

if errorlevel 1 (
    echo.
    echo BUILD FAILED
    exit /b 1
)

REM =====================================================
REM Verify publish output
REM =====================================================

if not exist "%PUBLISH_DIR%\%APP_EXE%" (
    echo ERROR: %APP_EXE% not found
    echo Expected: %PUBLISH_DIR%\%APP_EXE%
    exit /b 1
)

if not exist "%PUBLISH_DIR%\%APP_INI%" (
    echo ERROR: %APP_INI% not found
    echo Expected: %PUBLISH_DIR%\%APP_INI%
    exit /b 1
)

REM =====================================================
REM Collect release files
REM =====================================================

echo Copying release files...
copy "%PUBLISH_DIR%\%APP_EXE%" "%OUTPUT_DIR%\" >nul
copy "%PUBLISH_DIR%\%APP_INI%" "%OUTPUT_DIR%\" >nul

copy "README.md" "%OUTPUT_DIR%\" >nul
copy "citizen-history.com.url" "%OUTPUT_DIR%\" >nul

REM =====================================================
REM Create ZIP (contents only, no extra folder)
REM =====================================================

echo Creating ZIP archive...
powershell -NoProfile -Command ^
  "Compress-Archive -Path '%OUTPUT_DIR%\*' -DestinationPath '%ZIP_NAME%' -Force"

REM =====================================================
REM Done
REM =====================================================

echo.
echo Build completed successfully.
echo Version: %APP_VERSION%
echo Output folder: %OUTPUT_DIR%
echo ZIP archive: %ZIP_NAME%
echo.

endlocal
