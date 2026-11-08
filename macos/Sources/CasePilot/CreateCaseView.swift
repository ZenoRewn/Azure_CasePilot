import SwiftUI

struct CreateCaseView: View {
    @Environment(AppStore.self) private var store
    var onCreated: () -> Void

    @State private var kind: CaseKind = .technical
    @State private var draft = NewCase()
    @State private var serviceQuery = ""
    @State private var showServicePicker = false
    @State private var problems: [ProblemClassification] = []
    @State private var resources: [AzureResource] = []
    @State private var loadingProblems = false
    @State private var useStart = false
    @State private var quota = QuotaRequest()
    @State private var confirm = false
    @State private var preview: String?
    @State private var submitting = false
    @State private var result: (ok: Bool, text: String)?
    @State private var showResourcePicker = false
    @State private var createdName: String?

    private var isVMQuota: Bool { kind == .quota && (draft.problem?.displayName.lowercased().contains("compute-vm (cores-vcpus)") ?? false) }

    var body: some View {
        HStack(alignment: .top, spacing: 20) {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    header
                    categoryCard
                    if isVMQuota { quotaCard }
                    describeCard
                    severityCard
                    contactCard
                }
                .padding(24)
            }
            summaryPanel.frame(width: 300).padding(.vertical, 24).padding(.trailing, 24)
        }
        .background(Color(nsColor: .windowBackgroundColor))
        .navigationTitle("新建 Support Case")
        .onAppear { draft.contact = store.contact }
        .onChange(of: draft.contact) { _, c in store.contact = c }
        .onChange(of: kind) { _, _ in applyKind() }
        .onChange(of: store.services) { _, _ in applyKind() }
        .onChange(of: store.subscriptionId) { _, _ in Task { await loadResources() } }
        .sheet(isPresented: $showResourcePicker) { ResourcePicker { svc, res in Task { await pick(svc, resource: res.id) } } }
        .sheet(item: Binding(get: { preview.map { IdText(text: $0) } }, set: { preview = $0?.text })) { p in
            VStack(alignment: .leading, spacing: 12) {
                Text("请求体预览").font(.title3.weight(.semibold))
                Text("PUT \(SupportClient.base(store.sub))/supportTickets/{name}").font(.caption.monospaced()).foregroundStyle(.secondary)
                ScrollView { Text(p.text).font(.system(.caption, design: .monospaced)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(12) }
                    .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 12))
                HStack { Spacer(); Button("关闭") { preview = nil }.keyboardShortcut(.defaultAction) }
            }.padding(24).frame(width: 640, height: 520)
        }
        .alert("确认创建 Support Case?", isPresented: $confirm) {
            Button("取消", role: .cancel) {}
            Button("创建") { Task { await submit() } }
        } message: {
            Text(confirmText)
        }
    }

    // MARK: 区块

    private var header: some View {
        HStack(alignment: .bottom) {
            VStack(alignment: .leading, spacing: 4) {
                Text("新建 Support Case").font(.title.weight(.semibold)).fixedSize()
                Text("描述越具体, 首次响应越快。").foregroundStyle(.secondary)
            }
            Spacer()
            Picker("", selection: $kind) { ForEach(CaseKind.allCases) { Text($0.label).tag($0) } }
                .pickerStyle(.segmented).labelsHidden().frame(width: 300)
        }
    }

    private var categoryCard: some View {
        Card(step: 1, title: "问题类别", done: draft.problem != nil) {
            FieldLabel("服务")
            HStack {
                Button { showServicePicker = true } label: {
                    HStack {
                        Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                        Text(draft.service?.displayName ?? "搜索服务: OpenAI / Virtual Machine / Kubernetes …")
                            .foregroundStyle(draft.service == nil ? .secondary : .primary)
                        Spacer()
                        Image(systemName: "chevron.up.chevron.down").font(.caption).foregroundStyle(.tertiary)
                    }
                    .padding(.horizontal, 10).frame(height: 32)
                    .background(.background, in: RoundedRectangle(cornerRadius: 8))
                    .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(.separator))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain).disabled(kind != .technical)
                .popover(isPresented: $showServicePicker, arrowEdge: .bottom) {
                    ServicePicker(services: store.services, query: $serviceQuery) { s in showServicePicker = false; Task { await pick(s) } }
                }
                if kind == .technical && store.sub != nil {
                    Button { showResourcePicker = true } label: { Label("从资源选择", systemImage: "shippingbox") }
                }
            }
            if kind == .technical {
                FlowChips(items: chipServices.map(\.displayName)) { name in
                    if let s = store.services.first(where: { $0.displayName == name }) { Task { await pick(s) } }
                }
            }
            HStack(alignment: .top, spacing: 16) {
                VStack(alignment: .leading) {
                    FieldLabel("问题类型")
                    Picker("", selection: $draft.problem) {
                        Text(loadingProblems ? "加载中…" : draft.service == nil ? "先选择服务" : "— 请选择 (\(problems.count)) —").tag(ProblemClassification?.none)
                        ForEach(problems) { Text($0.displayName).tag(Optional($0)) }
                    }.labelsHidden().disabled(problems.isEmpty)
                }
                if kind == .technical {
                    VStack(alignment: .leading) {
                        FieldLabel("相关资源", optional: true)
                        Picker("", selection: $draft.resourceId) {
                            Text(resources.isEmpty ? "— 不指定 —" : "— 不指定 (\(resources.count) 个可选) —").tag(String?.none)
                            ForEach(resources) { Text("\($0.name) · \($0.location)").tag(Optional($0.id)) }
                        }.labelsHidden()
                    }
                }
            }
        }
    }

    private var quotaCard: some View {
        Card(step: nil, title: "配额明细 · Compute vCPU", done: quotaValid) {
            HStack(spacing: 12) {
                LabeledField("区域", text: $quota.region, prompt: "eastus / southeastasia")
                LabeledField("VM 系列", text: $quota.vmFamily, prompt: "DSv5 Series")
                LabeledField("新上限 (vCPU)", text: $quota.newLimit, prompt: "200")
            }
        }
    }

    private var describeCard: some View {
        Card(step: 2, title: "问题描述", done: titleOK && descOK) {
            HStack { FieldLabel("标题"); Spacer(); Text("\(draft.title.count) / 200").font(.caption).foregroundStyle(.tertiary) }
            TextField("例: East US gpt-4o 部署持续返回 429, 配额未用满", text: $draft.title).textFieldStyle(.roundedBorder)
                .onChange(of: draft.title) { _, v in if v.count > 200 { draft.title = String(v.prefix(200)) } }
            HStack { FieldLabel("详细描述"); Spacer(); Button("插入结构化模板", action: insertTemplate).buttonStyle(.link).font(.caption) }
            TextEditor(text: $draft.description)
                .font(.body).frame(minHeight: 180).padding(6).scrollContentBackground(.hidden)
                .background(.background, in: RoundedRectangle(cornerRadius: 8))
                .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(.separator))
            Text("附上 Request ID / Correlation ID 与精确时间 (含时区), 可显著缩短排查时间。").font(.caption).foregroundStyle(.secondary)
            HStack(spacing: 20) {
                Toggle("指定问题开始时间", isOn: $useStart)
                if useStart { DatePicker("", selection: Binding(get: { draft.startTime ?? Date() }, set: { draft.startTime = $0 })).labelsHidden() }
                Spacer()
                Toggle("允许 Microsoft 收集高级诊断信息", isOn: $draft.consent)
            }
        }
    }

    private var severityCard: some View {
        Card(step: 3, title: "影响程度", done: true, trailing: AnyView(planBadge)) {
            HStack(spacing: 10) {
                ForEach(Severity.allCases) { s in
                    let on = draft.severity == s
                    Button { draft.severity = s; draft.require24x7 = s.isHigh } label: {
                        VStack(alignment: .leading, spacing: 6) {
                            Text(s.level).font(.callout.weight(.bold)).frame(width: 28, height: 28)
                                .background(on ? Color.accentColor : Color.secondary.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
                                .foregroundStyle(on ? .white : .secondary)
                            Text(s.title).font(.callout.weight(.semibold))
                            Text(s.detail).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        }
                        .frame(maxWidth: .infinity, minHeight: 96, alignment: .topLeading).padding(12)
                        .background(on ? Color.accentColor.opacity(0.08) : Color.clear, in: RoundedRectangle(cornerRadius: 14))
                        .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(on ? Color.accentColor : Color.secondary.opacity(0.25), lineWidth: on ? 1.5 : 1))
                        .contentShape(Rectangle())
                    }.buttonStyle(.plain)
                }
            }
            Toggle("需要 24×7 响应 (仅 A / A+)", isOn: $draft.require24x7).disabled(!draft.severity.isHigh)
        }
    }

    private var planBadge: some View {
        Group {
            if let p = store.supportPlan { Badge(text: "支持计划: \(p)", tint: .accentColor).help("根据该订阅最近的 Case 推断") }
            else { Text("支持计划未知").font(.caption).foregroundStyle(.tertiary) }
        }
    }

    private var contactCard: some View {
        Card(step: 4, title: "联系方式", done: contactOK, trailing: AnyView(Text("仅保存在本机").font(.caption).foregroundStyle(.tertiary))) {
            Grid(horizontalSpacing: 12, verticalSpacing: 12) {
                GridRow {
                    LabeledField("姓", text: $draft.contact.lastName)
                    LabeledField("名", text: $draft.contact.firstName)
                    LabeledField("邮箱", text: $draft.contact.email)
                }
                GridRow {
                    VStack(alignment: .leading) {
                        FieldLabel("首选联系方式")
                        Picker("", selection: $draft.contact.preferPhone) { Text("邮件").tag(false); Text("电话").tag(true) }.labelsHidden()
                    }
                    LabeledField("电话", text: $draft.contact.phone, prompt: "+86 …", optional: !draft.contact.preferPhone)
                    LabeledField("抄送 (逗号分隔)", text: $draft.contact.cc, optional: true)
                }
                GridRow {
                    VStack(alignment: .leading) {
                        FieldLabel("支持语言")
                        Picker("", selection: $draft.contact.language) { ForEach(Contact.languages, id: \.0) { Text($0.1).tag($0.0) } }.labelsHidden()
                    }
                    VStack(alignment: .leading) {
                        FieldLabel("国家 / 地区")
                        Picker("", selection: $draft.contact.country) { ForEach(Contact.countries, id: \.0) { Text("\($0.1) (\($0.0))").tag($0.0) } }.labelsHidden()
                    }
                    VStack(alignment: .leading) {
                        FieldLabel("时区")
                        Picker("", selection: $draft.contact.timeZone) { ForEach(Contact.timeZones, id: \.self) { Text($0 == "China Standard Time" ? "China Standard Time (北京)" : $0).tag($0) } }.labelsHidden()
                    }
                }
            }
        }
    }

    private var summaryPanel: some View {
        let checks = checklist
        let done = checks.filter(\.1).count
        return VStack(alignment: .leading, spacing: 14) {
            HStack { Text("提交前检查").font(.headline); Spacer(); Text("\(done) / \(checks.count)").font(.caption).foregroundStyle(.secondary) }
            ProgressView(value: Double(done), total: Double(checks.count)).tint(.accentColor)
            VStack(alignment: .leading, spacing: 8) {
                ForEach(checks, id: \.0) { item in
                    Label(item.0, systemImage: item.1 ? "checkmark.circle.fill" : "circle.dashed")
                        .foregroundStyle(item.1 ? Color.primary : Color.secondary)
                        .symbolRenderingMode(.palette)
                        .font(.callout)
                }
            }
            Divider()
            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 6) {
                GridRow { Text("范围").foregroundStyle(.secondary); Text(store.subscriptions.first { $0.id == store.subscriptionId }?.name ?? "租户级").lineLimit(2) }
                GridRow { Text("服务").foregroundStyle(.secondary); Text(draft.service?.displayName ?? "—").lineLimit(2) }
                GridRow { Text("严重性").foregroundStyle(.secondary); Text("\(draft.severity.level) · \(draft.severity.title)") }
                GridRow { Text("语言").foregroundStyle(.secondary); Text(Contact.languages.first { $0.0 == draft.contact.language }?.1 ?? "") }
            }.font(.callout)
            Divider()
            Button { confirm = true } label: {
                HStack { if submitting { ProgressView().controlSize(.small) }; Text(submitting ? "提交中…" : "创建 Support Case") }.frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent).controlSize(.large).disabled(done != checks.count || submitting)
            .keyboardShortcut(.return, modifiers: .command)
            Button { preview = previewJSON } label: { Label("预览请求体", systemImage: "chevron.left.forwardslash.chevron.right").frame(maxWidth: .infinity) }
                .controlSize(.large)
            if let r = result {
                VStack(alignment: .leading, spacing: 6) {
                    Label(r.text, systemImage: r.ok ? "checkmark.seal.fill" : "exclamationmark.triangle.fill").textSelection(.enabled)
                    if r.ok { Button("查看我的 Case →", action: onCreated).buttonStyle(.link) }
                }
                .font(.callout).foregroundStyle(r.ok ? .green : .orange)
                .padding(10).frame(maxWidth: .infinity, alignment: .leading)
                .background((r.ok ? Color.green : Color.orange).opacity(0.1), in: RoundedRectangle(cornerRadius: 10))
            }
            Spacer()
        }
        .padding(20)
        .background(.background, in: RoundedRectangle(cornerRadius: 20))
        .overlay(RoundedRectangle(cornerRadius: 20).strokeBorder(.separator))
    }

    // MARK: 校验

    private var titleOK: Bool { draft.title.trimmingCharacters(in: .whitespaces).count >= 5 }
    private var descOK: Bool { draft.description.trimmingCharacters(in: .whitespacesAndNewlines).count >= 20 }
    private var contactOK: Bool {
        let c = draft.contact
        return !c.firstName.isEmpty && !c.lastName.isEmpty && c.email.range(of: #"^[^\s@]+@[^\s@]+\.[^\s@]+$"#, options: .regularExpression) != nil
            && (!c.preferPhone || !c.phone.isEmpty)
    }
    private var quotaValid: Bool { !quota.region.isEmpty && !quota.vmFamily.isEmpty && (Int(quota.newLimit) ?? 0) > 0 }
    private var checklist: [(String, Bool)] {
        var c: [(String, Bool)] = [("选择服务与问题类型", draft.service != nil && draft.problem != nil)]
        if isVMQuota { c.append(("配额区域 / 系列 / 新上限", quotaValid)) }
        c += [("标题 (≥ 5 字)", titleOK), ("详细描述 (≥ 20 字)", descOK), ("联系人姓名与有效邮箱", contactOK)]
        return c
    }
    private var confirmText: String {
        var t = "标题: \(draft.title)\n服务: \(draft.service?.displayName ?? "")\n严重性: \(draft.severity.level) · \(draft.severity.title)\n联系人: \(draft.contact.lastName)\(draft.contact.firstName) · \(draft.contact.email)"
        if draft.severity.isHigh, let p = store.supportPlan, p.range(of: "basic|developer", options: [.regularExpression, .caseInsensitive]) != nil {
            t += "\n\n⚠️ 当前支持计划 (\(p)) 可能不支持该级别。"
        }
        return t
    }

    private var chipServices: [SupportService] {
        let popular = ["Azure OpenAI Service", "Virtual Machine running Linux", "Virtual Machine running Windows", "Kubernetes Service (AKS)", "Storage Account Management"]
        var seen = Set<String>()
        return (store.recentServices + popular).filter { seen.insert($0).inserted }
            .compactMap { n in store.services.first { $0.displayName == n } }.prefix(6).map { $0 }
    }

    private var finalDraft: NewCase {
        var d = draft
        if !useStart { d.startTime = nil }
        if kind != .technical { d.resourceId = nil }
        d.quota = isVMQuota ? quota : nil
        return d
    }

    private var previewJSON: String {
        let data = try? JSONSerialization.data(withJSONObject: SupportClient.buildBody(finalDraft), options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        return data.flatMap { String(data: $0, encoding: .utf8) } ?? ""
    }

    // MARK: 动作

    private func applyKind() {
        if kind == .technical {
            if let s = draft.service, CaseKind.allCases.contains(where: { $0.matches(s) }) { draft.service = nil; draft.problem = nil; problems = [] }
        } else if let s = store.services.first(where: kind.matches) {
            Task { await pick(s) }
        }
    }

    private func pick(_ s: SupportService, resource: String? = nil) async {
        guard let t = store.tenantId else { return }
        draft.service = s; draft.problem = nil; problems = []; draft.resourceId = nil
        if kind == .technical { store.recentServices = [s.displayName] + store.recentServices.filter { $0 != s.displayName } }
        loadingProblems = true
        problems = (try? await store.client.problems(tenant: t, service: s)) ?? []
        loadingProblems = false
        if kind == .quota { draft.problem = problems.first { $0.displayName.lowercased().contains("compute-vm (cores-vcpus)") } }
        await loadResources()
        if let resource { draft.resourceId = resource }
    }

    private func loadResources() async {
        resources = []
        guard kind == .technical, let t = store.tenantId, let sub = store.sub, let s = draft.service, !s.resourceTypes.isEmpty else { return }
        resources = (try? await store.client.resources(tenant: t, sub: sub, types: s.resourceTypes)) ?? []
    }

    private func insertTemplate() {
        draft.description = ["【现象】", "", "【业务影响】(影响用户/请求量, 是否生产环境)", "", "【首次发生时间 (含时区) / 是否持续】", "",
                             "【复现步骤】", "1. ", "", "【错误信息 / Request ID / Correlation ID】", "", "【近期变更】", "", "【已尝试的排查】", ""]
            .joined(separator: "\n")
    }

    private func submit() async {
        guard let t = store.tenantId else { return }
        submitting = true; result = nil
        defer { submitting = false }
        do {
            let created = try await store.client.create(finalDraft, tenant: t, sub: store.sub)
            createdName = created.id
            result = (true, "已创建 Case \(created.ticketNumber.isEmpty ? created.id : created.ticketNumber)")
            draft.title = ""; draft.description = ""; draft.startTime = nil; useStart = false; quota = QuotaRequest()
            await store.loadTickets()
        } catch {
            result = (false, error.localizedDescription)
        }
    }
}

private struct IdText: Identifiable { let text: String; var id: String { text } }

// MARK: 服务搜索

struct ServicePicker: View {
    let services: [SupportService]
    @Binding var query: String
    var onPick: (SupportService) -> Void
    @FocusState private var focused: Bool

    private var filtered: [SupportService] {
        let terms = query.lowercased().split(separator: " ")
        return services.filter { s in terms.allSatisfy { s.displayName.lowercased().contains($0) } }
    }

    var body: some View {
        VStack(spacing: 0) {
            TextField("搜索服务", text: $query).textFieldStyle(.roundedBorder).padding(10).focused($focused)
                .onSubmit { if let f = filtered.first { onPick(f) } }
            Divider()
            if services.isEmpty { ProgressView("服务列表加载中…").padding(30) }
            List(filtered) { s in
                Button { onPick(s) } label: {
                    HStack { Text(s.displayName); Spacer(); if !s.resourceTypes.isEmpty { Text("\(s.resourceTypes.count) 种资源").font(.caption).foregroundStyle(.tertiary) } }
                        .contentShape(Rectangle())
                }.buttonStyle(.plain)
            }
            .listStyle(.plain)
        }
        .frame(width: 460, height: 400)
        .onAppear { focused = true }
    }
}

// MARK: 从订阅资源选择

struct ResourcePicker: View {
    @Environment(AppStore.self) private var store
    @Environment(\.dismiss) private var dismiss
    var onPick: (SupportService, AzureResource) -> Void
    @State private var all: [AzureResource]?
    @State private var query = ""
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("从订阅资源中选择").font(.title3.weight(.semibold))
            Text("选择出问题的资源, 再选择对应的支持服务 (最相关的排在前面)。").foregroundStyle(.secondary)
            TextField("搜索名称 / 类型 / 资源组", text: $query).textFieldStyle(.roundedBorder)
            if let error { Text(error).foregroundStyle(.orange) }
            if let all {
                List(filtered(all)) { r in
                    VStack(alignment: .leading, spacing: 6) {
                        Text(r.name).font(.callout.weight(.medium))
                        Text("\(r.type) · \(r.location)").font(.caption).foregroundStyle(.secondary)
                        let svcs = Self.rank(services(for: r), r)
                        if svcs.isEmpty { Text("无对应服务").font(.caption).foregroundStyle(.tertiary) }
                        else {
                            FlowChips(items: svcs.prefix(4).map(\.displayName)) { n in
                                if let s = svcs.first(where: { $0.displayName == n }) { onPick(s, r); dismiss() }
                            }
                        }
                    }.padding(.vertical, 4)
                }
            } else { ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity) }
            HStack { Spacer(); Button("取消") { dismiss() }.keyboardShortcut(.cancelAction) }
        }
        .padding(24).frame(width: 640, height: 560)
        .task {
            guard let t = store.tenantId, let s = store.sub else { return }
            do { all = try await store.client.resources(tenant: t, sub: s) } catch { self.error = error.localizedDescription; all = [] }
        }
    }

    private func filtered(_ rs: [AzureResource]) -> [AzureResource] {
        let terms = query.lowercased().split(separator: " ")
        return Array(rs.filter { r in terms.allSatisfy { (r.name + " " + r.type + " " + r.id).lowercased().contains($0) } }.prefix(100))
    }
    private func services(for r: AzureResource) -> [SupportService] {
        store.services.filter { $0.resourceTypes.contains { $0.lowercased() == r.type } }
    }
    /// 同一资源类型对应多个服务时: 名称与资源类型相近、覆盖类型更少 (更专一) 的优先
    static func rank(_ svcs: [SupportService], _ r: AzureResource) -> [SupportService] {
        let leaf = String((r.type.split(separator: "/").last ?? "").lowercased().prefix(7))
        func score(_ s: SupportService) -> Int { (s.displayName.lowercased().replacingOccurrences(of: " ", with: "").contains(leaf) ? -100 : 0) + s.resourceTypes.count }
        return svcs.sorted { score($0) < score($1) }
    }
}
