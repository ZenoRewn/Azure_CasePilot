#!/usr/bin/env bash
# 调试构建后用开发证书签名, 避免钥匙串每次重新授权
set -e; cd "$(dirname "$0")"
swift build "$@"
codesign --force --sign "${SIGN_ID:-Apple Development: zeno.ren@me.com (W96G7VCMTX)}" .build/debug/CasePilot
