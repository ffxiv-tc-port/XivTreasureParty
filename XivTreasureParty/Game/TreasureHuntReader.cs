using System;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;

namespace XivTreasureParty.Game;

/// <summary>
/// 讀取玩家當前解碼的藏寶圖資料（rank + spot subrow）。
/// 用 SimpleMapTracker 的 signature，讀遊戲內建的 TreasureHuntManager 狀態。
/// 沒有 hook，只做 sig scan + 呼叫 getter。
/// </summary>
public sealed unsafe class TreasureHuntReader
{
    private delegate uint GetCurrentTreasureHuntRankDelegate(nint a1);
    private delegate ushort GetCurrentTreasureHuntSpotDelegate(nint a1);

    // 三條都明寫 Fallible：遊戲改版後對不上的那一條欄位留 null／0（建構子逐條印 Warning），
    // IsAvailable 為 false、讀取功能整項停用；不靠「擲例外再接住」。
    [Signature("E8 ?? ?? ?? ?? 0F B6 D0 45 0F B6 CE", ScanType = ScanType.Text, Fallibility = Fallibility.Fallible)]
    private readonly GetCurrentTreasureHuntRankDelegate? _getRank = null;

    [Signature("E8 ?? ?? ?? ?? 44 0F B7 C0 45 33 C9 0F B7 D3", ScanType = ScanType.Text, Fallibility = Fallibility.Fallible)]
    private readonly GetCurrentTreasureHuntSpotDelegate? _getSpot = null;

    [Signature("48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 75 E4", Offset = 3, ScanType = ScanType.StaticAddress, Fallibility = Fallibility.Fallible)]
    private readonly nint _treasureHuntManager = nint.Zero;

    public bool IsAvailable => _getRank != null && _getSpot != null && _treasureHuntManager != nint.Zero;

    public TreasureHuntReader(IGameInteropProvider interop)
    {
        try
        {
            interop.InitializeFromAttributes(this);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[TreasureHuntReader] signature 解析失敗（遊戲更新後可能需要更新 sig）: {ex.Message}");
        }

        const string lost = "「從遊戲讀取當前藏寶圖」與打開藏寶圖時的自動選取／自動開地圖停用";
        if (_getRank == null)
            Plugin.Log.Warning($"[TreasureHuntReader] 特徵碼對不上 GetCurrentTreasureHuntRank（遊戲改版後需要更新）：{lost}");
        if (_getSpot == null)
            Plugin.Log.Warning($"[TreasureHuntReader] 特徵碼對不上 GetCurrentTreasureHuntSpot（遊戲改版後需要更新）：{lost}");
        if (_treasureHuntManager == nint.Zero)
            Plugin.Log.Warning($"[TreasureHuntReader] 特徵碼對不上 TreasureHuntManager（遊戲改版後需要更新）：{lost}");
    }

    /// <summary>
    /// 讀取當前 TreasureHuntRank (對應 TreasureHuntRank sheet RowId)。0 表示沒有解碼的圖。
    /// </summary>
    /// <remarks>三條特徵碼缺任何一條都回 0、不呼叫遊戲函式：管理器位址是 0 時呼叫下去是攔不到的存取違規。</remarks>
    public uint GetCurrentRank() => IsAvailable ? _getRank!(_treasureHuntManager) : 0;

    /// <summary>
    /// 讀取當前 TreasureSpot subrow id。
    /// 完整查詢需要 (rank, spot) 兩個 key 進 TreasureSpot sheet。
    /// </summary>
    public ushort GetCurrentSpot() => IsAvailable ? _getSpot!(_treasureHuntManager) : (ushort)0;

    /// <summary>
    /// 一次讀 rank + spot，若無有效值回 null。
    /// </summary>
    public (uint Rank, ushort Spot)? Read()
    {
        if (!IsAvailable) return null;
        var rank = GetCurrentRank();
        var spot = GetCurrentSpot();
        if (rank == 0) return null;
        return (rank, spot);
    }

    /// <summary>
    /// 把 rank+spot 解析為我們的 Treasure 物件（gradeItemId / mapId / coords）。
    /// 查 TreasureHuntRank（拿 ItemName → gradeItemId）與 TreasureSpot（拿 Location.Map / X,Y）。
    /// 失敗回 null。
    /// </summary>
    public DecodedTreasure? Resolve(uint rank, ushort spot)
    {
        try
        {
            var rankSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TreasureHuntRank>();
            if (rankSheet == null) return null;
            var rankRow = rankSheet.GetRow(rank);
            var gradeItemId = (int)rankRow.ItemName.RowId;
            if (gradeItemId == 0) return null;

            var spotSheet = Plugin.DataManager.GetSubrowExcelSheet<Lumina.Excel.Sheets.TreasureSpot>();
            if (spotSheet == null) return null;

            Lumina.Excel.Sheets.TreasureSpot spotRow;
            try
            {
                spotRow = spotSheet.GetSubrow(rank, spot);
            }
            catch
            {
                return null;
            }

            var loc = spotRow.Location.Value;
            var map = loc.Map.Value;
            var mapId = (int)map.RowId;
            if (mapId == 0) return null;

            // TreasureSpot.Location 的 X/Z 是遊戲世界座標，要轉為地圖可見座標
            var size = map.SizeFactor;
            var x = ToMapCoordinate(loc.X, size);
            var y = ToMapCoordinate(loc.Z, size);

            return new DecodedTreasure(rank, spot, gradeItemId, mapId, x, y);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[TreasureHuntReader] Resolve 失敗 rank={rank} spot={spot}: {ex.Message}");
            return null;
        }
    }

    public DecodedTreasure? ReadAndResolve()
    {
        var r = Read();
        if (r == null) return null;
        return Resolve(r.Value.Rank, r.Value.Spot);
    }

    // 與 Globetrotter / AutoConvertMapLink 同款公式
    private static float ToMapCoordinate(float val, float sizeFactor)
    {
        var c = sizeFactor / 100f;
        val *= c;
        return 41f / c * ((val + 1024f) / 2048f) + 1;
    }
}

public sealed record DecodedTreasure(uint Rank, ushort Spot, int GradeItemId, int MapId, float X, float Y);
