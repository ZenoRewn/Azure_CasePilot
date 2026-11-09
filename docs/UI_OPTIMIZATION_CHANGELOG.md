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
