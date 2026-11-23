using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XivTreasureParty.UI;

/// <summary>出廠字級（16 px）下的設計像素換成目前字級的像素。</summary>
/// <remarks>GetFontSize() 已含全域縮放，不再乘 GlobalScale；只能在畫面繪製中、沒推其他字型時呼叫。</remarks>
internal static class UiScale
{
    private const float FactoryFontSizePx = 16f;

    public static float Px(float designPixels) => designPixels * ImGui.GetFontSize() / FactoryFontSizePx;

    public static Vector2 Px(float x, float y) => new(Px(x), Px(y));
}
