# UI/UX Audit (v2.0.0 基线)
截图: `docs/ui-review/before/`。优先级 P0 阻碍使用 / P1 主要体验 / P2 细节。

| # | 级别 | 界面 / 代码 | 问题 |
|---|---|---|---|
| 1 | P1 | 我的 Case `HistoryView.swift` 统计卡片 | 4 张统计卡与下方状态筛选信息重复, 占用 ~70pt 纵向空间 |
| 2 | P1 | 我的 Case 表格 Case 列 | 打开 Inspector 后 Case 号被截断 (2611090030003…), 无法辨认 |
| 3 | P1 | 详情头 | Case 号换行 (…302\n5); "Portal" 按钮占位过大 |
| 4 | P1 | 表格交互 | 无右键菜单 (复制 Case 号 / 打开 Portal), 不符合 Mac 习惯 |
| 5 | P2 | 新建 Case 快捷入口 | 常用服务 + 最近使用两组 chips 共 9 个, 与 AI 区块争夺注意力 |
| 6 | P2 | 设置 → AI 与语音 | 无法配置 Key / Speech 区域, 未授权 RBAC 的用户无法使用 AI |
| 7 | P2 | 表格 | Case 号与标题同权重, 主次不分 |

保留不改: NavigationSplitView + Inspector 结构、grouped Form、工具栏提交 (⌘↩)、身份卡菜单、浅色外观 (用户要求)。
