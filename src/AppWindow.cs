// 界面窗口：工具自己的窗口，里面嵌入 Windows 自带的 WebView2 网页组件显示 ui.html。
// 点 X 时只隐藏窗口（开启后台运行时），再点托盘图标直接显示同一个窗口——不重新加载、不闪烁。
// 没有 WebView2 运行时的电脑会自动退回到 Edge 应用窗口方式（见 Server.cs）。
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeSwitcher
{
    static class WebUi
    {
        static AppWindow form;
        static bool? available;
        public static bool Failed;

        // WebView2 的两个托管库内嵌在 exe 里，用到时从资源加载
        public static void Init()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var n = new AssemblyName(e.Name).Name;
                if (n != "Microsoft.Web.WebView2.Core" && n != "Microsoft.Web.WebView2.WinForms") return null;
                var b = Res(n + ".dll");
                return b == null ? null : Assembly.Load(b);
            };
        }

        static byte[] Res(string name)
        {
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (st == null) return null;
                var b = new byte[st.Length]; int r = 0;
                while (r < b.Length) { int k = st.Read(b, r, b.Length - r); if (k <= 0) break; r += k; }
                return b;
            }
        }

        // 原生加载器 WebView2Loader.dll 需要放在磁盘上
        static string LoaderDir()
        {
            var arch = Environment.Is64BitProcess ? "x64" : "x86";
            var dir = Path.Combine(Settings.StateDir, "wv2", arch);
            var f = Path.Combine(dir, "WebView2Loader.dll");
            var b = Res("WebView2Loader." + arch + ".dll");
            if (b == null) throw new Exception("缺少 WebView2Loader");
            if (!File.Exists(f) || new FileInfo(f).Length != b.Length) { Directory.CreateDirectory(dir); File.WriteAllBytes(f, b); }
            return dir;
        }

        public static bool Available
        {
            get
            {
                if (available == null) available = Check();
                return available.Value && !Failed;
            }
        }

        static bool Check()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("CLAUDE_SWITCHER_EDGE") == "1") return false;
                return Probe();
            }
            catch (Exception ex) { OpLog.Add("界面组件", "WebView2", false, "不可用，改用 Edge 窗口：" + ex.Message, 0); return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool Probe()
        {
            CoreWebView2Environment.SetLoaderDllFolderPath(LoaderDir());
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Show(string url)
        {
            if (form == null || form.IsDisposed) form = new AppWindow(url);
            form.Reveal();
        }

        // 标题栏跟随界面的深色 / 浅色主题
        public static bool Dark = true;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SetTheme(bool dark)
        {
            Dark = dark;
            var f = form;
            if (f != null && !f.IsDisposed && f.IsHandleCreated) f.BeginInvoke((Action)(() => f.ApplyTitleBar()));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Close()
        {
            try { if (form != null && !form.IsDisposed) { form.AllowClose = true; form.Close(); form.Dispose(); } } catch { }
        }
    }

    class AppWindow : Form
    {
        readonly WebView2 wv;
        public bool AllowClose;
        static readonly Color Bg = Color.FromArgb(20, 16, 13);

        public AppWindow(string url)
        {
            Text = "Claude Tools";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BackColor = Bg;
            StartPosition = FormStartPosition.Manual;
            Bounds = SavedBounds() ?? Program.DefaultBounds();
            var s = Program.DpiScale();
            MinimumSize = new Size((int)(1000 * s), (int)(640 * s));
            wv = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Bg };
            Controls.Add(wv);
            Load += async (o, e) => await Start(url);
        }

        async Task Start(string url)
        {
            try
            {
                var opts = new CoreWebView2EnvironmentOptions("--disable-features=Translate,msEdgeSidebarV2,msEdgeShopping,AutofillServerCommunication,OptimizationHints --disable-background-networking");
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.StateDir, "webview2"), opts);
                await wv.EnsureCoreWebView2Async(env);
                var c = wv.CoreWebView2;
                c.Settings.AreDevToolsEnabled = false;
                c.Settings.AreDefaultContextMenusEnabled = false;
                c.Settings.IsStatusBarEnabled = false;
                c.Settings.IsZoomControlEnabled = false;          // 缩放由界面自己控制
                c.Settings.AreBrowserAcceleratorKeysEnabled = false; // 禁用浏览器自带的刷新 / 查找等快捷键（F5 由界面自己处理）
                c.Settings.IsPasswordAutosaveEnabled = false;
                c.Settings.IsGeneralAutofillEnabled = false;
                c.NewWindowRequested += (o, e) => { e.Handled = true; try { Process.Start(e.Uri); } catch { } }; // 外部链接用默认浏览器打开
                c.DocumentTitleChanged += (o, e) => { var t = c.DocumentTitle; if (!string.IsNullOrEmpty(t) && !t.StartsWith("http")) Text = t; };
                c.Navigate(url);
            }
            catch (Exception ex)
            {
                OpLog.Add("界面组件", "WebView2", false, "启动失败，改用 Edge 窗口：" + ex.Message, 0);
                WebUi.Failed = true;
                AllowClose = true;
                Close();
                Program.ShowUi();
            }
        }

        // ---------- 记住窗口位置和大小（重新打开、重启工具后都保持） ----------
        static string BoundsFile { get { return Path.Combine(Settings.StateDir, "window.txt"); } }
        static Rectangle? SavedBounds()
        {
            try
            {
                if (!File.Exists(BoundsFile)) return null;
                var a = File.ReadAllText(BoundsFile).Split(',');
                var r = new Rectangle(int.Parse(a[0]), int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]));
                // 必须大部分落在某个屏幕内，否则（比如拔了副屏）用默认位置
                foreach (var s in Screen.AllScreens)
                {
                    var i = Rectangle.Intersect(s.WorkingArea, r);
                    if (i.Width * i.Height >= r.Width * r.Height / 2 && r.Width >= 600 && r.Height >= 400) return r;
                }
            }
            catch { }
            return null;
        }
        void SaveBounds()
        {
            try
            {
                var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                if (r.Width < 600 || r.Height < 400) return;
                File.WriteAllText(BoundsFile, r.X + "," + r.Y + "," + r.Width + "," + r.Height);
            }
            catch { }
        }
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
        public void ApplyTitleBar()
        {
            try
            {
                int v = WebUi.Dark ? 1 : 0;
                if (DwmSetWindowAttribute(Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref v, 4); // 深色标题栏（Win10 20H1+ / 旧版）
                SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); // 刷新边框
            }
            catch { }
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyTitleBar(); }
        protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); SaveBounds(); }

        public void Reveal()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.None))
            {
                SaveBounds();
                e.Cancel = true;
                if (Settings.Bool("trayMode")) Hide();   // 后台运行：只隐藏，页面保留
                else Program.Quit();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
