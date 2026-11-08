import SwiftUI

struct HistoryView: View {
    @Environment(AppStore.self) private var store
    @State private var query = ""
    @State private var status = ""
    @State private var selection: TicketSummary.ID?
    @State private var sort = [KeyPathComparator(\TicketSummary.sortDate, order: .reverse)]

    private var rows: [TicketSummary] {
        store.tickets.filter { t in
            (status.isEmpty || t.status == status) &&
            (query.isEmpty || [t.ticketNumber, t.title, t.service].contains { $0.localizedCaseInsensitiveContains(query) })
        }.sorted(using: sort)
    }

    var body: some View {
        @Bindable var store = store
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 10) {
                Stat(title: "全部", value: store.tickets.count, symbol: "tray.full")
                Stat(title: "处理中", value: store.tickets.filter(\.isOpen).count, symbol: "clock", tint: .green)
                Stat(title: "已关闭", value: store.tickets.filter { $0.status == "Closed" }.count, symbol: "checkmark.circle")
                Stat(title: "A / A+ 级", value: store.tickets.filter { Severity(api: $0.severity)?.isHigh ?? false }.count, symbol: "exclamationmark.triangle", tint: .red)
            }
            Table(rows, selection: $selection, sortOrder: $sort) {
                TableColumn("Case", value: \.ticketNumber) { t in Text(t.ticketNumber).font(.callout.monospaced()) }.width(min: 110, ideal: 130)
                TableColumn("标题", value: \.title) { t in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(t.title).fontWeight(.medium).lineLimit(1)
                        Text([t.service, t.problem].filter { !$0.isEmpty }.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }.padding(.vertical, 4)
                }.width(min: 180, ideal: 320)
                TableColumn("严重性", value: \.severity) { t in severityBadge(t.severity) }.width(min: 80, ideal: 96)
                TableColumn("状态", value: \.status) { t in statusBadge(t.status) }.width(min: 70, ideal: 84)
                TableColumn("创建时间", value: \.sortDate) { t in Text(t.created?.relative ?? "").foregroundStyle(.secondary).help(t.created?.full ?? "") }.width(min: 70, ideal: 96)
            }
            .overlay {
                if store.loadingTickets && store.tickets.isEmpty { ProgressView() }
                else if rows.isEmpty { ContentUnavailableView("该范围内没有 Case", systemImage: "tray") }
            }
            .tableStyle(.inset(alternatesRowBackgrounds: true))
        }
        .padding(16)
        .navigationTitle("我的 Case")
        .navigationSubtitle(store.sub == nil ? "租户级" : store.subscriptions.first { $0.id == store.subscriptionId }?.name ?? "")
        .searchable(text: $query, placement: .toolbar, prompt: "Case 号 / 标题 / 服务")
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                Picker("状态", selection: $status) { Text("全部").tag(""); Text("处理中").tag("Open"); Text("已关闭").tag("Closed") }
                    .pickerStyle(.segmented).fixedSize()
                Picker("时间", selection: $store.historyDays) {
                    Text("近 7 天").tag(7); Text("近 30 天").tag(30); Text("近 90 天").tag(90); Text("近 1 年").tag(365)
                }.fixedSize()
                .onChange(of: store.historyDays) { _, _ in Task { await store.loadTickets() } }
                Button("刷新", systemImage: "arrow.clockwise") { Task { await store.loadTickets() } }
                    .disabled(store.loadingTickets).help("刷新 (⌘R)")
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .selectFirstTicket)) { _ in selection = rows.first?.id }
        .inspector(isPresented: Binding(get: { selection != nil }, set: { if !$0 { selection = nil } })) {
            if let id = selection, let t = store.tickets.first(where: { $0.id == id }) {
                TicketDetailView(summary: t).id(id).inspectorColumnWidth(min: 360, ideal: 420, max: 640)
            }
        }
    }
}

extension TicketSummary { var sortDate: Date { created ?? .distantPast } }

struct Stat: View {
    let title: String, value: Int, symbol: String
    var tint: Color = .primary
    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: symbol).font(.title3).foregroundStyle(tint == .primary ? .secondary : tint).frame(width: 24)
            VStack(alignment: .leading, spacing: 0) {
                Text("\(value)").font(.title2.weight(.semibold)).monospacedDigit().contentTransition(.numericText())
                Text(title).font(.caption).foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 14).padding(.vertical, 10)
        .background(.background.secondary, in: RoundedRectangle(cornerRadius: 12))
    }
}

struct TicketDetailView: View {
    @Environment(AppStore.self) private var store
    @Environment(\.openURL) private var openURL
    let summary: TicketSummary
    @State private var detail: TicketDetail?
    @State private var error: String?
    @State private var tab = 0
    @State private var reply = ""
    @State private var sending = false
    @State private var closing = false
    @State private var confirmClose = false
    @State private var toast: String?

    private var t: TicketSummary { detail?.summary ?? summary }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 8) {
                    Text(t.ticketNumber).font(.callout.monospaced()).foregroundStyle(.secondary).textSelection(.enabled)
                    Button { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(t.ticketNumber, forType: .string); flash("已复制 Case 号") }
                        label: { Image(systemName: "doc.on.doc") }.buttonStyle(.borderless).help("复制 Case 号")
                    statusBadge(t.status); severityBadge(t.severity)
                    Spacer()
                    if let u = store.portalURL(for: t.id) { Button { openURL(u) } label: { Label("Portal", systemImage: "arrow.up.right.square") } }
                    if t.isOpen {
                        Button(role: .destructive) { confirmClose = true } label: {
                            HStack { if closing { ProgressView().controlSize(.small) }; Text(closing ? "关闭中…" : "关闭 Case") }
                        }.disabled(closing)
                    }
                }
                Text(t.title).font(.title3.weight(.semibold)).textSelection(.enabled)
                Picker("", selection: $tab) { Text("概览").tag(0); Text("沟通记录 (\(detail?.communications.count ?? 0))").tag(1) }
                    .pickerStyle(.segmented).labelsHidden().frame(width: 260)
            }
            .padding(20)
            Divider()
            ScrollView {
                Group {
                    if let error { Label(error, systemImage: "exclamationmark.triangle").foregroundStyle(.orange).textSelection(.enabled) }
                    else if let d = detail { tab == 0 ? AnyView(overview(d)) : AnyView(comms(d)) }
                    else { ProgressView().frame(maxWidth: .infinity).padding(40) }
                }.padding(20)
            }
            if t.isOpen && detail != nil {
                Divider()
                VStack(alignment: .trailing, spacing: 8) {
                    TextEditor(text: $reply).font(.callout).frame(height: 70).scrollContentBackground(.hidden).padding(6)
                        .background(.background, in: RoundedRectangle(cornerRadius: 10))
                        .overlay(alignment: .topLeading) { if reply.isEmpty { Text("补充信息给支持工程师, 将以邮件形式发送…").foregroundStyle(.tertiary).padding(11).allowsHitTesting(false) } }
                        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(.separator))
                    Button { Task { await send() } } label: {
                        HStack { if sending { ProgressView().controlSize(.small) }; Label("发送", systemImage: "paperplane.fill") }
                    }
                    .buttonStyle(.borderedProminent).keyboardShortcut(.return, modifiers: .command)
                    .disabled(reply.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || sending)
                }
                .padding(16).background(.quaternary.opacity(0.3))
            }
        }
        .overlay(alignment: .bottom) {
            if let toast {
                Label(toast, systemImage: "checkmark.circle.fill").padding(.horizontal, 14).padding(.vertical, 10)
                    .background(.regularMaterial, in: Capsule()).padding(.bottom, 20)
                    .transition(.opacity.combined(with: .scale(scale: 0.96)))
            }
        }
        .task { await load() }
        .confirmationDialog("关闭 Case \(t.ticketNumber)?", isPresented: $confirmClose) {
            Button("关闭 Case", role: .destructive) { Task { await close() } }
        } message: { Text("关闭后支持工程师将停止处理。如问题复发, 需新建 Case 或在 Portal 中重新打开。") }
    }

    private func overview(_ d: TicketDetail) -> some View {
        let p = d.props
        let cd = p["contactDetails"] as? [String: Any] ?? [:]
        let sla = p["serviceLevelAgreement"] as? [String: Any]
        let s = { (k: String) in p[k] as? String ?? "" }
        let date = { (k: String) in SupportClient.date(p[k])?.full ?? "" }
        let quota = ((p["quotaTicketDetails"] as? [String: Any])?["quotaChangeRequests"] as? [[String: Any]])?
            .map { "\($0["region"] as? String ?? ""): \($0["payload"] as? String ?? "")" }.joined(separator: "\n") ?? ""
        let rows: [(String, String, Bool)] = [
            ("服务", s("serviceDisplayName"), false), ("问题类型", s("problemClassificationDisplayName"), false),
            ("创建时间", date("createdDate"), false), ("最后更新", date("modifiedDate"), false),
            ("SLA 首次响应截止", sla.map { "\(SupportClient.date($0["expirationTime"])?.full ?? "")\(($0["slaMinutes"] as? Int).map { " · \($0) 分钟" } ?? "")" } ?? "", false),
            ("支持计划", s("supportPlanDisplayName").isEmpty ? s("supportPlanType") : s("supportPlanDisplayName"), false),
            ("支持工程师", (p["supportEngineer"] as? [String: Any])?["emailAddress"] as? String ?? "", false),
            ("24×7 响应", (p["require24X7Response"] as? Bool ?? false) ? "是" : "否", false),
            ("联系人", ["\(cd["lastName"] as? String ?? "")\(cd["firstName"] as? String ?? "")", cd["primaryEmailAddress"] as? String ?? ""].filter { !$0.isEmpty }.joined(separator: " · "), false),
            ("语言 / 时区", [cd["preferredSupportLanguage"] as? String, cd["preferredTimeZone"] as? String].compactMap { $0 }.joined(separator: " · "), false),
            ("关联资源", (p["technicalTicketDetails"] as? [String: Any])?["resourceId"] as? String ?? "", true),
            ("配额请求", quota, true),
        ].filter { !$0.1.isEmpty }
        return VStack(alignment: .leading, spacing: 18) {
            LazyVGrid(columns: [GridItem(.flexible(), alignment: .topLeading), GridItem(.flexible(), alignment: .topLeading)], alignment: .leading, spacing: 14) {
                ForEach(rows.filter { !$0.2 }, id: \.0) { r in prop(r.0, r.1) }
            }
            ForEach(rows.filter(\.2), id: \.0) { r in prop(r.0, r.1, mono: true) }
            VStack(alignment: .leading, spacing: 8) {
                Text("问题描述").font(.callout.weight(.semibold)).foregroundStyle(.secondary)
                Text(s("description")).font(.callout).lineSpacing(3).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading).padding(14)
                    .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 14))
            }
        }
    }

    private func prop(_ k: String, _ v: String, mono: Bool = false) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(k).font(.caption).foregroundStyle(.secondary)
            Text(v).font(mono ? .caption.monospaced() : .callout.weight(.medium)).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func comms(_ d: TicketDetail) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            if d.communications.isEmpty { Text("暂无沟通记录").foregroundStyle(.secondary) }
            ForEach(d.communications) { c in MessageCard(c: c) }
        }
    }

    private func load() async {
        guard let tenant = store.tenantId else { return }
        do { detail = try await store.client.detail(summary.id, tenant: tenant, sub: store.sub); error = nil }
        catch { self.error = error.localizedDescription }
    }

    private func send() async {
        guard let tenant = store.tenantId else { return }
        sending = true; defer { sending = false }
        do {
            try await store.client.reply(t.id, text: reply, tenant: tenant, sub: store.sub)
            reply = ""; flash("补充信息已发送"); tab = 1
            try? await Task.sleep(for: .seconds(2))
            await load()
        } catch { self.error = error.localizedDescription }
    }

    private func close() async {
        guard let tenant = store.tenantId else { return }
        closing = true; defer { closing = false }
        do {
            try await store.client.close(t.id, tenant: tenant, sub: store.sub)
            flash("Case \(t.ticketNumber) 已关闭")
            await load(); await store.loadTickets()
        } catch { self.error = error.localizedDescription }
    }

    private func flash(_ s: String) {
        withAnimation(.easeOut(duration: 0.22)) { toast = s }
        Task { try? await Task.sleep(for: .seconds(2.5)); withAnimation(.easeIn(duration: 0.18)) { toast = nil } }
    }
}

struct MessageCard: View {
    let c: Communication
    @State private var expanded = false
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                Badge(text: c.fromMicrosoft ? "Microsoft" : "我方", tint: c.fromMicrosoft ? .accentColor : .green)
                Text(c.subject).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                Spacer()
                Text(c.created?.relative ?? "").font(.caption).foregroundStyle(.tertiary).help(c.created?.full ?? "")
            }
            .padding(.horizontal, 12).padding(.vertical, 9).background(.quaternary.opacity(0.3))
            Divider()
            Text(c.body).font(.callout).lineSpacing(3).textSelection(.enabled)
                .lineLimit(expanded ? nil : 6).frame(maxWidth: .infinity, alignment: .leading).padding(12)
            if c.body.count > 300 {
                Button(expanded ? "收起" : "展开全文") { withAnimation(.easeOut(duration: 0.2)) { expanded.toggle() } }
                    .buttonStyle(.link).font(.caption).padding(.horizontal, 12).padding(.bottom, 10)
            }
        }
        .background(.background, in: RoundedRectangle(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(.separator))
    }
}
