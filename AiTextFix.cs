using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;

namespace QRSnap
{
    // AI 识字强化：让大模型把一段 OCR 结果当成一整段来读，只修被认错的字。
    // 不动行、不动框、不改结构；三道闸（长度 / 相似度 / 数字冻结）任一不过就整行退回原文。
    internal static class AiTextFix
    {
        private static readonly object Gate = new object();
        private const int MaxLinesOneBatch = 24;
        private const int MaxCharsOneBatch = 1000;
        private const int MaxBatches = 3;
        private const int BudgetMs = 22000;

        // 最近一次的统计，供提示条显示「AI 到底做了什么」
        public static int LastAsked;
        public static int LastChanged;
        public static int LastRejected;
        public static int LastCostMs;
        // 「AI 翻译接管」时，翻译提示词里会顺带做 OCR 纠错，这一轮就别再单独跑一遍识字强化，
        // 否则同一张图要付两次大模型往返（实测 80 行要多花 13 秒，只改 3 处）。
        public static bool Suppress;

        private static readonly Regex FixRe = new Regex(
            "\\{\\s*\"i\"\\s*:\\s*\"?(\\d+)\"?\\s*,\\s*\"t\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\}",
            RegexOptions.Singleline);
        private static readonly Regex NumRe = new Regex("[0-9]{2,}", RegexOptions.Compiled);

        private const string SysPrompt =
            "OCR 校对。下面是同一段文字被 OCR 切成多行，行号即阅读顺序，连起来读再判断哪行错了。\n" +
            "只修 OCR 认错的字符，不改原文意思：\n" +
            "· 形近字符：数字串里的 O 是 0、l 是 1（O.28→0.28）；字母词里的 0 是 O、1 是 l（00ct→Oct）\n" +
            "· 空格只修两种：数字和单位之间缺的空格（US$0.0028→US$ 0.0028）、两个单词粘成一个（Ifit→If it）。" +
            "标点、分隔符（·、|、/）、缩进、行首行尾空白都不要动\n" +
            "· 全角半角混用\n" +
            "· 同一段里同一单位或缩写大小写不一致时，统一成出现次数多的那种（us$→US$）\n" +
            "· 上下文能确定的 OCR 错词可以修，改后必须是常见拼写：" +
            "custcenes→cutscenes、fltering→filtering、reflet→reflect、officialy→officially、Ul→UI、nvn ir→nvn interval\n" +
            "不要改：作者故意写错的专有名词/网络用语、语序、标点风格、数字金额日期链接版本号代码文件名、行序行数。" +
            "拿不准就照抄原行。\n" +
            "只输出 {\"fix\":[{\"i\":N,\"t\":\"整行\"}]}，没改的行不写，全对给 {\"fix\":[]}。不要解释、不要围栏、不要回抄输入。";

        // 微信 / 系统 OCR 这类「引擎自带文本 + 另外切出的几何分段」场景用这个：
        // 只把改动同步回原文本，不拿分段重建文本，否则左右两栏会被并成一行。
        public static bool PolishText(List<OcrSegment> segs, ref string text)
        {
            if (segs == null || segs.Count == 0 || text == null || text.Length == 0) return false;
            List<string> before = new List<string>(segs.Count);
            for (int i = 0; i < segs.Count; i++) before.Add(segs[i].Text ?? "");

            if (Polish(segs) == null) return false;

            string nl = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            string[] lines = text.Split(new string[] { nl }, StringSplitOptions.None);
            for (int i = 0; i < segs.Count && i < before.Count; i++)
            {
                string o = before[i], n = segs[i].Text ?? "";
                if (o.Length == 0 || o == n) continue;
                bool done = false;
                for (int li = 0; li < lines.Length; li++)
                    if (lines[li] == o) { lines[li] = n; done = true; break; }
                if (done) continue;
                // 空格多少不同但内容同一行的，按去空格比对，仍只换这一行，不动行数
                string key = NormKey(o);
                for (int li = 0; li < lines.Length; li++)
                    if (NormKey(lines[li]) == key) { lines[li] = n; done = true; break; }
            }
            text = string.Join(nl, lines);
            return true;
        }

        private static string NormKey(string s)
        {
            if (s == null) return "";
            return Regex.Replace(s, "\\s+", "").ToLowerInvariant();
        }

        private static string StripWs(string s)
        {
            if (s == null) return "";
            return Regex.Replace(s, "\\s+", "");
        }

        // 返回 null = 没启用 / 没改动 / 出错，调用方继续用原始结果
        public static string Polish(List<OcrSegment> segs)
        {
            if (segs == null || segs.Count == 0) return null;
            if (Suppress) { Rec("不介入｜AI 翻译接管已包含纠错，本轮跳过单独的识字强化"); return null; }
            if (!AppConfig.AiOcr) { Rec("不介入｜设置里「AI 识字强化」没打开"); return null; }
            if (string.IsNullOrEmpty(AppConfig.AiKey)) { Rec("不介入｜没有填 API Key"); return null; }
            if (!JadeHost.AiReadyNow()) { Rec("不介入｜AI 没就绪（校验没通过）"); return null; }

            lock (Gate)
            {
                try { return DoPolish(segs); }
                catch (Exception ex)
                {
                    JadeHost.Log("[aiocr] 异常：" + ex.Message);
                    Rec("中断｜异常：" + ex.Message);
                    return null;
                }
            }
        }

        private static string DoPolish(List<OcrSegment> segs)
        {
            int t0 = Environment.TickCount;
            List<int> targets = SelectTargets(segs);
            if (targets.Count == 0)
            {
                Rec("跳过｜没有低置信行，不整图送 AI");
                return null;
            }
            List<List<int>> batches = SplitBatches(segs, targets);
            // 孤零零一行的段（标题、署名、最后一行）没有上下文，合并到前一段一起送审：
            // 只看一行修不出东西，但连着上一段读就能判断（例如最后一行里的 fitering）。
            List<List<int>> merged = new List<List<int>>();
            foreach (List<int> b in batches)
            {
                if (b.Count < 2 && merged.Count > 0
                    && merged[merged.Count - 1].Count + b.Count <= MaxLinesOneBatch)
                {
                    merged[merged.Count - 1].AddRange(b);
                    continue;
                }
                merged.Add(b);
            }
            batches = merged;
            int asked = 0, changed = 0, rejected = 0, fails = 0;
            bool any = false;

            Rec("==== 介入 ====｜OCR " + segs.Count + " 行 · 分 " + batches.Count + " 段 · "
                + AppConfig.AiModel + " @ " + AppConfig.AiBase);

            for (int bi = 0; bi < batches.Count; bi++)
            {
                if (Environment.TickCount - t0 > BudgetMs)
                {
                    JadeHost.Log("[aiocr] 超预算，后面 " + (batches.Count - bi) + " 段不跑了");
                    Rec("停止｜超时预算，" + (batches.Count - bi) + " 段没送");
                    break;
                }

                List<int> idx = batches[bi];
                // 孤零零一行的段（标题、图标、署名）没有上下文，模型修不出东西，
                // 还白花一次请求；整图只有这一行时才送。
                if (idx.Count < 2 && batches.Count > 1)
                {
                    Rec("段" + (bi + 1) + "｜只有 1 行，没有上下文，跳过");
                    continue;
                }
                string body;
                string req = BuildPayload(segs, idx, true);
                // 排查服务端 500 用：QRSNAP_DUMP_AI=1 时把真实请求体落盘，方便拿 curl 原样重放
                if (Environment.GetEnvironmentVariable("QRSNAP_DUMP_AI") == "1")
                {
                    try { System.IO.File.WriteAllText(System.IO.Path.Combine(ExeDir(), "ai-body.txt"), req, new UTF8Encoding(false)); }
                    catch (Exception) { }
                }
                int code = JadeHost.AiPostChat(req, out body);
                if (code == 400)
                {
                    // 有的兼容网关不认 thinking 参数，换成最普通的请求再试一次
                    JadeHost.Log("[aiocr] 第" + (bi + 1) + "段 HTTP 400，降级重试");
                    Rec("降级重试｜第" + (bi + 1) + "段 HTTP 400");
                    code = JadeHost.AiPostChat(BuildPayload(segs, idx, false), out body);
                }
                else if (code == 503)
                {
                    // 503 是服务端在喊「太忙了，稍后再试」：这不是内容问题，退避 1.2 秒
                    // 用同一份请求再试一次往往就过；500 才是真的服务端内部错误，不重试。
                    JadeHost.Log("[aiocr] 第" + (bi + 1) + "段 HTTP 503（服务太忙），退避后重试一次");
                    Rec("重试｜第" + (bi + 1) + "段 HTTP 503 服务太忙，1.2 秒后再试");
                    try { System.Threading.Thread.Sleep(1200); } catch (Exception) { }
                    code = JadeHost.AiPostChat(req, out body);
                }
                asked += idx.Count;
                if (code != 200)
                {
                    // 单段内容触发的服务端错误不该拖垮整轮；但网络真断了（code=0）或连续两段
                    // 失败就收手，否则一次截图会被拖到分钟级。
                    JadeHost.Log("[aiocr] 第" + (bi + 1) + "段 HTTP " + code + "，跳过本段");
                    JadeHost.Log("[aiocr] 失败回包 " + Clip(body, 220));
                    Rec("跳过｜第" + (bi + 1) + "段 HTTP " + code + "，" + Clip(body, 160));
                    fails++;
                    if (code == 0 || fails >= 2) { Rec("停止｜连续失败，剩下 " + (batches.Count - bi - 1) + " 段不送了"); break; }
                    continue;
                }

                int hit = 0;
                string reply = Content(body);
                string rawBody = body ?? "";
                JadeHost.Log("[aiocr] body(" + rawBody.Length + ") "
                    + (rawBody.Length > 150 ? rawBody.Substring(0, 150) + "…" : rawBody));
                JadeHost.Log("[aiocr] 回包(" + reply.Length + ") "
                    + (reply.Length > 220 ? reply.Substring(0, 220) + "…" : reply));
                Rec("段" + (bi + 1) + "｜行 " + idx[0] + "–" + idx[idx.Count - 1] + " 共 " + idx.Count + " 行");
                List<KeyValuePair<int, string>> fixes = ParseFix(reply);
                for (int i = 0; i < fixes.Count; i++)
                {
                    int id = fixes[i].Key;
                    if (id < 0 || id >= segs.Count || !idx.Contains(id))
                    {
                        rejected++;
                        Rec("  拒绝｜i=" + id + " 行号不在本段，忽略");
                        continue;
                    }
                    OcrSegment s = segs[id];
                    string oldText = s.Text ?? "";
                    string nt = fixes[i].Value;
                    string why;
                    if (!Safe(oldText, nt, idx.Count == 1, out why))
                    {
                        rejected++;
                        JadeHost.Log("[aiocr] 拒绝 i=" + id + " " + why + " | 原:" + Clip(oldText, 60) + " → 改:" + Clip(nt, 60));
                        Rec("  拒绝｜i=" + id + " " + why);
                        Rec("        原：" + oldText);
                        Rec("        改：" + nt);
                        continue;
                    }
                    if (nt != oldText)
                    {
                        s.Text = nt;
                        changed++;
                        any = true;
                        hit++;
                        JadeHost.Log("[aiocr] 采纳 i=" + id + " | 原:" + Clip(oldText, 60) + " → 新:" + Clip(nt, 60));
                        Rec("  采纳｜i=" + id);
                        Rec("        原：" + oldText);
                        Rec("        新：" + nt);
                    }
                }
                JadeHost.Log("[aiocr] 第" + (bi + 1) + "段 " + idx.Count + " 行 → 采纳 " + hit);
                Rec("       本段采纳 " + hit + " 处");
            }

            int cost = Environment.TickCount - t0;
            LastAsked = asked; LastChanged = changed; LastRejected = rejected; LastCostMs = cost;
            JadeHost.Log("[aiocr] 共 " + segs.Count + " 行 / 送审 " + asked + " / 改动 " + changed
                + " / 拒绝 " + rejected + " / 分段 " + batches.Count + " / " + cost + "ms");
            Rec("==== 完成 ====｜送审 " + asked + " 行 · 改动 " + changed + " 处 · 拒绝 " + rejected
                + " 处 · " + cost + "ms · " + (any ? "结果已替换原文" : "没有改动，保留原始识别"));
            if (!any) return null;
            return Join(segs);
        }

        // 按几何关系把相邻行聚成「段」，段内行数 / 字数都有上限，避免一次塞爆上下文
        private static List<List<int>> SplitBatches(List<OcrSegment> segs, List<int> targets)
        {
            List<List<int>> res = new List<List<int>>();
            List<int> cur = new List<int>();
            int chars = 0;
            Rectangle prev = Rectangle.Empty;
            HashSet<int> keep = new HashSet<int>(targets);

            for (int i = 0; i < segs.Count; i++)
            {
                if (!keep.Contains(i)) continue;
                string t = segs[i].Text ?? "";
                if (t.Trim().Length == 0) continue;

                bool same = cur.Count > 0 && SamePara(prev, segs[i].Box);
                if (cur.Count > 0 && (!same || cur.Count >= MaxLinesOneBatch || chars + t.Length > MaxCharsOneBatch))
                {
                    res.Add(cur);
                    cur = new List<int>();
                    chars = 0;
                }
                cur.Add(i);
                chars += t.Length;
                prev = segs[i].Box;
            }
            if (cur.Count > 0) res.Add(cur);
            return res;
        }

        private static List<int> SelectTargets(List<OcrSegment> segs)
        {
            List<int> ids = new List<int>();
            for (int i = 0; i < segs.Count; i++)
            {
                OcrSegment s = segs[i];
                if (s.LowConfidence || Suspicious(s.Text)) ids.Add(i);
            }
            return ids;
        }

        private static bool Suspicious(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            string t = text.Trim();
            if (t.Length == 0) return false;
            if (t.IndexOf('\uFFFD') >= 0) return true;
            for (int i = 0; i < t.Length; i++)
                if (char.IsControl(t[i])) return true;
            if (t.Length <= 2 && t.IndexOfAny(new char[] { '?', '？', '|', '¦' }) < 0)
            {
                for (int i = 0; i < t.Length; i++) if (char.IsLetter(t[i])) return true;
            }
            int q = 0;
            for (int i = 0; i < t.Length; i++)
                if (t[i] == '?' || t[i] == '？' || t[i] == '!' || t[i] == '！') q++;
            if (q >= 3 && q >= t.Length / 2) return true;
            return false;
        }

        private static bool SamePara(Rectangle a, Rectangle b)
        {
            if (a.Width <= 0 || a.Height <= 0) return true;
            int left = Math.Max(a.Left, b.Left);
            int right = Math.Min(a.Right, b.Right);
            if (right <= left) return false;
            double overlap = (double)(right - left) / Math.Min(a.Width, b.Width);
            int gap = b.Top - a.Bottom;
            return overlap >= 0.35 && gap >= -a.Height && gap <= a.Height * 2.0;
        }

        private static string BuildPayload(List<OcrSegment> segs, List<int> idx, bool jsonMode)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"model\":\"").Append(Esc(AppConfig.AiModel)).Append("\",");
            sb.Append("\"temperature\":0,\"stream\":false,");
            if (jsonMode)
            {
                // 只关思考，不加 response_format：两者同时出现时，DeepSeek 对数字密集的内容
                // （价格表、参数表）会直接 internal_error 500，加与不加都得自己解析，索性不要。
                sb.Append("\"thinking\":{\"type\":\"disabled\"},");
            }
            sb.Append("\"max_tokens\":").Append(Math.Max(200, idx.Count * 90)).Append(',');
            sb.Append("\"messages\":[{\"role\":\"system\",\"content\":\"").Append(Esc(SysPrompt)).Append("\"},");
            sb.Append("{\"role\":\"user\",\"content\":\"").Append(Esc(UserText(segs, idx))).Append("\"}]}");
            return sb.ToString();
        }

        private static string UserText(List<OcrSegment> segs, List<int> idx)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("这是同一段文字的 ").Append(idx.Count).Append(" 行 OCR 结果，按阅读顺序给你：\n");
            for (int i = 0; i < idx.Count; i++)
            {
                OcrSegment s = segs[idx[i]];
                sb.Append("{\"i\":").Append(idx[i]).Append(",\"t\":\"").Append(Esc(s.Text ?? "")).Append("\"}\n");
            }
            sb.Append("只返回 JSON，且只列改过的行，不要回抄没改的行。");
            return sb.ToString();
        }

        private static string Content(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            string c = MiniJson.GetString(body, "content");
            return c ?? "";
        }

        private static List<KeyValuePair<int, string>> ParseFix(string content)
        {
            List<KeyValuePair<int, string>> list = new List<KeyValuePair<int, string>>();
            if (string.IsNullOrEmpty(content)) return list;
            MatchCollection ms = FixRe.Matches(content);
            for (int i = 0; i < ms.Count; i++)
            {
                int id;
                if (!int.TryParse(ms[i].Groups[1].Value, out id)) continue;
                list.Add(new KeyValuePair<int, string>(id, Unesc(ms[i].Groups[2].Value)));
            }
            return list;
        }

        // 三道闸：改得太多 / 改得不像原句 / 动了数字，一律退回
        private static bool Safe(string oldText, string nt, bool single, out string why)
        {
            why = "";
            if (nt == null) { why = "空"; return false; }
            nt = nt.Trim();
            if (nt.Length == 0) { why = "改空了"; return false; }
            if (nt.IndexOf('\n') >= 0 || nt.IndexOf('\r') >= 0) { why = "带换行（想合行？）"; return false; }

            int d = Math.Abs(nt.Length - oldText.Length);
            int allow = single ? 1 : Math.Max(2, oldText.Length / 6);
            if (d > allow) { why = "长度差 " + d + " 超过 " + allow; return false; }

            double dice = Dice(oldText, nt);
            // 0.62 是实测出来的：us$ O.14 us$ O.435 → US$ 0.14 US$ 0.435 这种「大小写+O→0」
            // 的正解只有 0.67，卡 0.72 会把正确修改挡掉；低于 0.62 的多半是模型在改写而不是修字。
            double need = single ? 0.75 : 0.62;
            if (dice < need) { why = "相似度 " + dice.ToString("0.00") + " 低于 " + need.ToString("0.00"); return false; }

            // 「用户名 ·日期 · 楼层」这类 UI 元信息行，模型总想给分隔符后面补空格。
            // 只动了空白、又带分隔符的修改一律不认。
            if (StripWs(oldText) == StripWs(nt)
                && (oldText.IndexOf('·') >= 0 || oldText.IndexOf('|') >= 0))
            {
                why = "只在分隔符行加了空格";
                return false;
            }

            string[] nums = Nums(oldText);
            string[] got = Nums(nt);
            for (int i = 0; i < nums.Length; i++)
            {
                bool found = false;
                for (int j = 0; j < got.Length; j++) if (got[j] == nums[i]) { found = true; break; }
                if (!found) { why = "数字 " + nums[i] + " 被改"; return false; }
            }
            return true;
        }

        private static string[] Nums(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            MatchCollection ms = NumRe.Matches(s);
            string[] r = new string[ms.Count];
            for (int i = 0; i < ms.Count; i++) r[i] = ms[i].Value;
            return r;
        }

        private static double Dice(string a, string b)
        {
            if (a.Length == 0 && b.Length == 0) return 1;
            if (a.Length == 0 || b.Length == 0) return 0;
            Dictionary<char, int> pool = new Dictionary<char, int>();
            for (int i = 0; i < a.Length; i++)
            {
                if (pool.ContainsKey(a[i])) pool[a[i]]++;
                else pool[a[i]] = 1;
            }
            int inter = 0;
            for (int i = 0; i < b.Length; i++)
            {
                int v;
                if (pool.TryGetValue(b[i], out v) && v > 0) { pool[b[i]] = v - 1; inter++; }
            }
            return 2.0 * inter / (a.Length + b.Length);
        }

        private static string Join(List<OcrSegment> segs)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < segs.Count; i++)
            {
                string t = segs[i].Text ?? "";
                if (t.Length == 0) continue;
                if (sb.Length > 0) sb.Append("\r\n");
                sb.Append(t);
            }
            return sb.ToString();
        }

        private static string Clip(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // 人看的账本：只记「AI 有没有介入、介入后原文长什么样、改成了什么」
        // 优先写在 exe 同目录，方便直接找到；目录不可写再退回 AppData。
        private static void Rec(string line)
        {
            string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine;
            if (TryRec(ExeDir(), text)) return;
            TryRec(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XuYuan-SnapScan", "jade-data"), text);
        }

        private static string ExeDir()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
            catch (Exception) { }
            return null;
        }

        private static bool TryRec(string dir, string text)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "ai-ocr.log");
                if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 2L * 1024 * 1024)
                {
                    System.IO.File.Copy(path, path + ".1", true);
                    System.IO.File.Delete(path);
                }
                if (!System.IO.File.Exists(path) || new System.IO.FileInfo(path).Length == 0)
                {
                    System.IO.File.AppendAllText(path,
                        "# AI 识字强化记录：有没有介入、原文是什么、改成了什么（超过 2 MB 自动轮转成 ai-ocr.log.1）"
                        + Environment.NewLine, new UTF8Encoding(false));
                }
                System.IO.File.AppendAllText(path, text, new UTF8Encoding(false));
                return true;
            }
            catch (Exception) { return false; }
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Unesc(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\') { sb.Append(c); continue; }
                i++;
                if (i >= s.Length) break;
                char n = s[i];
                if (n == 'n') sb.Append('\n');
                else if (n == 'r') sb.Append('\r');
                else if (n == 't') sb.Append('\t');
                else if (n == '"') sb.Append('"');
                else if (n == '\\') sb.Append('\\');
                else if (n == 'u' && i + 4 < s.Length)
                {
                    string hex = s.Substring(i + 1, 4);
                    try { sb.Append((char)Convert.ToInt32(hex, 16)); i += 4; }
                    catch (Exception) { sb.Append('u'); }
                }
                else if (n == '/') sb.Append('/');
                else { sb.Append('\\'); sb.Append(n); }
            }
            return sb.ToString();
        }
    }
}
