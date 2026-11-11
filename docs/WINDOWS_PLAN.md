FINAL PLAN: CasePilot for Windows (chief reviewer decision)

## Verified on this machine (2026-11-11)
- `dotnet new wpf` followed by `dotnet build` works offline and produces `W.exe`. It needs no packages.
- There is no WinUI 3 or Windows App SDK template, and no WindowsAppSDK package in the offline feed.
- `api.nuget.org` is unreachable (HTTP 000). The `packagefeedproxy.microsoft.io` source is registered, but nothing depends on it.
- The VS offline feed at `C:\Program Files (x86)\Microsoft SDKs\NuGetPackages\` has `MSTest.TestFramework`/`TestAdapter` 3.1.1 and `Microsoft.NET.Test.Sdk` **17.10.0-preview-24080-01** (that exact version string is required). It has no xUnit.
- An MSTest project restored from that feed alone (`<clear/>` in `nuget.config`) and passed 1 of 1 tests.

## Stack decision
**WPF on .NET 9 with the in-box Fluent theme (`ThemeMode="Light"` plus Mica), a pure C# REST client, zero third-party packages, and MSTest 3.1.1 from the offline feed.**
- **WinUI 3 / MAUI rejected:** they need WindowsAppSDK from NuGet, which cannot be restored here, and single-exe distribution is harder.
- **WebView2 shell rejected:** it isn't native and would need Python bundled with the app.
- **No Python dependency:** the app does what the macOS app does and calls ARM / Microsoft.Support 2024-04-01 directly. `az` is only a token source. This removes the port, Host-check, token and child-process risks.

Conflicts between the proposals, resolved:
- **Theme:** stay light-only, as on macOS. Proposal 2 asked for light and dark; that is dropped. Use theme resources (`DynamicResource`) throughout so dark can be added later.
- **Test framework:** MSTest, because xUnit cannot be restored. Proposal 1's homemade runner is not needed.
- **Unavailable controls:** CommunityToolkit (SettingsCard, Shimmer, DataGrid extras), AutoSuggestBox, InfoBar, NumberBox, SelectorBar and the WinUI TitleBar are not available. Use the WPF equivalents:

  | Proposal 2 control | WPF replacement |
  |---|---|
  | AutoSuggestBox | `TextBox` + `Popup` + `ListBox` |
  | InfoBar | Custom `InfoBar` UserControl (Border, icon, text, action) |
  | SelectorBar | `RadioButton`s styled as a segmented control |
  | NumberBox | `TextBox` with numeric validation |
  | ContentDialog | Owned modal `Window` |
  | DataGrid | In-box WPF `DataGrid` |

- **Title bar:** standard title bar with Mica. Tenant and subscription pickers go in a command bar at the top of the content area. Do not implement a custom title bar.
- **Recording:** `winmm` waveIn P/Invoke, 16 kHz mono WAV. It sits behind `IAudioRecorder`, and only a manual check is possible.
- **Secrets:** Credential Manager via P/Invoke `CredWriteW`/`CredReadW`/`CredDeleteW`, `CRED_TYPE_GENERIC`, persist `LOCAL_MACHINE`.
  - Targets: `CasePilot/sp/<clientId>`, `CasePilot/ai/aoai-key`, `CasePilot/ai/stt-key`.
  - An empty value deletes the entry.
- **Running az:** call `cmd.exe /d /c <az.cmd>` explicitly.
  - Validate every argument first: GUIDs, plus resource URLs on a fixed allow-list.
  - Set `CreateNoWindow`, UTF-8 stdout/stderr and `PYTHONIOENCODING=utf-8`.
  - Timeout 60 s, then `Kill(entireProcessTree:true)`.
  - Resolve az from PATH, falling back to `%ProgramFiles%\Microsoft SDKs\Azure\CLI2\wbin\az.cmd`.

## Solution layout
```
windows/
  CasePilot.Windows.sln
  nuget.config              <clear/> + VS offline feed only
  Directory.Build.props     Nullable enable, TreatWarningsAsErrors, LangVersion latest, NoWarn WPF0001
  src/CasePilot.Core/       net9.0, no UI and no static I/O
    Models/ Text/ Support/ Auth/ AI/ Plan/ Picking/ Abstractions/
  src/CasePilot.Platform/   net9.0-windows: CredentialStore, JsonSettingsStore, AzCli, WaveRecorder, ShellOps
  src/CasePilot.App/        WPF net9.0-windows: App.xaml, MainWindow, Views/, ViewModels/, Controls/ (InfoBar, Pill, MicButton, SegmentedControl), Mvvm/ (ObservableObject, RelayCommand, AsyncRelayCommand)
  tests/CasePilot.Core.Tests/   MSTest
  tests/CasePilot.App.Tests/    MSTest; ViewModels against fakes, no Dispatcher
  build.ps1                 test + publish
```
Test projects reference `MSTest.TestFramework` 3.1.1, `MSTest.TestAdapter` 3.1.1 and `Microsoft.NET.Test.Sdk` 17.10.0-preview-24080-01. ViewModels live in the App project. Tests reach them through `InternalsVisibleTo`, or the ViewModels move to a `net9.0-windows` class library if needed.

## Core API
**Models**
- `AzureCloud` (Global, China, UsGov) with `Arm`, `Login` and `Portal` URLs, plus `FromEnvironmentName`.
- Records: `Tenant`, `Subscription`, `SupportService`, `ProblemClassification`, `AzureResource`.
- `Severity` (enum plus extensions): `Parse` (case-insensitive), `Level`, `Title`, `Detail`, `IsHigh`.
- `CaseKind`: `From(SupportService)`.
- `Contact`: defaults, `IsValid`, `CcList`.
- `QuotaRequest`, `NewCase`.
- `TicketSummary`: `IsOpen`.
- `Communication`: `FromMicrosoft`.
- `TicketDetail`, `RecentProblem`.
- `AppError : Exception` with `RequestId`, `Hint` and `Status`.

**Text**
- `HtmlText.Plain(string)`
- `ReplyHtml.Escape(string)`
- `Jwt.Claims(string) -> IReadOnlyDictionary<string, JsonElement>`
- `FormEncoding.Encode(IDictionary)`

**Support**
- `ArmPaths`: `SupportBase(sub?)`, `ResourcesFilter(types)` (at most 15), `TicketsFilter(days, clock)`.
- `SupportBody.Build(NewCase) -> JsonObject`.
- `PortalLink.For(cloud, id)`.
- `SupportClient(HttpClient, IAuthProvider, IDelay, IIdGenerator)` with these methods:
  - `TenantsAsync`
  - `SubscriptionsAsync(tid)`
  - `QuotaIdAsync(sub)`
  - `ServicesAsync` (cached 6 h)
  - `ProblemsAsync(svc)`
  - `ResourcesAsync(sub, types?)`
  - `CreateAsync(scope, NewCase)`: checkNameAvailability, PUT, poll 40 × 3 s, then GET
  - `TicketsAsync(scope, days)`
  - `DetailAsync(scope, name)`
  - `ReplyAsync(scope, name, body, sender?)`
  - `CloseAsync(scope, name)`: up to 4 attempts with read-back

  Every call takes a `CancellationToken` and sends Bearer plus `x-ms-client-request-id`. Errors come back as `AppError` carrying `x-ms-request-id`. Paged calls follow `nextLink`.

**Auth**
- `IAuthProvider`: `Mode`, `Cloud`, `User`, `HomeTenant`, `GetTokenAsync(tenant?, resource)`.
- `AuthProvider` methods: `LoginCliAsync`, `StartDeviceAsync`/`PollDeviceAsync`, `LoginSpAsync`, `Logout`.
- `TokenCache` keyed on `tenant|resource`, with 300 s skew.
- `IAzCli.RunAsync(string[] args, CancellationToken) -> (int exit, string stdout, string stderr)`.

**AI**
- `AIConfig`: defaults, tolerant JSON, keys never serialized.
- `AIHeaders.For(config, kind, bearer?)`.
- `SttUrls`.
- `Multipart.Build`.
- `AIClient(HttpClient, IAuthProvider, IDelay)`: `ChatAsync(messages, schema)` (429 handling: Retry-After, default 3 s, cap 15 s, 2 retries) and `TranscribeAsync(bytes, filename, mime)`.
- `Shortlist`: `Services(text, catalog)` (at most 60, synonyms plus POPULAR) and `Problems(text, list)` (at most 40).
- `AIDrafter.DraftAsync(text, answers, catalog, problemsLoader, IChat) -> Draft{Service, Problem, Severity, Title, Description, Questions, Ready}`.

**Plan and picking**
- `SupportPlan.Infer(tickets, quotaId) -> {Plan, Tier, AllowsCritical, Source}`.
- `ResourceRanker.Rank`.
- `ServiceSearch.Filter(query, services)` (terms are AND'ed).

**Abstractions**
- `ISecretStore`, `ISettingsStore`, `IClock`, `IDelay`, `IIdGenerator`, `IChat`, `IAudioRecorder`, `IShell` (`Copy`, `OpenUrl`).

## TDD order (red, green, refactor; Tidy commits kept separate)
1. `Severity.Parse`, levels, `IsHigh`.
2. `CaseKind.From`.
3. `Contact` defaults; `IsValid` (phone required when phone is preferred).
4. `SupportBody` quota payload is exactly `{"NewLimit":100,"VMFamily":"DSv5 Series"}`.
5. `SupportBody` sets 24x7 only when severity is high and the toggle is on.
6. `SupportBody`: cc goes to `additionalEmailAddresses`; resource goes to `technicalTicketDetails`; consent is "Yes"/"No"; text is trimmed; ISO UTC start time; empty phone is omitted.
7. `HtmlText.Plain` returns `"你好 Zheng\n\nA & B"`.
8. `ReplyHtml.Escape` (`<&>` escaped, newline becomes `<br>`).
9. `Jwt.Claims` (unpadded base64url; `tid`, `upn`).
10. `FormEncoding` encodes `+` as `%2B`.
11. `ArmPaths`: subscription vs tenant base; filter of at most 15 types; ticket date filter.
12. `PortalLink` per cloud.
13. `ResourceRanker.Rank` prefers "Virtual Machine running Linux".
14. `ServiceSearch` ANDs terms and puts the first match first.
15. `SupportPlan.Infer`: tiers; MSDN gives `allowsCritical=false`; otherwise unknown.
16. `AIConfig`: defaults, old/partial JSON, keys not serialized.
17. `AIHeaders` routing: `api-key`, `Ocp-Apim-Subscription-Key`, Bearer.
18. `SttUrls` and `Multipart` field names.
19. `Shortlist`: services ≤60 keeping hits and popular ones, synonyms; problems ≤40 with ssh first.
20. `AIDrafter`: mapping, hallucinated value becomes null, ≤3 questions in one round, severity fallback, title ≤200, `ready`.
21. `TokenCache`: 300 s reuse, key.
22. `AuthProvider` CLI: parsing; `expires_on` fallback; Chinese tenant name survives UTF-8; missing az gives an `AppError`. Also: `AzCli` rejects non-GUID tenant arguments.
23. `AuthProvider` device code: pending and slow_down; `tid`; refresh-token rotation.
24. `AuthProvider` SP: `.default` scope; a foreign tenant throws.
25. `SupportClient` plumbing: headers, error parsing, `nextLink`.
26. `SupportClient.Create`: async polling succeeded, failed and canceled paths (with `IDelay` recorded).
27. `SupportClient.Close`: redundant action tolerated; read-back; throws after 4 attempts.
28. `SupportClient.Detail`: placeholder when communications fail; newest first; Outbound means Microsoft.
29. `AIClient.Chat`: 429 retry and cap; 401/403 hint.
30. `CredentialStore` round trip under the `CasePilot.Test/` prefix (integration, cleaned up); `JsonSettingsStore` atomic write and corrupt-file recovery.
31. `CreateCaseVM`: checklist gating; 24x7 enablement; `ApplyDraft`; submit clears the form, records the recent problem (max 6) and reloads; over-plan warning.
32. `HistoryVM`: status counts, days, search.
33. `ShellVM` load chain with saved tenant and per-tenant subscription restore.

## UI spec (WPF Fluent Light)
**Window and shell**
- Window: 1280×820 by default, minimum 1024×680. `ThemeMode=Light`; Mica via `WindowBackdropType` on Win11, solid on Win10. Placement is persisted.
- Layout is a 3-column Grid:
  - Left nav, 248 px, Mica: groups 工作台 (新建 Case, 我的 Case + open-count badge) and 设置 (AI 与语音, 联系人, MCP).
  - Content frame on opaque `LayerFill` cards with corner radius 8.
  - Optional detail pane, 420 px by default (360–640) with a GridSplitter.
- Identity card in the nav footer: avatar initials, user, "method · tenant domain" and a plan line. Its ContextMenu holds 切换租户 ▸, 切换订阅 ▸ (with 无·租户级), 认证方式 ▸ and 注销.
- Content command bar: tenant ComboBox, subscription ComboBox and refresh.
- Error InfoBar at the top of the content area: message, Request ID in mono, 复制, 重试. A toast InfoBar dismisses itself after 3 s.

**Typography and colour**
- Fonts: Segoe UI Variable, then Microsoft YaHei UI. Case numbers, codes and JSON use Cascadia Mono, then Consolas.
- Type sizes: page title 28 semibold, section 20, label 14 semibold, body 14, caption 12 secondary. Nothing below 12.
- Pills: 22 px tall, always with a text label. Status 处理中 is green; 已关闭 is grey. Severity A/A+ is red, B orange, C grey.

**Login**
- Centered 480 px card on an empty Mica background, with a segmented control: Azure CLI | 浏览器登录 | 应用注册. Cloud ComboBox.
- CLI: silent attempt on launch with a progress indicator; on failure a warning InfoBar with 重试.
- Device code: code in Mono 32, copied automatically, plus a link to the verification page and a cancel button.
- SP: Tenant and Client fields with GUID validation, a PasswordBox for the secret, a "保存到凭据管理器" checkbox (checked by default), and a role hint. Enter triggers the tab's primary action.

**新建 Case** (ScrollViewer, max width 760, centered; cards in this order)
1. AI 帮写:
   - Model·auth button that opens AI settings.
   - Multi-line input with a mic button and 生成草稿 (Ctrl+Enter).
   - Q&A answer fields with 补充并更新 / 跳过.
   - Error InfoBar with an 打开 AI 设置 action.
2. 服务与问题:
   - Service search popup (Up/Down/Enter) plus a 从资源选择 dialog.
   - Up to 6 quick chips and up to 3 recent items.
   - Problem ComboBox. Quota cases auto-pick "compute-vm (cores-vcpus)".
   - Quota sub-card (region, VM family, new limit) for quota cases.
3. 严重性: C/B/A/A+ segmented control with the selected level's caption, an over-plan warning InfoBar, and the 24x7 toggle (high severity only).
4. 描述:
   - Title with a counter out of 200.
   - 插入模板 drop-down with the 【…】 sections.
   - Description box (min height 200) with a mic button.
   - Technical cases only: resource picker.
   - Optional start time (DatePicker plus time TextBox) and the diagnostics-consent toggle.
5. 联系人: summary line with an 编辑 link. The card shows a red border when the contact is invalid.

The bottom bar is pinned, 64 px and opaque. On the left it shows checklist progress ("还差 N 项…"). On the right are 预览请求体 (a mono JSON dialog with 复制) and 提交 Case (accent button, Ctrl+Enter, disabled until the checklist is complete). Submit opens a confirmation dialog summarising the case, with the plan warning; Cancel is the default button. Success shows the Case # with 复制 and 在门户中打开.

**我的 Case**
- Header: segmented filter with counts (全部 n | 处理中 n | 已关闭 n), a days ComboBox (7/30/90/365), search (Ctrl+F) and refresh.
- DataGrid columns:
  - Case #: mono, secondary, fixed 150 px.
  - Title: with a "service · problem" subtitle.
  - Severity pill.
  - Status pill.
  - Created: relative time with the absolute time in a tooltip; sortable.
- Alternating rows. Context menu: 复制 Case 号, 在 Portal 打开, 关闭 Case…. Enter or double-click opens the detail pane.
- Empty state and loading state are included.

**Detail pane**
- Header: Case # on one line (mono 16) with copy; Portal icon button with a tooltip; 关闭 Case with confirmation.
- Below the header: the title and pills.
- Tabs: 概览 (key/value grid: service, problem, created, modified, SLA, plan, engineer, 24x7, contact, language/time zone, resource, quota) and 沟通记录 (n), newest first, plain text, selectable. Microsoft messages get a left accent bar.
- Reply footer: input, mic, 发送 (Ctrl+Enter). Disabled when the case is closed.

**Settings pages (in-shell)**
- AI 与语音:
  - Chat: Endpoint, Deployment, Key PasswordBox. Empty key means Entra ID.
  - Speech-to-text: provider ComboBox and the fields that depend on it.
  - 恢复默认 button. A stored key shows as "已保存 ●●●●" with 清除.
- 联系人: fields save automatically; enum ComboBoxes for language, country and time zone.
- MCP: copyable `mcpServers` JSON whose `command` is the absolute path to the az-bundled `python.exe`, plus a note that writes require `confirm=true`.

**Mic button**
- Idle E720 → recording (red tint, timer) → ProgressRing while transcribing.
- The result is inserted at the caret.
- If access is denied, an InfoBar offers to open `ms-settings:privacy-microphone`.

**Keyboard and accessibility**
- Shortcuts: Ctrl+N, Ctrl+R/F5, Ctrl+Enter, Ctrl+,, Ctrl+F, Ctrl+1/2, Esc (closes the pane or dialog).
- `AutomationProperties.Name` (Chinese) on every icon button. Tab order follows the visual order. Live-region announcements for the device code and toasts.

## Build, verification and packaging
```
cd windows
dotnet restore CasePilot.Windows.sln      # offline feed only
dotnet build  -c Release
dotnet test   tests/CasePilot.Core.Tests tests/CasePilot.App.Tests
dotnet publish src/CasePilot.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```
- **Runtime-pack check:** a self-contained `win-x64` publish needs the runtime pack. If it isn't available offline, fall back to a framework-dependent publish (`--self-contained false`).
- `build.ps1` wraps all of the above.
- **DEBUG `--selftest`:** read-only only. Steps: CLI login, tenants, subscriptions, services, problems, tickets(30), newest ticket detail, plan, and `SupportBody.Build` preview. It never runs create, reply or close.
- **`--snapshot <dir>`:** renders each page to PNG via `RenderTargetBitmap` for UI review against `docs/ui-review/after`.

**Web-on-Windows track** (Python; each fix gets a test first, separate commits):
- `_run_az`: `encoding="utf-8"`, `CREATE_NO_WINDOW`, kill the process tree on timeout.
- Server: `allow_reuse_address=False` on Windows.
- stdio MCP: reconfigure stdout to UTF-8.
- CSS `.mono`: add `"Cascadia Mono", Consolas`.
- `start.bat`: `chcp 65001` and a fallback to the az-bundled python.
- Then a read-only local-mode smoke test in Edge.

## Risks and mitigations
1. **Fluent theme and `WindowBackdropType` are experimental in .NET 9 (WPF0001):** suppress the warning and fall back to a solid background on Win10. Some Fluent control templates are incomplete, so keep custom styles minimal.
2. **WPF has no InfoBar, AutoSuggestBox or segmented control:** small in-house controls. Budget for them and test them via snapshots.
3. **Self-contained publish may need a runtime pack that is missing offline:** use the framework-dependent fallback.
4. **Running az through cmd.exe allows metacharacter injection:** strict argument validation (GUIDs, allow-listed resource URLs) plus a test.
5. **The device flow reuses the Azure CLI public client ID:** the same choice as macOS and Python. The refresh token is kept in memory only.
6. **The waveIn recorder can't be unit-tested:** keep it behind `IAudioRecorder` and verify by hand. WAV is accepted by both STT providers. Check the 14 MiB limit (about 7 minutes at 16 kHz).
7. **Logic is triplicated across Python, Swift and C#:** golden-value tests are shared across all three.
8. **No real writes allowed in this environment:** create, reply and close are tested only through fakes and the dry-run preview.
9. **Unsigned exe:** SmartScreen will warn on first run. Signing is deferred.
10. **No packages, so no CommunityToolkit:** hand-written `ObservableObject`, `RelayCommand` and `AsyncRelayCommand` (~80 lines), covered by tests.

The spike files are in `C:\Users\zhengyuren\AppData\Local\Temp\cpspike` and can be deleted. The proposed project root is `C:\Users\zhengyuren\ClaudeCode\Azure_CasePilot\windows\`.