using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using XivTreasureParty.Firebase;
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

    /// <summary>手動重連按鈕的節流終點，避免連點變成每幀重試。</summary>
    private DateTime _reconnectCooldownUntil = DateTime.MinValue;

    public void Draw()
    {
        var party = Plugin.PartyService;
        var sync = Plugin.SyncService;

        DrawConnectionRow();
        ImGui.Separator();

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

    /// <summary>
    /// 與網頁版共享房間靠的是三條 Firebase SSE 長連線，斷掉時畫面上必須看得出來。
    /// 「不知道」也要看得見：從沒收過資料時顯示「?」而不是 0。
    /// </summary>
    private void DrawConnectionRow()
    {
        var info = Plugin.SyncService.GetConnectionInfo();
        var inParty = Plugin.PartyService.IsInParty;
        var (label, color) = DescribeConnection(info, inParty);

        ImGui.TextColored(color, "●");
        ImGui.SameLine(0, 4f);
        ImGui.TextColored(color, label);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(BuildConnectionTooltip(info, inParty));

        const string btnLabel = "重新連線";
        var btnWidth = ImGui.CalcTextSize(btnLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SameLine();
        var remain = ImGui.GetContentRegionAvail().X;
        if (remain > btnWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + remain - btnWidth);

        var cooling = DateTime.UtcNow < _reconnectCooldownUntil;
        if (cooling) ImGui.BeginDisabled();
        if (ImGui.SmallButton(btnLabel))
        {
            // 自動重連還在退避時按也有效：這是插隊立刻重試一次，不是取消退避策略。
            _reconnectCooldownUntil = DateTime.UtcNow.AddSeconds(3);
            if (Plugin.SyncService.ReconnectNow())
                ShowStatus("已要求重新連線");
            else
                ShowStatus("目前不在隊伍中，沒有需要重連的內容");
        }
        if (cooling) ImGui.EndDisabled();
        // 停用中的項目預設不算 hover，要明確允許才看得到 tooltip。
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(cooling
                ? "剛剛已經送出重連要求，請稍候再按"
                : "立刻重試一次同步連線；自動重連還在等待退避時間時按這顆會直接插隊。");
    }

    private static (string Label, System.Numerics.Vector4 Color) DescribeConnection(SyncConnectionInfo info, bool inParty)
    {
        var green = new System.Numerics.Vector4(0.35f, 0.9f, 0.45f, 1f);
        var yellow = new System.Numerics.Vector4(1f, 0.85f, 0.35f, 1f);
        var orange = new System.Numerics.Vector4(1f, 0.62f, 0.25f, 1f);
        var red = new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f);
        var grey = new System.Numerics.Vector4(0.6f, 0.6f, 0.6f, 1f);

        switch (info.State)
        {
            case StreamConnectionState.Connected:
                return info.LastEventUtc == null
                    ? ("已連線（尚未收到資料）", yellow)
                    : ("已連線", green);

            case StreamConnectionState.Connecting:
                return (info.Connected > 0 ? $"連線中 {info.Connected}/{info.Total}" : "連線中", yellow);

            case StreamConnectionState.Reconnecting:
            {
                var seconds = info.NextRetryUtc is { } next
                    ? Math.Max(0, (int)Math.Ceiling((next - DateTime.UtcNow).TotalSeconds))
                    : -1;
                var when = seconds >= 0 ? $"{seconds} 秒後" : "時間未知";
                // 失敗次數為 0 ＝ 伺服器把上一條串流正常關掉，只是接回去，不是出錯。
                return info.FailedAttempts > 0
                    ? ($"重連中 第 {info.FailedAttempts} 次，{when}", orange)
                    : ($"重新接上中，{when}", yellow);
            }

            case StreamConnectionState.Stopped:
                return ("已斷線，不會自動重連", red);

            default:
                return (inParty ? "未連線" : "未連線（尚未加入隊伍）", grey);
        }
    }

    private static string BuildConnectionTooltip(SyncConnectionInfo info, bool inParty)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("與網頁版共享房間的即時連線（成員／藏寶圖／隊伍設定共三條）。");
        sb.AppendLine(info.Total == 0
            ? (inParty ? "目前串流數: 0（訂閱不存在，可按「重新連線」重建）" : "目前串流數: 0（尚未加入隊伍）")
            : $"已連上 {info.Connected}/{info.Total} 條");

        sb.AppendLine(info.LastEventUtc is { } last
            ? $"最後收到資料: {Math.Max(0, (int)(DateTime.UtcNow - last).TotalSeconds)} 秒前"
            : "最後收到資料: ?（這次連線還沒收到過任何資料）");

        if (info.FailedAttempts > 0)
            sb.AppendLine($"連續失敗次數: {info.FailedAttempts}");

        sb.AppendLine(string.IsNullOrWhiteSpace(info.LastError)
            ? "最後錯誤: 無"
            : $"最後錯誤: {info.LastError}");

        sb.Append("斷線時會自動以 1 秒起跳、上限 30 秒的間隔重連；按「重新連線」可以不等直接重試。");
        return sb.ToString();
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
