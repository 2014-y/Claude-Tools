// 代理设置：代理池（按账号绑定 / 权重随机 / 轮询 / 固定）、全局上游代理、自动小黑屋开关与应用范围。
// 代理用于：本工具查询官方额度、终端 Claude Code、（可选）Claude 桌面 App。只支持 HTTP 代理。
import Foundation
import CFNetwork

// ---------------- HTTP 客户端（可走 HTTP 代理，支持代理用户名密码） ----------------
struct HttpResponse {
    var status: Int
    var body: Data
    var headers: [String: String]
    func header(_ k: String) -> String? { headers[k.lowercased()] }
}

final class ProxyAuthDelegate: NSObject, URLSessionTaskDelegate {
    let user: String?, pass: String?
    var proxyAuthFailed = false
    init(user: String?, pass: String?) { self.user = user; self.pass = pass }
    func urlSession(_ session: URLSession, task: URLSessionTask, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        if challenge.protectionSpace.isProxy() {
            if let u = user, challenge.previousFailureCount == 0 {
                completionHandler(.useCredential, URLCredential(user: u, password: pass ?? "", persistence: .forSession))
            } else {
                proxyAuthFailed = true
                completionHandler(.cancelAuthenticationChallenge, nil)
            }
            return
        }
        completionHandler(.performDefaultHandling, nil)
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil) // 不跟随跳转，直接返回原始响应
    }
}

enum Http {
    static func request(_ url: String, headers: [String: String] = [:], proxy: String?, timeout: Double) throws -> HttpResponse {
        guard let u = URL(string: url) else { throw AppError("地址无效：" + url) }
        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = timeout
        cfg.timeoutIntervalForResource = timeout + 5
        cfg.requestCachePolicy = .reloadIgnoringLocalCacheData
        var user: String? = nil, pass: String? = nil
        if let p = proxy, !p.isEmpty {
            let px = try ProxyPool.parts(p)
            user = px.user; pass = px.pass
            var d: [AnyHashable: Any] = [
                kCFNetworkProxiesHTTPEnable as String: 1, kCFNetworkProxiesHTTPProxy as String: px.host, kCFNetworkProxiesHTTPPort as String: px.port,
                kCFNetworkProxiesHTTPSEnable as String: 1, kCFNetworkProxiesHTTPSProxy as String: px.host, kCFNetworkProxiesHTTPSPort as String: px.port
            ]
            if let us = px.user { d[kCFProxyUsernameKey as String] = us; d[kCFProxyPasswordKey as String] = px.pass ?? "" }
            cfg.connectionProxyDictionary = d
        }
        let del = ProxyAuthDelegate(user: user, pass: pass)
        let session = URLSession(configuration: cfg, delegate: del, delegateQueue: nil)
        defer { session.finishTasksAndInvalidate() }
        var req = URLRequest(url: u)
        req.httpMethod = "GET"
        for (k, v) in headers { req.setValue(v, forHTTPHeaderField: k) }
        let sem = DispatchSemaphore(value: 0)
        var result: HttpResponse? = nil
        var error: Error? = nil
        session.dataTask(with: req) { data, resp, err in
            if let h = resp as? HTTPURLResponse {
                var hs: [String: String] = [:]
                for (k, v) in h.allHeaderFields { hs[String(describing: k).lowercased()] = String(describing: v) }
                result = HttpResponse(status: h.statusCode, body: data ?? Data(), headers: hs)
            } else { error = err }
            sem.signal()
        }.resume()
        if sem.wait(timeout: .now() + timeout + 10) == .timedOut { session.invalidateAndCancel(); throw AppError("连接超时") }
        if let r = result { return r }
        if del.proxyAuthFailed { throw AppError("代理需要用户名密码（或用户名密码不对）") }
        throw AppError(describe(error))
    }

    static func describe(_ e: Error?) -> String {
        guard let e0 = e else { return "未知错误" }
        let e = e0 as NSError
        if e.domain == NSURLErrorDomain {
            switch e.code {
            case NSURLErrorTimedOut: return "连接超时"
            case NSURLErrorCannotConnectToHost: return "无法连接（对方没有响应或端口未开放）"
            case NSURLErrorCannotFindHost, NSURLErrorDNSLookupFailed: return "找不到服务器地址"
            case NSURLErrorNotConnectedToInternet: return "电脑没有联网"
            case NSURLErrorNetworkConnectionLost: return "连接中断"
            case NSURLErrorUserCancelledAuthentication, NSURLErrorUserAuthenticationRequired: return "代理需要用户名密码（或用户名密码不对）"
            case NSURLErrorSecureConnectionFailed, NSURLErrorServerCertificateUntrusted, NSURLErrorServerCertificateHasBadDate: return "安全连接失败（TLS）"
            default: break
            }
        }
        if e.domain == kCFErrorDomainCFNetwork as String {
            if e.code == 310 || e.code == 311 { return "代理无法连到官方服务器" }
            if e.code == 407 { return "代理需要用户名密码（或用户名密码不对）" }
        }
        return e.localizedDescription
    }
}

enum ProxyPool {
    static let lock = Lock()
    static var filePath: String { Settings.stateDir + "/proxies.json" }
    static var cache: [String: Any]? = nil
    static var sticky: [String: (String, Date)] = [:]
    static var rr = 0

    static func defaults() -> [String: Any] {
        ["enabled": false, "mode": "weighted", "rotateSec": 300, "globalEnabled": false, "globalUrl": "", "qos": true,
         "applyQuota": true, "applyCli": true, "applyDesktop": false, "items": [Any]()]
    }

    static func get() -> [String: Any] {
        lock.run {
            if let c = cache { return c }
            var d = defaults()
            if let f = Json.parseFile(filePath) as? [String: Any] { for (k, v) in f { d[k] = v } }
            d["items"] = items(d)
            cache = d
            return d
        }
    }

    static func items(_ d: [String: Any]) -> [[String: Any]] { ((d["items"] as? [Any]) ?? []).compactMap { $0 as? [String: Any] } }
    static func str(_ d: [String: Any], _ k: String) -> String {
        guard let v = d[k], !(v is NSNull) else { return "" }
        if let s = v as? String { return s }
        if let n = v as? NSNumber { return isJsonBool(n) ? (n.boolValue ? "True" : "False") : n.stringValue }
        return "\(v)"
    }
    static func bool(_ d: [String: Any], _ k: String) -> Bool { jsonTrue(d[k]) }
    static func int(_ d: [String: Any], _ k: String, _ def: Int) -> Int { Int(str(d, k).trimmingCharacters(in: .whitespaces)) ?? def }

    struct Parts { var scheme: String; var host: String; var port: Int; var user: String?; var pass: String?; var rawUserInfo: String }
    static let hostRe = Rx("^[A-Za-z0-9.\\-\\[\\]:]+$")

    static func parts(_ url0: String) throws -> Parts {
        var url = url0.trimmingCharacters(in: .whitespacesAndNewlines)
        if url.isEmpty { throw AppError("代理地址不能为空") }
        if !url.contains("://") { url = "http://" + url }
        guard let c = URLComponents(string: url), let scheme = c.scheme?.lowercased(), var host = c.host, !host.isEmpty else { throw AppError("代理地址格式不对：" + url) }
        if scheme != "http" && scheme != "https" { throw AppError("目前只支持 HTTP 代理：" + url) }
        if host.contains(":") && !host.hasPrefix("[") { host = "[" + host + "]" }
        if !hostRe.test(host) { throw AppError("代理地址格式不对：" + url) }
        let port = c.port ?? (scheme == "https" ? 443 : 80)
        if port <= 0 || port > 65535 { throw AppError("代理地址格式不对：" + url) }
        var raw = ""
        if let u = c.percentEncodedUser, !u.isEmpty { raw = u; if let p = c.percentEncodedPassword { raw += ":" + p } }
        return Parts(scheme: scheme, host: host.lowercased(), port: port, user: c.user, pass: c.password, rawUserInfo: raw)
    }

    // 规范化：支持 host:port、http://host:port、http://用户名:密码@host:port
    static func normalize(_ url: String) throws -> String {
        let p = try parts(url)
        return p.scheme + "://" + (p.rawUserInfo.isEmpty ? "" : p.rawUserInfo + "@") + p.host + ":" + String(p.port)
    }

    static func save(_ cfgJson: String) throws -> [String: Any] {
        guard let d = Json.parse(cfgJson) as? [String: Any] else { throw AppError("设置格式不对") }
        var full = defaults()
        for k in Array(full.keys) { if let v = d[k] { full[k] = v } }
        var out: [[String: Any]] = []
        var seen = Set<String>()
        for it in items(full) {
            let url = try normalize(str(it, "url"))
            var id = str(it, "id")
            if id.isEmpty || seen.contains(id) { id = String(UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased().prefix(8)) }
            seen.insert(id)
            let w = max(1, min(100, int(it, "weight", 1)))
            out.append(["id": id, "name": str(it, "name"), "url": url, "weight": w, "bind": str(it, "bind"),
                        "status": str(it, "status"), "latency": int(it, "latency", 0), "checkedAt": str(it, "checkedAt"), "error": str(it, "error")])
        }
        full["items"] = out
        full["rotateSec"] = max(10, min(86400, int(full, "rotateSec", 300)))
        let gu = str(full, "globalUrl").trimmingCharacters(in: .whitespaces)
        full["globalUrl"] = try gu.isEmpty ? "" : normalize(gu)
        if bool(full, "globalEnabled") && str(full, "globalUrl").isEmpty { throw AppError("开启全局上游代理前请先填写代理地址") }
        write(full)
        return full
    }

    static func write(_ d: [String: Any]) {
        lock.run {
            try? FS.write(filePath, Json.str(d))
            cache = d
            sticky.removeAll()
        }
    }

    static func newId() -> String { String(UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased().prefix(8)) }

    static func importText(_ text: String) -> [String: Any] {
        var d = get()
        var list = items(d)
        var added = 0
        var bad: [String] = []
        var exist = Set(list.map { str($0, "url") })
        for raw in text.components(separatedBy: "\n") {
            let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if line.isEmpty || line.hasPrefix("#") { continue }
            do {
                let url = try normalize(line)
                if exist.contains(url) { continue }
                exist.insert(url)
                list.append(["id": newId(), "name": "", "url": url, "weight": 1, "bind": "", "status": "", "latency": 0, "checkedAt": "", "error": ""])
                added += 1
            } catch { bad.append(line) }
        }
        d["items"] = list
        write(d)
        return ["cfg": d, "added": added, "bad": bad]
    }

    // 检测：通过代理访问官方接口地址，收到任何 HTTP 响应即视为可用
    static func check(_ it0: [String: Any]) -> [String: Any] {
        var it = it0
        let t0 = Date()
        do {
            let r = try Http.request(checkUrl, headers: ["User-Agent": "claude-tools"], proxy: str(it, "url"), timeout: 12)
            if r.status == 407 { throw AppError("代理需要用户名密码（或用户名密码不对）") }
            if r.status == 502 || r.status == 504 { throw AppError("代理无法连到官方服务器（\(r.status)）") }
            it["status"] = "ok"; it["error"] = ""
        } catch { it["status"] = "fail"; it["error"] = errMsg(error) }
        it["latency"] = Int(Date().timeIntervalSince(t0) * 1000)
        it["checkedAt"] = fmtDate(Date(), "HH:mm:ss")
        return it
    }

    static var checkUrl: String {
        if Env.test, let u = Env.get("CLAUDE_SWITCHER_PROXY_CHECK_URL") { return u }
        return "https://api.anthropic.com/"
    }

    static func test(_ id: String) -> [String: Any] {
        var d = get()
        var list = items(d)
        let idx = list.indices.filter { id == "*" || str(list[$0], "id") == id }
        let g = DispatchGroup()
        let rl = Lock()
        for i in idx {
            let it = list[i]
            g.enter()
            DispatchQueue.global().async {
                let r = ProxyPool.check(it)
                rl.run { list[i] = r }
                g.leave()
            }
        }
        _ = g.wait(timeout: .now() + 30)
        rl.run { d["items"] = list }
        write(d)
        return d
    }

    static func testUrl(_ url: String) throws -> [String: Any] { check(["url": try normalize(url)]) }

    // 给某个账号挑代理。key：桌面账号 "d:<id>"，终端账号 "c:<文件夹>"；kind：quota / cli / desktop
    static func forKey(_ key: String, _ kind: String) -> String? {
        let d = get()
        if kind == "quota" && !bool(d, "applyQuota") { return nil }
        if kind == "cli" && !bool(d, "applyCli") { return nil }
        if kind == "desktop" && !bool(d, "applyDesktop") { return nil }
        if bool(d, "enabled") {
            let usable = items(d).filter { str($0, "status") != "fail" }
            if let b = usable.first(where: { str($0, "bind") == key }) { return str(b, "url") }
            let free = usable.filter { str($0, "bind").isEmpty }
            if !free.isEmpty { return str(pick(key, free, str(d, "mode"), int(d, "rotateSec", 300)), "url") }
        }
        if bool(d, "globalEnabled") && !str(d, "globalUrl").isEmpty { return str(d, "globalUrl") }
        return nil
    }

    static func pick(_ key: String, _ free: [[String: Any]], _ mode: String, _ rotateSec: Int) -> [String: Any] {
        lock.run {
            if mode != "fixed", let s = sticky[key], s.1 > Date(), let keep = free.first(where: { str($0, "id") == s.0 }) { return keep }
            var chosen: [String: Any]
            if mode == "fixed" { chosen = free[0] }
            else if mode == "roundrobin" { chosen = free[rr % free.count]; rr += 1 }
            else {
                let total = free.reduce(0) { $0 + max(1, int($1, "weight", 1)) }
                var r = Int.random(in: 0..<max(1, total))
                chosen = free[free.count - 1]
                for x in free { r -= max(1, int(x, "weight", 1)); if r < 0 { chosen = x; break } }
            }
            sticky[key] = (str(chosen, "id"), Date().addingTimeInterval(Double(rotateSec)))
            return chosen
        }
    }

    // 去掉用户名密码，用于桌面 App 的 --proxy-server 参数
    static func hostPort(_ url: String) throws -> String { let p = try parts(url); return p.scheme + "://" + p.host + ":" + String(p.port) }
}
