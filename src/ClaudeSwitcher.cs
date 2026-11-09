// 命令行版 Claude 多账号存储（.NET Framework 4，C# 5）
// 原理：每个账号使用独立的 CLAUDE_CONFIG_DIR（~/.claude-accounts/<文件夹>），
// 其中 projects 等目录通过 junction 指向 ~/.claude，所有账号共享历史对话。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Claude Tools")]
[assembly: System.Reflection.AssemblyDescription("Claude Tools - Claude 多账号切换与用量管理")]
[assembly: System.Reflection.AssemblyProduct("Claude Tools")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.0.0")]

namespace ClaudeSwitcher
{
    class Account
    {
        public string Folder;   // default 或 acc1/acc2...
        public string Dir;      // 配置目录
        public string Remark;   // 备注名
        public string Email;
        public bool LoggedIn;
        public bool IsDefault { get { return Folder == "default"; } }
    }

    static class Store
    {
        public static readonly string Home = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_SWITCHER_HOME"))
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Environment.GetEnvironmentVariable("CLAUDE_SWITCHER_HOME");
        public static readonly string MainDir = Path.Combine(Home, ".claude");
        public static readonly string AccountsDir = Path.Combine(Home, ".claude-accounts");
        public static readonly string[] SharedDirs = { "projects", "file-history", "todos", "plans", "skills" };
        public static readonly string[] CopyFiles = { "settings.json", "CLAUDE.md" };
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static List<Account> Load()
        {
            var list = new List<Account>();
            var def = new Account { Folder = "default", Dir = MainDir, Remark = "原始账号" };
            Fill(def, Path.Combine(Home, ".claude.json"));
            list.Add(def);
            if (Directory.Exists(AccountsDir))
            {
                foreach (var d in Directory.GetDirectories(AccountsDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var a = new Account { Folder = Path.GetFileName(d), Dir = d };
                    var rf = Path.Combine(d, ".remark");
                    a.Remark = File.Exists(rf) ? File.ReadAllText(rf, Utf8).Trim() : a.Folder;
                    Fill(a, Path.Combine(d, ".claude.json"));
                    list.Add(a);
                }
            }
            return list;
        }

        static void Fill(Account a, string jsonPath)
        {
            a.LoggedIn = File.Exists(Path.Combine(a.Dir, ".credentials.json"));
            a.Email = "";
            string[] candidates = { jsonPath, Path.Combine(a.Dir, ".claude.json") };
            foreach (var p in candidates)
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    var m = Regex.Match(File.ReadAllText(p, Utf8), "\"emailAddress\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) { a.Email = m.Groups[1].Value; break; }
                }
                catch { }
            }
        }

        public static bool IsLink(string path)
        {
            return Directory.Exists(path) &&
                   (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }

        static void MakeJunction(string link, string target)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                if (p.ExitCode != 0 || !IsLink(link))
                    throw new Exception("创建链接失败：" + link);
            }
        }

        public static void ConnectShared(string accDir)
        {
            foreach (var d in SharedDirs)
            {
                var target = Path.Combine(MainDir, d);
                var link = Path.Combine(accDir, d);
                Directory.CreateDirectory(target);
                if (IsLink(link)) continue;
                if (Directory.Exists(link))
                {
                    // 已有独立文件夹：能合并的移进共享目录，剩下的改名保留
                    foreach (var e in Directory.GetFileSystemEntries(link))
                    {
                        var dest = Path.Combine(target, Path.GetFileName(e));
                        if (File.Exists(dest) || Directory.Exists(dest)) continue;
                        if (Directory.Exists(e)) Directory.Move(e, dest); else File.Move(e, dest);
                    }
                    if (Directory.GetFileSystemEntries(link).Length == 0) Directory.Delete(link);
                    else Directory.Move(link, link + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                }
                MakeJunction(link, target);
            }
        }

        public static Account Create(string remark)
        {
            Directory.CreateDirectory(AccountsDir);
            int n = 1;
            while (Directory.Exists(Path.Combine(AccountsDir, "acc" + n))) n++;
            var dir = Path.Combine(AccountsDir, "acc" + n);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".remark"), remark, Utf8);
            foreach (var f in CopyFiles)
            {
                var src = Path.Combine(MainDir, f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(dir, f));
            }
            ConnectShared(dir);
            return new Account { Folder = "acc" + n, Dir = dir, Remark = remark };
        }

        public static void SetRemark(Account a, string remark)
        {
            File.WriteAllText(Path.Combine(a.Dir, ".remark"), remark, Utf8);
        }

        // 先拆掉所有链接（不跟随进入），确认没有残留后才删除目录，保证共享历史不受影响
        internal static void RemoveLinks(string dir)
        {
            foreach (var sub in Directory.GetDirectories(dir))
            {
                if (IsLink(sub)) Directory.Delete(sub, false);
                else RemoveLinks(sub);
            }
        }

        internal static bool HasLinks(string dir)
        {
            foreach (var sub in Directory.GetDirectories(dir))
                if (IsLink(sub) || HasLinks(sub)) return true;
            return false;
        }

        public static void Delete(Account a)
        {
            if (a.IsDefault) throw new Exception("原始账号不能删除");
            if (!a.Dir.StartsWith(AccountsDir, StringComparison.OrdinalIgnoreCase)) throw new Exception("路径异常，已停止");
            RemoveLinks(a.Dir);
            if (HasLinks(a.Dir)) throw new Exception("还有链接没有拆除，已停止删除：" + a.Dir);
            Directory.Delete(a.Dir, true);
        }

        public static void RepairAll()
        {
            foreach (var a in Load())
                if (!a.IsDefault) ConnectShared(a.Dir);
        }

        // ---- 最近使用的项目目录 ----
        static string RecentFile { get { return Path.Combine(AccountsDir, "recent-dirs.txt"); } }

        public static List<string> LoadRecent()
        {
            var r = new List<string>();
            try
            {
                if (File.Exists(RecentFile))
                    r.AddRange(File.ReadAllLines(RecentFile, Utf8).Select(x => x.Trim()).Where(x => x.Length > 0 && Directory.Exists(x)));
                var last = Path.Combine(AccountsDir, ".last-dir");
                if (r.Count == 0 && File.Exists(last))
                {
                    var l = File.ReadAllText(last, Encoding.UTF8).Trim().TrimStart('﻿');
                    if (Directory.Exists(l)) r.Add(l);
                }
            }
            catch { }
            return r;
        }

        public static void SaveRecent(string dir)
        {
            try
            {
                Directory.CreateDirectory(AccountsDir);
                var r = LoadRecent();
                r.RemoveAll(x => string.Equals(x, dir, StringComparison.OrdinalIgnoreCase));
                r.Insert(0, dir);
                File.WriteAllLines(RecentFile, r.Take(15).ToArray(), Utf8);
                File.WriteAllText(Path.Combine(AccountsDir, ".last-dir"), dir, Utf8);
            }
            catch { }
        }
    }
}
