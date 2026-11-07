using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;

namespace QRSnap
{
    // 两个「大模型直接干活」的功能：
    //  ① 翻译接管：不跑本地小模型，整段交给大模型翻；报错就报错，不偷偷退回本地模型
    //  ② 遮罩分组：判断哪几行是同一段，让遮罩块按段落统一字号，而不是逐行各算各的
    internal static class AiBoost
    {
        private static readonly object Gate = new object();
        // 同一屏反复截图很常见：进程内记一份原文→译文，命中就不再走网络
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();
        private const int CacheMax = 4000;
        private const int MaxLines = 16;     // 一次送多少行（太多时模型会漏行，漏了会自动补发）
        private const int MaxChars = 900;
        private const int TrBudgetMs = 20000;
        private const int MaskBudgetMs = 9000;
        private const int MaskMaxLines = 120;

        private static readonly Regex TrRe = new Regex(
            "\\{\\s*\"i\"\\s*:\\s*\"?(\\d+)\"?\\s*,\\s*\"t\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\}",
            RegexOptions.Singleline);
        private static readonly Regex GrpRe = new Regex("\\[[0-9\\s,]*\\]");

        private const string TrPrompt =
            "翻译引擎。把给出的每一行/每一段原文译成简体中文，行号原样保留，一行不漏。\n" +
            "原文来自截图 OCR，可能带形近字/漏字/粘连错误；请先按上下文在内部纠正，再翻译，" +
            "不要把纠正过程或纠正后的原文输出（例：custcenes→cutscenes、fltering→filtering、Ifit→If it、" +
            "Ul→UI、officialy→officially、nvn ir→nvn interval）。\n" +
            "只输出 {\"tr\":[{\"i\":N,\"t\":\"译文\"}]}，不要解释、不要围栏、不要加注。\n" +
            "规则：用户名（如 masagrator、shaunj66）、ID、产品名（如 Switch Frame Tap）、版本号、链接、数字原样保留，不要音译；\n" +
            "游戏名用中文通用译名并加书名号（Xenoblade Chronicles→《异度之刃》，Xenoblade Chronicles Definitive Edition→《异度之刃：决定版》，Zelda Wind Waker→《塞尔达传说：风之杖》，Wind Waker HD→《风之杖 HD》，Wave Race 64→《波浪赛车64》）；\n" +
            "论坛标签按中文习惯（Homebrew→自制程序，Homebrew app→自制应用，ROM Hack→ROM黑客，Front-page→首页，Replies→回复数，Views→浏览量）；\n" +
            "没有通用中文名的专有名词保留英文。宁可直译，不要意译、不要扩写。";

        // 只有效率档（0.5B 短句）追加：多义词优先游戏行业术语
        private const string GamePrompt =
            "\n针对短句翻译时，若有多重意思的词尽量以游戏行业术语翻译。";

        private const string MaskPrompt =
            "截图 OCR 行的版面判断。每行格式：行号|x,y,宽,高|文本。\n" +
            "判断哪些行属于同一段：同一栏、上下紧邻、上一行话没说完由下一行接着。\n" +
            "只输出 {\"p\":[[0,1,2],[3],[4,5]]}：每组是连续行号，覆盖全部行，按阅读顺序。\n" +
            "拿不准就一行一组。不要输出别的。";

        // ================= 翻译接管 =================
        // 返回 null 表示失败，err 里带给人看的原因；调用方应直接提示，不要回退本地模型
        public static Dictionary<int, string> TranslateAll(List<OcrSegment> segs, string src, out string err)
        {
            err = null;
            if (segs == null || segs.Count == 0) { err = "没有识别到文字"; return null; }
            if (string.IsNullOrEmpty(AppConfig.AiKey)) { err = "设置里没有填 API Key"; return null; }
            if (!JadeHost.AiReadyNow()) { err = "AI 未就绪，先到设置里点「保存」校验一次"; return null; }

            lock (Gate)
            {
                Dictionary<int, string> res = new Dictionary<int, string>();
                // 固定标签/日期先用本地词表定下来，不送模型：既快，也不会出现同屏两种译法
                List<int> need = new List<int>();
                for (int i = 0; i < segs.Count; i++)
                {
                    string text = segs[i].Text ?? "";
                    if (text.Trim().Length == 0) continue;
                    string pre = LayoutBoost.PreTranslate(text);
                    if (pre != null) { res[i] = pre; continue; }
                    string ck = CacheKey(src, text);
                    string cv;
                    if (Cache.TryGetValue(ck, out cv) && cv.Length > 0) { res[i] = cv; continue; }
                    need.Add(i);
                }

                List<List<int>> batches = SplitIndices(segs, need);
                int t0 = Environment.TickCount;
                if (batches.Count == 0)
                {
                    Rec("接管完成｜" + segs.Count + " 行 · 全部命中词表/缓存 · 0ms");
                    return res;
                }
                for (int bi = 0; bi < batches.Count; bi++)
                {
                    if (Environment.TickCount - t0 > TrBudgetMs)
                    {
                        err = "超过 " + (TrBudgetMs / 1000) + " 秒还没翻完（共 " + batches.Count + " 段，停在 " + bi + " 段）";
                        return null;
                    }
                    List<int> idx = batches[bi];
                    string req = BuildTrPayload(segs, idx, src);
                    int code;
                    string body = "";
                    code = JadeHost.AiPostChat(req, out body);
                    if (code == 503)
                    {
                        try { System.Threading.Thread.Sleep(1000); } catch (Exception) { }
                        code = JadeHost.AiPostChat(req, out body);
                    }
                    if (code != 200)
                    {
                        string why = HttpWhy(code, body);
                        JadeHost.Log("[aimt] 第" + (bi + 1) + "段 " + why);
                        Rec("接管失败｜第" + (bi + 1) + "段 " + why);
                        err = "第 " + (bi + 1) + " 段请求失败（" + why + "）";
                        return null;
                    }
                    string reply = Content(body);
                    List<KeyValuePair<int, string>> got = ParseTr(reply);
                    if (got.Count == 0)
                    {
                        JadeHost.Log("[aimt] 第" + (bi + 1) + "段 回包无可用译文：" + Clip(reply, 160));
                        Rec("接管失败｜第" + (bi + 1) + "段 返回内容解析不出译文");
                        err = "模型没有返回可用的译文（第 " + (bi + 1) + " 段）";
                        return null;
                    }
                    int miss = 0;
                    List<int> missIds = new List<int>();
                    for (int i = 0; i < idx.Count; i++)
                    {
                        int id = idx[i];
                        string tr = null;
                        for (int k = 0; k < got.Count; k++)
                            if (got[k].Key == id) { tr = got[k].Value; break; }
                        if (tr == null || tr.Trim().Length == 0)
                        {
                            // 漏行不算整体失败，这一行先用原文顶上，日志里记一笔
                            miss++;
                            missIds.Add(id);
                            tr = segs[id].Text ?? "";
                        }
                        else
                        {
                            Remember(CacheKey(src, segs[id].Text ?? ""), tr);
                        }
                        res[id] = tr.Trim();
                    }
                    JadeHost.Log("[aimt] 第" + (bi + 1) + "段 " + idx.Count + " 行，漏 " + miss);

                    // 模型偶尔会跳过某几行（尤其是和上一行很像的日期/元信息）。
                    // 补一次只含漏行的请求，比让这几行留在英文好；仍然遵守总预算。
                    if (missIds.Count > 0 && Environment.TickCount - t0 < TrBudgetMs)
                    {
                        int c2;
                        string b2 = "";
                        c2 = JadeHost.AiPostChat(BuildTrPayload(segs, missIds, src), out b2);
                        if (c2 == 200)
                        {
                            List<KeyValuePair<int, string>> got2 = ParseTr(Content(b2));
                            int fixedN = 0;
                            for (int i = 0; i < missIds.Count; i++)
                            {
                                int id = missIds[i];
                                for (int k = 0; k < got2.Count; k++)
                                    if (got2[k].Key == id && got2[k].Value.Trim().Length > 0)
                                    {
                                        res[id] = got2[k].Value.Trim();
                                        Remember(CacheKey(src, segs[id].Text ?? ""), res[id]);
                                        fixedN++;
                                        break;
                                    }
                            }
                            JadeHost.Log("[aimt] 第" + (bi + 1) + "段 补漏 " + fixedN + "/" + missIds.Count);
                        }
                    }
                }
                Rec("接管完成｜" + segs.Count + " 行 · " + batches.Count + " 段 · "
                    + (Environment.TickCount - t0) + "ms");
                return res;
            }
        }

        // ================= 译文质检：只重翻低质量行 =================
        public static Dictionary<int, string> CheckTranslations(List<OcrSegment> segs, string[] trs, string src, out string err)
        {
            err = null;
            if (segs == null || trs == null || segs.Count == 0) return null;
            if (string.IsNullOrEmpty(AppConfig.AiKey) || !JadeHost.AiReadyNow()) return null;
            List<int> bad = new List<int>();
            for (int i = 0; i < segs.Count; i++)
            {
                string st = segs[i].Text == null ? "" : segs[i].Text.Trim();
                if (st.Length == 0) continue;
                if (LayoutBoost.PreTranslate(st) != null) continue;
                string tt = trs[i] == null ? "" : trs[i].Trim();
                if (IsBadTranslation(st, tt)) bad.Add(i);
            }
            if (bad.Count == 0)
            {
                JadeHost.Log("[aicheck] 没有低质量行，跳过");
                return null;
            }

            List<OcrSegment> sub = new List<OcrSegment>();
            List<int> map = new List<int>();
            for (int k = 0; k < bad.Count; k++)
            {
                OcrSegment s = new OcrSegment();
                s.Text = segs[bad[k]].Text;
                s.Box = segs[bad[k]].Box;
                s.Angle = segs[bad[k]].Angle;
                s.Order = segs[bad[k]].Order;
                sub.Add(s);
                map.Add(bad[k]);
            }
            Dictionary<int, string> got = TranslateAll(sub, src, out err);
            if (got == null) return null;
            Dictionary<int, string> res = new Dictionary<int, string>();
            for (int k = 0; k < map.Count; k++)
            {
                string t;
                if (got.TryGetValue(k, out t) && !string.IsNullOrEmpty(t))
                    res[map[k]] = t.Trim();
            }
            JadeHost.Log("[aicheck] 低质量 " + bad.Count + " 行，修回 " + res.Count + " 行");
            return res.Count == 0 ? null : res;
        }

        private static bool IsBadTranslation(string src, string tr)
        {
            if (string.IsNullOrEmpty(tr)) return true;
            if (string.Equals(src, tr, StringComparison.Ordinal)) return !LooksLikeKeep(src);
            if (tr.IndexOf("抱歉", StringComparison.Ordinal) >= 0
                || tr.IndexOf("无法", StringComparison.Ordinal) >= 0
                || tr.IndexOf("作为AI", StringComparison.OrdinalIgnoreCase) >= 0
                || tr.IndexOf("以下是", StringComparison.Ordinal) >= 0
                || tr.IndexOf("这段文字", StringComparison.Ordinal) >= 0
                || tr.IndexOf("翻译成", StringComparison.Ordinal) >= 0)
                return true;
            double ratio = tr.Length / (double)Math.Max(1, src.Length);
            if (ratio > 3.0 || ratio < 0.15) return true;
            if (tr.Length > 12)
            {
                int q = 0;
                for (int i = 0; i < tr.Length; i++) if (tr[i] == '?' || tr[i] == '？') q++;
                if (q >= 3) return true;
            }
            return false;
        }

        private static bool LooksLikeKeep(string s)
        {
            if (s.Length > 40) return false;
            bool hasDigit = false, hasLetter = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') hasDigit = true;
                else if (char.IsLetter(c)) hasLetter = true;
            }
            if (!hasLetter) return true;
            if (hasDigit && s.IndexOf(' ') < 0) return true;
            return false;
        }

        private const string MergePrompt =
            "截图 OCR 行的短句合并。每行格式：行号|x,y,宽,高|文本。\n" +
            "找出同一栏、上下紧邻、被 OCR 拆开的同一句：上一行没有句末标点，下一行以小写字母或 and/or/but/of/the/to/in/with/for 等词开头，或明显是上一句的续行。\n" +
            "导航栏、页眉、独立标题、日期、用户名不要合并。\n" +
            "只输出 {\"m\":[[0,1,2],[3]]}：每组是连续行号，覆盖全部行，按阅读顺序；不确定就一行一组。不要输出别的。";

        // ================= 短句合并 =================
        public static List<List<int>> MergeShortLines(List<OcrSegment> segs)
        {
            if (segs == null || segs.Count < 3 || segs.Count > MaskMaxLines) return null;
            if (string.IsNullOrEmpty(AppConfig.AiKey)) return null;
            if (!JadeHost.AiReadyNow()) return null;

            lock (Gate)
            {
                string req = BuildMergePayload(segs);
                int code;
                string body = "";
                code = JadeHost.AiPostChat(req, out body);
                if (code != 200)
                {
                    JadeHost.Log("[aimerge] HTTP " + code + "，跳过短句合并");
                    return null;
                }
                string reply = Content(body);
                List<List<int>> raw = ParseGroups(reply);
                if (raw.Count == 0)
                {
                    JadeHost.Log("[aimerge] 回包没有分组结果：" + Clip(reply, 200));
                    return null;
                }
                List<List<int>> ok = Check(segs, raw);
                if (ok == null)
                {
                    JadeHost.Log("[aimerge] 行号/几何校验没过，整份作废：" + Clip(reply, 240));
                    return null;
                }
                JadeHost.Log("[aimerge] 分组 " + ok.Count + " 段 / " + segs.Count + " 行");
                return ok;
            }
        }

        public static List<OcrSegment> ApplyGroups(List<OcrSegment> segs, List<List<int>> groups)
        {
            if (segs == null || groups == null) return segs;
            List<OcrSegment> result = new List<OcrSegment>();
            for (int g = 0; g < groups.Count; g++)
            {
                List<int> ids = groups[g];
                if (ids == null || ids.Count == 0) continue;
                if (ids.Count == 1)
                {
                    result.Add(segs[ids[0]]);
                    continue;
                }
                OcrSegment one = new OcrSegment();
                one.Angle = segs[ids[0]].Angle;
                one.Order = segs[ids[0]].Order;
                one.LowConfidence = false;
                StringBuilder text = new StringBuilder();
                List<Rectangle> boxes = new List<Rectangle>();
                Rectangle box = segs[ids[0]].Box;
                for (int k = 0; k < ids.Count; k++)
                {
                    OcrSegment s = segs[ids[k]];
                    if (text.Length > 0 && NeedSpace(text.ToString(), s.Text)) text.Append(' ');
                    text.Append(s.Text);
                    box = Rectangle.Union(box, s.Box);
                    if (s.SourceBoxes != null) boxes.AddRange(s.SourceBoxes);
                    else boxes.Add(s.Box);
                    if (s.LowConfidence) one.LowConfidence = true;
                }
                one.Text = text.ToString();
                one.Box = box;
                one.SourceBoxes = boxes;
                result.Add(one);
            }
            return result;
        }

        private static bool NeedSpace(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            char a = left[left.Length - 1], b = right[0];
            return (a < 128 && b < 128);
        }

        private static string BuildMergePayload(List<OcrSegment> segs)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"model\":\"").Append(Esc(AppConfig.AiModel)).Append("\",");
            sb.Append("\"thinking\":{\"type\":\"disabled\"},");
            sb.Append("\"temperature\":0,");
            sb.Append("\"max_tokens\":").Append(Math.Max(200, segs.Count * 12)).Append(',');
            sb.Append("\"messages\":[{\"role\":\"system\",\"content\":\"").Append(Esc(MergePrompt)).Append("\"},");
            StringBuilder u = new StringBuilder();
            u.Append("共 ").Append(segs.Count).Append(" 行：\n");
            for (int i = 0; i < segs.Count; i++)
            {
                Rectangle b = segs[i].Box;
                u.Append(i).Append('|').Append(b.X).Append(',').Append(b.Y).Append(',')
                 .Append(b.Width).Append(',').Append(b.Height).Append('|')
                 .Append(segs[i].Text ?? "").Append('\n');
            }
            u.Append("只返回 JSON。");
            sb.Append("{\"role\":\"user\",\"content\":\"").Append(Esc(u.ToString())).Append("\"}]}");
            return sb.ToString();
        }

        public static void ClearCache()
        {
            lock (Gate) Cache.Clear();
        }

        // ================= 遮罩分组 =================
        // 返回 null 表示不介入（按几何默认处理）；不做删行，只做分组
        public static List<List<int>> PlanMask(List<OcrSegment> segs)
        {
            if (segs == null || segs.Count < 3 || segs.Count > MaskMaxLines) return null;
            if (string.IsNullOrEmpty(AppConfig.AiKey)) return null;
            if (!JadeHost.AiReadyNow()) return null;

            lock (Gate)
            {
                string req = BuildMaskPayload(segs);
                int code;
                string body = "";
                code = JadeHost.AiPostChat(req, out body);
                if (code != 200)
                {
                    JadeHost.Log("[aimask] HTTP " + code + "，跳过遮罩强化");
                    return null;
                }
                string reply = Content(body);
                List<List<int>> raw = ParseGroups(reply);
                if (raw.Count == 0)
                {
                    JadeHost.Log("[aimask] 回包没有分组结果：" + Clip(reply, 200));
                    return null;
                }
                List<List<int>> ok = Check(segs, raw);
                if (ok == null)
                {
                    JadeHost.Log("[aimask] 行号不重不漏都没做到，整份作废：" + Clip(reply, 240));
                    return null;
                }
                JadeHost.Log("[aimask] 分组 " + ok.Count + " 段 / " + segs.Count + " 行");
                return ok;
            }
        }

        // ================= 分段 =================
        // 只把「需要模型翻」的行号分批；词表/缓存命中的行不占上下文。
        private static List<List<int>> SplitIndices(List<OcrSegment> segs, List<int> ids)
        {
            List<List<int>> res = new List<List<int>>();
            List<int> cur = new List<int>();
            int chars = 0;
            for (int k = 0; k < ids.Count; k++)
            {
                int i = ids[k];
                string t = segs[i].Text ?? "";
                if (t.Trim().Length == 0) continue;
                if (cur.Count > 0 && (cur.Count >= MaxLines || chars + t.Length > MaxChars))
                {
                    res.Add(cur);
                    cur = new List<int>();
                    chars = 0;
                }
                cur.Add(i);
                chars += t.Length;
            }
            if (cur.Count > 0) res.Add(cur);
            return res;
        }

        private static string CacheKey(string src, string text)
        {
            return AppConfig.AiModel + "\u0001" + src + "\u0001" + (text ?? "");
        }

        private static void Remember(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) return;
            if (key.Length > 800) return;
            if (Cache.Count >= CacheMax)
            {
                // 简单淘汰：清一半，避免无限涨
                List<string> keys = new List<string>(Cache.Keys);
                for (int i = 0; i < keys.Count / 2; i++) Cache.Remove(keys[i]);
            }
            Cache[key] = value;
        }

        private static string BuildTrPayload(List<OcrSegment> segs, List<int> idx, string src)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"model\":\"").Append(Esc(AppConfig.AiModel)).Append("\",");
            sb.Append("\"thinking\":{\"type\":\"disabled\"},");
            sb.Append("\"temperature\":0,");
            sb.Append("\"max_tokens\":").Append(Math.Max(300, idx.Count * 200)).Append(',');
            string sysPrompt = TrPrompt;
            if (AppConfig.NormalizeMtProfile(AppConfig.MtProfile) == AppConfig.MtProfileEfficiency)
                sysPrompt += GamePrompt;
            sb.Append("\"messages\":[{\"role\":\"system\",\"content\":\"").Append(Esc(sysPrompt)).Append("\"},");
            StringBuilder u = new StringBuilder();
            u.Append("源语种：").Append(src).Append("。共 ").Append(idx.Count).Append(" 行：\n");
            for (int i = 0; i < idx.Count; i++)
            {
                OcrSegment s = segs[idx[i]];
                u.Append("{\"i\":").Append(idx[i]).Append(",\"t\":\"").Append(Esc(s.Text ?? "")).Append("\"}\n");
            }
            u.Append("只返回 JSON。");
            sb.Append("{\"role\":\"user\",\"content\":\"").Append(Esc(u.ToString())).Append("\"}]}");
            return sb.ToString();
        }

        private static string BuildMaskPayload(List<OcrSegment> segs)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"model\":\"").Append(Esc(AppConfig.AiModel)).Append("\",");
            sb.Append("\"thinking\":{\"type\":\"disabled\"},");
            sb.Append("\"temperature\":0,");
            sb.Append("\"max_tokens\":").Append(Math.Max(200, segs.Count * 12)).Append(',');
            sb.Append("\"messages\":[{\"role\":\"system\",\"content\":\"").Append(Esc(MaskPrompt)).Append("\"},");
            StringBuilder u = new StringBuilder();
            u.Append("共 ").Append(segs.Count).Append(" 行：\n");
            for (int i = 0; i < segs.Count; i++)
            {
                Rectangle b = segs[i].Box;
                u.Append(i).Append('|').Append(b.X).Append(',').Append(b.Y).Append(',')
                 .Append(b.Width).Append(',').Append(b.Height).Append('|')
                 .Append(segs[i].Text ?? "").Append('\n');
            }
            u.Append("只返回 JSON。");
            sb.Append("{\"role\":\"user\",\"content\":\"").Append(Esc(u.ToString())).Append("\"}]}");
            return sb.ToString();
        }

        // ================= 解析 =================
        private static List<KeyValuePair<int, string>> ParseTr(string content)
        {
            List<KeyValuePair<int, string>> list = new List<KeyValuePair<int, string>>();
            if (string.IsNullOrEmpty(content)) return list;
            MatchCollection ms = TrRe.Matches(content);
            for (int i = 0; i < ms.Count; i++)
            {
                int id;
                if (!int.TryParse(ms[i].Groups[1].Value, out id)) continue;
                list.Add(new KeyValuePair<int, string>(id, Unesc(ms[i].Groups[2].Value)));
            }
            return list;
        }

        private static List<List<int>> ParseGroups(string content)
        {
            List<List<int>> res = new List<List<int>>();
            if (string.IsNullOrEmpty(content)) return res;
            MatchCollection ms = GrpRe.Matches(content);
            for (int i = 0; i < ms.Count; i++)
            {
                string inner = ms[i].Value.Trim('[', ']');
                List<int> one = new List<int>();
                string[] parts = inner.Split(',');
                for (int k = 0; k < parts.Length; k++)
                {
                    string p = parts[k].Trim();
                    if (p.Length == 0) continue;
                    int v;
                    if (!int.TryParse(p, out v)) { one.Clear(); break; }
                    one.Add(v);
                }
                if (one.Count > 0) res.Add(one);
            }
            return res;
        }

        // 分组必须：行号不重不漏、组内连续、差得不多、几何上同栏且上下相邻
        private static List<List<int>> Check(List<OcrSegment> segs, List<List<int>> raw)
        {
            int[] seen = new int[segs.Count];
            for (int g = 0; g < raw.Count; g++)
                for (int k = 0; k < raw[g].Count; k++)
                {
                    int id = raw[g][k];
                    if (id < 0 || id >= segs.Count) return null;
                    seen[id]++;
                }
            for (int i = 0; i < seen.Length; i++)
                if (seen[i] != 1) return null;

            List<List<int>> ok = new List<List<int>>();
            for (int g = 0; g < raw.Count; g++)
            {
                List<int> one = raw[g];
                if (one.Count == 1) { ok.Add(one); continue; }
                bool bad = one.Count > 6;
                for (int k = 1; k < one.Count && !bad; k++)
                {
                    if (one[k] != one[k - 1] + 1) { bad = true; break; }      // 只允许相邻行成组
                    Rectangle a = segs[one[k - 1]].Box, b = segs[one[k]].Box;
                    int ow = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                    int hAvg = (a.Height + b.Height) / 2;
                    int vGap = b.Top - a.Bottom;
                    if (ow < Math.Min(a.Width, b.Width) * 0.5f) bad = true;          // 横向对不上，可能跨栏
                    else if (vGap < -Math.Max(4, hAvg / 2) || vGap > Math.Max(8, hAvg)) bad = true;
                    else if (Math.Abs(a.Height - b.Height) > Math.Max(8, hAvg / 2)) bad = true;   // 字号差太多
                }
                if (bad)
                {
                    // 这一组几何上站不住：拆成单行，不影响其它组
                    for (int k = 0; k < one.Count; k++) ok.Add(new List<int>(new int[] { one[k] }));
                    continue;
                }
                ok.Add(one);
            }
            return ok;
        }

        // ================= 小工具 =================
        private static string Content(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            string c = MiniJson.GetString(body, "content");
            return c ?? "";
        }

        private static string HttpWhy(int code, string body)
        {
            if (code == 0) return "连不上：" + Clip(body, 60);
            string msg = "";
            try { msg = MiniJson.GetString(body, "message") ?? ""; } catch (Exception) { }
            if (msg.Length == 0) msg = Clip(body, 80);
            return "HTTP " + code + " " + msg;
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append(' ');
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string Unesc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                i++;
                char n = s[i];
                if (n == 'n') sb.Append('\n');
                else if (n == 'r') sb.Append('\r');
                else if (n == 't') sb.Append('\t');
                else if (n == 'u' && i + 4 < s.Length)
                {
                    string hex = s.Substring(i + 1, 4);
                    int v = 0;
                    bool good = true;
                    for (int k = 0; k < 4; k++)
                    {
                        char h = hex[k];
                        int d;
                        if (h >= '0' && h <= '9') d = h - '0';
                        else if (h >= 'a' && h <= 'f') d = h - 'a' + 10;
                        else if (h >= 'A' && h <= 'F') d = h - 'A' + 10;
                        else { good = false; break; }
                        v = v * 16 + d;
                    }
                    if (good) { sb.Append((char)v); i += 4; }
                    else sb.Append("\\u");
                }
                else sb.Append(n);
            }
            return sb.ToString();
        }

        private static string Clip(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static void Rec(string line)
        {
            string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);
                if (string.IsNullOrEmpty(dir)) return;
                string path = dir + "\\ai-mt.log";
                try
                {
                    if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 2L * 1024 * 1024)
                    {
                        if (System.IO.File.Exists(path + ".1")) System.IO.File.Delete(path + ".1");
                        System.IO.File.Move(path, path + ".1");
                    }
                }
                catch (Exception) { }
                System.IO.File.AppendAllText(path, text, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }
    }
}
