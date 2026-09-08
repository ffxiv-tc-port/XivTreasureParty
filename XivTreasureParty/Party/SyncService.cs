using System;
using System.Collections.Generic;
using System.Text.Json;
using XivTreasureParty.Firebase;
using XivTreasureParty.Party.Models;

namespace XivTreasureParty.Party;

/// <summary>
/// 對應網頁版 sync-service.js — 透過 Firebase SSE 同時監聽 members / treasures / meta。
/// 收到事件後解析 path 區分增刪改，更新本地 snapshot，最後觸發 UI 回調。
/// </summary>
public sealed class SyncService
{
    private readonly FirebaseStreamClient _stream;

    private FirebaseStreamClient.StreamSubscription? _membersSub;
    private FirebaseStreamClient.StreamSubscription? _treasuresSub;
    private FirebaseStreamClient.StreamSubscription? _metaSub;

    private string? _currentCode;

    private DateTime _lastManualReconnectUtc = DateTime.MinValue;

    public Dictionary<string, PartyMember> Members { get; } = new();
    public Dictionary<string, Treasure> Treasures { get; } = new();
    public PartyMeta? Meta { get; private set; }

    public event Action? MembersChanged;
    public event Action? TreasuresChanged;
    public event Action? MetaChanged;
    public event Action<Exception>? SyncError;

    public SyncService(FirebaseStreamClient stream)
    {
        _stream = stream;
    }

    public void Start(string partyCode)
    {
        Stop();
        _currentCode = partyCode;

        _membersSub = _stream.Subscribe(
            $"parties/{partyCode}/members",
            ev => RunSafeOnFrameworkThread(() => ApplyPut(Members, ev, ApplyMemberValue), "members"),
            ex => SyncError?.Invoke(ex));

        _treasuresSub = _stream.Subscribe(
            $"parties/{partyCode}/treasures",
            ev => RunSafeOnFrameworkThread(() => ApplyPut(Treasures, ev, ApplyTreasureValue), "treasures"),
            ex => SyncError?.Invoke(ex));

        _metaSub = _stream.Subscribe(
            $"parties/{partyCode}/meta",
            ev => RunSafeOnFrameworkThread(() => HandleMeta(ev), "meta"),
            ex => SyncError?.Invoke(ex));
    }

    private static void RunSafeOnFrameworkThread(Action action, string scope)
    {
        // 包一層 try/catch + 觀察 Task exception，避免 unobserved exception 終結整個 SSE callback。
        Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try { action(); }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, $"[Sync:{scope}] 處理事件時例外");
            }
        }).ContinueWith(t =>
        {
            if (t.Exception != null)
                Plugin.Log.Error(t.Exception, $"[Sync:{scope}] framework task 例外");
        }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Stop()
    {
        _membersSub?.Dispose(); _membersSub = null;
        _treasuresSub?.Dispose(); _treasuresSub = null;
        _metaSub?.Dispose(); _metaSub = null;
        Members.Clear();
        Treasures.Clear();
        Meta = null;
        _currentCode = null;
    }

    /// <summary>目前訂閱中的隊伍代碼（沒訂閱時為 null）。</summary>
    public string? CurrentCode => _currentCode;

    /// <summary>
    /// 把三條串流的狀態聚合成一列給 UI 用。在繪製執行緒呼叫；
    /// 每條串流的狀態都是背景執行緒整體指派的不可變快照，讀到的一定是完整的一份。
    /// </summary>
    public SyncConnectionInfo GetConnectionInfo()
    {
        var subs = new[] { _membersSub, _treasuresSub, _metaSub };
        var info = new SyncConnectionInfo { Total = 0, Connected = 0, State = StreamConnectionState.Idle };

        var anyStopped = false;
        var anyReconnecting = false;
        var anyConnecting = false;

        foreach (var sub in subs)
        {
            if (sub == null) continue;
            var st = sub.Status;
            info.Total++;

            switch (st.State)
            {
                case StreamConnectionState.Connected: info.Connected++; break;
                case StreamConnectionState.Reconnecting: anyReconnecting = true; break;
                case StreamConnectionState.Connecting: anyConnecting = true; break;
                case StreamConnectionState.Stopped: anyStopped = true; break;
            }

            if (st.FailedAttempts > info.FailedAttempts) info.FailedAttempts = st.FailedAttempts;

            if (st.NextRetryUtc is { } next && (info.NextRetryUtc == null || next < info.NextRetryUtc))
                info.NextRetryUtc = next;

            if (st.LastEventUtc is { } le && (info.LastEventUtc == null || le > info.LastEventUtc))
                info.LastEventUtc = le;

            if (info.LastError == null && st.State != StreamConnectionState.Connected)
                info.LastError = st.LastError;
        }

        if (info.Total == 0)
            info.State = StreamConnectionState.Idle;
        else if (anyStopped)
            info.State = StreamConnectionState.Stopped;
        else if (anyReconnecting)
            info.State = StreamConnectionState.Reconnecting;
        else if (info.Connected == info.Total)
            info.State = StreamConnectionState.Connected;
        else if (anyConnecting)
            info.State = StreamConnectionState.Connecting;
        else
            info.State = StreamConnectionState.Idle;

        return info;
    }

    /// <summary>
    /// 手動重新連線：三條串流各插隊一次立刻重試（不等退避）。
    /// 訂閱整個不見時（例如重連迴圈曾經停掉、或啟動時沒接上）則重新建立訂閱。
    /// 自帶 3 秒節流，避免使用者連點變成每幀重試。
    /// </summary>
    public bool ReconnectNow()
    {
        var now = DateTime.UtcNow;
        if (now - _lastManualReconnectUtc < TimeSpan.FromSeconds(3)) return false;
        _lastManualReconnectUtc = now;

        var code = _currentCode ?? Plugin.PartyService.CurrentPartyCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            Plugin.Log.Information("[Sync] 手動重新連線：目前不在任何隊伍中，沒有東西可以連。");
            return false;
        }

        if (_membersSub == null || _treasuresSub == null || _metaSub == null)
        {
            Plugin.Log.Information($"[Sync] 手動重新連線：訂閱不存在，重新建立三條串流（隊伍 {code}）。");
            Start(code!);
            return true;
        }

        Plugin.Log.Information($"[Sync] 手動重新連線：要求三條串流立刻重試（隊伍 {code}）。");
        _membersSub.RequestImmediateRetry();
        _treasuresSub.RequestImmediateRetry();
        _metaSub.RequestImmediateRetry();
        return true;
    }

    /// <summary>
    /// 給 PartyService 在 push 成功後立刻把新藏寶圖補進本地 dict 用，
    /// 避免 SSE 延遲或在 NAT/proxy 後靜默斷線時 UI 看不到剛新增的圖。
    /// SSE 之後若送回同一筆會直接覆寫，不會產生重複。
    /// </summary>
    public void UpsertTreasureLocal(string key, Treasure t)
    {
        Plugin.Framework.RunOnFrameworkThread(() =>
        {
            t.FirebaseKey = key;
            Treasures[key] = t;
            TreasuresChanged?.Invoke();
        });
    }

    /// <summary>同上，刪除時也補一個本地刪除（remove 在 SSE 醒來前不會出現）。</summary>
    public void RemoveTreasureLocal(string key)
    {
        Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Treasures.Remove(key))
                TreasuresChanged?.Invoke();
        });
    }

    /// <summary>給 PartyService 在批次本機更新 order 後通知 UI 重畫。</summary>
    public void NotifyTreasuresChanged()
    {
        Plugin.Framework.RunOnFrameworkThread(() => TreasuresChanged?.Invoke());
    }

    private void ApplyPut<T>(Dictionary<string, T> target, StreamEvent ev,
                             Action<Dictionary<string, T>, string, JsonElement> applyValue) where T : class
    {
        var path = ev.Path;
        using var doc = JsonDocument.Parse(ev.DataJson);
        var root = doc.RootElement;

        if (path == "/")
        {
            // PUT 於 "/" = 全量替換（初次訂閱 snapshot 或整路徑覆寫）
            // PATCH 於 "/" = 多路徑部分更新：data 形如 {"key1/field": v1, "key2/field": v2}
            //   或 {"key3": {...}}。不應清空原有 dict。
            if (ev.Type == StreamEventType.Put)
                target.Clear();

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name.StartsWith('.')) continue;
                    DispatchKeyedUpdate(target, prop.Name, prop.Value, applyValue);
                }
            }
            else if (root.ValueKind == JsonValueKind.Array && ev.Type == StreamEventType.Put)
            {
                var i = 0;
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Null)
                        applyValue(target, i.ToString(), item);
                    i++;
                }
            }
        }
        else
        {
            var segments = path.Trim('/').Split('/');
            if (segments.Length == 0) return;
            var id = segments[0];
            if (segments.Length == 1)
            {
                if (root.ValueKind == JsonValueKind.Null)
                    target.Remove(id);
                else
                    applyValue(target, id, root);
            }
            else
            {
                // 子節點更新 (e.g. /key1/order) — 簡化處理：從 DB 已儲存的記錄直接更新該欄位
                if (!target.TryGetValue(id, out _))
                    return;
                // 讓呼叫者遇到子欄位變更時保守地從 Firebase 重讀該記錄；為了低延遲這裡依欄位名 patch
                var fieldPath = string.Join('/', segments[1..]);
                ApplyFieldPatch(target, id, fieldPath, root);
            }
        }

        if (typeof(T) == typeof(PartyMember)) MembersChanged?.Invoke();
        else if (typeof(T) == typeof(Treasure)) TreasuresChanged?.Invoke();
    }

    /// <summary>
    /// 給 path="/" 底下的一個 property：
    ///   "key1" → 整筆覆寫 (或 null 刪除)
    ///   "key1/sub/field" → 單欄位 patch（來自 Firebase multi-path update）
    /// </summary>
    private static void DispatchKeyedUpdate<T>(Dictionary<string, T> target, string keyOrPath, JsonElement value,
                                               Action<Dictionary<string, T>, string, JsonElement> applyValue) where T : class
    {
        var slashIdx = keyOrPath.IndexOf('/');
        if (slashIdx < 0)
        {
            if (value.ValueKind == JsonValueKind.Null)
                target.Remove(keyOrPath);
            else
                applyValue(target, keyOrPath, value);
        }
        else
        {
            var id = keyOrPath[..slashIdx];
            var fieldPath = keyOrPath[(slashIdx + 1)..];
            if (!target.TryGetValue(id, out _)) return;
            ApplyFieldPatch(target, id, fieldPath, value);
        }
    }

    private static void ApplyMemberValue(Dictionary<string, PartyMember> target, string key, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) { target.Remove(key); return; }
        if (value.ValueKind != JsonValueKind.Object)
        {
            Plugin.Log.Debug($"[Sync] Skipping non-object member value at key={key}, kind={value.ValueKind}");
            return;
        }
        try
        {
            var member = JsonSerializer.Deserialize<PartyMember>(value.GetRawText(), FirebaseDatabaseClient.JsonOptions);
            if (member != null) target[key] = member;
        }
        catch (JsonException ex)
        {
            Plugin.Log.Warning($"[Sync] 無法解析成員 {key}: {ex.Message}");
        }
    }

    private static void ApplyTreasureValue(Dictionary<string, Treasure> target, string key, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) { target.Remove(key); return; }
        if (value.ValueKind != JsonValueKind.Object)
        {
            // 例如 Firebase 的 .priority 標記或孤立的非物件鍵，直接略過
            Plugin.Log.Debug($"[Sync] Skipping non-object treasure value at key={key}, kind={value.ValueKind}");
            return;
        }
        try
        {
            var treasure = JsonSerializer.Deserialize<Treasure>(value.GetRawText(), FirebaseDatabaseClient.JsonOptions);
            if (treasure == null) return;
            treasure.FirebaseKey = key;
            target[key] = treasure;
        }
        catch (JsonException ex)
        {
            Plugin.Log.Warning($"[Sync] 無法解析藏寶圖 {key}: {ex.Message} raw={value.GetRawText()}");
        }
    }

    private static void ApplyFieldPatch<T>(Dictionary<string, T> target, string id, string fieldPath, JsonElement value) where T : class
    {
        if (!target.TryGetValue(id, out var item)) return;
        // 針對我們關心的幾個欄位做直接賦值，其餘忽略 (容錯)
        if (item is Treasure t)
        {
            switch (fieldPath)
            {
                case "completed": t.Completed = value.ValueKind == JsonValueKind.True; break;
                case "order": if (value.TryGetInt32(out var o)) t.Order = o; break;
                case "note": t.Note = value.ValueKind == JsonValueKind.Null ? null : value.GetString(); break;
                case "player": t.Player = value.ValueKind == JsonValueKind.Null ? null : value.GetString(); break;
            }
        }
        else if (item is PartyMember m)
        {
            switch (fieldPath)
            {
                case "nickname": m.Nickname = value.GetString() ?? ""; break;
                case "isLeader": m.IsLeader = value.ValueKind == JsonValueKind.True; break;
                case "lastSeen":
                    m.LastSeen = value.ValueKind switch
                    {
                        JsonValueKind.Number => value.GetInt64(),
                        _ => null
                    };
                    break;
            }
        }
    }

    private void HandleMeta(StreamEvent ev)
    {
        using var doc = JsonDocument.Parse(ev.DataJson);
        var root = doc.RootElement;
        var path = ev.Path;

        if (path == "/")
        {
            Meta = root.ValueKind == JsonValueKind.Null
                ? null
                : JsonSerializer.Deserialize<PartyMeta>(root.GetRawText(), FirebaseDatabaseClient.JsonOptions);
        }
        else
        {
            Meta ??= new PartyMeta();
            var field = path.Trim('/');
            switch (field)
            {
                case "expiresAt":
                    if (root.ValueKind == JsonValueKind.Number) Meta.ExpiresAt = root.GetInt64();
                    else if (root.ValueKind == JsonValueKind.Null) Meta.ExpiresAt = null;
                    break;
                case "orderLocked":
                    Meta.OrderLocked = root.ValueKind == JsonValueKind.True;
                    break;
                case "createdBy":
                    Meta.CreatedBy = root.ValueKind == JsonValueKind.Null ? null : root.GetString();
                    break;
            }
        }

        // 同步更新 PartyService 狀態
        Plugin.PartyService.SetExpiresAt(Meta?.ExpiresAt);
        Plugin.PartyService.SetOrderLocked(Meta?.OrderLocked ?? false);
        MetaChanged?.Invoke();
    }
}

/// <summary>三條串流聚合後的連線狀態，只給 UI 顯示用。</summary>
public sealed class SyncConnectionInfo
{
    public StreamConnectionState State { get; set; } = StreamConnectionState.Idle;

    /// <summary>三條串流裡最高的連續失敗次數。</summary>
    public int FailedAttempts { get; set; }

    /// <summary>最快的一條下次重試的時刻。</summary>
    public DateTime? NextRetryUtc { get; set; }

    /// <summary>最後一次收到資料的時刻。null＝從來沒收過（畫面上要顯示成「?」而不是 0）。</summary>
    public DateTime? LastEventUtc { get; set; }

    public string? LastError { get; set; }

    /// <summary>目前有幾條串流。</summary>
    public int Total { get; set; }

    /// <summary>其中連上的有幾條。</summary>
    public int Connected { get; set; }
}
