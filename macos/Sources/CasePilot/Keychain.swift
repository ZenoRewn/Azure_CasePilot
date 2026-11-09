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

/// AI / Speech Key 仅保存在本机钥匙串
enum SecretStore {
    private static let service = "com.zenorewn.casepilot.ai"
    enum Name: String { case aoai = "aoai-key", stt = "stt-key" }

    static func get(_ n: Name) -> String? {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: n.rawValue, kSecReturnData as String: true]
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let d = out as? Data else { return nil }
        return String(data: d, encoding: .utf8).flatMap { $0.isEmpty ? nil : $0 }
    }

    static func set(_ n: Name, _ value: String) {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: n.rawValue]
        SecItemDelete(q as CFDictionary)
        guard !value.isEmpty else { return }
        var add = q; add[kSecValueData as String] = Data(value.utf8)
        SecItemAdd(add as CFDictionary, nil)
    }
}
