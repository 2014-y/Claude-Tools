# 启动带窗口的 App（假数据），逐页截图并检查界面状态。
# 用法：python3 mac/tests/screenshots.py "mac/build/Claude Tools.app" mac/build/shots
import json, os, subprocess, sys, tempfile, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fixtures as F

APP = os.path.abspath(sys.argv[1])
EXE = os.path.join(APP, "Contents", "MacOS", "ClaudeTools")
OUT = os.path.abspath(sys.argv[2] if len(sys.argv) > 2 else "mac/build/shots")
os.makedirs(OUT, exist_ok=True)
fails = []


def check(name, cond, detail=""):
    print(("  PASS " if cond else "  FAIL ") + name + ("" if cond else "  →  " + str(detail)[:400]), flush=True)
    if not cond:
        fails.append(name)


def js(app, code):
    return app.call("test/eval", {"js": code})


def shot(app, name, wait=2.5):
    time.sleep(wait)
    p = app.call("test/snapshot", {"name": name})
    ok = isinstance(p, str) and p.endswith(".png") and os.path.exists(p)
    check("截图 " + name, ok, p)
    if ok:
        os.replace(p, os.path.join(OUT, name + ".png"))


def wait_for(app, code, want="true", secs=30):
    v = None
    for _ in range(int(secs * 4)):
        v = js(app, code)
        if str(v).lower() == want:
            return True
        time.sleep(0.25)
    print("    等待超时：", code, "=", v)
    return False


def main():
    root = tempfile.mkdtemp(prefix="ct-shots-")
    F.setup_keychain()
    info = F.build(root)
    _, usage_url = F.usage_server()
    env = F.env_for(info, {"CLAUDE_SWITCHER_USAGE_URL": usage_url})
    app = F.App(EXE, env, os.path.join(OUT, "app-gui.log"))
    try:
        ok = False
        for _ in range(80):
            w = app.call("test/window")
            if w.get("window") and w.get("visible"):
                ok = True
                break
            time.sleep(0.25)
        check("窗口已显示", ok, w)
        check("窗口大小合理、最小尺寸 1000×640", ok and w["frame"][2] >= 1000 and w["frame"][3] >= 640 and w["minSize"] == [1000, 640], w)
        check("菜单栏图标已创建、Dock 显示", w.get("statusItem") and w.get("policy") == "regular", w)
        check("页面加载完成", wait_for(app, "document.readyState==='complete' && typeof S==='object' && S.desktop.length===3"))
        check("识别为 Mac", js(app, "String(IS_MAC && document.documentElement.classList.contains('mac'))") == "true")
        check("窗口标题", app.call("test/window").get("title") == "Claude Tools")
        check("没有断线提示", js(app, "String($('#offline').classList.contains('show'))") == "false")
        check("内置字体已加载", wait_for(app, "String(document.fonts.check('16px PortalCute'))", secs=15))
        fs = js(app, "getComputedStyle(document.body).fontSize")
        check("正文字号 ≥ 14px", str(fs).endswith("px") and float(str(fs)[:-2]) >= 14, fs)
        # 官方额度：打开后自动查询（假接口），仪表盘显示 5 小时已用 38%
        check("自动查询官方额度并显示", wait_for(app, "String(!!(DUSAGE.p1 && DUSAGE.p1.session && DUSAGE.p1.session.used===38))", secs=30))
        js(app, "go('stats');0")
        check("用量统计完成", wait_for(app, "String(!!(STATS && STATS.ready))", secs=30))

        for p in ["dash", "acc", "sess", "stats", "logs", "clean", "set"]:
            js(app, "go('%s');window.scrollTo(0,0);0" % p)
            shot(app, "%02d-%s" % (["dash", "acc", "sess", "stats", "logs", "clean", "set"].index(p) + 1, p), 3)
        js(app, "go('acc');setAccSub('cli');renderCli();0")
        shot(app, "08-acc-cli")
        js(app, "setAccSub('desk');go('set');document.querySelector('#setTabs [data-st=proxy]').click();0")
        shot(app, "09-set-proxy")
        js(app, "document.querySelector('#setTabs [data-st=gen]').click();0")
        js(app, "go('acc');document.querySelector('#dAdd').click();0")
        shot(app, "10-dialog-add", 1.5)
        js(app, "document.querySelector('#mCancel').click();applyMode('light');go('dash');0")
        shot(app, "11-dash-light")
        js(app, "applyMode('dark');try{localStorage.setItem('lang','en')}catch(e){};location.reload();0")
        time.sleep(1)
        check("英文界面重新加载", wait_for(app, "document.readyState==='complete' && typeof S==='object' && S.desktop.length===3 && document.documentElement.lang==='en'"))
        js(app, "go('dash');0")
        shot(app, "12-dash-en", 4)
        js(app, "go('set');0")
        shot(app, "13-set-en", 3)
        js(app, "try{localStorage.setItem('lang','zh-TW')}catch(e){};location.reload();0")
        time.sleep(1)
        wait_for(app, "document.readyState==='complete' && typeof S==='object' && S.desktop.length===3")
        js(app, "go('acc');0")
        shot(app, "14-acc-zh-TW", 4)
        js(app, "try{localStorage.setItem('lang','zh-CN')}catch(e){};location.reload();0")
        time.sleep(1)
        wait_for(app, "document.readyState==='complete' && typeof S==='object' && S.desktop.length===3")
        js(app, "go('dash');0")
        time.sleep(3)
        r = subprocess.run(["screencapture", "-x", os.path.join(OUT, "00-screen.png")], capture_output=True, text=True)
        check("整屏截图（含窗口标题栏与菜单栏）", r.returncode == 0 and os.path.exists(os.path.join(OUT, "00-screen.png")), r.stderr)

        # 关窗只隐藏、再显示同一个窗口；记住窗口位置
        js(app, "window.__keep=12345;0")
        app.call("test/close")
        time.sleep(1.5)
        w = app.call("test/window")
        check("点关闭只隐藏窗口（后台运行）", w.get("window") and not w.get("visible") and w.get("policy") == "accessory", w)
        check("关闭时记住窗口位置大小", os.path.exists(os.path.join(info["state"], "window.txt")))
        check("程序仍在运行", app.alive())
        app.call("ui/show")
        time.sleep(1.5)
        w = app.call("test/window")
        check("再次显示同一个窗口（页面没有重新加载）", w.get("visible") and js(app, "String(window.__keep)") == "12345", w)
    finally:
        app.stop()
    print("\n截图：", sorted(os.listdir(OUT)))
    if fails:
        print("失败：", fails)
        sys.exit(1)


if __name__ == "__main__":
    main()
