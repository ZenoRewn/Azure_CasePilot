import Foundation
import Security

/// App Registration 凭据仅保存在本机: Tenant/Client ID → UserDefaults, Secret → 钥匙串
enum SPStore {
    private static let service = "com.zenorewn.casepilot.sp"

    static func save(tenant: String, clientId: String, secret: String, cloud: AzureCloud) {
        UserDefaults.standard.set(["tenant": tenant, "clientId": clientId, "cloud": cloud.rawValue], forKey: "sp")
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: clientId]
        SecItemDelete(q as CFDictionary)
        var add = q; add[kSecValueData as String] = Data(secret.utf8)
        SecItemAdd(add as CFDictionary, nil)
    }

    static func load() -> (tenant: String, clientId: String, secret: String, cloud: AzureCloud)? {
        guard let d = UserDefaults.standard.dictionary(forKey: "sp") as? [String: String],
              let t = d["tenant"], let c = d["clientId"] else { return nil }
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: c, kSecReturnData as String: true]
        var out: CFTypeRef?
        let secret = SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess ? String(data: out as! Data, encoding: .utf8) ?? "" : ""
        return (t, c, secret, AzureCloud(rawValue: d["cloud"] ?? "") ?? .global)
    }

    static func clear() {
        if let c = load()?.clientId {
            SecItemDelete([kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: c] as CFDictionary)
        }
        UserDefaults.standard.removeObject(forKey: "sp")
    }
}
