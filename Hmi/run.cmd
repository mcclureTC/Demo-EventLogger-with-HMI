@echo off
rem Builds and starts the Event Logger Demo HMI. The browser opens once the server is listening.
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo The .NET SDK was not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo Building the HMI (the first build downloads NuGet packages and can take a minute)...
dotnet build --configuration Release
if errorlevel 1 (
    echo.
    echo BUILD FAILED - see the errors above.
    pause
    exit /b 1
)

echo.
echo Starting the HMI. Close this window or press Ctrl+C to stop it.
dotnet run --configuration Release --no-build
pause
