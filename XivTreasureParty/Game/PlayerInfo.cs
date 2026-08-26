using System;

namespace XivTreasureParty.Game;

public static class PlayerInfo
{
    /// <summary>
    /// 從遊戲讀取玩家自動暱稱 "角色名稱@伺服器名稱"。尚未登入或資料不可用時回 null。
    /// </summary>
    public static string? GetAutoNickname()
    {
        try
        {
            // API13：IClientState.LocalPlayer 已過時，改用 IObjectTable.LocalPlayer（純轉發）。
            var local = Plugin.ObjectTable.LocalPlayer;
            if (local == null) return null;
            var name = local.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name)) return null;

            string? world = null;
            try
            {
                var homeWorld = local.HomeWorld.Value;
                world = homeWorld.Name.ExtractText();
            }
            catch { /* sheet 查不到就略 */ }

            return string.IsNullOrWhiteSpace(world) ? name : $"{name}@{world}";
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[PlayerInfo] GetAutoNickname 例外: {ex.Message}");
            return null;
        }
    }
}
