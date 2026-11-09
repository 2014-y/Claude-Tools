// 程序入口：单实例 + 本机服务 + 菜单栏常驻 + 窗口
import AppKit
import Darwin

signal(SIGPIPE, SIG_IGN)

// 清掉从别的 Claude 会话继承来的环境变量（账号、令牌、代理地址等），否则子进程 claude 会用那个会话的账号
for (k, _) in ProcessInfo.processInfo.environment {
    let u = k.uppercased()
    if u.hasPrefix("CLAUDE_CODE_") || u.hasPrefix("ANTHROPIC_") || u.hasPrefix("CLAUDE_AGENT_SDK") || u.hasPrefix("CLAUDE_PREVIEW")
        || u == "CLAUDECODE" || u == "CLAUDE_PID" || u == "CLAUDE_CONFIG_DIR" {
        unsetenv(k)
    }
}

let launchArgs = CommandLine.arguments
let trayOnly = launchArgs.contains("--tray")
FS.mkdirs(Paths.stateDir)

// 通知已在运行的实例把窗口显示出来
func askRunningToShow() {
    guard let u = FS.readText(Server.urlFile)?.trimmingCharacters(in: .whitespacesAndNewlines),
          let q = u.range(of: "?t=") else { return }
    let base = String(u[..<q.lowerBound]), tok = String(u[q.upperBound...])
    guard let url = URL(string: base + "api/ui/show") else { return }
    var req = URLRequest(url: url, timeoutInterval: 4)
    req.httpMethod = "POST"
    req.setValue(tok, forHTTPHeaderField: "X-Token")
    req.setValue("application/json", forHTTPHeaderField: "Content-Type")
    req.httpBody = Data("{}".utf8)
    let sem = DispatchSemaphore(value: 0)
    URLSession.shared.dataTask(with: req) { _, _, _ in sem.signal() }.resume()
    _ = sem.wait(timeout: .now() + 5)
}

// 单实例：用文件锁（测试实例的数据目录不同，互不影响）
let instanceLock = open(Paths.stateDir + "/instance.lock", O_CREAT | O_RDWR, 0o600)
if instanceLock < 0 || flock(instanceLock, LOCK_EX | LOCK_NB) != 0 {
    if !trayOnly { askRunningToShow() }
    exit(0)
}

Settings.refreshAutoStart()
do { try Server.start() } catch {
    let a = NSAlert()
    a.messageText = "Claude Tools 启动失败"
    a.informativeText = errMsg(error)
    a.runModal()
    exit(1)
}
DispatchQueue.global(qos: .utility).async { _ = Cli.loginPath() }

// 自动合并所有账号的会话记录：启动后 15 秒一次，之后每 5 分钟
let mergeTimer = DispatchSource.makeTimerSource(queue: DispatchQueue.global(qos: .utility))
mergeTimer.schedule(deadline: .now() + 15, repeating: 300)
mergeTimer.setEventHandler { DesktopStore.autoMerge() }
if !(Env.test && Env.get("CLAUDE_SWITCHER_NO_AUTOMERGE") == "1") { mergeTimer.resume() }

if Env.noBrowser {
    // 自动测试：只运行本机服务
    while true { RunLoop.main.run(until: Date().addingTimeInterval(3600)) }
}

let app = NSApplication.shared
let appDelegate = AppDelegate(trayOnly: trayOnly)
AppUI.delegate = appDelegate
app.delegate = appDelegate
app.setActivationPolicy(trayOnly ? .accessory : .regular)
app.run()
