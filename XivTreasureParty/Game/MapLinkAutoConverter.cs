using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;

namespace XivTreasureParty.Game;

/// <summary>
/// 接收端訊息解析 hook：把聊天訊息中 "🚩地圖名 ( X.X , Y.Y )" 形式的純文字
/// 改寫為 <see cref="PreMapLinkPayload"/>（AutoTranslateText chunk），讓座標
/// 變成可點擊地圖連結。
///
/// 為什麼必須 hook 接收端而不是發送端：
///   ProcessChatBoxEntry 對輸入 SeString 有 whitelist，自製 AutoTranslateText
///   chunk 會被靜默剝掉。但訊息「接收 / 解析」這條 path 對這類 chunk 無限制，
///   所以在 incoming message 改寫即可達到效果。
///
/// 演算法 port 自 DailyRoutines AutoConvertMapLink (KirisameVanilla / Asvel)。
/// </summary>
public sealed unsafe class MapLinkAutoConverter : IDisposable
{
    private delegate nint MessageParseDelegate(nint a, nint b);

    [Signature("E8 ?? ?? ?? ?? 48 8B D0 48 8D 4D D0 E8 ?? ?? ?? ?? 49 8B 07",
               DetourName = nameof(ParseMessageDetour))]
    private readonly Hook<MessageParseDelegate>? _hook = null;

    // 規格：必須有  (FFXIV 內建的旗標 PUA 字符) 開頭，
    // 例如 "🚩太陽神草原 ( 32.6  , 28.3 )"。注意座標前後都有空格、 , 前是雙空格。
    private static readonly Regex MapLinkRegex = new(
        @"(?<map>.+?)(?<instance>[-])? \( (?<x>\d{1,2}\.\d)  , (?<y>\d{1,2}\.\d) \)",
        RegexOptions.Compiled);

    /// <summary>地圖名 (繁中) → (territoryId, mapId) lazy cache。</summary>
    private static readonly Lazy<Dictionary<string, (uint TerritoryId, uint MapId)>> ZoneByName = new(() =>
    {
        var dict = new Dictionary<string, (uint, uint)>();
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            if (sheet == null) return dict;
            foreach (var zone in sheet)
            {
                try
                {
                    var name = zone.PlaceName.Value.Name.ExtractText();
                    if (string.IsNullOrEmpty(name)) continue;
                    dict.TryAdd(name, (zone.RowId, zone.Map.RowId));
                }
                catch { /* 個別 row 失敗略過 */ }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MapLinkAutoConverter] 建立 zone 索引失敗: {ex.Message}");
        }
        return dict;
    });

    public MapLinkAutoConverter(IGameInteropProvider interop)
    {
        try
        {
            interop.InitializeFromAttributes(this);
            _hook?.Enable();
            if (_hook == null)
                Plugin.Log.Warning("[MapLinkAutoConverter] sig 找不到，地圖連結轉換功能無法啟用");
            else
                Plugin.Log.Info("[MapLinkAutoConverter] 已啟用接收端地圖連結轉換");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MapLinkAutoConverter] 初始化失敗: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { _hook?.Disable(); } catch { }
        try { _hook?.Dispose(); } catch { }
    }

    private nint ParseMessageDetour(nint a, nint b)
    {
        var ret = _hook!.OriginalDisposeSafe(a, b);
        if (!Plugin.Config.EnableMapLinkConversion) return ret;

        try
        {
            var pMessage = Marshal.ReadIntPtr(ret);
            if (pMessage == IntPtr.Zero) return ret;

            // 計算 null-terminated 長度
            var length = 0;
            while (Marshal.ReadByte(pMessage, length) != 0) length++;
            if (length == 0) return ret;

            var raw = new byte[length];
            Marshal.Copy(pMessage, raw, 0, length);

            var parsed = SeString.Parse(raw);

            // 已有 [C9][04] 地圖連結 chunk，把同訊息中重複的純文字 "(map)( X , Y )" 吃掉
            // (我們送出端為了「chunk 被吞」的保險會雙塞文字 + chunk；對方有插件時要去重)
            var hasMapChunk = false;
            foreach (var p in parsed.Payloads)
            {
                if (p is AutoTranslatePayload at)
                {
                    var enc = at.Encode();
                    if (enc.Length >= 5 && enc[3] == 0xC9 && enc[4] == 0x04) { hasMapChunk = true; break; }
                }
            }
            if (hasMapChunk)
            {
                var dedupChanged = false;
                foreach (var p in parsed.Payloads)
                {
                    if (p is TextPayload tp && !string.IsNullOrEmpty(tp.Text))
                    {
                        var newText = MapLinkRegex.Replace(tp.Text, "");
                        if (newText != tp.Text)
                        {
                            tp.Text = newText;
                            dedupChanged = true;
                        }
                    }
                }
                if (dedupChanged)
                {
                    var newMessage = parsed.Encode();
                    var capacity = Marshal.ReadInt64(ret + 8);
                    if (newMessage.Length + 1 <= capacity)
                    {
                        Marshal.WriteInt64(ret + 16, newMessage.Length + 1);
                        Marshal.Copy(newMessage, 0, pMessage, newMessage.Length);
                        Marshal.WriteByte(pMessage, newMessage.Length, 0x00);
                    }
                }
                return ret;
            }

            for (var i = 0; i < parsed.Payloads.Count; i++)
            {
                if (parsed.Payloads[i] is not TextPayload tp) continue;
                if (string.IsNullOrEmpty(tp.Text)) continue;
                var match = MapLinkRegex.Match(tp.Text);
                if (!match.Success) continue;

                var mapName = match.Groups["map"].Value;
                if (!ZoneByName.Value.TryGetValue(mapName, out var ids)) continue;

                var territoryId = ids.TerritoryId;
                var mapId = ids.MapId;

                var mapSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
                if (mapSheet == null) return ret;
                var map = mapSheet.GetRow(mapId);

                if (!float.TryParse(match.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                    !float.TryParse(match.Groups["y"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                    continue;

                var rawX = PreMapLinkPayload.GenerateRawPosition(x, map.OffsetX, map.SizeFactor);
                var rawY = PreMapLinkPayload.GenerateRawPosition(y, map.OffsetY, map.SizeFactor);

                // 多副本標記 (~ 對應副本 1~9)
                if (match.Groups["instance"].Value.Length > 0)
                    mapId |= ((uint)match.Groups["instance"].Value[0] - 0xE0B0u) << 16;

                var newPayloads = new List<Payload>();
                if (match.Index > 0)
                    newPayloads.Add(new TextPayload(tp.Text[..match.Index]));
                newPayloads.Add(new PreMapLinkPayload(territoryId, mapId, rawX, rawY));
                if (match.Index + match.Length < tp.Text.Length)
                    newPayloads.Add(new TextPayload(tp.Text[(match.Index + match.Length)..]));

                parsed.Payloads.RemoveAt(i);
                parsed.Payloads.InsertRange(i, newPayloads);

                var newMessage = parsed.Encode();
                var capacity = Marshal.ReadInt64(ret + 8);
                if (newMessage.Length + 1 > capacity)
                {
                    Plugin.Log.Debug($"[MapLinkAutoConverter] 訊息容量 {capacity} 不足以容納 {newMessage.Length + 1} bytes");
                    return ret;
                }
                Marshal.WriteInt64(ret + 16, newMessage.Length + 1);
                Marshal.Copy(newMessage, 0, pMessage, newMessage.Length);
                Marshal.WriteByte(pMessage, newMessage.Length, 0x00);
                break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[MapLinkAutoConverter] 例外 (略過): {ex.Message}");
        }

        return ret;
    }
}
