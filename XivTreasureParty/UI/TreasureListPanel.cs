using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using XivTreasureParty.Data;
using XivTreasureParty.Game;
using XivTreasureParty.Party.Models;

namespace XivTreasureParty.UI;

public sealed class TreasureListPanel
{
    private string? _editingKey;
    private string _editBuf = "";
    private int _editMode; // 0: none, 1: note, 2: player
    private bool _editJustStarted;
    private string _chatCommandBuf;
    private string _templateBuf;
    private bool _showTemplate;
    private string? _previewKey;
    private string _previewText = string.Empty;

    public TreasureListPanel()
    {
        _chatCommandBuf = NormalizeChatCommand(Plugin.Config.TreasureChatCommand);
        _templateBuf = ChatTemplate.Sanitize(Plugin.Config.TreasureChatTemplate);
    }

    public void Draw()
    {
        ImGui.TextUnformatted("藏寶圖清單");
        ImGui.Separator();

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("發送頻道指令");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(UiScale.Px(90f));
        var commandCommitted = ImGui.InputText(
            "##treasure-chat-command",
            ref _chatCommandBuf,
            16,
            ImGuiInputTextFlags.EnterReturnsTrue);
        if (commandCommitted || ImGui.IsItemDeactivatedAfterEdit())
            SaveChatCommand();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("藏寶圖座標的發送頻道，例如 /p（小隊）或 /cwl1（跨界通訊貝 1）");
        ImGui.SameLine();
        if (ImGui.SmallButton(_showTemplate ? "收起訊息格式" : "訊息格式…"))
            _showTemplate = !_showTemplate;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("自訂按「發送」時送出的訊息內容（例如只送角色和地圖連結）");
        if (_showTemplate)
            DrawTemplateEditor();

        var showOnMappy = Plugin.Config.ShowTreasuresOnMappy;
        if (ImGui.Checkbox("藏寶圖標到 Mappy 地圖", ref showOnMappy))
        {
            Plugin.Config.ShowTreasuresOnMappy = showOnMappy;
            Plugin.Config.Save();
            Plugin.MappyMarkers.MarkDirty();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把清單裡尚未完成的藏寶點「全部」畫到 Mappy 的地圖上（不只一個旗標）。\n"
                             + "需要安裝 Mappy；沒裝時這個選項不會有任何作用，原本的『地圖』按鈕照舊。\n"
                             + "標記可以在 Mappy 的設定裡以來源「XivTreasureParty」單獨關掉。");

        if (!Plugin.PartyService.IsInParty)
        {
            ImGui.TextDisabled("加入隊伍後會在此顯示同步的藏寶圖");
            return;
        }

        var treasures = Plugin.SyncService.Treasures.Values
            .OrderBy(t => t.Completed)
            .ThenBy(t => t.Order)
            .ToList();

        if (treasures.Count == 0)
        {
            ImGui.TextDisabled("清單為空，從左側新增藏寶圖");
            return;
        }

        DrawRouteControls(treasures);

        if (ImGui.BeginTable("##treasures", 6,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("順序", ImGuiTableColumnFlags.WidthFixed, UiScale.Px(40f));
            ImGui.TableSetupColumn("等級", ImGuiTableColumnFlags.WidthFixed, UiScale.Px(60f));
            ImGui.TableSetupColumn("地點 / 座標", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("負責玩家", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("備註", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, UiScale.Px(320f));
            ImGui.TableHeadersRow();

            for (var i = 0; i < treasures.Count; i++)
                DrawRow(treasures, i);

            ImGui.EndTable();
        }
    }

    private void DrawRow(System.Collections.Generic.List<Treasure> list, int index)
    {
        var t = list[index];
        var navStatus = TreasureNavigator.GetStatus();
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        var navigatingHere = navStatus.Busy == true && TreasureNavigator.NavigatingKey == t.FirebaseKey;
        ImGui.TextUnformatted(navigatingHere ? "→" + t.Order : t.Order.ToString());
        if (navigatingHere && ImGui.IsItemHovered())
            ImGui.SetTooltip("Lifestream 正在前往這一筆");

        ImGui.TableNextColumn();
        var grade = GradeData.GetByItemId(t.GradeItemId);
        if (t.Completed)
            ImGui.TextDisabled(grade?.Grade ?? $"#{t.GradeItemId}");
        else
            ImGui.TextUnformatted(grade?.Grade ?? $"#{t.GradeItemId}");

        ImGui.TableNextColumn();
        var line1 = MapData.GetMapName(t.MapId);
        var line2 = $"X: {t.Coords.X:0.0}  Y: {t.Coords.Y:0.0}";
        if (t.Completed)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.6f, 0.6f, 0.6f, 1f));
            ImGui.TextUnformatted(line1);
            ImGui.TextUnformatted(line2);
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.TextUnformatted(line1);
            ImGui.TextUnformatted(line2);
        }

        ImGui.TableNextColumn();
        if (_editingKey == t.FirebaseKey && _editMode == 2)
        {
            if (_editJustStarted) { ImGui.SetKeyboardFocusHere(); _editJustStarted = false; }
            ImGui.SetNextItemWidth(-1);
            var committed = ImGui.InputText($"##player-{t.FirebaseKey}", ref _editBuf, 32, ImGuiInputTextFlags.EnterReturnsTrue);
            if (committed || ImGui.IsItemDeactivatedAfterEdit())
                CommitPlayer(t);
            else if (ImGui.IsItemDeactivated())
            {
                _editingKey = null; _editMode = 0; _editBuf = "";
            }
        }
        else
        {
            var label = string.IsNullOrEmpty(t.Player) ? "(未指定)" : t.Player!;
            if (ImGui.Selectable($"{label}##p-{t.FirebaseKey}"))
            {
                _editingKey = t.FirebaseKey;
                _editMode = 2;
                _editBuf = t.Player ?? "";
                _editJustStarted = true;
            }
        }

        ImGui.TableNextColumn();
        if (_editingKey == t.FirebaseKey && _editMode == 1)
        {
            if (_editJustStarted) { ImGui.SetKeyboardFocusHere(); _editJustStarted = false; }
            ImGui.SetNextItemWidth(-1);
            var committed = ImGui.InputText($"##note-{t.FirebaseKey}", ref _editBuf, 64, ImGuiInputTextFlags.EnterReturnsTrue);
            if (committed || ImGui.IsItemDeactivatedAfterEdit())
                CommitNote(t);
            else if (ImGui.IsItemDeactivated())
            {
                _editingKey = null; _editMode = 0; _editBuf = "";
            }
        }
        else
        {
            var label = string.IsNullOrEmpty(t.Note) ? "(點擊新增)" : t.Note!;
            if (ImGui.Selectable($"{label}##n-{t.FirebaseKey}"))
            {
                _editingKey = t.FirebaseKey;
                _editMode = 1;
                _editBuf = t.Note ?? "";
                _editJustStarted = true;
            }
        }

        ImGui.TableNextColumn();
        var canModifyOrder = Plugin.PartyService.CanModifyOrder();

        if (ImGui.SmallButton((t.Completed ? "取消" : "完成") + $"##c-{t.FirebaseKey}"))
            _ = Plugin.PartyService.ToggleTreasureCompleteAsync(t.FirebaseKey);
        ImGui.SameLine();

        if (ImGui.SmallButton($"↑##u-{t.FirebaseKey}") && canModifyOrder && index > 0)
            _ = Plugin.PartyService.SwapTreasureOrderAsync(t.FirebaseKey, list[index - 1].FirebaseKey);
        ImGui.SameLine();
        if (ImGui.SmallButton($"↓##d-{t.FirebaseKey}") && canModifyOrder && index < list.Count - 1)
            _ = Plugin.PartyService.SwapTreasureOrderAsync(t.FirebaseKey, list[index + 1].FirebaseKey);
        ImGui.SameLine();

        if (ImGui.SmallButton($"地圖##map-{t.FirebaseKey}"))
        {
            TreasureLocator.OpenInGame(t);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("在遊戲內開啟地圖並將旗標設在藏寶點");
        ImGui.SameLine();

        var canNavigate = navStatus.LifestreamAvailable && navStatus.Busy != true && !t.Completed;
        if (!canNavigate) ImGui.BeginDisabled();
        if (ImGui.SmallButton("前往##go-" + t.FirebaseKey))
            TreasureNavigator.TryGoTo(t);
        if (!canNavigate) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(NavigateTooltip(navStatus, t));
        ImGui.SameLine();

        if (ImGui.SmallButton($"發送##send-{t.FirebaseKey}"))
        {
            // 若玩家剛改完指令就直接按發送，先提交目前輸入值，避免這次仍使用舊頻道。
            SaveChatCommand();
            SendTreasureToChat(t);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"以 {NormalizeChatCommand(Plugin.Config.TreasureChatCommand)} 發送『地圖 ( X , Y )』，含可點擊的地圖連結");
        ImGui.SameLine();

        if (ImGui.SmallButton($"刪除##rm-{t.FirebaseKey}"))
            _ = Plugin.PartyService.RemoveTreasureAsync(t.FirebaseKey);
    }

    /// <summary>
    /// 清單上方的路線控制列：一鍵前往「依目前排序的下一個未完成藏寶點」。
    /// </summary>
    /// <remarks>
    /// 🔴 順序沿用清單上看到的排序（已完成的排最後，其餘依 <c>order</c>），
    /// 而 <c>order</c> 正是「優化路線」用 <see cref="Party.RouteOptimizer"/> 寫回去的那一份
    /// ——所以「下一個」永遠是使用者在畫面上看到的下一列，不會偷偷走另一條路線。
    /// 🔴 只有按下去才會動，沒有任何自動接手；也不會幫忙開寶箱或打怪。
    /// </remarks>
    private static void DrawRouteControls(System.Collections.Generic.List<Treasure> treasures)
    {
        var status = TreasureNavigator.GetStatus();
        var next = treasures.FirstOrDefault(x => !x.Completed);

        var canGo = status.LifestreamAvailable && status.Busy != true && next != null;
        if (!canGo) ImGui.BeginDisabled();
        if (ImGui.Button("依路線前往下一個") && next != null)
            TreasureNavigator.TryGoTo(next);
        if (!canGo) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(NavigateTooltip(status, next));
        ImGui.SameLine();

        var fly = Plugin.Config.NavigateWithFlight;
        if (ImGui.Checkbox("允許飛行", ref fly))
        {
            Plugin.Config.NavigateWithFlight = fly;
            Plugin.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("最後一段路允許使用飛行坐騎。\n區域不可飛、或還沒解鎖飛行時，Lifestream 會自動改用走的。");
        ImGui.SameLine();

        // 「不知道」要在列上看得見：問不到 Lifestream 狀態時畫灰字的問號，不要畫成「就緒」。
        ImGui.AlignTextToFramePadding();
        if (!status.LifestreamAvailable)
            ImGui.TextDisabled("需要 Lifestream");
        else if (status.Busy == true)
            ImGui.TextDisabled("移動中…");
        else if (status.Busy == null)
            ImGui.TextDisabled("狀態 ?");
        else
            ImGui.TextDisabled("可出發");
    }

    /// <summary>「前往」按鈕的 tooltip：能按時說要去哪，不能按時說為什麼。</summary>
    private static string NavigateTooltip(TreasureNavigator.NavStatus status, Treasure? target)
    {
        if (!status.LifestreamAvailable)
            return "需要 Lifestream\n請先安裝並啟用 Lifestream（實際走路／飛行由 vnavmesh 負責）。";
        if (target == null)
            return "清單上沒有未完成的藏寶點";
        if (target.Completed)
            return "這一筆已經標記完成了";
        if (status.Busy == true)
            return "Lifestream 正在移動中，等它抵達之後再按";

        var line = $"傳送並移動到 {MapData.GetMapName(target.MapId)} ( {target.Coords.X:0.0} , {target.Coords.Y:0.0} )。\n"
                   + "只把人送過去，不會自動開寶箱、不會自動戰鬥。";
        if (status.Busy == null)
            line += "\n（問不到 Lifestream 目前狀態；按下去若被拒絕，稍後再試一次。）";
        return line;
    }

    /// <summary>
    /// 以玩家設定的頻道指令＋訊息樣板發送（<see cref="ChatTemplate"/>）。預設樣板＝下面這種**純文字座標** —
    /// 含一個保險的 PreMapLinkPayload 在文字後面（給有裝 AutoConvertMapLink
    /// 等接收端 rewrite 工具的玩家可點），但即使 payload 被 input
    /// sanitizer 剝掉，文字部分的座標一定看得到。
    ///
    /// 為何不靠 PreMapLinkPayload 取代文字：實測 ProcessChatBoxEntry 對
    /// AutoTranslateText 類 payload 有 whitelist (只允許 &lt;flag&gt;/&lt;pos&gt;
    /// 等預設 token)，地圖位置 chunk `[0xC9][0x04]` 雖然格式合法但不在
    /// whitelist，會被靜默剝掉，導致整段座標消失，下游 viewer 只看到
    /// "/p 玩家 | 最近傳送水晶:[..]"。
    /// </summary>
    private static void SendTreasureToChat(Treasure t)
    {
        try
        {
            var preLink = TreasureLocator.TryBuildAutoTranslateMapLink(t);
            var chatCommand = NormalizeChatCommand(Plugin.Config.TreasureChatCommand);
            var parts = ChatTemplate.Render(ChatTemplate.Sanitize(Plugin.Config.TreasureChatTemplate), TemplateValues(t, preLink != null));

            var sb = new Dalamud.Game.Text.SeStringHandling.SeStringBuilder();
            var prefix = chatCommand + " ";
            foreach (var part in parts)
            {
                if (part.IsLink)
                {
                    if (prefix.Length > 0) { sb.AddText(prefix); prefix = string.Empty; }
                    sb.Add(preLink!);
                }
                else
                {
                    sb.AddText(prefix + part.Text);
                    prefix = string.Empty;
                }
            }
            if (prefix.Length > 0) sb.AddText(prefix);

            var message = sb.Build();
            var bytes = message.Encode().Length;
            if (bytes >= ChatTemplate.MaxMessageBytes)
            {
                Plugin.Log.Warning($"[SendTreasure] 訊息太長（{bytes} 位元組），沒有送出");
                Plugin.ChatGui.PrintError($"[藏寶圖小幫手] 訊息太長（{bytes} 位元組，上限 {ChatTemplate.MaxMessageBytes - 1}），沒有送出；請縮短訊息樣板。");
                return;
            }
            Plugin.ChatSender.SendSeString(message);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[SendTreasure] 發送聊天失敗");
        }
    }

    private static ChatTemplate.Values TemplateValues(Treasure t, bool hasLink) => new(
        string.IsNullOrWhiteSpace(t.Player) ? string.Empty : t.Player,
        MapData.GetMapName(t.MapId),
        t.Coords.X,
        t.Coords.Y,
        hasLink,
        AetheryteData.FindNearestByMapId(t.MapId, t.Coords.X, t.Coords.Y)?.Name);

    private void DrawTemplateEditor()
    {
        ImGui.TextUnformatted("訊息樣板（頻道指令固定放在最前面）");
        ImGui.SetNextItemWidth(-1);
        var committed = ImGui.InputText("##treasure-chat-template", ref _templateBuf, 512, ImGuiInputTextFlags.EnterReturnsTrue);
        if (committed || ImGui.IsItemDeactivatedAfterEdit())
            SaveTemplate(_templateBuf);

        if (ImGui.SmallButton("完整（預設）"))
            SaveTemplate(ChatTemplate.Default);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("角色、地圖名與座標文字、地圖連結、最近傳送水晶（和以前一樣）");
        ImGui.SameLine();
        if (ImGui.SmallButton("最簡"))
            SaveTemplate(ChatTemplate.Minimal);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("只送「頻道指令 角色 地圖連結」。\n如果隊友那邊只看到名字、沒有連結，請改回完整格式。");

        ImGui.TextDisabled("可用：" + string.Join(" ", ChatTemplate.Placeholders.Select(p => p.Token)));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join("\n", ChatTemplate.Placeholders.Select(p => $"{p.Token}：{p.Help}"))
                             + "\n\n值是空的佔位符會連同旁邊一個空白一起省略。");

        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted("預覽：" + BuildPreview());
        ImGui.PopTextWrapPos();
    }

    /// <summary>用目前清單的第一筆代入；樣板或那一筆沒變就沿用上次的結果。</summary>
    private string BuildPreview()
    {
        var first = Plugin.PartyService.IsInParty
            ? Plugin.SyncService.Treasures.Values.OrderBy(t => t.Completed).ThenBy(t => t.Order).FirstOrDefault()
            : null;
        if (first == null)
            return "（清單有藏寶圖後，會用第一筆代入顯示）";

        var template = ChatTemplate.Sanitize(Plugin.Config.TreasureChatTemplate);
        var command = NormalizeChatCommand(Plugin.Config.TreasureChatCommand);
        var key = $"{template}|{command}|{first.FirebaseKey}|{first.MapId}|{first.Coords.X}|{first.Coords.Y}|{first.Player}";
        if (key != _previewKey)
        {
            _previewKey = key;
            var hasLink = TreasureLocator.TryBuildAutoTranslateMapLink(first) != null;
            _previewText = ChatTemplate.Preview(command, ChatTemplate.Render(template, TemplateValues(first, hasLink)));
        }
        return _previewText;
    }

    private void SaveTemplate(string template)
    {
        var sanitized = ChatTemplate.Sanitize(template);
        _templateBuf = sanitized;
        if (Plugin.Config.TreasureChatTemplate == sanitized)
            return;
        Plugin.Config.TreasureChatTemplate = sanitized;
        Plugin.Config.Save();
    }

    private void SaveChatCommand()
    {
        _chatCommandBuf = NormalizeChatCommand(_chatCommandBuf);
        if (Plugin.Config.TreasureChatCommand == _chatCommandBuf)
            return;

        Plugin.Config.TreasureChatCommand = _chatCommandBuf;
        Plugin.Config.Save();
    }

    internal static string NormalizeChatCommand(string? command)
    {
        command = command?.Trim();
        if (string.IsNullOrEmpty(command))
            return "/p";

        var firstWhitespace = command.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        if (firstWhitespace >= 0)
            command = command[..firstWhitespace];

        if (!command.StartsWith("/", StringComparison.Ordinal))
            command = "/" + command;

        return command.Length > 1 ? command : "/p";
    }

    private void CommitPlayer(Treasure t)
    {
        var newValue = _editBuf;
        var key = t.FirebaseKey;
        _editingKey = null; _editMode = 0; _editBuf = "";
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await Plugin.PartyService.UpdateTreasurePlayerAsync(key, newValue); }
            catch (Exception ex) { Plugin.Log.Error(ex, "更新玩家失敗"); }
        });
    }

    private void CommitNote(Treasure t)
    {
        var newValue = _editBuf;
        var key = t.FirebaseKey;
        _editingKey = null; _editMode = 0; _editBuf = "";
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await Plugin.PartyService.UpdateTreasureNoteAsync(key, newValue); }
            catch (Exception ex) { Plugin.Log.Error(ex, "更新備註失敗"); }
        });
    }
}
