using System;
using System.Text.RegularExpressions;

namespace XivTreasureParty.Firebase;

/// <summary>同步用的後端。數值會寫進設定檔，不可改號。</summary>
public enum BackendKind
{
    /// <summary>cycleapple 的 Firebase（網頁版共用）。</summary>
    Cycleapple = 0,

    /// <summary>使用者自填的臨時伺服器（自架服務或其他 Firebase 相容伺服器）。</summary>
    Custom = 1,
}

/// <summary>
/// 一組同步端點：資料庫、匿名登入、換證網址與 API key。不可變；切換後端＝換一顆新的。
/// </summary>
/// <remarks>
/// 驗證端點沿用 Firebase 模擬器的路徑排法（<c>{主機}/identitytoolkit.googleapis.com/v1</c>），
/// 這樣同一套插件碼對模擬器與自架服務走的是完全相同的網址。
/// </remarks>
public sealed class BackendEndpoint
{
    /// <summary>自訂後端沒填 API key 時送的佔位值：模擬器要求一定要有 key 參數，自架服務會忽略它。</summary>
    public const string PlaceholderApiKey = "xtp-selfhost";

    public const int MaxUrlLength = 200;

    public BackendKind Kind { get; }
    public string DatabaseUrl { get; }
    public string ApiKey { get; }
    public string IdentityToolkitBase { get; }
    public string SecureTokenBase { get; }

    /// <summary>給使用者看的主機名稱（cycleapple 固定字樣）。</summary>
    public string DisplayHost { get; }

    /// <summary>憑證分槽用的身分：資料庫、換證端點、API key 任一不同就是不同的槽。</summary>
    public string SlotId { get; }

    private BackendEndpoint(BackendKind kind, string databaseUrl, string apiKey,
                            string identityToolkitBase, string secureTokenBase, string displayHost)
    {
        Kind = kind;
        DatabaseUrl = databaseUrl;
        ApiKey = apiKey;
        IdentityToolkitBase = identityToolkitBase;
        SecureTokenBase = secureTokenBase;
        DisplayHost = displayHost;
        SlotId = kind == BackendKind.Cycleapple
            ? "cycleapple"
            : $"custom|{databaseUrl}|{secureTokenBase}|{apiKey}";
    }

    public static BackendEndpoint Cycleapple { get; } = new(
        BackendKind.Cycleapple,
        FirebaseConfig.DatabaseUrl,
        FirebaseConfig.ApiKey,
        FirebaseConfig.IdentityToolkitBase,
        FirebaseConfig.SecureTokenBase,
        "cycleapple");

    private const string GoogleIdentityToolkitBase = "https://identitytoolkit.googleapis.com/v1";
    private const string GoogleSecureTokenBase = "https://securetoken.googleapis.com/v1";

    /// <summary>
    /// 由使用者填的欄位組出自訂端點。驗證服務的來源依序是：
    /// 驗證伺服器網址（模擬器這類兩者分開的）→ 有 API key 時用 Google 官方（自開 Firebase）→ 與伺服器網址同一台（自架服務）。
    /// </summary>
    public static BackendEndpoint? TryCreateCustom(string? serverUrl, string? apiKey, string? authUrl, out string error)
    {
        if (!BackendUrl.TryNormalize(serverUrl, out var db, out error))
            return null;

        var key = (apiKey ?? string.Empty).Trim();
        if (key.Length > 100 || key.IndexOfAny(new[] { ' ', '\t', '&', '?', '#', '/' }) >= 0)
        {
            error = "API key 格式不正確";
            return null;
        }

        string idBase, tokenBase;
        if (!string.IsNullOrWhiteSpace(authUrl))
        {
            if (!BackendUrl.TryNormalize(authUrl, out var auth, out var authError))
            {
                error = "驗證伺服器網址：" + authError;
                return null;
            }
            idBase = auth + "/identitytoolkit.googleapis.com/v1";
            tokenBase = auth + "/securetoken.googleapis.com/v1";
        }
        else if (key.Length > 0)
        {
            idBase = GoogleIdentityToolkitBase;
            tokenBase = GoogleSecureTokenBase;
        }
        else
        {
            idBase = db + "/identitytoolkit.googleapis.com/v1";
            tokenBase = db + "/securetoken.googleapis.com/v1";
        }

        error = string.Empty;
        return new BackendEndpoint(BackendKind.Custom, db, key.Length > 0 ? key : PlaceholderApiKey,
                                   idBase, tokenBase, new Uri(db).Authority);
    }
}

/// <summary>臨時伺服器網址的驗證與正規化。</summary>
public static class BackendUrl
{
    private static readonly string CycleappleDbHost = new Uri(FirebaseConfig.DatabaseUrl).Host;

    /// <summary>
    /// 只收絕對網址；只准 https，唯一例外是 http 連 localhost／127.0.0.1（本機測試）。
    /// 一律拒絕帳密、query、fragment、空主機、超過 <see cref="BackendEndpoint.MaxUrlLength"/> 字、cycleapple 的資料庫主機；尾端斜線去掉。
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        var s = (input ?? string.Empty).Trim();
        if (s.Length == 0) { error = "網址是空白的"; return false; }
        if (s.Length > BackendEndpoint.MaxUrlLength) { error = $"網址太長（上限 {BackendEndpoint.MaxUrlLength} 字）"; return false; }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) { error = "不是完整的網址（要以 https:// 開頭）"; return false; }

        var loopback = uri.Host is "localhost" or "127.0.0.1";
        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            if (!loopback) { error = "只接受 https；http 只限本機測試（localhost／127.0.0.1）"; return false; }
        }
        else if (uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "只接受 https 網址";
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host)) { error = "網址沒有主機名稱"; return false; }
        if (!string.IsNullOrEmpty(uri.UserInfo)) { error = "網址不可以帶帳號密碼"; return false; }
        if (s.Contains('?') || s.Contains('#')) { error = "網址不可以帶 ? 或 # 之後的參數"; return false; }
        if (string.Equals(uri.Host, CycleappleDbHost, StringComparison.OrdinalIgnoreCase))
        {
            error = "這是 cycleapple 的伺服器，請直接選「cycleapple（預設）」";
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        error = string.Empty;
        return true;
    }
}

/// <summary>
/// 臨時伺服器邀請網址：<c>{伺服器網址}/#{隊伍代碼}</c>。同一個網址給網頁開、也給插件的「加入」框吃；
/// 代碼放 fragment，瀏覽器不會把它送進伺服器的存取紀錄。
/// </summary>
public static class PartyInvite
{
    public const string ChatPrefix = "XTP 臨時伺服器";

    private static readonly Regex UrlPattern = new(@"https?://[^\s]+", RegexOptions.CultureInvariant);

    public static string Build(string serverUrl, string partyCode) => $"{serverUrl}/#{partyCode}";

    /// <summary>含網址就當成邀請處理（不論格式對不對，錯了由 <see cref="TryParse"/> 說明）。</summary>
    public static bool LooksLikeInvite(string? text) => text != null && text.Contains("://", StringComparison.Ordinal);

    /// <summary>從一段文字（可以是整行聊天訊息）取出伺服器網址與隊伍代碼。代碼優先取 fragment，沒有再找文字裡的代碼。</summary>
    public static bool TryParse(string? text, int codeLength, out string serverUrl, out string partyCode, out string error)
    {
        serverUrl = partyCode = string.Empty;
        var s = text ?? string.Empty;
        var m = UrlPattern.Match(s);
        if (!m.Success) { error = "邀請裡找不到網址"; return false; }

        var url = m.Value;
        string? fragment = null;
        var hash = url.IndexOf('#');
        if (hash >= 0)
        {
            fragment = url[(hash + 1)..];
            url = url[..hash];
        }
        if (!BackendUrl.TryNormalize(url, out serverUrl, out var urlError)) { error = "邀請的網址不能用：" + urlError; return false; }

        var candidates = fragment != null
            ? new[] { fragment.Trim('/') }
            : s.Remove(m.Index, m.Length).Split(new[] { ' ', '\t', '\r', '\n', (char)0x3000, ':', (char)0xFF1A, (char)0xFF0C, ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in candidates)
        {
            var upper = token.ToUpperInvariant();
            if (Party.PartyCodeGenerator.IsValidFormat(upper, codeLength))
            {
                partyCode = upper;
                error = string.Empty;
                return true;
            }
        }

        error = $"邀請裡找不到 {codeLength} 碼的隊伍代碼";
        return false;
    }
}
