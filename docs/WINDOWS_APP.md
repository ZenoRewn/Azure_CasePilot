# CasePilot for Windows (原生 App)

`windows/` 下是 WPF (.NET 9) 原生 App, 功能对齐 macOS 版 (`macos/`)。它直接调用 ARM / Microsoft.Support 2024-04-01 REST API, 不依赖 Python。

## 功能
- **登录**: 支持 Azure CLI (复用 `az login`)、浏览器设备码、App Registration (Client ID + Secret) 三种方式。Secret 存放在 Windows 凭据管理器 (`CasePilot/sp/<clientId>`)。
- **切换范围**: 顶部命令栏可切换 租户 / 订阅。
- **支持计划**: 根据最近的 Case 或订阅类型推断。
- **新建 Case**: 依次选择 服务 → 问题类型 → 严重性 (C/B/A/A+), 再填写标题与描述, 可预览请求体后提交。
  - 也可从快捷入口、最近使用、订阅资源反查进入。
  - 支持配额表单。
- **AI 帮写与语音输入**: 由 Azure OpenAI 或 Azure Speech 提供, 通过 winmm 录音 (16 kHz 单声道)。语音为**流式**: 录音中输入框实时显示识别结果, 停止后替换为最终文本:
  - Azure OpenAI: Realtime 转写 (`wss://<资源>/openai/v1/realtime?intent=transcription`, 部署 `gpt-live-transcribe`, 16 kHz 重采样为 24 kHz)。
  - Azure Speech: 实时识别 WebSocket 协议 (无需 SDK) + Post-Stream Refinement (PSR); 先经 `/sts/v1.0/issueToken` 换取 Speech 令牌, 区域取设置或令牌中的 region。
  - 流式失败 (连接/认证/超时) 时自动退回整段转写 (`gpt-4o-mini-transcribe` / Speech 快速转写)。协议逻辑见 `CasePilot.Core/AI/Streaming.cs`、`StreamingTranscriber.cs`。
 AI / STT Key 存放在凭据管理器 (`CasePilot/ai/aoai-key`、`CasePilot/ai/stt-key`)。
- **我的 Case**: 列表、筛选、详情 (含 SLA 与沟通记录)、补充信息、关闭 Case。
- **联系人设置**与 **MCP 配置**页面。
- **界面**: WPF 内置 Fluent 主题 (Light) + Mica, 仅浅色。

## 架构
| 项目 | 目标框架 | 职责 |
|---|---|---|
| `src/CasePilot.Core` | net9.0 | 纯逻辑: 模型、Support API 客户端、认证、AI、计划推断、选择逻辑。无 UI, 不做静态 I/O |
| `src/CasePilot.Platform` | net9.0-windows | 平台实现: 凭据管理器 (P/Invoke CredWriteW 等)、JSON 设置、`az` 调用 (参数校验, 不弹出窗口, UTF-8, 60s 超时后结束整个进程树)、录音、Shell 操作 |
| `src/CasePilot.App` | net9.0-windows (WPF) | 视图 (Views)、ViewModel、控件 (InfoBar / Pill / MicButton / SegmentedControl)、MVVM 基础类 |
| `tests/*.Tests` | MSTest 3.1.1 | Core / Platform / App(ViewModel, 使用 fake) 单元测试 |

项目零第三方运行时依赖。NuGet 源: VS 离线源 + 微软内部代理源 `packagefeedproxy.microsoft.io` (仅用于获取 win-x64/arm64 运行时包以自包含发布)。

## 构建 / 测试 / 发布
```powershell
cd windows
dotnet build CasePilot.Windows.sln -c Release
dotnet test  CasePilot.Windows.sln -c Release --no-build
$env:CASEPILOT_LIVE=1; dotnet test CasePilot.Windows.sln -c Release --no-build   # 只读在线测试, 需先 az login
src\CasePilot.App\bin\Release\net9.0-windows\CasePilot.exe --selftest            # 只读冒烟: 登录/租户/订阅/目录/工单; 请求体只预览, 不提交
src\CasePilot.App\bin\Debug\net9.0-windows\CasePilot.exe --snapshot <dir>        # 生成 UI 截图 (docs/ui-review/windows)
powershell -ExecutionPolicy Bypass -File build.ps1                                # restore + build + test + publish 到 windows\dist
```
Python / Web 端测试: `python -m unittest discover -s tests`。

## 已知限制
- **发布形态**: 自包含单文件 exe (内置 .NET 9 运行时, 压缩后约 60MB), 用户解压双击即可, 无需安装 .NET。构建需能访问 NuGet 源以获取运行时包。
- **签名**: 未做代码签名, SmartScreen 可能会提示。
- **主题与云环境**: 仅支持浅色主题; 仅在 Azure Global 下验证过。
- **未自动化覆盖的功能**:
  - 录音只能人工验证。
  - 浏览器设备码登录需要人工输入验证码。
  - 创建 / 回复 / 关闭 Case 只经过 fake 测试与请求体预览, 没有向真实 Azure 提交。
- **标题栏**: 使用标准标题栏, 没有自定义标题栏。
- **AI 帮写 (Entra ID)**: 未填 Key 时以 az 默认租户取令牌。若 AI 资源属于其他租户, 服务返回 400 "does not match resource tenant", App 会提示填写 Key 或换用本租户 Endpoint。2026-11-11 用默认配置实测即为此情况 (只读测试 `CASEPILOT_LIVE=1 dotnet test tests/CasePilot.Platform.Tests --filter TestCategory=Live`, 遇此情况记为 Inconclusive)。

### 2026-11-11 已修复
- 占位提示: 窗口内容自带 AdornerDecorator, 搜索框 / 补充信息框等占位在应用与截图中都能显示; 占位在加载、可见性或尺寸变化时自动补挂。
- 页面级错误条 (加载失败 / Toast) 移到各页面标题下方, 标题位置不再随错误下移。
- 详情面板: "关闭 Case" 与面板关闭按钮之间改用清晰可见的竖向分隔线。
- 我的 Case: 列表宽度不足 (如详情面板打开) 时隐藏 "创建时间" 列; Case 号固定列宽并带提示, 标题带完整提示。
- Python 测试在 `-W error::ResourceWarning` 下无告警 (关闭 HTTPError 响应流、测试服务器 socket 与文件句柄)。
- 删除未使用的 `PlatformInfo.cs` 占位文件。

## 真实端到端测试 (会创建真实 Case)
```bash
cd windows
CASEPILOT_LIVE_WRITE=1 dotnet test tests/CasePilot.Platform.Tests --filter TestCategory=LiveWrite
```
用当前 `az login` 身份在第一个订阅下创建一个 C 级 `[TEST]` 计费类 Case → 中文回复 → 读取详情 → 关闭并回读确认为 Closed。
2026-11-11 实测通过 (Case 2611110040001315 / 1332 / 1340, 均已关闭)。
此次实测发现: 回复正文不应做 HTML 转义 (Azure 会包 `<pre>` 并剥离 `;` 与 `<>`, 转义后显示为 `&ltb&gt`), Windows 与 Mac 版均已改为纯文本。

## AI 帮写真实测试
```bash
CASEPILOT_LIVE=1 CASEPILOT_AI_ENDPOINT=https://<资源>.cognitiveservices.azure.com CASEPILOT_AI_DEPLOYMENT=<部署> \
  dotnet test tests/CasePilot.Platform.Tests --filter "FullyQualifiedName~LiveAITests"
```
2026-11-11 用本租户 eastus2-foundry-service / gpt-4.1-mini (Entra 身份, Cognitive Services User) 实测通过。
实测发现: 非推理模型不支持 `reasoning_effort`, 现在会自动去掉该参数重试。

## 语音转写真实测试
```bash
CASEPILOT_LIVE=1 CASEPILOT_SPEECH_ENDPOINT=https://<资源>.cognitiveservices.azure.com CASEPILOT_STT_LANG=en-US \
  CASEPILOT_STT_WAV=<16kHz WAV> dotnet test tests/CasePilot.Platform.Tests --filter "FullyQualifiedName~LiveSttTests"
```
2026-11-11 用 westus3-azureai-service 实测两种流式 (Entra 身份, 中文): AOAI `gpt-live-transcribe` 与 Speech PSR 均返回实时中间结果与正确最终文本 (Core 层在 macOS 上运行)。
同日用 eastus2-foundry-service 的 Azure Speech (Entra 身份, 区域留空、Endpoint 填资源域名) 实测转写正确。
`southeastasia-speech` 禁用了 Key 认证 (disableLocalAuth), 只能用 Entra 身份 + 自定义域名 Endpoint。
在 App 中使用: 设置 → AI 与语音 → 语音提供方 Azure Speech, 区域留空, Endpoint `https://eastus2-foundry-service.cognitiveservices.azure.com`, Key 留空。
