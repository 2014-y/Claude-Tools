# 自动测试用的假数据：临时目录里造 3 个桌面账号、终端账号、对话日志、缓存，
# 以及假的官方用量接口、本地 HTTP 代理（可带用户名密码）、测试用钥匙串。
import base64, hashlib, json, os, select, socket, socketserver, subprocess, threading, time, urllib.request
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

KEYCHAIN_SERVICE = "Claude Tools Test Safe Storage"
KEYCHAIN_PASS = "test-safe-storage-pass"
KEYCHAIN_FILE = os.path.expanduser("~/Library/Keychains/ct-test.keychain-db")

EMAIL_A = "alice.work@fastmail.com"
EMAIL_B = "bob.personal@proton.me"


def dumps(o):
    return json.dumps(o, ensure_ascii=False, separators=(",", ":"))


def ms(dt):
    return int(dt.timestamp() * 1000)


def iso(dt):
    return dt.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    mode = "wb" if isinstance(data, (bytes, bytearray)) else "w"
    with open(path, mode, **({} if mode == "wb" else {"encoding": "utf-8"})) as f:
        f.write(data)


# ---------------- 钥匙串与加密 ----------------
def setup_keychain():
    subprocess.run(["security", "delete-keychain", KEYCHAIN_FILE], capture_output=True)
    subprocess.run(["security", "create-keychain", "-p", "ctpw", KEYCHAIN_FILE], check=True)
    subprocess.run(["security", "set-keychain-settings", KEYCHAIN_FILE], check=True)
    subprocess.run(["security", "unlock-keychain", "-p", "ctpw", KEYCHAIN_FILE], check=True)
    cur = subprocess.run(["security", "list-keychains", "-d", "user"], capture_output=True, text=True).stdout.split()
    cur = [c.strip().strip('"') for c in cur if c.strip()]
    if KEYCHAIN_FILE not in cur:
        subprocess.run(["security", "list-keychains", "-d", "user", "-s", KEYCHAIN_FILE] + cur, check=True)
    subprocess.run(["security", "add-generic-password", "-U", "-s", KEYCHAIN_SERVICE, "-a", "Claude Key", "-w", KEYCHAIN_PASS, "-A", KEYCHAIN_FILE], check=True)


def encrypt_token_cache(cache):
    key = hashlib.pbkdf2_hmac("sha1", KEYCHAIN_PASS.encode(), b"saltysalt", 1003, 16)
    p = subprocess.run(["openssl", "enc", "-aes-128-cbc", "-K", key.hex(), "-iv", "20" * 16],
                       input=json.dumps(cache).encode(), capture_output=True, check=True)
    return base64.b64encode(b"v10" + p.stdout).decode()


# ---------------- 假的官方用量接口 ----------------
class UsageState:
    hits = {}
    lock = threading.Lock()


def usage_server():
    now = datetime.now(timezone.utc)

    class H(BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def do_GET(self):
            auth = self.headers.get("Authorization", "")
            tok = auth[7:] if auth.startswith("Bearer ") else ""
            with UsageState.lock:
                UsageState.hits[tok] = UsageState.hits.get(tok, 0) + 1
            if self.path != "/api/oauth/usage" or self.headers.get("anthropic-beta") != "oauth-2025-04-20":
                self.send_response(400); self.end_headers(); return
            if tok == "tok-A":
                body = {"five_hour": {"utilization": 37.5, "resets_at": (now + timedelta(hours=2, minutes=13)).isoformat()},
                        "seven_day": {"utilization": 62.0, "resets_at": (now + timedelta(days=3, hours=5)).isoformat()},
                        "seven_day_opus": None}
                data = json.dumps(body).encode()
                self.send_response(200); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(data))); self.end_headers()
                self.wfile.write(data); return
            if tok == "tok-B":
                self.send_response(429); self.send_header("retry-after", "30"); self.send_header("Content-Length", "0"); self.end_headers(); return
            self.send_response(401); self.send_header("Content-Length", "0"); self.end_headers()

    srv = ThreadingHTTPServer(("127.0.0.1", 0), H)
    srv.daemon_threads = True
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv, "http://127.0.0.1:%d/api/oauth/usage" % srv.server_address[1]


# ---------------- 本地 HTTP 代理（CONNECT 隧道，可要求用户名密码） ----------------
def proxy_server(user=None, password=None):
    stats = {"connects": 0, "denied": 0}

    class H(socketserver.BaseRequestHandler):
        def handle(self):
            s = self.request
            s.settimeout(20)
            buf = b""
            while b"\r\n\r\n" not in buf:
                d = s.recv(4096)
                if not d:
                    return
                buf += d
            head = buf.split(b"\r\n\r\n", 1)[0].decode("latin1")
            lines = head.split("\r\n")
            parts = lines[0].split(" ")
            if len(parts) < 2 or parts[0] != "CONNECT":
                s.sendall(b"HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); return
            if user is not None:
                want = "Basic " + base64.b64encode(("%s:%s" % (user, password)).encode()).decode()
                got = ""
                for l in lines[1:]:
                    if l.lower().startswith("proxy-authorization:"):
                        got = l.split(":", 1)[1].strip()
                if got != want:
                    stats["denied"] += 1
                    s.sendall(b"HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\nConnection: close\r\nProxy-Connection: close\r\n\r\n")
                    return
            host, port = parts[1].rsplit(":", 1)
            try:
                up = socket.create_connection((host, int(port)), timeout=15)
            except Exception:
                s.sendall(b"HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); return
            stats["connects"] += 1
            s.sendall(b"HTTP/1.1 200 Connection established\r\n\r\n")
            socks = [s, up]
            try:
                while True:
                    r, _, _ = select.select(socks, [], [], 20)
                    if not r:
                        break
                    for x in r:
                        d = x.recv(65536)
                        if not d:
                            return
                        (up if x is s else s).sendall(d)
            finally:
                up.close()

    class S(socketserver.ThreadingTCPServer):
        daemon_threads = True
        allow_reuse_address = True

    srv = S(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv, srv.server_address[1], stats


def dead_port():
    s = socket.socket(); s.bind(("127.0.0.1", 0)); p = s.getsockname()[1]; s.close()
    return p


# ---------------- 假数据 ----------------
def session_json(sid, cli, title, cwd, created, last, model="claude-opus-4-5"):
    return dumps({"sessionId": "local_" + sid, "cliSessionId": cli, "title": title, "model": model, "cwd": cwd,
                       "originCwd": cwd, "createdAt": created, "lastActivityAt": last, "isArchived": False,
                       "remoteMcpServersConfig": {"x": {"cwd": "/should/not/be/read"}}})


def plan_history(org, pairs, now):
    # pairs: [(分钟前, 5H%, 7天%)]
    return json.dumps({"samples": [{"t": ms(now - timedelta(minutes=m)), "org": org, "u": {"fh": fh, "sd": sd}} for m, fh, sd in pairs]})


def build(root):
    now = datetime.now().astimezone()
    home = os.path.join(root, "home")
    appdata = os.path.join(home, "Library", "Application Support")
    state = os.path.join(root, "state")
    active = os.path.join(appdata, "Claude")
    profiles = os.path.join(appdata, "Claude-profiles")
    main = os.path.join(home, ".claude")
    info = {"root": root, "home": home, "appdata": appdata, "state": state, "active": active, "profiles": profiles, "main": main}
    future = ms(now + timedelta(hours=6))

    # ---- 账号 A（当前，没有标记文件：启动时自动登记为 p1，备注 = 邮箱）----
    tokA = encrypt_token_cache({"claude-desktop:user:profile user:inference": {"token": "tok-A", "refreshToken": "r-A", "expiresAt": future},
                                "claude-desktop:user:inference": {"token": "tok-A-inf", "expiresAt": future + 999}})
    write(os.path.join(active, "config.json"), json.dumps({"oauth:tokenCacheV2": tokA, "lastKnownAccountUuid": "acc-A",
                                                          "userThemeMode": "dark", "windowControlsZoomFactor": 1.2, "locale": "zh-CN"}))
    write(os.path.join(active, "claude_desktop_config.json"), json.dumps({"mcpServers": {"demo": {"command": "echo"}}}))
    write(os.path.join(active, "window-state.json"), json.dumps({"width": 1234, "height": 777}))
    write(os.path.join(active, "Local Storage", "leveldb", "000003.log"),
          b"\x00\x01junk\"email\":\"" + EMAIL_A.encode() + b"\" x@sentry.io noreply@anthropic.com " + EMAIL_A.encode())
    sA = os.path.join(active, "claude-code-sessions", "acc-A", "org-A")
    ws = os.path.join(active, "scratch-workspaces", "ws1")
    write(os.path.join(ws, "notes.txt"), "scratch file")
    write(os.path.join(sA, "local_s1.json"), session_json("s1", "cli-1", "修复登录页面", "/Users/test/proj", ms(now - timedelta(days=2)), ms(now - timedelta(minutes=30))))
    write(os.path.join(sA, "local_s2.json"), session_json("s2", "cli-2", "临时会话测试", ws, ms(now - timedelta(days=1)), ms(now - timedelta(hours=3))))
    write(os.path.join(sA, "deleted_s9"), str(ms(now)))
    write(os.path.join(active, "plan-usage-history.json"), plan_history("org-A", [(300, 0, 40), (120, 10, 41), (30, 22, 44), (5, 25, 45)], now))
    for c in ["Cache/Cache_Data/f_000001", "Code Cache/js/abc", "GPUCache/data_0"]:
        write(os.path.join(active, c), os.urandom(20000))

    # ---- 账号 B（存档 p2）----
    pB = os.path.join(profiles, "p2")
    write(os.path.join(pB, ".cs-profile"), "p2\n工作号\n")
    tokB = encrypt_token_cache({"claude-desktop:user:profile": {"token": "tok-B", "expiresAt": future}})
    write(os.path.join(pB, "config.json"), json.dumps({"oauth:tokenCacheV2": tokB, "lastKnownAccountUuid": "acc-B", "userThemeMode": "light"}))
    write(os.path.join(pB, "IndexedDB", "https_claude.ai_0.indexeddb.leveldb", "000005.ldb"),
          b"\x07\x00" + EMAIL_B.encode("utf-16-le") + b"\x01\x02" + EMAIL_B.encode("utf-16-le") + b" other@proton.me ")
    sB = os.path.join(pB, "claude-code-sessions", "acc-B", "org-B")
    write(os.path.join(sB, "local_s3.json"), session_json("s3", "cli-3", "写周报", "/Users/test/docs", ms(now - timedelta(days=4)), ms(now - timedelta(days=1))))
    write(os.path.join(sB, "local_s9.json"), session_json("s9", "cli-9", "已在 A 删除的会话", "/Users/test/old", ms(now - timedelta(days=9)), ms(now - timedelta(days=8))))
    write(os.path.join(pB, "plan-usage-history.json"), plan_history("org-B", [(200, 50, 70), (10, 60, 80)], now))
    for c in ["Cache/Cache_Data/f_000009", "Crashpad/pending/r.dmp"]:
        write(os.path.join(pB, c), os.urandom(30000))

    # ---- 账号 C（存档 p3，从没登录过）----
    pC = os.path.join(profiles, "p3")
    write(os.path.join(pC, ".cs-profile"), "p3\n备用号\n")
    write(os.path.join(pC, "Preferences"), "{}")

    # ---- 系统缓存 ----
    write(os.path.join(home, "Library", "Caches", "com.anthropic.claudefordesktop", "Cache.db"), os.urandom(15000))
    write(os.path.join(home, "Library", "Logs", "Claude", "main.log"), "log line\n" * 500)

    # ---- Claude Code（~/.claude）----
    write(os.path.join(home, ".claude.json"), json.dumps({"oauthAccount": {"emailAddress": "main.user@fastmail.com"}, "numStartups": 3}))
    write(os.path.join(main, "settings.json"), json.dumps({"theme": "dark"}))
    write(os.path.join(main, "cache", "changelog.md"), "x" * 5000)
    write(os.path.join(main, "statsig", "statsig.cached"), "y" * 3000)
    os.makedirs(os.path.join(main, "todos"), exist_ok=True)
    os.makedirs(os.path.join(home, "proj"), exist_ok=True)

    # 对话日志：用于 Token 统计（同一条消息拆成多行时取最后一次用量；<synthetic> 不计）
    t = now - timedelta(minutes=20)
    if t.date() != now.date():
        t = now.replace(hour=0, minute=1, second=0, microsecond=0)
    old3 = now - timedelta(days=3)
    old40 = now - timedelta(days=40)
    pdir = os.path.join(main, "projects", "-Users-test-proj")
    lines = [
        {"type": "user", "timestamp": iso(t - timedelta(seconds=30)), "cwd": "/Users/test/proj", "message": {"role": "user", "content": "hi"}},
        {"type": "assistant", "timestamp": iso(t), "cwd": "/Users/test/proj",
         "message": {"id": "msg_1", "model": "claude-opus-4-5-20251101", "usage": {"input_tokens": 100, "cache_creation_input_tokens": 1000, "cache_read_input_tokens": 5000, "output_tokens": 10}}},
        {"type": "assistant", "timestamp": iso(t + timedelta(seconds=12)),
         "message": {"id": "msg_1", "model": "claude-opus-4-5-20251101", "usage": {"input_tokens": 100, "cache_creation_input_tokens": 1000, "cache_read_input_tokens": 5000, "output_tokens": 300}}},
        {"type": "user", "timestamp": iso(t + timedelta(seconds=40)), "message": {"role": "user", "content": "more"}},
        {"type": "assistant", "timestamp": iso(t + timedelta(seconds=50)),
         "message": {"id": "msg_2", "model": "claude-sonnet-4-5", "usage": {"input_tokens": 50, "cache_creation_input_tokens": 1000, "cache_read_input_tokens": 0, "output_tokens": 120,
                                                                         "cache_creation": {"ephemeral_5m_input_tokens": 200, "ephemeral_1h_input_tokens": 800}}}},
        {"type": "assistant", "timestamp": iso(t + timedelta(seconds=55)),
         "message": {"id": "msg_x", "model": "<synthetic>", "usage": {"input_tokens": 0, "output_tokens": 0}}},
        {"type": "assistant", "timestamp": iso(old3),
         "message": {"id": "msg_3", "model": "claude-haiku-4-5", "usage": {"input_tokens": 2000, "cache_read_input_tokens": 100, "output_tokens": 400}}},
        {"type": "assistant", "timestamp": iso(old40),
         "message": {"id": "msg_4", "model": "claude-opus-4-5-20251101", "usage": {"input_tokens": 7, "output_tokens": 7}}},
    ]
    write(os.path.join(pdir, "cli-1.jsonl"), "\n".join(dumps(l) for l in lines) + "\n")
    sub = [{"type": "assistant", "timestamp": iso(t + timedelta(seconds=70)), "cwd": "/Users/test/other",
            "message": {"id": "msg_5", "model": "claude-sonnet-4-5", "usage": {"input_tokens": 10, "output_tokens": 20, "cache_read_input_tokens": 30}}}]
    write(os.path.join(pdir, "cli-9x", "subagents", "agent-a.jsonl"), "\n".join(dumps(l) for l in sub) + "\n")
    info["records"] = {"t": t, "old3": old3, "old40": old40}
    return info


def env_for(info, extra=None):
    env = dict(os.environ)
    for k in list(env):
        if k.startswith("CLAUDE_SWITCHER_"):
            del env[k]
    env.update({"CLAUDE_SWITCHER_TEST": "1", "CLAUDE_SWITCHER_NO_AUTOMERGE": "1", "CLAUDE_SWITCHER_HOME": info["home"], "CLAUDE_SWITCHER_APPDATA": info["appdata"],
                "CLAUDE_SWITCHER_STATE": info["state"], "CLAUDE_SWITCHER_KEYCHAIN_SERVICE": KEYCHAIN_SERVICE})
    if extra:
        env.update(extra)
    return env


class App:
    def __init__(self, exe, env, log_path):
        self.log = open(log_path, "w")
        self.proc = subprocess.Popen([exe], env=env, stdout=self.log, stderr=subprocess.STDOUT)
        url_file = os.path.join(env["CLAUDE_SWITCHER_STATE"], "url.txt")
        for _ in range(200):
            if os.path.exists(url_file):
                break
            if self.proc.poll() is not None:
                raise RuntimeError("程序启动后退出了，退出码 %s" % self.proc.returncode)
            time.sleep(0.1)
        url = open(url_file).read().strip()
        self.base, self.token = url.split("?t=")
        self.url = url

    def raw(self, method, path, body=None, token=True, timeout=120):
        req = urllib.request.Request(self.base + path.lstrip("/"), data=body, method=method)
        if token:
            req.add_header("X-Token", self.token)
        req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.status, r.read()
        except urllib.error.HTTPError as e:
            return e.code, e.read()

    def call(self, name, body=None, timeout=120):
        st, data = self.raw("POST", "api/" + name, json.dumps(body or {}).encode(), timeout=timeout)
        if st != 200:
            raise RuntimeError("HTTP %d: %s" % (st, data[:200]))
        j = json.loads(data)
        if not j.get("ok"):
            raise ApiError(j.get("error"))
        return j.get("data")

    def alive(self):
        return self.proc.poll() is None

    def stop(self):
        try:
            self.proc.terminate(); self.proc.wait(5)
        except Exception:
            self.proc.kill()
        self.log.close()


class ApiError(Exception):
    pass
