using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XivTreasureParty.UI;

/// <summary>
/// 主視窗。分成左右兩欄：左為隊伍 + 新增藏寶圖；右為藏寶圖清單。
/// </summary>
public sealed class PluginWindow
{
    public bool IsOpen;

    private readonly PartyPanel _partyPanel = new();
    private readonly TreasureListPanel _listPanel = new();
    public AddTreasurePanel AddPanel { get; } = new();

    public void Draw()
    {
        if (!IsOpen) return;

        ImGui.SetNextWindowSize(new Vector2(820, 540), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(640, 420), new Vector2(1600, 1200));

        var open = IsOpen;
        if (!ImGui.Begin("XIV 藏寶圖工具小幫手", ref open))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }
        IsOpen = open;

        try
        {
            var avail = ImGui.GetContentRegionAvail();
            var leftWidth = MathF.Max(260f, avail.X * 0.38f);

            if (ImGui.BeginChild("##left", new Vector2(leftWidth, 0), true))
            {
                _partyPanel.Draw();
                ImGui.Separator();
                AddPanel.Draw();
            }
            ImGui.EndChild();

            ImGui.SameLine();

            if (ImGui.BeginChild("##right", new Vector2(0, 0), true))
            {
                _listPanel.Draw();
            }
            ImGui.EndChild();
        }
        finally
        {
            ImGui.End();
        }
    }
}
