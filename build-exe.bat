@echo off
setlocal
rem Build CodexInstaller.exe from CodexInstaller.cs (self-contained C#)
rem Requires .NET Framework 4.x compiler (ships with Windows).

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. .NET Framework 4.x required.
    exit /b 1
)

cd /d "%~dp0"
"%CSC%" /nologo /target:exe /optimize /out:CodexInstaller.exe CodexInstaller.cs
if errorlevel 1 (
    echo [ERROR] compile failed.
    exit /b 1
)
echo [OK] CodexInstaller.exe built.
endlocal
