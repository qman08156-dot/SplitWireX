@echo off
REM ================================================================
REM  SplitWireX build: SplitWire-Turkey + the SplitWireX tray panel
REM  Output: SplitWireTurkey\bin\Release\net8.0-windows\
REM ================================================================
setlocal
cd /d "%~dp0"

dotnet --list-sdks | findstr /b "8." >nul
if errorlevel 1 (
    echo Error: .NET 8 SDK not found. Install it with:  winget install Microsoft.DotNet.SDK.8
    pause
    exit /b 1
)

pushd SplitWireTurkey
dotnet build -c Release
if errorlevel 1 (
    echo Error: build failed!
    popd
    pause
    exit /b 1
)
popd

set OUT=SplitWireTurkey\bin\Release\net8.0-windows

REM SplitWire expects its tools in a "res" folder next to the exe
echo Copying tools to %OUT%\res ...
robocopy "SplitWireTurkey\Resources" "%OUT%\res" /E /NFL /NDL /NJH /NJS /NP >nul
if exist "Prerequisites" robocopy "Prerequisites" "%OUT%\Prerequisites" /E /NFL /NDL /NJH /NJS /NP >nul

echo.
echo Build complete: %OUT%\SplitWire-Turkey.exe
echo   (no arguments)  SplitWireX panel bottom-right + tray icon
echo   --tray          start hidden in the tray
echo   --classic       original full window
endlocal
