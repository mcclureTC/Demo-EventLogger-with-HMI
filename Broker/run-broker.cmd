@echo off
rem Starts the demo broker in this window. Close the window or press Ctrl+C to stop it.
cd /d "%~dp0"

set "MOSQUITTO=%ProgramFiles%\mosquitto\mosquitto.exe"
if not exist "%MOSQUITTO%" (
    echo Mosquitto was not found at %MOSQUITTO%.
    echo Install it from https://mosquitto.org/download/ ^(the default folder is fine^).
    pause
    exit /b 1
)

if not exist "certs\ca.crt" (
    echo No certificates yet. Create them first:
    echo     dotnet run --project ..\Tools\CertTool
    pause
    exit /b 1
)

if not exist "data" mkdir data

echo Starting Mosquitto on port 8883 ^(TLS, client certificates required^)...
"%MOSQUITTO%" -c mosquitto.conf -v
pause
