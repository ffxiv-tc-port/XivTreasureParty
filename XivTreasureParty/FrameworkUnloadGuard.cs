using System.Threading;

namespace XivTreasureParty;

/// <summary>
/// 卸載窗內「轉派到 framework 執行緒」的短路判斷：卸載時 <c>RunOnFrameworkThread</c>
/// 與無延遲的 <c>RunOnTick</c> <b>就地在呼叫端執行緒執行</b>，轉派因此失去保護作用
/// ——背景執行緒會與 framework 執行緒並行改同一份 snapshot 或 ImGui 狀態。
/// </summary>
internal static class FrameworkUnloadGuard
{
    private static int _reported;

    /// <summary>
    /// 卸載中且<b>不在</b> framework 執行緒上 ⇒ <see langword="true"/>，呼叫端直接放棄這次工作。
    /// 已經在 framework 執行緒上時恆為 <see langword="false"/>：那時就地執行是對的。
    /// </summary>
    internal static bool ShouldSkip(string scope)
    {
        var framework = Plugin.Framework;
        if (!framework.IsFrameworkUnloading || framework.IsInFrameworkUpdateThread)
            return false;

        if (Interlocked.Exchange(ref _reported, 1) == 0)
            Plugin.Log.Information($"外掛正在卸載，略過背景執行緒排進來的工作（{scope}）。");

        return true;
    }
}
