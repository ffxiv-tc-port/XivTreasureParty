using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using XivTreasureParty.Game;
using XivTreasureParty.Party;

namespace XivTreasureParty.UI;

public sealed class PartyPanel
{
    private string _nicknameBuf = "";
    private string _joinCodeBuf = "";
    private string _statusMessage = "";
    private DateTime _statusUntil;
    private bool _busy;

    public void Draw()
    {
        var party = Plugin.PartyService;
        var sync = Plugin.SyncService;

        if (_nicknameBuf.Length == 0)
        {
            // 優先順序：目前隊伍中的暱稱 → 設定檔上次儲存 → 遊戲中「角色名@伺服器」→ 空字串
            _nicknameBuf = party.Nickname
                           ?? (string.IsNullOrWhiteSpace(Plugin.Config.Nickname) ? null : Plugin.Config.Nickname)
                           ?? PlayerInfo.GetAutoNickname()
                           ?? "";
        }

        ImGui.TextUnformatted("我的暱稱");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##nickname", ref _nicknameBuf, 32))
        {
            Plugin.Config.Nickname = _nicknameBuf;
            Plugin.Config.Save();
        }

        if (party.IsInParty && _nicknameBuf != party.Nickname)
        {
            ImGui.SameLine();
            if (ImGui.Button("套用暱稱"))
            {
                RunAsync(() => party.UpdateNicknameAsync(_nicknameBuf),
                    "已更新暱稱");
            }
        }

        ImGui.Separator();

        if (!party.IsInParty)
        {
            DrawNotInParty();
        }
        else
        {
            DrawInParty();
        }

        if (!string.IsNullOrEmpty(_statusMessage) && DateTime.UtcNow < _statusUntil)
        {
            ImGui.Spacing();
            ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.9f, 0.4f, 1f), _statusMessage);
        }
    }

    private void DrawNotInParty()
    {
        ImGui.TextUnformatted("尚未加入任何隊伍");
        ImGui.Spacing();

        if (ImGui.Button("建立新隊伍", new System.Numerics.Vector2(-1, 28)) && !_busy)
        {
            RunAsync(async () =>
            {
                await Plugin.PartyService.CreatePartyAsync(string.IsNullOrWhiteSpace(_nicknameBuf) ? null : _nicknameBuf);
                Plugin.SyncService.Start(Plugin.PartyService.CurrentPartyCode!);
                Plugin.Heartbeat.Start();
            }, "已建立隊伍");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("加入隊伍 (8 碼代碼)");
        ImGui.SetNextItemWidth(-80f);
        ImGui.InputText("##joincode", ref _joinCodeBuf, 16, ImGuiInputTextFlags.CharsUppercase);
        ImGui.SameLine();
        if (ImGui.Button("加入", new System.Numerics.Vector2(-1, 0)) && !_busy)
        {
            var code = _joinCodeBuf;
            RunAsync(async () =>
            {
                await Plugin.PartyService.JoinPartyAsync(code,
                    string.IsNullOrWhiteSpace(_nicknameBuf) ? null : _nicknameBuf);
                Plugin.SyncService.Start(Plugin.PartyService.CurrentPartyCode!);
                Plugin.Heartbeat.Start();
            }, "已加入隊伍");
        }
    }

    private void DrawInParty()
    {
        var party = Plugin.PartyService;
        var sync = Plugin.SyncService;

        ImGui.TextUnformatted("隊伍代碼");
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.9f, 0.3f, 1f));
        ImGui.TextUnformatted(party.CurrentPartyCode ?? "");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.SmallButton("複製##copycode"))
            ImGui.SetClipboardText(party.CurrentPartyCode ?? "");
        ImGui.SameLine();
        if (ImGui.SmallButton("邀請連結##copylink"))
        {
            var url = $"https://cycleapple.github.io/xiv-tc-treasure-finder/?party={party.CurrentPartyCode}";
            ImGui.SetClipboardText(url);
            ShowStatus("已複製邀請連結到剪貼簿");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("複製網頁版邀請連結 (含隊伍代碼)，可貼給隊友");

        if (party.ExpiresAt is { } exp)
        {
            var remaining = DateTimeOffset.FromUnixTimeMilliseconds(exp) - DateTimeOffset.UtcNow;
            if (remaining.TotalSeconds > 0)
                ImGui.TextUnformatted($"過期倒數: {(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}");
            else
                ImGui.TextUnformatted("隊伍已過期");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted($"成員 ({sync.Members.Count}/{PartyService.MaxMembers})");
        if (ImGui.BeginChild("##members", new System.Numerics.Vector2(0, 120), true))
        {
            foreach (var (uid, member) in sync.Members.OrderByDescending(kv => kv.Value.IsLeader == true).ThenBy(kv => kv.Value.Nickname))
            {
                var prefix = member.IsLeader == true ? "[房] " : "";
                var lastSeenMs = member.LastSeen is long l ? l : (member.LastSeen is System.Text.Json.JsonElement je && je.TryGetInt64(out var jl) ? jl : 0);
                var online = lastSeenMs == 0 || (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastSeenMs) < 90_000;
                var color = online
                    ? new System.Numerics.Vector4(0.7f, 1f, 0.7f, 1f)
                    : new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 1f);
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                ImGui.TextUnformatted($"{prefix}{member.Nickname}{(online ? "" : " (離線)")}");
                ImGui.PopStyleColor();
            }
        }
        ImGui.EndChild();

        var canOptimize = party.CanModifyOrder() && sync.Treasures.Count > 1;
        if (!canOptimize) ImGui.BeginDisabled();
        if (ImGui.Button("優化路線") && !_busy)
        {
            RunOptimize();
        }
        if (!canOptimize) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            if (party.OrderLocked && !party.IsLeader)
                ImGui.SetTooltip("順序已被房主鎖定");
            else if (sync.Treasures.Count <= 1)
                ImGui.SetTooltip("需要至少 2 個藏寶點");
            else
                ImGui.SetTooltip("以地圖分組 + 最近鄰居法重新排序，與網頁版一致");
        }
        ImGui.SameLine();

        if (ImGui.Button(party.OrderLocked ? "解鎖順序" : "鎖定順序") && party.IsLeader && !_busy)
        {
            RunAsync(async () => await party.ToggleOrderLockAsync(),
                party.OrderLocked ? "順序已解鎖" : "順序已鎖定");
        }
        ImGui.SameLine();
        if (ImGui.Button("清除已完成") && !_busy)
        {
            RunAsync(() => party.ClearCompletedAsync(), "已清除完成的藏寶圖");
        }
        ImGui.SameLine();
        if (ImGui.Button("離開隊伍") && !_busy)
        {
            RunAsync(async () =>
            {
                Plugin.Heartbeat.Stop();
                Plugin.SyncService.Stop();
                await Plugin.PartyService.LeavePartyAsync();
            }, "已離開隊伍");
        }

        if (!party.IsLeader && party.OrderLocked)
        {
            ImGui.Spacing();
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.7f, 0.3f, 1f), "房主已鎖定順序");
        }
    }

    private void RunOptimize()
    {
        if (_busy) return;
        _busy = true;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var (before, after, reordered) = await Plugin.PartyService.AutoOptimizeRouteAsync();
                if (reordered == 0)
                    ShowStatus("路線已是最佳，未調整");
                else
                {
                    var pct = before > 0 ? (int)Math.Round((1 - after / before) * 100) : 0;
                    ShowStatus(pct > 0
                        ? $"已優化路線：距離縮短 {pct}%，調整 {reordered} 項"
                        : $"已重新排序 {reordered} 項");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "優化路線失敗");
                ShowStatus("優化失敗: " + ex.Message);
            }
            finally
            {
                _busy = false;
            }
        });
    }

    private void RunAsync(Func<System.Threading.Tasks.Task> action, string? successMessage = null)
    {
        if (_busy) return;
        _busy = true;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await action();
                if (!string.IsNullOrEmpty(successMessage))
                    ShowStatus(successMessage!);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "PartyPanel 操作失敗");
                ShowStatus("失敗: " + ex.Message);
            }
            finally
            {
                _busy = false;
            }
        });
    }

    private void ShowStatus(string msg)
    {
        _statusMessage = msg;
        _statusUntil = DateTime.UtcNow.AddSeconds(4);
    }
}
