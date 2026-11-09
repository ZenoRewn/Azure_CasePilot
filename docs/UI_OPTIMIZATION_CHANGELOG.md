# UI Optimization Changelog (v2.1.0)
| 问题 | 修改 | 文件 | 风险 |
|---|---|---|---|
| #1 统计重复 | 移除统计卡片, 计数并入状态分段 | HistoryView.swift | 低 (`Stat` 组件保留未用) |
| #2 Case 号截断 | 列宽 135–140, fixedSize | HistoryView.swift | 低 |
| #3 详情头 | Case 号单行; Portal 改为图标按钮 + tooltip | HistoryView.swift | 低 |
| #4 右键菜单 | 复制 Case 号 / 在 Portal 打开; 双击打开详情 | HistoryView.swift | 低 |
| #5 快捷入口 | 常用 ≤6、最近使用 ≤3, 改为小标题样式 | CreateCaseView.swift | 低 |
| #6 Key 配置 | AI/Speech Key (钥匙串)、Speech 区域 | SettingsView.swift, AI.swift, AIClient.swift, Keychain.swift | 中: 新增设置字段, 已兼容旧配置解码 (有测试) |

# v2.2.0
| 问题 | 修改 | 文件 |
|---|---|---|
| AI/语音配置入口难找 | 侧边栏新增「设置」分组 (AI 与语音 / 联系人 / MCP) 直达对应页; AI 帮写标题旁显示「模型 · 认证方式」按钮; AI 报错时出现「配置 AI」按钮; 设置默认打开 AI 页 | CasePilotApp.swift, AIComposer.swift |
| 缺少 macOS 玻璃质感 | 窗口背景系统材质 (macOS 15+), 侧边栏/表单透出背景; 身份卡与登录卡使用 Liquid Glass (macOS 26+, 低版本回退 regularMaterial)。不对每张卡片铺玻璃, 保证可读性 | Components.swift, CasePilotApp.swift, CreateCaseView.swift, LoginView.swift |

# v2.3.0 · Liquid Glass 系统性校正
原则: **玻璃属于浮于内容之上的导航/控件层, 内容层必须是不透明、可读的表面** (Apple HIG: Liquid Glass 用于 toolbar、sidebar、浮层控件)。
| 问题 (v2.2.0) | 根因 | 修改 |
|---|---|---|
| 整个内容区呈灰色毛玻璃, 文字对比度下降 | 给 `.window` 容器加了 thinMaterial, 并隐藏了 Form 背景 | 移除窗口材质与 Form 背景隐藏; 内容恢复系统 grouped 表面 |
| 描述框是突兀的白块 | TextEditor 自带不透明背景, 在透明表单上显得孤立 | TextEditor 隐藏自身背景, 融入 Form 行 |
| 侧边栏身份卡自定义玻璃与系统侧边栏玻璃叠加 | 玻璃叠玻璃 | 侧边栏交还系统 (macOS 26 自动 Liquid Glass), 身份卡用 `.fill.quaternary` |
| 保留 | — | 工具栏、侧边栏、Inspector 由系统渲染 Liquid Glass; 仅登录卡 (浮于空背景) 使用 `glassEffect` |
清理: 删除未使用组件 `Card` / `FieldLabel` / `LabeledField` / `Stat`、窗口材质扩展、PyInstaller 打包脚本 `build_mac.sh` 及其路径逻辑。
