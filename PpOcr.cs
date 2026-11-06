using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace QRSnap
{
    // PP-OCR (PaddleOCR) ONNX runner: DB detector + CTC recognizer, pure managed + onnxruntime.
    public sealed class PpOcr
    {
        public const string DetFile = "det.onnx";
        public const string RecFile = "rec.onnx";
        public const string KeysFile = "keys.txt";

        private InferenceSession _det;
        private InferenceSession _rec;
        private string _detIn;
        private string _recIn;
        private List<string> _keys = new List<string>();
        private string _label;
        private int _classes;

        public string Label { get { return _label; } }

        // "-dir" is a tier folder holding det.onnx / rec.onnx / keys.txt
        public PpOcr(string dir, string label)
        {
            _label = label;
            string detPath = Path.Combine(dir, DetFile);
            string recPath = Path.Combine(dir, RecFile);
            string keysPath = Path.Combine(dir, KeysFile);
            if (!File.Exists(detPath)) throw new FileNotFoundException("missing det model", detPath);
            if (!File.Exists(recPath)) throw new FileNotFoundException("missing rec model", recPath);
            if (!File.Exists(keysPath)) throw new FileNotFoundException("missing keys", keysPath);

            string[] raw = File.ReadAllLines(keysPath, Encoding.UTF8);
            for (int i = 0; i < raw.Length; i++)
            {
                string k = raw[i];
                if (k.EndsWith("\r")) k = k.Substring(0, k.Length - 1);
                _keys.Add(k);
            }

            SessionOptions so = new SessionOptions();
            try { so.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2); } catch { }
            try { so.InterOpNumThreads = 1; } catch { }
            so.EnableCpuMemArena = true;

            _det = new InferenceSession(detPath, so);
            _rec = new InferenceSession(recPath, so);

            foreach (string k in _det.InputMetadata.Keys) { _detIn = k; break; }
            foreach (string k in _rec.InputMetadata.Keys) { _recIn = k; break; }
            foreach (var kv in _rec.OutputMetadata)
            {
                int[] d = kv.Value.Dimensions;
                if (d != null && d.Length == 3) _classes = d[2];
                break;
            }
        }

        public static bool FolderReady(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            return File.Exists(Path.Combine(dir, DetFile))
                && File.Exists(Path.Combine(dir, RecFile))
                && File.Exists(Path.Combine(dir, KeysFile));
        }

        public void Dispose()
        {
            try { if (_det != null) _det.Dispose(); } catch { }
            try { if (_rec != null) _rec.Dispose(); } catch { }
            _det = null; _rec = null;
        }

        // ---------- geometry ----------
        public sealed class Box
        {
            public int X, Y, W, H;
            public float Score;
            public int Order;
        }

        public string Recognize(Bitmap src)
        {
            if (src == null) return "";
            List<Box> boxes = Detect(src);
            if (boxes.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            int lastLine = int.MinValue;
            for (int i = 0; i < boxes.Count; i++)
            {
                Box b = boxes[i];
                string line = RecOne(src, b);
                if (line == null) continue;
                line = line.Trim();
                if (line.Length == 0) continue;
                if (lastLine != int.MinValue)
                {
                    if (b.Order != lastLine) sb.Append('\n');
                    else sb.Append(' ');
                }
                sb.Append(line);
                lastLine = b.Order;
            }
            return sb.ToString();
        }

        private static Bitmap Resize(Bitmap src, int w, int h, bool white)
        {
            if (w < 1) w = 1;
            if (h < 1) h = 1;
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                if (white) g.Clear(Color.White);
                else g.Clear(Color.Black);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
            }
            return dst;
        }

        // RGB values copied out of a 24bpp bitmap in row order, as BGR triples
        private static void ReadPixels(Bitmap bmp, out byte[] rgb, out int w, out int h)
        {
            w = bmp.Width; h = bmp.Height;
            rgb = new byte[w * h * 3];
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = bd.Stride;
                byte[] row = new byte[Math.Abs(stride)];
                for (int y = 0; y < h; y++)
                {
                    IntPtr p = new IntPtr(bd.Scan0.ToInt64() + (long)y * stride);
                    System.Runtime.InteropServices.Marshal.Copy(p, row, 0, row.Length);
                    Buffer.BlockCopy(row, 0, rgb, y * w * 3, w * 3);
                }
            }
            finally { bmp.UnlockBits(bd); }
        }

        private static float[] Flatten(DisposableNamedOnnxValue v)
        {
            List<float> list = new List<float>();
            foreach (float f in v.AsEnumerable<float>()) list.Add(f);
            return list.ToArray();
        }

        // ---------- detection ----------
        private List<Box> Detect(Bitmap src)
        {
            const int MaxSide = 960;
            int ow = src.Width, oh = src.Height;
            double scale = Math.Min(1.0, (double)MaxSide / Math.Max(ow, oh));
            int w = (int)Math.Round(ow * scale / 32.0) * 32;
            int h = (int)Math.Round(oh * scale / 32.0) * 32;
            if (w < 32) w = 32;
            if (h < 32) h = 32;

            float[] data = new float[3 * h * w];
            using (Bitmap small = Resize(src, w, h, false))
            {
                byte[] px;
                int sw, sh;
                ReadPixels(small, out px, out sw, out sh);
                float m0 = 0.485f, m1 = 0.456f, m2 = 0.406f;
                float s0 = 0.229f, s1 = 0.224f, s2 = 0.225f;
                int plane = h * w;
                for (int y = 0; y < h; y++)
                {
                    int rowoff = y * w * 3;
                    int p = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int o = rowoff + x * 3;
                        float b = px[o] / 255f, g = px[o + 1] / 255f, r = px[o + 2] / 255f;
                        data[p + x] = (b - m0) / s0;
                        data[plane + p + x] = (g - m1) / s1;
                        data[2 * plane + p + x] = (r - m2) / s2;
                    }
                }
            }

            DenseTensor<float> t = new DenseTensor<float>(data, new int[] { 1, 3, h, w });

            float[] prob;
            int ph = h, pw = w;
            using (var results = _det.Run(new[] { NamedOnnxValue.CreateFromTensor(_detIn, t) }))
            {
                prob = Flatten(results[0]);
            }

            // binary mask + connected components
            const float Thresh = 0.2f;
            byte[] mask = new byte[ph * pw];
            int count = Math.Min(ph * pw, prob.Length);
            for (int i = 0; i < count; i++) mask[i] = prob[i] >= Thresh ? (byte)1 : (byte)0;

            List<Box> boxes = new List<Box>();
            int[] stack = new int[ph * pw];
            int[] lab = new int[ph * pw];
            for (int i = 0; i < lab.Length; i++) lab[i] = 0;
            int nextLab = 0;
            float unclip = 1.4f;
            for (int seed = 0; seed < count; seed++)
            {
                if (mask[seed] == 0 || lab[seed] != 0) continue;
                nextLab++;
                int sp = 0;
                stack[sp++] = seed;
                lab[seed] = nextLab;
                int minx = pw, miny = ph, maxx = -1, maxy = -1, area = 0;
                double sum = 0;
                while (sp > 0)
                {
                    int cur = stack[--sp];
                    int cy = cur / pw, cx = cur % pw;
                    area++;
                    sum += prob[cur];
                    if (cx < minx) minx = cx;
                    if (cx > maxx) maxx = cx;
                    if (cy < miny) miny = cy;
                    if (cy > maxy) maxy = cy;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = cy + dy;
                        if (ny < 0 || ny >= ph) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx;
                            if (nx < 0 || nx >= pw) continue;
                            int ni = ny * pw + nx;
                            if (mask[ni] == 0 || lab[ni] != 0) continue;
                            lab[ni] = nextLab;
                            stack[sp++] = ni;
                        }
                    }
                    if (sp > stack.Length - 2) break;
                }
                if (area < 6) continue;
                float score = (float)(sum / area);
                if (score < 0.4f) continue;
                int bw = maxx - minx + 1, bh = maxy - miny + 1;
                if (bw < 4 || bh < 4) continue;
                double dist = ((double)bw * bh) * unclip / (2.0 * (bw + bh));
                float sx = (float)ow / pw;
                float sy = (float)oh / ph;
                int x0 = (int)Math.Floor((minx - dist) * sx);
                int y0 = (int)Math.Floor((miny - dist) * sy);
                int x1 = (int)Math.Ceiling((maxx + 1 + dist) * sx);
                int y1 = (int)Math.Ceiling((maxy + 1 + dist) * sy);
                if (x0 < 0) x0 = 0;
                if (y0 < 0) y0 = 0;
                if (x1 > ow) x1 = ow;
                if (y1 > oh) y1 = oh;
                if (x1 - x0 < 4 || y1 - y0 < 6) continue;
                // skip nearly full-frame boxes (screenshot border noise)
                if ((x1 - x0) > ow * 0.98 && (y1 - y0) > oh * 0.98) continue;
                Box nb = new Box();
                nb.X = x0; nb.Y = y0; nb.W = x1 - x0; nb.H = y1 - y0; nb.Score = score;
                boxes.Add(nb);
            }

            SortLines(boxes);
            return boxes;
        }

        private static void SortLines(List<Box> boxes)
        {
            boxes.Sort(delegate(Box a, Box b)
            {
                if (Math.Abs(a.Y - b.Y) > Math.Max(a.H, b.H) / 2) return a.Y.CompareTo(b.Y);
                return a.X.CompareTo(b.X);
            });
            int order = 0;
            int lineTop = int.MinValue;
            int lineBottom = int.MinValue;
            for (int i = 0; i < boxes.Count; i++)
            {
                Box b = boxes[i];
                int cy = b.Y + b.H / 2;
                bool same = (cy >= lineTop && cy <= lineBottom);
                if (!same) { order++; lineTop = b.Y; lineBottom = b.Y + b.H; }
                else
                {
                    if (b.Y < lineTop) lineTop = b.Y;
                    if (b.Y + b.H > lineBottom) lineBottom = b.Y + b.H;
                }
                b.Order = order;
            }
        }

        // ---------- recognition ----------
        private string RecOne(Bitmap src, Box b)
        {
            int pad = 2;
            int x0 = b.X - pad, y0 = b.Y - pad;
            int w = b.W + pad * 2, h = b.H + pad * 2;
            if (x0 < 0) { w += x0; x0 = 0; }
            if (y0 < 0) { h += y0; y0 = 0; }
            if (x0 + w > src.Width) w = src.Width - x0;
            if (y0 + h > src.Height) h = src.Height - y0;
            if (w < 2 || h < 2) return null;

            Rectangle rc = new Rectangle(x0, y0, w, h);
            int th = 48;
            int tw = (int)Math.Round((double)th * w / h);
            if (tw < 40) tw = 40;
            if (tw > 1600) tw = 1600;

            float[] data = new float[3 * th * tw];
            // padded region stays 0 (= mid gray after normalisation), matching Paddle padding
            using (Bitmap crop = src.Clone(rc, PixelFormat.Format24bppRgb))
            using (Bitmap rs = Resize(crop, tw, th, false))
            {
                byte[] px;
                int sw, sh;
                ReadPixels(rs, out px, out sw, out sh);
                int plane = th * tw;
                for (int y = 0; y < th; y++)
                {
                    int rowoff = y * sw * 3;
                    int p = y * tw;
                    for (int x = 0; x < sw; x++)
                    {
                        int o = rowoff + x * 3;
                        float bb = px[o] / 255f - 0.5f; bb *= 2f;
                        float gg = px[o + 1] / 255f - 0.5f; gg *= 2f;
                        float rr = px[o + 2] / 255f - 0.5f; rr *= 2f;
                        data[p + x] = bb;
                        data[plane + p + x] = gg;
                        data[2 * plane + p + x] = rr;
                    }
                }
            }

            DenseTensor<float> t = new DenseTensor<float>(data, new int[] { 1, 3, th, tw });
            using (var results = _rec.Run(new[] { NamedOnnxValue.CreateFromTensor(_recIn, t) }))
            {
                int classes = _classes > 0 ? _classes : 1;
                float[] flat = Flatten(results[0]);
                int steps = classes > 0 ? flat.Length / classes : 0;
                int blank = 0;
                int prev = -1;
                StringBuilder sb = new StringBuilder();
                for (int s = 0; s < steps; s++)
                {
                    int baseIdx = s * classes;
                    int best = 0;
                    float bestV = flat[baseIdx];
                    for (int c = 1; c < classes; c++)
                    {
                        float v = flat[baseIdx + c];
                        if (v > bestV) { bestV = v; best = c; }
                    }
                    if (best != blank && best != prev)
                    {
                        string ch = CharOf(best);
                        if (ch != null) sb.Append(ch);
                    }
                    prev = best;
                }
                return sb.ToString();
            }
        }

        private string CharOf(int idx)
        {
            int i = idx - 1;
            if (i >= 0 && i < _keys.Count) return _keys[i];
            // classes = keys.Count + 2 usually means a trailing space token
            if (idx == _keys.Count + 1) return " ";
            if (idx >= _keys.Count + 2) return null;
            return null;
        }
    }
}
