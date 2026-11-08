#!/usr/bin/env bash
# macOS / Linux 一键启动: 双击或 ./start.sh
cd "$(dirname "$0")" || exit 1
PY=$(command -v python3 || command -v python) || { echo "需要 Python 3.9+: https://www.python.org/downloads/"; exit 1; }
command -v az >/dev/null || echo "提示: 未检测到 Azure CLI, 只能使用 App Registration 登录 (安装: https://aka.ms/azcli)"
exec "$PY" server.py
