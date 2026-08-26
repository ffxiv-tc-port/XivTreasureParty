using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using XivTreasureParty.Data;
using XivTreasureParty.Party.Models;

namespace XivTreasureParty.Game;

/// <summary>
/// 把隊伍同步到的「全部」藏寶點推到 Mappy 的地圖上，補足原本一次只能對一筆下旗標的限制。
/// 原本的「地圖」按鈕（OpenMapWithMapLink）行為完全不動，這裡是額外的顯示層。
///
/// 走 Mappy 的通用標記 IPC（契約在 Mappy/Controllers/MarkerIpcController.cs）：
///   Mappy.GetVersion()  -> int
///   Mappy.AddMarker(source, mapId, mapCoords, iconId, tooltip) -> uint handle（0 = 被拒絕）
///   Mappy.RemoveMarker(source, handle) -> bool
///   Mappy.ClearSource(source) -> bool
///
/// 幾個刻意的設計：
///   * 沒裝 Mappy（或 Mappy 還沒載入）時整個橋接靜默待命，不寫 log、不影響其他功能。
///   * 座標直接用 Treasure.Coords —— 那已經是「地圖可見座標」（TreasureHuntReader 在
///     Resolve() 就用 SizeFactor 換算過，TryBuildAutoTranslateMapLink 也是拿它去 *產生* raw
///     位置），正好就是 Mappy IPC 要的東西，不需要再換算一次。
///   * 不做「只推當前地圖」的過濾：Mappy 自己就會用 AgentMap.SelectedMapId 濾，
///     全部推上去反而讓玩家翻到別張地圖時也看得到隊友的圖。
///   * 每 30 秒做一次完整重推，用來從「Mappy 被重新載入 → 我們手上的 handle 全部作廢」
///     這種偵測不到的狀況復原。RemoveMarker 不會把來源整個拿掉，所以重推不會洗 Mappy 的 log。
/// </summary>
public sealed class MappyMarkerBridge : IDisposable
{
    /// <summary>
    /// Mappy 設定頁上顯示的來源名稱，也是使用者逐來源開關的鍵。
    /// 🔴 改這個字串會讓使用者原本關掉的設定失效（變成一個全新的來源）。
    /// </summary>
    private const string MarkerSource = "XivTreasureParty";

    private const int MinimumIpcVersion = 1;

    /// <summary>
    /// 查不到藏寶圖等級對應的圖示時的後備值。60354 是 TreasureHuntRank 表裡
    /// 絕大多數等級用的那顆藏寶圖圖示（台服 7.20 EXD 實查：31 列中 20 列是它）。
    /// </summary>
    private const uint FallbackIconId = 60354;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResyncInterval = TimeSpan.FromSeconds(30);

    private readonly IFramework _framework;
    private readonly Party.SyncService _syncService;

    private readonly ICallGateSubscriber<int> _getVersion;
    private readonly ICallGateSubscriber<string, uint, Vector2, uint, string, uint> _addMarker;
    private readonly ICallGateSubscriber<string, uint, bool> _removeMarker;
    private readonly ICallGateSubscriber<string, bool> _clearSource;

    /// <summary>FirebaseKey → 已經推給 Mappy 的那一筆。</summary>
    private readonly Dictionary<string, PushedMarker> _pushed = new();

    /// <summary>gradeItemId → iconId，第一次用到才查表。</summary>
    private static Dictionary<int, uint>? _iconByGradeItemId;

    private bool _dirty = true;
    private bool _running;
    private bool _ipcReady;
    private bool _loggedUnavailableVersion;
    private bool _loggedSyncFailure;
    private DateTime _nextFlushUtc = DateTime.MinValue;
    private DateTime _nextResyncUtc = DateTime.MinValue;

    private readonly record struct PushedMarker(uint Handle, uint MapId, float X, float Y, uint IconId, string Tooltip);

    public MappyMarkerBridge(IFramework framework, Party.SyncService syncService)
    {
        _framework = framework;
        _syncService = syncService;

        _getVersion = Plugin.PluginInterface.GetIpcSubscriber<int>("Mappy.GetVersion");
        _addMarker = Plugin.PluginInterface.GetIpcSubscriber<string, uint, Vector2, uint, string, uint>("Mappy.AddMarker");
        _removeMarker = Plugin.PluginInterface.GetIpcSubscriber<string, uint, bool>("Mappy.RemoveMarker");
        _clearSource = Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Mappy.ClearSource");

        _syncService.TreasuresChanged += MarkDirty;
        _framework.Update += OnUpdate;
    }

    /// <summary>同步集合有變動、或使用者切了開關時呼叫，下一個 tick 會重算。</summary>
    public void MarkDirty() => _dirty = true;

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _syncService.TreasuresChanged -= MarkDirty;

        // 走人前把標記收乾淨；Mappy 不在就當作沒事發生。
        try
        {
            if (_pushed.Count > 0)
                _clearSource.InvokeFunc(MarkerSource);
        }
        catch
        {
            // Mappy 沒載入 / 已經先被卸載，兩種都不需要處理。
        }

        _pushed.Clear();
    }

    private void OnUpdate(IFramework framework)
    {
        if (_running) return;

        var now = DateTime.UtcNow;
        if (now < _nextFlushUtc) return;
        _nextFlushUtc = now.Add(FlushInterval);

        _running = true;
        try
        {
            if (!Plugin.Config.ShowTreasuresOnMappy)
            {
                if (_pushed.Count > 0)
                    ClearAll();
                return;
            }

            var forceResync = now >= _nextResyncUtc;
            if (!_dirty && !forceResync) return;

            if (!EnsureIpcReady())
            {
                // Mappy 不在就靜默待命：保留 dirty，過一陣子再試。
                _nextFlushUtc = now.Add(RetryInterval);
                _pushed.Clear();
                return;
            }

            _nextResyncUtc = now.Add(ResyncInterval);
            Reconcile(forceResync);
            _dirty = false;
            _loggedSyncFailure = false;
        }
        catch (Exception ex)
        {
            // 不論什麼原因失敗，都不要讓它變成每幀噴一次；下一輪重試。
            // 只在「連續失敗」的第一次寫 log，避免持續性錯誤（例如 IPC 型別對不上）洗檔。
            if (!_loggedSyncFailure)
            {
                _loggedSyncFailure = true;
                Plugin.Log.Warning($"[Mappy 標記] 同步失敗，稍後重試：{ex.Message}");
            }

            _ipcReady = false;
            _pushed.Clear();
            _nextFlushUtc = DateTime.UtcNow.Add(RetryInterval);
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    /// 問一次 Mappy 的 IPC 版本。Mappy 不在時 InvokeFunc 會丟 IpcNotReadyError，
    /// 這是預期中的狀況，不寫 log。
    /// </summary>
    private bool EnsureIpcReady()
    {
        try
        {
            var version = _getVersion.InvokeFunc();
            if (version < MinimumIpcVersion)
            {
                if (!_loggedUnavailableVersion)
                {
                    _loggedUnavailableVersion = true;
                    Plugin.Log.Information(
                        $"[Mappy 標記] Mappy 的標記 IPC 版本是 {version}，需要 {MinimumIpcVersion} 以上，本功能停用。");
                }

                _ipcReady = false;
                return false;
            }

            if (!_ipcReady)
            {
                _ipcReady = true;
                Plugin.Log.Information($"[Mappy 標記] 已連上 Mappy 標記 IPC（版本 {version}），開始把隊伍藏寶點畫到地圖上。");
            }

            _loggedUnavailableVersion = false;
            return true;
        }
        catch (Exception)
        {
            // 沒裝 Mappy、Mappy 尚未載入、或版本太舊沒有這組端點 —— 全部靜默待命。
            if (_ipcReady)
            {
                _ipcReady = false;
                Plugin.Log.Information("[Mappy 標記] 與 Mappy 的連線中斷（可能是 Mappy 被停用或重新載入），標記暫停。");
            }

            return false;
        }
    }

    /// <summary>把「應該顯示的」和「已經推上去的」對齊：多的移掉、缺的補上、變了的重推。</summary>
    private void Reconcile(bool forceRepush)
    {
        if (forceRepush)
        {
            // 完整重推：先把手上的 handle 全部退掉（Mappy 已重載時會回 false，無所謂），
            // 再讓下面的迴圈整批重新加回去。
            foreach (var entry in _pushed.Values)
            {
                try { _removeMarker.InvokeFunc(MarkerSource, entry.Handle); }
                catch { /* Mappy 剛好在這一刻消失，下一輪 EnsureIpcReady 會處理 */ }
            }

            _pushed.Clear();
        }

        var desired = BuildDesiredMarkers();

        // 1) 已經不該存在、或內容變了的先移除。
        List<string>? stale = null;
        foreach (var (key, entry) in _pushed)
        {
            if (desired.TryGetValue(key, out var want)
                && want.MapId == entry.MapId
                && want.X == entry.X
                && want.Y == entry.Y
                && want.IconId == entry.IconId
                && want.Tooltip == entry.Tooltip)
                continue;

            (stale ??= []).Add(key);
        }

        if (stale != null)
        {
            foreach (var key in stale)
            {
                _removeMarker.InvokeFunc(MarkerSource, _pushed[key].Handle);
                _pushed.Remove(key);
            }
        }

        // 2) 還沒推上去的補上。
        foreach (var (key, want) in desired)
        {
            if (_pushed.ContainsKey(key)) continue;

            var handle = _addMarker.InvokeFunc(
                MarkerSource,
                want.MapId,
                new Vector2(want.X, want.Y),
                want.IconId,
                want.Tooltip);

            // 0 = 被 Mappy 拒絕（超過數量上限之類）。跳過即可，下一輪還會再試。
            if (handle == 0) continue;

            _pushed[key] = want with { Handle = handle };
        }
    }

    /// <summary>目前應該畫在地圖上的標記：隊伍清單裡尚未完成、且查得到地圖的藏寶點。</summary>
    private Dictionary<string, PushedMarker> BuildDesiredMarkers()
    {
        var result = new Dictionary<string, PushedMarker>();

        if (!Plugin.PartyService.IsInParty) return result;

        foreach (var (key, treasure) in _syncService.Treasures)
        {
            if (string.IsNullOrEmpty(key)) continue;

            // 挖完的點就不要繼續佔著地圖。
            if (treasure.Completed) continue;
            if (treasure.MapId <= 0) continue;

            var coords = treasure.Coords;
            if (coords == null) continue;
            if (!float.IsFinite(coords.X) || !float.IsFinite(coords.Y)) continue;

            result[key] = new PushedMarker(
                0,
                (uint)treasure.MapId,
                coords.X,
                coords.Y,
                GetIconId(treasure.GradeItemId),
                BuildTooltip(treasure));
        }

        return result;
    }

    private static string BuildTooltip(Treasure treasure)
    {
        var grade = GradeData.GetByItemId(treasure.GradeItemId);
        var gradeLabel = grade == null
            ? $"藏寶圖 #{treasure.GradeItemId}"
            : $"{grade.Grade} {grade.Name}";

        var owner = FirstNonBlank(treasure.Player, treasure.AddedByNickname) ?? "(未指定)";

        var text = $"{gradeLabel}\n負責：{owner}\n{MapData.GetMapName(treasure.MapId)} ( {treasure.Coords.X:0.0} , {treasure.Coords.Y:0.0} )";

        if (!string.IsNullOrWhiteSpace(treasure.Note))
            text += $"\n備註：{treasure.Note!.Trim()}";

        return text;
    }

    private static string? FirstNonBlank(string? a, string? b)
    {
        if (!string.IsNullOrWhiteSpace(a)) return a!.Trim();
        if (!string.IsNullOrWhiteSpace(b)) return b!.Trim();
        return null;
    }

    /// <summary>
    /// 藏寶圖等級 → 地圖圖示。用遊戲自己的 TreasureHuntRank.Icon（G1~G12 是 60356/60355，
    /// G13 之後統一 60354），查不到就退回 <see cref="FallbackIconId"/>。
    /// </summary>
    private static uint GetIconId(int gradeItemId)
    {
        var table = _iconByGradeItemId ??= BuildIconTable();
        return table.TryGetValue(gradeItemId, out var icon) && icon != 0 ? icon : FallbackIconId;
    }

    private static Dictionary<int, uint> BuildIconTable()
    {
        var table = new Dictionary<int, uint>();

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TreasureHuntRank>();
            if (sheet == null) return table;

            foreach (var row in sheet)
            {
                var itemId = (int)row.ItemName.RowId;
                if (itemId == 0) continue;

                var icon = (uint)row.Icon;
                if (icon == 0) continue;

                table[itemId] = icon;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Mappy 標記] 讀 TreasureHuntRank 圖示失敗，改用預設圖示：{ex.Message}");
        }

        return table;
    }

    private void ClearAll()
    {
        try { _clearSource.InvokeFunc(MarkerSource); }
        catch { /* Mappy 不在就沒東西要清 */ }

        _pushed.Clear();
        _dirty = true;
    }
}
