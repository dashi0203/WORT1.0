namespace Wort.Generation
{
    /// <summary>
    /// 生成核心统一的调试输出：Log 为 null 时关闭（默认）。预览工具加 --debug 时打开；
    /// TerraceField 的坡道轮次、RandomRegion 校验失败时的格子详情都写到这里。不影响生成结果。
    /// </summary>
    public static class GenDebug
    {
        public static System.Text.StringBuilder Log;
        public static bool On => Log != null;
    }
}
