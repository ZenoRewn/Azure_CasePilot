import SwiftUI

struct Card<Content: View>: View {
    let step: Int?
    let title: String
    var done: Bool = false
    var trailing: AnyView? = nil
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 10) {
                if let step {
                    Text("\(step)").font(.caption.weight(.semibold)).frame(width: 26, height: 26)
                        .background(done ? Color.accentColor : Color.secondary.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
                        .foregroundStyle(done ? .white : .secondary)
                        .animation(.easeOut(duration: 0.2), value: done)
                }
                Text(title).font(.headline)
                Spacer()
                trailing
            }
            content
        }
        .padding(20)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.background, in: RoundedRectangle(cornerRadius: 20))
        .overlay(RoundedRectangle(cornerRadius: 20).strokeBorder(.separator))
    }
}

struct FieldLabel: View {
    let text: String
    var optional = false
    init(_ text: String, optional: Bool = false) { self.text = text; self.optional = optional }
    var body: some View {
        HStack(spacing: 4) {
            Text(text).font(.callout.weight(.medium))
            if optional { Text("可选").font(.caption).foregroundStyle(.tertiary) }
        }
    }
}

struct LabeledField: View {
    let label: String
    @Binding var text: String
    var prompt = ""
    var optional = false
    init(_ label: String, text: Binding<String>, prompt: String = "", optional: Bool = false) {
        self.label = label; _text = text; self.prompt = prompt; self.optional = optional
    }
    var body: some View {
        VStack(alignment: .leading) {
            FieldLabel(label, optional: optional)
            TextField(prompt, text: $text).textFieldStyle(.roundedBorder)
        }
    }
}

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

/// Liquid Glass (macOS 26+), 低版本回退为系统材质; 仅用于浮层/强调控件, 不铺满整窗
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
