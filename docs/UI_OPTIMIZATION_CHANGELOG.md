# UI Optimization Changelog (v2.1.0)
| 问题 | 修改 | 文件 | 风险 |
|---|---|---|---|
| #1 统计重复 | 移除统计卡片, 计数并入状态分段 | HistoryView.swift | 低 (`Stat` 组件保留未用) |
| #2 Case 号截断 | 列宽 135–140, fixedSize | HistoryView.swift | 低 |
| #3 详情头 | Case 号单行; Portal 改为图标按钮 + tooltip | HistoryView.swift | 低 |
| #4 右键菜单 | 复制 Case 号 / 在 Portal 打开; 双击打开详情 | HistoryView.swift | 低 |
| #5 快捷入口 | 常用 ≤6、最近使用 ≤3, 改为小标题样式 | CreateCaseView.swift | 低 |
| #6 Key 配置 | AI/Speech Key (钥匙串)、Speech 区域 | SettingsView.swift, AI.swift, AIClient.swift, Keychain.swift | 中: 新增设置字段, 已兼容旧配置解码 (有测试) |
