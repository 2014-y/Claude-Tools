// 用量统计：逐条读取 ~/.claude/projects 下的对话记录，按消息去重，支持时间范围 / 模型 / 账号筛选、费用估算和请求日志
import Foundation

struct Price: Codable {
    var Key: String
    var Input: Double, Output: Double, CacheRead: Double, CacheWrite5m: Double, CacheWrite1h: Double
    var dict: [String: Any] { ["Key": Key, "Input": Input, "Output": Output, "CacheRead": CacheRead, "CacheWrite5m": CacheWrite5m, "CacheWrite1h": CacheWrite1h] }
}

enum Pricing {
    static let lock = Lock()
    static var filePath: String { Settings.stateDir + "/pricing.json" }
    static var cache: [Price]? = nil

    // 默认值：Anthropic 官方 API 价格；缓存写入 5 分钟 = 1.25×输入，1 小时 = 2×输入
    static func p(_ k: String, _ i: Double, _ o: Double, _ cr: Double) -> Price { Price(Key: k, Input: i, Output: o, CacheRead: cr, CacheWrite5m: i * 1.25, CacheWrite1h: i * 2) }
    static func defaults() -> [Price] {
        [p("claude-fable-5-1", 10, 50, 0.25), p("claude-mythos-5-1", 10, 50, 0.25), p("claude-fable-5", 10, 50, 1.0),
         p("claude-opus-5-5", 4, 20, 0.20), p("claude-opus-5", 5, 25, 0.50),
         p("claude-opus-4-8", 5, 25, 0.50), p("claude-opus-4-7", 5, 25, 0.50), p("claude-opus-4-6", 5, 25, 0.50), p("claude-opus-4-5", 5, 25, 0.50),
         p("claude-sonnet-5-5", 2, 10, 0.20), p("claude-sonnet-5", 2, 10, 0.20),
         p("claude-sonnet-4-6", 3, 15, 0.30), p("claude-sonnet-4-5", 3, 15, 0.30),
         p("claude-haiku-5-5", 0.10, 0.50, 0.01), p("claude-haiku-4-5", 1, 5, 0.10)]
    }

    static func all() -> [Price] {
        lock.run {
            if let c = cache { return c }
            if let d = FS.read(filePath), let l = try? JSONDecoder().decode([Price].self, from: d) { cache = l; return l }
            let l = defaults(); cache = l; return l
        }
    }
    static func allJson() -> [[String: Any]] { all().map { $0.dict } }

    static func save(_ list: [Price]) throws {
        let d = try JSONEncoder().encode(list)
        try FS.write(filePath, String(decoding: d, as: UTF8.self))
        lock.run { cache = list }
    }
    static func reset() { lock.run { try? FS.remove(filePath); cache = nil } }

    // 最长前缀匹配；没有定价的模型返回 nil（不计费）
    static func find(_ model: String) -> Price? {
        if model.isEmpty { return nil }
        let m = model.lowercased()
        return all().filter { !$0.Key.isEmpty && m.hasPrefix($0.Key.lowercased()) }.max { $0.Key.count < $1.Key.count }
    }
    static func cost(_ r: Rec) -> Double {
        guard let p = find(r.model) else { return 0 }
        return (Double(r.inp) * p.Input + Double(r.out) * p.Output + Double(r.cacheRead) * p.CacheRead + Double(r.cw5) * p.CacheWrite5m + Double(r.cw1) * p.CacheWrite1h) / 1e6
    }
}

// 一次模型回复
final class Rec {
    var ts: Double = 0          // UTC 秒
    var hasTs = false
    var day = 0, hour = 0       // 本地日期序号 / 小时
    var model = "", session = "", cwd = ""
    var inp: Int64 = 0, out: Int64 = 0, cacheRead: Int64 = 0, cw5: Int64 = 0, cw1: Int64 = 0
    var secs: Double = 0
    var cacheWrite: Int64 { cw5 + cw1 }
    var total: Int64 { inp + out + cacheRead + cw5 + cw1 }
}

func localDay(_ t: Double) -> (Int, Int) {
    let off = Double(TimeZone.current.secondsFromGMT(for: Date(timeIntervalSince1970: t)))
    let x = t + off
    let day = Int(floor(x / 86400))
    let sec = x - Double(day) * 86400
    return (day, min(23, max(0, Int(sec / 3600))))
}

enum TokenStats {
    static let lock = Lock()
    static var fileCache: [String: (Int64, Date, [Rec])] = [:]
    static var all: [Rec] = []
    static var accountMap: [String: String] = [:]
    static var scanning = false, ready = false
    static var lastScan = Date.distantPast
    static var progressDone = 0, progressTotal = 0

    static let P_CWD = Array("\"cwd\":\"".utf8)
    static let P_TS = Array("\"timestamp\":\"".utf8)
    static let P_ASSIST = Array("\"type\":\"assistant\"".utf8)
    static let P_USAGE = Array("\"usage\":{".utf8)
    static let P_MODEL = Array("\"model\":\"".utf8)
    static let P_ID = Array("\"id\":\"msg_".utf8)
    static let P_IN = Array("\"input_tokens\":".utf8)
    static let P_OUT = Array("\"output_tokens\":".utf8)
    static let P_CR = Array("\"cache_read_input_tokens\":".utf8)
    static let P_CW = Array("\"cache_creation_input_tokens\":".utf8)
    static let P_CW5 = Array("\"ephemeral_5m_input_tokens\":".utf8)
    static let P_CW1 = Array("\"ephemeral_1h_input_tokens\":".utf8)

    static func find(_ p: UnsafePointer<UInt8>, _ n: Int, _ from: Int, _ pat: [UInt8]) -> Int {
        let m = pat.count
        var i = from
        while i <= n - m {
            guard let q = memchr(p + i, Int32(pat[0]), n - m - i + 1) else { return -1 }
            let pos = UnsafePointer<UInt8>(q.assumingMemoryBound(to: UInt8.self)) - p
            if memcmp(p + pos, pat, m) == 0 { return pos }
            i = pos + 1
        }
        return -1
    }
    // 从 s 开始读 JSON 字符串内容，直到未转义的引号；返回结束位置（引号处），找不到返回 -1
    static func strEnd(_ p: UnsafePointer<UInt8>, _ n: Int, _ s: Int) -> Int {
        var i = s
        while i < n {
            let c = p[i]
            if c == 92 { i += 2; continue }
            if c == 34 { return i }
            i += 1
        }
        return -1
    }
    static func str(_ p: UnsafePointer<UInt8>, _ s: Int, _ e: Int) -> String {
        String(decoding: UnsafeBufferPointer(start: p + s, count: e - s), as: UTF8.self)
    }
    static func num(_ p: UnsafePointer<UInt8>, _ n: Int, _ from: Int, _ pat: [UInt8]) -> Int64 {
        let i = find(p, n, from, pat)
        if i < 0 { return 0 }
        var k = i + pat.count, v: Int64 = 0, any = false
        while k < n && p[k] >= 48 && p[k] <= 57 { v = v &* 10 &+ Int64(p[k] - 48); k += 1; any = true }
        return any ? v : 0
    }

    static func parseFile(_ path: String, _ sessionId: String) -> [Rec] {
        let size = FS.size(path)
        let mt = FS.mtime(path) ?? Date.distantPast
        if let old = lock.run({ fileCache[path] }), old.0 == size, old.1 == mt { return old.2 }
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: path), options: .alwaysMapped) else { return [] }
        var msgs: [String: Rec] = [:]
        var order: [String] = []
        var cwd: String? = nil
        var prevTs: Double? = nil
        var anon = 0
        data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            guard let base = raw.bindMemory(to: UInt8.self).baseAddress else { return }
            let total = raw.count
            var start = 0
            while start < total {
                var end = total
                if let q = memchr(base + start, 10, total - start) { end = UnsafePointer<UInt8>(q.assumingMemoryBound(to: UInt8.self)) - base }
                let p = base + start, n = end - start
                defer { start = end + 1 }
                if n < 10 { continue }
                if cwd == nil {
                    let i = find(p, n, 0, P_CWD)
                    if i >= 0 { let s = i + P_CWD.count, e = strEnd(p, n, s); if e >= s { cwd = Json.unescape(str(p, s, e)) } }
                }
                var ts: Double? = nil
                let ti = find(p, n, 0, P_TS)
                if ti >= 0 {
                    let s = ti + P_TS.count
                    var e = s
                    while e < n && p[e] != 34 { e += 1 }
                    if e > s && e < n { ts = parseIsoBytes(Array(UnsafeBufferPointer(start: p + s, count: e - s)), 0, e - s) }
                }
                let isAssistant = find(p, n, 0, P_ASSIST) >= 0
                let ui = isAssistant ? find(p, n, 0, P_USAGE) : -1
                if ui >= 0 {
                    var model = "unknown"
                    var from = 0
                    while true {
                        let mi = find(p, ui, from, P_MODEL)
                        if mi < 0 { break }
                        let s = mi + P_MODEL.count
                        var e = s
                        while e < ui && p[e] != 34 { e += 1 }
                        if e > s && e < ui { model = str(p, s, e); break }
                        from = mi + 1
                    }
                    if model != "<synthetic>" {
                        var id = ""
                        let ii = find(p, n, 0, P_ID)
                        if ii >= 0 {
                            let s = ii + 6 // 跳过 "id":"
                            var e = s + 4
                            while e < n && p[e] != 34 { e += 1 }
                            if e > s + 4 && e < n { id = str(p, s, e) }
                        }
                        if id.isEmpty { id = "anon\(anon)"; anon += 1 }
                        let cw = num(p, n, ui, P_CW)
                        var cw5 = num(p, n, ui, P_CW5)
                        let cw1 = num(p, n, ui, P_CW1)
                        if cw5 + cw1 == 0 { cw5 = cw } // 旧记录没有细分时按 5 分钟缓存算
                        var rec = msgs[id]
                        if rec == nil {
                            let r = Rec()
                            r.session = sessionId; r.model = model
                            if let t = ts { r.ts = t; r.hasTs = true }
                            if let t = ts, let pt = prevTs { r.secs = t - pt }
                            msgs[id] = r; order.append(id); rec = r
                        }
                        let r = rec!
                        // 同一条消息会被拆成多行，取最后一次（最终用量）
                        r.inp = num(p, n, ui, P_IN)
                        r.out = max(r.out, num(p, n, ui, P_OUT))
                        r.cacheRead = num(p, n, ui, P_CR)
                        r.cw5 = cw5; r.cw1 = cw1
                        if let t = ts, r.hasTs, prevTs != nil { r.secs = max(r.secs, (t - r.ts) + r.secs) }
                    }
                }
                if let t = ts, ui < 0 { prevTs = t }
            }
        }
        let list = order.compactMap { msgs[$0] }.filter { $0.hasTs }
        for x in list {
            x.cwd = cwd ?? ""
            let (d, h) = localDay(x.ts)
            x.day = d; x.hour = h
        }
        lock.run { fileCache[path] = (size, mt, list) }
        return list
    }

    static func transcriptFiles() -> [(String, String)] {
        let root = Store.mainDir + "/projects"
        var list: [(String, String)] = []
        for proj in FS.subdirs(root) {
            for f in FS.files(proj, "*.jsonl") { list.append((f, FS.stem(f))) }
            for sd in FS.subdirs(proj) {
                let sub = sd + "/subagents"
                if FS.isDir(sub) { for f in FS.files(sub, "*.jsonl") { list.append((f, FS.name(sd))) } }
            }
        }
        return list
    }

    static func ensureFresh(_ force: Bool, _ maxAge: Double) {
        lock.run {
            if scanning { return }
            if !force && ready && Date().timeIntervalSince(lastScan) < maxAge { return }
            scanning = true
            DispatchQueue.global(qos: .utility).async { TokenStats.scan() }
        }
    }

    static func scan() {
        let files = transcriptFiles()
        lock.run { progressTotal = files.count; progressDone = 0 }
        var recs: [Rec] = []
        for f in files {
            recs += parseFile(f.0, f.1)
            lock.run { progressDone += 1 }
        }
        var acc: [String: String] = [:]
        for s in Sessions.all() { if let c = s.cliId { acc[c] = s.account } }
        recs.sort { $0.ts < $1.ts }
        lock.run { all = recs; accountMap = acc; ready = true; lastScan = Date(); scanning = false }
    }

    static func accountOf(_ map: [String: String], _ r: Rec) -> String { map[r.session] ?? "终端 Claude Code / 其他" }
    static func projectOf(_ r: Rec) -> String {
        if r.cwd.isEmpty { return "(未知)" }
        if r.cwd.contains("/scratch-workspaces/") || r.cwd.contains("\\scratch-workspaces\\") { return "(临时会话)" }
        return r.cwd
    }

    static func sum<S: Sequence>(_ rs: S, _ name: String) -> [String: Any] where S.Element == Rec {
        var n: Int64 = 0, i: Int64 = 0, o: Int64 = 0, cr: Int64 = 0, cw: Int64 = 0
        var cost = 0.0
        for r in rs { n += 1; i += r.inp; o += r.out; cr += r.cacheRead; cw += r.cacheWrite; cost += Pricing.cost(r) }
        let prompt = i + cr + cw
        return ["name": name, "requests": n, "input": i, "output": o, "cacheRead": cr, "cacheWrite": cw, "total": i + o + cr + cw,
                "cost": roundEven(cost, 4), "hitRate": prompt == 0 ? 0.0 : roundEven(Double(cr) * 100 / Double(prompt), 1)]
    }

    static func query(_ a: [String: Any], _ S: ([String: Any], String) -> String) -> [String: Any] {
        let force = S(a, "force") == "True" || S(a, "force") == "true"
        let auto = Int(S(a, "auto")) ?? 0
        ensureFresh(force, auto > 0 ? Double(max(3, auto - 1)) : 90)
        let (src, map, isReady, isScanning, done, totalFiles, scannedAt) = lock.run { (all, accountMap, ready, scanning, progressDone, progressTotal, lastScan) }
        var res: [String: Any] = ["scanning": isScanning, "ready": isReady, "progress": totalFiles == 0 ? 0 : done * 100 / totalFiles,
                                  "scannedAt": isReady ? fmtDate(scannedAt, "HH:mm:ss") : ""]
        if !isReady { return res }

        var range = S(a, "range"); if range.isEmpty { range = "7d" }
        let now = Date()
        let (today, nowHour) = localDay(now.timeIntervalSince1970)
        var from: Int
        switch range {
        case "today": from = today
        case "30d": from = today - 29
        case "all": from = Int.min
        default: from = today - 6; range = "7d"
        }
        let model = S(a, "model"), account = S(a, "account")
        let inRange = src.filter { $0.day >= from }
        let sel = inRange.filter { (model.isEmpty || $0.model == model) && (account.isEmpty || accountOf(map, $0) == account) }

        res["range"] = range
        res["summary"] = sum(sel, "summary")
        res["models"] = Array(Set(inRange.map { $0.model })).sorted()
        res["accounts"] = Array(Set(inRange.map { accountOf(map, $0) })).sorted()

        var trend: [Any] = []
        if range == "today" {
            for h in 0...nowHour { trend.append(sum(sel.filter { $0.hour == h }, String(format: "%02d:00", h))) }
        } else {
            var first = range == "all" ? (sel.map { $0.day }.min() ?? today) : from
            if today - first > 120 { first = today - 119 }
            var byDay: [Int: [Rec]] = [:]
            for r in sel { byDay[r.day, default: []].append(r) }
            let cal = Calendar.current
            let startOfToday = cal.startOfDay(for: now)
            if first <= today {
                for d in first...today {
                    let date = cal.date(byAdding: .day, value: d - today, to: startOfToday) ?? now
                    trend.append(sum(byDay[d] ?? [], fmtDate(date, "MM/dd")))
                }
            }
        }
        res["trend"] = trend

        func groups(_ key: (Rec) -> String) -> [[String: Any]] {
            var g: [String: [Rec]] = [:]
            for r in sel { g[key(r), default: []].append(r) }
            return g.map { sum($0.value, $0.key) }
        }
        let tot: ([String: Any]) -> Int64 = { ($0["total"] as? Int64) ?? 0 }
        let cst: ([String: Any]) -> Double = { ($0["cost"] as? Double) ?? 0 }
        res["byModel"] = groups { $0.model }.sorted { cst($0) != cst($1) ? cst($0) > cst($1) : tot($0) > tot($1) }
        res["byAccount"] = groups { accountOf(map, $0) }.sorted { tot($0) > tot($1) }
        res["byProject"] = Array(groups { projectOf($0) }.sorted { tot($0) > tot($1) }.prefix(30))

        // 请求日志：所选范围内最多 5000 条
        let df = DateFormatter()
        df.locale = Locale(identifier: "en_US_POSIX"); df.timeZone = TimeZone.current
        df.dateFormat = range == "today" ? "HH:mm:ss" : "MM/dd HH:mm:ss"
        res["logs"] = sel.sorted { $0.ts > $1.ts }.prefix(5000).map { r -> [String: Any] in
            let speed = r.secs >= 0.8 && r.secs <= 900 && r.out > 20 ? Double(r.out) / r.secs : 0
            return ["t": df.string(from: Date(timeIntervalSince1970: r.ts)), "model": r.model, "account": accountOf(map, r), "project": projectOf(r),
                    "input": r.inp, "output": r.out, "cacheRead": r.cacheRead, "cacheWrite": r.cacheWrite,
                    "cost": roundEven(Pricing.cost(r), 4), "speed": roundEven(speed), "secs": roundEven(r.secs, 1), "priced": Pricing.find(r.model) != nil]
        }

        // 今日 / 本月（仪表盘用，不受筛选影响）
        let todayRecs = src.filter { $0.day >= today }
        res["today"] = sum(todayRecs, "today")
        var hourly: [Int64] = []
        for h in 0..<24 { hourly.append(h > nowHour ? -1 : todayRecs.filter { $0.hour == h }.reduce(Int64(0)) { $0 + $1.total }) }
        res["todayHourly"] = hourly
        let comps = Calendar.current.dateComponents([.year, .month], from: now)
        let monthStart = Calendar.current.date(from: comps) ?? now
        let monthDay = localDay(monthStart.timeIntervalSince1970 + 43200).0
        res["month"] = sum(src.filter { $0.day >= monthDay }, "month")
        res["sessions"] = Set(sel.map { $0.session }).count
        return res
    }
}
