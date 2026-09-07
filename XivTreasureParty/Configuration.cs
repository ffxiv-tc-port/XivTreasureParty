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

    /// <summary>
    /// 把隊伍清單裡「全部」尚未完成的藏寶點畫到 Mappy 的地圖上（透過 Mappy 的標記 IPC）。
    /// 沒裝 Mappy 時這個開關不做任何事；原本的「地圖」按鈕（開圖打旗標）不受影響。
    /// </summary>
    public bool ShowTreasuresOnMappy { get; set; } = true;

    /// <summary>
    /// 按「前往」時允許 Lifestream 用飛行坐騎跑最後一段。
    /// 區域不可飛、或還沒解鎖飛行時，Lifestream 會自動退回地面路線，不會因此失敗。
    /// </summary>
    public bool NavigateWithFlight { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
