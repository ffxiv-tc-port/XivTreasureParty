using System;
using Dalamud.Configuration;
using XivTreasureParty.Firebase;

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

    /// <summary>
    /// 插件載入時若記得上次的隊伍代碼，自動登入並重新加入。關閉時只有使用者按「建立新隊伍」或「加入」才會連線。
    /// </summary>
    public bool AutoRejoinOnStart { get; set; } = false;

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

    /// <summary>
    /// 同步合併成一條串流（訂閱整個隊伍節點）；伺服器不准讀整個隊伍時自動改回成員／藏寶圖／隊伍設定三條。
    /// 關掉＝一律三條（舊行為）。下次建立／加入隊伍才生效。
    /// </summary>
    public bool SingleStreamSync { get; set; } = true;

    // ── 臨時伺服器 ──
    // 契約：上面的 LastPartyCode／Last*Token／LastLocalId／Nickname 就是 cycleapple 那一槽（既有使用者不搬資料）；
    // 自訂後端只讀寫 Custom* 欄位，而且 CustomCredentialOwner 對不上目前端點時整組視為沒有。

    public BackendKind Backend { get; set; } = BackendKind.Cycleapple;

    public string CustomServerUrl { get; set; } = string.Empty;

    public string CustomApiKey { get; set; } = string.Empty;

    /// <summary>驗證服務與資料庫不在同一台時才填（例如 Firebase 模擬器）。</summary>
    public string CustomAuthUrl { get; set; } = string.Empty;

    public string? CustomLastPartyCode { get; set; }

    /// <summary>自訂後端的暱稱；預設空白＝不帶角色名，用「玩家xxxx」。</summary>
    public string CustomNickname { get; set; } = string.Empty;

    public string? CustomIdToken { get; set; }

    public string? CustomRefreshToken { get; set; }

    public string? CustomLocalId { get; set; }

    public DateTime? CustomTokenRefreshedAtUtc { get; set; }

    /// <summary>Custom* 憑證屬於哪一個端點（<see cref="BackendEndpoint.SlotId"/>）。</summary>
    public string? CustomCredentialOwner { get; set; }

    public string? GetLastPartyCode(BackendKind kind) => kind == BackendKind.Custom ? CustomLastPartyCode : LastPartyCode;

    public void SetLastPartyCode(BackendKind kind, string? code)
    {
        if (kind == BackendKind.Custom) CustomLastPartyCode = code;
        else LastPartyCode = code;
    }

    public string GetNickname(BackendKind kind) => kind == BackendKind.Custom ? CustomNickname : Nickname;

    public void SetNickname(BackendKind kind, string nickname)
    {
        if (kind == BackendKind.Custom) CustomNickname = nickname;
        else Nickname = nickname;
    }

    /// <summary>讀出 <paramref name="endpoint"/> 那一槽的憑證；槽的主人對不上時回全空。</summary>
    public (string? IdToken, string? RefreshToken, string? LocalId, DateTime? RefreshedAtUtc) GetCredentials(BackendEndpoint endpoint)
    {
        if (endpoint.Kind == BackendKind.Cycleapple)
            return (LastIdToken, LastRefreshToken, LastLocalId, LastTokenRefreshedAtUtc);
        if (CustomCredentialOwner != endpoint.SlotId)
            return (null, null, null, null);
        return (CustomIdToken, CustomRefreshToken, CustomLocalId, CustomTokenRefreshedAtUtc);
    }

    public void SetCredentials(BackendEndpoint endpoint, string? idToken, string? refreshToken, string? localId, DateTime refreshedAtUtc)
    {
        if (endpoint.Kind == BackendKind.Cycleapple)
        {
            LastIdToken = idToken;
            LastRefreshToken = refreshToken;
            LastLocalId = localId;
            LastTokenRefreshedAtUtc = refreshedAtUtc;
            return;
        }
        CustomCredentialOwner = endpoint.SlotId;
        CustomIdToken = idToken;
        CustomRefreshToken = refreshToken;
        CustomLocalId = localId;
        CustomTokenRefreshedAtUtc = refreshedAtUtc;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
