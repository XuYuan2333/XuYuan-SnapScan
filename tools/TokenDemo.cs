using System;

namespace QRSnap
{
    static class TokenDemo
    {
        static void Main()
        {
            string[] samples = new string[]
            {
                "xc2fsr",
                "FSR 3.1",
                "v1.4.2",
                "RTX5060",
                "SnapScan",
                "macOS",
                "iPhone",
                "GPU",
                "HP",
                "ROM Hack",
                "Watcher Karr",
                "masagrator",
                "Features:",
                "Review",
                "Home > Forums",
                "Oct 18, 2026",
                "2026-11-14",
                "менің",
                "見守る者",
            };
            foreach (string s in samples)
            {
                string pre = LayoutBoost.PreTranslate(s);
                string tag;
                if (pre == null) tag = "TRANS";
                else if (pre.Trim() == s.Trim()) tag = "ASIS ";
                else tag = "GLOSS";
                Console.WriteLine(tag + " | " + s + (pre != null ? "  ->  " + pre : ""));
            }
        }
    }
}
