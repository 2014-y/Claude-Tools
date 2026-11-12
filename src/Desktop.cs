// Claude 桌面 App 多账号：每个账号保存一整套 %APPDATA%\Claude 数据目录。
// 切换 = 关闭桌面 App -> 当前目录改名存档 -> 目标存档改名为 %APPDATA%\Claude -> 重新打开。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ClaudeSwitcher
{
    class DesktopProfile
    {
        public string Id;
        public string Remark;
        public string Dir;
        public bool Active;
        public DateTime LastUsed;
        public string Email;
        public int Sessions;
        public bool LoggedIn;
    }

    static class DesktopStore
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        const string Marker = ".cs-profile";

        static string Env(string name, Environment.SpecialFolder fallback)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? Environment.GetFolderPath(fallback) : v;
        }

        public static readonly string LocalAppData = Env("CLAUDE_SWITCHER_LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData);

        // ---------- 两种安装方式 ----------
        // 传统安装版（Squirrel）：程序在 %LOCALAPPDATA%\AnthropicClaude，数据在 %APPDATA%\Claude
        // MSIX 包版（官网新安装包 / 微软商店 / winget）：程序在 C:\Program Files\WindowsApps\Claude_*，
        //   数据被系统重定向到 %LOCALAPPDATA%\Packages\Claude_<发布者>\LocalCache\Roaming\Claude
        public static readonly string MsixPackageDir = FindMsixPackageDir();
        public static readonly string AppData = ResolveAppData();
        // 数据是否放在 MSIX 的重定向文件夹里（仅用于显示）
        public static bool DataInPackage { get { return MsixPackageDir != null && AppData.StartsWith(MsixPackageDir + "\\", StringComparison.OrdinalIgnoreCase); } }

        static string FindMsixPackageDir()
        {
            try
            {
                var root = Path.Combine(LocalAppData, "Packages");
                if (!Directory.Exists(root)) return null;
                // 包目录名就是「包系列名」，例如 Claude_pzs8sxrjxfjjc；取最近有数据活动的那个
                return Directory.GetDirectories(root, "Claude_*")
                    .Where(d => Directory.Exists(Path.Combine(d, @"LocalCache\Roaming\Claude")) || Directory.Exists(Path.Combine(d, "LocalCache")))
                    .OrderByDescending(d => Stamp(Path.Combine(d, @"LocalCache\Roaming\Claude")))
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        // 数据目录最近一次活动的时间（登录信息 / 配置文件的修改时间）
        static DateTime Stamp(string claudeDir)
        {
            var t = DateTime.MinValue;
            foreach (var f in new[] { "config.json", "Local State", @"Local Storage\leveldb" })
            {
                try
                {
                    var p = Path.Combine(claudeDir, f);
                    var w = File.Exists(p) ? File.GetLastWriteTimeUtc(p) : Directory.Exists(p) ? Directory.GetLastWriteTimeUtc(p) : DateTime.MinValue;
                    if (w > t) t = w;
                }
                catch { }
            }
            return t;
        }

        // MSIX 版到底用哪个文件夹，取决于这台电脑的历史：
        //   以前装过旧版 Claude（真实的 %APPDATA%\Claude 已存在）→ MSIX 版继续用这个真实文件夹；
        //   全新安装 → 用系统重定向的 Packages\Claude_*\LocalCache\Roaming\Claude。
        // 所以不能看「装没装传统版」，而要看哪个文件夹里真的有登录、由本工具管理、最近被使用。
        static bool HasLogin(string claudeDir)
        {
            try { var c = Path.Combine(claudeDir, "config.json"); return File.Exists(c) && File.ReadAllText(c).Contains("\"oauth:tokenCache"); } catch { return false; }
        }
        static int Score(string claudeDir)
        {
            if (!Directory.Exists(claudeDir)) return -1;
            return (HasLogin(claudeDir) ? 4 : 0) + (File.Exists(Path.Combine(claudeDir, Marker)) ? 2 : 0);
        }
        static string ResolveAppData()
        {
            var forced = Environment.GetEnvironmentVariable("CLAUDE_SWITCHER_APPDATA");
            if (!string.IsNullOrEmpty(forced)) return forced;
            var roaming = Env("CLAUDE_SWITCHER_ROAMING", Environment.SpecialFolder.ApplicationData);
            if (MsixPackageDir == null) return roaming;
            var msixRoaming = Path.Combine(MsixPackageDir, @"LocalCache\Roaming");
            string real = Path.Combine(roaming, "Claude"), virt = Path.Combine(msixRoaming, "Claude");
            int rs = Score(real), vs = Score(virt);
            if (rs < 0 && vs < 0) return ClassicInstalled ? roaming : msixRoaming; // 都还没有数据：按安装方式
            if (rs != vs) return rs > vs ? roaming : msixRoaming;                     // 有登录 > 有本工具标记 > 只是存在
            return Stamp(virt) > Stamp(real) ? msixRoaming : roaming;                // 不分上下时看谁最近被使用
        }
        static bool ClassicInstalled { get { return File.Exists(Path.Combine(LocalAppData, @"AnthropicClaude\claude.exe")) || Directory.Exists(Path.Combine(LocalAppData, @"AnthropicClaude")); } }
        // 启动 / 识别桌面 App 用哪种方式：只看程序装成了什么，和数据放在哪个文件夹无关
        public static bool MsixApp { get { return MsixPackageDir != null && !ClassicInstalled; } }
        public static string Edition { get { return MsixApp ? "MSIX 包版" : "传统安装版"; } }
        public static readonly string ActiveDir = Path.Combine(AppData, "Claude");
        public static readonly string ProfilesDir = Path.Combine(AppData, "Claude-profiles");
        static bool TestMode { get { return Environment.GetEnvironmentVariable("CLAUDE_SWITCHER_TEST") == "1"; } }

        // ---------- 标记文件：第一行 id，第二行备注 ----------
        static string[] ReadMarker(string dir)
        {
            var p = Path.Combine(dir, Marker);
            if (!File.Exists(p)) return null;
            var lines = File.ReadAllLines(p, Utf8);
            if (lines.Length == 0 || lines[0].Trim().Length == 0) return null;
            return new[] { lines[0].Trim(), lines.Length > 1 ? lines[1].Trim() : lines[0].Trim() };
        }

        static void WriteMarker(string dir, string id, string remark)
        {
            File.WriteAllLines(Path.Combine(dir, Marker), new[] { id, remark }, Utf8);
        }

        static string NewId(string exclude)
        {
            int n = 1;
            while (true)
            {
                var id = "p" + n;
                if (id != exclude && !Directory.Exists(Path.Combine(ProfilesDir, id))) return id;
                n++;
            }
        }

        // 当前目录没有标记时，自动登记为一个账号
        public static string[] EnsureActiveMarker()
        {
            if (!Directory.Exists(ActiveDir)) return null;
            var m = ReadMarker(ActiveDir);
            if (m != null) return m;
            var id = NewId(null);
            var email = DetectEmail(ActiveDir);
            WriteMarker(ActiveDir, id, email.Length > 0 ? email : "账号 1");
            return ReadMarker(ActiveDir);
        }

        public static List<DesktopProfile> Load()
        {
            var list = new List<DesktopProfile>();
            var am = EnsureActiveMarker();
            if (am != null) list.Add(Make(ActiveDir, am, true));
            if (Directory.Exists(ProfilesDir))
            {
                foreach (var d in Directory.GetDirectories(ProfilesDir))
                {
                    var m = ReadMarker(d);
                    if (m == null) continue;
                    if (am != null && m[0] == am[0]) continue;
                    list.Add(Make(d, m, false));
                }
            }
            return list.OrderByDescending(p => p.Active).ThenByDescending(p => p.LastUsed).ToList();
        }

        static DesktopProfile Make(string dir, string[] m, bool active)
        {
            var p = new DesktopProfile { Id = m[0], Remark = m[1], Dir = dir, Active = active };
            try { p.LastUsed = File.GetLastWriteTime(Path.Combine(dir, Marker)); } catch { }
            p.Email = DetectEmail(dir);
            // 登录信息：config.json 里有 oauth 令牌缓存即视为已登录
            try { var cfg = Path.Combine(dir, "config.json"); p.LoggedIn = File.Exists(cfg) && File.ReadAllText(cfg).Contains("\"oauth:tokenCache"); } catch { }
            try
            {
                var s = Path.Combine(dir, "claude-code-sessions");
                if (Directory.Exists(s))
                    p.Sessions = Directory.GetFiles(s, "*", SearchOption.AllDirectories).Count(f => !Path.GetFileName(f).StartsWith("deleted_"));
            }
            catch { }
            return p;
        }

        // ---------- 从 claude.ai 的 IndexedDB 里识别登录邮箱（识别不到就为空） ----------
        static readonly Dictionary<string, Tuple<DateTime, string>> EmailCache = new Dictionary<string, Tuple<DateTime, string>>();
        static readonly Regex EmailRe = new Regex(@"[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9.\-]{1,100}\.[A-Za-z]{2,12}");

        public static string DetectEmail(string dir)
        {
            try
            {
                var roots = new[] {
                    Path.Combine(dir, @"IndexedDB\https_claude.ai_0.indexeddb.blob"),
                    Path.Combine(dir, @"IndexedDB\https_claude.ai_0.indexeddb.leveldb"),
                    Path.Combine(dir, @"Local Storage\leveldb")
                };
                var files = new List<FileInfo>();
                foreach (var r in roots)
                    if (Directory.Exists(r))
                        files.AddRange(new DirectoryInfo(r).GetFiles("*", SearchOption.AllDirectories).Where(f => f.Length < 8 * 1024 * 1024));
                if (files.Count == 0) return "";
                var stamp = files.Max(f => f.LastWriteTimeUtc);
                Tuple<DateTime, string> c;
                if (EmailCache.TryGetValue(dir, out c) && c.Item1 == stamp) return c.Item2;

                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    byte[] b;
                    try
                    {
                        using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            b = new byte[fs.Length];
                            int read = 0;
                            while (read < b.Length) { int k = fs.Read(b, read, b.Length - read); if (k <= 0) break; read += k; }
                        }
                    }
                    catch { continue; }
                    foreach (var text in new[] { Encoding.UTF8.GetString(b), Encoding.Unicode.GetString(b) })
                        foreach (Match m in EmailRe.Matches(text))
                        {
                            var e = m.Value.ToLowerInvariant();
                            if (e.EndsWith("@anthropic.com") || e.Contains("example.") || e.Contains("sentry")) continue;
                            int n; counts.TryGetValue(e, out n); counts[e] = n + 1;
                        }
                }
                var best = counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? "";
                EmailCache[dir] = Tuple.Create(stamp, best);
                return best;
            }
            catch { return ""; }
        }

        // ---------- 进程控制 ----------
        static string InstallDir { get { return Path.Combine(LocalAppData, "AnthropicClaude"); } }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        static string ProcessPath(Process p)
        {
            try { return p.MainModule.FileName; } catch { }
            var h = OpenProcess(0x1000, false, p.Id);   // PROCESS_QUERY_LIMITED_INFORMATION，打包应用也能查
            if (h == IntPtr.Zero) return null;
            try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null; }
            finally { CloseHandle(h); }
        }
        static bool IsDesktopProcess(Process p)
        {
            var path = ProcessPath(p);
            if (string.IsNullOrEmpty(path)) return false;
            return path.StartsWith(InstallDir + "\\", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(ActiveDir + "\\", StringComparison.OrdinalIgnoreCase)
                || (MsixPackageDir != null && path.IndexOf("\\WindowsApps\\Claude_", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static List<Process> DesktopProcesses()
        {
            return Process.GetProcessesByName("claude").Where(IsDesktopProcess).ToList();
        }

        public static bool IsRunning()
        {
            if (TestMode) return false;
            return DesktopProcesses().Count > 0;
        }

        // 桌面 App 及其启动的所有子进程（终端、Claude Code 会话等），它们可能占用数据目录
        static List<int> ProcessTree(IEnumerable<int> roots)
        {
            var children = new Dictionary<int, List<int>>();
            using (var s = new System.Management.ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process"))
                foreach (System.Management.ManagementObject o in s.Get())
                {
                    int pid = Convert.ToInt32(o["ProcessId"]), ppid = Convert.ToInt32(o["ParentProcessId"]);
                    List<int> l;
                    if (!children.TryGetValue(ppid, out l)) children[ppid] = l = new List<int>();
                    l.Add(pid);
                }
            var result = new HashSet<int>(roots);
            var queue = new Queue<int>(result);
            int self = Process.GetCurrentProcess().Id;
            while (queue.Count > 0)
            {
                List<int> l;
                if (children.TryGetValue(queue.Dequeue(), out l))
                    foreach (var c in l)
                        if (c != self && result.Add(c)) queue.Enqueue(c);
            }
            return result.ToList();
        }

        public static void CloseForMaintenance() { CloseDesktop(); }

        static void CloseDesktop()
        {
            if (TestMode) return;
            List<int> tree;
            try { tree = ProcessTree(DesktopProcesses().Select(p => p.Id)); }
            catch { tree = new List<int>(); }
            foreach (var p in DesktopProcesses())
                try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
            for (int i = 0; i < 30 && DesktopProcesses().Count > 0; i++) Thread.Sleep(100);
            foreach (var id in tree)
                try { Process.GetProcessById(id).Kill(); } catch { }
            foreach (var p in DesktopProcesses())
                try { p.Kill(); } catch { }
            for (int i = 0; i < 100 && DesktopProcesses().Count > 0; i++) Thread.Sleep(100);
            if (DesktopProcesses().Count > 0) throw new Exception("无法关闭 Claude 桌面 App，请手动退出（托盘图标右键 → Quit）后再试。");
            Thread.Sleep(800); // 等文件句柄释放
        }

        static string aumid;
        // MSIX 版的应用 ID（包系列名!应用名），用于通过 shell:AppsFolder 启动
        static string MsixAppId()
        {
            if (aumid != null) return aumid;
            var family = Path.GetFileName(MsixPackageDir);
            string app = null;
            try
            {
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"$p=Get-AppxPackage | ? { $_.PackageFamilyName -eq '" + family + "' } | select -First 1; if($p){ ((Get-AppxPackageManifest $p).Package.Applications.Application | select -First 1).Id }\"")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using (var pr = Process.Start(psi)) { var o = pr.StandardOutput.ReadToEnd().Trim(); pr.WaitForExit(15000); if (o.Length > 0 && o.IndexOf(' ') < 0) app = o; }
            }
            catch { }
            aumid = family + "!" + (app ?? "Claude");
            return aumid;
        }

        public static void LaunchDesktop()
        {
            if (TestMode) return;
            if (MsixApp)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + MsixAppId()) { UseShellExecute = true });
                return;
            }
            // 当前账号绑定 / 分配到的代理（桌面 App 不支持带账号密码的代理，只传地址）
            string args = "";
            try
            {
                var m = ReadMarker(ActiveDir);
                var px = m == null ? null : ProxyPool.For("d:" + m[0], "desktop");
                if (px != null) args = "--proxy-server=\"" + ProxyPool.HostPort(px) + "\"";
            }
            catch { }
            var stub = Path.Combine(InstallDir, "claude.exe");
            string exe = File.Exists(stub) ? stub : null;
            if (exe == null && Directory.Exists(InstallDir))
                exe = Directory.GetDirectories(InstallDir, "app-*")
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "claude.exe")).FirstOrDefault(File.Exists);
            if (exe == null) throw new Exception("没有找到 Claude 桌面 App 的安装位置：" + InstallDir);
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
        }

        static void MoveRetry(string from, string to)
        {
            Exception last = null;
            for (int i = 0; i < 15; i++)
            {
                try { Directory.Move(from, to); return; }
                catch (IOException ex) { last = ex; }
                catch (UnauthorizedAccessException ex) { last = ex; }
                Thread.Sleep(700);
            }
            throw new Exception("Claude 数据目录被占用，无法切换。\n请关闭打开着该目录的终端 / 资源管理器窗口后重试。\n(" + (last != null ? last.Message : "") + ")");
        }

        static readonly object SwitchLock = new object();

        // 切换到指定账号（target 为 null 时由 prepareNew 创建新的空账号）
        static void SwitchCore(Func<string> prepareTarget)
        {
            lock (SwitchLock)
            {
                Directory.CreateDirectory(ProfilesDir);
                var am = EnsureActiveMarker();
                string parked = null;
                if (am != null)
                {
                    parked = Path.Combine(ProfilesDir, am[0]);
                    if (Directory.Exists(parked)) throw new Exception("存档目录已存在，已停止：" + parked);
                }
                var target = prepareTarget();
                bool wasRunning = IsRunning();
                CloseDesktop();
                bool movedOut = false;
                try
                {
                    if (am != null) { MoveRetry(ActiveDir, parked); movedOut = true; }
                    MoveRetry(target, ActiveDir);
                    var m = ReadMarker(ActiveDir);
                    if (m != null) WriteMarker(ActiveDir, m[0], m[1]); // 刷新“上次使用”
                    LastSync = "";
                    if (parked != null && Settings.Bool("syncSessions"))
                    {
                        try { LastSync = SyncSessions(parked, ActiveDir); }
                        catch (Exception ex) { LastSync = "会话同步失败：" + ex.Message; }
                    }
                    if (parked != null && Settings.Bool("syncClientSettings"))
                    {
                        string r;
                        try { r = SyncClientSettings(parked, ActiveDir); }
                        catch (Exception ex) { r = "客户端设置同步失败：" + ex.Message; }
                        if (r.Length > 0) LastSync = LastSync.Length > 0 ? LastSync + "；" + r : r;
                    }
                }
                catch
                {
                    if (movedOut && !Directory.Exists(ActiveDir))
                        try { MoveRetry(parked, ActiveDir); } catch { }
                    if (wasRunning) try { LaunchDesktop(); } catch { }
                    throw;
                }
                LaunchDesktop();
            }
        }

        // ---------- 会话同步：把上一个账号的会话记录带到新账号 ----------
        public static string LastSync = "";
        public static int[] LastCounts = new int[3];

        // 自动合并：启动时、每 5 分钟、切换后各合并一次；有新增才写日志
        public static void AutoMerge()
        {
            try
            {
                if (!Settings.Bool("autoMerge")) return;
                if (Load().Count < 2) return;
                var msg = MergeAll();
                if (LastCounts[0] + LastCounts[2] > 0) { } // 计数在 MergeAll 内累加
                if (msg.IndexOf("补齐 0 条") < 0 || msg.IndexOf("同步删除") >= 0) OpLog.Add("自动合并会话", "所有桌面账号", true, msg, 0);
            }
            catch (Exception ex) { OpLog.Add("自动合并会话", "所有桌面账号", false, ex.Message, 0); }
        }

        // 手动合并：所有账号存档两两互相同步会话（含删除标记），结果是每个账号都有全部会话
        public static string MergeAll()
        {
            lock (SwitchLock)
            {
                var profiles = Load();
                if (profiles.Count < 2) throw new Exception("至少需要两个桌面账号才能合并");
                // 合并只是往会话目录里新增文件，不需要也不再关闭桌面 App
                int copied = 0, ws = 0, removed = 0, skipped = 0;
                try
                {
                    foreach (var to in profiles)
                        foreach (var from in profiles)
                        {
                            if (from.Id == to.Id) continue;
                            if (TargetSessionDir(to.Dir) == null) { skipped++; continue; }
                            SyncSessions(from.Dir, to.Dir);
                            copied += LastCounts[0]; ws += LastCounts[1]; removed += LastCounts[2];
                        }
                }
                finally { }
                var msg = "已合并 " + profiles.Count + " 个账号：补齐 " + copied + " 条会话记录" + (ws > 0 ? "（含 " + ws + " 个临时工作区）" : "") + (removed > 0 ? "，同步删除 " + removed + " 条" : "");
                if (skipped > 0) msg += "；有账号还没登录过，已跳过";
                return msg;
            }
        }
        static readonly Regex CwdField = new Regex("\"(cwd|originCwd)\":\"((?:[^\"\\\\]|\\\\.)*)\"");

        // 新账号当前登录的 账号ID\组织ID 会话目录：桌面 App 只显示这个目录里的会话
        // 优先用 config.json 里的 lastKnownAccountUuid（当前登录账号）；其次用最近一次用量记录的组织
        static string TargetSessionDir(string profileDir)
        {
            var root = Path.Combine(profileDir, "claude-code-sessions");
            if (!Directory.Exists(root)) return null;
            string accId = null;
            try
            {
                var cfg = Path.Combine(profileDir, "config.json");
                if (File.Exists(cfg))
                {
                    var m = Regex.Match(File.ReadAllText(cfg, Encoding.UTF8), "\"lastKnownAccountUuid\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) accId = m.Groups[1].Value;
                }
            }
            catch { }
            var q = QuotaReader.Read(profileDir);
            if (accId != null)
            {
                var accDir = Path.Combine(root, accId);
                if (Directory.Exists(accDir))
                {
                    var orgs = Directory.GetDirectories(accDir);
                    if (orgs.Length > 0)
                    {
                        if (q != null && q.Org.Length > 0)
                        {
                            var hit = orgs.FirstOrDefault(d => string.Equals(Path.GetFileName(d), q.Org, StringComparison.OrdinalIgnoreCase));
                            if (hit != null) return hit;
                        }
                        return orgs.OrderByDescending(d => Directory.GetLastWriteTimeUtc(d)).First();
                    }
                }
            }
            var orgDirs = Directory.GetDirectories(root).SelectMany(a => Directory.GetDirectories(a)).ToList();
            if (orgDirs.Count == 0) return null;
            if (q != null && q.Org.Length > 0)
            {
                var hit = orgDirs.FirstOrDefault(d => string.Equals(Path.GetFileName(d), q.Org, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return orgDirs.OrderByDescending(d => Directory.GetLastWriteTimeUtc(d)).First();
        }
        static int CopyDir(string src, string dst, ref int budget)
        {
            int n = 0;
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                if (--budget < 0) throw new Exception("工作区文件太多，已跳过");
                var to = Path.Combine(dst, Path.GetFileName(f));
                if (!File.Exists(to)) { File.Copy(f, to); n++; }
            }
            foreach (var d in Directory.GetDirectories(src))
            {
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                n += CopyDir(d, Path.Combine(dst, Path.GetFileName(d)), ref budget);
            }
            return n;
        }

        public static string SyncSessions(string fromDir, string toDir)
        {
            var target = TargetSessionDir(toDir);
            if (target == null) return "新账号还没有会话目录（首次登录后再切换一次即可同步）";
            var srcRoot = Path.Combine(fromDir, "claude-code-sessions");
            if (!Directory.Exists(srcRoot)) return "";
            var toRoot = Path.Combine(toDir, "claude-code-sessions");
            // 只算目标目录（当前登录账号）里已有的：别的账号目录里有同名文件，当前账号一样看不到
            var existing = new HashSet<string>(Directory.GetFiles(target, "local_*.json").Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            int copied = 0, ws = 0, removed = 0;
            // 删除同步：两边任意一边删过的会话，都按已删除处理
            var deleted = Sessions.DeletedIds(new[] { fromDir, toDir });
            foreach (var id in deleted)
            {
                if (!Sessions.SafeFiles(toRoot, "deleted_" + id).Any())
                    try { File.WriteAllText(Path.Combine(target, "deleted_" + id), ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds).ToString()); } catch { }
                foreach (var f in Sessions.SafeFiles(toRoot, "local_" + id + ".json").ToList())
                    try { File.Delete(f); removed++; existing.Remove(Path.GetFileName(f)); } catch { }
            }
            var wsPrefix = Path.Combine(ActiveDir, "scratch-workspaces") + "\\";
            foreach (var f in Directory.GetFiles(srcRoot, "local_*.json", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f);
                if (existing.Contains(name)) continue;
                if (deleted.Contains(Path.GetFileNameWithoutExtension(name).Substring(6))) continue; // 已删除的不再复活
                File.Copy(f, Path.Combine(target, name));
                existing.Add(name); copied++;
                // 临时会话的工作目录在数据目录里，一起带过去，保证能继续对话
                try
                {
                    var head = File.ReadAllText(f, Encoding.UTF8);
                    var cut = head.IndexOf("\"remoteMcpServersConfig\"", StringComparison.Ordinal);
                    if (cut > 0) head = head.Substring(0, cut);
                    foreach (Match m in CwdField.Matches(head))
                    {
                        var cwd = m.Groups[2].Value.Replace("\\\\", "\\");
                        if (!cwd.StartsWith(wsPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                        var rel = cwd.Substring(ActiveDir.Length).TrimStart('\\');
                        var src = Path.Combine(fromDir, rel); var dst = Path.Combine(toDir, rel);
                        if (Directory.Exists(src) && !Directory.Exists(dst))
                        {
                            int budget = 20000;
                            try { CopyDir(src, dst, ref budget); ws++; } catch { }
                        }
                    }
                }
                catch { }
            }
            // 同一个存档里还可能躺着别的（旧）账号目录下的会话：桌面 App 只显示当前登录账号目录里的，所以也一并补进来
            foreach (var f in Directory.GetFiles(toRoot, "local_*.json", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetDirectoryName(f), target, StringComparison.OrdinalIgnoreCase)) continue;
                var nm = Path.GetFileName(f);
                if (existing.Contains(nm)) continue;
                if (deleted.Contains(Path.GetFileNameWithoutExtension(nm).Substring(6))) continue;
                try { File.Copy(f, Path.Combine(target, nm)); existing.Add(nm); copied++; } catch { }
            }
            LastCounts = new[] { copied, ws, removed };
            var msg = copied == 0 ? "会话记录已是最新" : "已同步 " + copied + " 条会话记录" + (ws > 0 ? "（含 " + ws + " 个临时工作区）" : "");
            return removed > 0 ? msg + "，同步删除 " + removed + " 条" : msg;
        }

        // ---------- 客户端设置同步：主题、界面缩放（字号）、窗口布局等，不含登录信息 ----------
        static readonly string[] ConfigKeys = {
            "userThemeMode", "windowControlsZoomFactor", "locale", "bootFrameLayout", "bootSidebarPlaceholder",
            "bootPlaceholderColors", "quickWindowPosition", "terminalCliPowerShellPath"
        };
        static readonly string[] WholeFiles = { "window-state.json", "Preferences", "ccd-session-presets.json", "claude_desktop_config.json" };

        public static string SyncClientSettings(string fromDir, string toDir)
        {
            var json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            int n = 0;
            // config.json 只合并白名单里的设置项（同一文件里还有登录令牌，绝不整体复制）
            var src = Path.Combine(fromDir, "config.json"); var dst = Path.Combine(toDir, "config.json");
            if (File.Exists(src))
            {
                var a = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(src, Encoding.UTF8));
                var b = File.Exists(dst) ? json.Deserialize<Dictionary<string, object>>(File.ReadAllText(dst, Encoding.UTF8)) : new Dictionary<string, object>();
                bool changed = false;
                foreach (var k in ConfigKeys)
                {
                    object v;
                    if (a.TryGetValue(k, out v)) { b[k] = v; changed = true; n++; }
                }
                if (changed)
                {
                    if (File.Exists(dst)) File.Copy(dst, dst + ".bak", true);
                    File.WriteAllText(dst, json.Serialize(b), new UTF8Encoding(false));
                }
            }
            foreach (var f in WholeFiles)
            {
                var s = Path.Combine(fromDir, f);
                if (!File.Exists(s)) continue;
                File.Copy(s, Path.Combine(toDir, f), true);
                n++;
            }
            return n > 0 ? "已同步客户端设置（字号、主题、窗口布局等）" : "";
        }

        public static void SwitchTo(string id)
        {
            var am = EnsureActiveMarker();
            if (am != null && am[0] == id) { if (!IsRunning()) LaunchDesktop(); return; }
            var dir = FindStored(id);
            SwitchCore(() => dir);
        }

        public static void AddNew(string remark)
        {
            if (string.IsNullOrWhiteSpace(remark)) remark = "新账号";
            SwitchCore(() =>
            {
                var am = ReadMarker(ActiveDir);
                var id = NewId(am != null ? am[0] : null);
                var dir = Path.Combine(ProfilesDir, id);
                Directory.CreateDirectory(dir);
                WriteMarker(dir, id, remark.Trim());
                // 共用 MCP / 偏好配置，登录信息不复制
                foreach (var f in new[] { "claude_desktop_config.json" })
                {
                    var src = Path.Combine(ActiveDir, f);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(dir, f));
                }
                return dir;
            });
        }

        static string FindStored(string id)
        {
            if (Directory.Exists(ProfilesDir))
                foreach (var d in Directory.GetDirectories(ProfilesDir))
                {
                    var m = ReadMarker(d);
                    if (m != null && m[0] == id) return d;
                }
            throw new Exception("找不到这个账号的存档");
        }

        static string FindAny(string id)
        {
            var am = EnsureActiveMarker();
            if (am != null && am[0] == id) return ActiveDir;
            return FindStored(id);
        }

        public static void Rename(string id, string remark)
        {
            if (string.IsNullOrWhiteSpace(remark)) return;
            var dir = FindAny(id);
            var m = ReadMarker(dir);
            var t = File.GetLastWriteTime(Path.Combine(dir, Marker));
            WriteMarker(dir, m[0], remark.Trim());
            File.SetLastWriteTime(Path.Combine(dir, Marker), t);
        }

        public static void Delete(string id)
        {
            var am = EnsureActiveMarker();
            if (am != null && am[0] == id) throw new Exception("正在使用的账号不能删除，请先切换到其他账号。");
            var dir = FindStored(id);
            if (!dir.StartsWith(ProfilesDir + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("路径异常，已停止");
            Store.RemoveLinks(dir);
            if (Store.HasLinks(dir)) throw new Exception("存档里还有链接没拆除，已停止删除：" + dir);
            Directory.Delete(dir, true);
        }

        public static void OpenDir(string id)
        {
            Process.Start("explorer.exe", "\"" + FindAny(id) + "\"");
        }
    }
}
