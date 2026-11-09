// 终端 Claude Code 多账号（macOS）：每个账号使用独立的 CLAUDE_CONFIG_DIR（~/.claude-accounts/<文件夹>），
// 其中 projects 等目录用符号链接指向 ~/.claude，所有账号共享历史对话。在「终端」App 里启动。
import Foundation
import Security
import CommonCrypto

final class Account {
    var folder = "", dir = "", remark = "", email = ""
    var loggedIn = false
    var isDefault: Bool { folder == "default" }
}

enum Store {
    static var home: String { Paths.home }
    static var mainDir: String { home + "/.claude" }
    static var accountsDir: String { home + "/.claude-accounts" }
    static let sharedDirs = ["projects", "file-history", "todos", "plans", "skills"]
    static let copyFiles = ["settings.json", "CLAUDE.md"]
    static let emailRe = Rx("\"emailAddress\"\\s*:\\s*\"([^\"]+)\"")

    static func load() -> [Account] {
        var list: [Account] = []
        let def = Account()
        def.folder = "default"; def.dir = mainDir; def.remark = "原始账号"
        fill(def, home + "/.claude.json")
        list.append(def)
        let dirs = FS.subdirs(accountsDir).sorted { $0.lowercased() < $1.lowercased() }
        for d in dirs {
            let a = Account()
            a.folder = FS.name(d); a.dir = d
            a.remark = FS.readText(d + "/.remark")?.trimmingCharacters(in: .whitespacesAndNewlines) ?? a.folder
            if a.remark.isEmpty { a.remark = a.folder }
            fill(a, d + "/.claude.json")
            list.append(a)
        }
        return list
    }

    // macOS 上 Claude Code 把登录令牌存在钥匙串里（服务名 Claude Code-credentials[-配置目录哈希]），
    // 这里只检查条目是否存在，不读取内容（不会弹出钥匙串授权框）
    static func keychainService(_ a: Account) -> String {
        if a.isDefault { return "Claude Code-credentials" }
        let d = Data(a.dir.utf8)
        var h = [UInt8](repeating: 0, count: Int(CC_SHA256_DIGEST_LENGTH))
        d.withUnsafeBytes { _ = CC_SHA256($0.baseAddress, CC_LONG(d.count), &h) }
        return "Claude Code-credentials-" + h.prefix(4).map { String(format: "%02x", $0) }.joined()
    }
    static func keychainHas(_ service: String) -> Bool {
        if Env.test { return false }
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecReturnAttributes as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var r: CFTypeRef?
        return SecItemCopyMatching(q as CFDictionary, &r) == errSecSuccess
    }

    static func fill(_ a: Account, _ jsonPath: String) {
        a.email = ""
        var hasOauth = false
        for p in [jsonPath, a.dir + "/.claude.json"] {
            guard let s = FS.readText(p) else { continue }
            if s.contains("\"oauthAccount\"") { hasOauth = true }
            if let m = emailRe.first(s) { a.email = m[1]; break }
        }
        a.loggedIn = FS.isFile(a.dir + "/.credentials.json") || keychainHas(keychainService(a)) || (hasOauth && !a.email.isEmpty)
    }

    static func makeLink(_ link: String, _ target: String) throws {
        do { try FS.fm.createSymbolicLink(atPath: link, withDestinationPath: target) }
        catch { throw AppError("创建链接失败：" + link) }
        if !FS.isLink(link) { throw AppError("创建链接失败：" + link) }
    }

    static func connectShared(_ accDir: String) throws {
        for d in sharedDirs {
            let target = mainDir + "/" + d, link = accDir + "/" + d
            FS.mkdirs(target)
            if FS.isLink(link) { continue }
            if FS.isRealDir(link) {
                // 已有独立文件夹：能合并的移进共享目录，剩下的改名保留
                for n in FS.names(link) {
                    let e = link + "/" + n, dest = target + "/" + n
                    if FS.exists(dest) { continue }
                    try FS.move(e, dest)
                }
                if FS.names(link).isEmpty { try FS.remove(link) }
                else { try FS.move(link, link + ".bak-" + fmtDate(Date(), "yyyyMMddHHmmss")) }
            }
            try makeLink(link, target)
        }
    }

    @discardableResult
    static func create(_ remark: String) throws -> Account {
        FS.mkdirs(accountsDir)
        var n = 1
        while FS.exists(accountsDir + "/acc\(n)") { n += 1 }
        let dir = accountsDir + "/acc\(n)"
        FS.mkdirs(dir)
        try FS.write(dir + "/.remark", remark)
        for f in copyFiles {
            let src = mainDir + "/" + f
            if FS.isFile(src) { try? FS.copy(src, dir + "/" + f) }
        }
        try connectShared(dir)
        let a = Account(); a.folder = "acc\(n)"; a.dir = dir; a.remark = remark
        return a
    }

    static func setRemark(_ a: Account, _ remark: String) throws { try FS.write(a.dir + "/.remark", remark) }

    // 先拆掉所有链接（不跟随进入），确认没有残留后才删除目录，保证共享历史不受影响
    static func removeLinks(_ dir: String) {
        for n in FS.names(dir) {
            let p = dir + "/" + n
            if FS.isLink(p) { unlink(p) }
            else if FS.isRealDir(p) { removeLinks(p) }
        }
    }
    static func hasLinks(_ dir: String) -> Bool {
        for n in FS.names(dir) {
            let p = dir + "/" + n
            if FS.isLink(p) { return true }
            if FS.isRealDir(p) && hasLinks(p) { return true }
        }
        return false
    }

    static func delete(_ a: Account) throws {
        if a.isDefault { throw AppError("原始账号不能删除") }
        guard a.dir.hasPrefix(accountsDir + "/") else { throw AppError("路径异常，已停止") }
        removeLinks(a.dir)
        if hasLinks(a.dir) { throw AppError("还有链接没有拆除，已停止删除：" + a.dir) }
        try FS.remove(a.dir)
    }

    static func repairAll() throws { for a in load() where !a.isDefault { try connectShared(a.dir) } }

    // ---- 最近使用的项目目录 ----
    static var recentFile: String { accountsDir + "/recent-dirs.txt" }
    static func loadRecent() -> [String] {
        var r: [String] = []
        if let s = FS.readText(recentFile) {
            r = s.components(separatedBy: "\n").map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty && FS.isDir($0) }
        }
        if r.isEmpty, let l = FS.readText(accountsDir + "/.last-dir")?.trimmingCharacters(in: .whitespacesAndNewlines), FS.isDir(l) { r.append(l) }
        return r
    }
    static func saveRecent(_ dir: String) {
        FS.mkdirs(accountsDir)
        var r = loadRecent().filter { $0 != dir }
        r.insert(dir, at: 0)
        try? FS.write(recentFile, r.prefix(15).joined(separator: "\n") + "\n")
        try? FS.write(accountsDir + "/.last-dir", dir)
    }
}

// ---------------- Claude Code（claude 命令）检测、启动、安装、查用量 ----------------
enum Cli {
    static let lock = Lock()
    static var cliPath: String? = nil
    static var cliVersion = ""
    static var checked = Date.distantPast
    static var shellPath: String? = nil

    // 图形界面程序的 PATH 很短，用登录 shell 取到用户平时的 PATH（只取一次）
    static func loginPath() -> String {
        if let p = lock.run({ shellPath }) { return p }
        var p = ""
        if !Env.test {
            let sh = ProcessInfo.processInfo.environment["SHELL"] ?? "/bin/zsh"
            let r = runProcess(sh, ["-ilc", "printf '__P__%s__P__' \"$PATH\""], timeout: 6)
            if let m = Rx("__P__(.*?)__P__").first(r.out) { p = m[1] }
        }
        let extra = [Paths.home + "/.local/bin", "/opt/homebrew/bin", "/usr/local/bin", Paths.home + "/.npm-global/bin", Paths.home + "/.bun/bin"]
        let cur = ProcessInfo.processInfo.environment["PATH"] ?? "/usr/bin:/bin:/usr/sbin:/sbin"
        var seen = Set<String>(), parts: [String] = []
        for x in (p.split(separator: ":").map(String.init) + extra + cur.split(separator: ":").map(String.init)) where !x.isEmpty && !seen.contains(x) {
            seen.insert(x); parts.append(x)
        }
        let full = parts.joined(separator: ":")
        lock.run { shellPath = full }
        return full
    }

    static func findClaude() -> String? {
        if let t = Env.get("CLAUDE_SWITCHER_CLAUDE") { return FS.fm.isExecutableFile(atPath: t) ? t : nil }
        var cands = [Paths.home + "/.local/bin/claude", Paths.home + "/.claude/local/claude"]
        for d in loginPath().split(separator: ":") { cands.append(String(d) + "/claude") }
        return cands.first { FS.isFile($0) && FS.fm.isExecutableFile(atPath: $0) }
    }

    static func childEnv(configDir: String?, proxy: String?) -> [String: String] {
        var env = ProcessInfo.processInfo.environment
        for k in env.keys where k.hasPrefix("CLAUDE_SWITCHER_") { env.removeValue(forKey: k) }
        env["PATH"] = loginPath()
        if let c = configDir { env["CLAUDE_CONFIG_DIR"] = c } else { env.removeValue(forKey: "CLAUDE_CONFIG_DIR") }
        if let p = proxy, !p.isEmpty { env["HTTPS_PROXY"] = p; env["HTTP_PROXY"] = p }
        return env
    }

    static func status(_ force: Bool) -> [String: Any] {
        lock.run {
            if force || Date().timeIntervalSince(checked) > 30 {
                if force { shellPath = nil }
                let p = findClaude()
                if p != cliPath || force {
                    cliVersion = ""
                    if let p = p {
                        let r = runProcess(p, ["--version"], env: childEnv(configDir: nil, proxy: nil), timeout: 8)
                        cliVersion = r.out.trimmingCharacters(in: .whitespacesAndNewlines)
                    }
                }
                cliPath = p; checked = Date()
            }
            return ["installed": cliPath != nil, "path": cliPath ?? "", "version": cliVersion]
        }
    }

    static func requirePath(_ msg: String) throws -> String {
        var st = status(false)
        if !jsonTrue(st["installed"]) { st = status(true) }
        guard jsonTrue(st["installed"]), let p = st["path"] as? String, !p.isEmpty else { throw AppError(msg) }
        return p
    }

    // 让 Claude Code 自己执行 /usage（它自己处理登录令牌）
    static func runUsage(_ configDir: String?, _ proxy: String?) throws -> [String: Any] {
        let exe = try requirePath("这台电脑还没有安装 Claude Code")
        let r = runProcess(exe, ["-p", "/usage"], env: childEnv(configDir: configDir, proxy: proxy), cwd: Settings.stateDir, timeout: 75)
        if r.timedOut { throw AppError("查询超时（75 秒），请稍后重试") }
        let ses = Rx("Current session:\\s*(\\d+)%\\s*used[^\\n]*?resets\\s+([^\\n(]+?)\\s*\\(").first(r.out)
        let wk = Rx("Current week \\(all models\\):\\s*(\\d+)%\\s*used[^\\n]*?resets\\s+([^\\n(]+?)\\s*\\(").first(r.out)
        if ses == nil && wk == nil { throw AppError("没有读到用量（登录可能已失效，请重新连接）") }
        func f(_ m: [String]?) -> Any { guard let m = m else { return NSNull() }; return ["used": Int(m[1]) ?? 0, "resets": m[2].trimmingCharacters(in: .whitespaces)] as [String: Any] }
        return ["session": f(ses), "week": f(wk), "at": fmtDate(Date(), "HH:mm:ss")]
    }

    static func usage(_ id: String) throws -> [String: Any] {
        guard let acc = Store.load().first(where: { $0.folder == id }) else { throw AppError("找不到这个账号") }
        if !acc.loggedIn { throw AppError("这个终端账号还没有登录，请先点「新对话」登录一次") }
        return try runUsage(acc.isDefault ? nil : acc.dir, ProxyPool.forKey("c:" + acc.folder, "cli"))
    }

    // 生成 .command 脚本并用「终端」打开；测试模式只生成不打开，返回脚本路径
    @discardableResult
    static func openInTerminal(_ name: String, _ body: String) throws -> String {
        let dir = Settings.stateDir + "/launch"
        FS.mkdirs(dir)
        let path = dir + "/" + name + "-" + String(UUID().uuidString.prefix(8)) + ".command"
        let script = "#!/bin/bash\nrm -f \"$0\"\n" + body
        try FS.write(path, script)
        chmod(path, 0o755)
        if Env.test { return path }
        let r = runProcess("/usr/bin/open", ["-a", "Terminal", path], timeout: 15)
        if r.status != 0 { throw AppError("打开终端失败：" + r.err.trimmingCharacters(in: .whitespacesAndNewlines)) }
        return path
    }

    static var lastScript = ""

    static func launch(_ id: String, _ dir0: String, _ mode: String) throws {
        guard let acc = Store.load().first(where: { $0.folder == id }) else { throw AppError("找不到这个账号") }
        var dir = dir0.trimmingCharacters(in: .whitespacesAndNewlines)
        if dir.hasPrefix("\"") && dir.hasSuffix("\"") && dir.count >= 2 { dir = String(dir.dropFirst().dropLast()) }
        if dir.hasPrefix("~/") { dir = Paths.home + String(dir.dropFirst(1)) }
        if dir.isEmpty || !FS.isDir(dir) { throw AppError("请先选择一个存在的项目文件夹") }
        let exe = try requirePath("这台电脑还没有安装 Claude Code（claude 命令），请先点「一键安装」")
        let extra = mode == "resume" ? " --resume" : mode == "continue" ? " --continue" : ""
        var b = "printf '\\033]0;%s\\007' " + shq("Claude - " + acc.remark) + "\n"
        b += "cd " + shq(dir) + " || exit 1\n"
        b += acc.isDefault ? "unset CLAUDE_CONFIG_DIR\n" : "export CLAUDE_CONFIG_DIR=" + shq(acc.dir) + "\n"
        if let px = ProxyPool.forKey("c:" + acc.folder, "cli") { b += "export HTTPS_PROXY=" + shq(px) + " HTTP_PROXY=" + shq(px) + "\n" }
        b += "export PATH=" + shq(loginPath()) + "\n"
        b += shq(exe) + extra + "\n"
        b += "exec \"${SHELL:-/bin/zsh}\" -l\n"
        lastScript = try openInTerminal("claude-" + acc.folder, b)
        Store.saveRecent(dir)
    }

    static func install() throws {
        let b = "printf '\\033]0;%s\\007' '安装 Claude Code'\n"
            + "echo '正在安装 Claude Code（官方安装脚本 https://claude.ai/install.sh）...'\n"
            + "curl -fsSL https://claude.ai/install.sh | bash\n"
            + "echo\necho '安装结束。回到 Claude Tools 点「重新检测」即可使用。'\n"
            + "exec \"${SHELL:-/bin/zsh}\" -l\n"
        lastScript = try openInTerminal("install-claude-code", b)
    }
}
