@echo off
rem Builds the Playcast Companion NSIS setup:
rem   1. cleans and re-publishes a self-contained single-file exe into installer\payload
rem   2. compiles installer.nsi into PlaycastCompanionSetup-<version>.exe
setlocal
cd /d "%~dp0.."

echo [1/3] Cleaning payload...
if exist "installer\payload" rmdir /s /q "installer\payload"

echo [2/3] Publishing self-contained single-file build...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "installer\payload"
if errorlevel 1 goto :fail
if not exist "installer\payload\PlaycastCompanion.exe" goto :missing
if not exist "installer\payload\config.json" goto :missing

echo [3/3] Compiling NSIS installer...
set "MAKENSIS=%ProgramFiles(x86)%\NSIS\makensis.exe"
if not exist "%MAKENSIS%" set "MAKENSIS=%ProgramFiles%\NSIS\makensis.exe"
if exist "%MAKENSIS%" goto :compile
where makensis >nul 2>nul
if errorlevel 1 goto :nonsis
set "MAKENSIS=makensis"

:compile
"%MAKENSIS%" "installer\installer.nsi"
if errorlevel 1 goto :fail

echo.
echo Done: installer\PlaycastCompanionSetup-2.4.0.exe
pause
exit /b 0

:missing
echo.
echo BUILD FAILED: publish output is incomplete - PlaycastCompanion.exe or
echo config.json is missing from installer\payload
pause
exit /b 1

:nonsis
echo NSIS not found. Install it first:  winget install NSIS.NSIS
pause
exit /b 1

:fail
echo.
echo BUILD FAILED
pause
exit /b 1
