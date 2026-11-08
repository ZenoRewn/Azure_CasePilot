import Foundation

struct AppError: LocalizedError {
    let message: String
    var requestId: String? = nil
    var errorDescription: String? { requestId.map { "\(message)\nRequest ID: \($0)" } ?? message }
}

/// 认证: Azure CLI 会话或浏览器设备码登录 (使用 Azure CLI 公共 Client ID, 客户无需建 App)
actor AuthProvider {
    enum Mode { case none, cli, device }
    static let deviceClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"

    private(set) var mode: Mode = .none
    private(set) var cloud: AzureCloud = .global
    private var refreshToken: String?
    private var homeTenant: String?
    private var cache: [String: (String, Date)] = [:]

    struct Identity { let user: String; let tenant: String; let cloud: AzureCloud; let method: String }

    // MARK: Azure CLI

    static func azPath() -> String? {
        let dirs = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)
            + ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]
        return dirs.map { "\($0)/az" }.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    static func runAz(_ args: [String]) async throws -> Any {
        guard let az = azPath() else { throw AppError(message: "未找到 Azure CLI (az)。请安装 Azure CLI, 或改用浏览器登录。") }
        return try await withCheckedThrowingContinuation { cont in
            let p = Process()
            p.executableURL = URL(fileURLWithPath: az)
            p.arguments = args + ["-o", "json"]
            var env = ProcessInfo.processInfo.environment
            env["PATH"] = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:" + (env["PATH"] ?? "")
            p.environment = env
            let out = Pipe(), err = Pipe()
            p.standardOutput = out; p.standardError = err
            p.terminationHandler = { proc in
                let o = out.fileHandleForReading.readDataToEndOfFile()
                let e = String(data: err.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
                if proc.terminationStatus != 0 {
                    cont.resume(throwing: AppError(message: "Azure CLI 错误, 可在终端执行 az login 后重试。\n" + String(e.suffix(600))))
                } else if let j = try? JSONSerialization.jsonObject(with: o) {
                    cont.resume(returning: j)
                } else {
                    cont.resume(throwing: AppError(message: "无法解析 Azure CLI 输出"))
                }
            }
            do { try p.run() } catch { cont.resume(throwing: error) }
        }
    }

    func useCLI() async throws -> Identity {
        let acct = try await Self.runAz(["account", "show"]) as? [String: Any] ?? [:]
        let c = AzureCloud(rawValue: acct["environmentName"] as? String ?? "") ?? .global
        mode = .cli; cloud = c; cache = [:]
        let user = (acct["user"] as? [String: Any])?["name"] as? String ?? "-"
        return Identity(user: user, tenant: acct["tenantId"] as? String ?? "", cloud: c, method: "Azure CLI")
    }

    // MARK: 设备码

    struct DeviceCode { let userCode: String; let verificationURL: URL; let deviceCode: String; let interval: Int; let expires: Date }

    func startDevice(cloud c: AzureCloud) async throws -> DeviceCode {
        let j = try await Self.postForm("\(c.login)/organizations/oauth2/v2.0/devicecode", [
            "client_id": Self.deviceClientId, "scope": "\(c.arm)/user_impersonation offline_access openid profile"])
        cloud = c
        return DeviceCode(userCode: j["user_code"] as? String ?? "", verificationURL: URL(string: j["verification_uri"] as? String ?? "https://microsoft.com/devicelogin")!,
                          deviceCode: j["device_code"] as? String ?? "", interval: j["interval"] as? Int ?? 5,
                          expires: Date().addingTimeInterval(Double(j["expires_in"] as? Int ?? 900)))
    }

    /// 轮询直到用户在浏览器完成登录
    func pollDevice(_ code: DeviceCode) async throws -> Identity {
        while Date() < code.expires {
            try await Task.sleep(for: .seconds(code.interval))
            do {
                let j = try await Self.postForm("\(cloud.login)/organizations/oauth2/v2.0/token", [
                    "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
                    "client_id": Self.deviceClientId, "device_code": code.deviceCode])
                let claims = Self.jwtClaims(j["id_token"] as? String ?? j["access_token"] as? String ?? "")
                let tid = claims["tid"] as? String ?? ""
                mode = .device; refreshToken = j["refresh_token"] as? String; homeTenant = tid
                cache = [tid: (j["access_token"] as? String ?? "", Date().addingTimeInterval(Double(j["expires_in"] as? Int ?? 3600)))]
                let user = claims["preferred_username"] as? String ?? claims["upn"] as? String ?? "-"
                return Identity(user: user, tenant: tid, cloud: cloud, method: "浏览器登录")
            } catch let e as AppError where e.message.contains("authorization_pending") || e.message.contains("slow_down") {
                continue
            }
        }
        throw AppError(message: "验证码已过期, 请重新获取")
    }

    func logout() { mode = .none; refreshToken = nil; homeTenant = nil; cache = [:] }

    // MARK: 令牌

    func token(tenant: String?) async throws -> String {
        let key = tenant ?? "_"
        if let (t, exp) = cache[key], exp.timeIntervalSinceNow > 300 { return t }
        let (t, exp): (String, Date)
        switch mode {
        case .none: throw AppError(message: "尚未登录")
        case .cli:
            var args = ["account", "get-access-token", "--resource", cloud.arm + "/"]
            if let tenant { args += ["--tenant", tenant] }
            let j = try await Self.runAz(args) as? [String: Any] ?? [:]
            let e = (j["expires_on"] as? Double) ?? (j["expires_on"] as? Int).map(Double.init) ?? Date().addingTimeInterval(1800).timeIntervalSince1970
            (t, exp) = (j["accessToken"] as? String ?? "", Date(timeIntervalSince1970: e))
        case .device:
            let tid = tenant ?? homeTenant ?? "organizations"
            let j: [String: Any]
            do {
                j = try await Self.postForm("\(cloud.login)/\(tid)/oauth2/v2.0/token", [
                    "grant_type": "refresh_token", "client_id": Self.deviceClientId, "refresh_token": refreshToken ?? "",
                    "scope": "\(cloud.arm)/user_impersonation offline_access"])
            } catch { throw AppError(message: "无法获取租户 \(tid) 的令牌 (可能需要 MFA 或未加入该租户)\n\(error.localizedDescription)") }
            if let r = j["refresh_token"] as? String { refreshToken = r }
            (t, exp) = (j["access_token"] as? String ?? "", Date().addingTimeInterval(Double(j["expires_in"] as? Int ?? 3600)))
        }
        cache[key] = (t, exp)
        return t
    }

    // MARK: helpers

    static func postForm(_ url: String, _ fields: [String: String]) async throws -> [String: Any] {
        var req = URLRequest(url: URL(string: url)!)
        req.httpMethod = "POST"
        req.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        var comps = URLComponents(); comps.queryItems = fields.map { URLQueryItem(name: $0.key, value: $0.value) }
        req.httpBody = comps.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B").data(using: .utf8)
        let (data, resp) = try await URLSession.shared.data(for: req)
        let j = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
        if let http = resp as? HTTPURLResponse, http.statusCode >= 400 {
            throw AppError(message: "\(j["error"] as? String ?? "\(http.statusCode)"): \((j["error_description"] as? String ?? "").prefix(400))")
        }
        return j
    }

    static func jwtClaims(_ jwt: String) -> [String: Any] {
        let parts = jwt.split(separator: ".")
        guard parts.count > 1 else { return [:] }
        var s = String(parts[1]).replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        s += String(repeating: "=", count: (4 - s.count % 4) % 4)
        guard let d = Data(base64Encoded: s) else { return [:] }
        return (try? JSONSerialization.jsonObject(with: d)) as? [String: Any] ?? [:]
    }
}
