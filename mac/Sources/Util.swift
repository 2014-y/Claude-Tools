// 通用工具：路径、文件、JSON、正则、子进程、时间
import Foundation
import Darwin

struct AppError: Error, LocalizedError {
    let msg: String
    init(_ m: String) { msg = m }
    var errorDescription: String? { msg }
}

func errMsg(_ e: Error) -> String {
    if let a = e as? AppError { return a.msg }
    return (e as NSError).localizedDescription
}

final class Lock {
    private let l = NSRecursiveLock()
    @discardableResult
    func run<T>(_ f: () throws -> T) rethrows -> T { l.lock(); defer { l.unlock() }; return try f() }
}

// ---------------- 环境与路径 ----------------
enum Env {
    static func get(_ k: String) -> String? {
        guard let v = ProcessInfo.processInfo.environment[k], !v.isEmpty else { return nil }
        return v
    }
    static var test: Bool { get("CLAUDE_SWITCHER_TEST") == "1" }
    static var noBrowser: Bool { get("CLAUDE_SWITCHER_NOBROWSER") == "1" }
}

enum Paths {
    // 测试时可以把「家目录」「Application Support」「本工具数据目录」都指到临时目录
    static let home: String = Env.get("CLAUDE_SWITCHER_HOME") ?? NSHomeDirectory()
    static let appSupport: String = Env.get("CLAUDE_SWITCHER_APPDATA") ?? (home + "/Library/Application Support")
    static let stateDir: String = Env.get("CLAUDE_SWITCHER_STATE") ?? (home + "/Library/Application Support/ClaudeSwitcher")
}

// ---------------- 文件 ----------------
enum FS {
    static let fm = FileManager.default

    static func exists(_ p: String) -> Bool { var st = stat(); return lstat(p, &st) == 0 }
    static func isDir(_ p: String) -> Bool { var d: ObjCBool = false; return fm.fileExists(atPath: p, isDirectory: &d) && d.boolValue }
    static func isFile(_ p: String) -> Bool { var d: ObjCBool = false; return fm.fileExists(atPath: p, isDirectory: &d) && !d.boolValue }
    static func isLink(_ p: String) -> Bool { var st = stat(); return lstat(p, &st) == 0 && (st.st_mode & S_IFMT) == S_IFLNK }
    // 真实目录（不是符号链接）
    static func isRealDir(_ p: String) -> Bool { var st = stat(); return lstat(p, &st) == 0 && (st.st_mode & S_IFMT) == S_IFDIR }

    static func names(_ dir: String) -> [String] { (try? fm.contentsOfDirectory(atPath: dir)) ?? [] }

    // 子目录（跟随链接判断是否为目录，与 Windows 的 GetDirectories 一致）
    static func subdirs(_ dir: String) -> [String] {
        names(dir).map { dir + "/" + $0 }.filter { isDir($0) }
    }
    static func files(_ dir: String, _ pattern: String = "*") -> [String] {
        names(dir).filter { fnmatch(pattern, $0, 0) == 0 }.map { dir + "/" + $0 }.filter { isFile($0) }
    }
    // 递归找文件（不进入符号链接）
    static func filesRecursive(_ root: String, _ pattern: String) -> [String] {
        var out: [String] = []
        var stack = [root]
        while let d = stack.popLast() {
            for n in names(d) {
                let p = d + "/" + n
                if isRealDir(p) { stack.append(p) }
                else if fnmatch(pattern, n, 0) == 0 && isFile(p) { out.append(p) }
            }
        }
        return out
    }

    static func mtime(_ p: String) -> Date? {
        var st = stat()
        guard stat(p, &st) == 0 else { return nil }
        return Date(timeIntervalSince1970: Double(st.st_mtimespec.tv_sec) + Double(st.st_mtimespec.tv_nsec) / 1e9)
    }
    static func size(_ p: String) -> Int64 { var st = stat(); return lstat(p, &st) == 0 ? Int64(st.st_size) : 0 }
    static func setMtime(_ p: String, _ d: Date) { try? fm.setAttributes([.modificationDate: d], ofItemAtPath: p) }

    static func read(_ p: String) -> Data? { fm.contents(atPath: p) }
    static func readText(_ p: String) -> String? {
        guard let d = read(p) else { return nil }
        var s = String(decoding: d, as: UTF8.self)
        if s.hasPrefix("\u{FEFF}") { s.removeFirst() }
        return s
    }
    static func write(_ p: String, _ s: String) throws {
        mkdirs((p as NSString).deletingLastPathComponent)
        try Data(s.utf8).write(to: URL(fileURLWithPath: p), options: .atomic)
    }
    static func append(_ p: String, _ s: String) {
        mkdirs((p as NSString).deletingLastPathComponent)
        if let h = FileHandle(forWritingAtPath: p) {
            h.seekToEndOfFile(); h.write(Data(s.utf8)); h.closeFile()
        } else {
            try? Data(s.utf8).write(to: URL(fileURLWithPath: p))
        }
    }
    static func mkdirs(_ p: String) { if !p.isEmpty { try? fm.createDirectory(atPath: p, withIntermediateDirectories: true) } }
    static func copy(_ a: String, _ b: String, overwrite: Bool = false) throws {
        if overwrite && exists(b) { try fm.removeItem(atPath: b) }
        try fm.copyItem(atPath: a, toPath: b)
    }
    static func move(_ a: String, _ b: String) throws { try fm.moveItem(atPath: a, toPath: b) }
    static func remove(_ p: String) throws { try fm.removeItem(atPath: p) }
    static func name(_ p: String) -> String { (p as NSString).lastPathComponent }
    static func parent(_ p: String) -> String { (p as NSString).deletingLastPathComponent }
    static func stem(_ p: String) -> String { ((p as NSString).lastPathComponent as NSString).deletingPathExtension }

    // 目录大小与文件数（不跟随符号链接，避免重复计算共享数据）
    static func measure(_ dir: String) -> (Int64, Int64) {
        if isFile(dir) && !isLink(dir) { return (size(dir), 1) }
        guard isRealDir(dir) else { return (0, 0) }
        var size: Int64 = 0, files: Int64 = 0
        var stack = [dir]
        while let d = stack.popLast() {
            for n in names(d) {
                let p = d + "/" + n
                var st = stat()
                guard lstat(p, &st) == 0 else { continue }
                let t = st.st_mode & S_IFMT
                if t == S_IFDIR { stack.append(p) }
                else if t == S_IFREG { size += Int64(st.st_size); files += 1 }
            }
        }
        return (size, files)
    }
}

// ---------------- JSON ----------------
func isJsonBool(_ v: Any?) -> Bool {
    guard let n = v as? NSNumber else { return false }
    return CFGetTypeID(n) == CFBooleanGetTypeID()
}
func jsonTrue(_ v: Any?) -> Bool { isJsonBool(v) && (v as! NSNumber).boolValue }
func jsonInt(_ v: Any?) -> Int64? {
    if let n = v as? NSNumber, !isJsonBool(n) { let d = n.doubleValue; return d.isFinite ? Int64(d.rounded(.toNearestOrEven)) : nil }
    if let s = v as? String { return Int64(s.trimmingCharacters(in: .whitespaces)) }
    return nil
}
func jsonDouble(_ v: Any?) -> Double? {
    if let n = v as? NSNumber, !isJsonBool(n) { return n.doubleValue }
    if let s = v as? String { return Double(s.trimmingCharacters(in: .whitespaces)) }
    return nil
}

enum Json {
    static func parse(_ s: String) -> Any? { parse(Data(s.utf8)) }
    static func parse(_ d: Data) -> Any? { try? JSONSerialization.jsonObject(with: d, options: [.fragmentsAllowed]) }
    static func parseFile(_ p: String) -> Any? {
        guard let s = FS.readText(p) else { return nil }
        return parse(s)
    }

    static func str(_ v: Any?) -> String { var o = ""; enc(v, &o); return o }

    private static func enc(_ v: Any?, _ o: inout String) {
        guard let v = v else { o += "null"; return }
        if v is NSNull { o += "null"; return }
        if let s = v as? String { quote(s, &o); return }
        if let n = v as? NSNumber {
            if CFGetTypeID(n) == CFBooleanGetTypeID() { o += n.boolValue ? "true" : "false"; return }
            let t = String(cString: n.objCType)
            if t == "d" || t == "f" {
                let d = n.doubleValue
                if !d.isFinite { o += "0"; return }
                if d == d.rounded() && abs(d) < 1e15 { o += String(Int64(d)); return }
                o += "\(d)"; return
            }
            o += n.stringValue; return
        }
        if let d = v as? [String: Any] {
            o += "{"; var first = true
            for (k, x) in d { if !first { o += "," }; first = false; quote(k, &o); o += ":"; enc(unwrap(x), &o) }
            o += "}"; return
        }
        if let a = v as? [Any] {
            o += "["; var first = true
            for x in a { if !first { o += "," }; first = false; enc(unwrap(x), &o) }
            o += "]"; return
        }
        if let d = v as? Date { o += String(Int64(d.timeIntervalSince1970 * 1000)); return }
        quote("\(v)", &o)
    }
    // Any 里可能包着 Optional
    private static func unwrap(_ x: Any) -> Any? {
        let m = Mirror(reflecting: x)
        if m.displayStyle == .optional { return m.children.first?.value }
        return x
    }
    static func quote(_ s: String, _ o: inout String) {
        o += "\""
        for u in s.unicodeScalars {
            switch u {
            case "\"": o += "\\\""
            case "\\": o += "\\\\"
            case "\n": o += "\\n"
            case "\r": o += "\\r"
            case "\t": o += "\\t"
            case "\u{2028}": o += "\\u2028"
            case "\u{2029}": o += "\\u2029"
            default:
                if u.value < 0x20 { o += String(format: "\\u%04x", u.value) } else { o.unicodeScalars.append(u) }
            }
        }
        o += "\""
    }
    // 把 JSON 字符串里的转义内容（不含两边引号）还原
    static func unescape(_ s: String) -> String {
        if !s.contains("\\") { return s }
        return (parse("\"" + s + "\"") as? String) ?? s
    }
}

// ---------------- 正则 ----------------
final class Rx {
    let re: NSRegularExpression
    init(_ p: String, _ o: NSRegularExpression.Options = []) { re = try! NSRegularExpression(pattern: p, options: o) }
    private func groups(_ m: NSTextCheckingResult, _ s: NSString) -> [String] {
        (0..<m.numberOfRanges).map { i in let r = m.range(at: i); return r.location == NSNotFound ? "" : s.substring(with: r) }
    }
    func first(_ s: String) -> [String]? {
        let ns = s as NSString
        guard let m = re.firstMatch(in: s, range: NSRange(location: 0, length: ns.length)) else { return nil }
        return groups(m, ns)
    }
    func all(_ s: String) -> [[String]] {
        let ns = s as NSString
        return re.matches(in: s, range: NSRange(location: 0, length: ns.length)).map { groups($0, ns) }
    }
    func test(_ s: String) -> Bool { first(s) != nil }
    func replace(_ s: String, _ t: String) -> String {
        re.stringByReplacingMatches(in: s, range: NSRange(location: 0, length: (s as NSString).length), withTemplate: t)
    }
}

// ---------------- 时间 ----------------
func nowMs() -> Int64 { Int64(Date().timeIntervalSince1970 * 1000) }
func fmtDate(_ d: Date, _ f: String) -> String {
    let df = DateFormatter()
    df.locale = Locale(identifier: "en_US_POSIX")
    df.timeZone = TimeZone.current
    df.dateFormat = f
    return df.string(from: d)
}
func roundEven(_ x: Double, _ digits: Int = 0) -> Double {
    let p = pow(10.0, Double(digits))
    return (x * p).rounded(.toNearestOrEven) / p
}

// ISO 8601 时间（如 2025-11-09T10:20:30.123Z、2025-11-09T10:20:30.123456+00:00）→ 秒（UTC），失败返回 nil
func parseIso(_ s: String) -> Double? {
    let b = Array(s.utf8)
    return parseIsoBytes(b, 0, b.count)
}
func parseIsoBytes(_ b: [UInt8], _ start: Int, _ end: Int) -> Double? {
    func num(_ i: Int, _ n: Int) -> Int? {
        guard i + n <= end else { return nil }
        var v = 0
        for k in i..<(i + n) { let c = b[k]; guard c >= 48 && c <= 57 else { return nil }; v = v * 10 + Int(c - 48) }
        return v
    }
    let i = start
    guard let y = num(i, 4), i + 4 < end, b[i + 4] == 45, let mo = num(i + 5, 2), i + 7 < end, b[i + 7] == 45, let d = num(i + 8, 2) else { return nil }
    var h = 0, mi = 0, sec = 0, frac = 0.0
    var p = i + 10
    if p < end && (b[p] == 84 || b[p] == 32) {
        guard let hh = num(p + 1, 2), p + 3 < end, b[p + 3] == 58, let mm = num(p + 4, 2) else { return nil }
        h = hh; mi = mm; p += 6
        if p < end && b[p] == 58 { guard let ss = num(p + 1, 2) else { return nil }; sec = ss; p += 3 }
        if p < end && (b[p] == 46 || b[p] == 44) {
            p += 1; var scale = 0.1
            while p < end && b[p] >= 48 && b[p] <= 57 { frac += Double(b[p] - 48) * scale; scale /= 10; p += 1 }
        }
    }
    var offset = 0
    var hasZone = false
    if p < end {
        if b[p] == 90 || b[p] == 122 { hasZone = true }
        else if b[p] == 43 || b[p] == 45 {
            let sign = b[p] == 45 ? -1 : 1
            guard let oh = num(p + 1, 2) else { return nil }
            var om = 0
            if p + 3 < end && b[p + 3] == 58 { om = num(p + 4, 2) ?? 0 } else { om = num(p + 3, 2) ?? 0 }
            offset = sign * (oh * 3600 + om * 60); hasZone = true
        }
    }
    // days from civil（公历日期 → 1970 起的天数）
    let yy = mo <= 2 ? y - 1 : y
    let era = (yy >= 0 ? yy : yy - 399) / 400
    let yoe = yy - era * 400
    let mp = (mo + 9) % 12
    let doy = (153 * mp + 2) / 5 + d - 1
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy
    let days = era * 146097 + doe - 719468
    var t = Double(days * 86400 + h * 3600 + mi * 60 + sec) + frac - Double(offset)
    if !hasZone { t -= Double(TimeZone.current.secondsFromGMT(for: Date(timeIntervalSince1970: t))) } // 没有时区按本地时间
    return t
}

// ---------------- 子进程 ----------------
struct RunResult { var status: Int32; var out: String; var err: String; var timedOut: Bool }

func runProcess(_ exe: String, _ args: [String], env: [String: String]? = nil, cwd: String? = nil, timeout: Double = 30) -> RunResult {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: exe)
    p.arguments = args
    if let env = env { p.environment = env }
    if let cwd = cwd { p.currentDirectoryURL = URL(fileURLWithPath: cwd) }
    let o = Pipe(), e = Pipe()
    p.standardOutput = o; p.standardError = e
    p.standardInput = FileHandle.nullDevice
    var outData = Data(), errData = Data()
    let g = DispatchGroup()
    do { try p.run() } catch { return RunResult(status: -1, out: "", err: errMsg(error), timedOut: false) }
    g.enter(); DispatchQueue.global().async { outData = o.fileHandleForReading.readDataToEndOfFile(); g.leave() }
    g.enter(); DispatchQueue.global().async { errData = e.fileHandleForReading.readDataToEndOfFile(); g.leave() }
    let done = DispatchSemaphore(value: 0)
    DispatchQueue.global().async { p.waitUntilExit(); done.signal() }
    var timedOut = false
    if done.wait(timeout: .now() + timeout) == .timedOut {
        timedOut = true
        p.terminate()
        kill(p.processIdentifier, SIGKILL)
        _ = done.wait(timeout: .now() + 3)
    }
    _ = g.wait(timeout: .now() + 3)
    return RunResult(status: timedOut ? -1 : p.terminationStatus, out: String(decoding: outData, as: UTF8.self), err: String(decoding: errData, as: UTF8.self), timedOut: timedOut)
}

// 单引号转义，用于生成 shell 脚本
func shq(_ s: String) -> String { "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'" }

func fmtMB(_ b: Int64) -> String { String(format: "%.1f", Double(b) / 1048576.0) }
