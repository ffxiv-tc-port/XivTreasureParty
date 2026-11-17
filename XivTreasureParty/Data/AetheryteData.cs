using System;
using System.Collections.Generic;
using System.Linq;

namespace XivTreasureParty.Data;

public sealed record Aetheryte(string Name, float X, float Y);

/// <summary>
/// 各地圖主要傳送水晶座標 (繁中)。來自 xiv-tc-treasure-finder/js/data.js ZONE_AETHERYTES。
/// key = PlaceName ID (同 <see cref="MapData.PlaceNames"/> 的 key)
/// </summary>
public static class AetheryteData
{
    public static readonly IReadOnlyDictionary<int, IReadOnlyList<Aetheryte>> ByPlaceName = new Dictionary<int, IReadOnlyList<Aetheryte>>
    {
        // === 7.0 黃金遺產 ===
        [4505] = new Aetheryte[] {
            new("瓦丘恩佩洛", 28.1f, 13.1f),
            new("沃拉的迴響", 30.8f, 34.2f),
        },
        [4506] = new Aetheryte[] {
            new("哈努聚落", 18.1f, 11.9f),
            new("朋友的燈火", 32.3f, 25.6f),
            new("土陶郡", 11.9f, 27.7f),
            new("水果碼頭", 37.2f, 16.8f),
        },
        [4507] = new Aetheryte[] {
            new("紅豹村", 13.5f, 12.8f),
            new("瑪穆克", 35.9f, 32.0f),
        },
        [4508] = new Aetheryte[] {
            new("胡薩塔伊驛鎮", 29.1f, 30.8f),
            new("美花黑澤恩", 27.7f, 10.1f),
            new("謝申內青磷泉", 15.6f, 19.2f),
        },
        [4509] = new Aetheryte[] {
            new("亞斯拉尼站", 31.8f, 25.6f),
            new("雷轉質礦場", 17.1f, 23.9f),
            new("邊郊鎮", 17.0f, 9.8f),
        },
        [4510] = new Aetheryte[] {
            new("憶之節點", 21.5f, 37.5f),
            new("火之節點", 34.7f, 15.8f),
            new("風之節點", 16.4f, 13.6f),
        },

        // === 6.0 曉月終焉 ===
        [3708] = new Aetheryte[] {
            new("公堂保管院", 30.3f, 11.9f),
            new("小薩雷安", 21.6f, 20.5f),
            new("無路總部", 6.9f, 27.5f),
        },
        [3709] = new Aetheryte[] {
            new("新港", 25.4f, 34.0f),
            new("代米爾遺烈鄉", 11.0f, 22.2f),
            new("波洛伽護法村", 29.6f, 16.5f),
        },
        [3711] = new Aetheryte[] {
            new("淚灣", 10.1f, 34.5f),
            new("最佳威兔洞", 21.5f, 11.2f),
        },
        [3712] = new Aetheryte[] {
            new("半途終旅", 10.6f, 26.8f),
            new("異亞村落", 22.7f, 8.3f),
            new("奧密克戎基地", 31.3f, 28.1f),
        },
        [3713] = new Aetheryte[] {
            new("醒悟天測園", 24.6f, 24.0f),
            new("十二奇園", 8.8f, 32.3f),
            new("創作者之家", 10.9f, 17.0f),
        },

        // === 5.0 暗影乾漬 ===
        [2953] = new Aetheryte[] {
            new("喬布要塞", 36.6f, 20.9f),
            new("奧斯塔爾嚴命城", 6.8f, 16.9f),
        },
        [2954] = new Aetheryte[] {
            new("滯潮村", 34.8f, 27.3f),
            new("工匠村", 16.6f, 29.2f),
            new("特美拉村", 13.0f, 9.0f),
        },
        [2955] = new Aetheryte[] {
            new("鼴靈市集", 26.4f, 17.0f),
            new("上路客店", 29.5f, 27.6f),
            new("絡尾部落", 11.3f, 17.2f),
        },
        [2956] = new Aetheryte[] {
            new("群花館", 14.6f, 31.7f),
            new("普拉恩尼茸洞", 20.0f, 4.3f),
            new("雲村", 29.1f, 7.7f),
        },
        [2957] = new Aetheryte[] {
            new("蛇行枝", 19.4f, 27.4f),
            new("法諾村", 29.1f, 17.6f),
        },
        [2958] = new Aetheryte[] {
            new("鰭人潮池", 32.7f, 17.5f),
            new("馬克連薩斯廣場", 18.6f, 25.8f),
        },

        // === 4.0 紅蓮解放 ===
        [2406] = new Aetheryte[] {
            new("對等石", 29.8f, 26.4f),
            new("帝國東方堡", 8.9f, 11.3f),
        },
        [2407] = new Aetheryte[] {
            new("阿拉加納", 23.7f, 6.5f),
            new("阿拉基利", 16.1f, 36.4f),
        },
        [2408] = new Aetheryte[] {
            new("天營門", 8.5f, 21.1f),
            new("阿拉米格人居住區", 33.8f, 34.5f),
        },
        [2409] = new Aetheryte[] {
            new("碧玉水", 28.7f, 16.2f),
            new("自凝島", 23.2f, 9.8f),
        },
        [2410] = new Aetheryte[] {
            new("茨菰村", 30.1f, 19.7f),
            new("烈士庵", 26.4f, 13.4f),
        },
        [2411] = new Aetheryte[] {
            new("重逢市集", 32.6f, 28.3f),
            new("晨曦王座", 23.0f, 22.1f),
            new("朵洛衣樓", 6.4f, 23.8f),
        },

        // === 3.0 蒼天伊修加德 ===
        [2200] = new Aetheryte[] {
            new("隼巢", 32.0f, 36.7f),
        },
        [2000] = new Aetheryte[] {
            new("尾羽部落", 33.2f, 23.1f),
            new("不潔三塔", 16.5f, 23.2f),
        },
        [2002] = new Aetheryte[] {
            new("天極白堊宮", 10.8f, 28.8f),
            new("莫古利之家", 27.9f, 34.3f),
        },
        [2100] = new Aetheryte[] {
            new("尊杜部落", 10.5f, 14.2f),
            new("雲頂營地", 10.3f, 33.6f),
        },
    };

    /// <summary>
    /// 給定地點的所有傳送水晶裡，回傳與座標歐式距離最近的那個。
    /// </summary>
    public static Aetheryte? FindNearestByPlaceName(int placeNameId, float x, float y)
    {
        if (!ByPlaceName.TryGetValue(placeNameId, out var list) || list.Count == 0)
            return null;

        var nearest = list[0];
        var minDistSq = Distance2(nearest.X - x, nearest.Y - y);
        for (var i = 1; i < list.Count; i++)
        {
            var d = Distance2(list[i].X - x, list[i].Y - y);
            if (d < minDistSq)
            {
                minDistSq = d;
                nearest = list[i];
            }
        }
        return nearest;
    }

    public static Aetheryte? FindNearestByMapId(int mapId, float x, float y)
    {
        if (!MapData.MapToPlace.TryGetValue(mapId, out var placeId))
            return null;
        return FindNearestByPlaceName(placeId, x, y);
    }

    private static float Distance2(float dx, float dy) => dx * dx + dy * dy;
}
