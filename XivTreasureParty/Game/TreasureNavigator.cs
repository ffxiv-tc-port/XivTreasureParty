using System;
using Dalamud.Plugin.Ipc;
using XivTreasureParty.Party.Models;

namespace XivTreasureParty.Game;

/// <summary>
/// 「前往藏寶點」：把清單上的一筆藏寶圖交給 Lifestream 帶路
/// （跨區自動傳送 → 由 vnavmesh 走或飛到那個點）。
///
/// 🔴 這裡**只有使用者親手按按鈕才會呼叫**，沒有任何事件驅動的自動接手鏈；
///    也不會幫玩家開寶箱、不會打怪，純粹是「把人送到座標」。
///
/// 🔴 跨外掛契約（對應 Lifestream/Lifestream/IPC/IPCProvider.cs、vnavmesh/vnavmesh/IPCProvider.cs）：
///   Lifestream.GoToMapPoint(uint territory, float worldX, float worldZ, bool fly) -> bool
///   Lifestream.IsBusy() -> bool
///   vnavmesh.Path.IsRunning() -> bool
/// 端點名改了要兩邊一起改，否則失敗形式是「按鈕永遠灰著」而不是報錯。
///
/// 📌 Lifestream 的 <c>GoToMapPoint</c> 回 false 代表**它一件事都沒排**
///   （忙碌中、人不能動、沒裝 vnavmesh、該區沒有已解鎖的乙太之光），呼叫端不要傻等。
/// </summary>
public static class TreasureNavigator
{
    /// <summary>Lifestream 的外掛內部名稱（<c>InstalledPlugins</c> 比對用）。</summary>
    private const string LifestreamInternalName = "Lifestream";

    /// <summary>
    /// 狀態查詢的最短間隔。清單每一列都要問「現在能不能按」，不節流就是每幀好幾次跨外掛呼叫。
    /// </summary>
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(200);

    private static readonly ICallGateSubscriber<uint, float, float, bool, bool> GoToMapPointIpc =
        Plugin.PluginInterface.GetIpcSubscriber<uint, float, float, bool, bool>("Lifestream.GoToMapPoint");

    private static readonly ICallGateSubscriber<bool> LifestreamIsBusyIpc =
        Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");

    private static readonly ICallGateSubscriber<bool> VnavPathIsRunningIpc =
        Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");

    private static DateTime _nextStatusAtUtc = DateTime.MinValue;
    private static NavStatus _status = new(false, null);

    /// <summary>最後一次按「前往」而且真的排出去的那一筆（FirebaseKey），用來在清單上標出目的地。</summary>
    public static string? NavigatingKey { get; private set; }

    /// <summary>
    /// 「現在能不能按前往」。
    /// </summary>
    /// <param name="LifestreamAvailable">Lifestream 有沒有裝而且載入。</param>
    /// <param name="Busy">
    /// <see langword="true"/>／<see langword="false"/> 是問到的答案；
    /// <see langword="null"/> 代表**問不到**（端點不存在／舊版）。
    /// 🔑 刻意分三態：把「不知道」摺成 false 會讓 UI 對使用者說謊。
    /// </param>
    public readonly record struct NavStatus(bool LifestreamAvailable, bool? Busy);

    /// <summary>取得（快取過的）狀態。UI 每幀呼叫，實際的 IPC 查詢有節流。</summary>
    public static NavStatus GetStatus()
    {
        var now = DateTime.UtcNow;
        if (now < _nextStatusAtUtc) return _status;
        _nextStatusAtUtc = now.Add(StatusInterval);

        if (!IsLifestreamLoaded())
        {
            NavigatingKey = null;
            _status = new NavStatus(false, null);
            return _status;
        }

        bool? busy = null;
        try
        {
            busy = LifestreamIsBusyIpc.InvokeFunc();
        }
        catch (Exception)
        {
            // 端點不存在（IpcNotReadyError）或舊版 Lifestream —— 維持「不知道」，不要假裝是 false。
        }

        if (busy != true)
        {
            try
            {
                if (VnavPathIsRunningIpc.InvokeFunc()) busy = true;
            }
            catch (Exception)
            {
                // 沒裝 vnavmesh。Lifestream 那邊本來就會拒絕出發，這裡不需要多做什麼。
            }
        }

        if (busy != true) NavigatingKey = null;

        _status = new NavStatus(true, busy);
        return _status;
    }

    private static bool IsLifestreamLoaded()
    {
        try
        {
            foreach (var plugin in Plugin.PluginInterface.InstalledPlugins)
            {
                if (plugin is { InternalName: LifestreamInternalName, IsLoaded: true })
                    return true;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[前往藏寶點] 查詢已安裝外掛失敗：{ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// 請 Lifestream 把角色送到這一筆藏寶點。
    /// </summary>
    /// <returns>
    /// <see langword="true"/> 代表 Lifestream 真的排了工作；
    /// <see langword="false"/> 代表**什麼都沒排**（座標換不出來、沒裝 Lifestream、
    /// 忙碌中、人不能動、該區沒有可用的乙太之光…），呼叫端不要等。
    /// </returns>
    public static bool TryGoTo(Treasure treasure)
    {
        if (!TryGetWorldPosition(treasure, out var territoryId, out var worldX, out var worldZ))
        {
            Plugin.Log.Information(
                $"[前往藏寶點] 算不出世界座標（mapId={treasure.MapId}），這一筆略過。");
            return false;
        }

        var fly = Plugin.Config.NavigateWithFlight;

        try
        {
            var accepted = GoToMapPointIpc.InvokeFunc(territoryId, worldX, worldZ, fly);

            Plugin.Log.Information(
                $"[前往藏寶點] {MapName(treasure)} ( {treasure.Coords.X:0.0} , {treasure.Coords.Y:0.0} ) "
                + $"→ territory {territoryId} 的世界座標 ({worldX:F1}, {worldZ:F1})，"
                + $"允許飛行={fly}，Lifestream 回應={accepted}。");

            NavigatingKey = accepted ? treasure.FirebaseKey : null;

            if (!accepted)
            {
                // 這是使用者剛按下去的按鈕，沒反應要說得出原因；Lifestream 自己也會在聊天欄補充。
                Plugin.Log.Information(
                    "[前往藏寶點] Lifestream 沒有排任何工作（可能正在忙、角色不能動、"
                    + "沒有安裝 vnavmesh，或該區域沒有已解鎖的乙太之光）。");
            }

            return accepted;
        }
        catch (Exception ex)
        {
            // 沒裝 Lifestream 或版本太舊沒有這個端點時走到這裡（IpcNotReadyError）。
            // 不崩、不做事；下一次 GetStatus() 會把按鈕變回灰的。
            Plugin.Log.Information(ex, "[前往藏寶點] 呼叫 Lifestream.GoToMapPoint 失敗（可能是 Lifestream 版本太舊）。");
            NavigatingKey = null;
            _nextStatusAtUtc = DateTime.MinValue;
            return false;
        }
    }

    private static string MapName(Treasure treasure) => Data.MapData.GetMapName(treasure.MapId);

    /// <summary>
    /// 把一筆藏寶圖的**地圖座標**換成 <c>Lifestream.GoToMapPoint</c> 要的
    /// <b>TerritoryType + 世界座標 X/Z</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Treasure.Coords 存的是地圖座標（介面上顯示的那個 22.1），不是世界座標。</b>
    /// 證據有三：<see cref="TreasureLocator.TryBuildMapLink"/> 把它直接丟給
    /// <c>MapLinkPayload(territory, map, x, y)</c>（那個建構子吃的就是可見座標）；
    /// <see cref="TreasureLocator.TryBuildAutoTranslateMapLink"/> 拿它去
    /// <see cref="PreMapLinkPayload.GenerateRawPosition"/> **產生** raw 位置；
    /// <see cref="MappyMarkerBridge"/> 也是原值推給 Mappy 的地圖座標 IPC。
    /// 所以這裡必須反解。
    /// </para>
    /// <para>
    /// 📌 <b>採用的公式（＝ Dalamud <c>MapLinkPayload.ConvertMapCoordinateToRawPosition</c>
    /// 的反函數，也就是本外掛「地圖」按鈕插旗用的那一條）</b>：
    /// <c>world = ((mapCoord - 1) * scale / 41 * 2048 - 1024) / scale - offset</c>，
    /// 其中 <c>scale = Map.SizeFactor / 100</c>、<c>offset</c> 是 Lumina 的
    /// <c>Map.OffsetX</c>／<c>OffsetY</c> <b>原值（不加負號）</b>。
    /// ⇒ 「前往」去的位置與「地圖」按鈕插的旗**同一點**。
    /// </para>
    /// <para>
    /// 🔴 <b>艦隊裡有三份互不相容的地圖座標公式</b>（TCToolbox/Core/MapCoords.cs 逐條記著）。
    /// 這裡刻意不用 Dalamud <c>MapUtil</c> 那一份：它把每格的邊長寫成 50，
    /// 精確值是 2048/41 ≈ 49.9512，兩者在地圖邊緣差約 2 個世界單位。
    /// 插旗差 2 碼看不出來，自動移動差 2 碼會停在錯的地方。
    /// </para>
    /// <para>
    /// 🔴 <b>offset 的正負號有兩套慣例</b>：Lumina <c>Map.OffsetX</c> 與
    /// <c>AgentMap.SelectedOffsetX</c> 符號相反，用錯的失敗形式是「靜靜地把人送到偏掉的地方」。
    /// 本函式吃 Lumina 那一套。
    /// </para>
    /// <para>
    /// ✅ <b>離線驗證（台服 7.20 EXD dump）</b>：
    /// ①反解 round-trip：map → world → map 的誤差 &lt; 4e-15 個地圖格（多張不同
    /// SizeFactor／offset 的圖：100/200/300/400/800、offset 0/-77/-448）。
    /// ②與正向式交叉：map 4「黑衣森林中央林區」的 (28.8, 22.7) 反解成世界
    /// (364.644, 59.941)，用 <c>MapLinkPayload</c> 的正向式回推誤差 0.0000；
    /// 用 <c>MapUtil</c> 的正向式回推是 (28.7729, 22.6788)，差 -0.027／-0.021 個地圖格
    /// ——那正是上面說的 50 vs 2048/41。
    /// ③offset 正負號：拿 <c>Level.csv</c> 裡 3596 筆位在「offset 非零」地圖上的真實座標，
    /// 用原值算有 99.555% 落在 1..42 的合法地圖範圍內，取負號只有 77.892%
    /// ——兩個方向可分辨，不是「都通過」。
    /// </para>
    /// <para>
    /// ⚠️ 換不出來時回 <see langword="false"/>，<b>不會給一個看起來很正常的 0</b>。
    /// </para>
    /// </remarks>
    public static bool TryGetWorldPosition(Treasure treasure, out uint territoryId, out float worldX, out float worldZ)
    {
        territoryId = 0;
        worldX = 0f;
        worldZ = 0f;

        if (treasure.MapId <= 0) return false;

        var coords = treasure.Coords;
        if (coords == null) return false;
        if (!float.IsFinite(coords.X) || !float.IsFinite(coords.Y)) return false;

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
            if (sheet == null) return false;

            var map = sheet.GetRowOrDefault((uint)treasure.MapId);
            if (map == null) return false;

            // SizeFactor 在公式裡當分母，0 會算出無限大。
            var sizeFactor = map.Value.SizeFactor;
            if (sizeFactor == 0) return false;

            territoryId = map.Value.TerritoryType.RowId;
            if (territoryId == 0) return false;

            worldX = MapCoordToWorld(coords.X, sizeFactor, map.Value.OffsetX);
            worldZ = MapCoordToWorld(coords.Y, sizeFactor, map.Value.OffsetY);

            if (!float.IsFinite(worldX) || !float.IsFinite(worldZ))
            {
                territoryId = 0;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[前往藏寶點] 查 Map 表換算座標失敗 mapId={treasure.MapId}：{ex.Message}");
            territoryId = 0;
            return false;
        }
    }

    /// <summary>地圖座標的單軸反解。公式與符號慣例見 <see cref="TryGetWorldPosition"/> 的說明。</summary>
    public static float MapCoordToWorld(float mapCoordinate, ushort sizeFactor, short offset)
    {
        var scale = sizeFactor / 100.0f;
        return (((mapCoordinate - 1.0f) * scale / 41.0f * 2048.0f) - 1024.0f) / scale - offset;
    }
}
