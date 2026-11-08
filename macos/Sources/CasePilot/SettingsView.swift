import SwiftUI

/// ⌘, 设置: 联系人默认值 (每个 Case 复用)
struct ContactSettingsView: View {
    @Environment(AppStore.self) private var store

    var body: some View {
        @Bindable var store = store
        Form {
            Section {
                TextField("姓", text: $store.contact.lastName)
                TextField("名", text: $store.contact.firstName)
                TextField("邮箱", text: $store.contact.email)
                TextField("抄送", text: $store.contact.cc, prompt: Text("可选, 逗号分隔"))
            } header: { Text("联系人") } footer: { Text("Microsoft 支持将通过此邮箱联系你。仅保存在本机。").foregroundStyle(.secondary) }
            Section("联系方式") {
                Picker("首选方式", selection: $store.contact.preferPhone) { Text("邮件").tag(false); Text("电话").tag(true) }
                TextField("电话", text: $store.contact.phone, prompt: Text(store.contact.preferPhone ? "必填" : "可选"))
            }
            Section("区域") {
                Picker("支持语言", selection: $store.contact.language) { ForEach(Contact.languages, id: \.0) { Text($0.1).tag($0.0) } }
                Picker("国家 / 地区", selection: $store.contact.country) { ForEach(Contact.countries, id: \.0) { Text($0.1).tag($0.0) } }
                Picker("时区", selection: $store.contact.timeZone) { ForEach(Contact.timeZones, id: \.self) { Text($0).tag($0) } }
            }
        }
        .formStyle(.grouped)
        .frame(width: 480, height: 560)
    }
}
