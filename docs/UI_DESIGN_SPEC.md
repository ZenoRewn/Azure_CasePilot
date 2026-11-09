# UI Design Spec
方向: Native macOS × Quiet Sophistication。系统控件优先, 不引入自定义卡片/渐变。
- 布局: NavigationSplitView (侧边栏 210–280) + 内容 + Inspector (360–640); 表单 `formStyle(.grouped)` 最大宽 720 居中
- 字体: 系统字体; Case 号 / ID 使用 `.monospaced()` + `.secondary` 降权; 标题 `.medium`
- 颜色: 系统语义色; 状态 绿=处理中, 次要=已关闭; 严重性 红=A/A+, 橙=B
- 信息密度: 统计并入筛选分段控件 ("处理中 1"), 不单独占行
- 交互: 每个主操作有快捷键 (⌘N 新建, ⌘R 刷新, ⌘↩ 提交/发送, ⌘, 设置); 表格支持右键与双击
- 敏感信息: Key 使用 SecureField, 存钥匙串
