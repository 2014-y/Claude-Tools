// 用量统计：逐条读取 ~/.claude/projects 下的对话记录，按消息去重，支持时间范围 / 模型 / 账号筛选、费用估算和请求日志
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClaudeSwitcher
{
    // ---------------- 定价（美元 / 百万 Token，可在界面修改） ----------------
    class Price
    {
        public string Key; public double Input, Output, CacheRead, CacheWrite5m, CacheWrite1h;
    }

    static class Pricing
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static string FilePath { get { return Path.Combine(Settings.StateDir, "pricing.json"); } }
        static List<Price> cache;

        // 默认值：Anthropic 官方 API 价格；缓存写入 5 分钟 = 1.25×输入，1 小时 = 2×输入
        static Price P(string k, double i, double o, double cr) { return new Price { Key = k, Input = i, Output = o, CacheRead = cr, CacheWrite5m = i * 1.25, CacheWrite1h = i * 2 }; }
        public static List<Price> Defaults()
        {
            return new List<Price> {
                P("claude-fable-5-1", 10, 50, 0.25), P("claude-mythos-5-1", 10, 50, 0.25), P("claude-fable-5", 10, 50, 1.0),
                P("claude-opus-5-5", 4, 20, 0.20), P("claude-opus-5", 5, 25, 0.50),
                P("claude-opus-4-8", 5, 25, 0.50), P("claude-opus-4-7", 5, 25, 0.50), P("claude-opus-4-6", 5, 25, 0.50), P("claude-opus-4-5", 5, 25, 0.50),
                P("claude-sonnet-5-5", 2, 10, 0.20), P("claude-sonnet-5", 2, 10, 0.20),
                P("claude-sonnet-4-6", 3, 15, 0.30), P("claude-sonnet-4-5", 3, 15, 0.30),
                P("claude-haiku-5-5", 0.10, 0.50, 0.01), P("claude-haiku-4-5", 1, 5, 0.10)
            };
        }

        public static List<Price> All()
        {
            if (cache != null) return cache;
            try
            {
                if (File.Exists(FilePath))
                    return cache = Json.Deserialize<List<Price>>(File.ReadAllText(FilePath, Encoding.UTF8));
            }
            catch { }
            return cache = Defaults();
        }

        public static void Save(List<Price> list)
        {
            Directory.CreateDirectory(Settings.StateDir);
            File.WriteAllText(FilePath, Json.Serialize(list), Encoding.UTF8);
            cache = list;
        }

        public static void Reset() { try { File.Delete(FilePath); } catch { } cache = null; }

        // 最长前缀匹配；没有定价的模型返回 null（不计费）
        public static Price Find(string model)
        {
            if (string.IsNullOrEmpty(model)) return null;
            return All().Where(p => !string.IsNullOrEmpty(p.Key) && model.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Key.Length).FirstOrDefault();
        }

        public static double Cost(Rec r)
        {
            var p = Find(r.Model);
            if (p == null) return 0;
            return (r.In * p.Input + r.Out * p.Output + r.CacheRead * p.CacheRead + r.Cw5m * p.CacheWrite5m + r.Cw1h * p.CacheWrite1h) / 1e6;
        }
    }

    // 一次模型回复
    class Rec
    {
        public DateTime Ts; public string Model, Session, Cwd;
        public long In, Out, CacheRead, Cw5m, Cw1h;
        public double Secs; // 距上一条记录的时间（近似生成耗时）
        public long CacheWrite { get { return Cw5m + Cw1h; } }
        public long Total { get { return In + Out + CacheRead + Cw5m + Cw1h; } }
    }

    class FileRecs { public long Len; public DateTime Mt; public List<Rec> Recs; }

    static class TokenStats
    {
        static readonly Dictionary<string, FileRecs> Cache = new Dictionary<string, FileRecs>();
        static readonly object Lock = new object();
        static List<Rec> all = new List<Rec>();
        static Dictionary<string, string> accountOf = new Dictionary<string, string>();
        static bool scanning, ready;
        static DateTime lastScan = DateTime.MinValue;
        static int progressDone, progressTotal;

        static readonly Regex IdRe = new Regex("\"id\":\"(msg_[^\"]+)\"");
        static readonly Regex ModelRe = new Regex("\"model\":\"([^\"]+)\"");
        static readonly Regex TsRe = new Regex("\"timestamp\":\"([^\"]+)\"");
        static readonly Regex CwdRe = new Regex("\"cwd\":\"((?:[^\"\\\\]|\\\\.)*)\"");
        static readonly Regex InRe = new Regex("\"input_tokens\":(\\d+)"), OutRe = new Regex("\"output_tokens\":(\\d+)"),
            CrRe = new Regex("\"cache_read_input_tokens\":(\\d+)"), CwRe = new Regex("\"cache_creation_input_tokens\":(\\d+)"),
            Cw5Re = new Regex("\"ephemeral_5m_input_tokens\":(\\d+)"), Cw1Re = new Regex("\"ephemeral_1h_input_tokens\":(\\d+)");

        static long Num(Regex re, string s) { var m = re.Match(s); return m.Success ? long.Parse(m.Groups[1].Value) : 0; }

        static void EnsureFresh(bool force, double maxAgeSec)
        {
            lock (Lock)
            {
                if (scanning) return;
                if (!force && ready && (DateTime.Now - lastScan).TotalSeconds < maxAgeSec) return;
                scanning = true;
                new Thread(Scan) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
            }
        }

        static List<Tuple<string, string>> TranscriptFiles()
        {
            var root = Path.Combine(Store.MainDir, "projects");
            var list = new List<Tuple<string, string>>();
            if (!Directory.Exists(root)) return list;
            foreach (var proj in Safe(() => Directory.GetDirectories(root)))
            {
                foreach (var f in Safe(() => Directory.GetFiles(proj, "*.jsonl")))
                    list.Add(Tuple.Create(f, Path.GetFileNameWithoutExtension(f)));
                foreach (var sd in Safe(() => Directory.GetDirectories(proj)))
                {
                    var sub = Path.Combine(sd, "subagents");
                    if (Directory.Exists(sub))
                        foreach (var f in Safe(() => Directory.GetFiles(sub, "*.jsonl")))
                            list.Add(Tuple.Create(f, Path.GetFileName(sd)));
                }
            }
            return list;
        }

        static string[] Safe(Func<string[]> f) { try { return f(); } catch { return new string[0]; } }

        static List<Rec> ParseFile(string path, string sessionId)
        {
            var fi = new FileInfo(path);
            FileRecs old;
            lock (Cache) Cache.TryGetValue(path, out old);
            if (old != null && old.Len == fi.Length && old.Mt == fi.LastWriteTimeUtc) return old.Recs;

            var msgs = new Dictionary<string, Rec>();
            var order = new List<string>();
            string cwd = null;
            DateTime prevTs = DateTime.MinValue;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8))
            {
                string line; int anon = 0;
                while ((line = r.ReadLine()) != null)
                {
                    if (cwd == null) { var cm = CwdRe.Match(line); if (cm.Success) cwd = cm.Groups[1].Value.Replace("\\\\", "\\"); }
                    DateTime ts = DateTime.MinValue;
                    var tm = TsRe.Match(line);
                    if (tm.Success) DateTime.TryParse(tm.Groups[1].Value, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out ts);
                    bool isAssistant = line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) >= 0;
                    int ui = isAssistant ? line.IndexOf("\"usage\":{", StringComparison.Ordinal) : -1;
                    if (ui >= 0)
                    {
                        var head = line.Substring(0, ui); var usage = line.Substring(ui);
                        var mm = ModelRe.Match(head);
                        var model = mm.Success ? mm.Groups[1].Value : "unknown";
                        if (model != "<synthetic>")
                        {
                            var idm = IdRe.Match(line);
                            var id = idm.Success ? idm.Groups[1].Value : "anon" + (anon++);
                            long cw = Num(CwRe, usage), cw5 = Num(Cw5Re, usage), cw1 = Num(Cw1Re, usage);
                            if (cw5 + cw1 == 0) cw5 = cw; // 旧记录没有细分时按 5 分钟缓存算
                            Rec rec;
                            if (!msgs.TryGetValue(id, out rec))
                            {
                                rec = new Rec { Session = sessionId, Model = model };
                                rec.Ts = ts == DateTime.MinValue ? DateTime.MinValue : ts.ToLocalTime();
                                rec.Secs = (ts != DateTime.MinValue && prevTs != DateTime.MinValue) ? (ts - prevTs).TotalSeconds : 0;
                                msgs[id] = rec; order.Add(id);
                            }
                            // 同一条消息会被拆成多行，取最后一次（最终用量）
                            rec.In = Num(InRe, usage); rec.Out = Math.Max(rec.Out, Num(OutRe, usage));
                            rec.CacheRead = Num(CrRe, usage); rec.Cw5m = cw5; rec.Cw1h = cw1;
                            if (ts != DateTime.MinValue && rec.Ts != DateTime.MinValue && prevTs != DateTime.MinValue)
                                rec.Secs = Math.Max(rec.Secs, (ts.ToLocalTime() - rec.Ts).TotalSeconds + rec.Secs);
                        }
                    }
                    if (ts != DateTime.MinValue && ui < 0) prevTs = ts;
                }
            }
            var list = order.Select(id => msgs[id]).Where(x => x.Ts != DateTime.MinValue).ToList();
            foreach (var x in list) x.Cwd = cwd ?? "";
            lock (Cache) Cache[path] = new FileRecs { Len = fi.Length, Mt = fi.LastWriteTimeUtc, Recs = list };
            return list;
        }

        static void Scan()
        {
            try
            {
                var files = TranscriptFiles();
                progressTotal = files.Count; progressDone = 0;
                var recs = new List<Rec>();
                foreach (var f in files)
                {
                    try { recs.AddRange(ParseFile(f.Item1, f.Item2)); } catch { }
                    progressDone++;
                }
                var acc = new Dictionary<string, string>();
                try { foreach (var s in Sessions.All()) if (s.CliId != null) acc[s.CliId] = s.Account; } catch { }
                lock (Lock) { all = recs.OrderBy(r => r.Ts).ToList(); accountOf = acc; ready = true; lastScan = DateTime.Now; }
            }
            catch { }
            finally { lock (Lock) scanning = false; }
        }

        static string AccountOf(Dictionary<string, string> map, Rec r)
        {
            string a; return map.TryGetValue(r.Session, out a) ? a : "终端 Claude Code / 其他";
        }

        static string ProjectOf(Rec r)
        {
            if (string.IsNullOrEmpty(r.Cwd)) return "(未知)";
            if (r.Cwd.IndexOf("\\scratch-workspaces\\", StringComparison.OrdinalIgnoreCase) >= 0) return "(临时会话)";
            return r.Cwd;
        }

        static Dictionary<string, object> Sum(IEnumerable<Rec> rs, string name)
        {
            long n = 0, i = 0, o = 0, cr = 0, cw = 0; double cost = 0;
            foreach (var r in rs) { n++; i += r.In; o += r.Out; cr += r.CacheRead; cw += r.CacheWrite; cost += Pricing.Cost(r); }
            long prompt = i + cr + cw;
            return new Dictionary<string, object> {
                { "name", name }, { "requests", n }, { "input", i }, { "output", o }, { "cacheRead", cr }, { "cacheWrite", cw },
                { "total", i + o + cr + cw }, { "cost", Math.Round(cost, 4) },
                { "hitRate", prompt == 0 ? 0 : Math.Round(cr * 100.0 / prompt, 1) }
            };
        }

        public static Dictionary<string, object> Query(Dictionary<string, object> a, Func<Dictionary<string, object>, string, string> S)
        {
            bool force = S(a, "force") == "True" || S(a, "force") == "true";
            int auto; int.TryParse(S(a, "auto"), out auto);
            EnsureFresh(force, auto > 0 ? Math.Max(3, auto - 1) : 90);

            List<Rec> src; Dictionary<string, string> map; bool isReady, isScanning;
            lock (Lock) { src = all; map = accountOf; isReady = ready; isScanning = scanning; }
            var res = new Dictionary<string, object> {
                { "scanning", isScanning }, { "ready", isReady },
                { "progress", progressTotal == 0 ? 0 : progressDone * 100 / progressTotal },
                { "scannedAt", isReady ? lastScan.ToString("HH:mm:ss") : "" }
            };
            if (!isReady) return res;

            var range = S(a, "range"); if (range == "") range = "7d";
            var now = DateTime.Now; DateTime from;
            switch (range)
            {
                case "today": from = now.Date; break;
                case "30d": from = now.Date.AddDays(-29); break;
                case "all": from = DateTime.MinValue; break;
                default: from = now.Date.AddDays(-6); range = "7d"; break;
            }
            var model = S(a, "model"); var account = S(a, "account");
            var inRange = src.Where(r => r.Ts >= from).ToList();
            var sel = inRange.Where(r => (model == "" || r.Model == model) && (account == "" || AccountOf(map, r) == account)).ToList();

            res["range"] = range;
            res["summary"] = Sum(sel, "summary");
            res["models"] = inRange.Select(r => r.Model).Distinct().OrderBy(x => x).ToList();
            res["accounts"] = inRange.Select(r => AccountOf(map, r)).Distinct().OrderBy(x => x).ToList();

            // 趋势：今天按小时，其它按天
            var trend = new List<object>();
            if (range == "today")
            {
                for (int h = 0; h <= now.Hour; h++)
                {
                    var hh = h;
                    trend.Add(Sum(sel.Where(r => r.Ts.Hour == hh), h.ToString("00") + ":00"));
                }
            }
            else
            {
                var first = range == "all" ? (sel.Count > 0 ? sel.Min(r => r.Ts).Date : now.Date) : from;
                if ((now.Date - first).TotalDays > 120) first = now.Date.AddDays(-119);
                var byDay = sel.GroupBy(r => r.Ts.Date).ToDictionary(g => g.Key, g => g.ToList());
                for (var d = first; d <= now.Date; d = d.AddDays(1))
                {
                    List<Rec> l; byDay.TryGetValue(d, out l);
                    trend.Add(Sum(l ?? new List<Rec>(), d.ToString("MM/dd")));
                }
            }
            res["trend"] = trend;

            res["byModel"] = sel.GroupBy(r => r.Model).Select(g => Sum(g, g.Key)).OrderByDescending(x => (double)x["cost"]).ThenByDescending(x => (long)x["total"]).Cast<object>().ToList();
            res["byAccount"] = sel.GroupBy(r => AccountOf(map, r)).Select(g => Sum(g, g.Key)).OrderByDescending(x => (long)x["total"]).Cast<object>().ToList();
            res["byProject"] = sel.GroupBy(ProjectOf).Select(g => Sum(g, g.Key)).OrderByDescending(x => (long)x["total"]).Take(30).Cast<object>().ToList();

            // 请求日志：所选范围内最多 5000 条，界面分页显示
            res["logs"] = sel.OrderByDescending(r => r.Ts).Take(5000).Select(r =>
            {
                double speed = r.Secs >= 0.8 && r.Secs <= 900 && r.Out > 20 ? r.Out / r.Secs : 0;
                return (object)new Dictionary<string, object> {
                    { "t", r.Ts.ToString(range == "today" ? "HH:mm:ss" : "MM/dd HH:mm:ss") }, { "model", r.Model },
                    { "account", AccountOf(map, r) }, { "project", ProjectOf(r) },
                    { "input", r.In }, { "output", r.Out }, { "cacheRead", r.CacheRead }, { "cacheWrite", r.CacheWrite },
                    { "cost", Math.Round(Pricing.Cost(r), 4) }, { "speed", Math.Round(speed) }, { "secs", Math.Round(r.Secs, 1) },
                    { "priced", Pricing.Find(r.Model) != null }
                };
            }).ToList();

            // 今日 / 本月（仪表盘用，不受筛选影响）
            res["today"] = Sum(src.Where(r => r.Ts >= now.Date), "today");
            var todayRecs = src.Where(r => r.Ts >= now.Date).ToList();
            var hourly = new List<long>();
            for (int h = 0; h < 24; h++) { var hh = h; hourly.Add(h > now.Hour ? -1 : (long)Sum(todayRecs.Where(r => r.Ts.Hour == hh), "h")["total"]); }
            res["todayHourly"] = hourly;
            res["month"] = Sum(src.Where(r => r.Ts >= new DateTime(now.Year, now.Month, 1)), "month");
            res["sessions"] = sel.Select(r => r.Session).Distinct().Count();
            return res;
        }
    }
}
