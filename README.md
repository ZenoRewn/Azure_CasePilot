# Azure CasePilot

<img src="static/logo.png" width="96" alt="CasePilot logo">

本地一键创建 Azure Support Case 的 Web 工具。零第三方依赖 (Python 3.9+ 标准库), 直接调用 ARM `Microsoft.Support` REST API (2024-04-01)。

## 原生 macOS App (推荐, v1.1.0+)
SwiftUI 原生应用 (`macos/`), Universal (Apple Silicon + Intel), 约 1.2MB, macOS 14+。
到 [Releases](https://github.com/ZenoRewn/Azure_CasePilot/releases) 下载 `CasePilot-*-macOS-universal.zip`, 解压双击即可。
关闭窗口或 ⌘Q 即完全退出, 无后台服务。

### 首次打开 (必做一次)
App 未经 Apple 公证, 下载后 macOS 会提示"已损坏"或"无法验证开发者"。解压后在终端执行:
```bash
xattr -dr com.apple.quarantine ~/Downloads/CasePilot.app   # 按实际路径修改
```
之后双击即可正常打开。
支持 Azure CLI、浏览器 (设备码)、App Registration 三种登录。App Registration 的 Tenant/Client ID 保存在本机, Secret 保存在 macOS 钥匙串, 下次自动填充; 需在订阅上授予 Support Request Contributor 与 Reader 角色。
构建: `cd macos && ./build_app.sh v1.1.0`; 测试: `swift test`。

## Web 版 (跨平台, 下载即用 macOS Apple Silicon)
到 [Releases](https://github.com/ZenoRewn/Azure_CasePilot/releases) 下载 `CasePilot-*-macOS-arm64.zip`, 解压后双击 `CasePilot.app`。
App 未签名, 首次打开如被拦截: 右键 → 打开, 或执行 `xattr -dr com.apple.quarantine CasePilot.app`。

## 从源码启动 (前提: Python 3.9+, 推荐已安装并 `az login` 的 Azure CLI)
- macOS: 双击 `start.command` (或终端 `./start.sh`)
- Windows: 双击 `start.bat`

无需 pip install。
自动打开 http://127.0.0.1:8765 。端口可通过 `CASEPILOT_PORT` 修改。

## 认证
| 方式 | 前提 | 说明 |
|---|---|---|
| Azure CLI (默认, 页面打开自动尝试) | 已 `az login` | 支持切换到 CLI 已登录过的任意租户; 若提示失败执行 `az login --tenant <id>` |
| 浏览器登录 (设备码) | 无需安装 CLI | 在浏览器中用工作账号登录, 支持 MFA |
| App Registration | Client ID + Secret | SP 需在目标订阅具备 **Support Request Contributor** 角色; Secret 仅存进程内存 |

支持 Azure Global / China (21V) / US Gov (CLI 模式按 `az cloud` 当前环境自动识别)。

## 功能
- **新建 Case**: 技术问题 / 配额申请 (Compute vCPU 结构化表单) / 计费订阅 三类入口; 服务搜索 + 常用服务快捷选择 + 从订阅资源反查服务; 结构化描述模板; 提交前检查清单; 请求体预览。
- **默认中文 / 中国**: 支持语言 zh-hans、国家 CHN、时区 China Standard Time, 可修改并本地记忆。
- **我的 Case**: 统计、搜索、按状态/时间筛选; 详情抽屉含属性、SLA、沟通记录时间线; 发送补充信息; **关闭 Case**。
- **支持计划提示**: 根据该订阅最近 Case 的 supportPlanType 推断, 选择 A/A+ 时提示可能不支持。

## 注意
- 严重性 A 需要 Standard 及以上支持计划; "业务关键"需 ProDirect / Unified。无付费支持计划的订阅只能创建计费/订阅管理/配额类 Case。
- 仅监听 127.0.0.1, API 需每次启动随机生成的会话令牌并校验 Host 头 (防 CSRF / DNS rebinding)。
- python.org 版 macOS Python 若无 CA 证书: 安装 `certifi` 或运行 `Install Certificates.command`; 程序也会回退读取系统根证书。

## 测试
```bash
python3 -m unittest discover -s tests
```

## 构建 macOS App
```bash
python3 -m pip install pyinstaller certifi
./build_mac.sh v1.0.0      # 产物: dist/CasePilot-v1.0.0-macOS-<arch>.zip
```

## 已知限制 / 待办
- **云环境**: 原生 App 仅支持 Azure Global。
- **Windows**: 原生 App 仅 macOS; Windows 用户可使用 Web 版 (`start.bat`, 需 Python 3.9+), 但 **尚未在 Windows 上实测**。
- **签名公证**: 目前为 ad-hoc 签名, 首次打开需执行 `xattr`。要免除此步需 Apple Developer Program 的 "Developer ID Application" 证书 + `notarytool` 公证。
