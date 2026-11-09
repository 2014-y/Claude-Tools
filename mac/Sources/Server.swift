// 本机 HTTP 服务（只监听 127.0.0.1）+ 与 Windows 版相同的 /api/* 接口和 X-Token 校验
import Foundation
import AppKit
import Darwin

enum Server {
    static var token = ""
    static var url = ""
    static var port = 0
    static var urlFile: String { Settings.stateDir + "/url.txt" }
    static var isTest: Bool { Env.test }

    // ---------------- 监听 ----------------
    static func start() throws {
        FS.mkdirs(Settings.stateDir)
        let tokenFile = Settings.stateDir + "/token.txt"
        token = FS.readText(tokenFile)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if token.count < 32 {
            token = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
            try FS.write(tokenFile, token)
            chmod(tokenFile, 0o600)
        }
        var fd: Int32 = -1
        let ports: [Int] = (isTest ? [] : Array(38517..<38527)) + [0]
        for p in ports {
            let s = socket(AF_INET, SOCK_STREAM, 0)
            if s < 0 { continue }
            var yes: Int32 = 1
            setsockopt(s, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))
            var addr = sockaddr_in()
            addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            addr.sin_family = sa_family_t(AF_INET)
            addr.sin_port = in_port_t(UInt16(p).bigEndian)
            addr.sin_addr.s_addr = inet_addr("127.0.0.1")
            let ok = withUnsafePointer(to: &addr) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(s, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
            if ok != 0 || listen(s, 64) != 0 { close(s); continue }
            var a2 = sockaddr_in(); var len = socklen_t(MemoryLayout<sockaddr_in>.size)
            _ = withUnsafeMutablePointer(to: &a2) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(s, $0, &len) } }
            port = Int(UInt16(bigEndian: a2.sin_port))
            fd = s
            break
        }
        if fd < 0 { throw AppError("无法启动本地服务") }
        url = "http://127.0.0.1:\(port)/?t=" + token
        try FS.write(urlFile, url)
        let listener = fd
        let th = Thread {
            while true {
                let c = accept(listener, nil, nil)
                if c < 0 { if errno == EINTR { continue }; usleep(10_000); continue }
                var one: Int32 = 1
                setsockopt(c, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
                DispatchQueue.global(qos: .userInitiated).async { Server.serve(c) }
            }
        }
        th.stackSize = 1 << 20
        th.start()
    }

    // ---------------- 一个连接 ----------------
    struct Request { var method = "", path = "", query: [String: String] = [:], headers: [String: String] = [:], body = Data() }

    static func readRequest(_ c: Int32) -> Request? {
        var tv = timeval(tv_sec: 30, tv_usec: 0)
        setsockopt(c, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        var buf = Data()
        var tmp = [UInt8](repeating: 0, count: 65536)
        var headerEnd = -1
        while headerEnd < 0 {
            let n = recv(c, &tmp, tmp.count, 0)
            if n <= 0 { return nil }
            buf.append(contentsOf: tmp[0..<n])
            if let r = buf.range(of: Data([13, 10, 13, 10])) { headerEnd = r.upperBound }
            if buf.count > 1 << 20 { return nil }
        }
        let head = String(decoding: buf[0..<headerEnd], as: UTF8.self)
        var lines = head.components(separatedBy: "\r\n")
        let first = lines.removeFirst().split(separator: " ")
        guard first.count >= 2 else { return nil }
        var req = Request()
        req.method = String(first[0])
        let target = String(first[1])
        if let q = target.firstIndex(of: "?") {
            req.path = String(target[..<q])
            for kv in target[target.index(after: q)...].split(separator: "&") {
                let parts = kv.split(separator: "=", maxSplits: 1).map(String.init)
                let k = parts[0].removingPercentEncoding ?? parts[0]
                let v = parts.count > 1 ? (parts[1].replacingOccurrences(of: "+", with: " ").removingPercentEncoding ?? parts[1]) : ""
                req.query[k] = v
            }
        } else { req.path = target }
        for l in lines where !l.isEmpty {
            guard let i = l.firstIndex(of: ":") else { continue }
            req.headers[l[..<i].lowercased()] = l[l.index(after: i)...].trimmingCharacters(in: .whitespaces)
        }
        let len = Int(req.headers["content-length"] ?? "0") ?? 0
        if len > 64 << 20 { return nil }
        var body = buf.count > headerEnd ? Data(buf[headerEnd...]) : Data()
        while body.count < len {
            let n = recv(c, &tmp, min(tmp.count, len - body.count), 0)
            if n <= 0 { return nil }
            body.append(contentsOf: tmp[0..<n])
        }
        req.body = body
        return req
    }

    static func send(_ c: Int32, _ code: Int, _ type: String, _ data: Data, _ extra: [String: String] = [:]) {
        let reason = code == 200 ? "OK" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : "Error"
        var h = "HTTP/1.1 \(code) \(reason)\r\nContent-Type: \(type)\r\nContent-Length: \(data.count)\r\nConnection: close\r\n"
        h += "Cache-Control: " + (extra["Cache-Control"] ?? "no-store") + "\r\n\r\n"
        var all = Data(h.utf8); all.append(data)
        all.withUnsafeBytes { (p: UnsafeRawBufferPointer) in
            var off = 0
            while off < p.count {
                let n = Darwin.send(c, p.baseAddress! + off, p.count - off, 0)
                if n <= 0 { if errno == EINTR { continue }; break }
                off += n
            }
        }
    }

    static func serve(_ c: Int32) {
        defer { close(c) }
        guard let req = readRequest(c) else { return }
        let path = req.path
        if path == "/" || path == "/index.html" {
            if req.query["t"] != token { send(c, 403, "text/plain", Data("forbidden".utf8)); return }
            send(c, 200, "text/html; charset=utf-8", Data(Res.ui().utf8)); return
        }
        if path == "/favicon.ico" || path == "/logo.png" { send(c, 200, "image/png", Res.data("logo.png")); return }
        if path == "/font.woff2" { send(c, 200, "font/woff2", Res.data("font.woff2"), ["Cache-Control": "max-age=31536000"]); return }
        guard path.hasPrefix("/api/"), req.headers["x-token"] == token, req.method == "POST" else {
            send(c, 403, "text/plain", Data("forbidden".utf8)); return
        }
        var out: [String: Any]
        do {
            let a = req.body.isEmpty ? [:] : ((Json.parse(req.body) as? [String: Any]) ?? [:])
            let r = try Api.call(String(path.dropFirst(5)), a)
            out = ["ok": true, "data": r ?? NSNull()]
        } catch {
            out = ["ok": false, "error": errMsg(error)]
        }
        send(c, 200, "application/json; charset=utf-8", Data(Json.str(out).utf8))
    }
}

// ---------------- 内置资源 ----------------
enum Res {
    static let lock = Lock()
    static var cache: [String: Data] = [:]
    static func path(_ name: String) -> String? {
        if let dev = Env.get("CLAUDE_SWITCHER_RES"), FS.isFile(dev + "/" + name) { return dev + "/" + name }
        let base = (name as NSString).deletingPathExtension, ext = (name as NSString).pathExtension
        return Bundle.main.path(forResource: base, ofType: ext)
    }
    static func data(_ name: String) -> Data {
        lock.run {
            if let d = cache[name] { return d }
            let d = path(name).flatMap { FS.read($0) } ?? Data()
            cache[name] = d
            return d
        }
    }
    static func ui() -> String {
        if let dev = Env.get("CLAUDE_SWITCHER_UI"), FS.isFile(dev) { return FS.readText(dev) ?? "" }
        return String(decoding: data("ui.html"), as: UTF8.self)
    }
}

// ---------------- 接口 ----------------
enum Api {
    static var lastPing = Date()

    static func S(_ a: [String: Any], _ k: String) -> String {
        guard let v = a[k], !(v is NSNull) else { return "" }
        if let s = v as? String { return s }
        if let n = v as? NSNumber { return isJsonBool(n) ? (n.boolValue ? "True" : "False") : n.stringValue }
        return "\(v)"
    }

    static func desktopName(_ id: String) -> String { DesktopStore.load().first { $0.id == id }?.remark ?? id }
    static func cliName(_ id: String) -> String { Store.load().first { $0.folder == id }?.remark ?? id }

    // 记录日志的执行包装
    static func logged(_ action: String, _ target: String, _ detail: (() -> String)? = nil, _ act: () throws -> Void) throws {
        let t0 = Date()
        do {
            try act()
            OpLog.add(action, target, true, detail?() ?? "", Int64(Date().timeIntervalSince(t0) * 1000))
        } catch {
            OpLog.add(action, target, false, errMsg(error), Int64(Date().timeIntervalSince(t0) * 1000))
            throw error
        }
    }

    static func doSwitch(_ id: String) throws {
        let cur = DesktopStore.load().first { $0.active }
        let target = desktopName(id)
        try logged("切换桌面账号", (cur?.remark ?? "?") + " → " + target, { DesktopStore.lastSync }) { try DesktopStore.switchTo(id) }
    }

    static var buildId: String {
        let p = Settings.exePath()
        return String(Int64((FS.mtime(p) ?? Date(timeIntervalSince1970: 0)).timeIntervalSince1970 * 1000))
    }

    static func call(_ name: String, _ a: [String: Any]) throws -> Any? {
        switch name {
        case "ui/show": AppUI.showFromAnyThread(); return nil
        case "proxy/get": return ProxyPool.get()
        case "proxy/save":
            let r = try ProxyPool.save(S(a, "cfg"))
            OpLog.add("代理设置", "保存", true, "代理池 \(ProxyPool.items(r).count) 个", 0)
            return r
        case "proxy/import": return ProxyPool.importText(S(a, "text"))
        case "proxy/test": return ProxyPool.test(S(a, "id"))
        case "proxy/testUrl": return try ProxyPool.testUrl(S(a, "url"))
        case "ui/theme": AppUI.setTheme(dark: S(a, "dark") != "0"); return nil
        case "ping": lastPing = Date(); return buildId
        case "state": lastPing = Date(); return state()

        case "desktop/switch": try doSwitch(S(a, "id")); return state()
        case "desktop/best":
            guard let best = bestAccount() else { throw AppError("没有可以切换的其他账号") }
            try doSwitch(best.id); return state()
        case "desktop/add":
            try logged("添加桌面账号", S(a, "remark"), { DesktopStore.lastSync }) { try DesktopStore.addNew(S(a, "remark")) }
            return state()
        case "desktop/rename":
            let old = desktopName(S(a, "id"))
            try logged("改备注", old + " → " + S(a, "remark")) { try DesktopStore.rename(S(a, "id"), S(a, "remark")) }
            return state()
        case "desktop/delete":
            try logged("删除桌面账号", desktopName(S(a, "id"))) { try DesktopStore.delete(S(a, "id")) }
            return state()
        case "desktop/open": try DesktopStore.openDir(S(a, "id")); return nil
        case "desktop/merge":
            var msg = ""
            try logged("合并会话记录", "所有桌面账号", { msg }) { msg = try DesktopStore.mergeAll() }
            var st = state(); st["mergeResult"] = msg; return st
        case "desktop/launch": try logged("打开桌面 App", "") { try DesktopStore.launchDesktop() }; return state()

        case "cli/add": try logged("添加终端账号", S(a, "remark")) { try Store.create(S(a, "remark")) }; return state()
        case "cli/rename":
            guard let acc = Store.load().first(where: { $0.folder == S(a, "id") }) else { throw AppError("找不到这个账号") }
            if acc.isDefault { throw AppError("原始账号不能改备注") }
            try logged("改备注", acc.remark + " → " + S(a, "remark")) { try Store.setRemark(acc, S(a, "remark")) }
            return state()
        case "cli/delete":
            guard let acc = Store.load().first(where: { $0.folder == S(a, "id") }) else { throw AppError("找不到这个账号") }
            try logged("删除终端账号", acc.remark) { try Store.delete(acc) }
            return state()
        case "cli/repair": try logged("修复共享链接", "") { try Store.repairAll() }; return state()
        case "cli/launch":
            let mode = S(a, "mode")
            let label = mode == "resume" ? "（选历史）" : mode == "continue" ? "（继续上次）" : "（新对话）"
            try logged("启动终端", cliName(S(a, "id")) + label + " @ " + S(a, "dir")) { try Cli.launch(S(a, "id"), S(a, "dir"), mode) }
            var st = state()
            if isTest_ { st["script"] = Cli.lastScript }
            return st
        case "pickDir": return AppUI.pickDir(S(a, "start"))
        case "cli/status": return Cli.status(true)
        case "cli/usage": return try Cli.usage(S(a, "id"))
        case "desktop/usage": return try desktopUsage(S(a, "id"))
        case "cli/install":
            try logged("安装 Claude Code", "官方安装脚本") { try Cli.install() }
            return isTest_ ? Cli.lastScript : nil
        case "openPath":
            let p = S(a, "path")
            if !FS.isDir(p) { throw AppError("目录不存在：" + p) }
            if !isTest_ { _ = runProcess("/usr/bin/open", [p], timeout: 10) }
            return nil

        case "sessions": return sessionList()
        case "stats": return TokenStats.query(a, S)
        case "pricing": return Pricing.allJson()
        case "pricing/set":
            let raw = (a["list"] as? [Any]) ?? []
            let list: [Price] = raw.compactMap { x in
                guard let d = x as? [String: Any] else { return nil }
                let k = S(d, "Key").trimmingCharacters(in: .whitespacesAndNewlines)
                if k.isEmpty { return nil }
                return Price(Key: k, Input: jsonDouble(d["Input"]) ?? 0, Output: jsonDouble(d["Output"]) ?? 0, CacheRead: jsonDouble(d["CacheRead"]) ?? 0,
                             CacheWrite5m: jsonDouble(d["CacheWrite5m"]) ?? 0, CacheWrite1h: jsonDouble(d["CacheWrite1h"]) ?? 0)
            }
            try Pricing.save(list)
            try logged("修改定价", "\(list.count) 个模型") { }
            return Pricing.allJson()
        case "pricing/reset": Pricing.reset(); return Pricing.allJson()
        case "storage/scan": return StorageScan.scan()
        case "cache/scan": return CacheCleaner.scan()
        case "cache/clean":
            let ids = ((a["ids"] as? [Any]) ?? []).map { "\($0)" }
            var msg = ""
            try logged("清理缓存", "\(ids.count) 项", { msg }) { msg = try CacheCleaner.clean(ids) }
            var r = CacheCleaner.scan(); r["message"] = msg; return r
        case "storage/clean":
            var freed: Int64 = 0
            let id = S(a, "id")
            try logged("清理缓存", id.isEmpty ? "全部已保存账号" : desktopName(id), { "释放 " + fmtMB(freed) + " MB" }) { freed = try StorageScan.cleanCache(id) }
            return StorageScan.scan()
        case "i18n/s2t": return S2T.map()
        case "logs": return OpLog.read(500)
        case "logs/clear": OpLog.clear(); return nil
        case "logs/add":
            let act = S(a, "action")
            if act != "刷新全部数据" && act != "查询官方额度" && act != "自动查询官方额度" { throw AppError("不支持的日志类型") }
            OpLog.add(act, S(a, "target"), S(a, "ok") != "0", S(a, "detail"), Int64(S(a, "ms")) ?? 0)
            return nil
        case "settings": return Settings.all()
        case "settings/set": try Settings.set(S(a, "key"), a["value"]); return Settings.all()
        case "quit":
            DispatchQueue.global().asyncAfter(deadline: .now() + 0.3) { AppUI.quit() }
            return nil

        // ---- 以下只在自动测试时可用 ----
        case "test/proxyFor" where isTest_: return ProxyPool.forKey(S(a, "key"), S(a, "kind")).map { $0 as Any } ?? NSNull()
        case "test/eval" where isTest_: return AppUI.evalJs(S(a, "js"))
        case "test/snapshot" where isTest_: return AppUI.snapshot(S(a, "name"))
        case "test/window" where isTest_: return AppUI.windowInfo()
        case "test/close" where isTest_: DispatchQueue.main.async { AppUI.delegate?.window?.performClose(nil) }; return nil
        default: break
        }
        throw AppError("未知接口：" + name)
    }

    static var isTest_: Bool { Env.test }

    static func bestAccount() -> DesktopProfile? {
        // 7 天用量最低、其次 5 小时用量最低的非当前账号
        let list = DesktopStore.load().filter { !$0.active }.map { p -> (DesktopProfile, Int, Int) in
            let q = QuotaReader.read(p.dir)
            return (p, q == nil || q!.sevenDay < 0 ? 999 : q!.sevenDay, q == nil || q!.fiveHour < 0 ? 999 : q!.fiveHour)
        }
        return list.sorted { $0.1 != $1.1 ? $0.1 < $1.1 : $0.2 < $1.2 }.first?.0
    }

    static func quotaJson(_ q: Quota?) -> Any {
        guard let q = q else { return NSNull() }
        return ["fiveHour": q.fiveHour, "sevenDay": q.sevenDay, "t": q.t, "fiveReset": q.fiveReset, "sevenReset": q.sevenReset,
                "recent": q.recent, "samples": q.samples] as [String: Any]
    }

    static func state() -> [String: Any] {
        let profiles = DesktopStore.load()
        let desktop: [[String: Any]] = profiles.map { p in
            ["id": p.id, "remark": p.remark, "email": p.email, "active": p.active, "lastUsed": fmtDate(p.lastUsed, "yyyy/MM/dd HH:mm"),
             "sessions": p.sessions, "dir": p.dir, "loggedIn": p.loggedIn, "quota": quotaJson(QuotaReader.read(p.dir)), "live": LiveUsage.available(p.dir)]
        }
        let cli: [[String: Any]] = Store.load().map { c in
            ["id": c.folder, "remark": c.remark, "email": c.email, "loggedIn": c.loggedIn, "isDefault": c.isDefault, "dir": c.dir]
        }
        let best = bestAccount()
        return ["desktop": desktop, "desktopRunning": DesktopStore.isRunning(), "cli": cli, "recent": projectDirs(), "cliStatus": Cli.status(false),
                "best": best.map { $0.id as Any } ?? NSNull(), "switchesToday": OpLog.countToday("切换桌面账号"), "settings": Settings.all(),
                "lastSync": DesktopStore.lastSync, "build": buildId, "now": nowMs()]
    }

    static func sessionList() -> [[String: Any]] {
        Sessions.all().prefix(1000).map { s in
            ["id": s.id ?? "", "cli": s.cliId ?? "", "title": s.title ?? "", "model": s.model ?? "", "cwd": s.cwd ?? "",
             "account": s.account, "profile": s.profile, "created": s.created, "last": s.lastActivity, "archived": s.archived]
        }
    }

    static func desktopUsage(_ id: String) throws -> [String: Any] {
        guard let p = DesktopStore.load().first(where: { $0.id == id }) else { throw AppError("找不到这个账号") }
        let dir = p.dir, pid = p.id
        return LiveUsage.cached(pid) { try LiveUsage.query(dir, ProxyPool.forKey("d:" + pid, "quota")) }
    }

    // 常用项目文件夹：最近启动过的 + 桌面 App 会话里用过的（不含临时会话）
    static func projectDirs() -> [String] {
        var list = Store.loadRecent()
        for s in Sessions.all() {
            let c = s.cwd ?? ""
            if c.isEmpty || c.contains("/scratch-workspaces/") { continue }
            if list.contains(where: { $0.lowercased() == c.lowercased() }) { continue }
            if FS.isDir(c) { list.append(c) }
        }
        return Array(list.prefix(12))
    }
}

// ---------------- 简体 → 繁体字符对照表（用系统自带的 ICU 转换） ----------------
enum S2T {
    static let lock = Lock()
    static var cached: [String: String]? = nil
    static func map() -> [String: String] {
        lock.run {
            if let c = cached { return c }
            var d: [String: String] = [:]
            let t = StringTransform(rawValue: "Hans-Hant")
            var chars: [String] = []
            for c in 0x4E00...0x9FA5 { if let u = Unicode.Scalar(UInt32(c)) { chars.append(String(Character(u))) } }
            let joined = chars.joined()
            if let out = joined.applyingTransform(t, reverse: false), out.count == joined.count {
                for (a, b) in zip(chars, out.map { String($0) }) where a != b { d[a] = b }
            } else {
                for a in chars { if let b = a.applyingTransform(t, reverse: false), b != a, b.count == 1 { d[a] = b } }
            }
            cached = d
            return d
        }
    }
}
