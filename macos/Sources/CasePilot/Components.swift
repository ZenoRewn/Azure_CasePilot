import SwiftUI

struct Badge: View {
    let text: String
    var tint: Color = .secondary
    var dot = false
    var body: some View {
        HStack(spacing: 5) {
            if dot { Circle().fill(tint).frame(width: 6, height: 6) }
            Text(text)
        }
        .font(.caption.weight(.medium)).padding(.horizontal, 8).padding(.vertical, 3)
        .foregroundStyle(tint).background(tint.opacity(0.12), in: Capsule())
    }
}

struct FlowChips: View {
    let items: [String]
    var onTap: (String) -> Void
    var body: some View {
        FlowLayout(spacing: 6) {
            ForEach(items, id: \.self) { i in
                Button(i) { onTap(i) }.buttonStyle(ChipStyle())
            }
        }
    }
}

struct ChipStyle: ButtonStyle {
    @State private var hover = false
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.font(.caption).padding(.horizontal, 10).padding(.vertical, 5)
            .foregroundStyle(hover ? Color.accentColor : .secondary)
            .background(hover ? Color.accentColor.opacity(0.08) : Color.clear, in: Capsule())
            .overlay(Capsule().strokeBorder(hover ? Color.accentColor.opacity(0.4) : Color.secondary.opacity(0.3)))
            .opacity(configuration.isPressed ? 0.7 : 1)
            .onHover { hover = $0 }
            .animation(.easeOut(duration: 0.15), value: hover)
    }
}

struct FlowLayout: Layout {
    var spacing: CGFloat = 6
    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(proposal.width ?? .infinity, subviews)
        return CGSize(width: proposal.width ?? rows.map(\.width).max() ?? 0, height: rows.last.map { $0.y + $0.height } ?? 0)
    }
    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        for row in arrange(bounds.width, subviews) {
            for (i, x) in zip(row.items, row.xs) {
                subviews[i].place(at: CGPoint(x: bounds.minX + x, y: bounds.minY + row.y), proposal: .unspecified)
            }
        }
    }
    private struct Row { var items: [Int] = [], xs: [CGFloat] = [], width: CGFloat = 0, height: CGFloat = 0, y: CGFloat = 0 }
    private func arrange(_ maxW: CGFloat, _ subviews: Subviews) -> [Row] {
        var rows = [Row()], x: CGFloat = 0, y: CGFloat = 0
        for (i, v) in subviews.enumerated() {
            let s = v.sizeThatFits(.unspecified)
            if x > 0 && x + s.width > maxW { y += rows[rows.count - 1].height + spacing; rows.append(Row(y: y)); x = 0 }
            rows[rows.count - 1].items.append(i); rows[rows.count - 1].xs.append(x)
            rows[rows.count - 1].height = max(rows[rows.count - 1].height, s.height)
            x += s.width + spacing; rows[rows.count - 1].width = x
        }
        return rows
    }
}

func severityBadge(_ s: String) -> Badge {
    guard let sev = Severity(api: s) else { return Badge(text: s.isEmpty ? "-" : s) }
    let tint: Color = sev.isHigh ? .red : sev == .moderate ? .orange : .secondary
    return Badge(text: "\(sev.level) \(sev.title)", tint: tint)
}

func statusBadge(_ s: String) -> Badge {
    let open = s.lowercased() == "open"
    return Badge(text: open ? "处理中" : s == "Closed" ? "已关闭" : s, tint: open ? .green : .secondary, dot: true)
}

extension Date {
    var relative: String {
        let f = RelativeDateTimeFormatter(); f.locale = Locale(identifier: "zh_CN"); f.unitsStyle = .short
        return f.localizedString(for: self, relativeTo: Date())
    }
    var full: String { formatted(.dateTime.year().month(.twoDigits).day(.twoDigits).hour().minute().locale(Locale(identifier: "zh_CN"))) }
}

/// Liquid Glass 只用于浮于内容之上的控件 (macOS 26+); 低版本回退系统材质。内容区不使用
extension View {
    @ViewBuilder func glassCard(cornerRadius: CGFloat = 16) -> some View {
        if #available(macOS 26.0, *) {
            self.glassEffect(.regular, in: RoundedRectangle(cornerRadius: cornerRadius))
        } else {
            self.background(.regularMaterial, in: RoundedRectangle(cornerRadius: cornerRadius))
        }
    }
}

/// 打开设置并跳到指定页
enum SettingsTab: String { case contact, ai, mcp }
extension Notification.Name { static let selectSettingsTab = Notification.Name("selectSettingsTab") }

/// 支持计划徽章: 已知为蓝色, 未知为灰色, 所选严重性不被支持时为橙色; 悬停显示来源
struct PlanBadge: View {
    let plan: SupportPlan
    var warn = false
    var body: some View {
        if let name = plan.name {
            Badge(text: name, tint: warn ? .orange : .accentColor, dot: true)
                .help(warn ? "当前支持计划可能不支持所选严重性 · \(plan.sourceLabel)" : plan.sourceLabel)
        } else {
            Badge(text: "未知", tint: .secondary)
                .help("Azure 无公开查询 API: 根据该订阅最近的 Case 或订阅类型推断。租户级或没有历史 Case 时无法识别。")
        }
    }
}

/// 侧边栏顶部的主操作: 醒目的「新建 Case」
struct PrimaryCreateButton: View {
    var action: () -> Void
    var body: some View {
        Button(action: action) {
            Label("新建 Case", systemImage: "plus").font(.body.weight(.semibold)).frame(maxWidth: .infinity)
        }
        .buttonStyle(.borderedProminent).controlSize(.large)
        .keyboardShortcut("n")
        .help("新建 Support Case (⌘N)")
    }
}

extension View {
    /// 可提交时为蓝色强调按钮, 禁用时系统自动变灰; macOS 26 工具栏中 borderedProminent 会被渲染为普通玻璃按钮, 需用 glassProminent
    @ViewBuilder func prominentSubmit() -> some View {
        if #available(macOS 26.0, *) {
            self.buttonStyle(.glassProminent).tint(.accentColor)
        } else {
            self.buttonStyle(.borderedProminent).tint(.accentColor)
        }
    }
}
