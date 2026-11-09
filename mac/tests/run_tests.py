# macOS 版自动回归测试：用假数据在临时目录造 3 个桌面账号，测接口与 Windows 版行为一致。
# 用法：python3 mac/tests/run_tests.py "mac/build/Claude Tools.app" [日志目录]
import glob, hashlib, json, os, plistlib, shutil, sys, tempfile, time, traceback, urllib.request
from datetime import datetime, timedelta

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fixtures as F

APP = os.path.abspath(sys.argv[1])
EXE = os.path.join(APP, "Contents", "MacOS", "ClaudeTools")
LOGDIR = os.path.abspath(sys.argv[2] if len(sys.argv) > 2 else "mac/build/test-logs")
os.makedirs(LOGDIR, exist_ok=True)

results = []


def check(name, cond, detail=""):
    results.append((name, bool(cond), detail))
    print(("  PASS " if cond else "  FAIL ") + name + ("" if cond else "  →  " + str(detail)[:600]), flush=True)
    return bool(cond)


def group(title):
    def deco(fn):
        def run(*a):
            print("\n== " + title, flush=True)
            try:
                fn(*a)
            except Exception as e:
                check(title + "（异常）", False, "".join(traceback.format_exception(e))[-1500:])
        run.title = title
        return run
    return deco


def sha(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest() if os.path.exists(path) else None


def tree_hash(root):
    out = {}
    for dp, dn, fn in os.walk(root):
        for f in fn:
            p = os.path.join(dp, f)
            out[os.path.relpath(p, root)] = sha(p)
    return out


def token_of(dir_):
    p = os.path.join(dir_, "config.json")
    return json.load(open(p)).get("oauth:tokenCacheV2") if os.path.exists(p) else None


def marker(dir_):
    p = os.path.join(dir_, ".cs-profile")
    return open(p, encoding="utf-8").read().split("\n")[0].strip() if os.path.exists(p) else None


def by_id(st, i):
    return next((p for p in st["desktop"] if p["id"] == i), None)


def expect_error(app, name, body, frag):
    try:
        app.call(name, body)
        return check("%s 应报错（%s）" % (name, frag), False, "没有报错")
    except F.ApiError as e:
        return check("%s 报错：%s" % (name, e), frag in str(e), str(e))


# ---------------------------------------------------------------- 测试
@group("安全：令牌校验")
def t_security(app):
    st, body = app.raw("GET", "/", token=False)
    check("首页不带令牌 → 403", st == 403, st)
    st, body = app.raw("GET", "/?t=" + app.token, token=False)
    check("首页带令牌 → 200 且是界面", st == 200 and b"Claude Tools" in body and b"IS_MAC" in body, st)
    st, _ = app.raw("POST", "api/state", b"{}", token=False)
    check("接口不带 X-Token → 403", st == 403, st)
    req = urllib.request.Request(app.base + "api/state", data=b"{}", method="POST", headers={"X-Token": "wrong" * 8})
    try:
        urllib.request.urlopen(req, timeout=10); code = 200
    except urllib.error.HTTPError as e:
        code = e.code
    check("接口令牌错误 → 403", code == 403, code)
    st, _ = app.raw("GET", "api/state")
    check("接口用 GET → 403", st == 403, st)
    st, font = app.raw("GET", "/font.woff2", token=False)
    check("内置字体可加载", st == 200 and len(font) > 100000 and font[:4] == b"wOF2", (st, len(font)))
    st, logo = app.raw("GET", "/logo.png", token=False)
    check("图标可加载", st == 200 and logo[:4] == b"\x89PNG", st)
    expect_error(app, "no/such", {}, "未知接口")
    b = app.call("ping")
    check("ping 返回版本标识", isinstance(b, str) and b.isdigit(), b)


@group("state：桌面账号、终端账号、额度、设置")
def t_state(app, info):
    st = app.call("state")
    d = st["desktop"]
    check("识别到 3 个桌面账号", len(d) == 3, [p["id"] for p in d])
    a = d[0]
    check("当前账号自动登记为 p1、排在第一", a["id"] == "p1" and a["active"], a)
    check("p1 备注 = 识别到的邮箱", a["remark"] == F.EMAIL_A, a["remark"])
    check("p1 邮箱识别正确（排除 sentry/anthropic）", a["email"] == F.EMAIL_A, a["email"])
    check("标记文件已写入当前目录", marker(info["active"]) == "p1")
    b, c = by_id(st, "p2"), by_id(st, "p3")
    check("p2 邮箱（UTF-16 编码）识别正确", b and b["email"] == F.EMAIL_B, b and b["email"])
    check("p2 备注", b and b["remark"] == "工作号")
    check("登录状态：p1/p2 已登录、p3 未登录", a["loggedIn"] and b["loggedIn"] and not c["loggedIn"], (a["loggedIn"], b["loggedIn"], c["loggedIn"]))
    check("会话数：p1=2（不含删除标记）p2=2 p3=0", (a["sessions"], b["sessions"], c["sessions"]) == (2, 2, 0), (a["sessions"], b["sessions"], c["sessions"]))
    q = a["quota"]
    check("p1 桌面 App 记录的额度：5H=25 7天=45", q and q["fiveHour"] == 25 and q["sevenDay"] == 45, q)
    check("p1 额度走势样本", q and len(q["samples"]) == 4 and len(q["samples"][0]) == 3, q and q["samples"])
    check("p3 没有额度记录 → null", c["quota"] is None)
    check("live：p1/p2 有可查询的登录，p3 没有", a["live"] and b["live"] and not c["live"], (a["live"], b["live"], c["live"]))
    check("推荐账号 best = p2（唯一有用量的非当前账号）", st["best"] == "p2", st["best"])
    check("桌面 App 运行状态（测试模式）= false", st["desktopRunning"] is False)
    check("终端账号：只有原始账号", len(st["cli"]) == 1 and st["cli"][0]["isDefault"] and st["cli"][0]["id"] == "default", st["cli"])
    check("原始终端账号已登录、邮箱", st["cli"][0]["loggedIn"] and st["cli"][0]["email"] == "main.user@fastmail.com", st["cli"][0])
    s = st["settings"]
    check("设置：默认值 + 平台 mac + 版本 2.0.0", s["platform"] == "mac" and s["version"] == "2.0.0" and s["trayMode"] is True and s["syncSessions"] is True, s)
    check("设置：路径", s["profilesDir"] == info["profiles"] and s["cliDir"] == os.path.join(info["home"], ".claude-accounts") and s["stateDir"] == info["state"], s)
    check("lastUsed 格式 yyyy/MM/dd HH:mm", len(a["lastUsed"]) == 16 and a["lastUsed"][4] == "/", a["lastUsed"])
    check("now 接近当前时间", abs(st["now"] - time.time() * 1000) < 60000, st["now"])


@group("会话记录列表")
def t_sessions(app, info):
    ss = app.call("sessions")
    ids = sorted(s["id"] for s in ss)
    check("会话列表：s1 s2 s3（s9 已在 A 删除不显示）", ids == ["local_s1", "local_s2", "local_s3"], ids)
    s1 = next(s for s in ss if s["id"] == "local_s1")
    check("会话字段：标题 / 目录 / cliSessionId / 账号标签", s1["title"] == "修复登录页面" and s1["cwd"] == "/Users/test/proj" and s1["cli"] == "cli-1" and s1["account"] == F.EMAIL_A, s1)
    check("按最后活动时间倒序", ss[0]["id"] == "local_s1", [s["id"] for s in ss])
    st = app.call("state")
    check("常用项目目录不含临时会话、不含不存在的目录", all("scratch-workspaces" not in r for r in st["recent"]), st["recent"])


@group("官方实时额度（钥匙串解密 + 假接口 + 缓存 + 限流）")
def t_live(app, info):
    F.UsageState.hits.clear()
    r = app.call("desktop/usage", {"id": "p1"})
    check("p1 查询成功、没有错误", r["error"] is None, r.get("error"))
    check("5 小时已用 = 38（37.5 按银行家舍入）", r["session"] and r["session"]["used"] == 38, r["session"])
    check("每周已用 = 62", r["week"] and r["week"]["used"] == 62, r["week"])
    now = time.time() * 1000
    check("重置时间换算为毫秒", r["session"]["resetAt"] > now + 2 * 3600e3 and r["week"]["resetAt"] > now + 3 * 86400e3, (r["session"]["resetAt"], r["week"]["resetAt"]))
    check("查询锁 120 秒", 100e3 < r["nextAt"] - now <= 121e3, r["nextAt"] - now)
    check("用的是 user:profile 令牌（tok-A）", F.UsageState.hits.get("tok-A") == 1, F.UsageState.hits)
    check("走势记录 hist", isinstance(r["hist"], list) and len(r["hist"]) == 1 and r["hist"][0][1:] == [38, 62], r["hist"])
    r2 = app.call("desktop/usage", {"id": "p1"})
    check("锁定期内再查：不访问接口、返回缓存", F.UsageState.hits.get("tok-A") == 1 and r2["session"]["used"] == 38, F.UsageState.hits)
    rb = app.call("desktop/usage", {"id": "p2"})
    check("p2 被限流：提示 30 秒后再查", rb["error"] == "官方限流，30 秒后自动再查" and rb["session"] is None, rb["error"])
    check("限流时下次查询时间按官方给的 30 秒", 20e3 < rb["nextAt"] - time.time() * 1000 <= 31e3, rb["nextAt"] - time.time() * 1000)
    rc = app.call("desktop/usage", {"id": "p3"})
    check("p3 没有登录：明确提示", rc["error"] == "这个账号还没有登录桌面 App", rc["error"])
    cache = json.load(open(os.path.join(info["state"], "live-cache.json")))
    check("缓存与查询锁持久化（重启也不会提前查）", "lockUntil" in cache["p1"] and cache["p1"]["session"]["used"] == 38 and "lockUntil" in cache["p2"], list(cache))
    check("不写回、不改动登录信息", token_of(info["active"]) == info["tokA0"])


@group("切换账号 p1 → p2 → p1（目录互换、会话同步、客户端设置同步、回到原样）")
def t_switch(app, info):
    act, prof = info["active"], info["profiles"]
    st = app.call("desktop/switch", {"id": "p2"})
    check("切换后当前账号 = p2", marker(act) == "p2" and by_id(st, "p2")["active"], marker(act))
    check("p1 已存档到 Claude-profiles/p1", marker(os.path.join(prof, "p1")) == "p1")
    check("Claude-profiles/p2 已不存在（移到当前位置）", not os.path.exists(os.path.join(prof, "p2")))
    tgt = os.path.join(act, "claude-code-sessions", "acc-B", "org-B")
    got = sorted(os.path.basename(x) for x in glob.glob(os.path.join(tgt, "*")))
    check("p1 的会话已同步到 p2 当前登录账号目录", "local_s1.json" in got and "local_s2.json" in got, got)
    check("A 里删除过的 s9 在 B 里也删除、留下删除标记", "local_s9.json" not in got and "deleted_s9" in got, got)
    check("lastSync 提示", "已同步 2 条会话记录" in st["lastSync"] and "同步删除 1 条" in st["lastSync"] and "已同步客户端设置" in st["lastSync"], st["lastSync"])
    check("临时会话的工作目录一起带过去", open(os.path.join(act, "scratch-workspaces", "ws1", "notes.txt")).read() == "scratch file")
    cfg = json.load(open(os.path.join(act, "config.json")))
    check("客户端设置同步：主题、缩放", cfg.get("userThemeMode") == "dark" and cfg.get("windowControlsZoomFactor") == 1.2 and cfg.get("locale") == "zh-CN", cfg)
    check("p2 的登录令牌没被覆盖", cfg.get("oauth:tokenCacheV2") == info["tokB0"] and cfg.get("lastKnownAccountUuid") == "acc-B")
    check("config.json 修改前有备份", os.path.exists(os.path.join(act, "config.json.bak")))
    check("整文件同步（window-state.json / MCP 配置）", json.load(open(os.path.join(act, "window-state.json")))["width"] == 1234 and os.path.exists(os.path.join(act, "claude_desktop_config.json")))
    check("p1 存档的令牌原样保留", token_of(os.path.join(prof, "p1")) == info["tokA0"])
    logs = app.call("logs")
    check("操作日志记录了切换", logs and logs[0]["action"] == "切换桌面账号" and logs[0]["ok"] and "→ 工作号" in logs[0]["target"], logs[:1])

    st = app.call("desktop/switch", {"id": "p1"})
    check("切回 p1", marker(act) == "p1" and marker(os.path.join(prof, "p2")) == "p2" and not os.path.exists(os.path.join(prof, "p1")))
    got = sorted(os.path.basename(x) for x in glob.glob(os.path.join(act, "claude-code-sessions", "acc-A", "org-A", "*")))
    check("p2 的会话 s3 同步回 p1、s9 不复活", "local_s3.json" in got and "local_s9.json" not in got, got)
    check("切回后 p1 令牌不变", token_of(act) == info["tokA0"])
    check("切回后 p2 令牌不变", token_of(os.path.join(prof, "p2")) == info["tokB0"])
    st = app.call("desktop/switch", {"id": "p1"})
    check("切换到当前账号：不移动目录", marker(act) == "p1")
    check("今日切换次数 = 3（含切到当前账号）", st["switchesToday"] == 3, st["switchesToday"])


@group("添加 / 改备注 / 删除桌面账号")
def t_add_delete(app, info):
    act, prof = info["active"], info["profiles"]
    st = app.call("desktop/add", {"remark": "新号"})
    m = marker(act)
    check("添加后当前是新的空账号 p4", m == "p4" and by_id(st, "p4") and by_id(st, "p4")["remark"] == "新号" and not by_id(st, "p4")["loggedIn"], m)
    cfg4 = json.load(open(os.path.join(act, "config.json"))) if os.path.exists(os.path.join(act, "config.json")) else {}
    check("新账号带上 MCP 配置和客户端设置、不带登录信息", os.path.exists(os.path.join(act, "claude_desktop_config.json")) and "oauth:tokenCacheV2" not in cfg4 and cfg4.get("userThemeMode") == "dark", cfg4)
    check("原账号 p1 自动存档", marker(os.path.join(prof, "p1")) == "p1" and token_of(os.path.join(prof, "p1")) == info["tokA0"])
    check("共 4 个账号", len(st["desktop"]) == 4)
    st = app.call("desktop/switch", {"id": "p1"})
    check("切回 p1", marker(act) == "p1")
    expect_error(app, "desktop/delete", {"id": "p1"}, "正在使用的账号不能删除")
    st = app.call("desktop/delete", {"id": "p4"})
    check("删除 p4", not os.path.exists(os.path.join(prof, "p4")) and len(st["desktop"]) == 3)
    expect_error(app, "desktop/delete", {"id": "p4"}, "找不到这个账号的存档")
    mt = os.path.getmtime(os.path.join(prof, "p3", ".cs-profile"))
    st = app.call("desktop/rename", {"id": "p3", "remark": "备用号2"})
    check("改备注", by_id(st, "p3")["remark"] == "备用号2")
    check("改备注不影响「上次使用」时间", abs(os.path.getmtime(os.path.join(prof, "p3", ".cs-profile")) - mt) < 1)
    st = app.call("desktop/rename", {"id": "p1", "remark": "主力号"})
    check("当前账号改备注", by_id(st, "p1")["remark"] == "主力号" and open(os.path.join(act, ".cs-profile"), encoding="utf-8").read().split("\n")[1] == "主力号")


@group("合并会话记录（含删除同步）")
def t_merge(app, info):
    act, prof = info["active"], info["profiles"]
    # 在 p2 里删除 s3：合并后 p1 里的 s3 也删掉，不会复活
    sB = os.path.join(prof, "p2", "claude-code-sessions", "acc-B", "org-B")
    os.remove(os.path.join(sB, "local_s3.json"))
    open(os.path.join(sB, "deleted_s3"), "w").write("1")
    st = app.call("desktop/merge")
    msg = st["mergeResult"]
    check("合并结果提示（p3 没登录跳过）", msg.startswith("已合并 3 个账号") and "同步删除 1 条" in msg and "有账号还没登录过，已跳过" in msg, msg)
    a = sorted(os.path.basename(x) for x in glob.glob(os.path.join(act, "claude-code-sessions", "acc-A", "org-A", "local_*")))
    b = sorted(os.path.basename(x) for x in glob.glob(os.path.join(sB, "local_*")))
    check("p1 只剩 s1 s2（s3 已同步删除）", a == ["local_s1.json", "local_s2.json"], a)
    check("p2 有 s1 s2", b == ["local_s1.json", "local_s2.json"], b)
    msg2 = app.call("desktop/merge")["mergeResult"]
    check("再合并一次：补齐 0 条", "补齐 0 条" in msg2, msg2)
    ss = app.call("sessions")
    check("会话列表不再显示 s3", sorted(s["id"] for s in ss) == ["local_s1", "local_s2"], [s["id"] for s in ss])


@group("Token 用量统计（去重、筛选、费用、趋势、日志）")
def t_stats(app, info):
    r = app.call("stats", {"force": True, "range": "7d"})
    for _ in range(100):
        if r.get("ready") and not r.get("scanning"):
            break
        time.sleep(0.2)
        r = app.call("stats", {"range": "7d"})
    check("统计完成", r.get("ready"), r)
    s = r["summary"]
    # 近 7 天：msg_1(opus 去重取最后一次) + msg_2(sonnet 细分缓存) + msg_3(haiku 3 天前) + msg_5(子代理)
    cost1 = (100 * 5 + 300 * 25 + 5000 * 0.5 + 1000 * 6.25) / 1e6
    cost2 = (50 * 3 + 120 * 15 + 200 * 3.75 + 800 * 6) / 1e6
    cost3 = (2000 * 1 + 400 * 5 + 100 * 0.1) / 1e6
    cost5 = (10 * 3 + 20 * 15 + 30 * 0.3) / 1e6
    check("请求数 = 4（同一消息去重、<synthetic> 不计、40 天前不计）", s["requests"] == 4, s)
    check("新增输入 = 2160", s["input"] == 100 + 50 + 2000 + 10, s["input"])
    check("输出 = 840（拆分行取最终用量）", s["output"] == 300 + 120 + 400 + 20, s["output"])
    check("缓存命中 = 5130", s["cacheRead"] == 5000 + 100 + 30, s["cacheRead"])
    check("缓存创建 = 2000", s["cacheWrite"] == 2000, s["cacheWrite"])
    check("费用估算", abs(s["cost"] - round(cost1 + cost2 + cost3 + cost5, 4)) < 1e-9, (s["cost"], cost1 + cost2 + cost3 + cost5))
    prompt = 2160 + 5130 + 2000
    check("缓存命中率", abs(s["hitRate"] - round(5130 * 100 / prompt, 1)) < 1e-9, s["hitRate"])
    check("模型列表", r["models"] == sorted(["claude-opus-4-5-20251101", "claude-sonnet-4-5", "claude-haiku-4-5"]), r["models"])
    check("账号归类：桌面会话按所属账号、其余归终端", set(r["accounts"]) == {"主力号", "终端 Claude Code / 其他"}, r["accounts"])
    check("趋势 7 天", len(r["trend"]) == 7, len(r["trend"]))
    check("按模型汇总按费用排序", r["byModel"][0]["name"] == "claude-opus-4-5-20251101", [m["name"] for m in r["byModel"]])
    projs = {p["name"] for p in r["byProject"]}
    check("按项目汇总", projs == {"/Users/test/proj", "/Users/test/other"}, projs)
    logs = r["logs"]
    check("请求日志 4 条、按时间倒序", len(logs) == 4 and logs[0]["model"] == "claude-sonnet-4-5", [l["model"] for l in logs])
    l1 = next(l for l in logs if l["model"].startswith("claude-opus"))
    check("日志：费用、已定价、耗时（拆分行累计）", abs(l1["cost"] - round(cost1, 4)) < 1e-9 and l1["priced"] and l1["secs"] == 42.0, l1)
    check("日志时间格式 MM/dd HH:mm:ss", len(l1["t"]) == 14 and l1["t"][2] == "/", l1["t"])
    today = datetime.now().date()
    t = info["records"]["t"]
    if t.date() == today:
        check("今日：3 次请求（opus+sonnet+子代理）", r["today"]["requests"] == 3, r["today"])
        hourly = r["todayHourly"]
        check("今日每小时：24 格、未来为 -1、当小时有数据", len(hourly) == 24 and hourly[t.hour] > 0 and all(v == -1 for v in hourly[datetime.now().hour + 1:]), hourly)
    check("本月 ≥ 今日", r["month"]["requests"] >= r["today"]["requests"])
    ra = app.call("stats", {"range": "all"})
    check("全部范围：5 次请求（含 40 天前）", ra["summary"]["requests"] == 5, ra["summary"])
    rm = app.call("stats", {"range": "all", "model": "claude-haiku-4-5"})
    check("按模型筛选", rm["summary"]["requests"] == 1 and rm["summary"]["input"] == 2000, rm["summary"])
    racc = app.call("stats", {"range": "all", "account": "主力号"})
    check("按账号筛选", racc["summary"]["requests"] == 4, racc["summary"])
    rt = app.call("stats", {"range": "today"})
    check("今天按小时趋势", len(rt["trend"]) == datetime.now().hour + 1 and rt["trend"][0]["name"] == "00:00", len(rt["trend"]))
    # 定价
    pr = app.call("pricing")
    check("默认定价 15 个模型", len(pr) == 15 and pr[0]["Key"] == "claude-fable-5-1", len(pr))
    pr2 = app.call("pricing/set", {"list": [{"Key": "claude-opus-4-5", "Input": 1, "Output": 1, "CacheRead": 0, "CacheWrite5m": 0, "CacheWrite1h": 0}, {"Key": " ", "Input": 1}]})
    check("修改定价（空名称忽略）", len(pr2) == 1 and pr2[0]["Input"] == 1, pr2)
    r2 = app.call("stats", {"range": "all", "model": "claude-haiku-4-5"})
    check("没有定价的模型不计费", r2["summary"]["cost"] == 0 and r2["logs"][0]["priced"] is False, r2["summary"])
    pr3 = app.call("pricing/reset")
    check("恢复默认定价", len(pr3) == 15)


@group("终端 Claude Code 账号（增删改、共享链接、启动脚本、/usage）")
def t_cli(app, info):
    home, main = info["home"], info["main"]
    proj_before = tree_hash(os.path.join(main, "projects"))
    cs = app.call("cli/status")
    check("检测到 Claude Code 与版本", cs["installed"] and cs["version"] == "2.0.99 (Claude Code)", cs)
    st = app.call("cli/add", {"remark": "测试终端A"})
    acc = next((c for c in st["cli"] if c["id"] == "acc1"), None)
    adir = os.path.join(home, ".claude-accounts", "acc1")
    check("新增终端账号 acc1", acc and acc["remark"] == "测试终端A" and not acc["loggedIn"], acc)
    links = {d: os.path.islink(os.path.join(adir, d)) and os.path.realpath(os.path.join(adir, d)) == os.path.realpath(os.path.join(main, d)) for d in ["projects", "file-history", "todos", "plans", "skills"]}
    check("共享目录用符号链接指向 ~/.claude", all(links.values()), links)
    check("复制 settings.json", os.path.exists(os.path.join(adir, "settings.json")))
    st = app.call("cli/rename", {"id": "acc1", "remark": "终端甲"})
    check("改备注", next(c for c in st["cli"] if c["id"] == "acc1")["remark"] == "终端甲")
    expect_error(app, "cli/rename", {"id": "default", "remark": "x"}, "原始账号不能改备注")
    st = app.call("cli/launch", {"id": "acc1", "dir": info["home"] + "/proj", "mode": "resume"})
    sc = open(st["script"], encoding="utf-8").read()
    check("启动脚本：独立配置目录、项目目录、--resume", ("export CLAUDE_CONFIG_DIR='%s'" % adir) in sc and ("cd '%s/proj'" % home) in sc and "--resume" in sc and sc.startswith("#!/bin/bash"), sc)
    check("启动脚本可执行", os.access(st["script"], os.X_OK))
    check("记住最近的项目目录", st["recent"][0] == home + "/proj", st["recent"])
    st = app.call("cli/launch", {"id": "default", "dir": info["home"] + "/proj", "mode": "new"})
    sc = open(st["script"], encoding="utf-8").read()
    check("原始账号：不设置 CLAUDE_CONFIG_DIR", "unset CLAUDE_CONFIG_DIR" in sc and "--resume" not in sc, sc)
    expect_error(app, "cli/launch", {"id": "acc1", "dir": "/no/such/dir", "mode": "new"}, "请先选择一个存在的项目文件夹")
    u = app.call("cli/usage", {"id": "default"})
    check("原始账号 /usage", u["session"]["used"] == 12 and u["week"]["used"] == 40 and u["session"]["resets"] == "3pm", u)
    expect_error(app, "cli/usage", {"id": "acc1"}, "还没有登录")
    open(os.path.join(adir, ".credentials.json"), "w").write("{}")
    u = app.call("cli/usage", {"id": "acc1"})
    check("acc1 /usage 用自己的配置目录", u["session"]["used"] == 77, u)
    # 修复：把 todos 链接换成真实目录，修复后文件并入共享目录
    os.remove(os.path.join(adir, "todos"))
    os.makedirs(os.path.join(adir, "todos"))
    open(os.path.join(adir, "todos", "t1.json"), "w").write("[]")
    app.call("cli/repair")
    check("修复后重新链接、文件并入共享目录", os.path.islink(os.path.join(adir, "todos")) and os.path.exists(os.path.join(main, "todos", "t1.json")))
    st = app.call("cli/delete", {"id": "acc1"})
    check("删除终端账号", not os.path.exists(adir) and len(st["cli"]) == 1)
    check("删除后共享对话记录完好", tree_hash(os.path.join(main, "projects")) == proj_before)
    expect_error(app, "cli/delete", {"id": "default"}, "原始账号不能删除")
    inst = app.call("cli/install")
    check("一键安装脚本", "curl -fsSL https://claude.ai/install.sh | bash" in open(inst, encoding="utf-8").read())


@group("代理池：导入、保存、绑定、分配、检测（本地代理 + 用户名密码）")
def t_proxy(app, info):
    _, p_ok, s_ok = F.proxy_server()
    _, p_auth, s_auth = F.proxy_server("user1", "p@ss:word")
    dead = F.dead_port()
    text = "\n".join(["# 注释", "127.0.0.1:%d" % p_ok, "http://127.0.0.1:%d" % p_ok, "http://user1:p%%40ss%%3Aword@127.0.0.1:%d" % p_auth,
                      "http://user1:wrong@127.0.0.1:%d" % p_auth, "127.0.0.1:%d" % dead, "ftp://1.2.3.4:21", "not a proxy"])
    r = app.call("proxy/import", {"text": text})
    check("导入：4 个有效（重复的跳过）、2 行格式不对", r["added"] == 4 and len(r["bad"]) == 2, (r["added"], r["bad"]))
    items = r["cfg"]["items"]
    urls = [i["url"] for i in items]
    check("地址规范化", "http://127.0.0.1:%d" % p_ok in urls and "http://user1:p%%40ss%%3Aword@127.0.0.1:%d" % p_auth in urls, urls)
    t = app.call("proxy/test", {"id": "*"}, timeout=90)
    st = {i["url"]: i for i in t["items"]}
    ok = st["http://127.0.0.1:%d" % p_ok]
    check("可用代理检测通过（经本地代理连到 api.anthropic.com）", ok["status"] == "ok" and ok["latency"] > 0 and s_ok["connects"] >= 1, (ok, s_ok))
    au = st["http://user1:p%%40ss%%3Aword@127.0.0.1:%d" % p_auth]
    check("带用户名密码的代理检测通过", au["status"] == "ok", (au, s_auth))
    bad = st["http://user1:wrong@127.0.0.1:%d" % p_auth]
    check("密码错误：不可用并提示用户名密码", bad["status"] == "fail" and "用户名密码" in bad["error"], bad)
    dd = st["http://127.0.0.1:%d" % dead]
    check("连不上的代理：不可用", dd["status"] == "fail" and dd["error"], dd)
    cfg = t
    cfg["enabled"] = True
    cfg["mode"] = "roundrobin"
    for i in cfg["items"]:
        if i["url"] == "http://127.0.0.1:%d" % p_ok:
            i["bind"] = "d:p2"
    saved = app.call("proxy/save", {"cfg": json.dumps(cfg)})
    check("保存代理池设置", saved["enabled"] and saved["mode"] == "roundrobin" and len(saved["items"]) == 4)
    got = app.call("test/proxyFor", {"key": "d:p2", "kind": "quota"})
    check("绑定的账号用绑定的代理", got == "http://127.0.0.1:%d" % p_ok, got)
    free = app.call("test/proxyFor", {"key": "c:default", "kind": "cli"})
    check("没绑定的账号从可用的空闲代理里分配（跳过检测失败的）", free == "http://user1:p%%40ss%%3Aword@127.0.0.1:%d" % p_auth, free)
    again = app.call("test/proxyFor", {"key": "c:default", "kind": "cli"})
    check("轮换周期内保持同一个代理", again == free)
    saved["applyQuota"] = False
    saved = app.call("proxy/save", {"cfg": json.dumps(saved)})
    check("关闭「查询官方额度」后不用代理", app.call("test/proxyFor", {"key": "d:p2", "kind": "quota"}) is None)
    saved["enabled"] = False
    saved["globalEnabled"] = True
    saved["globalUrl"] = "127.0.0.1:%d" % p_ok
    saved = app.call("proxy/save", {"cfg": json.dumps(saved)})
    check("全局上游代理（规范化）", saved["globalUrl"] == "http://127.0.0.1:%d" % p_ok and app.call("test/proxyFor", {"key": "c:x", "kind": "cli"}) == saved["globalUrl"], saved["globalUrl"])
    saved["globalUrl"] = ""
    expect_error(app, "proxy/save", {"cfg": json.dumps(saved)}, "开启全局上游代理前请先填写代理地址")
    tu = app.call("proxy/testUrl", {"url": "127.0.0.1:%d" % dead})
    check("单独检测地址", tu["status"] == "fail", tu)
    expect_error(app, "proxy/testUrl", {"url": "socks5://1.2.3.4:1080"}, "只支持 HTTP 代理")
    g = app.call("proxy/get")
    check("重新读取设置一致", g["globalEnabled"] and not g["enabled"] and g["applyQuota"] is False)
    check("代理设置写入文件", os.path.exists(os.path.join(info["state"], "proxies.json")))
    # 还原
    g["globalEnabled"] = False; g["applyQuota"] = True
    app.call("proxy/save", {"cfg": json.dumps(g)})


@group("缓存扫描与清理（不碰登录与会话）")
def t_cache(app, info):
    act, prof = info["active"], info["profiles"]
    keep = {p: sha(p) for p in [os.path.join(act, "config.json"), os.path.join(prof, "p2", "config.json")]}
    r = app.call("cache/scan")
    ids = {i["id"]: i for i in r["items"]}
    need = ["desk|p1|Cache", "desk|p1|Code Cache", "desk|p1|GPUCache", "desk|p2|Cache", "desk|p2|Crashpad", "sys|caches", "sys|logs", "code|0|cache", "code|0|statsig"]
    check("扫描到各类缓存", all(n in ids for n in need), sorted(ids))
    check("当前账号的缓存标记需要关闭 App", ids["desk|p1|Cache"]["needsClose"] and not ids["desk|p2|Cache"]["needsClose"])
    check("合计大小", r["total"] == sum(i["size"] for i in r["items"]) and r["total"] > 100000, r["total"])
    c = app.call("cache/clean", {"ids": ["desk|p2|Cache", "code|0|cache", "sys|logs", "not-in-list"]})
    check("清理 3 项（不在清单里的忽略）", c["message"].startswith("清理了 3 项缓存"), c["message"])
    check("缓存文件已删除、目录保留", not os.listdir(os.path.join(prof, "p2", "Cache")) and os.path.isdir(os.path.join(prof, "p2", "Cache")) and not os.listdir(os.path.join(info["main"], "cache")))
    check("清理后不再列出", "desk|p2|Cache" not in {i["id"] for i in c["items"]})
    expect_error(app, "cache/clean", {"ids": ["xxx"]}, "没有选中可清理的缓存")
    s = app.call("storage/scan")
    check("存储扫描：3 个账号 + 3 个目录", len(s["items"]) == 6 and s["items"][0]["kind"] == "desktop", [i["name"] for i in s["items"]])
    check("可清理 = 非当前账号的缓存", s["cleanable"] == sum(i["cache"] for i in s["items"] if not i["active"]))
    s2 = app.call("storage/clean", {"id": "p2"})
    check("清理存档账号缓存目录", not os.path.exists(os.path.join(prof, "p2", "Crashpad")))
    expect_error(app, "storage/clean", {"id": "p1"}, "当前使用中的账号不能清理缓存")
    check("登录信息未改动", all(sha(p) == h for p, h in keep.items()))


@group("设置、开机自启（LaunchAgent）、日志、简繁对照")
def t_settings(app, info):
    s = app.call("settings/set", {"key": "trayMode", "value": False})
    check("保存设置", s["trayMode"] is False)
    s = app.call("settings/set", {"key": "weeklyResets", "value": json.dumps({"p1": "5|07:00"})})
    check("保存字符串设置", json.loads(s["weeklyResets"])["p1"] == "5|07:00")
    expect_error(app, "settings/set", {"key": "noSuchKey", "value": 1}, "未知设置")
    agent = os.path.join(info["home"], "Library", "LaunchAgents", "io.github.y2014.claudetools.plist")
    s = app.call("settings/set", {"key": "autoStart", "value": True})
    ok = os.path.exists(agent) and s["autoStart"] is True
    pl = plistlib.load(open(agent, "rb")) if os.path.exists(agent) else {}
    check("开启登录时自动启动：写入 LaunchAgent（带 --tray）", ok and pl.get("ProgramArguments") == [EXE, "--tray"] and pl.get("RunAtLoad") is True, pl)
    s = app.call("settings/set", {"key": "autoStart", "value": False})
    check("关闭登录时自动启动", not os.path.exists(agent) and s["autoStart"] is False)
    app.call("settings/set", {"key": "trayMode", "value": True})
    expect_error(app, "logs/add", {"action": "随便写"}, "不支持的日志类型")
    app.call("logs/add", {"action": "查询官方额度", "target": "主力号", "ok": "1", "detail": "测试", "ms": "12"})
    logs = app.call("logs")
    check("界面写入的日志", logs[0]["action"] == "查询官方额度" and logs[0]["ms"] == 12 and logs[0]["ok"] is True, logs[0])
    acts = {l["action"] for l in logs}
    check("各类操作都有日志", {"切换桌面账号", "添加桌面账号", "删除桌面账号", "改备注", "合并会话记录", "添加终端账号", "删除终端账号", "清理缓存", "代理设置", "开机自启"} <= acts, acts)
    m = app.call("i18n/s2t")
    check("简繁对照表", m.get("国") == "國" and m.get("这") == "這" and len(m) > 2000, (len(m), m.get("国")))
    app.call("logs/clear")
    check("清空日志", app.call("logs") == [])


@group("令牌和对话记录不丢（全部操作之后）")
def t_integrity(app, info):
    act, prof = info["active"], info["profiles"]
    check("p1 令牌完好", token_of(act) == info["tokA0"])
    check("p2 令牌完好", token_of(os.path.join(prof, "p2")) == info["tokB0"])
    check("~/.claude/projects 对话日志完好", tree_hash(os.path.join(info["main"], "projects")) == info["projects0"])
    left = sorted(os.listdir(prof))
    check("存档目录只剩 p2 p3", left == ["p2", "p3"], left)
    st = app.call("state")
    check("最终仍是 3 个账号、当前 p1", len(st["desktop"]) == 3 and st["desktop"][0]["id"] == "p1" and st["desktop"][0]["active"])
    r = app.call("desktop/usage", {"id": "p1"})
    check("仍能返回额度数据", r["session"] and r["session"]["used"] == 38, r)


def fake_claude(root):
    p = os.path.join(root, "bin", "claude")
    os.makedirs(os.path.dirname(p), exist_ok=True)
    open(p, "w").write("""#!/bin/bash
if [ "$1" = "--version" ]; then echo "2.0.99 (Claude Code)"; exit 0; fi
if [ "$1" = "-p" ]; then
  if [ -n "$CLAUDE_CONFIG_DIR" ]; then S=77; else S=12; fi
  echo "Current session: ${S}% used · resets 3pm (Asia/Shanghai)"
  echo "Current week (all models): 40% used · resets Nov 14, 9am (Asia/Shanghai)"
  exit 0
fi
exit 1
""")
    os.chmod(p, 0o755)
    return p


def main():
    root = tempfile.mkdtemp(prefix="ct-test-")
    print("测试目录：" + root)
    F.setup_keychain()
    info = F.build(root)
    _, usage_url = F.usage_server()
    info["tokA0"] = token_of(info["active"])
    info["tokB0"] = token_of(os.path.join(info["profiles"], "p2"))
    info["projects0"] = tree_hash(os.path.join(info["main"], "projects"))
    env = F.env_for(info, {"CLAUDE_SWITCHER_NOBROWSER": "1", "CLAUDE_SWITCHER_USAGE_URL": usage_url, "CLAUDE_SWITCHER_CLAUDE": fake_claude(root)})
    app = F.App(EXE, env, os.path.join(LOGDIR, "app.log"))
    try:
        t_security(app)
        for t in [t_state, t_sessions, t_live, t_switch, t_add_delete, t_merge, t_stats, t_cli, t_proxy, t_cache, t_settings, t_integrity]:
            t(app, info)
            if not app.alive():
                check("程序在「%s」之后仍在运行" % t.title, False, "进程已退出")
                break
        check("全部测试后程序仍在运行（没有崩溃）", app.alive())
    finally:
        app.stop()
    # x86_64（Intel）版本：用 Rosetta 跑一遍基本接口
    import subprocess
    if subprocess.run(["arch", "-x86_64", "/usr/bin/true"], capture_output=True).returncode == 0:
        print("\n== Intel（x86_64）版本冒烟测试")
        root2 = tempfile.mkdtemp(prefix="ct-x86-")
        info2 = F.build(root2)
        env2 = F.env_for(info2, {"CLAUDE_SWITCHER_NOBROWSER": "1"})
        log2 = open(os.path.join(LOGDIR, "app-x86.log"), "w")
        p = subprocess.Popen(["arch", "-x86_64", EXE], env=env2, stdout=log2, stderr=subprocess.STDOUT)
        try:
            uf = os.path.join(info2["state"], "url.txt")
            for _ in range(300):
                if os.path.exists(uf):
                    break
                time.sleep(0.1)
            a2 = F.App.__new__(F.App)
            a2.base, a2.token = open(uf).read().strip().split("?t=")
            st = a2.call("state")
            check("x86_64 版本：state 正常", len(st["desktop"]) == 3 and st["desktop"][0]["email"] == F.EMAIL_A)
            r = a2.call("stats", {"force": True})
            check("x86_64 版本：统计接口正常", "ready" in r)
        except Exception as e:
            check("x86_64 版本冒烟测试", False, e)
        finally:
            p.terminate(); log2.close()
    else:
        print("\n（这台机器没有 Rosetta，跳过 x86_64 冒烟测试）")
    shutil.copytree(info["state"], os.path.join(LOGDIR, "state"), dirs_exist_ok=True)
    fails = [r for r in results if not r[1]]
    print("\n==== 结果：%d 项通过，%d 项失败 ====" % (len(results) - len(fails), len(fails)))
    for f in fails:
        print("FAIL: " + f[0] + "  →  " + str(f[2])[:400])
    json.dump([{"name": n, "ok": o, "detail": str(d)[:400]} for n, o, d in results], open(os.path.join(LOGDIR, "results.json"), "w"), ensure_ascii=False, indent=1)
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
