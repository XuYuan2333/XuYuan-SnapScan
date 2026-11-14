using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace QRSnap
{
    // 腾讯云机器翻译（TMT）在线接口：TC3-HMAC-SHA256 签名，整批走 HTTP，
    // 全程不加载本地翻译模型。返回数组与输入等长，失败返回 null 并把原因写进 LastError。
    public static class TencentMt
    {
        private const string HostName = "tmt.tencentcloudapi.com";
        private const string ServiceName = "tmt";
        private const string ApiRegion = "ap-guangzhou";
        private const string ApiVersion = "2018-03-21";

        public static string LastError = "";
        public static long LastMs;

        public static bool Configured
        {
            get { return !string.IsNullOrEmpty(AppConfig.TmtId) && !string.IsNullOrEmpty(AppConfig.TmtKey); }
        }

        // 语言码：en / ja / zh 三家通用，中文统一用 zh
        private static string LangCode(string code, bool isTarget)
        {
            string s = (code ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return isTarget ? "zh" : "en";
            if (s == "zh-cn" || s == "zh-hans" || s == "cmn") return "zh";
            if (s == "zh-tw" || s == "zh-hant") return "zh";   // TMT 不分简繁目标，统一 zh
            return s;
        }

        public static string SelfTest()
        {
            string[] got = TranslateBatch(new string[] { "Hello, this is a test." }, "en", "zh");
            if (got == null || got.Length == 0) return null;
            if (string.IsNullOrEmpty(got[0])) { LastError = "接口返回空译文"; return null; }
            return got[0];
        }

        public static string[] TranslateBatch(string[] texts, string src, string tgt)
        {
            LastError = "";
            LastMs = 0;
            if (texts == null || texts.Length == 0) return new string[0];
            if (!Configured) { LastError = "还没填腾讯云 SecretId / SecretKey"; return null; }

            long t0 = Environment.TickCount;
            string from = LangCode(src, false);
            string to = LangCode(tgt, true);
            string[] done = new string[texts.Length];

            int i = 0;
            while (i < texts.Length)
            {
                int n = Math.Min(20, texts.Length - i);
                string[] one = new string[n];
                for (int k = 0; k < n; k++) one[k] = texts[i + k] ?? "";
                string[] got = TranslateChunk(one, from, to);
                if (got == null) return null;
                for (int k = 0; k < n; k++) done[i + k] = got[k];
                i += n;
            }
            LastMs = Environment.TickCount - t0;
            return done;
        }

        // 一批（最多 20 条）：优先批量接口，接口不认就退回逐条
        private static string[] TranslateChunk(string[] texts, string from, string to)
        {
            string[] batch = CallBatch(texts, from, to);
            if (batch != null) return batch;
            if (IsUnsupportedAction()) return CallOneByOne(texts, from, to);
            return null;
        }

        private static bool IsUnsupportedAction()
        {
            if (LastError == null) return false;
            // 批量接口不可用/参数不被接受时，退回逐条接口；鉴权类错误不退回
            return LastError.IndexOf("InvalidAction", StringComparison.Ordinal) >= 0
                || LastError.IndexOf("UnsupportedOperation", StringComparison.Ordinal) >= 0
                || LastError.IndexOf("InvalidParameter", StringComparison.Ordinal) >= 0
                || LastError.IndexOf("UnsupportedRegion", StringComparison.Ordinal) >= 0;
        }

        private static string[] CallBatch(string[] texts, string from, string to)
        {
            StringBuilder body = new StringBuilder();
            body.Append("{\"SourceTextList\":[");
            for (int i = 0; i < texts.Length; i++)
            {
                if (i > 0) body.Append(',');
                body.Append('"').Append(Esc(texts[i])).Append('"');
            }
            body.Append("],\"Source\":\"").Append(from).Append("\",\"Target\":\"").Append(to).Append("\",\"ProjectId\":0}");

            string resp = PostSigned("TextTranslateBatch", body.ToString());
            if (resp == null) return null;
            string[] list = ParseArray(resp, "TargetTextList");
            if (list == null) return null;
            if (list.Length < texts.Length)
            {
                string[] fixedList = new string[texts.Length];
                for (int i = 0; i < texts.Length; i++) fixedList[i] = i < list.Length ? list[i] : "";
                return fixedList;
            }
            return list;
        }

        private static string[] CallOneByOne(string[] texts, string from, string to)
        {
            string[] r = new string[texts.Length];
            for (int i = 0; i < texts.Length; i++)
            {
                StringBuilder body = new StringBuilder();
                body.Append("{\"SourceText\":\"").Append(Esc(texts[i]))
                    .Append("\",\"Source\":\"").Append(from)
                    .Append("\",\"Target\":\"").Append(to).Append("\",\"ProjectId\":0}");
                string resp = PostSigned("TextTranslate", body.ToString());
                if (resp == null) return null;
                string t = ParseString(resp, "TargetText");
                r[i] = t ?? "";
            }
            return r;
        }

        private static string PostSigned(string action, string body)
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 768 | 192); }
            catch (Exception) { }

            long ts = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            string date = DateTime.UtcNow.ToString("yyyy-MM-dd");
            string contentType = "application/json; charset=utf-8";
            string actionLower = action.ToLowerInvariant();

            // 1. 规范请求
            string canonicalHeaders = "content-type:" + contentType + "\n"
                + "host:" + HostName + "\n"
                + "x-tc-action:" + actionLower + "\n";
            string signedHeaders = "content-type;host;x-tc-action";
            string hashedBody = Sha256Hex(body);
            string canonicalRequest = "POST" + "\n"
                + "/" + "\n"
                + "" + "\n"
                + canonicalHeaders + "\n"
                + signedHeaders + "\n"
                + hashedBody;

            // 2. 待签串
            string scope = date + "/" + ServiceName + "/tc3_request";
            string stringToSign = "TC3-HMAC-SHA256" + "\n"
                + ts + "\n"
                + scope + "\n"
                + Sha256Hex(canonicalRequest);

            // 3. 派生签名密钥
            byte[] kSecret = Encoding.UTF8.GetBytes("TC3" + AppConfig.TmtKey.Trim());
            byte[] kDate = HmacSha256(kSecret, date);
            byte[] kService = HmacSha256(kDate, ServiceName);
            byte[] kSigning = HmacSha256(kService, "tc3_request");
            string signature = Hex(HmacSha256(kSigning, stringToSign));

            string auth = "TC3-HMAC-SHA256 Credential=" + AppConfig.TmtId.Trim() + "/" + scope
                + ", SignedHeaders=" + signedHeaders + ", Signature=" + signature;

            byte[] data = Encoding.UTF8.GetBytes(body);
            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create("https://" + HostName + "/");
                req.Method = "POST";
                req.ContentType = contentType;
                req.UserAgent = "SnapScan";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.Headers.Add("Authorization", auth);
                req.Headers.Add("X-TC-Action", action);
                req.Headers.Add("X-TC-Version", ApiVersion);
                req.Headers.Add("X-TC-Timestamp", ts.ToString());
                req.Headers.Add("X-TC-Region", ApiRegion);
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    string code = ParseString(json, "Code");
                    if (code != null && code.Length > 0)
                    {
                        string msg = ParseString(json, "Message");
                        LastError = code + " " + (msg ?? "");
                        return null;
                    }
                    return json;
                }
            }
            catch (WebException wex)
            {
                try
                {
                    HttpWebResponse hr = wex.Response as HttpWebResponse;
                    if (hr != null)
                    {
                        string detail = "";
                        using (StreamReader sr = new StreamReader(hr.GetResponseStream(), Encoding.UTF8))
                            detail = sr.ReadToEnd();
                        string code = ParseString(detail, "Code");
                        string msg = ParseString(detail, "Message");
                        LastError = code != null ? (code + " " + (msg ?? "")) : ("HTTP " + (int)hr.StatusCode);
                        hr.Close();
                    }
                    else LastError = wex.Message;
                }
                catch (Exception) { LastError = wex.Message; }
                if (wex.Status == WebExceptionStatus.Timeout) LastError = "请求超时（15 秒）";
                return null;
            }
            catch (Exception ex) { LastError = ex.Message; return null; }
            finally { if (req != null) { try { req.Abort(); } catch (Exception) { } } }
        }

        private static string Sha256Hex(string s)
        {
            using (SHA256 sha = new SHA256Managed())
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
        }

        private static byte[] HmacSha256(byte[] key, string data)
        {
            using (HMACSHA256 h = new HMACSHA256(key))
                return h.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        private static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b.Length * 2);
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        // ---------------- 极简 JSON 取值（响应结构固定，够用） ----------------

        private static string ParseString(string json, string key)
        {
            if (json == null) return null;
            int a = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (a < 0) return null;
            int c = a + key.Length + 2;
            while (c < json.Length && IsSpace(json[c])) c++;
            if (c < json.Length && json[c] == ':') c++;
            while (c < json.Length && IsSpace(json[c])) c++;
            if (c >= json.Length || json[c] != '"') return null;
            c++;
            StringBuilder sb = new StringBuilder();
            while (c < json.Length)
            {
                char ch = json[c];
                if (ch == '\\') { c = Unescape(json, c, sb); continue; }
                if (ch == '"') break;
                sb.Append(ch);
                c++;
            }
            return sb.ToString();
        }

        private static string[] ParseArray(string json, string key)
        {
            if (json == null) return null;
            int a = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (a < 0) return null;
            int c = a + key.Length + 2;
            while (c < json.Length && IsSpace(json[c])) c++;
            if (c < json.Length && json[c] == ':') c++;
            while (c < json.Length && IsSpace(json[c])) c++;
            if (c >= json.Length || json[c] != '[') return null;
            c++;
            List<string> list = new List<string>();
            while (c < json.Length)
            {
                while (c < json.Length && (IsSpace(json[c]) || json[c] == ',')) c++;
                if (c >= json.Length || json[c] == ']') break;
                if (json[c] != '"')
                {
                    // 不是字符串，跳过到下一个逗号，保证不错位
                    while (c < json.Length && json[c] != ',' && json[c] != ']') c++;
                    continue;
                }
                c++;
                StringBuilder sb = new StringBuilder();
                while (c < json.Length)
                {
                    char ch = json[c];
                    if (ch == '\\') { c = Unescape(json, c, sb); continue; }
                    if (ch == '"') { c++; break; }
                    sb.Append(ch);
                    c++;
                }
                list.Add(sb.ToString());
            }
            return list.Count == 0 ? null : list.ToArray();
        }

        private static bool IsSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\r' || c == '\n';
        }

        private static int Unescape(string s, int i, StringBuilder sb)
        {
            if (i + 1 >= s.Length) { sb.Append('\\'); return i + 1; }
            char e = s[i + 1];
            switch (e)
            {
                case '"': sb.Append('"'); return i + 2;
                case '\\': sb.Append('\\'); return i + 2;
                case '/': sb.Append('/'); return i + 2;
                case 'b': sb.Append('\b'); return i + 2;
                case 'f': sb.Append('\f'); return i + 2;
                case 'n': sb.Append('\n'); return i + 2;
                case 'r': sb.Append('\r'); return i + 2;
                case 't': sb.Append('\t'); return i + 2;
                case 'u':
                    {
                        if (i + 5 < s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                            {
                                sb.Append((char)code);
                                return i + 6;
                            }
                        }
                        sb.Append('u');
                        return i + 2;
                    }
            }
            sb.Append(e);
            return i + 2;
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
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
                        if (c < 32) sb.Append(' ');
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
