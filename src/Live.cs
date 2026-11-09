// 官方实时余量：用每个桌面账号自己已有的登录（桌面 App 保存在本机、由 Windows 加密的令牌），
// 只读地调用官方用量接口。不续期令牌、不写回任何文件、不上传到除 api.anthropic.com 以外的任何地方，
// 所以不会影响桌面 App 的登录状态。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeSwitcher
{
    class RateLimited : Exception
    {
        public int Seconds;
        public RateLimited(int s) : base("官方限流，" + s + " 秒后可再查") { Seconds = s; }
    }

    static class LiveUsage
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        [DllImport("bcrypt.dll")] static extern int BCryptOpenAlgorithmProvider(out IntPtr h, [MarshalAs(UnmanagedType.LPWStr)] string alg, [MarshalAs(UnmanagedType.LPWStr)] string impl, int f);
        [DllImport("bcrypt.dll")] static extern int BCryptSetProperty(IntPtr h, [MarshalAs(UnmanagedType.LPWStr)] string p, byte[] v, int n, int f);
        [DllImport("bcrypt.dll")] static extern int BCryptGenerateSymmetricKey(IntPtr a, out IntPtr k, IntPtr o, int on, byte[] key, int kn, int f);
        [DllImport("bcrypt.dll")] static extern int BCryptDecrypt(IntPtr k, byte[] i, int n, ref AuthInfo a, byte[] iv, int ivn, byte[] o, int on, out int r, int f);
        [DllImport("bcrypt.dll")] static extern int BCryptDestroyKey(IntPtr k);
        [DllImport("bcrypt.dll")] static extern int BCryptCloseAlgorithmProvider(IntPtr h, int f);
        [StructLayout(LayoutKind.Sequential)]
        struct AuthInfo { public int cbSize, dwInfoVersion; public IntPtr pbNonce; public int cbNonce; public IntPtr pbAuthData; public int cbAuthData; public IntPtr pbTag; public int cbTag; public IntPtr pbMacContext; public int cbMacContext, cbAAD; public long cbData; public int dwFlags; }

        static byte[] AesGcmDecrypt(byte[] key, byte[] nonce, byte[] ct, byte[] tag)
        {
            IntPtr alg, k;
            if (BCryptOpenAlgorithmProvider(out alg, "AES", null, 0) != 0) throw new Exception("AES 不可用");
            try
            {
                var mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                BCryptSetProperty(alg, "ChainingMode", mode, mode.Length, 0);
                if (BCryptGenerateSymmetricKey(alg, out k, IntPtr.Zero, 0, key, key.Length, 0) != 0) throw new Exception("密钥无效");
                var hn = GCHandle.Alloc(nonce, GCHandleType.Pinned); var ht = GCHandle.Alloc(tag, GCHandleType.Pinned);
                try
                {
                    var ai = new AuthInfo { cbSize = Marshal.SizeOf(typeof(AuthInfo)), dwInfoVersion = 1, pbNonce = hn.AddrOfPinnedObject(), cbNonce = nonce.Length, pbTag = ht.AddrOfPinnedObject(), cbTag = tag.Length };
                    var o = new byte[ct.Length]; int r;
                    if (BCryptDecrypt(k, ct, ct.Length, ref ai, null, 0, o, o.Length, out r, 0) != 0) throw new Exception("解密失败");
                    return o;
                }
                finally { hn.Free(); ht.Free(); BCryptDestroyKey(k); }
            }
            finally { BCryptCloseAlgorithmProvider(alg, 0); }
        }

        // 读出这个账号目录里可用于查询用量的令牌（取有效期最长的 user:profile 令牌）；没有返回 null
        static string ReadToken(string dir, out long expiresAt)
        {
            expiresAt = 0;
            var lsPath = Path.Combine(dir, "Local State"); var cfgPath = Path.Combine(dir, "config.json");
            if (!File.Exists(lsPath) || !File.Exists(cfgPath)) return null;
            var ls = Json.DeserializeObject(File.ReadAllText(lsPath)) as Dictionary<string, object>;
            var oc = ls != null && ls.ContainsKey("os_crypt") ? ls["os_crypt"] as Dictionary<string, object> : null;
            if (oc == null || !oc.ContainsKey("encrypted_key")) return null;
            var ek = Convert.FromBase64String((string)oc["encrypted_key"]);
            var ek2 = new byte[ek.Length - 5]; Array.Copy(ek, 5, ek2, 0, ek2.Length);   // 去掉 "DPAPI" 前缀
            var key = ProtectedData.Unprotect(ek2, null, DataProtectionScope.CurrentUser);
            var cfg = Json.DeserializeObject(File.ReadAllText(cfgPath)) as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("oauth:tokenCacheV2")) return null;
            var raw = Convert.FromBase64String((string)cfg["oauth:tokenCacheV2"]);
            if (raw.Length < 3 + 12 + 16 || Encoding.ASCII.GetString(raw, 0, 3) != "v10") return null;
            var nonce = new byte[12]; Array.Copy(raw, 3, nonce, 0, 12);
            var ct = new byte[raw.Length - 15 - 16]; Array.Copy(raw, 15, ct, 0, ct.Length);
            var tag = new byte[16]; Array.Copy(raw, raw.Length - 16, tag, 0, 16);
            var cache = Json.DeserializeObject(Encoding.UTF8.GetString(AesGcmDecrypt(key, nonce, ct, tag))) as Dictionary<string, object>;
            if (cache == null) return null;
            string tok = null;
            foreach (var kv in cache)
            {
                var v = kv.Value as Dictionary<string, object>;
                if (v == null || !kv.Key.Contains("user:profile") || !v.ContainsKey("token")) continue;
                long exp = v.ContainsKey("expiresAt") ? Convert.ToInt64(v["expiresAt"]) : 0;
                if (tok == null || exp > expiresAt) { tok = (string)v["token"]; expiresAt = exp; }
            }
            return tok;
        }

        static readonly object HistLock = new object();
        static string HistPath { get { return Path.Combine(Settings.StateDir, "live-history.json"); } }
        // 记录并返回这个账号最近 8 天的 [时间ms, 5H已用%, 7天已用%]
        public static List<object> Record(string id, Dictionary<string, object> r)
        {
            lock (HistLock)
            {
                Dictionary<string, object> all = null;
                try { if (File.Exists(HistPath)) all = Json.DeserializeObject(File.ReadAllText(HistPath)) as Dictionary<string, object>; } catch { }
                if (all == null) all = new Dictionary<string, object>();
                var list = new List<object>();
                if (all.ContainsKey(id) && all[id] is object[]) list.AddRange((object[])all[id]);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cut = now - 8L * 86400000;
                list.RemoveAll(x => !(x is object[]) || Convert.ToInt64(((object[])x)[0]) < cut);
                if (r == null) return list;
                var s = r["session"] as Dictionary<string, object>; var w = r["week"] as Dictionary<string, object>;
                int su = s != null ? (int)s["used"] : -1, wu = w != null ? (int)w["used"] : -1;
                var last = list.Count > 0 ? (object[])list[list.Count - 1] : null;
                bool add = last == null || Convert.ToInt32(last[1]) != su || Convert.ToInt32(last[2]) != wu || now - Convert.ToInt64(last[0]) >= 600000;
                if (add)
                {
                    list.Add(new object[] { now, su, wu });
                    all[id] = list;
                    try { Directory.CreateDirectory(Settings.StateDir); File.WriteAllText(HistPath, Json.Serialize(all)); } catch { }
                }
                return list;
            }
        }

        // 缓存与限流保护：50 秒内重复请求直接用缓存；官方返回限流/网络失败时用最近一次成功的数据，并退避 5 分钟
        static readonly Dictionary<string, Dictionary<string, object>> Last = new Dictionary<string, Dictionary<string, object>>();
        static readonly Dictionary<string, DateTime> LastOk = new Dictionary<string, DateTime>();
        static readonly Dictionary<string, DateTime> Backoff = new Dictionary<string, DateTime>();
        static string CachePath { get { return Path.Combine(Settings.StateDir, "live-cache.json"); } }
        static bool cacheLoaded;
        static void LoadCache()
        {
            if (cacheLoaded) return; cacheLoaded = true;
            try
            {
                if (!File.Exists(CachePath)) return;
                var all = Json.DeserializeObject(File.ReadAllText(CachePath)) as Dictionary<string, object>;
                if (all == null) return;
                foreach (var kv in all) { var d = kv.Value as Dictionary<string, object>; if (d != null) Last[kv.Key] = d; }
            }
            catch { }
        }
        public const int LockSeconds = 30;
        // 被官方限流过的账号，接下来 1 小时内改为每 2 分钟查一次，避免反复撞限流
        static readonly Dictionary<string, DateTime> Slow = new Dictionary<string, DateTime>();
        static int Gap(string id) { DateTime s; return Slow.TryGetValue(id, out s) && DateTime.Now < s ? 120 : LockSeconds; }
        static long NowMs() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
        public static Dictionary<string, object> Cached(string id, Func<Dictionary<string, object>> query)
        {
            lock (HistLock)
            {
                LoadCache();
                DateTime bo;
                if (Backoff.TryGetValue(id, out bo) && DateTime.Now < bo) return Answer(id, null, bo);
            }
            Dictionary<string, object> r = null; string err = null; DateTime next;
            try { r = query(); lock (HistLock) next = DateTime.Now.AddSeconds(Gap(id)); }
            catch (RateLimited ex) { err = ex.Message; next = DateTime.Now.AddSeconds(ex.Seconds); lock (HistLock) Slow[id] = DateTime.Now.AddHours(1); }
            catch (Exception ex) { err = ex.Message; next = DateTime.Now.AddSeconds(LockSeconds); }
            lock (HistLock)
            {
                Backoff[id] = next;
                if (r != null)
                {
                    r["hist"] = Record(id, r);
                    Last[id] = r; LastOk[id] = DateTime.Now;
                    try
                    {
                        var save = new Dictionary<string, object>();
                        foreach (var kv in Last) { var c = new Dictionary<string, object>(kv.Value); c.Remove("hist"); c.Remove("error"); c.Remove("nextAt"); save[kv.Key] = c; }
                        File.WriteAllText(CachePath, Json.Serialize(save));
                    }
                    catch { }
                }
                return Answer(id, err, next);
            }
        }
        // 返回最近一次成功的数据（没有则为空）+ 错误信息 + 下次可查询的时间
        static Dictionary<string, object> Answer(string id, string err, DateTime next)
        {
            var d = Last.ContainsKey(id) ? new Dictionary<string, object>(Last[id]) : new Dictionary<string, object> { { "session", null }, { "week", null } };
            if (!d.ContainsKey("hist")) d["hist"] = Record(id, null);
            d["error"] = err;
            d["nextAt"] = new DateTimeOffset(next).ToUnixTimeMilliseconds();
            return d;
        }

        public static bool Available(string dir)
        {
            try { long exp; return ReadToken(dir, out exp) != null; } catch { return false; }
        }

        static Dictionary<string, object> Window(Dictionary<string, object> d, string name)
        {
            var w = d.ContainsKey(name) ? d[name] as Dictionary<string, object> : null;
            if (w == null || w["utilization"] == null) return null;
            long reset = 0; DateTimeOffset dto;
            if (w["resets_at"] is string && DateTimeOffset.TryParse((string)w["resets_at"], out dto)) reset = dto.ToUnixTimeMilliseconds();
            return new Dictionary<string, object> { { "used", (int)Math.Round(Convert.ToDouble(w["utilization"])) }, { "resetAt", reset } };
        }

        public static Dictionary<string, object> Query(string dir)
        {
            long exp; string tok;
            try { tok = ReadToken(dir, out exp); }
            catch (Exception ex) { throw new Exception("读取登录信息失败：" + ex.Message); }
            if (tok == null) throw new Exception("这个账号还没有登录桌面 App");
            if (exp > 0 && exp < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) throw new Exception("登录已过期：切换到这个账号打开一次桌面 App 即可恢复");
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var rq = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/api/oauth/usage");
            rq.Headers["Authorization"] = "Bearer " + tok;
            rq.Headers["anthropic-beta"] = "oauth-2025-04-20";
            rq.UserAgent = "claude-tools"; rq.Timeout = 20000; rq.ReadWriteTimeout = 20000;
            string body;
            try { using (var rs = rq.GetResponse()) using (var sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8)) body = sr.ReadToEnd(); }
            catch (WebException ex)
            {
                var hr = ex.Response as HttpWebResponse;
                if (hr != null && (int)hr.StatusCode == 401) throw new Exception("登录已失效：切换到这个账号打开一次桌面 App 即可恢复");
                if (hr != null && (int)hr.StatusCode == 429)
                {
                    int sec = 300; int v;
                    try { if (int.TryParse(hr.Headers["retry-after"], out v) && v > 0) sec = v; } catch { }
                    throw new RateLimited(sec);
                }
                throw new Exception("连接官方服务器失败：" + ex.Message);
            }
            var d = Json.DeserializeObject(body) as Dictionary<string, object>;
            if (d == null) throw new Exception("官方返回的数据无法识别");
            return new Dictionary<string, object> { { "session", Window(d, "five_hour") }, { "week", Window(d, "seven_day") }, { "at", DateTime.Now.ToString("HH:mm:ss") } };
        }
    }
}
