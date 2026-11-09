// 官方实时额度（macOS）：桌面 App 把登录令牌加密保存在 config.json 的 oauth:tokenCacheV2 里（Electron safeStorage），
// 密钥在钥匙串「Claude Safe Storage」。这里只读地解出令牌、调用官方用量接口：不续期、不写回任何文件、
// 不发往 api.anthropic.com 以外的任何地方，不影响桌面 App 的登录状态。
import Foundation
import Security
import CommonCrypto

struct RateLimited: Error { let seconds: Int }

// ---------------- 钥匙串里的 Safe Storage 密钥 ----------------
enum SafeStorage {
    static let lock = Lock()
    static var key: Data? = nil
    static var deniedAt: Date? = nil
    static var deniedMsg = ""

    static var services: [String] {
        if Env.test, let s = Env.get("CLAUDE_SWITCHER_KEYCHAIN_SERVICE") { return [s] }
        return ["Claude Safe Storage", "Claude Desktop Safe Storage"]
    }

    static var hasKey: Bool { lock.run { key != nil } }

    // 取 AES 密钥：钥匙串密码 → PBKDF2-SHA1（salt "saltysalt"，1003 次）→ 16 字节
    static func getKey() throws -> Data {
        try lock.run {
            if let k = key { return k }
            if let d = deniedAt, Date().timeIntervalSince(d) < 600 { throw AppError(deniedMsg) }
            var notFound = true
            for svc in services {
                let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: svc,
                                        kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
                var r: CFTypeRef?
                let st = SecItemCopyMatching(q as CFDictionary, &r)
                if st == errSecSuccess, let d = r as? Data {
                    let k = derive(d)
                    key = k; deniedAt = nil
                    return k
                }
                if st == errSecItemNotFound { continue }
                notFound = false
                if st == errSecUserCanceled || st == errSecAuthFailed || st == errSecInteractionNotAllowed {
                    deniedMsg = "钥匙串访问被拒绝：请重新查询，并在弹窗里输入开机密码后点「始终允许」"
                } else {
                    deniedMsg = "读取钥匙串失败（错误码 \(st)）"
                }
                deniedAt = Date()
                throw AppError(deniedMsg)
            }
            _ = notFound
            throw AppError("钥匙串里没有找到 Claude 的加密密钥（Claude Safe Storage），请先打开一次 Claude 桌面 App 并登录")
        }
    }

    static func derive(_ password: Data) -> Data {
        var out = [UInt8](repeating: 0, count: 16)
        let salt = Array("saltysalt".utf8)
        password.withUnsafeBytes { pw in
            _ = CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2), pw.baseAddress?.assumingMemoryBound(to: Int8.self), password.count,
                                     salt, salt.count, CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA1), 1003, &out, out.count)
        }
        return Data(out)
    }

    // v10/v11 + AES-128-CBC（IV 为 16 个空格，PKCS7 填充）
    static func decrypt(_ blob: Data, _ key: Data) throws -> Data {
        let ct = blob.dropFirst(3)
        let iv = [UInt8](repeating: 0x20, count: 16)
        var out = [UInt8](repeating: 0, count: ct.count + 16)
        var moved = 0
        let st = key.withUnsafeBytes { k in
            ct.withUnsafeBytes { c in
                CCCrypt(CCOperation(kCCDecrypt), CCAlgorithm(kCCAlgorithmAES128), CCOptions(kCCOptionPKCS7Padding),
                        k.baseAddress, key.count, iv, c.baseAddress, ct.count, &out, out.count, &moved)
            }
        }
        if st != CCCryptorStatus(kCCSuccess) { throw AppError("解密失败") }
        return Data(out.prefix(moved))
    }
}

enum LiveUsage {
    static let histLock = Lock()
    static var histPath: String { Settings.stateDir + "/live-history.json" }
    static var cachePath: String { Settings.stateDir + "/live-cache.json" }

    // 读出这个账号目录里可用于查询用量的令牌（取有效期最长的 user:profile 令牌）；没有返回 nil
    static func readToken(_ dir: String) throws -> (String, Int64)? {
        guard let cfg = Json.parseFile(dir + "/config.json") as? [String: Any], let raw = cfg["oauth:tokenCacheV2"] as? String, !raw.isEmpty else { return nil }
        var plain: Data
        if let blob = Data(base64Encoded: raw), blob.count > 3, let pre = String(data: blob.prefix(3), encoding: .ascii), pre == "v10" || pre == "v11" {
            plain = try SafeStorage.decrypt(blob, try SafeStorage.getKey())
        } else if raw.hasPrefix("{") {
            plain = Data(raw.utf8)
        } else if let blob = Data(base64Encoded: raw), let s = String(data: blob, encoding: .utf8), s.hasPrefix("{") {
            plain = blob
        } else { return nil }
        guard let cache = Json.parse(plain) as? [String: Any] else { throw AppError("登录信息格式无法识别") }
        var tok: String? = nil
        var expires: Int64 = 0
        for (k, v) in cache {
            guard k.contains("user:profile"), let d = v as? [String: Any], let t = d["token"] as? String else { continue }
            let exp = jsonInt(d["expiresAt"]) ?? 0
            if tok == nil || exp > expires { tok = t; expires = exp }
        }
        guard let t = tok else { return nil }
        return (t, expires)
    }

    // 是否有可查询的登录：已拿到钥匙串密钥时真正解出来看；还没拿到时只看有没有加密令牌（不弹钥匙串框）
    static func available(_ dir: String) -> Bool {
        guard let cfg = Json.parseFile(dir + "/config.json") as? [String: Any], let raw = cfg["oauth:tokenCacheV2"] as? String, !raw.isEmpty else { return false }
        if let blob = Data(base64Encoded: raw), blob.count > 3, let pre = String(data: blob.prefix(3), encoding: .ascii), pre == "v10" || pre == "v11" {
            if !SafeStorage.hasKey { return true }
        }
        return ((try? readToken(dir)) ?? nil) != nil
    }

    // 记录并返回这个账号最近 8 天的 [时间ms, 5H已用%, 7天已用%]
    static func record(_ id: String, _ r: [String: Any]?) -> [Any] {
        histLock.run {
            var all = (Json.parseFile(histPath) as? [String: Any]) ?? [:]
            var list: [[Any]] = ((all[id] as? [Any]) ?? []).compactMap { $0 as? [Any] }.filter { $0.count >= 3 }
            let now = nowMs(), cut = now - 8 * 86_400_000
            list.removeAll { (jsonInt($0.first) ?? 0) < cut }
            guard let r = r else { return list }
            let s = r["session"] as? [String: Any], w = r["week"] as? [String: Any]
            let su = s.flatMap { jsonInt($0["used"]) } ?? -1, wu = w.flatMap { jsonInt($0["used"]) } ?? -1
            let last = list.last
            let add = last == nil || jsonInt(last![1]) != su || jsonInt(last![2]) != wu || now - (jsonInt(last![0]) ?? 0) >= 600_000
            if add {
                list.append([now, su, wu])
                all[id] = list
                try? FS.write(histPath, Json.str(all))
            }
            return list
        }
    }

    // 缓存与限流保护：查询后锁 120 秒；官方限流 / 网络失败时用最近一次成功的数据
    static var last: [String: [String: Any]] = [:]
    static var backoff: [String: Date] = [:]
    static var cacheLoaded = false
    static let lockSeconds = 120

    static func loadCache() {
        if cacheLoaded { return }
        cacheLoaded = true
        guard let all = Json.parseFile(cachePath) as? [String: Any] else { return }
        for (k, v) in all {
            guard var d = v as? [String: Any] else { continue }
            if let lu = jsonInt(d["lockUntil"]) { backoff[k] = Date(timeIntervalSince1970: Double(lu) / 1000); d.removeValue(forKey: "lockUntil") }
            if d["session"] != nil { last[k] = d }
        }
    }

    static func cached(_ id: String, _ query: () throws -> [String: Any]) -> [String: Any] {
        let early: [String: Any]? = histLock.run {
            loadCache()
            if let bo = backoff[id], Date() < bo { return answer(id, nil, bo) }
            return nil
        }
        if let e = early { return e }
        var r: [String: Any]? = nil
        var err: String? = nil
        var next: Date
        do { r = try query(); next = Date().addingTimeInterval(Double(lockSeconds)) }
        catch let e as RateLimited {
            let s = min(e.seconds, lockSeconds)
            err = "官方限流，\(s) 秒后自动再查"; next = Date().addingTimeInterval(Double(s))
        } catch {
            err = errMsg(error); next = Date().addingTimeInterval(Double(lockSeconds))
        }
        return histLock.run {
            backoff[id] = next
            if var rr = r { rr["hist"] = record(id, rr); last[id] = rr }
            var save: [String: Any] = [:]
            for (k, v) in last { var c = v; c.removeValue(forKey: "hist"); c.removeValue(forKey: "error"); c.removeValue(forKey: "nextAt"); save[k] = c }
            for (k, v) in backoff {
                var c = (save[k] as? [String: Any]) ?? [:]
                c["lockUntil"] = Int64(v.timeIntervalSince1970 * 1000)
                save[k] = c
            }
            try? FS.write(cachePath, Json.str(save))
            return answer(id, err, next)
        }
    }

    static func answer(_ id: String, _ err: String?, _ next: Date) -> [String: Any] {
        var d = last[id] ?? ["session": NSNull(), "week": NSNull()]
        if d["hist"] == nil { d["hist"] = record(id, nil) }
        d["error"] = err.map { $0 as Any } ?? NSNull()
        d["nextAt"] = Int64(next.timeIntervalSince1970 * 1000)
        return d
    }

    static func window(_ d: [String: Any], _ name: String) -> Any {
        guard let w = d[name] as? [String: Any], let u = jsonDouble(w["utilization"]) else { return NSNull() }
        var reset: Int64 = 0
        if let s = w["resets_at"] as? String, let t = parseIso(s) { reset = Int64(t * 1000) }
        return ["used": Int(u.rounded(.toNearestOrEven)), "resetAt": reset] as [String: Any]
    }

    static var usageUrl: String {
        // 自动测试时可以指向本机的假接口
        if Env.test, let u = Env.get("CLAUDE_SWITCHER_USAGE_URL"), u.hasPrefix("http://127.0.0.1:") { return u }
        return "https://api.anthropic.com/api/oauth/usage"
    }

    static func query(_ dir: String, _ proxy: String?) throws -> [String: Any] {
        let t: (String, Int64)?
        do { t = try readToken(dir) } catch { throw AppError("读取登录信息失败：" + errMsg(error)) }
        guard let tt = t else { throw AppError("这个账号还没有登录桌面 App") }
        let tok = tt.0, exp = tt.1
        if exp > 0 && exp < nowMs() { throw AppError("登录已过期：切换到这个账号打开一次桌面 App 即可恢复") }
        let res: HttpResponse
        do {
            res = try Http.request(usageUrl, headers: ["Authorization": "Bearer " + tok, "anthropic-beta": "oauth-2025-04-20", "User-Agent": "claude-tools"], proxy: proxy, timeout: 20)
        } catch { throw AppError("连接官方服务器失败：" + errMsg(error)) }
        if res.status == 401 { throw AppError("登录已失效：切换到这个账号打开一次桌面 App 即可恢复") }
        if res.status == 429 {
            var sec = 300
            if let v = res.header("retry-after"), let n = Int(v.trimmingCharacters(in: .whitespaces)), n > 0 { sec = n }
            throw RateLimited(seconds: sec)
        }
        if res.status < 200 || res.status >= 300 { throw AppError("连接官方服务器失败：HTTP \(res.status)") }
        guard let d = Json.parse(res.body) as? [String: Any] else { throw AppError("官方返回的数据无法识别") }
        return ["session": window(d, "five_hour"), "week": window(d, "seven_day"), "at": fmtDate(Date(), "HH:mm:ss")]
    }
}
