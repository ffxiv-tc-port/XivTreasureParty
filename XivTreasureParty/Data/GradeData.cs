using System.Collections.Generic;
using System.Linq;

namespace XivTreasureParty.Data;

public sealed class TreasureGrade
{
    public string Grade { get; init; } = "";
    public int ItemId { get; init; }
    public string Name { get; init; } = "";
    public int PartySize { get; init; }
    public string Expansion { get; init; } = "";
    public bool Special { get; init; }

    // 名稱已帶等級代號（例如「陳舊的地圖G1」）時只顯示名稱，免得變成「G1 陳舊的地圖G1」。
    public string Label => Name.Contains(Grade) ? Name : $"{Grade} {Name}";
}

public static class GradeData
{
    public static readonly IReadOnlyList<TreasureGrade> All = new List<TreasureGrade>
    {
        new() { Grade = "G17", ItemId = 43557, Name = "陳舊的獰豹革地圖", PartySize = 8, Expansion = "7.0" },
        new() { Grade = "G16", ItemId = 43556, Name = "陳舊的銀狼革地圖", PartySize = 1, Expansion = "7.0" },
        new() { Grade = "G15", ItemId = 39591, Name = "陳舊的蛇牛革地圖", PartySize = 8, Expansion = "6.3" },
        new() { Grade = "S4", ItemId = 39918, Name = "陳舊的地圖S4", PartySize = 8, Expansion = "6.3" },
        new() { Grade = "G14", ItemId = 36612, Name = "陳舊的金毗羅鱷革地圖", PartySize = 8, Expansion = "6.0" },
        new() { Grade = "G13", ItemId = 36611, Name = "陳舊的賽加羚羊革地圖", PartySize = 1, Expansion = "6.0" },
        new() { Grade = "S3", ItemId = 39593, Name = "陳舊的地圖S3", PartySize = 8, Expansion = "6.0" },
        new() { Grade = "G12", ItemId = 26745, Name = "陳舊的纏尾蛟革地圖", PartySize = 8, Expansion = "5.0" },
        new() { Grade = "G11", ItemId = 26744, Name = "陳舊的綠飄龍革地圖", PartySize = 1, Expansion = "5.0" },
        new() { Grade = "S2", ItemId = 33328, Name = "陳舊的地圖S2", PartySize = 8, Expansion = "5.0" },
        new() { Grade = "綠圖", ItemId = 19770, Name = "深層傳送魔紋的地圖", PartySize = 8, Expansion = "4.05", Special = true },
        new() { Grade = "G10", ItemId = 17836, Name = "陳舊的瞪羚革地圖", PartySize = 8, Expansion = "4.0" },
        new() { Grade = "G9", ItemId = 17835, Name = "陳舊的迦迦納怪鳥革地圖", PartySize = 1, Expansion = "4.0" },
        new() { Grade = "S1", ItemId = 24794, Name = "陳舊的地圖S1", PartySize = 8, Expansion = "4.0" },
        new() { Grade = "G8", ItemId = 12243, Name = "陳舊的巨龍革地圖", PartySize = 8, Expansion = "3.0" },
        new() { Grade = "G7", ItemId = 12242, Name = "陳舊的飛龍革地圖", PartySize = 1, Expansion = "3.0" },
        new() { Grade = "G6", ItemId = 12241, Name = "陳舊的古鳥革地圖", PartySize = 1, Expansion = "3.0" },
        new() { Grade = "G5", ItemId = 6692, Name = "陳舊的地圖G5", PartySize = 8, Expansion = "2.0" },
        new() { Grade = "G4", ItemId = 6691, Name = "陳舊的地圖G4", PartySize = 1, Expansion = "2.0" },
        new() { Grade = "G3", ItemId = 6690, Name = "陳舊的地圖G3", PartySize = 1, Expansion = "2.0" },
        new() { Grade = "G2", ItemId = 6689, Name = "陳舊的地圖G2", PartySize = 1, Expansion = "2.0" },
        new() { Grade = "G1", ItemId = 6688, Name = "陳舊的地圖G1", PartySize = 1, Expansion = "2.0" },
        new() { Grade = "索恩朝", ItemId = 9900, Name = "索恩朝時代的地圖", PartySize = 1, Expansion = "2.0" },
        new() { Grade = "隱藏", ItemId = 8156, Name = "鞣革製的隱藏地圖", PartySize = 1, Expansion = "2.0" },
        new() { Grade = "神秘", ItemId = 7884, Name = "神秘地圖", PartySize = 1, Expansion = "2.0" }
    };

    private static readonly Dictionary<int, TreasureGrade> ByItemId = All.ToDictionary(g => g.ItemId);

    public static TreasureGrade? GetByItemId(int itemId)
        => ByItemId.TryGetValue(itemId, out var g) ? g : null;

    public static string GetGradeLabel(int itemId)
    {
        var g = GetByItemId(itemId);
        return g == null ? $"#{itemId}" : g.Label;
    }
}
