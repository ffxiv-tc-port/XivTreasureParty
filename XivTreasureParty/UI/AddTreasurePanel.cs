using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using XivTreasureParty.Data;
using XivTreasureParty.Game;

namespace XivTreasureParty.UI;

public sealed class AddTreasurePanel
{
    private int _gradeIndex = 0;
    private int _mapIndex = 0;
    private int _spotIndex = 0;
    private string _status = "";
    private DateTime _statusUntil;
    private bool _busy;

    public void Draw()
    {
        ImGui.TextUnformatted("新增藏寶圖");

        if (!Plugin.PartyService.IsInParty)
        {
            ImGui.TextDisabled("加入隊伍後才能新增");
            return;
        }

        DrawReadCurrentButton();

        var grades = GradeData.All;
        if (_gradeIndex < 0 || _gradeIndex >= grades.Count) _gradeIndex = 0;
        var currentGrade = grades[_gradeIndex];

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##grade", $"{currentGrade.Grade} {currentGrade.Name}"))
        {
            for (var i = 0; i < grades.Count; i++)
            {
                var g = grades[i];
                var selected = i == _gradeIndex;
                var label = $"{g.Grade} {g.Name} [{(g.PartySize == 8 ? "8人" : "單人")}]";
                if (ImGui.Selectable(label, selected))
                {
                    _gradeIndex = i;
                    _mapIndex = 0;
                    _spotIndex = 0;
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var maps = TreasureData.MapsForItem(currentGrade.ItemId).ToList();
        if (maps.Count == 0)
        {
            ImGui.TextDisabled("此等級暫無可用地圖");
            return;
        }
        if (_mapIndex < 0 || _mapIndex >= maps.Count) _mapIndex = 0;
        var currentMapId = maps[_mapIndex];

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##map", MapData.GetMapName(currentMapId)))
        {
            for (var i = 0; i < maps.Count; i++)
            {
                var selected = i == _mapIndex;
                if (ImGui.Selectable(MapData.GetMapName(maps[i]), selected))
                {
                    _mapIndex = i;
                    _spotIndex = 0;
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var spots = TreasureData.ByItemAndMap(currentGrade.ItemId, currentMapId).ToList();
        if (spots.Count == 0)
        {
            ImGui.TextDisabled("此地圖暫無藏寶點資料");
            return;
        }
        if (_spotIndex < 0 || _spotIndex >= spots.Count) _spotIndex = 0;
        var currentSpot = spots[_spotIndex];

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##spot", $"{currentSpot.Label}  ({currentSpot.X:0.0}, {currentSpot.Y:0.0})"))
        {
            for (var i = 0; i < spots.Count; i++)
            {
                var s = spots[i];
                var label = $"{s.Label}  X: {s.X:0.0}  Y: {s.Y:0.0}";
                if (ImGui.Selectable(label, i == _spotIndex))
                    _spotIndex = i;
                if (i == _spotIndex) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.Spacing();
        var disabled = _busy;
        if (disabled) ImGui.BeginDisabled();
        if (ImGui.Button("加入清單", new System.Numerics.Vector2(-1, 30)))
        {
            var spot = currentSpot;
            RunAsync(async () =>
            {
                var treasure = spot.ToTreasure(Plugin.PartyService.Nickname);
                await Plugin.PartyService.AddTreasureAsync(treasure);
            }, $"已加入 {spot.Label} ({spot.X:0.0}, {spot.Y:0.0})");
        }
        if (disabled) ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(_status) && DateTime.UtcNow < _statusUntil)
        {
            ImGui.Spacing();
            ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.9f, 0.4f, 1f), _status);
        }
    }

    /// <summary>
    /// 在欄位上方畫「讀取當前藏寶圖」按鈕。按下會呼叫 TreasureHuntReader 把玩家已解碼的
    /// 圖 rank/spot 轉成 (gradeItemId, mapId, x, y)，然後把三個 dropdown 預選好（不會直接加入清單）。
    /// </summary>
    private void DrawReadCurrentButton()
    {
        if (!Plugin.HuntReader.IsAvailable)
        {
            ImGui.TextDisabled("(讀取藏寶圖功能不可用，signature 解析失敗)");
            ImGui.Spacing();
            return;
        }

        if (ImGui.Button("從遊戲讀取當前藏寶圖", new System.Numerics.Vector2(-1, 26)))
        {
            var decoded = Plugin.HuntReader.ReadAndResolve();
            if (decoded == null)
            {
                ShowStatus("目前背包中沒有已解碼的藏寶圖");
                return;
            }
            ApplyDecoded(decoded);

            if (Plugin.Config.AutoOpenMapOnCapture)
            {
                var fake = new Party.Models.Treasure
                {
                    MapId = decoded.MapId,
                    Coords = new Party.Models.TreasureCoords { X = decoded.X, Y = decoded.Y }
                };
                TreasureLocator.OpenInGame(fake);
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("讀取你背包中最新一張已解碼的藏寶圖，預選下方欄位。按下『加入清單』才會推送到隊伍。");

        var autoCapture = Plugin.Config.AutoCaptureOnDecode;
        if (ImGui.Checkbox("打開藏寶圖時自動選取", ref autoCapture))
        {
            Plugin.Config.AutoCaptureOnDecode = autoCapture;
            Plugin.Config.Save();
            if (autoCapture) Plugin.HuntAutoCapture.Reset();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("偵測到玩家解碼新藏寶圖時自動幫你填好下方欄位（仍需按『加入清單』才推送）");
        ImGui.SameLine();
        var autoOpen = Plugin.Config.AutoOpenMapOnCapture;
        if (ImGui.Checkbox("同時打旗標", ref autoOpen))
        {
            Plugin.Config.AutoOpenMapOnCapture = autoOpen;
            Plugin.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("讀取/偵測到藏寶圖時，同時在遊戲內開地圖並打旗標");

        ImGui.Spacing();
    }

    /// <summary>
    /// 從 TreasureHuntReader 讀到的結果對應到 dropdown 索引。公開給 Plugin 的 auto-poll 呼叫。
    /// </summary>
    public void ApplyDecoded(DecodedTreasure decoded)
    {
        var grades = GradeData.All;
        var gradeIdx = -1;
        for (var i = 0; i < grades.Count; i++)
        {
            if (grades[i].ItemId == decoded.GradeItemId) { gradeIdx = i; break; }
        }
        if (gradeIdx < 0)
        {
            ShowStatus($"未知等級 itemId={decoded.GradeItemId}");
            return;
        }

        var maps = TreasureData.MapsForItem(decoded.GradeItemId).ToList();
        var mapIdx = maps.IndexOf(decoded.MapId);
        if (mapIdx < 0)
        {
            ShowStatus($"未知地圖 mapId={decoded.MapId}");
            return;
        }

        var spots = TreasureData.ByItemAndMap(decoded.GradeItemId, decoded.MapId).ToList();
        // 以座標最接近的 spot 當匹配（精確匹配應該是一模一樣但保險起見用距離）
        var spotIdx = 0;
        if (spots.Count > 0)
        {
            var bestDist = float.MaxValue;
            for (var i = 0; i < spots.Count; i++)
            {
                var dx = spots[i].X - decoded.X;
                var dy = spots[i].Y - decoded.Y;
                var d = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; spotIdx = i; }
            }
        }

        _gradeIndex = gradeIdx;
        _mapIndex = mapIdx;
        _spotIndex = spotIdx;

        var g = grades[gradeIdx];
        var mapName = MapData.GetMapName(decoded.MapId);
        ShowStatus($"已預選: {g.Grade} {mapName} ({decoded.X:0.0}, {decoded.Y:0.0})");
    }

    private void ShowStatus(string msg)
    {
        _status = msg;
        _statusUntil = DateTime.UtcNow.AddSeconds(4);
    }

    private void RunAsync(Func<System.Threading.Tasks.Task> action, string successMessage)
    {
        if (_busy) return;
        _busy = true;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await action();
                _status = successMessage;
                _statusUntil = DateTime.UtcNow.AddSeconds(3);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "加入藏寶圖失敗");
                _status = "失敗: " + ex.Message;
                _statusUntil = DateTime.UtcNow.AddSeconds(5);
            }
            finally
            {
                _busy = false;
            }
        });
    }
}
