using System;
using System.Collections.Generic;
using System.Text;

namespace XivTreasureParty.Game;

/// <summary>
/// 發送藏寶圖座標的訊息樣板。頻道指令另外放在最前面，不在樣板裡。
/// </summary>
/// <remarks>
/// 規則：值是空的佔位符會一併吃掉緊鄰的一個空白（先吃後面、沒有再吃前面），
/// 所以「{角色} {地圖}」在沒有角色時不會留下多的空白；預設樣板因此逐字等於舊版寫死的格式。
/// </remarks>
public static class ChatTemplate
{
    /// <summary>舊版寫死的完整格式（「(」「,」「)」的空白照舊，包含逗號前的兩個空白）。</summary>
    public const string Default = "{角色} {地圖} ( {X}  , {Y} ){座標連結}{水晶}";

    public const string Minimal = "{角色} {座標連結}";

    /// <summary>樣板本身的 UTF-8 位元組上限；整則訊息另受聊天 500 位元組上限。</summary>
    public const int MaxTemplateBytes = 300;

    /// <summary>遊戲聊天一次送出的位元組上限（與 <see cref="ChatSender"/> 的檢查一致：要小於這個數）。</summary>
    public const int MaxMessageBytes = 500;

    /// <summary>遊戲的旗標符號（私用區字元）；接收端靠「旗標＋地圖名 ( X , Y )」把純文字轉回可點連結。</summary>
    private static readonly string Flag = ((char)0xE0BB).ToString();

    public static readonly (string Token, string Help)[] Placeholders =
    {
        ("{角色}", "清單上這張圖的玩家（沒填就省略）"),
        ("{地圖}", "旗標符號＋地圖名稱"),
        ("{X}", "X 座標（一位小數）"),
        ("{Y}", "Y 座標（一位小數）"),
        ("{座標連結}", "可點的地圖連結；建不出來時樣板裡沒有 {地圖} 就改送「地圖 ( X  , Y )」文字"),
        ("{水晶}", "「 | 最近傳送水晶:[名稱]」整段（找不到就省略）"),
        ("{水晶名}", "最近傳送水晶的名稱（找不到就省略）"),
    };

    /// <summary>代入的值。<see cref="HasLink"/>＝建得出地圖連結。</summary>
    public readonly record struct Values(string Player, string MapName, float X, float Y, bool HasLink, string? CrystalName);

    /// <summary>樣板的一段：純文字，或地圖連結的位置。</summary>
    public readonly record struct Part(string Text, bool IsLink);

    /// <summary>去掉換行與控制字元、限制長度；空白樣板退回預設。</summary>
    public static string Sanitize(string? template)
    {
        var sb = new StringBuilder();
        foreach (var ch in template ?? string.Empty)
        {
            if (ch is '\r' or '\n' or '\t' or (char)0x2028 or (char)0x2029) { sb.Append(' '); continue; }
            if (char.IsControl(ch) || ch is >= (char)0xE000 and <= (char)0xF8FF) continue;
            sb.Append(ch);
        }
        var s = sb.ToString().Trim();
        while (Encoding.UTF8.GetByteCount(s) > MaxTemplateBytes)
            s = s[..^1];
        return s.Length == 0 ? Default : s;
    }

    public static List<Part> Render(string template, Values v)
    {
        var parts = new List<Part>();
        var text = new StringBuilder();
        var hasMapText = template.Contains("{地圖}", StringComparison.Ordinal);
        var i = 0;
        while (i < template.Length)
        {
            var token = MatchToken(template, i);
            if (token == null)
            {
                text.Append(template[i]);
                i++;
                continue;
            }

            i += token.Length;
            if (token == "{座標連結}" && v.HasLink)
            {
                Flush();
                parts.Add(new Part(string.Empty, true));
                continue;
            }

            var value = token switch
            {
                "{角色}" => v.Player,
                "{地圖}" => Flag + v.MapName,
                "{X}" => v.X.ToString("0.0"),
                "{Y}" => v.Y.ToString("0.0"),
                "{座標連結}" => hasMapText ? string.Empty : $"{Flag}{v.MapName} ( {v.X:0.0}  , {v.Y:0.0} )",
                "{水晶}" => string.IsNullOrEmpty(v.CrystalName) ? string.Empty : $" | 最近傳送水晶:[{v.CrystalName}]",
                "{水晶名}" => v.CrystalName ?? string.Empty,
                _ => token,
            };

            if (value.Length > 0)
            {
                text.Append(value);
            }
            else if (i < template.Length && template[i] == ' ')
            {
                i++;
            }
            else if (text.Length > 0 && text[^1] == ' ')
            {
                text.Length--;
            }
        }
        Flush();
        return parts;

        void Flush()
        {
            if (text.Length == 0) return;
            parts.Add(new Part(text.ToString(), false));
            text.Clear();
        }
    }

    /// <summary>給畫面預覽用：連結位置顯示成〔地圖連結〕。</summary>
    public static string Preview(string chatCommand, List<Part> parts)
    {
        var sb = new StringBuilder(chatCommand).Append(' ');
        foreach (var p in parts) sb.Append(p.IsLink ? "〔地圖連結〕" : p.Text);
        return sb.ToString();
    }

    private static string? MatchToken(string s, int i)
    {
        if (s[i] != '{') return null;
        foreach (var (token, _) in Placeholders)
            if (string.CompareOrdinal(s, i, token, 0, token.Length) == 0)
                return token;
        return null;
    }
}
