using System;
using Dalamud.Configuration;

namespace XivTreasureParty;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string Nickname { get; set; } = string.Empty;

    public string? LastPartyCode { get; set; }

    public string? LastRefreshToken { get; set; }

    public string? LastIdToken { get; set; }

    public string? LastLocalId { get; set; }

    public DateTime? LastTokenRefreshedAtUtc { get; set; }

    public bool AutoRejoinOnStart { get; set; } = true;

    /// <summary>自動偵測玩家解碼藏寶圖時預選下方新增欄位（不會直接推送，需手動按加入清單）。</summary>
    public bool AutoCaptureOnDecode { get; set; } = true;

    /// <summary>偵測到玩家解碼新藏寶圖時，自動開啟對應地圖並在寶藏位置設定旗標。</summary>
    public bool AutoOpenMapOnCapture { get; set; } = false;

    /// <summary>
    /// 攔截聊天訊息把「🚩地圖名 ( X , Y )」純文字轉成可點擊地圖連結 (本插件接收端)。
    /// 等同 DailyRoutines AutoConvertMapLink 的功能；兩者並存沒問題（idempotent，只有第一個會作用）。
    /// </summary>
    public bool EnableMapLinkConversion { get; set; } = true;

    /// <summary>發送藏寶圖座標時使用的聊天頻道指令，例如 /p 或 /cwl1。</summary>
    public string TreasureChatCommand { get; set; } = "/p";

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
