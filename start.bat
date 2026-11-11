@echo off
chcp 65001 >nul
cd /d "%~dp0"
set "AZPY=%ProgramFiles%\Microsoft SDKs\Azure\CLI2\python.exe"
where py >nul 2>nul && (py -3 server.py & goto :eof)
where python >nul 2>nul && (python server.py & goto :eof)
if exist "%AZPY%" ("%AZPY%" server.py & goto :eof)
echo Python 3.9+ not found. Install from https://www.python.org/downloads/ or Azure CLI (bundles Python).
echo 需要 Python 3.9+: https://www.python.org/downloads/ (或安装 Azure CLI, 其自带 Python)
pause
