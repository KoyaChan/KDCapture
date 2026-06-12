@echo off
setlocal

cd /d "%~dp0"

echo [INFO] Building KDCapture.csproj (Release)...
dotnet build ".\KDCapture.csproj" -c Release
if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)

echo [INFO] Build succeeded.
echo [INFO] Output: .\bin\Release\net48\KDCapture.exe
exit /b 0
