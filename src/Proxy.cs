// 代理设置：代理池（按账号绑定 / 权重随机 / 轮询 / 固定）、全局上游代理、自动小黑屋开关，以及应用范围。
// 代理用于：本工具查询官方额度、终端 Claude Code、（可选）Claude 桌面 App。只支持 HTTP 代理。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeSwitcher
{
    static class ProxyPool
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static readonly object Lock = new object();
        static readonly Random Rnd = new Random();
        static string FilePath { get { return Path.Combine(Settings.StateDir, "proxies.json"); } }
        static Dictionary<string, object> cache;
        // 账号 -> (代理 id, 有效期)：轮换周期内保持同一个代理
        static readonly Dictionary<string, Tuple<string, DateTime>> Sticky = new Dictionary<string, Tuple<string, DateTime>>();
        static int rr;

        static Dictionary<string, object> Defaults()
        {
            return new Dictionary<string, object> {
                { "enabled", false }, { "mode", "weighted" }, { "rotateSec", 300 },
                { "globalEnabled", false }, { "globalUrl", "" },
                { "qos", true },
                { "applyQuota", true }, { "applyCli", true }, { "applyDesktop", false },
                { "items", new List<object>() }
            };
        }

        public static Dictionary<string, object> Get()
        {
            lock (Lock)
            {
                if (cache != null) return cache;
                var d = Defaults();
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var f = Json.DeserializeObject(File.ReadAllText(FilePath, Encoding.UTF8)) as Dictionary<string, object>;
                        if (f != null) foreach (var kv in f) d[kv.Key] = kv.Value;
                    }
                }
                catch { }
                d["items"] = Items(d);
                cache = d;
                return d;
            }
        }

        static List<object> Items(Dictionary<string, object> d)
        {
            var l = new List<object>();
            var raw = d.ContainsKey("items") ? d["items"] as System.Collections.IEnumerable : null;
            if (raw != null) foreach (var x in raw) if (x is Dictionary<string, object>) l.Add(x);
            return l;
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }
        static bool Bool(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is bool && (bool)v; }
        static int Int(Dictionary<string, object> d, string k, int def) { object v; int i; return d.TryGetValue(k, out v) && v != null && int.TryParse(Convert.ToString(v), out i) ? i : def; }

        // 规范化代理地址：支持 host:port、http://host:port、http://用户名:密码@host:port
        public static string Normalize(string url)
        {
            url = (url ?? "").Trim();
            if (url.Length == 0) throw new Exception("代理地址不能为空");
            if (!url.Contains("://")) url = "http://" + url;
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || u.Port <= 0 || string.IsNullOrEmpty(u.Host)) throw new Exception("代理地址格式不对：" + url);
            if (u.Scheme != "http" && u.Scheme != "https") throw new Exception("目前只支持 HTTP 代理：" + url);
            if (!System.Text.RegularExpressions.Regex.IsMatch(u.Host, @"^[A-Za-z0-9.\-\[\]:]+$")) throw new Exception("代理地址格式不对：" + url);
            return u.Scheme + "://" + (string.IsNullOrEmpty(u.UserInfo) ? "" : u.UserInfo + "@") + u.Host + ":" + u.Port;
        }

        public static WebProxy ToWebProxy(string url)
        {
            var u = new Uri(Normalize(url));
            var p = new WebProxy(u.Scheme + "://" + u.Host + ":" + u.Port);
            if (!string.IsNullOrEmpty(u.UserInfo))
            {
                var parts = u.UserInfo.Split(new[] { ':' }, 2);
                p.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            }
            return p;
        }

        public static Dictionary<string, object> Save(string cfgJson)
        {
            var d = Json.DeserializeObject(cfgJson) as Dictionary<string, object>;
            if (d == null) throw new Exception("设置格式不对");
            var full = Defaults();
            foreach (var k in full.Keys.ToList()) if (d.ContainsKey(k)) full[k] = d[k];
            var items = new List<object>();
            var seen = new HashSet<string>();
            foreach (Dictionary<string, object> it in Items(full))
            {
                var url = Normalize(Str(it, "url"));
                var id = Str(it, "id"); if (id.Length == 0 || !seen.Add(id)) { id = Guid.NewGuid().ToString("N").Substring(0, 8); seen.Add(id); }
                int w = Math.Max(1, Math.Min(100, Int(it, "weight", 1)));
                items.Add(new Dictionary<string, object> {
                    { "id", id }, { "name", Str(it, "name") }, { "url", url }, { "weight", w }, { "bind", Str(it, "bind") },
                    { "status", Str(it, "status") }, { "latency", Int(it, "latency", 0) }, { "checkedAt", Str(it, "checkedAt") }, { "error", Str(it, "error") }
                });
            }
            full["items"] = items;
            full["rotateSec"] = Math.Max(10, Math.Min(86400, Int(full, "rotateSec", 300)));
            var gu = Str(full, "globalUrl"); full["globalUrl"] = gu.Trim().Length == 0 ? "" : Normalize(gu);
            if (Bool(full, "globalEnabled") && Str(full, "globalUrl").Length == 0) throw new Exception("开启全局上游代理前请先填写代理地址");
            Write(full);
            return full;
        }

        static void Write(Dictionary<string, object> d)
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Settings.StateDir);
                File.WriteAllText(FilePath, Json.Serialize(d), new UTF8Encoding(false));
                cache = d;
                Sticky.Clear();
            }
        }

        public static Dictionary<string, object> Import(string text)
        {
            var d = Get(); var items = Items(d); int added = 0; var bad = new List<string>();
            var exist = new HashSet<string>(items.Cast<Dictionary<string, object>>().Select(x => Str(x, "url")));
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
                try
                {
                    var url = Normalize(line);
                    if (!exist.Add(url)) continue;
                    items.Add(new Dictionary<string, object> { { "id", Guid.NewGuid().ToString("N").Substring(0, 8) }, { "name", "" }, { "url", url }, { "weight", 1 }, { "bind", "" }, { "status", "" }, { "latency", 0 }, { "checkedAt", "" }, { "error", "" } });
                    added++;
                }
                catch { bad.Add(line); }
            }
            d["items"] = items; Write(d);
            return new Dictionary<string, object> { { "cfg", d }, { "added", added }, { "bad", bad } };
        }

        // 检测：通过代理访问官方接口地址，收到任何 HTTP 响应即视为可用
        public static Dictionary<string, object> Test(string id)
        {
            var d = Get();
            var list = Items(d).Cast<Dictionary<string, object>>().Where(x => id == "*" || Str(x, "id") == id).ToList();
            var tasks = list.Select(it => System.Threading.Tasks.Task.Factory.StartNew(() => Check(it))).ToArray();
            System.Threading.Tasks.Task.WaitAll(tasks, 30000);
            Write(d);
            return d;
        }

        public static Dictionary<string, object> TestUrl(string url)
        {
            var it = new Dictionary<string, object> { { "url", Normalize(url) } };
            Check(it);
            return it;
        }

        static void Check(Dictionary<string, object> it)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                var rq = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/");
                rq.Proxy = ToWebProxy(Str(it, "url")); rq.Timeout = 12000; rq.ReadWriteTimeout = 12000; rq.UserAgent = "claude-tools";
                try { using (rq.GetResponse()) { } }
                catch (WebException ex)
                {
                    if (ex.Response == null) throw;
                    var code = (int)((HttpWebResponse)ex.Response).StatusCode; ex.Response.Close();
                    if (code == 407) throw new Exception("代理需要用户名密码（或用户名密码不对）");
                    if (code == 502 || code == 504) throw new Exception("代理无法连到官方服务器（" + code + "）");
                    // 404 等是官方服务器返回的，说明已经连通
                }
                it["status"] = "ok"; it["error"] = "";
            }
            catch (Exception ex) { it["status"] = "fail"; it["error"] = ex.Message; }
            it["latency"] = (int)sw.ElapsedMilliseconds;
            it["checkedAt"] = DateTime.Now.ToString("HH:mm:ss");
        }

        // 给某个账号挑代理。key：桌面账号 "d:<id>"，终端账号 "c:<文件夹>"；kind：quota / cli / desktop
        public static string For(string key, string kind)
        {
            var d = Get();
            if (kind == "quota" && !Bool(d, "applyQuota")) return null;
            if (kind == "cli" && !Bool(d, "applyCli")) return null;
            if (kind == "desktop" && !Bool(d, "applyDesktop")) return null;
            if (Bool(d, "enabled"))
            {
                var usable = Items(d).Cast<Dictionary<string, object>>().Where(x => Str(x, "status") != "fail").ToList();
                var bound = usable.FirstOrDefault(x => Str(x, "bind") == key);
                if (bound != null) return Str(bound, "url");
                var free = usable.Where(x => Str(x, "bind").Length == 0).ToList();
                if (free.Count > 0) return Str(Pick(key, free, Str(d, "mode"), Int(d, "rotateSec", 300)), "url");
            }
            if (Bool(d, "globalEnabled") && Str(d, "globalUrl").Length > 0) return Str(d, "globalUrl");
            return null;
        }

        static Dictionary<string, object> Pick(string key, List<Dictionary<string, object>> free, string mode, int rotateSec)
        {
            lock (Lock)
            {
                Tuple<string, DateTime> s;
                if (mode != "fixed" && Sticky.TryGetValue(key, out s) && s.Item2 > DateTime.Now)
                {
                    var keep = free.FirstOrDefault(x => Str(x, "id") == s.Item1);
                    if (keep != null) return keep;
                }
                Dictionary<string, object> chosen;
                if (mode == "fixed") chosen = free[0];
                else if (mode == "roundrobin") chosen = free[(rr++) % free.Count];
                else
                {
                    int total = free.Sum(x => Math.Max(1, Int(x, "weight", 1))), r = Rnd.Next(total);
                    chosen = free.Last();
                    foreach (var x in free) { r -= Math.Max(1, Int(x, "weight", 1)); if (r < 0) { chosen = x; break; } }
                }
                Sticky[key] = Tuple.Create(Str(chosen, "id"), DateTime.Now.AddSeconds(rotateSec));
                return chosen;
            }
        }

        // 去掉用户名密码，用于桌面 App 的 --proxy-server 参数和日志
        public static string HostPort(string url) { var u = new Uri(Normalize(url)); return u.Scheme + "://" + u.Host + ":" + u.Port; }
        public static string Mask(string url) { try { var u = new Uri(Normalize(url)); return u.Host + ":" + u.Port + (string.IsNullOrEmpty(u.UserInfo) ? "" : "（带账号）"); } catch { return url; } }
    }
}
