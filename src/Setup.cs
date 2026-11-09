// Claude Tools 安装程序（也兼作卸载程序）。.NET Framework 4 / C# 5，不需要任何第三方工具。
// 用法：双击 = 图形安装向导；Setup.exe /S [/D=安装目录] = 静默安装；Uninstall.exe /uninstall [/S] = 卸载
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Claude Tools 安装程序")]
[assembly: AssemblyProduct("Claude Tools")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

[ComImport, Guid("00021401-0000-0000-C000-000000000046")] class SetupShellLink { }
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
interface ISetupShellLink
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder f, int c, IntPtr p, int fl);
    void GetIDList(out IntPtr p); void SetIDList(IntPtr p);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int c);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string s);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int c);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string s);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int c);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string s);
    void GetHotkey(out short h); void SetHotkey(short h);
    void GetShowCmd(out int i); void SetShowCmd(int i);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int c, out int i);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string s, int i);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string s, int r);
    void Resolve(IntPtr h, int f);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string s);
}

static class Setup
{
    const string AppName = "Claude Tools", Version = "2.0.0", ExeName = "Claude Tools.exe";
    const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaudeTools";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    static string DefaultDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\" + AppName); } }
    static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }
    static string MenuLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }

    [STAThread]
    static int Main(string[] a)
    {
        Application.EnableVisualStyles();
        bool silent = a.Any(x => x.Equals("/S", StringComparison.OrdinalIgnoreCase));
        string dir = null;
        foreach (var x in a) if (x.StartsWith("/D=", StringComparison.OrdinalIgnoreCase)) dir = x.Substring(3).Trim('"');
        try
        {
            if (a.Any(x => x.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
                return Uninstall(silent, dir ?? Path.GetDirectoryName(Application.ExecutablePath));
            if (silent)
            {
                Install(dir ?? DefaultDir, a.Contains("/desktop"), a.Contains("/startmenu"), a.Contains("/autostart"), false);
                return 0;
            }
            Application.Run(new Wizard(dir ?? DefaultDir));
            return 0;
        }
        catch (Exception ex)
        {
            if (!silent) MessageBox.Show(ex.Message, AppName + " 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static void KillRunning(string exePath)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            try { if (string.Equals(p.MainModule.FileName, exePath, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); } } catch { }
        }
        // 安装目录下自带的界面窗口（Edge）由程序退出时一并关闭，这里不动用户自己的浏览器
        System.Threading.Thread.Sleep(500);
    }

    static void MakeShortcut(string lnk, string target, string workDir)
    {
        var l = (ISetupShellLink)new SetupShellLink();
        l.SetPath(target); l.SetWorkingDirectory(workDir); l.SetIconLocation(target, 0); l.SetDescription(AppName);
        ((IPersistFile)l).Save(lnk, true);
    }

    public static void Install(string dir, bool desktop, bool startMenu, bool autoStart, bool launch)
    {
        dir = Path.GetFullPath(dir);
        var exe = Path.Combine(dir, ExeName);
        KillRunning(exe);
        Directory.CreateDirectory(dir);
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.exe"))
        using (var f = File.Create(exe)) s.CopyTo(f);
        var un = Path.Combine(dir, "Uninstall.exe");
        if (!string.Equals(Application.ExecutablePath, un, StringComparison.OrdinalIgnoreCase)) File.Copy(Application.ExecutablePath, un, true);

        if (desktop) MakeShortcut(DesktopLnk, exe, dir); else try { File.Delete(DesktopLnk); } catch { }
        if (startMenu) MakeShortcut(MenuLnk, exe, dir); else try { File.Delete(MenuLnk); } catch { }

        using (var k = Registry.CurrentUser.CreateSubKey(UninstKey))
        {
            k.SetValue("DisplayName", AppName);
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "2014-y");
            k.SetValue("InstallLocation", dir);
            k.SetValue("DisplayIcon", exe);
            k.SetValue("UninstallString", "\"" + un + "\" /uninstall");
            k.SetValue("QuietUninstallString", "\"" + un + "\" /uninstall /S");
            k.SetValue("URLInfoAbout", "https://github.com/2014-y/Claude-Tools");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)((new FileInfo(exe).Length + new FileInfo(un).Length) / 1024), RegistryValueKind.DWord);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (autoStart)
            {
                k.SetValue(AppName, "\"" + exe + "\" --tray");
                try { using (var ap = Registry.CurrentUser.CreateSubKey(ApprovedKey)) ap.SetValue(AppName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary); } catch { }
            }
        }
        if (launch) Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true });
    }

    static int Uninstall(bool silent, string dir)
    {
        if (!silent && MessageBox.Show("确定要卸载 " + AppName + " 吗？\n\n你的账号存档、会话记录和设置不会被删除。", AppName + " 卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
        var exe = Path.Combine(dir, ExeName);
        KillRunning(exe);
        try { File.Delete(DesktopLnk); } catch { }
        try { File.Delete(MenuLnk); } catch { }
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (k != null)
                {
                    var v = k.GetValue(AppName) as string;
                    if (v != null && v.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0) k.DeleteValue(AppName, false);
                }
            using (var ap = Registry.CurrentUser.OpenSubKey(ApprovedKey, true)) if (ap != null) ap.DeleteValue(AppName, false);
        }
        catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); } catch { }
        try { File.Delete(exe); } catch { }
        // 卸载程序自己正在运行，退出后再删除自身和目录
        var un = Path.Combine(dir, "Uninstall.exe");
        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + un + "\" & rmdir \"" + dir + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        if (!silent) MessageBox.Show(AppName + " 已卸载。\n你的账号存档、会话记录和设置仍保留在电脑上，没有删除。", AppName + " 卸载", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }
}

class Wizard : Form
{
    TextBox path; CheckBox cDesk, cMenu, cAuto, cRun; Button ok;

    public Wizard(string dir)
    {
        Text = "安装 Claude Tools"; Font = new Font("Microsoft YaHei UI", 9.5f); ClientSize = new Size(540, 360);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen; BackColor = Color.White;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        var head = new Panel { Left = 0, Top = 0, Width = 540, Height = 84, BackColor = Color.FromArgb(244, 99, 58) };
        head.Controls.Add(new Label { Text = "Claude Tools", ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 17f, FontStyle.Bold), Left = 22, Top = 12, AutoSize = true });
        head.Controls.Add(new Label { Text = "Claude 多账号切换与用量管理 · v2.0.0", ForeColor = Color.White, Left = 24, Top = 52, AutoSize = true });
        Controls.Add(head);
        Controls.Add(new Label { Text = "安装位置（不需要管理员权限）：", Left = 22, Top = 104, AutoSize = true });
        path = new TextBox { Left = 22, Top = 130, Width = 400, Text = dir };
        var browse = new Button { Text = "浏览…", Left = 432, Top = 128, Width = 84, Height = 28 };
        browse.Click += delegate { using (var d = new FolderBrowserDialog { SelectedPath = path.Text }) if (d.ShowDialog(this) == DialogResult.OK) path.Text = Path.Combine(d.SelectedPath, "Claude Tools"); };
        cDesk = new CheckBox { Text = "创建桌面快捷方式", Left = 22, Top = 176, Checked = true, AutoSize = true };
        cMenu = new CheckBox { Text = "创建开始菜单快捷方式", Left = 22, Top = 204, Checked = true, AutoSize = true };
        cAuto = new CheckBox { Text = "开机自动在后台运行（托盘，不弹窗口）", Left = 22, Top = 232, Checked = false, AutoSize = true };
        cRun = new CheckBox { Text = "安装完成后立即运行", Left = 22, Top = 260, Checked = true, AutoSize = true };
        ok = new Button { Text = "安装", Left = 320, Top = 308, Width = 100, Height = 34, BackColor = Color.FromArgb(244, 99, 58), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "取消", Left = 432, Top = 308, Width = 84, Height = 34 };
        cancel.Click += delegate { Close(); };
        ok.Click += delegate
        {
            ok.Enabled = false; ok.Text = "安装中…"; Application.DoEvents();
            try
            {
                Setup.Install(path.Text, cDesk.Checked, cMenu.Checked, cAuto.Checked, cRun.Checked);
                MessageBox.Show("安装完成。", "Claude Tools", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error); ok.Enabled = true; ok.Text = "安装"; }
        };
        Controls.AddRange(new Control[] { path, browse, cDesk, cMenu, cAuto, cRun, ok, cancel });
        Controls.Add(new Label { Text = "非官方工具，与 Anthropic 无关 · MIT 开源 · github.com/2014-y/Claude-Tools", ForeColor = Color.Gray, Left = 22, Top = 316, AutoSize = true, Font = new Font("Microsoft YaHei UI", 8f) });
    }
}
