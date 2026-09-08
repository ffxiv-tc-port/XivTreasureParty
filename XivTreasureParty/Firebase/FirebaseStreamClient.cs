using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XivTreasureParty.Firebase;

/// <summary>單一 SSE 訂閱目前的連線狀態。</summary>
public enum StreamConnectionState
{
    /// <summary>尚未啟動或已收工。</summary>
    Idle,
    /// <summary>正在建立連線。</summary>
    Connecting,
    /// <summary>已連上，正在接收事件。</summary>
    Connected,
    /// <summary>連線失敗，等待退避時間後會自己重連。</summary>
    Reconnecting,
    /// <summary>重連迴圈已經停止，不會自己回來（要手動重新連線）。</summary>
    Stopped
}

/// <summary>
/// 連線狀態的不可變快照。背景執行緒整份重建後以單次參考指派發布，
/// 繪製執行緒讀到的一定是完整的一份，不會看到寫到一半的狀態。
/// </summary>
public sealed class StreamStatus
{
    public StreamConnectionState State { get; init; } = StreamConnectionState.Idle;

    /// <summary>連續失敗次數（連上就歸零）。</summary>
    public int FailedAttempts { get; init; }

    /// <summary>Reconnecting 時的預定重試時刻。</summary>
    public DateTime? NextRetryUtc { get; init; }

    /// <summary>最後一次從伺服器收到任何位元組的時刻（含 keep-alive）。null = 從來沒收過。</summary>
    public DateTime? LastEventUtc { get; init; }

    /// <summary>目前這條連線建立的時刻。</summary>
    public DateTime? ConnectedSinceUtc { get; init; }

    /// <summary>最後一次失敗的原因。</summary>
    public string? LastError { get; init; }
}

/// <summary>Firebase 回報登入憑證已失效（auth_revoked）。要重新取得憑證才能重連。</summary>
public sealed class FirebaseAuthRevokedException : FirebaseException
{
    public FirebaseAuthRevokedException(string message) : base(message) { }
}

/// <summary>
/// Firebase Realtime Database REST streaming (Server-Sent Events)
/// GET {path}.json with Accept: text/event-stream 回傳長連線的 put/patch 事件。
/// 單一 client 可同時開多條 stream，關閉由 CancellationToken 控制。
/// </summary>
public sealed class FirebaseStreamClient : IDisposable
{
    private readonly string _dbUrl;
    private readonly FirebaseAuthClient _auth;
    private readonly ConcurrentDictionary<Guid, StreamSubscription> _subscriptions = new();

    public FirebaseStreamClient(string dbUrl, FirebaseAuthClient auth)
    {
        _dbUrl = dbUrl.TrimEnd('/');
        _auth = auth;
    }

    public StreamSubscription Subscribe(string path, Action<StreamEvent> onEvent, Action<Exception>? onError = null)
    {
        var sub = new StreamSubscription(this, path, onEvent, onError);
        _subscriptions.TryAdd(sub.Id, sub);
        sub.Start();
        return sub;
    }

    /// <summary>目前還活著的訂閱。ConcurrentDictionary.Values 本身就是快照，列舉時不會被中途改動。</summary>
    public ICollection<StreamSubscription> ActiveSubscriptions => _subscriptions.Values;

    /// <summary>要求所有訂閱立刻重試一次（插隊，不等退避時間走完）。</summary>
    public void ReconnectAll()
    {
        foreach (var sub in _subscriptions.Values)
        {
            try { sub.RequestImmediateRetry(); }
            catch (Exception ex) { Plugin.Log.Warning($"[Stream] 手動重連要求失敗: {ex.Message}"); }
        }
    }

    internal void Remove(Guid id) => _subscriptions.TryRemove(id, out _);

    public void Dispose()
    {
        foreach (var sub in _subscriptions.Values)
        {
            try { sub.Dispose(); } catch { }
        }
        _subscriptions.Clear();
    }

    internal string BuildUrlWithoutToken(string path) => $"{_dbUrl}/{path.TrimStart('/')}.json";

    internal FirebaseAuthClient Auth => _auth;

    public sealed class StreamSubscription : IDisposable
    {
        /// <summary>第一次重連的退避時間。</summary>
        private const int InitialBackoffMs = 1000;

        /// <summary>退避時間上限。</summary>
        private const int MaxBackoffMs = 30000;

        /// <summary>
        /// 讀取停滯看門狗：這段時間內沒有從伺服器收到「任何」位元組就判定連線已死。
        /// Firebase 大約每 30~45 秒送一次 keep-alive，所以 90 秒等於連漏兩三次。
        /// 沒有這個看門狗時，半開的 TCP 連線（睡眠、換網路、NAT 逾時）會讓讀取永遠不返回也永遠不擲例外，
        /// 串流就這樣無聲地死掉，既不重連也不會在畫面上留下任何徵兆。
        /// </summary>
        private const int StallTimeoutMs = 90000;

        /// <summary>建立連線（送出請求到收到 header）的逾時。</summary>
        private const int ConnectTimeoutMs = 30000;

        /// <summary>連線活過這麼久才算「穩定」，斷掉後可以把退避時間歸零重來。</summary>
        private const int StableConnectionSeconds = 30;

        /// <summary>伺服器正常關閉串流後的最短重連間隔（避免無延遲熱迴圈）。</summary>
        private const int MinReconnectDelayMs = 1000;

        /// <summary>「已連線」訊息的節流間隔；從失敗中恢復時一律照印。</summary>
        private const int ConnectLogThrottleSeconds = 60;

        public Guid Id { get; } = Guid.NewGuid();
        public string Path => _path;

        private readonly FirebaseStreamClient _owner;
        private readonly string _path;
        private readonly Action<StreamEvent> _onEvent;
        private readonly Action<Exception>? _onError;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _attemptLock = new();

        private Task? _loop;

        /// <summary>目前這一輪（連線中或退避中）的取消來源，手動重連靠取消它來插隊。</summary>
        private CancellationTokenSource? _attemptCts;

        private int _manualRetryPending;
        private int _disposed;

        private long _lastEventTicks;
        private long _connectedSinceTicks;
        private long _lastConnectLogTicks;

        private volatile StreamStatus _status = new();

        /// <summary>目前狀態的快照（整體參考指派，繪製執行緒可直接讀）。</summary>
        public StreamStatus Status => _status;

        public StreamSubscription(FirebaseStreamClient owner, string path, Action<StreamEvent> onEvent, Action<Exception>? onError)
        {
            _owner = owner;
            _path = path;
            _onEvent = onEvent;
            _onError = onError;
        }

        public void Start()
        {
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        /// <summary>
        /// 立刻重試一次：取消目前這一輪（不論是卡在讀取還是在等退避），下一圈馬上重連。
        /// 退避策略本身保留，這是插隊不是取消退避。
        /// </summary>
        public void RequestImmediateRetry()
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            Interlocked.Exchange(ref _manualRetryPending, 1);

            CancellationTokenSource? current;
            lock (_attemptLock) current = _attemptCts;
            // 可能剛好被迴圈換掉並釋放；旗標已經設好，下一圈自然會處理，所以吞掉即可。
            try { current?.Cancel(); } catch { }

            // 迴圈若已經整個停掉（Stopped），重新啟動它——這是「已斷線且不會自己回來」的唯一出路。
            if (_loop is { IsCompleted: true })
            {
                lock (_attemptLock)
                {
                    if (_loop is { IsCompleted: true } && Volatile.Read(ref _disposed) == 0 && !_cts.IsCancellationRequested)
                    {
                        Plugin.Log.Information($"[Stream {_path}] 重連迴圈先前已停止，重新啟動");
                        _loop = Task.Run(() => RunAsync(_cts.Token));
                    }
                }
            }
        }

        private CancellationTokenSource NewAttemptScope(CancellationToken ct)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            CancellationTokenSource? previous;
            lock (_attemptLock)
            {
                previous = _attemptCts;
                _attemptCts = cts;
            }
            try { previous?.Dispose(); } catch { }
            return cts;
        }

        private void Publish(StreamConnectionState state, int failedAttempts, DateTime? nextRetryUtc, string? lastError)
        {
            var lastEvent = Interlocked.Read(ref _lastEventTicks);
            var connectedSince = Interlocked.Read(ref _connectedSinceTicks);
            _status = new StreamStatus
            {
                State = state,
                FailedAttempts = failedAttempts,
                NextRetryUtc = nextRetryUtc,
                LastEventUtc = lastEvent == 0 ? null : new DateTime(lastEvent, DateTimeKind.Utc),
                ConnectedSinceUtc = connectedSince == 0 ? null : new DateTime(connectedSince, DateTimeKind.Utc),
                LastError = lastError
            };
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var backoffMs = InitialBackoffMs;
            var failedAttempts = 0;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var attemptCts = NewAttemptScope(ct);
                    var startedUtc = DateTime.UtcNow;
                    Exception? failure = null;
                    var authRevoked = false;
                    var manualInterrupt = false;

                    Publish(StreamConnectionState.Connecting, failedAttempts, null, _status.LastError);

                    try
                    {
                        await StreamOnceAsync(attemptCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        // attemptCts 被取消：不是手動插隊，就是讀取停滯看門狗開火。
                        if (Interlocked.Exchange(ref _manualRetryPending, 0) == 1)
                            manualInterrupt = true;
                        else
                            failure = new TimeoutException($"{StallTimeoutMs / 1000} 秒內沒有收到任何資料（含 keep-alive），判定連線已死");
                    }
                    catch (FirebaseAuthRevokedException ex)
                    {
                        authRevoked = true;
                        failure = ex;
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }

                    Interlocked.Exchange(ref _connectedSinceTicks, 0);
                    if (ct.IsCancellationRequested) break;

                    if (manualInterrupt)
                    {
                        Plugin.Log.Information($"[Stream {_path}] 收到手動重新連線要求，立刻重試（不等退避）");
                        backoffMs = InitialBackoffMs;
                        failedAttempts = 0;
                        continue;
                    }

                    if (authRevoked)
                    {
                        // 憑證已被伺服器判定失效：沿用同一顆會無限重試同一個失敗，必須強制重新取得。
                        _owner.Auth.InvalidateToken();
                        Plugin.Log.Information($"[Stream {_path}] 伺服器回報登入憑證已失效，重新取得憑證後再連線");
                    }

                    var connectedSeconds = (DateTime.UtcNow - startedUtc).TotalSeconds;
                    int delayMs;

                    if (failure == null)
                    {
                        // 伺服器把串流正常關掉（讀到 EOF）。這是 Firebase 的常態行為，不是錯誤。
                        if (connectedSeconds >= StableConnectionSeconds)
                        {
                            backoffMs = InitialBackoffMs;
                            failedAttempts = 0;
                            delayMs = MinReconnectDelayMs;
                        }
                        else
                        {
                            // 一連上就被關掉：照樣退避，否則會變成無延遲熱迴圈猛打 Firebase。
                            failedAttempts++;
                            delayMs = backoffMs;
                            backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
                        }
                        Plugin.Log.Debug($"[Stream {_path}] 伺服器關閉串流（已連線 {connectedSeconds:F0} 秒），{delayMs}ms 後重連");
                    }
                    else
                    {
                        failedAttempts++;
                        Plugin.Log.Warning($"[Stream {_path}] 連線中斷，{backoffMs}ms 後重連: {failure.Message}");
                        try { _onError?.Invoke(failure); } catch { }
                        delayMs = backoffMs;
                        backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
                    }

                    Publish(StreamConnectionState.Reconnecting, failedAttempts,
                            DateTime.UtcNow.AddMilliseconds(delayMs), failure?.Message);

                    var delayCts = NewAttemptScope(ct);
                    try
                    {
                        await Task.Delay(delayMs, delayCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        if (Interlocked.Exchange(ref _manualRetryPending, 0) == 1)
                        {
                            Plugin.Log.Information($"[Stream {_path}] 收到手動重新連線要求，不等退避直接重試");
                            backoffMs = InitialBackoffMs;
                            failedAttempts = 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 迴圈本身掛掉＝之後不會有人再重連，狀態列必須看得見。
                Plugin.Log.Error(ex, $"[Stream {_path}] 重連迴圈非預期結束");
                Interlocked.Exchange(ref _connectedSinceTicks, 0);
                Publish(StreamConnectionState.Stopped, failedAttempts, null, ex.Message);
                return;
            }

            Interlocked.Exchange(ref _connectedSinceTicks, 0);
            Publish(ct.IsCancellationRequested ? StreamConnectionState.Idle : StreamConnectionState.Stopped,
                    failedAttempts, null, _status.LastError);
        }

        private async Task StreamOnceAsync(CancellationToken ct)
        {
            var token = await _owner.Auth.EnsureSignedInAsync(ct).ConfigureAwait(false);
            var url = _owner.BuildUrlWithoutToken(_path) + $"?auth={Uri.EscapeDataString(token)}";

            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            // HttpClient.Timeout 必須是無限（長連線），所以建立連線那一段自己帶逾時，
            // 否則連不上時會卡在 SendAsync 永遠不返回。
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeoutMs);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var resp = await SendWithConnectTimeoutAsync(http, req, connectCts, ct).ConfigureAwait(false);
            try
            {
                if ((int)resp.StatusCode == 307 && resp.Headers.Location is { } redirect)
                {
                    using var req2 = new HttpRequestMessage(HttpMethod.Get, redirect);
                    var resp2 = await SendWithConnectTimeoutAsync(http, req2, connectCts, ct).ConfigureAwait(false);
                    try
                    {
                        resp2.EnsureSuccessStatusCode();
                        connectCts.CancelAfter(Timeout.Infinite);
                        await ReadEventsAsync(resp2, ct).ConfigureAwait(false);
                    }
                    finally { resp2.Dispose(); }
                    return;
                }

                resp.EnsureSuccessStatusCode();
                // header 已到手，解除連線逾時計時器，接下來交給讀取停滯看門狗。
                connectCts.CancelAfter(Timeout.Infinite);
                await ReadEventsAsync(resp, ct).ConfigureAwait(false);
            }
            finally { resp.Dispose(); }
        }

        private static async Task<HttpResponseMessage> SendWithConnectTimeoutAsync(
            HttpClient http, HttpRequestMessage req, CancellationTokenSource connectCts, CancellationToken ct)
        {
            try
            {
                return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && connectCts.IsCancellationRequested)
            {
                throw new TimeoutException($"建立連線逾時（{ConnectTimeoutMs / 1000} 秒內沒有回應）");
            }
        }

        private async Task ReadEventsAsync(HttpResponseMessage resp, CancellationToken ct)
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            var previous = _status;
            Interlocked.Exchange(ref _connectedSinceTicks, DateTime.UtcNow.Ticks);
            Publish(StreamConnectionState.Connected, 0, null, null);
            LogConnected(previous);

            // 讀取停滯看門狗：每收到一行就重新計時；整段都沒動靜就取消讀取，讓外層走重連。
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(StallTimeoutMs);

            string? currentEvent = null;
            var dataBuilder = new System.Text.StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
                if (line == null) break;

                // 任何一行（含 keep-alive 與分隔用的空行）都證明連線還活著。
                Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);
                readCts.CancelAfter(StallTimeoutMs);

                if (line.Length == 0)
                {
                    if (currentEvent != null && dataBuilder.Length > 0)
                    {
                        DispatchEvent(currentEvent, dataBuilder.ToString());
                    }
                    currentEvent = null;
                    dataBuilder.Clear();
                    continue;
                }

                if (line.StartsWith("event:"))
                    currentEvent = line.Substring(6).Trim();
                else if (line.StartsWith("data:"))
                {
                    if (dataBuilder.Length > 0) dataBuilder.Append('\n');
                    dataBuilder.Append(line.AsSpan(5).TrimStart());
                }
            }
        }

        private void LogConnected(StreamStatus previous)
        {
            // 只有「從失敗中回來」才強制印。
            // 不能把 previous.State == Reconnecting 也算進來：伺服器定期關閉串流時每一輪都會經過
            // Reconnecting，那樣節流等於沒有作用，正常運作也會一直洗 log。
            var recovered = previous.FailedAttempts > 0
                            || previous.State == StreamConnectionState.Stopped;

            var now = DateTime.UtcNow.Ticks;
            var last = Interlocked.Read(ref _lastConnectLogTicks);
            var quiet = last != 0 && new TimeSpan(now - last).TotalSeconds < ConnectLogThrottleSeconds;

            // 從失敗中恢復一定要印（那是使用者最需要看到的一行）；平常的定期重連則節流。
            if (!recovered && quiet) return;

            Interlocked.Exchange(ref _lastConnectLogTicks, now);
            Plugin.Log.Information(recovered
                ? $"[Stream {_path}] 已重新連線（先前連續失敗 {previous.FailedAttempts} 次）"
                : $"[Stream {_path}] 已連線");
        }

        private void DispatchEvent(string eventName, string data)
        {
            switch (eventName)
            {
                case "put":
                case "patch":
                {
                    // 單筆酬載解析失敗不該弄死整條串流，這一段維持原本的例外隔離。
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        var root = doc.RootElement;
                        var path = root.GetProperty("path").GetString() ?? "/";
                        JsonElement value = root.GetProperty("data");
                        _onEvent(new StreamEvent
                        {
                            Type = eventName == "put" ? StreamEventType.Put : StreamEventType.Patch,
                            Path = path,
                            DataJson = value.GetRawText()
                        });
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Error(ex, $"[Stream {_path}] 處理事件錯誤 ({eventName})");
                        try { _onError?.Invoke(ex); } catch { }
                    }
                    break;
                }
                case "keep-alive":
                    break;
                // 下面兩個一定要擲得出去，讓 RunAsync 接到並重連。
                // 舊版把 throw 包在自己的 try/catch 裡，例外根本離不開這個方法，
                // 於是「憑證失效」只留下一行 Error，重連完全靠伺服器順手把 socket 關掉。
                case "cancel":
                    throw new FirebaseException("Firebase 伺服器主動取消串流: " + data);
                case "auth_revoked":
                    throw new FirebaseAuthRevokedException("Firebase 回報登入憑證已失效 (auth_revoked)");
                default:
                    Plugin.Log.Debug($"[Stream {_path}] 未知事件類型: {eventName}");
                    break;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try { _cts.Cancel(); } catch { }

            CancellationTokenSource? current;
            lock (_attemptLock) current = _attemptCts;
            try { current?.Cancel(); } catch { }

            Interlocked.Exchange(ref _connectedSinceTicks, 0);
            Publish(StreamConnectionState.Idle, 0, null, null);
            _owner.Remove(Id);
        }
    }
}

public enum StreamEventType { Put, Patch }

public sealed class StreamEvent
{
    public StreamEventType Type { get; set; }
    public string Path { get; set; } = "/";
    public string DataJson { get; set; } = "null";

    public JsonElement Data
    {
        get
        {
            using var doc = JsonDocument.Parse(DataJson);
            return doc.RootElement.Clone();
        }
    }

    public T? DeserializeData<T>() =>
        string.IsNullOrWhiteSpace(DataJson) || DataJson == "null"
            ? default
            : JsonSerializer.Deserialize<T>(DataJson, FirebaseDatabaseClient.JsonOptions);
}
