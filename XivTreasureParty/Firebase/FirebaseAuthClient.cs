using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace XivTreasureParty.Firebase;

/// <summary>
/// 匿名登入與換證。憑證按後端分槽（<see cref="Configuration.GetCredentials"/>）：
/// 每次呼叫先看請求要去的端點，記憶體裡載的不是那一槽就換槽，
/// 所以某個後端的 refresh token 只會送到同一個後端的換證端點。
/// </summary>
public sealed class FirebaseAuthClient : IDisposable
{
    private readonly BackendSelector _backend;
    private readonly HttpClient _http = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);

    /// <summary>記憶體裡這組憑證屬於哪一槽（<see cref="BackendEndpoint.SlotId"/>）；null＝還沒載。</summary>
    private string? _loadedSlot;

    public string? IdToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public string? LocalId { get; private set; }
    public DateTime TokenExpiresAtUtc { get; private set; } = DateTime.MinValue;

    public bool IsSignedIn => !string.IsNullOrEmpty(IdToken) && !string.IsNullOrEmpty(LocalId);

    public FirebaseAuthClient(BackendSelector backend)
    {
        _backend = backend;
        _http.Timeout = TimeSpan.FromSeconds(30);
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

    public Task<string> EnsureSignedInAsync(CancellationToken ct = default) =>
        EnsureSignedInAsync(_backend.Current, ct);

    /// <summary>取得 <paramref name="endpoint"/> 那一槽的有效 ID token；呼叫端拿它去打同一個端點的資料庫。</summary>
    public async Task<string> EnsureSignedInAsync(BackendEndpoint endpoint, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loadedSlot != endpoint.SlotId)
                LoadSlot(endpoint);

            // 刻意不寫成 TokenExpiresAtUtc - TimeSpan.FromMinutes(2)：InvalidateToken() 會把 TokenExpiresAtUtc 設成 DateTime.MinValue，
            // 建構子在設定檔沒有 LastTokenRefreshedAtUtc 時也會給 MinValue；對 MinValue 做減法會擲 ArgumentOutOfRangeException。
            // 這是認證的第一道判斷，炸在這裡等於建立隊伍／加入隊伍／心跳／串流重連全部失效。
            // 加法形式語意等價（a < b - c ⟺ b > a + c），而 UtcNow + 2 分鐘不可能溢位。
            if (IsSignedIn && TokenExpiresAtUtc > DateTime.UtcNow + TimeSpan.FromMinutes(2))
                return IdToken!;

            if (!string.IsNullOrEmpty(RefreshToken))
            {
                try
                {
                    await RefreshAsyncInternal(endpoint, ct).ConfigureAwait(false);
                    return IdToken!;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"Token refresh 失敗，改走匿名登入：{ex.Message}");
                }
            }

            await SignInAnonymouslyAsyncInternal(endpoint, ct).ConfigureAwait(false);
            return IdToken!;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public Task<string> GetFreshTokenAsync(CancellationToken ct = default) => EnsureSignedInAsync(ct);

    private void LoadSlot(BackendEndpoint endpoint)
    {
        var (idToken, refreshToken, localId, refreshedAt) = Plugin.Config.GetCredentials(endpoint);
        if (!string.IsNullOrEmpty(idToken) && !string.IsNullOrEmpty(refreshToken))
        {
            IdToken = idToken;
            RefreshToken = refreshToken;
            LocalId = localId;
            TokenExpiresAtUtc = refreshedAt?.AddMinutes(55) ?? DateTime.MinValue;
        }
        else
        {
            IdToken = RefreshToken = LocalId = null;
            TokenExpiresAtUtc = DateTime.MinValue;
        }
        _loadedSlot = endpoint.SlotId;
    }

    private static string Label(BackendEndpoint endpoint) =>
        endpoint.Kind == BackendKind.Custom ? "臨時伺服器" : "Firebase";

    private async Task SignInAnonymouslyAsyncInternal(BackendEndpoint endpoint, CancellationToken ct)
    {
        var url = $"{endpoint.IdentityToolkitBase}/accounts:signUp?key={Uri.EscapeDataString(endpoint.ApiKey)}";
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

        PersistToConfig(endpoint);
        Plugin.Log.Info($"{Label(endpoint)} 匿名登入成功 uid={LocalId}");
    }

    private async Task RefreshAsyncInternal(BackendEndpoint endpoint, CancellationToken ct)
    {
        var url = $"{endpoint.SecureTokenBase}/token?key={Uri.EscapeDataString(endpoint.ApiKey)}";
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

        PersistToConfig(endpoint);
        Plugin.Log.Debug($"{Label(endpoint)} token 已刷新");
    }

    private void PersistToConfig(BackendEndpoint endpoint)
    {
        var cfg = Plugin.Config;
        cfg.SetCredentials(endpoint, IdToken, RefreshToken, LocalId, DateTime.UtcNow);
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
