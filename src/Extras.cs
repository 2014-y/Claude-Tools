// 设置、操作日志、用量配额、会话记录、Token 统计
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace ClaudeSwitcher
{
    // ---------------- 设置 ----------------
    static class Settings
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static readonly object Lock = new object();
        public static string StateDir { get { return Path.Combine(DesktopStore.LocalAppData, "ClaudeSwitcher"); } }
        static string FilePath { get { return Path.Combine(StateDir, "settings.json"); } }
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "Claude Tools";
        const string OldRunName = "ClaudeSwitcher";

        static Dictionary<string, object> data;

        static readonly Dictionary<string, object> Defaults = new Dictionary<string, object> {
            { "trayMode", false },         // 关闭窗口后在托盘后台运行（默认关闭）
            { "confirmSwitch", true },     // 切换前确认
            { "autoOpenAfterSwitch", true },
            { "maskDefault", true },
            { "notify", true },            // 托盘气泡通知
            { "syncSessions", true },      // 切换时把上一个账号的会话记录带到新账号
            { "syncClientSettings", true }, // 切换时把客户端设置（字号、主题、窗口布局等）带到新账号
            { "userName", "" },
            { "weeklyResets", "" }        // 手动设置的每周重置时间，JSON：{ 账号id: "5|07:00" }
        };

        static Dictionary<string, object> Data
        {
            get
            {
                if (data != null) return data;
                data = new Dictionary<string, object>(Defaults);
                try
                {
                    if (File.Exists(FilePath))
                        foreach (var kv in Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(FilePath, Encoding.UTF8)))
                            data[kv.Key] = kv.Value;
                }
                catch { }
                return data;
            }
        }

        public static bool Bool(string k) { lock (Lock) { object v; return Data.TryGetValue(k, out v) && v is bool && (bool)v; } }
        public static string Str(string k) { lock (Lock) { object v; return Data.TryGetValue(k, out v) && v != null ? v.ToString() : ""; } }

        public static void Set(string k, object v)
        {
            lock (Lock)
            {
                if (k == "autoStart") { SetAutoStart(v is bool && (bool)v); return; }
                if (!Defaults.ContainsKey(k)) throw new Exception("未知设置：" + k);
                Data[k] = v;
                Directory.CreateDirectory(StateDir);
                File.WriteAllText(FilePath, Json.Serialize(Data), Encoding.UTF8);
            }
        }

        public static Dictionary<string, object> All()
        {
            lock (Lock)
            {
                var d = new Dictionary<string, object>(Data);
                d["autoStart"] = AutoStart;
                d["stateDir"] = StateDir;
                d["profilesDir"] = DesktopStore.ProfilesDir;
                d["cliDir"] = Store.AccountsDir;
                d["exe"] = System.Windows.Forms.Application.ExecutablePath;
                d["version"] = "2.0.0";
                return d;
            }
        }

        public static bool AutoStart
        {
            get
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && (k.GetValue(RunName) != null || k.GetValue(OldRunName) != null); }
                catch { return false; }
            }
        }

        // 已开启开机启动时，按当前名称和 exe 路径重新登记（程序改名或挪位置后仍然有效）
        public static void RefreshAutoStart()
        {
            try { if (AutoStart) SetAutoStart(true); } catch { }
        }

        static void SetAutoStart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (k.GetValue(OldRunName) != null) k.DeleteValue(OldRunName);   // 清理旧名称
                if (on) k.SetValue(RunName, "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --tray");
                else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
            }
        }
    }

    // ---------------- 操作日志 ----------------
    static class OpLog
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static readonly object Lock = new object();
        static string FilePath { get { return Path.Combine(Settings.StateDir, "log.jsonl"); } }

        public static void Add(string action, string target, bool ok, string detail, long ms)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Settings.StateDir);
                    var e = new Dictionary<string, object> {
                        { "t", DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") }, { "action", action }, { "target", target ?? "" },
                        { "ok", ok }, { "detail", detail ?? "" }, { "ms", ms }
                    };
                    File.AppendAllText(FilePath, Json.Serialize(e) + "\n", new UTF8Encoding(false));
                }
            }
            catch { }
        }

        public static List<object> Read(int max)
        {
            lock (Lock)
            {
                var r = new List<object>();
                if (!File.Exists(FilePath)) return r;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8).Reverse().Take(max))
                    try { r.Add(Json.Deserialize<Dictionary<string, object>>(line)); } catch { }
                return r;
            }
        }

        public static void Clear() { lock (Lock) { try { File.Delete(FilePath); } catch { } } }

        public static int CountToday(string action)
        {
            var today = DateTime.Now.ToString("yyyy/MM/dd");
            return Read(2000).Cast<Dictionary<string, object>>()
                .Count(e => e["t"].ToString().StartsWith(today) && e["action"].ToString() == action && (bool)e["ok"]);
        }
    }

    // ---------------- 用量配额（桌面 App 记录的 5 小时 / 7 天用量百分比） ----------------
    class Quota
    {
        public string Org;
        public int FiveHour = -1, SevenDay = -1;
        public long T;
        public long FiveReset, SevenReset; // 推算的重置时间（毫秒时间戳，0 表示无法推算）
        public List<int[]> Recent = new List<int[]>(); // 最近 7 天的 7天用量走势（按小时取样）
    }

    static class QuotaReader
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static readonly Dictionary<string, Tuple<DateTime, Quota>> Cache = new Dictionary<string, Tuple<DateTime, Quota>>();

        public static Quota Read(string profileDir)
        {
            var p = Path.Combine(profileDir, "plan-usage-history.json");
            try
            {
                if (!File.Exists(p)) return null;
                var mt = File.GetLastWriteTimeUtc(p);
                lock (Cache)
                {
                    Tuple<DateTime, Quota> c;
                    if (Cache.TryGetValue(p, out c) && c.Item1 == mt) return c.Item2;
                }
                string text;
                using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Encoding.UTF8)) text = r.ReadToEnd();
                var root = Json.Deserialize<Dictionary<string, object>>(text);
                var samples = ((System.Collections.ArrayList)root["samples"]).Cast<Dictionary<string, object>>()
                    .Select(s => new { t = Convert.ToInt64(s["t"]), org = s.ContainsKey("org") ? (s["org"] ?? "").ToString() : "", u = (Dictionary<string, object>)s["u"] })
                    .OrderBy(s => s.t).ToList();
                if (samples.Count == 0) return null;
                var last = samples.Last();
                var q = new Quota { Org = last.org, T = last.t };
                if (last.u.ContainsKey("fh") && last.u["fh"] != null) q.FiveHour = Convert.ToInt32(last.u["fh"]);
                if (last.u.ContainsKey("sd") && last.u["sd"] != null) q.SevenDay = Convert.ToInt32(last.u["sd"]);
                // 推算重置时间：找到本轮窗口里用量从 0 / 回落后开始上涨的那次记录，加上窗口时长
                var mine = samples.Where(s => s.org == last.org).ToList();
                Func<string, long, long> est = (key, window) =>
                {
                    Func<int, int> v = i => mine[i].u.ContainsKey(key) && mine[i].u[key] != null ? Convert.ToInt32(mine[i].u[key]) : 0;
                    int n = mine.Count; if (n == 0 || v(n - 1) <= 0) return 0;
                    int k = n - 1;
                    while (k > 0 && v(k - 1) > 0 && v(k - 1) <= v(k) + 2 && mine[k].t - mine[k - 1].t < window) k--;
                    // 窗口开始时间：取上一条 0% 记录（若在 30 分钟内），向下取整到半点
                    long start = (k > 0 && v(k - 1) == 0 && mine[k].t - mine[k - 1].t <= 30 * 60000L) ? mine[k - 1].t : mine[k].t;
                    start -= start % (30 * 60000L);
                    return start + window;
                };
                q.FiveReset = est("fh", 5L * 3600 * 1000);
                // 每周额度是固定时间重置：只有记录到清零的那一刻才能确定，记不到就交给界面让用户设置
                q.SevenReset = 0;
                for (int i = mine.Count - 1; i > 0; i--)
                {
                    int a0 = mine[i - 1].u.ContainsKey("sd") && mine[i - 1].u["sd"] != null ? Convert.ToInt32(mine[i - 1].u["sd"]) : 0;
                    int b0 = mine[i].u.ContainsKey("sd") && mine[i].u["sd"] != null ? Convert.ToInt32(mine[i].u["sd"]) : 0;
                    if (a0 - b0 >= 5 && b0 <= 2)
                    {
                        long anchor = mine[i].t - mine[i].t % (3600000L), week = 7L * 24 * 3600000, nowMs = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                        while (anchor <= nowMs) anchor += week;
                        q.SevenReset = anchor; break;
                    }
                }
                long from = last.t - 7L * 24 * 3600 * 1000;
                long bucket = -1;
                foreach (var s in samples.Where(s => s.org == last.org && s.t >= from))
                {
                    long b = s.t / (3600L * 1000 * 3);
                    if (b == bucket) continue;
                    bucket = b;
                    int fh = s.u.ContainsKey("fh") && s.u["fh"] != null ? Convert.ToInt32(s.u["fh"]) : 0;
                    int sd = s.u.ContainsKey("sd") && s.u["sd"] != null ? Convert.ToInt32(s.u["sd"]) : 0;
                    q.Recent.Add(new[] { (int)((s.t - from) / 60000), fh, sd });
                }
                lock (Cache) Cache[p] = Tuple.Create(mt, q);
                return q;
            }
            catch { return null; }
        }
    }

    // ---------------- 会话记录（桌面 App 的 Claude Code 会话） ----------------
    class SessionInfo
    {
        public string Id, CliId, Title, Model, Cwd, Org, Account, Profile;
        public long Created, LastActivity;
        public bool Archived;
    }

    static class Sessions
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static readonly Regex StrField = new Regex("\"(sessionId|cliSessionId|title|model|cwd)\":\"((?:[^\"\\\\]|\\\\.)*)\"");
        static readonly Regex NumField = new Regex("\"(createdAt|lastActivityAt)\":(\\d+)");
        static readonly Regex ArchField = new Regex("\"isArchived\":(true|false)");
        static readonly Dictionary<string, Tuple<DateTime, SessionInfo>> Cache = new Dictionary<string, Tuple<DateTime, SessionInfo>>();

        static string Unescape(string s)
        {
            try { return Json.Deserialize<string>("\"" + s + "\""); } catch { return s; }
        }

        static SessionInfo Parse(string file)
        {
            var mt = File.GetLastWriteTimeUtc(file);
            lock (Cache)
            {
                Tuple<DateTime, SessionInfo> c;
                if (Cache.TryGetValue(file, out c) && c.Item1 == mt) return c.Item2;
            }
            string head;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var buf = new byte[Math.Min(fs.Length, 16384)];
                int n = fs.Read(buf, 0, buf.Length);
                head = Encoding.UTF8.GetString(buf, 0, n);
            }
            var cut = head.IndexOf("\"remoteMcpServersConfig\"");
            if (cut > 0) head = head.Substring(0, cut);
            var s = new SessionInfo();
            foreach (Match m in StrField.Matches(head))
            {
                var v = Unescape(m.Groups[2].Value);
                switch (m.Groups[1].Value)
                {
                    case "sessionId": if (s.Id == null) s.Id = v; break;
                    case "cliSessionId": if (s.CliId == null) s.CliId = v; break;
                    case "title": if (s.Title == null) s.Title = v; break;
                    case "model": if (s.Model == null) s.Model = v; break;
                    case "cwd": if (s.Cwd == null) s.Cwd = v; break;
                }
            }
            foreach (Match m in NumField.Matches(head))
            {
                long v = long.Parse(m.Groups[2].Value);
                if (m.Groups[1].Value == "createdAt" && s.Created == 0) s.Created = v;
                if (m.Groups[1].Value == "lastActivityAt" && s.LastActivity == 0) s.LastActivity = v;
            }
            var a = ArchField.Match(head);
            s.Archived = a.Success && a.Groups[1].Value == "true";
            s.Org = Path.GetFileName(Path.GetDirectoryName(file));
            lock (Cache) Cache[file] = Tuple.Create(mt, s);
            return s;
        }

        // 每个账号的标签：组织 ID -> 账号备注（以该存档最近一次用量记录的组织为准）
        public static Dictionary<string, string> OrgLabels(List<DesktopProfile> profiles)
        {
            var map = new Dictionary<string, string>();
            foreach (var p in profiles)
            {
                var q = QuotaReader.Read(p.Dir);
                if (q != null && q.Org.Length > 0 && !map.ContainsKey(q.Org)) map[q.Org] = p.Remark;
            }
            return map;
        }

        public static List<SessionInfo> All()
        {
            var profiles = DesktopStore.Load();
            var labels = OrgLabels(profiles);
            var seen = new HashSet<string>();
            var list = new List<SessionInfo>();
            // 客户端删除会话时会留下 deleted_<编号> 标记；任何一个账号里删过的都不再显示
            var deleted = DeletedIds(profiles.Select(p => p.Dir));
            foreach (var p in profiles)
            {
                var root = Path.Combine(p.Dir, "claude-code-sessions");
                if (!Directory.Exists(root)) continue;
                foreach (var f in SafeFiles(root, "local_*.json"))
                {
                    SessionInfo s;
                    try { s = Parse(f); } catch { continue; }
                    if (s.Id == null || !seen.Add(s.Id)) continue;
                    if (deleted.Contains(s.Id.StartsWith("local_") ? s.Id.Substring(6) : s.Id)) continue;
                    string label;
                    s.Account = labels.TryGetValue(s.Org, out label) ? label : "其他账号 · " + s.Org.Substring(0, Math.Min(8, s.Org.Length));
                    s.Profile = p.Remark;
                    list.Add(s);
                }
            }
            return list.OrderByDescending(x => x.LastActivity).ToList();
        }

        public static HashSet<string> DeletedIds(IEnumerable<string> profileDirs)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in profileDirs)
            {
                var root = Path.Combine(d, "claude-code-sessions");
                if (!Directory.Exists(root)) continue;
                foreach (var f in SafeFiles(root, "deleted_*")) set.Add(Path.GetFileName(f).Substring(8));
            }
            return set;
        }

        public static IEnumerable<string> SafeFiles(string root, string pattern)
        {
            var stack = new Stack<string>(); stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                string[] files = new string[0], dirs = new string[0];
                try { files = Directory.GetFiles(d, pattern); dirs = Directory.GetDirectories(d); } catch { }
                foreach (var f in files) yield return f;
                foreach (var x in dirs) stack.Push(x);
            }
        }
    }

    // ---------------- 存储扫描：各数据目录占用、可清理的缓存 ----------------
    static class StorageScan
    {
        // 桌面 App 数据目录里可安全删除的缓存（App 会自动重建）
        public static readonly string[] CacheDirs = { "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache", "GrShaderCache", "ShaderCache", "GPUPersistentCache", "Crashpad", "logs" };

        public static long[] Measure(string dir)
        {
            long size = 0, files = 0;
            if (!Directory.Exists(dir)) return new long[] { 0, 0 };
            var stack = new Stack<string>(); stack.Push(dir);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                try { foreach (var f in new DirectoryInfo(d).GetFiles()) { size += f.Length; files++; } } catch { }
                try
                {
                    foreach (var sub in new DirectoryInfo(d).GetDirectories())
                        if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(sub.FullName); // 不跟随共享链接，避免重复计算
                }
                catch { }
            }
            return new long[] { size, files };
        }

        static long CacheSize(string profileDir)
        {
            long s = 0;
            foreach (var c in CacheDirs) s += Measure(Path.Combine(profileDir, c))[0];
            return s;
        }

        static Dictionary<string, object> Item(string kind, string id, string name, string path, string note, bool active = false, bool withCache = false)
        {
            var m = Measure(path);
            return new Dictionary<string, object> {
                { "kind", kind }, { "id", id }, { "name", name }, { "path", path }, { "note", note }, { "exists", Directory.Exists(path) },
                { "size", m[0] }, { "files", m[1] }, { "active", active }, { "cache", withCache ? CacheSize(path) : 0 }
            };
        }

        public static Dictionary<string, object> Scan()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var items = new List<object>();
            foreach (var p in DesktopStore.Load())
                items.Add(Item("desktop", p.Id, p.Remark, p.Dir, p.Active ? "桌面 App 账号 · 当前使用" : "桌面 App 账号存档", p.Active, true));
            items.Add(Item("projects", "", "对话记录（所有账号共用）", Path.Combine(Store.MainDir, "projects"), "~/.claude/projects，会话内容和 Token 统计都来自这里"));
            items.Add(Item("cli", "", "终端账号目录", Store.AccountsDir, "终端 Claude Code 各账号的登录与设置（共享的对话记录不重复计算）"));
            items.Add(Item("tool", "", "本工具数据", Settings.StateDir, "设置、操作日志、定价、窗口缓存"));
            long total = items.Cast<Dictionary<string, object>>().Sum(x => (long)x["size"]);
            long cache = items.Cast<Dictionary<string, object>>().Where(x => !(bool)x["active"]).Sum(x => (long)x["cache"]);
            return new Dictionary<string, object> {
                { "items", items }, { "total", total }, { "cleanable", cache },
                { "scannedAt", DateTime.Now.ToString("HH:mm:ss") }, { "ms", sw.ElapsedMilliseconds }
            };
        }

        // 清理已保存（非当前）账号存档里的缓存；当前账号正在被桌面 App 使用，不清理
        public static long CleanCache(string id)
        {
            var targets = DesktopStore.Load().Where(p => !p.Active && (id == "" || p.Id == id)).ToList();
            if (targets.Count == 0) throw new Exception(id == "" ? "没有可清理的已保存账号" : "当前使用中的账号不能清理缓存，请先切换到其他账号");
            long freed = 0;
            foreach (var p in targets)
            {
                if (!p.Dir.StartsWith(DesktopStore.ProfilesDir + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var c in CacheDirs)
                {
                    var d = Path.Combine(p.Dir, c);
                    if (!Directory.Exists(d) || (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                    var before = Measure(d)[0];
                    try { Store.RemoveLinks(d); Directory.Delete(d, true); freed += before; }
                    catch { freed += before - Measure(d)[0]; }
                }
            }
            return freed;
        }
    }

    // ---------------- 缓存清理（参考 CCBaga-A 的缓存清单；不清理登录信息、会话记录、设置） ----------------
    static class CacheCleaner
    {
        // Claude 桌面 App（Electron）数据目录里的缓存
        static readonly string[][] DesktopItems = {
            new[] { "Cache", "网页缓存" }, new[] { "Code Cache", "脚本编译缓存" }, new[] { "GPUCache", "GPU 缓存" },
            new[] { "DawnCache", "图形缓存" }, new[] { "DawnGraphiteCache", "图形缓存（Graphite）" }, new[] { "DawnWebGPUCache", "图形缓存（WebGPU）" },
            new[] { "GrShaderCache", "着色器缓存" }, new[] { "GraphiteDawnCache", "着色器缓存（Graphite）" }, new[] { "ShaderCache", "着色器缓存（旧版）" },
            new[] { "GPUPersistentCache", "GPU 持久缓存" }, new[] { @"Service Worker\CacheStorage", "Service Worker 缓存" },
            new[] { @"Service Worker\ScriptCache", "Service Worker 脚本缓存" }, new[] { "Crashpad", "崩溃报告" }, new[] { "logs", "运行日志" }
        };
        // Claude Code（~/.claude）里的缓存；待办、IDE 连接、后台进程、Shell 快照等运行中会用到的不清理
        static readonly string[][] CodeItems = {
            new[] { "cache", "Claude Code 缓存" }, new[] { "stats-cache.json", "使用统计缓存" }, new[] { "telemetry", "本地遥测缓存" },
            new[] { "paste-cache", "粘贴缓存" }, new[] { "statsig", "实验特性与遥测缓存" }, new[] { "downloads", "Claude Code 下载缓存" }
        };

        static bool IsLink(string p) { try { return (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0; } catch { return false; } }

        static long[] Measure(string p)
        {
            if (File.Exists(p)) return new[] { new FileInfo(p).Length, 1L };
            return StorageScan.Measure(p);
        }

        static List<Dictionary<string, object>> Build()
        {
            var list = new List<Dictionary<string, object>>();
            Action<string, string, string, string, bool> add = (id, group, label, path, needsClose) =>
            {
                if (!(Directory.Exists(path) || File.Exists(path)) || IsLink(path)) return;
                var m = Measure(path);
                if (m[0] <= 0) return;
                list.Add(new Dictionary<string, object> {
                    { "id", id }, { "group", group }, { "label", label }, { "path", path },
                    { "size", m[0] }, { "files", m[1] }, { "needsClose", needsClose }
                });
            };
            foreach (var p in DesktopStore.Load())
                foreach (var it in DesktopItems)
                    add("desk|" + p.Id + "|" + it[0], "桌面 App · " + p.Remark + (p.Active ? "（当前使用）" : ""), it[1], Path.Combine(p.Dir, it[0]), p.Active);
            var roots = new List<string[]> { new[] { Store.MainDir, "Claude Code · ~/.claude" } };
            foreach (var a in Store.Load()) if (!a.IsDefault) roots.Add(new[] { a.Dir, "终端账号 · " + a.Remark });
            for (int i = 0; i < roots.Count; i++)
                foreach (var it in CodeItems)
                    add("code|" + i + "|" + it[0], roots[i][1], it[1], Path.Combine(roots[i][0], it[0]), false);
            return list;
        }

        public static Dictionary<string, object> Scan()
        {
            var items = Build();
            return new Dictionary<string, object> {
                { "items", items }, { "total", items.Sum(x => (long)x["size"]) }, { "scannedAt", DateTime.Now.ToString("HH:mm:ss") }
            };
        }

        static long Remove(string path)
        {
            long freed = 0;
            if (File.Exists(path)) { var len = new FileInfo(path).Length; try { File.Delete(path); freed += len; } catch { } return freed; }
            if (!Directory.Exists(path) || IsLink(path)) return 0;
            Store.RemoveLinks(path); // 先拆链接，绝不顺着链接删到共享数据
            foreach (var f in Directory.GetFiles(path))
            { var len = new FileInfo(f).Length; try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); freed += len; } catch { } }
            foreach (var d in Directory.GetDirectories(path))
            { var m = StorageScan.Measure(d)[0]; try { Directory.Delete(d, true); freed += m; } catch { freed += m - StorageScan.Measure(d)[0]; } }
            return freed;
        }

        // 按扫描得到的 id 清理；只允许清理清单里的路径
        public static string Clean(IList<string> ids)
        {
            var all = Build().ToDictionary(x => (string)x["id"]);
            var sel = ids.Where(all.ContainsKey).Select(id => all[id]).ToList();
            if (sel.Count == 0) throw new Exception("没有选中可清理的缓存");
            bool needClose = sel.Any(x => (bool)x["needsClose"]) && DesktopStore.IsRunning();
            long freed = 0;
            if (needClose) DesktopStore.CloseForMaintenance();
            try { foreach (var x in sel) freed += Remove((string)x["path"]); }
            finally { if (needClose) try { DesktopStore.LaunchDesktop(); } catch { } }
            return "清理了 " + sel.Count + " 项缓存，释放 " + (freed / 1048576.0).ToString("0.0") + " MB" + (needClose ? "（已重新打开桌面 App）" : "");
        }
    }}
