using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace XivTreasureParty.Firebase;

public sealed class FirebaseDatabaseClient
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly BackendSelector _backend;
    private readonly FirebaseAuthClient _auth;
    private readonly HttpClient _http = new();

    public FirebaseDatabaseClient(BackendSelector backend, FirebaseAuthClient auth)
    {
        _backend = backend;
        _auth = auth;
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    /// <summary>網址與 token 取自同一個端點快照：token 只會送到簽發它的那個後端。</summary>
    private async Task<string> BuildUrlAsync(string path, CancellationToken ct)
    {
        var endpoint = _backend.Current;
        var token = await _auth.EnsureSignedInAsync(endpoint, ct).ConfigureAwait(false);
        return $"{endpoint.DatabaseUrl}/{path.TrimStart('/')}.json?auth={Uri.EscapeDataString(token)}";
    }

    public async Task<T?> GetAsync<T>(string path, CancellationToken ct = default)
    {
        var url = await BuildUrlAsync(path, ct).ConfigureAwait(false);
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new FirebaseException($"GET {path} 失敗 ({(int)resp.StatusCode}): {raw}");
        if (string.IsNullOrWhiteSpace(raw) || raw == "null") return default;
        return JsonSerializer.Deserialize<T>(raw, JsonOptions);
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var url = await BuildUrlAsync(path, ct).ConfigureAwait(false) + "&shallow=true";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new FirebaseException($"exists {path} 失敗 ({(int)resp.StatusCode}): {raw}");
        return !string.IsNullOrWhiteSpace(raw) && raw != "null";
    }

    public async Task SetAsync<T>(string path, T value, CancellationToken ct = default)
    {
        var url = await BuildUrlAsync(path, ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(value, JsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new FirebaseException($"PUT {path} 失敗 ({(int)resp.StatusCode}): {raw}");
        }
    }

    public async Task UpdateAsync(string path, IDictionary<string, object?> updates, CancellationToken ct = default)
    {
        var url = await BuildUrlAsync(path, ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(updates, JsonOptions);
        using var req = new HttpRequestMessage(new HttpMethod("PATCH"), url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new FirebaseException($"PATCH {path} 失敗 ({(int)resp.StatusCode}): {raw}");
        }
    }

    public async Task<string> PushAsync<T>(string path, T value, CancellationToken ct = default)
    {
        var url = await BuildUrlAsync(path, ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(value, JsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new FirebaseException($"POST {path} 失敗 ({(int)resp.StatusCode}): {raw}");
        var doc = JsonSerializer.Deserialize<PushResponse>(raw)
                  ?? throw new FirebaseException("push 回應無法解析");
        return doc.Name;
    }

    public async Task RemoveAsync(string path, CancellationToken ct = default)
    {
        var url = await BuildUrlAsync(path, ct).ConfigureAwait(false);
        using var resp = await _http.DeleteAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new FirebaseException($"DELETE {path} 失敗 ({(int)resp.StatusCode}): {raw}");
        }
    }

    private sealed class PushResponse
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }
}

public class FirebaseException : Exception
{
    public FirebaseException(string message) : base(message) { }
}

/// <summary>
/// 對應網頁版 serverTimestamp() — 寫入時傳 {".sv":"timestamp"}，Firebase 伺服器會替換為實際時間 (ms)。
/// 為避免 STJ 對 object? 欄位的多型序列化不穩定，直接以靜態 Dictionary 作為寫入值。
/// 讀取時 Firebase 回傳的是 long (ms)。
/// </summary>
public static class ServerTimestamp
{
    public static readonly IReadOnlyDictionary<string, string> Instance =
        new Dictionary<string, string> { [".sv"] = "timestamp" };
}

/// <summary>
/// 保留相容性 (若未來改用強型別)，現階段不主動掛載到 JsonOptions。
/// </summary>
public sealed class ServerTimestampConverter : JsonConverter<IReadOnlyDictionary<string, string>>
{
    public override IReadOnlyDictionary<string, string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in value) writer.WriteString(k, v);
        writer.WriteEndObject();
    }
}
