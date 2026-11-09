// 设置、操作日志、用量配额、会话记录、存储扫描、缓存清理（对应 Windows 版 Extras.cs）
import Foundation

// ---------------- 设置 ----------------
enum Settings {
    static let lock = Lock()
    static var stateDir: String { Paths.stateDir }
    static var filePath: String { stateDir + "/settings.json" }
    static let agentLabel = "io.github.y2014.claudetools"
    static var agentPath: String { Paths.home + "/Library/LaunchAgents/" + agentLabel + ".plist" }

    static let defaults: [String: Any] = [
        "trayMode": true,          // 关闭窗口后留在菜单栏（Mac 上默认开启）
        "confirmSwitch": true,
        "autoOpenAfterSwitch": true,
        "maskDefault": true,
        "notify": true,
        "syncSessions": true,
        "autoMerge": true,
        "syncClientSettings": true,
        "userName": "",
        "hiddenMenus": "",
        "weeklyResets": ""
    ]
    private static var data: [String: Any]?

    private static func load() -> [String: Any] {
        if let d = data { return d }
        var d = defaults
        if let f = Json.parseFile(filePath) as? [String: Any] { for (k, v) in f { d[k] = v } }
        data = d
        return d
    }
    private static func save(_ d: [String: Any]) {
        data = d
        try? FS.write(filePath, Json.str(d))
    }

    static func bool(_ k: String) -> Bool { lock.run { jsonTrue(load()[k]) } }
    static func str(_ k: String) -> String {
        lock.run {
            guard let v = load()[k], !(v is NSNull) else { return "" }
            return (v as? String) ?? "\(v)"
        }
    }

    static func set(_ k: String, _ v: Any?) throws {
        try lock.run {
            var d = load()
            if k == "autoStart" {
                let on = jsonTrue(v)
                OpLog.add("开机自启", on ? "开启" : "关闭", true, "", 0)
                try setAutoStart(on)
                d[k] = on
                save(d)
                return
            }
            guard defaults[k] != nil else { throw AppError("未知设置：" + k) }
            d[k] = v ?? NSNull()
            save(d)
        }
    }

    static func all() -> [String: Any] {
        lock.run {
            var d = load()
            d["autoStart"] = autoStart
            d["stateDir"] = stateDir
            d["profilesDir"] = DesktopStore.profilesDir
            d["cliDir"] = Store.accountsDir
            d["exe"] = Bundle.main.bundlePath
            d["version"] = "2.0.0"
            d["platform"] = "mac"
            return d
        }
    }

    // 登录时自动启动：用 LaunchAgent（~/Library/LaunchAgents），带 --tray 参数只在菜单栏启动
    static var autoStart: Bool { FS.isFile(agentPath) }

    static func refreshAutoStart() {
        if autoStart || bool("autoStart") { try? setAutoStart(true, quiet: true) }
    }

    static func exePath() -> String { Bundle.main.executablePath ?? CommandLine.arguments[0] }

    static func setAutoStart(_ on: Bool, quiet: Bool = false) throws {
        if !on { try? FS.remove(agentPath); return }
        let exe = exePath()
        if exe.contains("/AppTranslocation/") || exe.hasPrefix("/Volumes/") {
            if quiet { return }
            throw AppError("请先把 Claude Tools 拖到「应用程序」文件夹，再开启登录时自动启动")
        }
        let plist: [String: Any] = [
            "Label": agentLabel,
            "ProgramArguments": [exe, "--tray"],
            "RunAtLoad": true,
            "LimitLoadToSessionType": "Aqua",
            "ProcessType": "Interactive"
        ]
        let data = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
        FS.mkdirs(FS.parent(agentPath))
        try data.write(to: URL(fileURLWithPath: agentPath), options: .atomic)
    }
}

// ---------------- 操作日志 ----------------
enum OpLog {
    static let lock = Lock()
    static var filePath: String { Settings.stateDir + "/log.jsonl" }

    static func add(_ action: String, _ target: String, _ ok: Bool, _ detail: String, _ ms: Int64) {
        lock.run {
            let e: [String: Any] = ["t": fmtDate(Date(), "yyyy/MM/dd HH:mm:ss"), "action": action, "target": target, "ok": ok, "detail": detail, "ms": ms]
            FS.append(filePath, Json.str(e) + "\n")
        }
    }

    static func read(_ max: Int) -> [[String: Any]] {
        lock.run {
            guard let s = FS.readText(filePath) else { return [] }
            var r: [[String: Any]] = []
            for line in s.split(separator: "\n").reversed() {
                if r.count >= max { break }
                if let d = Json.parse(String(line)) as? [String: Any] { r.append(d) }
            }
            return r
        }
    }

    static func clear() { lock.run { try? FS.remove(filePath) } }

    static func countToday(_ action: String) -> Int {
        let today = fmtDate(Date(), "yyyy/MM/dd")
        return read(2000).filter { (($0["t"] as? String) ?? "").hasPrefix(today) && ($0["action"] as? String) == action && jsonTrue($0["ok"]) }.count
    }
}

// ---------------- 用量配额（桌面 App 自己记录的 5 小时 / 7 天用量） ----------------
final class Quota {
    var org = ""
    var fiveHour = -1, sevenDay = -1
    var t: Int64 = 0
    var fiveReset: Int64 = 0, sevenReset: Int64 = 0
    var recent: [[Int]] = []
    var samples: [[Int64]] = []
}

enum QuotaReader {
    static let lock = Lock()
    static var cache: [String: (Date, Quota)] = [:]

    struct Sample { let t: Int64; let org: String; let u: [String: Any] }

    static func val(_ u: [String: Any], _ k: String) -> Int? {
        guard let v = u[k], !(v is NSNull), let d = jsonDouble(v) else { return nil }
        return Int(d.rounded(.toNearestOrEven))
    }

    static func read(_ profileDir: String) -> Quota? {
        let p = profileDir + "/plan-usage-history.json"
        guard let mt = FS.mtime(p) else { return nil }
        if let c = lock.run({ cache[p] }), c.0 == mt { return c.1 }
        guard let root = Json.parseFile(p) as? [String: Any], let arr = root["samples"] as? [Any] else { return nil }
        var samples: [Sample] = []
        for x in arr {
            guard let s = x as? [String: Any], let t = jsonInt(s["t"]), let u = s["u"] as? [String: Any] else { continue }
            let org = (s["org"] as? String) ?? ""
            samples.append(Sample(t: t, org: org, u: u))
        }
        samples.sort { $0.t < $1.t }
        guard let last = samples.last else { return nil }
        let q = Quota()
        q.org = last.org; q.t = last.t
        if let v = val(last.u, "fh") { q.fiveHour = v }
        if let v = val(last.u, "sd") { q.sevenDay = v }
        let mine = samples.filter { $0.org == last.org }
        func est(_ key: String, _ window: Int64) -> Int64 {
            func v(_ i: Int) -> Int { val(mine[i].u, key) ?? 0 }
            let n = mine.count
            if n == 0 || v(n - 1) <= 0 { return 0 }
            var k = n - 1
            while k > 0 && v(k - 1) > 0 && v(k - 1) <= v(k) + 2 && mine[k].t - mine[k - 1].t < window { k -= 1 }
            var start = (k > 0 && v(k - 1) == 0 && mine[k].t - mine[k - 1].t <= 30 * 60000) ? mine[k - 1].t : mine[k].t
            start -= start % (30 * 60000)
            return start + window
        }
        q.fiveReset = est("fh", 5 * 3600 * 1000)
        q.sevenReset = 0
        if mine.count > 1 {
            for i in stride(from: mine.count - 1, to: 0, by: -1) {
                let a0 = val(mine[i - 1].u, "sd") ?? 0, b0 = val(mine[i].u, "sd") ?? 0
                if a0 - b0 >= 5 && b0 <= 2 {
                    var anchor = mine[i].t - mine[i].t % 3600000
                    let week: Int64 = 7 * 24 * 3600000, now = nowMs()
                    while anchor <= now { anchor += week }
                    q.sevenReset = anchor
                    break
                }
            }
        }
        let cut8 = last.t - 8 * 24 * 3600 * 1000
        var pf = -2, ps = -2
        for s in samples where s.org == last.org && s.t >= cut8 {
            let f1 = val(s.u, "fh") ?? -1, s1 = val(s.u, "sd") ?? -1
            if f1 == pf && s1 == ps { continue }
            pf = f1; ps = s1
            q.samples.append([s.t, Int64(f1), Int64(s1)])
        }
        let from = last.t - 7 * 24 * 3600 * 1000
        var bucket: Int64 = -1
        for s in samples where s.org == last.org && s.t >= from {
            let b = s.t / (3600 * 1000 * 3)
            if b == bucket { continue }
            bucket = b
            q.recent.append([Int((s.t - from) / 60000), val(s.u, "fh") ?? 0, val(s.u, "sd") ?? 0])
        }
        lock.run { cache[p] = (mt, q) }
        return q
    }
}

// ---------------- 会话记录（桌面 App 的 Claude Code 会话） ----------------
final class SessionInfo {
    var id: String?, cliId: String?, title: String?, model: String?, cwd: String?
    var org = "", account = "", profile = ""
    var created: Int64 = 0, lastActivity: Int64 = 0
    var archived = false
}

enum Sessions {
    static let strField = Rx("\"(sessionId|cliSessionId|title|model|cwd)\":\"((?:[^\"\\\\]|\\\\.)*)\"")
    static let numField = Rx("\"(createdAt|lastActivityAt)\":(\\d+)")
    static let archField = Rx("\"isArchived\":(true|false)")
    static let lock = Lock()
    static var cache: [String: (Date, SessionInfo)] = [:]

    static func head(_ file: String, _ max: Int = 16384) -> String {
        guard let h = FileHandle(forReadingAtPath: file) else { return "" }
        defer { h.closeFile() }
        let d = h.readData(ofLength: max)
        return String(decoding: d, as: UTF8.self)
    }

    static func parse(_ file: String) -> SessionInfo? {
        guard let mt = FS.mtime(file) else { return nil }
        if let c = lock.run({ cache[file] }), c.0 == mt { return c.1 }
        var h = head(file)
        if let r = h.range(of: "\"remoteMcpServersConfig\""), r.lowerBound > h.startIndex { h = String(h[..<r.lowerBound]) }
        let s = SessionInfo()
        for m in strField.all(h) {
            let v = Json.unescape(m[2])
            switch m[1] {
            case "sessionId": if s.id == nil { s.id = v }
            case "cliSessionId": if s.cliId == nil { s.cliId = v }
            case "title": if s.title == nil { s.title = v }
            case "model": if s.model == nil { s.model = v }
            case "cwd": if s.cwd == nil { s.cwd = v }
            default: break
            }
        }
        for m in numField.all(h) {
            let v = Int64(m[2]) ?? 0
            if m[1] == "createdAt" && s.created == 0 { s.created = v }
            if m[1] == "lastActivityAt" && s.lastActivity == 0 { s.lastActivity = v }
        }
        if let a = archField.first(h) { s.archived = a[1] == "true" }
        s.org = FS.name(FS.parent(file))
        lock.run { cache[file] = (mt, s) }
        return s
    }

    static func orgLabels(_ profiles: [DesktopProfile]) -> [String: String] {
        var map: [String: String] = [:]
        for p in profiles {
            if let q = QuotaReader.read(p.dir), !q.org.isEmpty, map[q.org] == nil { map[q.org] = p.remark }
        }
        return map
    }

    static func all() -> [SessionInfo] {
        let profiles = DesktopStore.load()
        let labels = orgLabels(profiles)
        var seen = Set<String>()
        var list: [SessionInfo] = []
        let deleted = deletedIds(profiles.map { $0.dir })
        for p in profiles {
            let root = p.dir + "/claude-code-sessions"
            guard FS.isDir(root) else { continue }
            for f in FS.filesRecursive(root, "local_*.json") {
                guard let s0 = parse(f), let id = s0.id, !seen.contains(id) else { continue }
                seen.insert(id)
                if deleted.contains(id.hasPrefix("local_") ? String(id.dropFirst(6)) : id) { continue }
                let s = SessionInfo()
                s.id = s0.id; s.cliId = s0.cliId; s.title = s0.title; s.model = s0.model; s.cwd = s0.cwd
                s.org = s0.org; s.created = s0.created; s.lastActivity = s0.lastActivity; s.archived = s0.archived
                s.account = labels[s.org] ?? ("其他账号 · " + String(s.org.prefix(8)))
                s.profile = p.remark
                list.append(s)
            }
        }
        return list.sorted { $0.lastActivity > $1.lastActivity }
    }

    static func deletedIds(_ dirs: [String]) -> Set<String> {
        var set = Set<String>()
        for d in dirs {
            let root = d + "/claude-code-sessions"
            guard FS.isDir(root) else { continue }
            for f in FS.filesRecursive(root, "deleted_*") { set.insert(String(FS.name(f).dropFirst(8))) }
        }
        return set
    }
}

// ---------------- 存储扫描 ----------------
enum StorageScan {
    // 桌面 App 数据目录里可安全删除的缓存（App 会自动重建）
    static let cacheDirs = ["Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache", "GrShaderCache", "ShaderCache", "GPUPersistentCache", "Crashpad", "logs"]

    static func cacheSize(_ dir: String) -> Int64 { cacheDirs.reduce(0) { $0 + FS.measure(dir + "/" + $1).0 } }

    static func item(_ kind: String, _ id: String, _ name: String, _ path: String, _ note: String, _ active: Bool = false, _ withCache: Bool = false) -> [String: Any] {
        let m = FS.measure(path)
        return ["kind": kind, "id": id, "name": name, "path": path, "note": note, "exists": FS.isDir(path),
                "size": m.0, "files": m.1, "active": active, "cache": withCache ? cacheSize(path) : Int64(0)]
    }

    static func scan() -> [String: Any] {
        let t0 = Date()
        var items: [[String: Any]] = []
        for p in DesktopStore.load() {
            items.append(item("desktop", p.id, p.remark, p.dir, p.active ? "桌面 App 账号 · 当前使用" : "桌面 App 账号存档", p.active, true))
        }
        items.append(item("projects", "", "对话记录（所有账号共用）", Store.mainDir + "/projects", "~/.claude/projects，会话内容和 Token 统计都来自这里"))
        items.append(item("cli", "", "终端账号目录", Store.accountsDir, "终端 Claude Code 各账号的登录与设置（共享的对话记录不重复计算）"))
        items.append(item("tool", "", "本工具数据", Settings.stateDir, "设置、操作日志、定价、窗口缓存"))
        let total = items.reduce(Int64(0)) { $0 + (($1["size"] as? Int64) ?? 0) }
        let cache = items.filter { !jsonTrue($0["active"]) }.reduce(Int64(0)) { $0 + (($1["cache"] as? Int64) ?? 0) }
        return ["items": items, "total": total, "cleanable": cache, "scannedAt": fmtDate(Date(), "HH:mm:ss"), "ms": Int64(Date().timeIntervalSince(t0) * 1000)]
    }

    // 清理已保存（非当前）账号存档里的缓存
    static func cleanCache(_ id: String) throws -> Int64 {
        let targets = DesktopStore.load().filter { !$0.active && (id.isEmpty || $0.id == id) }
        if targets.isEmpty { throw AppError(id.isEmpty ? "没有可清理的已保存账号" : "当前使用中的账号不能清理缓存，请先切换到其他账号") }
        var freed: Int64 = 0
        for p in targets {
            guard p.dir.hasPrefix(DesktopStore.profilesDir + "/") else { continue }
            for c in cacheDirs {
                let d = p.dir + "/" + c
                guard FS.isRealDir(d) else { continue }
                let before = FS.measure(d).0
                Store.removeLinks(d)
                do { try FS.remove(d); freed += before } catch { freed += before - FS.measure(d).0 }
            }
        }
        return freed
    }
}

// ---------------- 缓存清理（不清理登录信息、会话记录、设置） ----------------
enum CacheCleaner {
    static let desktopItems: [(String, String)] = [
        ("Cache", "网页缓存"), ("Code Cache", "脚本编译缓存"), ("GPUCache", "GPU 缓存"),
        ("DawnCache", "图形缓存"), ("DawnGraphiteCache", "图形缓存（Graphite）"), ("DawnWebGPUCache", "图形缓存（WebGPU）"),
        ("GrShaderCache", "着色器缓存"), ("GraphiteDawnCache", "着色器缓存（Graphite）"), ("ShaderCache", "着色器缓存（旧版）"),
        ("GPUPersistentCache", "GPU 持久缓存"), ("Service Worker/CacheStorage", "Service Worker 缓存"),
        ("Service Worker/ScriptCache", "Service Worker 脚本缓存"), ("Crashpad", "崩溃报告"), ("logs", "运行日志")
    ]
    static let codeItems: [(String, String)] = [
        ("cache", "Claude Code 缓存"), ("stats-cache.json", "使用统计缓存"), ("telemetry", "本地遥测缓存"),
        ("paste-cache", "粘贴缓存"), ("statsig", "实验特性与遥测缓存"), ("downloads", "Claude Code 下载缓存")
    ]
    // macOS 上桌面 App 放在 ~/Library 里的缓存（所有账号共用）
    static var systemItems: [(String, String, String, Bool)] {
        let lib = Paths.home + "/Library"
        return [
            ("sys|caches", "系统缓存目录", lib + "/Caches/" + DesktopStore.bundleId, true),
            ("sys|shipit", "自动更新下载缓存", lib + "/Caches/" + DesktopStore.bundleId + ".ShipIt", true),
            ("sys|logs", "运行日志（~/Library/Logs/Claude）", lib + "/Logs/Claude", false)
        ]
    }

    static func build() -> [[String: Any]] {
        var list: [[String: Any]] = []
        func add(_ id: String, _ group: String, _ label: String, _ path: String, _ needsClose: Bool) {
            guard (FS.isDir(path) || FS.isFile(path)) && !FS.isLink(path) else { return }
            let m = FS.measure(path)
            guard m.0 > 0 else { return }
            list.append(["id": id, "group": group, "label": label, "path": path, "size": m.0, "files": m.1, "needsClose": needsClose])
        }
        for p in DesktopStore.load() {
            for it in desktopItems {
                add("desk|" + p.id + "|" + it.0, "桌面 App · " + p.remark + (p.active ? "（当前使用）" : ""), it.1, p.dir + "/" + it.0, p.active)
            }
        }
        for it in systemItems { add(it.0, "桌面 App · 系统缓存（所有账号共用）", it.1, it.2, it.3) }
        var roots: [(String, String)] = [(Store.mainDir, "Claude Code · ~/.claude")]
        for a in Store.load() where !a.isDefault { roots.append((a.dir, "终端账号 · " + a.remark)) }
        for (i, r) in roots.enumerated() {
            for it in codeItems { add("code|\(i)|" + it.0, r.1, it.1, r.0 + "/" + it.0, false) }
        }
        return list
    }

    static func scan() -> [String: Any] {
        let items = build()
        return ["items": items, "total": items.reduce(Int64(0)) { $0 + (($1["size"] as? Int64) ?? 0) }, "scannedAt": fmtDate(Date(), "HH:mm:ss")]
    }

    static func removeContents(_ path: String) -> Int64 {
        var freed: Int64 = 0
        if FS.isFile(path) && !FS.isLink(path) {
            let len = FS.size(path)
            if (try? FS.remove(path)) != nil { freed += len }
            return freed
        }
        guard FS.isRealDir(path) else { return 0 }
        Store.removeLinks(path) // 先拆链接，绝不顺着链接删到共享数据
        for n in FS.names(path) {
            let p = path + "/" + n
            let m = FS.measure(p).0
            do { try FS.remove(p); freed += m } catch { freed += m - FS.measure(p).0 }
        }
        return freed
    }

    static func clean(_ ids: [String]) throws -> String {
        var all: [String: [String: Any]] = [:]
        for x in build() { if let id = x["id"] as? String { all[id] = x } }
        let sel = ids.compactMap { all[$0] }
        if sel.isEmpty { throw AppError("没有选中可清理的缓存") }
        let needClose = sel.contains { jsonTrue($0["needsClose"]) } && DesktopStore.isRunning()
        var freed: Int64 = 0
        if needClose { try DesktopStore.closeForMaintenance() }
        defer { if needClose { try? DesktopStore.launchDesktop() } }
        for x in sel { freed += removeContents(x["path"] as? String ?? "") }
        return "清理了 \(sel.count) 项缓存，释放 " + fmtMB(freed) + " MB" + (needClose ? "（已重新打开桌面 App）" : "")
    }
}
