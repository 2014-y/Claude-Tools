// 程序入口：托盘常驻 + 本地接口（HttpListener）+ Edge 应用模式窗口（ui.html 内嵌在 exe 中）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClaudeSwitcher
{
    static class Program
    {
        static string Token, Url;
        static DateTime LastPing = DateTime.MinValue;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static NotifyIcon tray;
        static System.Threading.Timer autoMergeTimer;
        static SynchronizationContext ui;
        static bool windowEverOpened;

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

        // 如果被别的程序（比如从 Claude 桌面 App 里的终端启动）放进了它的作业对象，
        // 那个程序退出时会连带结束我们。这种情况下通过 WMI 以独立进程重新启动自己。
        static bool RelaunchOutsideJob(string[] args)
        {
            try
            {
                if (args.Contains("--detached")) return false;
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_SWITCHER_LOCALAPPDATA"))) return false; // 测试实例不处理
                bool inJob;
                if (!IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out inJob) || !inJob) return false;
                var cmd = "\"" + Application.ExecutablePath + "\" " + string.Join(" ", args.Select(x => "\"" + x + "\"")) + " --detached";
                using (var cls = new System.Management.ManagementClass("Win32_Process"))
                {
                    var r = cls.InvokeMethod("Create", new object[] { cmd, Path.GetDirectoryName(Application.ExecutablePath), null, null });
                    return Convert.ToInt32(r) == 0;
                }
            }
            catch { return false; }
        }

        [STAThread]
        // 清掉从别的 Claude 会话继承来的环境变量（账号、令牌、代理地址等），
        // 否则子进程 claude 会用那个会话的账号，而不是各账号自己的登录
        static void CleanInheritedEnv()
        {
            try
            {
                foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                {
                    var k = (string)e.Key;
                    if (k.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) ||
                        k.StartsWith("CLAUDE_AGENT_SDK", StringComparison.OrdinalIgnoreCase) || k.StartsWith("CLAUDE_PREVIEW", StringComparison.OrdinalIgnoreCase) ||
                        k.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase) || k.Equals("CLAUDE_PID", StringComparison.OrdinalIgnoreCase) ||
                        k.Equals("CLAUDE_CONFIG_DIR", StringComparison.OrdinalIgnoreCase))
                        Environment.SetEnvironmentVariable(k, null);
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        static void EnableDpiAwareness()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { } // 每个显示器各自缩放（Win10 1703+）
            try { SetProcessDPIAware(); } catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            CleanInheritedEnv();
            EnableDpiAwareness();
            WebUi.Init();
            if (RelaunchOutsideJob(args)) return;
            bool first;
            // 测试实例用独立的单实例锁，不影响正在运行的正式版
            var isTest = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_SWITCHER_LOCALAPPDATA"));
            using (var mutex = new Mutex(true, "ClaudeSwitcher_SingleInstance" + (isTest ? "_test" : ""), out first))
            {
                Directory.CreateDirectory(Settings.StateDir);
                var urlFile = Path.Combine(Settings.StateDir, "url.txt");
                bool trayOnly = args.Contains("--tray");
                if (!first)
                {
                    if (!trayOnly && !AskRunningToShow(urlFile) && !FocusExisting()) try { OpenWindow(File.ReadAllText(urlFile).Trim()); SizeNewWindow(); Thread.Sleep(9000); } catch { }
                    return;
                }
                try
                {
                    Application.EnableVisualStyles();
                    // 全局异常保护：出错只记日志，不让程序退出
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    Application.ThreadException += (s, e) => OpLog.Add("程序异常", "界面线程", false, e.Exception.Message, 0);
                    AppDomain.CurrentDomain.UnhandledException += (s, e) => OpLog.Add("程序异常", "后台线程", false, Convert.ToString(e.ExceptionObject), 0);
                    System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => { e.SetObserved(); OpLog.Add("程序异常", "后台任务", false, e.Exception.Message, 0); };
                    Settings.RefreshAutoStart();
                    StartServer(urlFile);
                    // 自动合并所有账号的会话记录：启动后一次，之后每 5 分钟
                    autoMergeTimer = new System.Threading.Timer(_ => DesktopStore.AutoMerge(), null, 15000, 5 * 60 * 1000);
                    if (Environment.GetEnvironmentVariable("CLAUDE_SWITCHER_NOBROWSER") == "1") { Thread.Sleep(Timeout.Infinite); return; }
                    RunTray(trayOnly);
                }
                catch (Exception ex) { MessageBox.Show(ex.ToString(), "Claude Tools 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        // 通知已在运行的实例把窗口显示出来（窗口在那个进程里，可能只是被隐藏了）
        static bool AskRunningToShow(string urlFile)
        {
            try
            {
                AllowSetForegroundWindow(-1); // 允许那个实例把窗口切到最前
                var u = File.ReadAllText(urlFile).Trim();
                var baseUrl = u.Split('?')[0]; var tok = u.Substring(u.IndexOf("t=") + 2);
                var rq = (HttpWebRequest)WebRequest.Create(baseUrl + "api/ui/show");
                rq.Method = "POST"; rq.Headers["X-Token"] = tok; rq.ContentType = "application/json"; rq.Timeout = 4000;
                var b = Encoding.UTF8.GetBytes("{}"); rq.ContentLength = b.Length;
                using (var s = rq.GetRequestStream()) s.Write(b, 0, b.Length);
                using (var r = (HttpWebResponse)rq.GetResponse()) using (var sr = new StreamReader(r.GetResponseStream())) return sr.ReadToEnd().Contains("\"ok\":true");
            }
            catch { return false; }
        }

        // ---------------- 托盘 ----------------
        static void RunTray(bool trayOnly)
        {
            ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);

            tray = new NotifyIcon { Text = "Claude Tools", Visible = true };
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { tray.Icon = SystemIcons.Application; }
            var menu = new ContextMenuStrip();
            menu.Opening += delegate { BuildMenu(menu); };
            BuildMenu(menu);
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
            tray.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

            // 没开启托盘常驻时：窗口关掉（不再心跳）就退出
            var timer = new System.Windows.Forms.Timer { Interval = 3000 };
            timer.Tick += delegate
            {
                if (!WebUi.Available && !Settings.Bool("trayMode") && windowEverOpened && (DateTime.Now - LastPing).TotalSeconds > 15) Quit();
            };
            timer.Start();

            if (!trayOnly) ShowWindow();
            else Balloon("Claude Tools 已在后台运行", "点击托盘图标打开控制台，右键可快速切换账号。");
            Application.Run();
        }

        static void BuildMenu(ContextMenuStrip menu)
        {
            menu.Items.Clear();
            menu.Items.Add("打开控制台", null, delegate { ShowWindow(); }).Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripLabel("切换 Claude 桌面 App 账号") { ForeColor = Color.Gray });
            try
            {
                var mask = Settings.Bool("maskDefault");
                foreach (var p in DesktopStore.Load())
                {
                    var id = p.Id; var name = mask ? Mask(p.Remark) : p.Remark;
                    var email = string.IsNullOrEmpty(p.Email) ? "" : "  (" + (mask ? Mask(p.Email) : p.Email) + ")";
                    var item = new ToolStripMenuItem(name + email) { Checked = p.Active };
                    item.Click += delegate { SwitchInBackground(id, p.Remark); };
                    menu.Items.Add(item);
                }
            }
            catch { }
            menu.Items.Add(new ToolStripSeparator());
            var auto = new ToolStripMenuItem("开机自动启动") { Checked = Settings.AutoStart };
            auto.Click += delegate { try { Settings.Set("autoStart", !Settings.AutoStart); } catch (Exception ex) { Balloon("设置失败", ex.Message); } };
            menu.Items.Add(auto);
            var keep = new ToolStripMenuItem("关闭窗口后后台运行") { Checked = Settings.Bool("trayMode") };
            keep.Click += delegate { Settings.Set("trayMode", !Settings.Bool("trayMode")); };
            menu.Items.Add(keep);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Quit(); });
        }

        static string Mask(string s)
        {
            return System.Text.RegularExpressions.Regex.Replace(s ?? "", @"([A-Za-z0-9._%+\-]+)@([A-Za-z0-9\-]+)((?:\.[A-Za-z0-9\-]+)+)", m =>
            {
                var u = m.Groups[1].Value; var h = m.Groups[2].Value;
                var uu = u.Length <= 2 ? u.Substring(0, 1) + "*" : u.Length <= 4 ? u.Substring(0, 1) + "**" + u.Substring(u.Length - 1) : u.Substring(0, 2) + "****" + u.Substring(u.Length - 2);
                return uu + "@" + h.Substring(0, 1) + "***" + m.Groups[3].Value;
            });
        }

        static void SwitchInBackground(string id, string remark)
        {
            new Thread(() =>
            {
                try { DoSwitch(id); Balloon("切换成功", "Claude 桌面 App 已切换到「" + (Settings.Bool("maskDefault") ? Mask(remark) : remark) + "」"); }
                catch (Exception ex) { Balloon("切换失败", ex.Message); }
            }) { IsBackground = true }.Start();
        }

        static void Balloon(string title, string text)
        {
            if (tray == null || !Settings.Bool("notify")) return;
            Action a = () => tray.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);
            if (ui != null) ui.Post(_ => a(), null); else a();
        }

        static DateTime lastShow = DateTime.MinValue;

        // 已有窗口（包括后台预备好的隐藏窗口）就直接显示；没有才新开（双击 / 连点只算一次）
        internal static void ShowUi() { if (ui != null) ui.Post(_ => ShowWindow(), null); else ShowWindow(); }

        static void ShowWindow()
        {
            windowEverOpened = true;
            if (WebUi.Available) { try { WebUi.Show(Url); return; } catch (Exception ex) { OpLog.Add("界面组件", "WebView2", false, ex.Message, 0); WebUi.Failed = true; } }
            if (FocusExisting()) return;
            // 隐藏窗口正在准备中：稍等它就绪再显示，避免再开一个
            for (int i = 0; i < 25 && preparing; i++) { Application.DoEvents(); Thread.Sleep(120); }
            if (FocusExisting()) return;
            if ((DateTime.Now - lastShow).TotalSeconds < 4) return; // 窗口正在打开
            lastShow = DateTime.Now;
            LastPing = DateTime.Now;
            OpenWindow(Url);
            SizeNewWindow();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
        delegate bool EnumProc(IntPtr h, IntPtr p);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr h, int cmd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

        // 找到已打开的 Claude Tools 窗口（Edge 应用窗口，标题等于页面标题）；includeHidden 时包括后台预备的隐藏窗口
        static IntPtr FindAppWindow(bool includeHidden = false)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, p) =>
            {
                if (!includeHidden && !IsWindowVisible(h)) return true;
                var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
                if (sb.ToString() != "Claude Tools") return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                try { if (!Process.GetProcessById((int)pid).ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase)) return true; } catch { return true; }
                found = h; return false;
            }, IntPtr.Zero);
            return found;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        // 新窗口出现后，直接设成默认大小并居中（不受 Edge 记住的旧尺寸影响）
        static void SizeNewWindow()
        {
            new Thread(() =>
            {
                for (int i = 0; i < 40; i++)
                {
                    Thread.Sleep(200);
                    var h = FindAppWindow();
                    if (h == IntPtr.Zero) continue;
                    var r = DefaultBounds();
                    if (IsIconic(h)) ShowWindowAsync(h, 9);
                    SetWindowPos(h, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, 0x0004 | 0x0010); // NOZORDER | NOACTIVATE
                    return;
                }
            }) { IsBackground = true }.Start();
        }

        static bool FocusExisting()
        {
            var h = FindAppWindow(true);
            if (h == IntPtr.Zero) return false;
            if (!IsWindowVisible(h))
            {
                // 后台预备的隐藏窗口：放到屏幕中间再显示
                var r = DefaultBounds();
                SetWindowPos(h, IntPtr.Zero, r.Left, r.Top, r.Width - 1, r.Height, 0x0004 | 0x0040); // NOZORDER | SHOWWINDOW
                ShowWindowAsync(h, 5); // SW_SHOW
                SetWindowPos(h, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, 0x0004);      // 大小变化一次，让 Edge 立即重绘
            }
            if (IsIconic(h)) ShowWindowAsync(h, 9); // SW_RESTORE
            SetForegroundWindow(h);
            return true;
        }

        // ---------- 后台预备窗口 ----------
        static volatile bool preparing;
        static DateTime lastPrepare = DateTime.MinValue;
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);

        // 本工具专用 Edge（数据目录是 ClaudeSwitcher\webview）的所有进程
        static HashSet<int> WebviewPids()
        {
            var set = new HashSet<int>();
            try
            {
                var marker = Path.Combine(Settings.StateDir, "webview");
                using (var s = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedge.exe'"))
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        var cl = Convert.ToString(o["CommandLine"]);
                        if (cl != null && cl.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) set.Add(Convert.ToInt32(o["ProcessId"]));
                    }
            }
            catch { }
            return set;
        }

        static IntPtr FindWebviewWindow(HashSet<int> pids)
        {
            IntPtr found = IntPtr.Zero;
            if (pids.Count == 0) return found;
            EnumWindows((h, p) =>
            {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains((int)pid)) return true;
                var cls = new StringBuilder(64); GetClassName(h, cls, 64);
                if (cls.ToString() != "Chrome_WidgetWin_1") return true;
                var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
                if (sb.Length == 0) return true;
                found = h; return false;
            }, IntPtr.Zero);
            return found;
        }

        static void PrepareStandby()
        {
            if (preparing || ui == null || (DateTime.Now - lastPrepare).TotalSeconds < 20) return;
            if (FindAppWindow(true) != IntPtr.Zero) return;
            if (FindWebviewWindow(WebviewPids()) != IntPtr.Zero) return;
            preparing = true; lastPrepare = DateTime.Now;
            new Thread(() =>
            {
                try
                {
                    OpenWindow(Url, true);
                    for (int i = 0; i < 60; i++)
                    {
                        Thread.Sleep(150);
                        var h = FindWebviewWindow(WebviewPids());
                        if (h == IntPtr.Zero) continue;
                        ShowWindowAsync(h, 0); // SW_HIDE：在屏幕外出现后立刻隐藏，用户看不到
                        break;
                    }
                }
                catch { }
                finally { preparing = false; }
            }) { IsBackground = true }.Start();
        }

        internal static void Quit()
        {
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            WebUi.Close();
            CloseUiWindow();
            Environment.Exit(0);
        }

        // 退出时一并关掉界面窗口：只结束本工具专用的 Edge 进程（数据目录含 ClaudeSwitcher\webview），不碰你自己的浏览器
        static void CloseUiWindow()
        {
            try
            {
                var marker = Path.Combine(Settings.StateDir, "webview");
                using (var s = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedge.exe'"))
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        var cl = Convert.ToString(o["CommandLine"]);
                        if (cl != null && cl.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                            try { Process.GetProcessById(Convert.ToInt32(o["ProcessId"])).Kill(); } catch { }
                    }
            }
            catch { }
        }

        // ---------------- 窗口 ----------------
        internal static double DpiScale()
        {
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96.0; } catch { return 1; }
        }
        // 默认窗口：最大 1400×900（按系统缩放），不超过屏幕的 88% × 90%，居中
        internal static Rectangle DefaultBounds()
        {
            var wa = Screen.PrimaryScreen.WorkingArea; var s = DpiScale();
            int w = Math.Min((int)(1400 * s), (int)(wa.Width * 0.88)), h = Math.Min((int)(900 * s), (int)(wa.Height * 0.9));
            return new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
        }
        // 按系统显示缩放计算窗口的物理像素大小，避免窗口先小后大
        static string WindowArgs()
        {
            var r = DefaultBounds(); var s = DpiScale();
            return "--window-size=" + (int)(r.Width / s) + "," + (int)(r.Height / s) + " --window-position=" + (int)(r.Left / s) + "," + (int)(r.Top / s);
        }

        // 关闭窗口后不让 Edge 继续在后台运行
        static void DisableEdgeBackgroundMode(string profile)
        {
            try
            {
                Directory.CreateDirectory(profile);
                var ls = Path.Combine(profile, "Local State");
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var d = File.Exists(ls) ? json.Deserialize<Dictionary<string, object>>(File.ReadAllText(ls, Encoding.UTF8)) : new Dictionary<string, object>();
                d["background_mode"] = new Dictionary<string, object> { { "enabled", false } };
                File.WriteAllText(ls, json.Serialize(d), new UTF8Encoding(false));
            }
            catch { }
        }

        static Process OpenWindow(string url, bool hidden = false)
        {
            var edge = new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe")
            }.FirstOrDefault(File.Exists);
            if (edge == null) { Process.Start(url); return null; }
            var profile = Path.Combine(Settings.StateDir, "webview");
            var winArgs = WindowArgs();
            // 后台预备窗口：放在屏幕外创建，出现后立即隐藏
            if (hidden) winArgs = System.Text.RegularExpressions.Regex.Replace(winArgs, "--window-position=\\S+", "--window-position=-32000,-32000");
            var args = "--app=\"" + url + "\" --user-data-dir=\"" + profile + "\" " + winArgs + " " +
                       "--no-first-run --no-default-browser-check --lang=zh-CN " +
                       // 精简：这个专用窗口用不到扩展、翻译、侧边栏、同步、后台联网等，关掉以省内存
                       "--disable-extensions --disable-component-update --disable-background-networking --disable-sync --no-pings " +
                       "--disable-default-apps --disable-breakpad --disable-crash-reporter --renderer-process-limit=2 --disable-gpu-shader-disk-cache " +
                       // 界面很简单，用 CPU 绘制即可，省掉 GPU 进程的内存
                       "--disable-gpu --disable-gpu-compositing " +
                       "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-background-timer-throttling " +
                       "--disable-features=Translate,msEdgeSidebarV2,msEdgeShopping,EdgeCollections,msUndersideButton,msEdgeEnhanceImages,msImplicitSignin,AutofillServerCommunication,MediaRouter,OptimizationHints,CalculateNativeWinOcclusion";
            DisableEdgeBackgroundMode(profile);
            return Process.Start(new ProcessStartInfo(edge, args) { UseShellExecute = false });
        }

        // ---------------- HTTP ----------------
        static void StartServer(string urlFile)
        {
            // 固定令牌 + 优先固定端口：程序重启后，已打开的窗口能自动重新连上
            var tokenFile = Path.Combine(Settings.StateDir, "token.txt");
            try { Token = File.Exists(tokenFile) ? File.ReadAllText(tokenFile).Trim() : ""; } catch { Token = ""; }
            if (Token.Length < 32) { Token = Guid.NewGuid().ToString("N"); File.WriteAllText(tokenFile, Token); }
            HttpListener http = null; int port = 0;
            // 测试实例（自定义数据目录）只用随机端口，不占正式端口
            bool testInstance = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_SWITCHER_LOCALAPPDATA")) || Environment.GetEnvironmentVariable("CLAUDE_SWITCHER_TEST") == "1";
            foreach (var p in (testInstance ? new int[0] : Enumerable.Range(38517, 10)).Concat(new[] { 0 }))
            {
                port = p;
                if (p == 0) { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
                var h = new HttpListener();
                h.Prefixes.Add("http://localhost:" + port + "/");
                try { h.Start(); http = h; break; } catch { try { h.Close(); } catch { } }
            }
            if (http == null) throw new Exception("无法启动本地服务");
            Url = "http://localhost:" + port + "/?t=" + Token;
            File.WriteAllText(urlFile, Url);
            new Thread(() =>
            {
                while (http.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = http.GetContext(); } catch { return; }
                    ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
                }
            }) { IsBackground = true }.Start();
        }

        static void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            try
            {
                var path = req.Url.AbsolutePath;
                if (path == "/" || path == "/index.html")
                {
                    if (req.QueryString["t"] != Token) { Send(res, 403, "text/plain", "forbidden"); return; }
                    Send(res, 200, "text/html; charset=utf-8", LoadUi());
                    return;
                }
                if (path == "/favicon.ico") { SendBytes(res, 200, "image/x-icon", LoadRes("app.ico")); return; }
                if (path == "/logo.png") { SendBytes(res, 200, "image/png", LoadRes("logo.png")); return; }
                if (path == "/font.woff2") { res.Headers["Cache-Control"] = "max-age=31536000"; SendBytes(res, 200, "font/woff2", LoadRes("font.woff2")); return; }
                if (!path.StartsWith("/api/") || req.Headers["X-Token"] != Token || req.HttpMethod != "POST")
                {
                    Send(res, 403, "text/plain", "forbidden"); return;
                }
                string body;
                using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
                var a = body.Length > 0 ? Json.Deserialize<Dictionary<string, object>>(body) : new Dictionary<string, object>();
                object result = Api(path.Substring(5), a ?? new Dictionary<string, object>());
                Send(res, 200, "application/json; charset=utf-8", Json.Serialize(new Dictionary<string, object> { { "ok", true }, { "data", result } }));
            }
            catch (Exception ex)
            {
                try { Send(res, 200, "application/json; charset=utf-8", Json.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } })); } catch { }
            }
        }

        static string S(Dictionary<string, object> a, string k)
        {
            object v; return a.TryGetValue(k, out v) && v != null ? v.ToString() : "";
        }

        static string DesktopName(string id)
        {
            var p = DesktopStore.Load().FirstOrDefault(x => x.Id == id);
            return p == null ? id : p.Remark;
        }

        static string CliName(string id)
        {
            var c = Store.Load().FirstOrDefault(x => x.Folder == id);
            return c == null ? id : c.Remark;
        }

        // 记录日志的执行包装
        static void Logged(string action, string target, Action act, Func<string> detail = null)
        {
            var sw = Stopwatch.StartNew();
            try { act(); OpLog.Add(action, target, true, detail != null ? detail() : "", sw.ElapsedMilliseconds); }
            catch (Exception ex) { OpLog.Add(action, target, false, ex.Message, sw.ElapsedMilliseconds); throw; }
        }

        static void DoSwitch(string id)
        {
            var cur = DesktopStore.Load().FirstOrDefault(x => x.Active);
            var target = DesktopName(id);
            Logged("切换桌面账号", (cur != null ? cur.Remark : "?") + " → " + target, () => DesktopStore.SwitchTo(id), () => DesktopStore.LastSync);
        }

        static object Api(string name, Dictionary<string, object> a)
        {
            switch (name)
            {
                case "ui/show": ShowUi(); return null;
                case "proxy/get": return ProxyPool.Get();
                case "proxy/save": { var r = ProxyPool.Save(S(a, "cfg")); OpLog.Add("代理设置", "保存", true, "代理池 " + ((System.Collections.ICollection)r["items"]).Count + " 个", 0); return r; }
                case "proxy/import": return ProxyPool.Import(S(a, "text"));
                case "proxy/test": return ProxyPool.Test(S(a, "id"));
                case "proxy/testUrl": return ProxyPool.TestUrl(S(a, "url"));
                case "ui/theme": if (WebUi.Available) WebUi.SetTheme(S(a, "dark") != "0"); return null;
                case "ping": LastPing = DateTime.Now; windowEverOpened = true; return File.GetLastWriteTimeUtc(Application.ExecutablePath).Ticks.ToString();
                case "state": LastPing = DateTime.Now; windowEverOpened = true; return State();

                case "desktop/switch": DoSwitch(S(a, "id")); return State();
                case "desktop/best":
                    {
                        var best = BestAccount();
                        if (best == null) throw new Exception("没有可以切换的其他账号");
                        DoSwitch(best.Id); return State();
                    }
                case "desktop/add":
                    Logged("添加桌面账号", S(a, "remark"), () => DesktopStore.AddNew(S(a, "remark")), () => DesktopStore.LastSync); return State();
                case "desktop/rename":
                    {
                        var old = DesktopName(S(a, "id"));
                        Logged("改备注", old + " → " + S(a, "remark"), () => DesktopStore.Rename(S(a, "id"), S(a, "remark"))); return State();
                    }
                case "desktop/delete":
                    Logged("删除桌面账号", DesktopName(S(a, "id")), () => DesktopStore.Delete(S(a, "id"))); return State();
                case "desktop/open": DesktopStore.OpenDir(S(a, "id")); return null;
                case "desktop/merge":
                    {
                        string msg = "";
                        Logged("合并会话记录", "所有桌面账号", () => { msg = DesktopStore.MergeAll(); }, () => msg);
                        var st = (Dictionary<string, object>)State(); st["mergeResult"] = msg; return st;
                    }
                case "desktop/launch": Logged("打开桌面 App", "", DesktopStore.LaunchDesktop); return State();

                case "cli/add": Logged("添加终端账号", S(a, "remark"), () => Store.Create(S(a, "remark"))); return State();
                case "cli/rename":
                    {
                        var acc = Store.Load().First(x => x.Folder == S(a, "id"));
                        if (acc.IsDefault) throw new Exception("原始账号不能改备注");
                        Logged("改备注", acc.Remark + " → " + S(a, "remark"), () => Store.SetRemark(acc, S(a, "remark"))); return State();
                    }
                case "cli/delete":
                    {
                        var acc = Store.Load().First(x => x.Folder == S(a, "id"));
                        Logged("删除终端账号", acc.Remark, () => Store.Delete(acc)); return State();
                    }
                case "cli/repair": Logged("修复共享链接", "", Store.RepairAll); return State();
                case "cli/launch":
                    {
                        var mode = S(a, "mode");
                        var label = mode == "resume" ? "（选历史）" : mode == "continue" ? "（继续上次）" : "（新对话）";
                        Logged("启动终端", CliName(S(a, "id")) + label + " @ " + S(a, "dir"), () => CliLaunch(S(a, "id"), S(a, "dir"), mode));
                        return State();
                    }
                case "pickDir": return PickDir(S(a, "start"));
                case "cli/status": return CliStatus(true);
                case "cli/usage": return CliUsage(S(a, "id"));
                case "desktop/usage": return DesktopUsage(S(a, "id"));
                case "cli/install": Logged("安装 Claude Code", "官方安装脚本", InstallCli); return null;
                case "openPath":
                    {
                        var p = S(a, "path");
                        if (!Directory.Exists(p)) throw new Exception("目录不存在：" + p);
                        Process.Start("explorer.exe", "\"" + p + "\""); return null;
                    }

                case "sessions": return SessionList();
                case "stats": return TokenStats.Query(a, S);
                case "pricing": return Pricing.All();
                case "pricing/set":
                    {
                        var list = ((System.Collections.ArrayList)a["list"]).Cast<Dictionary<string, object>>().Select(d => new Price {
                            Key = (d["Key"] ?? "").ToString().Trim(), Input = Convert.ToDouble(d["Input"]), Output = Convert.ToDouble(d["Output"]),
                            CacheRead = Convert.ToDouble(d["CacheRead"]), CacheWrite5m = Convert.ToDouble(d["CacheWrite5m"]), CacheWrite1h = Convert.ToDouble(d["CacheWrite1h"])
                        }).Where(p => p.Key.Length > 0).ToList();
                        Pricing.Save(list); Logged("修改定价", list.Count + " 个模型", () => { }); return Pricing.All();
                    }
                case "pricing/reset": Pricing.Reset(); return Pricing.All();
                case "storage/scan": return StorageScan.Scan();
                case "cache/scan": return CacheCleaner.Scan();
                case "cache/clean":
                    {
                        var ids = ((System.Collections.ArrayList)a["ids"]).Cast<object>().Select(o => o.ToString()).ToList();
                        string msg = "";
                        Logged("清理缓存", ids.Count + " 项", () => { msg = CacheCleaner.Clean(ids); }, () => msg);
                        var r = CacheCleaner.Scan(); r["message"] = msg; return r;
                    }
                case "storage/clean":
                    {
                        long freed = 0; var id = S(a, "id");
                        Logged("清理缓存", id == "" ? "全部已保存账号" : DesktopName(id), () => { freed = StorageScan.CleanCache(id); }, () => "释放 " + (freed / 1048576.0).ToString("0.0") + " MB");
                        return StorageScan.Scan();
                    }
                case "i18n/s2t": return S2TMap();
                case "logs": return OpLog.Read(500);
                case "logs/clear": OpLog.Clear(); return null;
                case "logs/add":
                    {
                        // 界面上的刷新 / 查询额度操作写入操作日志（只接受这几类，防止乱写）
                        var act = S(a, "action");
                        if (act != "刷新全部数据" && act != "查询官方额度" && act != "自动查询官方额度") throw new Exception("不支持的日志类型");
                        long ms; long.TryParse(S(a, "ms"), out ms);
                        OpLog.Add(act, S(a, "target"), S(a, "ok") != "0", S(a, "detail"), ms);
                        return null;
                    }
                case "settings": return Settings.All();
                case "settings/set": Settings.Set(S(a, "key"), a.ContainsKey("value") ? a["value"] : null); return Settings.All();
                case "quit": new Thread(() => { Thread.Sleep(300); Quit(); }).Start(); return null;
            }
            throw new Exception("未知接口：" + name);
        }

        static DesktopProfile BestAccount()
        {
            // 7 天用量最低、其次 5 小时用量最低的非当前账号
            return DesktopStore.Load().Where(p => !p.Active)
                .Select(p => new { p, q = QuotaReader.Read(p.Dir) })
                .OrderBy(x => x.q == null || x.q.SevenDay < 0 ? 999 : x.q.SevenDay)
                .ThenBy(x => x.q == null || x.q.FiveHour < 0 ? 999 : x.q.FiveHour)
                .Select(x => x.p).FirstOrDefault();
        }

        static object QuotaJson(Quota q)
        {
            if (q == null) return null;
            return new Dictionary<string, object> {
                { "fiveHour", q.FiveHour }, { "sevenDay", q.SevenDay }, { "t", q.T }, { "fiveReset", q.FiveReset }, { "sevenReset", q.SevenReset },
                { "recent", q.Recent.Select(r => new[] { r[0], r[1], r[2] }).ToList() },
                { "samples", q.Samples }
            };
        }

        static object State()
        {
            var profiles = DesktopStore.Load();
            var desktop = profiles.Select(p => new Dictionary<string, object> {
                { "id", p.Id }, { "remark", p.Remark }, { "email", p.Email ?? "" }, { "active", p.Active },
                { "lastUsed", p.LastUsed.ToString("yyyy/MM/dd HH:mm") }, { "sessions", p.Sessions }, { "dir", p.Dir }, { "loggedIn", p.LoggedIn },
                { "quota", QuotaJson(QuotaReader.Read(p.Dir)) }, { "live", LiveUsage.Available(p.Dir) }
            }).ToList();
            var cli = Store.Load().Select(c => new Dictionary<string, object> {
                { "id", c.Folder }, { "remark", c.Remark }, { "email", c.Email ?? "" }, { "loggedIn", c.LoggedIn },
                { "isDefault", c.IsDefault }, { "dir", c.Dir }
            }).ToList();
            var best = BestAccount();
            return new Dictionary<string, object> {
                { "desktop", desktop }, { "desktopRunning", DesktopStore.IsRunning() },
                { "cli", cli }, { "recent", ProjectDirs() }, { "cliStatus", CliStatus(false) },
                { "best", best == null ? null : best.Id },
                { "switchesToday", OpLog.CountToday("切换桌面账号") },
                { "settings", Settings.All() }, { "lastSync", DesktopStore.LastSync }, { "build", File.GetLastWriteTimeUtc(Application.ExecutablePath).Ticks.ToString() }, { "now", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds }
            };
        }

        static object SessionList()
        {
            return Sessions.All().Take(1000).Select(s => new Dictionary<string, object> {
                { "id", s.Id }, { "cli", s.CliId ?? "" }, { "title", s.Title ?? "" }, { "model", s.Model ?? "" },
                { "cwd", s.Cwd ?? "" }, { "account", s.Account ?? "" }, { "profile", s.Profile ?? "" },
                { "created", s.Created }, { "last", s.LastActivity }, { "archived", s.Archived }
            }).ToList();
        }

        // ---------- Claude Code（claude 命令）检测与安装 ----------
        static string cliPath; static string cliVersion = ""; static DateTime cliChecked = DateTime.MinValue;
        static readonly object CliLock = new object();

        static string FindClaude()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var cands = new List<string> { Path.Combine(home, @".local\bin\claude.exe") };
            // 同时读取最新的用户 / 系统 PATH（刚安装完时本进程的环境变量还是旧的）
            var paths = string.Join(";", new[] {
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) });
            foreach (var p in paths.Split(';'))
            {
                var d = p.Trim().Trim('"'); if (d.Length == 0) continue;
                foreach (var n in new[] { "claude.exe", "claude.cmd" }) cands.Add(Path.Combine(d, n));
            }
            return cands.FirstOrDefault(c => { try { return File.Exists(c); } catch { return false; } });
        }

        static Dictionary<string, object> CliStatus(bool force)
        {
            lock (CliLock)
            {
                if (force || (DateTime.Now - cliChecked).TotalSeconds > 30)
                {
                    var p = FindClaude();
                    if (p != cliPath || force)
                    {
                        cliVersion = "";
                        if (p != null)
                            try
                            {
                                var psi = new ProcessStartInfo(p, "--version") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                                using (var pr = Process.Start(psi)) { var o = pr.StandardOutput.ReadToEnd(); pr.WaitForExit(8000); cliVersion = o.Trim(); }
                            }
                            catch { }
                    }
                    cliPath = p; cliChecked = DateTime.Now;
                }
                return new Dictionary<string, object> { { "installed", cliPath != null }, { "path", cliPath ?? "" }, { "version", cliVersion } };
            }
        }

        // 打开一个 PowerShell 窗口运行官方安装命令，过程对用户可见
        static void InstallCli()
        {
            var cmd = "$host.UI.RawUI.WindowTitle='安装 Claude Code'; Write-Host '正在安装 Claude Code（官方安装脚本 https://claude.ai/install.ps1）...' -ForegroundColor Cyan; " +
                      "irm https://claude.ai/install.ps1 | iex; Write-Host ''; Write-Host '安装结束。回到 Claude Tools 点「重新检测」即可使用。' -ForegroundColor Green";
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoExit -NoProfile -ExecutionPolicy Bypass -Command \"" + cmd.Replace("\"", "\\\"") + "\"") { UseShellExecute = true });
        }

        // ---------- 官方实时余量 ----------
        // 让 Claude Code 自己执行 /usage（它自己处理登录令牌，本工具从不读取或解密任何令牌）
        // 简体 -> 繁体字符对照表（用 Windows 自带的 LCMapStringEx，不需要额外数据）
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int LCMapStringEx(string locale, uint flags, string src, int srcLen, StringBuilder dest, int destLen, IntPtr ver, IntPtr res, IntPtr p);
        static Dictionary<string, string> s2t;
        static object S2TMap()
        {
            if (s2t != null) return s2t;
            var sb = new StringBuilder(); for (int c = 0x4E00; c <= 0x9FA5; c++) sb.Append((char)c);
            var srcs = sb.ToString(); var dst = new StringBuilder(srcs.Length + 64);
            int n = LCMapStringEx("zh-CN", 0x04000000, srcs, srcs.Length, dst, dst.Capacity, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            var d = new Dictionary<string, string>(); var o = dst.ToString();
            if (n == srcs.Length && o.Length >= srcs.Length) for (int i = 0; i < srcs.Length; i++) if (o[i] != srcs[i]) d[srcs[i].ToString()] = o[i].ToString();
            s2t = d; return d;
        }

        static Dictionary<string, object> CliUsage(string id)
        {
            var acc = Store.Load().FirstOrDefault(x => x.Folder == id);
            if (acc == null) throw new Exception("找不到这个账号");
            if (!acc.LoggedIn) throw new Exception("这个终端账号还没有登录，请先点「新对话」登录一次");
            return RunUsage(acc.IsDefault ? null : acc.Dir, acc.Remark, ProxyPool.For("c:" + acc.Folder, "cli"));
        }

        // 桌面账号：用它自己已有的登录只读查询官方用量（见 Live.cs）
        static Dictionary<string, object> DesktopUsage(string id)
        {
            var p = DesktopStore.Load().FirstOrDefault(x => x.Id == id);
            if (p == null) throw new Exception("找不到这个账号");
            return LiveUsage.Cached(p.Id, () => LiveUsage.Query(p.Dir, ProxyPool.For("d:" + p.Id, "quota")));
        }
        static readonly object UsageLock = new object();
        static Dictionary<string, object> RunUsage(string configDir, string name, string proxy = null)
        {
            var st = CliStatus(false); if (!(bool)st["installed"]) st = CliStatus(true);
            if (!(bool)st["installed"]) throw new Exception("这台电脑还没有安装 Claude Code");
            var psi = new ProcessStartInfo((string)st["path"], "-p \"/usage\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = Settings.StateDir };
            if (configDir == null) psi.EnvironmentVariables.Remove("CLAUDE_CONFIG_DIR"); else psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = configDir;
            if (!string.IsNullOrEmpty(proxy)) { psi.EnvironmentVariables["HTTPS_PROXY"] = proxy; psi.EnvironmentVariables["HTTP_PROXY"] = proxy; }
            string output;
            using (var p = Process.Start(psi))
            {
                var errTask = p.StandardError.ReadToEndAsync();
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(75000)) { try { p.Kill(); } catch { } throw new Exception("查询超时（75 秒），请稍后重试"); }
                output = outTask.Result;
            }
            var ses = System.Text.RegularExpressions.Regex.Match(output, @"Current session:\s*(\d+)%\s*used[^\n]*?resets\s+([^\n(]+?)\s*\(");
            var wk = System.Text.RegularExpressions.Regex.Match(output, @"Current week \(all models\):\s*(\d+)%\s*used[^\n]*?resets\s+([^\n(]+?)\s*\(");
            if (!ses.Success && !wk.Success) throw new Exception("没有读到用量（登录可能已失效，请重新连接）");
            Func<System.Text.RegularExpressions.Match, Dictionary<string, object>> f = m => m.Success
                ? new Dictionary<string, object> { { "used", int.Parse(m.Groups[1].Value) }, { "resets", m.Groups[2].Value.Trim() } } : null;
            return new Dictionary<string, object> { { "session", f(ses) }, { "week", f(wk) }, { "at", DateTime.Now.ToString("HH:mm:ss") } };
        }

        static void CliLaunch(string id, string dir, string mode)
        {
            var acc = Store.Load().FirstOrDefault(x => x.Folder == id);
            if (acc == null) throw new Exception("找不到这个账号");
            dir = (dir ?? "").Trim().Trim('"');
            if (dir.Length == 0 || !Directory.Exists(dir)) throw new Exception("请先选择一个存在的项目文件夹");
            var extra = mode == "resume" ? " --resume" : mode == "continue" ? " --continue" : "";
            var title = ("Claude - " + acc.Remark).Replace("&", "").Replace("|", "").Replace("^", "");
            var st = CliStatus(false);
            if (!(bool)st["installed"]) st = CliStatus(true);
            if (!(bool)st["installed"]) throw new Exception("这台电脑还没有安装 Claude Code（claude 命令），请先点「一键安装」");
            var exe = "\"" + (string)st["path"] + "\"";
            var psi = new ProcessStartInfo("cmd.exe", "/k title " + title + " && " + exe + extra) { UseShellExecute = false, WorkingDirectory = dir };
            if (acc.IsDefault) psi.EnvironmentVariables.Remove("CLAUDE_CONFIG_DIR");
            else psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = acc.Dir;
            // 这个终端账号绑定 / 分配到的代理
            var px = ProxyPool.For("c:" + acc.Folder, "cli");
            if (px != null) { psi.EnvironmentVariables["HTTPS_PROXY"] = px; psi.EnvironmentVariables["HTTP_PROXY"] = px; }
            Process.Start(psi);
            Store.SaveRecent(dir);
        }

        // 新版文件夹选择框（IFileOpenDialog + FOS_PICKFOLDERS），可粘贴路径，始终在最前
        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogRCW { }
        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [System.Runtime.InteropServices.PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint c, IntPtr f); void SetFileTypeIndex(uint i); void GetFileTypeIndex(out uint i);
            void Advise(IntPtr p, out uint c); void Unadvise(uint c); void SetOptions(uint fos); void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem si); void SetFolder(IShellItem si); void GetFolder(out IShellItem si); void GetCurrentSelection(out IShellItem si);
            void SetFileName([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string n); void GetFileName(out IntPtr n);
            void SetTitle([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string t);
            void SetOkButtonLabel([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string t);
            void SetFileNameLabel([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string t);
            void GetResult(out IShellItem si);
        }
        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv); void GetParent(out IShellItem si);
            void GetDisplayName(uint sigdn, out IntPtr name); void GetAttributes(uint mask, out uint attrs); void Compare(IShellItem si, uint hint, out int order);
        }
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        // 由后台进程弹出的窗口默认拿不到焦点；模拟一次 Alt 键解除系统的前台锁定
        static void ForceForeground(IntPtr h)
        {
            keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(h);
        }

        static string PickDir(string start)
        {
            string result = "";
            var t = new Thread(() =>
            {
                using (var owner = new Form { TopMost = true, ShowInTaskbar = false, Width = 1, Height = 1, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.CenterScreen, Opacity = 0 })
                {
                    owner.Show(); ForceForeground(owner.Handle);
                    try
                    {
                        var dlg = (IFileDialog)new FileOpenDialogRCW();
                        uint opts; dlg.GetOptions(out opts);
                        dlg.SetOptions(opts | 0x20 | 0x40); // FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM
                        dlg.SetTitle("选择项目文件夹");
                        dlg.SetOkButtonLabel("选择此文件夹");
                        if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
                        {
                            var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"); IShellItem si;
                            try { SHCreateItemFromParsingName(start, IntPtr.Zero, ref iid, out si); dlg.SetFolder(si); } catch { }
                        }
                        if (dlg.Show(owner.Handle) == 0)
                        {
                            IShellItem item; dlg.GetResult(out item);
                            IntPtr p; item.GetDisplayName(0x80058000, out p); // SIGDN_FILESYSPATH
                            result = System.Runtime.InteropServices.Marshal.PtrToStringUni(p);
                            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p);
                        }
                    }
                    catch
                    {
                        using (var old = new FolderBrowserDialog { Description = "选择项目文件夹", ShowNewFolderButton = true })
                        {
                            if (Directory.Exists(start)) old.SelectedPath = start;
                            if (old.ShowDialog(owner) == DialogResult.OK) result = old.SelectedPath;
                        }
                    }
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start(); t.Join();
            return result;
        }

        // 常用项目文件夹：最近启动过的 + 桌面 App 会话里用过的（不含临时会话）
        static List<string> ProjectDirs()
        {
            var list = new List<string>(Store.LoadRecent());
            try
            {
                foreach (var s in Sessions.All())
                {
                    var c = s.Cwd ?? "";
                    if (c.Length == 0 || c.IndexOf("\\scratch-workspaces\\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (list.Any(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase))) continue;
                    if (Directory.Exists(c)) list.Add(c);
                }
            }
            catch { }
            return list.Take(12).ToList();
        }
        // ---------------- 资源 ----------------
        static string uiCache;
        static string LoadUi()
        {
            var dev = Environment.GetEnvironmentVariable("CLAUDE_SWITCHER_UI");
            if (!string.IsNullOrEmpty(dev) && File.Exists(dev)) return File.ReadAllText(dev, Encoding.UTF8);
            if (uiCache != null) return uiCache;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.html"))
            using (var r = new StreamReader(s, Encoding.UTF8)) return uiCache = r.ReadToEnd();
        }

        static byte[] LoadRes(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) return new byte[0];
                var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
            }
        }

        static void Send(HttpListenerResponse res, int code, string type, string text)
        {
            SendBytes(res, code, type, Encoding.UTF8.GetBytes(text));
        }

        static void SendBytes(HttpListenerResponse res, int code, string type, byte[] data)
        {
            res.StatusCode = code; res.ContentType = type;
            res.Headers["Cache-Control"] = "no-store";
            res.ContentLength64 = data.Length;
            res.OutputStream.Write(data, 0, data.Length);
            res.OutputStream.Close();
        }
    }
}
