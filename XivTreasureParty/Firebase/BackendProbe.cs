using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XivTreasureParty.Firebase;

/// <summary>「測試連線」的一行結果。</summary>
public sealed record ProbeLine(bool Ok, string Text);

/// <summary>
/// 手動按「測試連線」才跑：不帶任何憑證、不建帳號、不寫資料。
/// 依序問：自架服務的身分（<c>/.well-known/xtp</c>）、資料庫有沒有照 Firebase REST 回應、換證端點有沒有照 Firebase 回應。
/// </summary>
public static class BackendProbe
{
    /// <summary>插件看得懂的 <c>/.well-known/xtp</c> 協定版本上限。</summary>
    public const int SupportedProtocolVersion = 1;

    /// <summary>故意送的無效 refresh token：正常的換證端點必定回 400 INVALID_REFRESH_TOKEN。</summary>
    private const string BogusRefreshToken = "xtp-connection-test";

    public static async Task<(bool Ok, List<ProbeLine> Lines)> TestAsync(BackendEndpoint endpoint, CancellationToken ct = default)
    {
        var lines = new List<ProbeLine>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        lines.Add(await ProbeWellKnownAsync(http, endpoint, ct).ConfigureAwait(false));
        var db = await ProbeDatabaseAsync(http, endpoint, ct).ConfigureAwait(false);
        lines.Add(db);
        var auth = await ProbeAuthAsync(http, endpoint, ct).ConfigureAwait(false);
        lines.Add(auth);
        return (db.Ok && auth.Ok, lines);
    }

    private static async Task<ProbeLine> ProbeWellKnownAsync(HttpClient http, BackendEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(endpoint.DatabaseUrl + "/.well-known/xtp", ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new ProbeLine(true, "不是 XTP 自架服務（沒有 /.well-known/xtp），當成 Firebase 相容伺服器繼續測");

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("protocol", out var p) || p.GetString() != "xtp"
                || !root.TryGetProperty("version", out var v) || !v.TryGetInt32(out var version))
                return new ProbeLine(false, "/.well-known/xtp 的內容看不懂（不是 XTP 自架服務的格式）");

            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var max = root.TryGetProperty("maxMembers", out var m) && m.TryGetInt32(out var mm) ? $"，每隊上限 {mm} 人" : string.Empty;
            if (version > SupportedProtocolVersion)
                return new ProbeLine(false, $"XTP 自架服務「{name}」的協定版本 {version} 比插件新（插件支援到 {SupportedProtocolVersion}），請更新插件");
            return new ProbeLine(true, $"XTP 自架服務「{name}」（協定版本 {version}{max}）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ProbeLine(true, "沒有讀到 /.well-known/xtp（" + Short(ex.Message) + "），當成 Firebase 相容伺服器繼續測");
        }
    }

    private static async Task<ProbeLine> ProbeDatabaseAsync(HttpClient http, BackendEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(endpoint.DatabaseUrl + "/.json?shallow=true", ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            var isJson = IsJson(raw);
            if ((resp.IsSuccessStatusCode && isJson) || (code is 401 or 403 && isJson && raw.Contains("error", StringComparison.Ordinal)))
                return new ProbeLine(true, $"資料庫有照 Firebase 的格式回應（HTTP {code}）");
            return new ProbeLine(false, $"資料庫沒有照 Firebase 的格式回應：HTTP {code} {Short(raw)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ProbeLine(false, "連不上資料庫：" + Short(ex.Message));
        }
    }

    private static async Task<ProbeLine> ProbeAuthAsync(HttpClient http, BackendEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", BogusRefreshToken)
            });
            using var resp = await http.PostAsync($"{endpoint.SecureTokenBase}/token?key={Uri.EscapeDataString(endpoint.ApiKey)}", form, ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (raw.Contains("INVALID_REFRESH_TOKEN", StringComparison.Ordinal))
                return new ProbeLine(true, "驗證服務有照 Firebase 的格式回應");
            if (raw.Contains("API key not valid", StringComparison.OrdinalIgnoreCase) || raw.Contains("API_KEY_INVALID", StringComparison.Ordinal))
                return new ProbeLine(false, "驗證服務說 API key 不正確");
            return new ProbeLine(false, $"驗證服務沒有照 Firebase 的格式回應：HTTP {(int)resp.StatusCode} {Short(raw)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ProbeLine(false, "連不上驗證服務：" + Short(ex.Message));
        }
    }

    private static bool IsJson(string raw)
    {
        try { using var _ = JsonDocument.Parse(raw); return true; }
        catch (JsonException) { return false; }
    }

    private static string Short(string s) => StreamHttpException.Shorten(s, 120);
}
