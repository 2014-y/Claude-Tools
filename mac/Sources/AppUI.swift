// 界面：工具自己的窗口（内嵌 WKWebView 显示 ui.html）+ 菜单栏图标 + 系统通知。
// 关窗只隐藏（开启后台运行时），再点菜单栏图标直接显示同一个窗口；记住窗口位置和大小。
import AppKit
import WebKit
import UserNotifications

enum AppUI {
    static var delegate: AppDelegate?

    static func showFromAnyThread() {
        if Env.noBrowser { return }
        DispatchQueue.main.async { delegate?.showWindow() }
    }

    static func setTheme(dark: Bool) {
        if Env.noBrowser { return }
        DispatchQueue.main.async {
            delegate?.dark = dark
            delegate?.window?.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
            delegate?.window?.backgroundColor = dark ? AppDelegate.bgDark : AppDelegate.bgLight
        }
    }

    static func quit() {
        if Env.noBrowser { exit(0) }
        DispatchQueue.main.async { delegate?.saveFrame(); NSApp.terminate(nil) }
    }

    // 文件夹选择框（在主线程弹出，可粘贴路径 ⌘⇧G）
    static func pickDir(_ start: String) -> String {
        if Env.noBrowser { return "" }
        var result = ""
        DispatchQueue.main.sync {
            NSApp.activate(ignoringOtherApps: true)
            let p = NSOpenPanel()
            p.canChooseDirectories = true
            p.canChooseFiles = false
            p.allowsMultipleSelection = false
            p.canCreateDirectories = true
            p.title = "选择项目文件夹"
            p.prompt = "选择此文件夹"
            if !start.isEmpty && FS.isDir(start) { p.directoryURL = URL(fileURLWithPath: start) }
            if p.runModal() == .OK, let u = p.url { result = u.path }
        }
        return result
    }

    static func notify(_ title: String, _ body: String) {
        if Env.noBrowser || !Settings.bool("notify") { return }
        let c = UNUserNotificationCenter.current()
        c.requestAuthorization(options: [.alert, .sound]) { ok, _ in
            guard ok else { return }
            let ct = UNMutableNotificationContent()
            ct.title = title; ct.body = body
            c.add(UNNotificationRequest(identifier: UUID().uuidString, content: ct, trigger: nil))
        }
    }

    static let maskRe = Rx("([A-Za-z0-9._%+\\-]+)@([A-Za-z0-9\\-]+)((?:\\.[A-Za-z0-9\\-]+)+)")
    static func mask(_ s: String) -> String {
        let ns = s as NSString
        var out = s as NSString
        for m in maskRe.re.matches(in: s, range: NSRange(location: 0, length: ns.length)).reversed() {
            let u = ns.substring(with: m.range(at: 1)), h = ns.substring(with: m.range(at: 2)), rest = ns.substring(with: m.range(at: 3))
            let uu = u.count <= 2 ? String(u.prefix(1)) + "*" : u.count <= 4 ? String(u.prefix(1)) + "**" + String(u.suffix(1)) : String(u.prefix(2)) + "****" + String(u.suffix(2))
            out = out.replacingCharacters(in: m.range, with: uu + "@" + String(h.prefix(1)) + "***" + rest) as NSString
        }
        return out as String
    }

    // ---- 自动测试用 ----
    static func evalJs(_ js: String) -> Any {
        let sem = DispatchSemaphore(value: 0)
        var result: Any = NSNull()
        DispatchQueue.main.async {
            guard let wv = delegate?.web else { result = "ERR: no window"; sem.signal(); return }
            wv.evaluateJavaScript(js) { r, e in
                if let r = r { result = "\(r)" } else if let e = e { result = "ERR: " + e.localizedDescription }
                sem.signal()
            }
        }
        _ = sem.wait(timeout: .now() + 15)
        return result
    }

    static func snapshot(_ name0: String) -> String {
        let name = String(name0.unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) || $0 == "-" || $0 == "_" })
        let sem = DispatchSemaphore(value: 0)
        var path = ""
        DispatchQueue.main.async {
            guard let wv = delegate?.web else { path = "ERR: no window"; sem.signal(); return }
            wv.takeSnapshot(with: WKSnapshotConfiguration()) { img, err in
                if let img = img, let tiff = img.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff), let png = rep.representation(using: .png, properties: [:]) {
                    let p = Settings.stateDir + "/shots/" + name + ".png"
                    FS.mkdirs(FS.parent(p))
                    do { try png.write(to: URL(fileURLWithPath: p)); path = p } catch { path = "ERR: " + errMsg(error) }
                } else { path = "ERR: " + (err?.localizedDescription ?? "snapshot failed") }
                sem.signal()
            }
        }
        _ = sem.wait(timeout: .now() + 20)
        return path
    }

    static func windowInfo() -> [String: Any] {
        var r: [String: Any] = [:]
        DispatchQueue.main.sync {
            guard let w = delegate?.window else { r["window"] = false; return }
            r["window"] = true
            r["visible"] = w.isVisible
            r["title"] = w.title
            r["frame"] = [w.frame.origin.x, w.frame.origin.y, w.frame.size.width, w.frame.size.height]
            r["minSize"] = [w.minSize.width, w.minSize.height]
            r["windowNumber"] = w.windowNumber
            r["policy"] = NSApp.activationPolicy() == .regular ? "regular" : "accessory"
            r["statusItem"] = delegate?.statusItem != nil
        }
        return r
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSMenuDelegate, WKNavigationDelegate, WKUIDelegate, UNUserNotificationCenterDelegate {
    static let bgDark = NSColor(srgbRed: 13 / 255, green: 11 / 255, blue: 10 / 255, alpha: 1)
    static let bgLight = NSColor(srgbRed: 246 / 255, green: 243 / 255, blue: 240 / 255, alpha: 1)
    let trayOnly: Bool
    var window: NSWindow?
    var web: WKWebView?
    var statusItem: NSStatusItem?
    var dark = true
    var titleObs: NSKeyValueObservation?

    init(trayOnly: Bool) { self.trayOnly = trayOnly }

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMainMenu()
        setupStatusItem()
        UNUserNotificationCenter.current().delegate = self
        if trayOnly { NSApp.setActivationPolicy(.accessory) } else { showWindow() }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { showWindow(); return true }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply { saveFrame(); return .terminateNow }

    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list])
    }

    // ---------------- 主菜单（含编辑菜单，输入框里才能用 ⌘C / ⌘V） ----------------
    func buildMainMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem(); main.addItem(appItem)
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "关于 Claude Tools", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "隐藏 Claude Tools", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        let ho = appMenu.addItem(withTitle: "隐藏其他", action: #selector(NSApplication.hideOtherApplications(_:)), keyEquivalent: "h")
        ho.keyEquivalentModifierMask = [.command, .option]
        appMenu.addItem(withTitle: "全部显示", action: #selector(NSApplication.unhideAllApplications(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "退出 Claude Tools", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu

        let editItem = NSMenuItem(); main.addItem(editItem)
        let edit = NSMenu(title: "编辑")
        edit.addItem(withTitle: "撤销", action: Selector(("undo:")), keyEquivalent: "z")
        let redo = edit.addItem(withTitle: "重做", action: Selector(("redo:")), keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(.separator())
        edit.addItem(withTitle: "剪切", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "拷贝", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "粘贴", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "全选", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit

        let viewItem = NSMenuItem(); main.addItem(viewItem)
        let view = NSMenu(title: "显示")
        let r = view.addItem(withTitle: "刷新全部数据", action: #selector(refreshAll), keyEquivalent: "r")
        r.target = self
        viewItem.submenu = view

        let winItem = NSMenuItem(); main.addItem(winItem)
        let win = NSMenu(title: "窗口")
        win.addItem(withTitle: "最小化", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        win.addItem(withTitle: "缩放", action: #selector(NSWindow.performZoom(_:)), keyEquivalent: "")
        win.addItem(.separator())
        win.addItem(withTitle: "关闭窗口", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        winItem.submenu = win
        NSApp.mainMenu = main
        NSApp.windowsMenu = win
    }

    @objc func refreshAll() { web?.evaluateJavaScript("typeof refreshAll==='function'&&refreshAll(false);0", completionHandler: nil) }

    // ---------------- 菜单栏图标 ----------------
    static func menuIcon() -> NSImage {
        let img = NSImage(size: NSSize(width: 18, height: 18), flipped: true) { _ in
            NSColor.black.setFill()
            let s: CGFloat = 17.0 / 889.0
            let ox: CGFloat = (18 - 889 * s) / 2 - 183 * s, oy: CGFloat = (18 - 535 * s) / 2 - 348 * s
            func R(_ x1: CGFloat, _ y1: CGFloat, _ x2: CGFloat, _ y2: CGFloat) -> NSRect {
                NSRect(x: ox + x1 * s, y: oy + y1 * s, width: (x2 - x1) * s, height: (y2 - y1) * s)
            }
            for rr in [R(340, 348, 915, 758), R(183, 494, 1072, 617), R(340, 758, 415, 883), R(495, 758, 570, 883), R(685, 758, 760, 883), R(840, 758, 915, 883)] { rr.fill() }
            R(415, 410, 500, 493).fill(using: .clear)
            R(755, 410, 838, 493).fill(using: .clear)
            return true
        }
        img.isTemplate = true
        return img
    }

    func setupStatusItem() {
        let it = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        it.button?.image = AppDelegate.menuIcon()
        it.button?.toolTip = "Claude Tools"
        let menu = NSMenu()
        menu.delegate = self
        menu.autoenablesItems = false
        it.menu = menu
        statusItem = it
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let open = NSMenuItem(title: "打开 Claude Tools", action: #selector(openClicked), keyEquivalent: "")
        open.target = self
        let bold = NSFontManager.shared.convert(NSFont.menuFont(ofSize: 0), toHaveTrait: .boldFontMask)
        open.attributedTitle = NSAttributedString(string: "打开 Claude Tools", attributes: [.font: bold])
        menu.addItem(open)
        menu.addItem(.separator())
        let h = NSMenuItem(title: "切换 Claude 桌面 App 账号", action: nil, keyEquivalent: "")
        h.isEnabled = false
        menu.addItem(h)
        let maskOn = Settings.bool("maskDefault")
        let profiles = DesktopStore.load()
        if profiles.isEmpty {
            let e = NSMenuItem(title: "还没有账号", action: nil, keyEquivalent: ""); e.isEnabled = false; e.indentationLevel = 1; menu.addItem(e)
        }
        for p in profiles {
            let name = maskOn ? AppUI.mask(p.remark) : p.remark
            let email = p.email.isEmpty || p.email == p.remark ? "" : "  (" + (maskOn ? AppUI.mask(p.email) : p.email) + ")"
            let item = NSMenuItem(title: name + email, action: #selector(switchClicked(_:)), keyEquivalent: "")
            item.target = self
            item.state = p.active ? .on : .off
            item.representedObject = [p.id, p.remark]
            item.indentationLevel = 1
            menu.addItem(item)
        }
        menu.addItem(.separator())
        let auto = NSMenuItem(title: "登录时自动启动", action: #selector(toggleAuto), keyEquivalent: "")
        auto.target = self; auto.state = Settings.autoStart ? .on : .off
        menu.addItem(auto)
        let keep = NSMenuItem(title: "关闭窗口后后台运行", action: #selector(toggleKeep), keyEquivalent: "")
        keep.target = self; keep.state = Settings.bool("trayMode") ? .on : .off
        menu.addItem(keep)
        menu.addItem(.separator())
        let q = NSMenuItem(title: "退出 Claude Tools", action: #selector(quitClicked), keyEquivalent: "")
        q.target = self
        menu.addItem(q)
    }

    @objc func openClicked() { showWindow() }
    @objc func quitClicked() { saveFrame(); NSApp.terminate(nil) }
    @objc func toggleAuto() {
        do { try Settings.set("autoStart", !Settings.autoStart) } catch { AppUI.notify("设置失败", errMsg(error)); showAlert("设置失败", errMsg(error)) }
    }
    @objc func toggleKeep() { try? Settings.set("trayMode", !Settings.bool("trayMode")) }
    @objc func switchClicked(_ sender: NSMenuItem) {
        guard let v = sender.representedObject as? [String], v.count == 2 else { return }
        let id = v[0], remark = v[1]
        DispatchQueue.global(qos: .userInitiated).async {
            do {
                try Api.doSwitch(id)
                AppUI.notify("切换成功", "Claude 桌面 App 已切换到「" + (Settings.bool("maskDefault") ? AppUI.mask(remark) : remark) + "」")
            } catch { AppUI.notify("切换失败", errMsg(error)) }
            DispatchQueue.main.async { self.web?.evaluateJavaScript("typeof refresh==='function'&&refresh(true);0", completionHandler: nil) }
        }
    }

    func showAlert(_ title: String, _ text: String) {
        let a = NSAlert()
        a.messageText = title; a.informativeText = text
        a.alertStyle = .warning
        NSApp.activate(ignoringOtherApps: true)
        a.runModal()
    }

    // ---------------- 窗口 ----------------
    var boundsFile: String { Settings.stateDir + "/window.txt" }

    func defaultFrame() -> NSRect {
        let vf = (NSScreen.main ?? NSScreen.screens.first)?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let w = min(1400, vf.width * 0.88), h = min(900, vf.height * 0.9)
        return NSRect(x: vf.minX + (vf.width - w) / 2, y: vf.minY + (vf.height - h) / 2, width: w, height: h)
    }

    func savedFrame() -> NSRect? {
        guard let s = FS.readText(boundsFile) else { return nil }
        let a = s.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: ",").compactMap { Double($0) }
        guard a.count == 4 else { return nil }
        let r = NSRect(x: a[0], y: a[1], width: a[2], height: a[3])
        guard r.width >= 600 && r.height >= 400 else { return nil }
        // 必须大部分落在某个屏幕内，否则（比如拔了外接屏）用默认位置
        for sc in NSScreen.screens {
            let i = sc.visibleFrame.intersection(r)
            if i.width * i.height >= r.width * r.height / 2 { return r }
        }
        return nil
    }

    func saveFrame() {
        guard let w = window, !w.isMiniaturized else { return }
        let r = w.frame
        if r.width < 600 || r.height < 400 { return }
        try? FS.write(boundsFile, "\(Int(r.origin.x)),\(Int(r.origin.y)),\(Int(r.size.width)),\(Int(r.size.height))")
    }

    func createWindow() {
        let frame = savedFrame() ?? defaultFrame()
        let w = NSWindow(contentRect: frame, styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        w.setFrame(frame, display: false)
        w.title = "Claude Tools"
        w.minSize = NSSize(width: 1000, height: 640)
        w.isReleasedWhenClosed = false
        w.tabbingMode = .disallowed
        w.delegate = self
        w.backgroundColor = dark ? AppDelegate.bgDark : AppDelegate.bgLight
        w.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)

        let cfg = WKWebViewConfiguration()
        cfg.websiteDataStore = .default()
        cfg.userContentController.addUserScript(WKUserScript(source: "window.CT_MAC=true;", injectionTime: .atDocumentStart, forMainFrameOnly: true))
        cfg.preferences.javaScriptCanOpenWindowsAutomatically = false
        let wv = WKWebView(frame: NSRect(origin: .zero, size: frame.size), configuration: cfg)
        wv.autoresizingMask = [.width, .height]
        wv.setValue(false, forKey: "drawsBackground")
        if #available(macOS 12.0, *) { wv.underPageBackgroundColor = AppDelegate.bgDark }
        wv.navigationDelegate = self
        wv.uiDelegate = self
        wv.allowsBackForwardNavigationGestures = false
        wv.allowsMagnification = false
        w.contentView = wv
        titleObs = wv.observe(\.title, options: [.new]) { [weak self] v, _ in
            if let t = v.title, !t.isEmpty, !t.hasPrefix("http") { self?.window?.title = t }
        }
        window = w
        web = wv
        if let u = URL(string: Server.url) { wv.load(URLRequest(url: u)) }
    }

    func showWindow() {
        if window == nil { createWindow() }
        guard let w = window else { return }
        NSApp.setActivationPolicy(.regular)
        if w.isMiniaturized { w.deminiaturize(nil) }
        w.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) {
            NSApp.activate(ignoringOtherApps: true)
            w.makeKeyAndOrderFront(nil)
        }
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        saveFrame()
        if Settings.bool("trayMode") {
            sender.orderOut(nil)                // 后台运行：只隐藏，页面保留
            NSApp.setActivationPolicy(.accessory)
        } else {
            NSApp.terminate(nil)
        }
        return false
    }
    func windowDidEndLiveResize(_ notification: Notification) { saveFrame() }
    func windowDidMove(_ notification: Notification) { saveFrame() }

    // 外部链接用默认浏览器打开
    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        if let u = navigationAction.request.url, let h = u.host, h != "127.0.0.1" && h != "localhost", u.scheme == "http" || u.scheme == "https" {
            NSWorkspace.shared.open(u)
            decisionHandler(.cancel); return
        }
        decisionHandler(.allow)
    }
    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration, for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let u = navigationAction.request.url { NSWorkspace.shared.open(u) }
        return nil
    }
    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) { webView.reload() }
}
