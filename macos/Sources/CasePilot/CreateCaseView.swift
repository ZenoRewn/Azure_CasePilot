import SwiftUI

struct CreateCaseView: View {
    @Environment(AppStore.self) private var store
    var onCreated: () -> Void

    @State private var showMore = false
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
        Form {
            AIComposer { d in await applyDraft(d) }
            Section {
                LabeledContent("服务") {
                    HStack {
                        Button { showServicePicker = true } label: {
                            Text(draft.service?.displayName ?? "选择服务…").foregroundStyle(draft.service == nil ? .secondary : .primary)
                        }
                        .popover(isPresented: $showServicePicker, arrowEdge: .bottom) {
                            ServicePicker(services: store.services, query: $serviceQuery) { s in showServicePicker = false; Task { await pick(s) } }
                        }
                        if store.sub != nil {
                            Button("从资源选择", systemImage: "shippingbox") { showResourcePicker = true }
                                .labelStyle(.iconOnly).help("从订阅资源反查服务")
                        }
                    }
                }
                if draft.service == nil {
                    FlowChips(items: quickServices.map(\.label)) { l in
                        if let s = quickServices.first(where: { $0.label == l })?.service { Task { await pick(s) } }
                    }
                }
                if draft.service == nil && !store.recentProblems.isEmpty {
                    LabeledContent("最近使用") {
                        FlowChips(items: store.recentProblems.map(\.label)) { l in
                            if let r = store.recentProblems.first(where: { $0.label == l }) { Task { await reuse(r) } }
                        }
                    }
                }
                Picker("问题类型", selection: $draft.problem) {
                    Text(loadingProblems ? "加载中…" : draft.service == nil ? "先选择服务" : "请选择").tag(ProblemClassification?.none)
                    ForEach(problems) { Text($0.displayName).tag(Optional($0)) }
                }.disabled(problems.isEmpty)
                Picker("严重性", selection: $draft.severity) {
                    ForEach(Severity.allCases) { Text("\($0.level) \($0.title)").tag($0) }
                }
                .pickerStyle(.segmented)
                .onChange(of: draft.severity) { _, s in draft.require24x7 = s.isHigh }
            } footer: {
                HStack(spacing: 4) {
                    Text(draft.severity.detail)
                    if draft.severity.isHigh && store.plan.allowsCritical == false {
                        Label("当前支持计划不支持该级别", systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                    }
                }.foregroundStyle(.secondary)
            }

            if isVMQuota {
                Section("配额明细") {
                    TextField("区域", text: $quota.region, prompt: Text("eastus"))
                    TextField("VM 系列", text: $quota.vmFamily, prompt: Text("DSv5 Series"))
                    TextField("新上限 (vCPU)", text: $quota.newLimit, prompt: Text("200"))
                }
            }

            Section {
                TextField("标题", text: $draft.title, prompt: Text("一句话说明问题"))
                    .onChange(of: draft.title) { _, v in if v.count > 200 { draft.title = String(v.prefix(200)) } }
                TextEditor(text: $draft.description)
                    .overlay(alignment: .bottomTrailing) { MicButton(text: $draft.description).padding(6) }
                    .font(.body).frame(minHeight: 200)
                    .overlay(alignment: .topLeading) {
                        if draft.description.isEmpty {
                            Text("现象、影响、发生时间 (含时区)、错误信息 / Request ID")
                                .foregroundStyle(.tertiary).padding(.leading, 5).allowsHitTesting(false)
                        }
                    }
            } header: {
                HStack { Text("描述"); Spacer(); Button("插入模板", action: insertTemplate).buttonStyle(.link).font(.caption) }
            }

            Section {
                DisclosureGroup("更多选项", isExpanded: $showMore) {
                    if kind == .technical {
                        Picker("相关资源", selection: $draft.resourceId) {
                            Text(resources.isEmpty ? "不指定" : "不指定 (\(resources.count) 个可选)").tag(String?.none)
                            ForEach(resources) { Text("\($0.name) · \($0.location)").tag(Optional($0.id)) }
                        }
                    }
                    Toggle("指定问题开始时间", isOn: $useStart)
                    if useStart { DatePicker("开始时间", selection: Binding(get: { draft.startTime ?? Date() }, set: { draft.startTime = $0 })) }
                    Toggle("需要 24×7 响应", isOn: $draft.require24x7).disabled(!draft.severity.isHigh)
                    Toggle("允许收集高级诊断信息", isOn: $draft.consent)
                }
                LabeledContent {
                    SettingsLink { Text("编辑") }
                } label: {
                    if store.contactValid {
                        Text("联系人  ").foregroundStyle(.secondary) + Text("\(draft.contact.lastName)\(draft.contact.firstName) · \(draft.contact.email)")
                    } else {
                        Label("请先设置联系人", systemImage: "exclamationmark.circle").foregroundStyle(.orange)
                    }
                }
            }

            if let r = result {
                Section {
                    HStack {
                        Label(r.text, systemImage: r.ok ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                            .foregroundStyle(r.ok ? .green : .orange).textSelection(.enabled)
                        Spacer()
                        if r.ok { Button("查看", action: onCreated) }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .frame(maxWidth: 720)
        .frame(maxWidth: .infinity)
        .navigationTitle("新建 Case")
        .navigationSubtitle(missing.isEmpty ? "可以提交" : "还需: " + missing.joined(separator: "、"))
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                Button("预览请求体", systemImage: "curlybraces") { preview = previewJSON }.help("预览请求体")
                Button { confirm = true } label: {
                    if submitting { ProgressView().controlSize(.small) } else { Label("提交", systemImage: "paperplane.fill") }
                }
                .buttonStyle(.borderedProminent)
                .disabled(!missing.isEmpty || submitting)
                .keyboardShortcut(.return, modifiers: .command)
                .help("创建 Support Case (⌘↩)")
            }
        }
        .onAppear { draft.contact = store.contact }
        .onChange(of: store.contact) { _, c in draft.contact = c }
        .onChange(of: store.subscriptionId) { _, _ in Task { await loadResources() } }
        .sheet(isPresented: $showResourcePicker) { ResourcePicker { svc, res in Task { await pick(svc, resource: res.id) } } }
        .sheet(item: Binding(get: { preview.map { IdText(text: $0) } }, set: { preview = $0?.text })) { p in
            NavigationStack {
                ScrollView { Text(p.text).font(.system(.callout, design: .monospaced)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding() }
                    .navigationTitle("请求体预览")
                    .toolbar { ToolbarItem(placement: .confirmationAction) { Button("完成") { preview = nil } } }
            }.frame(width: 640, height: 520)
        }
        .alert("创建 Support Case?", isPresented: $confirm) {
            Button("取消", role: .cancel) {}
            Button("创建") { Task { await submit() } }
        } message: { Text(confirmText) }
    }

    private var missing: [String] { checklist.filter { !$0.1 }.map(\.0) }

    /// 类别由所选服务推断, 无需用户额外选择
    private var kind: CaseKind {
        guard let s = draft.service else { return .technical }
        return CaseKind.allCases.first { $0.matches(s) } ?? .technical
    }

    /// 快捷入口: 配额 / 计费 + 最近与常用服务
    private var quickServices: [(label: String, service: SupportService)] {
        var out: [(String, SupportService)] = []
        if let q = store.services.first(where: CaseKind.quota.matches) { out.append(("配额申请", q)) }
        if let b = store.services.first(where: CaseKind.billing.matches) { out.append(("计费 / 订阅", b)) }
        return out + chipServices.prefix(4).map { ($0.displayName, $0) }
    }

    // MARK: 校验

    private var titleOK: Bool { draft.title.trimmingCharacters(in: .whitespaces).count >= 5 }
    private var descOK: Bool { draft.description.trimmingCharacters(in: .whitespacesAndNewlines).count >= 20 }
    private var contactOK: Bool { store.contactValid }
    private var quotaValid: Bool { !quota.region.isEmpty && !quota.vmFamily.isEmpty && (Int(quota.newLimit) ?? 0) > 0 }
    private var checklist: [(String, Bool)] {
        var c: [(String, Bool)] = [("问题类型", draft.service != nil && draft.problem != nil)]
        if isVMQuota { c.append(("配额明细", quotaValid)) }
        c += [("标题", titleOK), ("描述 (≥20 字)", descOK), ("联系人", contactOK)]
        return c
    }
    private var confirmText: String {
        var t = "标题: \(draft.title)\n服务: \(draft.service?.displayName ?? "")\n严重性: \(draft.severity.level) · \(draft.severity.title)\n联系人: \(draft.contact.lastName)\(draft.contact.firstName) · \(draft.contact.email)"
        if draft.severity.isHigh, store.plan.allowsCritical == false {
            t += "\n\n⚠️ 当前支持计划 (\(store.plan.name ?? "")) 不支持该级别, 提交可能被拒绝。"
        }
        return t
    }

    private var chipServices: [SupportService] {
        let popular = ["Azure OpenAI", "Microsoft Foundry", "Virtual Machine running Linux", "Virtual Machine running Windows", "Kubernetes Service (AKS)", "Storage Account Management"]
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

    private func applyDraft(_ d: AIDraft) async {
        if let s = d.service { await pick(s) }
        if let p = d.problem { draft.problem = problems.first { $0.id == p.id } ?? p }
        draft.severity = d.severity; draft.require24x7 = d.severity.isHigh
        draft.title = d.title; draft.description = d.description
    }

    private func reuse(_ r: RecentProblem) async {
        guard let s = store.services.first(where: { $0.name == r.serviceName }) else { return }
        await pick(s)
        draft.problem = problems.first { $0.id == r.problemId }
    }

    private func submit() async {
        guard let t = store.tenantId else { return }
        submitting = true; result = nil
        defer { submitting = false }
        do {
            let created = try await store.client.create(finalDraft, tenant: t, sub: store.sub)
            if let sv = draft.service, let pb = draft.problem { store.remember(service: sv, problem: pb) }
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
