using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace XivTreasureParty.Firebase;

public sealed class FirebaseAuthClient : IDisposable
{
    private readonly string _apiKey;
    private readonly HttpClient _http = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public string? IdToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public string? LocalId { get; private set; }
    public DateTime TokenExpiresAtUtc { get; private set; } = DateTime.MinValue;

    public bool IsSignedIn => !string.IsNullOrEmpty(IdToken) && !string.IsNullOrEmpty(LocalId);

    public FirebaseAuthClient(string apiKey)
    {
        _apiKey = apiKey;
        _http.Timeout = TimeSpan.FromSeconds(30);

        var cfg = Plugin.Config;
        if (!string.IsNullOrEmpty(cfg.LastIdToken) && !string.IsNullOrEmpty(cfg.LastRefreshToken))
        {
            IdToken = cfg.LastIdToken;
            RefreshToken = cfg.LastRefreshToken;
            LocalId = cfg.LastLocalId;
            TokenExpiresAtUtc = cfg.LastTokenRefreshedAtUtc?.AddMinutes(55) ?? DateTime.MinValue;
        }
    }

    /// <summary>
    /// 把快取的憑證標記為過期，讓下一次 EnsureSignedInAsync 一定重新取得。
    /// 伺服器回報 auth_revoked 時必須呼叫：本地記的到期時間只是「上次刷新時間 + 55 分」的估算，
    /// 伺服器已經不認這顆時，沿用同一顆重連只會無限重試同一個失敗。
    /// </summary>
    public void InvalidateToken()
    {
        // 只是一個 DateTime 欄位的整體指派，讀寫兩端都不會看到寫到一半的值。
        TokenExpiresAtUtc = DateTime.MinValue;
    }

    public async Task<string> EnsureSignedInAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 🔴 這裡刻意不寫成 TokenExpiresAtUtc - TimeSpan.FromMinutes(2)：
            //    InvalidateToken() 會把 TokenExpiresAtUtc 設成 DateTime.MinValue，而建構子在
            //    設定檔沒有 LastTokenRefreshedAtUtc 時也會給 MinValue；對 MinValue 做減法會直接擲
            //    ArgumentOutOfRangeException（un-representable DateTime）。
            //    這是認證的第一道判斷，炸在這裡等於建立隊伍／加入隊伍／心跳／串流重連全部失效。
            //    2026-09-08 實機踩到：v7.20.0.12 出貨後使用者完全無法建隊或加入。
            //    改用加法形式，語意完全等價（a < b - c  ⟺  b > a + c），而 UtcNow + 2 分鐘不可能溢位。
            if (IsSignedIn && TokenExpiresAtUtc > DateTime.UtcNow + TimeSpan.FromMinutes(2))
                return IdToken!;

            if (!string.IsNullOrEmpty(RefreshToken))
            {
                try
                {
                    await RefreshAsyncInternal(ct).ConfigureAwait(false);
                    return IdToken!;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"Token refresh 失敗，改走匿名登入：{ex.Message}");
                }
            }

            await SignInAnonymouslyAsyncInternal(ct).ConfigureAwait(false);
            return IdToken!;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<string> GetFreshTokenAsync(CancellationToken ct = default)
    {
        return await EnsureSignedInAsync(ct).ConfigureAwait(false);
    }

    private async Task SignInAnonymouslyAsyncInternal(CancellationToken ct)
    {
        var url = $"{FirebaseConfig.IdentityToolkitBase}/accounts:signUp?key={_apiKey}";
        using var resp = await _http.PostAsJsonAsync(url, new { returnSecureToken = true }, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"匿名登入失敗 ({(int)resp.StatusCode}): {raw}");

        var data = JsonSerializer.Deserialize<SignUpResponse>(raw)
                   ?? throw new Exception("匿名登入回應無法解析");

        IdToken = data.IdToken;
        RefreshToken = data.RefreshToken;
        LocalId = data.LocalId;
        if (int.TryParse(data.ExpiresIn, out var seconds))
            TokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(seconds);
        else
            TokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(55);

        PersistToConfig();
        Plugin.Log.Info($"Firebase 匿名登入成功 uid={LocalId}");
    }

    private async Task RefreshAsyncInternal(CancellationToken ct)
    {
        var url = $"{FirebaseConfig.SecureTokenBase}/token?key={_apiKey}";
        var form = new FormUrlEncodedContent(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, string>("grant_type", "refresh_token"),
            new System.Collections.Generic.KeyValuePair<string, string>("refresh_token", RefreshToken!)
        });
        using var resp = await _http.PostAsync(url, form, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"refresh 失敗 ({(int)resp.StatusCode}): {raw}");

        var data = JsonSerializer.Deserialize<RefreshResponse>(raw)
                   ?? throw new Exception("refresh 回應無法解析");

        IdToken = data.IdToken;
        RefreshToken = data.RefreshToken;
        LocalId = data.UserId;
        if (int.TryParse(data.ExpiresIn, out var seconds))
            TokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(seconds);
        else
            TokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(55);

        PersistToConfig();
        Plugin.Log.Debug("Firebase token 已刷新");
    }

    private void PersistToConfig()
    {
        var cfg = Plugin.Config;
        cfg.LastIdToken = IdToken;
        cfg.LastRefreshToken = RefreshToken;
        cfg.LastLocalId = LocalId;
        cfg.LastTokenRefreshedAtUtc = DateTime.UtcNow;
        cfg.Save();
    }

    public void Dispose()
    {
        _http.Dispose();
        _mutex.Dispose();
    }

    private sealed class SignUpResponse
    {
        [JsonPropertyName("idToken")] public string IdToken { get; set; } = "";
        [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("localId")] public string LocalId { get; set; } = "";
        [JsonPropertyName("expiresIn")] public string ExpiresIn { get; set; } = "3600";
    }

    private sealed class RefreshResponse
    {
        [JsonPropertyName("id_token")] public string IdToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
        [JsonPropertyName("expires_in")] public string ExpiresIn { get; set; } = "3600";
    }
}
