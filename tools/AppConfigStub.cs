namespace QRSnap
{
    // 仅测试用桩：让 PpOcr.cs 能在独立冒烟测试里编译（正式构建用的是 src.cs 里的 AppConfig）
    internal static class AppConfig
    {
        public static bool OcrGpu = false;
        public static string MtGpu = "auto";
        public static string NormalizeMtGpu(string v) { return string.IsNullOrEmpty(v) ? "auto" : v; }
    }
}
