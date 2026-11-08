# Azure CasePilot

<img src="static/logo.png" width="96" alt="CasePilot logo">

在 Mac 上快速创建和管理 Azure Support Case 的原生 App。

## 下载安装 (推荐)
1. 到 [Releases](https://github.com/ZenoRewn/Azure_CasePilot/releases/latest) 下载 `CasePilot-*-macOS-universal.zip` 并解压
2. **首次打开前必须执行一次** (App 未经 Apple 公证, 否则会提示"已损坏"或"无法验证开发者"):
   ```bash
   xattr -dr com.apple.quarantine ~/Downloads/CasePilot.app   # 按实际路径修改
   ```
3. 双击 `CasePilot.app`

要求 macOS 14+, Universal (Apple Silicon + Intel), 约 1.2MB。关闭窗口或 ⌘Q 即完全退出, 无后台服务。

## 登录
| 方式 | 前提 | 说明 |
|---|---|---|
| Azure CLI (默认, 自动尝试) | 已 `az login` | 复用本机 CLI 会话 |
| 浏览器登录 (设备码) | 无 | 在浏览器输入验证码, 支持 MFA |
| App Registration | Client ID + Secret | 需在订阅上授予 **Support Request Contributor** 与 **Reader**; Tenant/Client ID 存本机, Secret 存钥匙串, 下次自动填充 |

点击左下角**身份卡**可随时切换 租户 / 订阅 (含租户级) / 认证方式。

## 功能
- **新建 Case**: 选服务 → 问题类型 → 严重性 (C/B/A/A+) → 标题与描述 → 提交 (⌘↩)
  - 快捷入口: 配额申请、计费/订阅、Azure OpenAI、Microsoft Foundry 及最近使用的服务
  - 「最近使用」: 提交过的 服务 + 问题类型 一键复用
  - 从订阅资源反查服务; 结构化描述模板; 请求体预览
  - 「更多选项」: 关联资源、开始时间、24×7、诊断授权
  - Compute vCPU 配额结构化表单
- **联系人**: 在 设置 (⌘,) 中维护, 默认 简体中文 / 中国 / China Standard Time
- **我的 Case**: 统计、搜索、状态与时间筛选; 详情 (属性、SLA、描述、沟通记录); 补充信息; 关闭 Case (回读确认)
- **支持计划提示**: 根据该订阅最近 Case 推断

## 从源码构建
```bash
cd macos
swift test                    # 单元测试
./build_app.sh v1.4.1         # 产物: dist/CasePilot-v1.4.1-macOS-universal.zip
```

## Web 版 (备选, 跨平台)
仓库根目录的 `server.py` + `static/` 为早期 Web 版 (仅需 Python 3.9+, 零依赖):
macOS 双击 `start.command`, Windows 双击 `start.bat`, 浏览器自动打开 http://127.0.0.1:8765 。
测试: `python3 -m unittest discover -s tests`。新功能仅在原生 App 中维护。

## 已知限制 / 待办
- **云环境**: 原生 App 仅支持 Azure Global。
- **Windows**: 原生 App 仅 macOS; Web 版 `start.bat` **尚未在 Windows 上实测**。
- **签名公证**: 目前为 ad-hoc 签名, 首次需执行 `xattr`。免除此步需 Apple Developer Program 的 "Developer ID Application" 证书 + `notarytool` 公证。
- **浏览器登录**: 需人工输入验证码, 尚未完整人工验证。

## 安全
凭据仅在本机 (内存 / 钥匙串), App 直接调用 Azure ARM `Microsoft.Support` API (2024-04-01), 不经过任何第三方服务。
