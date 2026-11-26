using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
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
    private bool _statusIsError;

    /// <summary>手動重連按鈕的節流終點，避免連點變成每幀重試。</summary>
    private DateTime _reconnectCooldownUntil = DateTime.MinValue;

    /// <summary>上一幀的後端；換了就重載暱稱與上次隊伍代碼（兩個後端各存一份）。</summary>
    private BackendKind? _seenKind;

    private bool _advancedInit;
    private string _urlBuf = "";
    private string _apiKeyBuf = "";
    private string _authUrlBuf = "";
    private (string Url, string Key, string Auth) _customCacheKey = (null!, null!, null!);
    private BackendEndpoint? _customCache;
    private string _customCacheError = "";

    private volatile bool _testing;
    private volatile List<ProbeLine>? _testLines;
    private volatile bool _testOk;

    private bool _openInviteConfirm;
    private string _pendingInviteUrl = "";
    private string _pendingInviteCode = "";
    private bool _openMigrateConfirm;

    private const string InvitePopupId = "連到臨時伺服器？##xtp-invite-confirm";
    private const string MigratePopupId = "搬到臨時伺服器？##xtp-migrate-confirm";

    public void Draw()
    {
        var party = Plugin.PartyService;
        var kind = Plugin.Backend.Current.Kind;
        if (_seenKind != kind)
        {
            _seenKind = kind;
            _nicknameBuf = "";
            _joinCodePrefilled = false;
            _joinCodeBuf = "";
        }

        DrawConnectionRow();
        ImGui.Separator();

        if (_nicknameBuf.Length == 0)
        {
            // 優先順序：目前隊伍中的暱稱 → 設定檔上次儲存 → 遊戲中「角色名@伺服器」（只限 cycleapple）→ 空字串
            var saved = Plugin.Config.GetNickname(kind);
            _nicknameBuf = party.Nickname
                           ?? (string.IsNullOrWhiteSpace(saved) ? null : saved)
                           ?? (kind == BackendKind.Cycleapple ? PlayerInfo.GetAutoNickname() : null)
                           ?? "";
        }

        ImGui.TextUnformatted("我的暱稱");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##nickname", ref _nicknameBuf, 32))
        {
            Plugin.Config.SetNickname(kind, _nicknameBuf);
            Plugin.Config.Save();
        }
        if (kind == BackendKind.Custom && _nicknameBuf.Length == 0)
            ImGui.TextDisabled("臨時伺服器不自動帶角色名；留空會用「玩家xxxx」。");

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
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(_statusIsError ? ErrorColor : new System.Numerics.Vector4(0.4f, 0.9f, 0.4f, 1f), _statusMessage);
            ImGui.PopTextWrapPos();
        }

        ImGui.Spacing();
        DrawAdvanced();
        DrawPopups();
    }

    /// <summary>
    /// 同步靠 SSE 長連線（一條或三條），斷掉時畫面上必須看得出來。
    /// 「不知道」也要看得見：從沒收過資料時顯示「?」而不是 0。
    /// </summary>
    private void DrawConnectionRow()
    {
        var info = Plugin.SyncService.GetConnectionInfo();
        var inParty = Plugin.PartyService.IsInParty;
        var endpoint = Plugin.Backend.Current;
        var (label, color) = DescribeConnection(info, inParty);

        ImGui.TextColored(color, "●");
        ImGui.SameLine(0, UiScale.Px(4f));
        ImGui.TextColored(color, label);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(BuildConnectionTooltip(info, inParty, endpoint));

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
                if (info.LastStatusCode == 402)
                    return ($"同步伺服器連線已滿，{when}重試", orange);
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

    private static string BuildConnectionTooltip(SyncConnectionInfo info, bool inParty, BackendEndpoint endpoint)
    {
        var custom = endpoint.Kind == BackendKind.Custom;
        var sb = new System.Text.StringBuilder();
        var what = custom ? $"與臨時伺服器 {endpoint.DisplayHost} 的即時連線" : "與網頁版共享房間的即時連線";
        sb.AppendLine(info.Total == 0
            ? what + "。"
            : info.Single
                ? what + "（成員／藏寶圖／隊伍設定合併成一條）。"
                : what + "（成員／藏寶圖／隊伍設定共三條）。");
        if (info.LastStatusCode == 402)
            sb.AppendLine(custom
                ? "臨時伺服器回應 402（連線已滿或額度用完），稍後自動重試。"
                : "cycleapple 伺服器同時連線已滿（免費方案上限 100），稍後自動重試。");
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
        var endpoint = Plugin.Backend.Current;
        ImGui.TextUnformatted("尚未加入任何隊伍");
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(endpoint.Kind == BackendKind.Custom
            ? $"目前連的是臨時伺服器 {endpoint.DisplayHost}。建立或加入隊伍時才會連線；離開隊伍後就不再連線。"
            : "建立或加入隊伍時才會連線到同步伺服器（Firebase）；離開隊伍後就不再連線。");
        ImGui.PopTextWrapPos();

        if (!_joinCodePrefilled)
        {
            _joinCodePrefilled = true;
            var last = Plugin.Config.GetLastPartyCode(endpoint.Kind);
            if (_joinCodeBuf.Length == 0 && !string.IsNullOrWhiteSpace(last))
                _joinCodeBuf = last!;
        }

        var rejoinError = Plugin.AutoRejoinError;
        if (rejoinError != null)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ErrorColor, rejoinError);
            ImGui.PopTextWrapPos();
        }

        var autoRejoining = Plugin.AutoRejoinInProgress;
        if (autoRejoining)
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0.35f, 1f), "正在自動重新加入上次的隊伍…");
        ImGui.Spacing();

        // 推彈條件用區域變數：AutoRejoinInProgress 由背景執行緒改，同一幀內可能翻轉。
        var blockActions = autoRejoining;
        if (blockActions) ImGui.BeginDisabled();

        if (ImGui.Button("建立新隊伍", new System.Numerics.Vector2(-1, UiScale.Px(28))) && !_busy)
        {
            RunAsync(async () =>
            {
                await Plugin.PartyService.CreatePartyAsync(string.IsNullOrWhiteSpace(_nicknameBuf) ? null : _nicknameBuf);
                StartSessionAfterJoin();
            }, "已建立隊伍");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(endpoint.Kind == BackendKind.Custom ? "加入隊伍（10 碼代碼或邀請網址）" : "加入隊伍（8 碼代碼，或貼上臨時伺服器的邀請網址）");
        ImGui.SetNextItemWidth(-UiScale.Px(80f));
        ImGui.InputText("##joincode", ref _joinCodeBuf, 256);
        ImGui.SameLine();
        if (ImGui.Button("加入", new System.Numerics.Vector2(-1, 0)) && !_busy)
        {
            var input = _joinCodeBuf;
            if (PartyInvite.LooksLikeInvite(input))
            {
                if (PartyInvite.TryParse(input, PartyCodeGenerator.CustomCodeLength, out var url, out var code, out var error))
                {
                    _pendingInviteUrl = url;
                    _pendingInviteCode = code;
                    _openInviteConfirm = true;
                }
                else
                {
                    ShowStatus(error, isError: true);
                }
            }
            else
            {
                RunAsync(async () =>
                {
                    await Plugin.PartyService.JoinPartyAsync(input,
                        string.IsNullOrWhiteSpace(_nicknameBuf) ? null : _nicknameBuf);
                    StartSessionAfterJoin();
                }, "已加入隊伍");
            }
        }

        if (blockActions) ImGui.EndDisabled();

        ImGui.Spacing();
        var autoRejoin = Plugin.Config.AutoRejoinOnStart;
        if (ImGui.Checkbox("啟動時自動重新加入上次的隊伍", ref autoRejoin))
        {
            Plugin.Config.AutoRejoinOnStart = autoRejoin;
            Plugin.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("開啟後，插件載入時若記得上次的隊伍代碼，會自動連線到同步伺服器並重新加入。\n"
                             + "關閉時（預設）載入插件不會連線，只有按「建立新隊伍」或「加入」才會連線。\n"
                             + "改這個選項不會立刻連線，下次載入插件才生效。");
    }

    private static readonly System.Numerics.Vector4 ErrorColor = new(1f, 0.4f, 0.4f, 1f);

    private bool _joinCodePrefilled;

    /// <summary>建立／加入成功後開串流與心跳；等回應期間插件已卸載就不開。</summary>
    private static void StartSessionAfterJoin()
    {
        if (!Plugin.TryStartSession(Plugin.PartyService.CurrentPartyCode!)) return;
        Plugin.AutoRejoinError = null;
    }

    private void DrawInParty()
    {
        var party = Plugin.PartyService;
        var sync = Plugin.SyncService;
        var endpoint = Plugin.Backend.Current;
        var custom = endpoint.Kind == BackendKind.Custom;

        ImGui.TextUnformatted("隊伍代碼");
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.9f, 0.3f, 1f));
        ImGui.TextUnformatted(party.CurrentPartyCode ?? "");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.SmallButton("複製##copycode"))
            ImGui.SetClipboardText(party.CurrentPartyCode ?? "");
        ImGui.SameLine();
        if (!custom)
        {
            if (ImGui.SmallButton("邀請連結##copylink"))
            {
                var url = $"https://cycleapple.github.io/xiv-tc-treasure-finder/?party={party.CurrentPartyCode}";
                ImGui.SetClipboardText(url);
                ShowStatus("已複製邀請連結到剪貼簿");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("複製網頁版邀請連結 (含隊伍代碼)，可貼給隊友");
        }
        else
        {
            var invite = PartyInvite.Build(endpoint.DatabaseUrl, party.CurrentPartyCode ?? "");
            if (ImGui.SmallButton("邀請網址##copyinvite"))
            {
                ImGui.SetClipboardText(invite);
                ShowStatus("已複製邀請網址到剪貼簿");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("複製臨時伺服器的邀請網址：裝了插件的隊友貼進「加入」框，\n沒裝插件的隊友用瀏覽器開（伺服器有提供臨時網頁時）。");
            ImGui.SameLine();
            if (ImGui.SmallButton("送到頻道##sendinvite"))
                SendInviteToChat(invite);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"用 {TreasureListPanel.NormalizeChatCommand(Plugin.Config.TreasureChatCommand)} 送出一行邀請網址");
        }

        if (party.ExpiresAt is { } exp)
        {
            var remaining = DateTimeOffset.FromUnixTimeMilliseconds(exp) - DateTimeOffset.UtcNow;
            if (remaining.TotalSeconds > 0)
                ImGui.TextUnformatted($"過期倒數: {(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}");
            else
                ImGui.TextUnformatted("隊伍已過期");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(party.MemberLimit is { } limit
            ? $"成員 ({sync.Members.Count}/{limit})"
            : $"成員 ({sync.Members.Count})");
        if (ImGui.BeginChild("##members", new System.Numerics.Vector2(0, UiScale.Px(120)), true))
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
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
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

        if (!custom && party.IsLeader)
        {
            ImGui.Spacing();
            var target = CustomEndpoint(out var targetError);
            var cannotMigrate = target == null || _busy;
            if (cannotMigrate) ImGui.BeginDisabled();
            if (ImGui.Button("搬到臨時伺服器…"))
                _openMigrateConfirm = true;
            if (cannotMigrate) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(target == null
                    ? $"先在下方「進階：臨時伺服器」填好伺服器網址（{targetError}）"
                    : "cycleapple 連線滿了時用：把未完成的藏寶圖搬到臨時伺服器的新隊伍，並在頻道送出邀請網址。按了會先確認。");
        }
    }

    /// <summary>用設定裡的欄位組出自訂端點；欄位沒變就沿用上次的結果。</summary>
    private BackendEndpoint? CustomEndpoint(out string error)
    {
        var cfg = Plugin.Config;
        var key = (cfg.CustomServerUrl ?? "", cfg.CustomApiKey ?? "", cfg.CustomAuthUrl ?? "");
        if (key != _customCacheKey)
        {
            _customCacheKey = key;
            _customCache = string.IsNullOrWhiteSpace(key.Item1)
                ? null
                : BackendEndpoint.TryCreateCustom(key.Item1, key.Item2, key.Item3, out _customCacheError);
            if (string.IsNullOrWhiteSpace(key.Item1)) _customCacheError = "伺服器網址是空白的";
        }
        error = _customCacheError;
        return _customCache;
    }

    private void DrawAdvanced()
    {
        if (!ImGui.CollapsingHeader("進階：臨時伺服器"))
            return;

        var cfg = Plugin.Config;
        if (!_advancedInit)
        {
            _advancedInit = true;
            _urlBuf = cfg.CustomServerUrl ?? "";
            _apiKeyBuf = cfg.CustomApiKey ?? "";
            _authUrlBuf = cfg.CustomAuthUrl ?? "";
        }

        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled("平時用 cycleapple。只有它連線滿或連不上時，才切到自己或朋友架的臨時伺服器。網址空白時不會連任何臨時伺服器，存檔也不會連線。");
        ImGui.PopTextWrapPos();

        var party = Plugin.PartyService;
        var current = Plugin.Backend.Current;
        var customEndpoint = CustomEndpoint(out var customError);

        // 推彈條件一律先存成區域變數：背景工作會在同一幀內改 _busy／_testing。
        var inParty = party.IsInParty;
        var lockSwitch = inParty || _busy || Plugin.AutoRejoinInProgress || _testing;
        if (lockSwitch) ImGui.BeginDisabled();
        if (ImGui.RadioButton("cycleapple（預設）", current.Kind == BackendKind.Cycleapple) && current.Kind != BackendKind.Cycleapple)
            SwitchBackend(BackendEndpoint.Cycleapple);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            FullAlphaTooltip(inParty ? "在隊伍中不能切換，先離開隊伍" : "cycleapple 的 Firebase，和網頁版共用房間");
        ImGui.SameLine();
        var customUnavailable = customEndpoint == null;
        if (customUnavailable) ImGui.BeginDisabled();
        if (ImGui.RadioButton("自訂伺服器（臨時）", current.Kind == BackendKind.Custom) && customEndpoint != null && current.SlotId != customEndpoint.SlotId)
            SwitchBackend(customEndpoint);
        if (customUnavailable) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            FullAlphaTooltip(customUnavailable
                ? $"先在下面填好伺服器網址（{customError}）"
                : inParty ? "在隊伍中不能切換，先離開隊伍" : "改連下面填的臨時伺服器；網頁版的使用者看不到這邊的隊伍");
        if (lockSwitch) ImGui.EndDisabled();
        if (inParty)
            ImGui.TextDisabled("在隊伍中不能切換後端，先離開隊伍。");

        // 用自訂後端時欄位鎖住：改網址＝換伺服器，要先切回 cycleapple 再改，免得畫面和實際連線不一致。
        var lockFields = lockSwitch || current.Kind == BackendKind.Custom;
        if (lockFields) ImGui.BeginDisabled();
        ImGui.TextUnformatted("伺服器網址");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##xtp-url", "https://", ref _urlBuf, BackendEndpoint.MaxUrlLength + 20);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var trimmed = _urlBuf.Trim();
            if (BackendUrl.TryNormalize(trimmed, out var normalized, out _)) trimmed = normalized;
            _urlBuf = trimmed;
            cfg.CustomServerUrl = trimmed;
            cfg.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            FullAlphaTooltip(current.Kind == BackendKind.Custom
                ? "使用中不能改；要改先切回 cycleapple"
                : "臨時伺服器的網址，只接受 https（本機測試可用 http://127.0.0.1）。資料庫與驗證都從這個網址延伸。");

        ImGui.TextUnformatted("API key（選填）");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##xtp-apikey", ref _apiKeyBuf, 128, ImGuiInputTextFlags.Password))
        {
            cfg.CustomApiKey = _apiKeyBuf.Trim();
            cfg.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            FullAlphaTooltip("只有自己開的 Firebase 專案才要填（網頁設定裡的 apiKey）；XTP 自架服務不用填。");

        ImGui.TextUnformatted("驗證伺服器網址（選填）");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##xtp-authurl", "通常留空", ref _authUrlBuf, BackendEndpoint.MaxUrlLength + 20);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var trimmed = _authUrlBuf.Trim();
            if (BackendUrl.TryNormalize(trimmed, out var normalized, out _)) trimmed = normalized;
            _authUrlBuf = trimmed;
            cfg.CustomAuthUrl = trimmed;
            cfg.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            FullAlphaTooltip("只有驗證服務和資料庫不在同一台時才填，例如 Firebase 模擬器（http://127.0.0.1:9099）。\nXTP 自架服務與自己開的 Firebase 都留空。");
        if (lockFields) ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(cfg.CustomServerUrl) && customEndpoint == null)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ErrorColor, customError);
            ImGui.PopTextWrapPos();
        }

        var testing = _testing;
        var cannotTest = testing || customEndpoint == null;
        if (cannotTest) ImGui.BeginDisabled();
        if (ImGui.Button(testing ? "測試中…" : "測試連線") && customEndpoint != null)
            StartConnectionTest(customEndpoint);
        if (cannotTest) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(customEndpoint == null
                ? $"先填好伺服器網址（{customError}）"
                : "只在按下時連一次：確認對方是不是 XTP 自架服務、資料庫與驗證服務有沒有照 Firebase 的格式回應。\n不帶任何憑證、不建帳號、不寫資料。");

        var lines = _testLines;
        if (lines != null)
        {
            ImGui.PushTextWrapPos(0f);
            foreach (var line in lines)
                ImGui.TextColored(line.Ok ? new System.Numerics.Vector4(0.6f, 0.9f, 0.6f, 1f) : ErrorColor, (line.Ok ? "○ " : "× ") + line.Text);
            ImGui.TextUnformatted(_testOk ? "結果：可以使用" : "結果：不能使用");
            ImGui.PopTextWrapPos();
        }

        ImGui.Spacing();
        var single = cfg.SingleStreamSync;
        if (ImGui.Checkbox("同步合併成一條連線（建議）", ref single))
        {
            cfg.SingleStreamSync = single;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("開啟（預設）時每人只佔同步伺服器 1 條連線，原本是 3 條；伺服器同時連線有上限，佔得越少越不容易被擋。\n"
                             + "伺服器不准用一條連線讀整個隊伍時，會自動改回 3 條。\n"
                             + "改這個選項下次建立／加入隊伍才生效。");
    }

    /// <summary>停用區段裡面的說明框照樣全亮（BeginDisabled 會把之後開的視窗一起調暗）。</summary>
    private static void FullAlphaTooltip(string text)
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 1f))
            ImGui.SetTooltip(text);
    }

    private void SwitchBackend(BackendEndpoint endpoint)
    {
        if (!Plugin.TrySwitchBackend(endpoint, out var reason))
        {
            ShowStatus("不能切換：" + reason, isError: true);
            return;
        }
        Plugin.Config.Backend = endpoint.Kind;
        Plugin.Config.Save();
        ShowStatus(endpoint.Kind == BackendKind.Custom
            ? $"已切換到臨時伺服器 {endpoint.DisplayHost}（建立或加入隊伍時才會連線）"
            : "已切換回 cycleapple");
    }

    private void StartConnectionTest(BackendEndpoint endpoint)
    {
        _testing = true;
        _testLines = null;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var (ok, lines) = await BackendProbe.TestAsync(endpoint).ConfigureAwait(false);
                _testOk = ok;
                _testLines = lines;
                Plugin.Log.Information($"[臨時伺服器] 測試連線：{(ok ? "可以使用" : "不能使用")}｜{string.Join("｜", lines.Select(l => l.Text))}");
            }
            catch (Exception ex)
            {
                _testOk = false;
                _testLines = new List<ProbeLine> { new(false, "測試失敗：" + ex.Message) };
            }
            finally
            {
                _testing = false;
            }
        });
    }

    private void DrawPopups()
    {
        if (_openInviteConfirm)
        {
            _openInviteConfirm = false;
            ImGui.OpenPopup(InvitePopupId);
        }
        if (_openMigrateConfirm)
        {
            _openMigrateConfirm = false;
            ImGui.OpenPopup(MigratePopupId);
        }

        if (ImGui.BeginPopupModal(InvitePopupId, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var host = Uri.TryCreate(_pendingInviteUrl, UriKind.Absolute, out var u) ? u.Authority : _pendingInviteUrl;
            ImGui.PushTextWrapPos(UiScale.Px(420f));
            ImGui.TextUnformatted($"將連到 {host}，要繼續嗎？");
            ImGui.TextDisabled($"會切換到「自訂伺服器（臨時）」並加入隊伍 {_pendingInviteCode}。網頁版的使用者看不到這個隊伍。\n要回 cycleapple：離開隊伍後在「進階：臨時伺服器」切回。");
            ImGui.PopTextWrapPos();
            if (ImGui.Button("繼續", UiScale.Px(100f, 0f)))
            {
                AcceptInvite(_pendingInviteUrl, _pendingInviteCode);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("取消", UiScale.Px(100f, 0f)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal(MigratePopupId, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var target = CustomEndpoint(out _);
            var pending = Plugin.SyncService.Treasures.Values.Count(t => !t.Completed);
            var cmd = TreasureListPanel.NormalizeChatCommand(Plugin.Config.TreasureChatCommand);
            ImGui.PushTextWrapPos(UiScale.Px(420f));
            ImGui.TextUnformatted($"把未完成的藏寶圖（目前看到 {pending} 張）搬到臨時伺服器 {target?.DisplayHost ?? "?"} 的新隊伍，並離開目前的 cycleapple 隊伍？");
            ImGui.TextDisabled($"清單會重新從 cycleapple 讀一次（不需要即時連線）。完成後用 {cmd} 送出邀請網址，隊友貼進「加入」框就能跟上。");
            ImGui.PopTextWrapPos();
            var canGo = target != null && !_busy;
            if (!canGo) ImGui.BeginDisabled();
            if (ImGui.Button("搬過去", UiScale.Px(100f, 0f)) && target != null)
            {
                MigrateToCustom(target);
                ImGui.CloseCurrentPopup();
            }
            if (!canGo) ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("取消", UiScale.Px(100f, 0f)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    /// <summary>使用者確認過的邀請：寫進自訂後端設定、切過去、加入。網址和原本的不同時，API key 與驗證網址一併清掉（邀請只帶網址）。</summary>
    private void AcceptInvite(string url, string code)
    {
        if (_busy) return;
        var cfg = Plugin.Config;
        if (!string.Equals(cfg.CustomServerUrl, url, StringComparison.Ordinal))
        {
            cfg.CustomServerUrl = url;
            cfg.CustomApiKey = string.Empty;
            cfg.CustomAuthUrl = string.Empty;
            _urlBuf = url;
            _apiKeyBuf = _authUrlBuf = string.Empty;
        }

        var endpoint = BackendEndpoint.TryCreateCustom(cfg.CustomServerUrl, cfg.CustomApiKey, cfg.CustomAuthUrl, out var error);
        if (endpoint == null)
        {
            ShowStatus("邀請的網址不能用：" + error, isError: true);
            return;
        }
        if (!Plugin.TrySwitchBackend(endpoint, out var reason))
        {
            ShowStatus("不能切換到臨時伺服器：" + reason, isError: true);
            return;
        }
        cfg.Backend = BackendKind.Custom;
        cfg.Save();

        var nickname = cfg.GetNickname(BackendKind.Custom);
        RunAsync(async () =>
        {
            await Plugin.PartyService.JoinPartyAsync(code, string.IsNullOrWhiteSpace(nickname) ? null : nickname);
            StartSessionAfterJoin();
        }, $"已加入臨時伺服器的隊伍 {code}");
    }

    /// <summary>
    /// 房主手動觸發：從 cycleapple 讀未完成清單（REST，不吃連線名額）→ 離開 → 切到臨時伺服器 → 開新隊伍寫入清單 → 在頻道送邀請網址。
    /// </summary>
    private void MigrateToCustom(BackendEndpoint target)
    {
        var cmd = TreasureListPanel.NormalizeChatCommand(Plugin.Config.TreasureChatCommand);
        RunAsync(async () =>
        {
            var party = Plugin.PartyService;
            var oldCode = party.CurrentPartyCode;
            var pending = await party.GetPendingTreasuresAsync();

            Plugin.Heartbeat.Stop();
            Plugin.SyncService.Stop();
            await party.LeavePartyAsync();

            if (!Plugin.TrySwitchBackend(target, out var reason))
                throw new Exception($"不能切換到臨時伺服器：{reason}（原 cycleapple 隊伍代碼 {oldCode}）");
            Plugin.Config.Backend = BackendKind.Custom;
            Plugin.Config.Save();

            string code;
            try
            {
                var nickname = Plugin.Config.GetNickname(BackendKind.Custom);
                code = await party.CreatePartyAsync(string.IsNullOrWhiteSpace(nickname) ? null : nickname);
                foreach (var t in pending)
                {
                    var key = await party.AddTreasureAsync(t);
                    if (!string.IsNullOrEmpty(t.Note)) await party.UpdateTreasureNoteAsync(key, t.Note!);
                    if (!string.IsNullOrEmpty(t.Player)) await party.UpdateTreasurePlayerAsync(key, t.Player!);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"{ex.Message}（原 cycleapple 隊伍代碼 {oldCode}）", ex);
            }

            StartSessionAfterJoin();
            var invite = PartyInvite.Build(target.DatabaseUrl, code);
            SendInviteToChat(invite);
            Plugin.Log.Information($"[臨時伺服器] 已從 cycleapple 隊伍 {oldCode} 搬 {pending.Count} 張藏寶圖到臨時伺服器隊伍 {code}");
            ShowStatus($"已搬到臨時伺服器：新隊伍 {code}（{pending.Count} 張），邀請網址已用 {cmd} 送出");
        });
    }

    /// <summary>在 framework 執行緒用設定的頻道指令送出一行邀請。</summary>
    private static void SendInviteToChat(string invite)
    {
        if (FrameworkUnloadGuard.ShouldSkip("送出邀請網址")) return;
        var cmd = TreasureListPanel.NormalizeChatCommand(Plugin.Config.TreasureChatCommand);
        Plugin.Framework.RunOnFrameworkThread(() => Plugin.ChatSender.SendMessage($"{cmd} {PartyInvite.ChatPrefix} {invite}"));
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
                ShowStatus("優化失敗: " + ex.Message, isError: true);
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
                ShowStatus("失敗: " + ex.Message, isError: true);
            }
            finally
            {
                _busy = false;
            }
        });
    }

    private void ShowStatus(string msg, bool isError = false)
    {
        _statusIsError = isError;
        _statusMessage = msg;
        // 失敗訊息（例如連不上同步伺服器）停留久一點，免得沒看到就消失。
        _statusUntil = DateTime.UtcNow.AddSeconds(isError ? 15 : 4);
    }
}
