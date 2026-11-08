@echo off
cd /d %~dp0
where py >nul 2>nul && (py -3 server.py & goto :eof)
where python >nul 2>nul && (python server.py & goto :eof)
echo 需要 Python 3.9+: https://www.python.org/downloads/
pause
