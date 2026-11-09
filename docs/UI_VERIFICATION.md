# UI Verification
- 构建: `swift build` / `./build_app.sh v2.1.0` 成功 (Universal, Apple Development 签名)
- 测试: `swift test` 16/16 通过; 服务端 `python3 -m unittest discover -s tests` 全部通过
- 截图: 前后对比 `docs/ui-review/before|after/` (真实窗口 cacheDisplay 截图, 1280×820, 浅色)
- 回归: 导航、AI 帮写、提交按钮状态、Inspector 详情加载已在截图中确认
- **未验证**: 右键菜单与双击 (无自动化点击); 设置页 Key 实际调用 (你的 AI 资源均禁用本地认证, 只能验证错误 Key 返回 401); 深色模式 (按要求固定浅色)
