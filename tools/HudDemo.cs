using System;

namespace QRSnap
{
    static class HudDemo
    {
        static void Main()
        {
            string[] samples = new string[]
            {
                "FPS 0 CPU 5287MHz 12% 32°C CL GPU 1755MHz 21% 39°C 46W",
                "FPS 0 CPU 5287MHz 10% 32°C.L GPU 1755MHZn7%_39CCh 50W",
                "Presenting to you plugin injecting FSR 3.1 to Xenoblade Chronicles 2 3.0.1, tested on 4GB Nintendo",
                "見守る者 · カル",
                "Watcher Karr",
                "HP 3500/3500 MP 120",
                "Oct 18, 2026",
                "2,400 · 16 · 13",
                "ROM Hack xc2fsr - FSR 3.1 for Xenoblade Chronicles 2 (1080p, up to 120",
                "Home > Forums > PC, Console & Handheld Discussions > Nintendo Switch",
                "13%",
                "線上 12 人 / 延遲 46ms",
                "Features:",
            };
            foreach (string s in samples)
                Console.WriteLine((Ocr.LooksLikeHud(s) ? "HUD " : "TXT ") + "| " + s);
        }
    }
}
