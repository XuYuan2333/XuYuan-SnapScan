namespace QRSnap
{
    // 离屏验证用的最小 AppConfig 桩：只给 PpOcr 用到的 GPU 开关填默认值（纯 CPU）
    internal static class AppConfig
    {
        public static bool OcrGpu;
        public static string MtGpu = "off";
        public static string NormalizeMtGpu(string s) { return "off"; }
    }
}
