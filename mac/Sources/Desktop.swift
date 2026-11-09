// Claude 桌面 App 多账号（macOS）：每个账号保存一整套 ~/Library/Application Support/Claude 数据目录。
// 切换 = 退出桌面 App -> 当前目录改名存档 -> 目标存档改名为 Claude -> 重新打开（open -a Claude）。任何一步失败都回滚。
import Foundation
import AppKit
import Darwin

final class DesktopProfile {
    var id = "", remark = "", dir = ""
    var active = false
    var lastUsed = Date(timeIntervalSince1970: 0)
    var email = ""
    var sessions = 0
    var loggedIn = false
}

enum DesktopStore {
    static let marker = ".cs-profile"
    static let bundleId = "com.anthropic.claudefordesktop"
    static var appSupport: String { Paths.appSupport }
    static var activeDir: String { appSupport + "/Claude" }
    static var profilesDir: String { appSupport + "/Claude-profiles" }
    static var testMode: Bool { Env.test }
    static let switchLock = Lock()
    static var lastSync = ""
    static var lastCounts = [0, 0, 0]

    // ---------- 标记文件：第一行 id，第二行备注 ----------
    static func readMarker(_ dir: String) -> (String, String)? {
        guard let s = FS.readText(dir + "/" + marker) else { return nil }
        let lines = s.components(separatedBy: "\n").map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
        guard let first = lines.first, !first.isEmpty else { return nil }
        return (first, lines.count > 1 && !lines[1].isEmpty ? lines[1] : first)
    }
    static func writeMarker(_ dir: String, _ id: String, _ remark: String) throws {
        try FS.write(dir + "/" + marker, id + "\n" + remark + "\n")
    }

    static func newId(_ exclude: String?) -> String {
        var n = 1
        while true {
            let id = "p\(n)"
            if id != exclude && !FS.exists(profilesDir + "/" + id) { return id }
            n += 1
        }
    }

    @discardableResult
    static func ensureActiveMarker() -> (String, String)? {
        guard FS.isDir(activeDir) else { return nil }
        if let m = readMarker(activeDir) { return m }
        let id = newId(nil)
        let email = detectEmail(activeDir)
        try? writeMarker(activeDir, id, email.isEmpty ? "账号 1" : email)
        return readMarker(activeDir)
    }

    static func load() -> [DesktopProfile] {
        var list: [DesktopProfile] = []
        let am = ensureActiveMarker()
        if let am = am { list.append(make(activeDir, am, true)) }
        for d in FS.subdirs(profilesDir) {
            guard let m = readMarker(d) else { continue }
            if let am = am, m.0 == am.0 { continue }
            list.append(make(d, m, false))
        }
        return list.sorted { a, b in a.active != b.active ? a.active : a.lastUsed > b.lastUsed }
    }

    static func make(_ dir: String, _ m: (String, String), _ active: Bool) -> DesktopProfile {
        let p = DesktopProfile()
        p.id = m.0; p.remark = m.1; p.dir = dir; p.active = active
        p.lastUsed = FS.mtime(dir + "/" + marker) ?? Date(timeIntervalSince1970: 0)
        p.email = detectEmail(dir)
        if let s = FS.readText(dir + "/config.json") { p.loggedIn = s.contains("\"oauth:tokenCache") }
        let s = dir + "/claude-code-sessions"
        if FS.isDir(s) { p.sessions = FS.filesRecursive(s, "*").filter { !FS.name($0).hasPrefix("deleted_") }.count }
        return p
    }

    // ---------- 从 claude.ai 的 IndexedDB / Local Storage 里识别登录邮箱 ----------
    static let emailLock = Lock()
    static var emailCache: [String: (Date, String)] = [:]
    static let emailRe = Rx("[A-Za-z0-9._%+\\-]{1,64}@[A-Za-z0-9.\\-]{1,100}\\.[A-Za-z]{2,12}")

    static func isLocal(_ c: UInt8) -> Bool { (c >= 48 && c <= 57) || (c >= 65 && c <= 90) || (c >= 97 && c <= 122) || c == 46 || c == 95 || c == 37 || c == 43 || c == 45 }
    static func isDomain(_ c: UInt8) -> Bool { (c >= 48 && c <= 57) || (c >= 65 && c <= 90) || (c >= 97 && c <= 122) || c == 46 || c == 45 }

    // 在字节里找 '@'，取前后合法字符组成候选串，再用正则确认
    static func scanEmails(_ b: [UInt8], _ counts: inout [String: Int]) {
        let n = b.count
        var i = 0
        while i < n {
            if b[i] == 64 {
                var l = i
                while l > 0 && isLocal(b[l - 1]) && i - l < 200 { l -= 1 }
                var r = i + 1
                while r < n && isDomain(b[r]) && r - i < 200 { r += 1 }
                if l < i && r > i + 1 {
                    let cand = String(decoding: b[l..<r], as: UTF8.self)
                    for m in emailRe.all(cand) {
                        let e = m[0].lowercased()
                        if e.hasSuffix("@anthropic.com") || e.contains("example.") || e.contains("sentry") { continue }
                        counts[e, default: 0] += 1
                    }
                }
                i = r
            } else { i += 1 }
        }
    }

    static func detectEmail(_ dir: String) -> String {
        let roots = [dir + "/IndexedDB/https_claude.ai_0.indexeddb.blob", dir + "/IndexedDB/https_claude.ai_0.indexeddb.leveldb", dir + "/Local Storage/leveldb"]
        var files: [String] = []
        for r in roots where FS.isRealDir(r) {
            files += FS.filesRecursive(r, "*").filter { FS.size($0) < 8 * 1024 * 1024 }
        }
        if files.isEmpty { return "" }
        let stamp = files.compactMap { FS.mtime($0) }.max() ?? Date(timeIntervalSince1970: 0)
        if let c = emailLock.run({ emailCache[dir] }), c.0 == stamp { return c.1 }
        var counts: [String: Int] = [:]
        for f in files {
            guard let d = FS.read(f) else { continue }
            let b = [UInt8](d)
            scanEmails(b, &counts)
            // UTF-16LE 编码的文本：取出 ASCII 字节（偶数 / 奇数对齐各一次）
            for off in 0..<2 {
                var t: [UInt8] = []
                t.reserveCapacity(b.count / 2)
                var k = off
                while k + 1 < b.count {
                    let c = b[k], z = b[k + 1]
                    t.append(z == 0 && c >= 32 && c < 127 ? c : 0)
                    k += 2
                }
                scanEmails(t, &counts)
            }
        }
        let best = counts.sorted { $0.value != $1.value ? $0.value > $1.value : $0.key < $1.key }.first?.key ?? ""
        emailLock.run { emailCache[dir] = (stamp, best) }
        return best
    }

    // ---------- 进程控制 ----------
    static func allProcesses() -> [(pid_t, String)] {
        let n = proc_listallpids(nil, 0)
        guard n > 0 else { return [] }
        var pids = [pid_t](repeating: 0, count: Int(n) + 128)
        let got = pids.withUnsafeMutableBufferPointer { buf -> Int32 in
            proc_listallpids(buf.baseAddress, Int32(buf.count * MemoryLayout<pid_t>.size))
        }
        var out: [(pid_t, String)] = []
        var buf = [CChar](repeating: 0, count: 4096)
        for pid in pids.prefix(Int(max(0, got))) where pid > 0 {
            let r = proc_pidpath(pid, &buf, UInt32(buf.count))
            if r > 0 { out.append((pid, String(cString: buf))) }
        }
        return out
    }

    // 桌面 App 本体及其辅助进程，以及从数据目录里运行的程序（内置的 Claude Code 等）
    static func desktopPids(mainOnly: Bool) -> [pid_t] {
        let me = getpid()
        let ad = activeDir + "/"
        return allProcesses().filter { item in
            let pid = item.0, path = item.1
            if pid == me { return false }
            if path.hasSuffix("/Claude.app/Contents/MacOS/Claude") { return true }
            if mainOnly { return false }
            return path.contains("/Claude.app/Contents/") || path.hasPrefix(ad)
        }.map { $0.0 }
    }

    static func isRunning() -> Bool {
        if testMode { return false }
        return !desktopPids(mainOnly: true).isEmpty
    }

    static func closeForMaintenance() throws { try closeDesktop() }

    static func closeDesktop() throws {
        if testMode { return }
        let apps = NSRunningApplication.runningApplications(withBundleIdentifier: bundleId)
        for a in apps { a.terminate() }
        for pid in desktopPids(mainOnly: true) where !apps.contains(where: { $0.processIdentifier == pid }) { kill(pid, SIGTERM) }
        var i = 0
        while i < 50 && !desktopPids(mainOnly: true).isEmpty { usleep(100_000); i += 1 }
        for a in NSRunningApplication.runningApplications(withBundleIdentifier: bundleId) { a.forceTerminate() }
        for pid in desktopPids(mainOnly: false) { kill(pid, SIGKILL) }
        i = 0
        while i < 100 && !desktopPids(mainOnly: false).isEmpty { usleep(100_000); i += 1 }
        if !desktopPids(mainOnly: true).isEmpty { throw AppError("无法关闭 Claude 桌面 App，请手动退出（菜单栏 Claude → 退出 Claude）后再试。") }
        usleep(800_000) // 等文件写完
    }

    static func appPath() -> String? {
        if let u = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleId) { return u.path }
        for p in ["/Applications/Claude.app", Paths.home + "/Applications/Claude.app"] where FS.isDir(p) { return p }
        return nil
    }

    static func launchDesktop() throws {
        if testMode { return }
        guard let app = appPath() else { throw AppError("没有找到 Claude 桌面 App，请先安装到「应用程序」文件夹") }
        var args = ["-a", app]
        if let m = readMarker(activeDir), let px = ProxyPool.forKey("d:" + m.0, "desktop"), let hp = try? ProxyPool.hostPort(px) {
            args += ["--args", "--proxy-server=" + hp]
        }
        let r = runProcess("/usr/bin/open", args, timeout: 20)
        if r.status != 0 { throw AppError("打开 Claude 桌面 App 失败：" + (r.err.isEmpty ? "open 退出码 \(r.status)" : r.err.trimmingCharacters(in: .whitespacesAndNewlines))) }
    }

    static func moveRetry(_ from: String, _ to: String) throws {
        var last = ""
        for _ in 0..<15 {
            do { try FS.move(from, to); return } catch { last = errMsg(error) }
            usleep(700_000)
        }
        throw AppError("Claude 数据目录被占用，无法切换。\n请关闭打开着该目录的终端 / 访达窗口后重试。\n(" + last + ")")
    }

    // 切换到指定账号（prepareTarget 返回要切换到的存档目录）
    static func switchCore(_ prepareTarget: () throws -> String) throws {
        try switchLock.run {
            FS.mkdirs(profilesDir)
            let am = ensureActiveMarker()
            var parked: String? = nil
            if let am = am {
                parked = profilesDir + "/" + am.0
                if FS.exists(parked!) { throw AppError("存档目录已存在，已停止：" + parked!) }
            }
            let target = try prepareTarget()
            let wasRunning = isRunning()
            try closeDesktop()
            var movedOut = false
            do {
                if am != nil { try moveRetry(activeDir, parked!); movedOut = true }
                try moveRetry(target, activeDir)
                if let m = readMarker(activeDir) { try? writeMarker(activeDir, m.0, m.1) } // 刷新「上次使用」
                lastSync = ""
                if let parked = parked, Settings.bool("syncSessions") {
                    do { lastSync = try syncSessions(parked, activeDir) } catch { lastSync = "会话同步失败：" + errMsg(error) }
                }
                if let parked = parked, Settings.bool("syncClientSettings") {
                    var r = ""
                    do { r = try syncClientSettings(parked, activeDir) } catch { r = "客户端设置同步失败：" + errMsg(error) }
                    if !r.isEmpty { lastSync = lastSync.isEmpty ? r : lastSync + "；" + r }
                }
            } catch {
                // 回滚：目标已移入但出错时也要保证原账号回到当前位置
                if movedOut && !FS.exists(activeDir) { try? moveRetry(parked!, activeDir) }
                if wasRunning { try? launchDesktop() }
                throw error
            }
            try launchDesktop()
        }
    }

    // ---------- 自动合并 / 手动合并 ----------
    static func autoMerge() {
        guard Settings.bool("autoMerge"), load().count >= 2 else { return }
        do {
            let msg = try mergeAll()
            if !msg.contains("补齐 0 条") || msg.contains("同步删除") { OpLog.add("自动合并会话", "所有桌面账号", true, msg, 0) }
        } catch { OpLog.add("自动合并会话", "所有桌面账号", false, errMsg(error), 0) }
    }

    static func mergeAll() throws -> String {
        try switchLock.run {
            let profiles = load()
            if profiles.count < 2 { throw AppError("至少需要两个桌面账号才能合并") }
            var copied = 0, ws = 0, removed = 0, skipped = 0
            for to in profiles {
                for from in profiles where from.id != to.id {
                    if targetSessionDir(to.dir) == nil { skipped += 1; continue }
                    _ = try syncSessions(from.dir, to.dir)
                    copied += lastCounts[0]; ws += lastCounts[1]; removed += lastCounts[2]
                }
            }
            var msg = "已合并 \(profiles.count) 个账号：补齐 \(copied) 条会话记录" + (ws > 0 ? "（含 \(ws) 个临时工作区）" : "") + (removed > 0 ? "，同步删除 \(removed) 条" : "")
            if skipped > 0 { msg += "；有账号还没登录过，已跳过" }
            return msg
        }
    }

    static let cwdField = Rx("\"(cwd|originCwd)\":\"((?:[^\"\\\\]|\\\\.)*)\"")
    static let accRe = Rx("\"lastKnownAccountUuid\"\\s*:\\s*\"([^\"]+)\"")

    // 当前登录账号的 账号ID/组织ID 会话目录：桌面 App 只显示这个目录里的会话
    static func targetSessionDir(_ profileDir: String) -> String? {
        let root = profileDir + "/claude-code-sessions"
        guard FS.isDir(root) else { return nil }
        var accId: String? = nil
        if let s = FS.readText(profileDir + "/config.json"), let m = accRe.first(s) { accId = m[1] }
        let q = QuotaReader.read(profileDir)
        func newest(_ dirs: [String]) -> String? { dirs.max { (FS.mtime($0) ?? .distantPast) < (FS.mtime($1) ?? .distantPast) } }
        if let accId = accId {
            let accDir = root + "/" + accId
            if FS.isDir(accDir) {
                let orgs = FS.subdirs(accDir)
                if !orgs.isEmpty {
                    if let q = q, !q.org.isEmpty, let hit = orgs.first(where: { FS.name($0).lowercased() == q.org.lowercased() }) { return hit }
                    return newest(orgs)
                }
            }
        }
        let orgDirs = FS.subdirs(root).flatMap { FS.subdirs($0) }
        if orgDirs.isEmpty { return nil }
        if let q = q, !q.org.isEmpty, let hit = orgDirs.first(where: { FS.name($0).lowercased() == q.org.lowercased() }) { return hit }
        return newest(orgDirs)
    }

    static func copyDir(_ src: String, _ dst: String, _ budget: inout Int) throws -> Int {
        var n = 0
        FS.mkdirs(dst)
        for name in FS.names(src) {
            let p = src + "/" + name
            if FS.isLink(p) { continue }
            if FS.isRealDir(p) { n += try copyDir(p, dst + "/" + name, &budget); continue }
            budget -= 1
            if budget < 0 { throw AppError("工作区文件太多，已跳过") }
            let to = dst + "/" + name
            if !FS.exists(to) { try FS.copy(p, to); n += 1 }
        }
        return n
    }

    static func syncSessions(_ fromDir: String, _ toDir: String) throws -> String {
        lastCounts = [0, 0, 0]
        guard let target = targetSessionDir(toDir) else { return "新账号还没有会话目录（首次登录后再切换一次即可同步）" }
        let srcRoot = fromDir + "/claude-code-sessions"
        guard FS.isDir(srcRoot) else { return "" }
        let toRoot = toDir + "/claude-code-sessions"
        var existing = Set(FS.files(target, "local_*.json").map { FS.name($0) })
        var copied = 0, ws = 0, removed = 0
        // 删除同步：两边任意一边删过的会话，都按已删除处理
        let deleted = Sessions.deletedIds([fromDir, toDir])
        for id in deleted {
            if FS.filesRecursive(toRoot, "deleted_" + id).isEmpty {
                try? FS.write(target + "/deleted_" + id, String(nowMs()))
            }
            for f in FS.filesRecursive(toRoot, "local_" + id + ".json") {
                if (try? FS.remove(f)) != nil { removed += 1; existing.remove(FS.name(f)) }
            }
        }
        let wsPrefix = activeDir + "/scratch-workspaces/"
        for f in FS.filesRecursive(srcRoot, "local_*.json") {
            let name = FS.name(f)
            if existing.contains(name) { continue }
            if deleted.contains(String(FS.stem(name).dropFirst(6))) { continue } // 已删除的不再复活
            try FS.copy(f, target + "/" + name)
            existing.insert(name); copied += 1
            // 临时会话的工作目录在数据目录里，一起带过去，保证能继续对话
            var head = FS.readText(f) ?? ""
            if let r = head.range(of: "\"remoteMcpServersConfig\"") { head = String(head[..<r.lowerBound]) }
            for m in cwdField.all(head) {
                let cwd = Json.unescape(m[2])
                guard cwd.hasPrefix(wsPrefix) else { continue }
                let rel = String(cwd.dropFirst(activeDir.count + 1))
                let src = fromDir + "/" + rel, dst = toDir + "/" + rel
                if FS.isDir(src) && !FS.exists(dst) {
                    var budget = 20000
                    if (try? copyDir(src, dst, &budget)) != nil { ws += 1 }
                }
            }
        }
        // 同一个存档里别的（旧）账号目录下的会话也补进当前账号目录
        for f in FS.filesRecursive(toRoot, "local_*.json") {
            if FS.parent(f) == target { continue }
            let nm = FS.name(f)
            if existing.contains(nm) { continue }
            if deleted.contains(String(FS.stem(nm).dropFirst(6))) { continue }
            if (try? FS.copy(f, target + "/" + nm)) != nil { existing.insert(nm); copied += 1 }
        }
        lastCounts = [copied, ws, removed]
        let msg = copied == 0 ? "会话记录已是最新" : "已同步 \(copied) 条会话记录" + (ws > 0 ? "（含 \(ws) 个临时工作区）" : "")
        return removed > 0 ? msg + "，同步删除 \(removed) 条" : msg
    }

    // ---------- 客户端设置同步：主题、界面缩放、窗口布局等，不含登录信息 ----------
    static let configKeys = ["userThemeMode", "windowControlsZoomFactor", "locale", "bootFrameLayout", "bootSidebarPlaceholder",
                             "bootPlaceholderColors", "quickWindowPosition", "terminalCliPowerShellPath"]
    static let wholeFiles = ["window-state.json", "Preferences", "ccd-session-presets.json", "claude_desktop_config.json"]

    static func syncClientSettings(_ fromDir: String, _ toDir: String) throws -> String {
        var n = 0
        let src = fromDir + "/config.json", dst = toDir + "/config.json"
        // config.json 只合并白名单里的设置项（同一文件里还有登录令牌，绝不整体复制）
        if FS.isFile(src), let a = Json.parseFile(src) as? [String: Any] {
            var b: [String: Any] = [:]
            if FS.isFile(dst) {
                guard let x = Json.parseFile(dst) as? [String: Any] else { throw AppError("新账号的 config.json 无法读取，已跳过") }
                b = x
            }
            var changed = false
            for k in configKeys { if let v = a[k] { b[k] = v; changed = true; n += 1 } }
            if changed {
                if FS.isFile(dst) { try FS.copy(dst, dst + ".bak", overwrite: true) }
                try FS.write(dst, Json.str(b))
            }
        }
        for f in wholeFiles {
            let s = fromDir + "/" + f
            guard FS.isFile(s) else { continue }
            try FS.copy(s, toDir + "/" + f, overwrite: true)
            n += 1
        }
        return n > 0 ? "已同步客户端设置（字号、主题、窗口布局等）" : ""
    }

    static func switchTo(_ id: String) throws {
        if let am = ensureActiveMarker(), am.0 == id { if !isRunning() { try launchDesktop() }; return }
        let dir = try findStored(id)
        try switchCore { dir }
    }

    static func addNew(_ remark0: String) throws {
        let remark = remark0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "新账号" : remark0.trimmingCharacters(in: .whitespacesAndNewlines)
        try switchCore {
            let am = readMarker(activeDir)
            let id = newId(am?.0)
            let dir = profilesDir + "/" + id
            FS.mkdirs(dir)
            try writeMarker(dir, id, remark)
            // 共用 MCP 配置，登录信息不复制
            let src = activeDir + "/claude_desktop_config.json"
            if FS.isFile(src) { try? FS.copy(src, dir + "/claude_desktop_config.json") }
            return dir
        }
    }

    static func findStored(_ id: String) throws -> String {
        for d in FS.subdirs(profilesDir) { if let m = readMarker(d), m.0 == id { return d } }
        throw AppError("找不到这个账号的存档")
    }

    static func findAny(_ id: String) throws -> String {
        if let am = ensureActiveMarker(), am.0 == id { return activeDir }
        return try findStored(id)
    }

    static func rename(_ id: String, _ remark: String) throws {
        let r = remark.trimmingCharacters(in: .whitespacesAndNewlines)
        if r.isEmpty { return }
        let dir = try findAny(id)
        guard let m = readMarker(dir) else { throw AppError("找不到这个账号") }
        let t = FS.mtime(dir + "/" + marker)
        try writeMarker(dir, m.0, r)
        if let t = t { FS.setMtime(dir + "/" + marker, t) }
    }

    static func delete(_ id: String) throws {
        if let am = ensureActiveMarker(), am.0 == id { throw AppError("正在使用的账号不能删除，请先切换到其他账号。") }
        let dir = try findStored(id)
        guard dir.hasPrefix(profilesDir + "/") else { throw AppError("路径异常，已停止") }
        Store.removeLinks(dir)
        if Store.hasLinks(dir) { throw AppError("存档里还有链接没拆除，已停止删除：" + dir) }
        try FS.remove(dir)
    }

    static func openDir(_ id: String) throws {
        let d = try findAny(id)
        _ = runProcess("/usr/bin/open", [d], timeout: 10)
    }
}
