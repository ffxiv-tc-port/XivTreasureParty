using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace XivTreasureParty.Firebase;

/// <summary>
/// WebSocket 用不了（升級被拒、握手不對、協定版本不認得、訊息看不懂）：呼叫端應改走 SSE。
/// <see cref="ReasonKey"/> 給「同一種原因只記一次」用；訊息不含網址與憑證。
/// </summary>
public sealed class RealtimeUnavailableException : FirebaseException
{
    public string ReasonKey { get; }

    public RealtimeUnavailableException(string reasonKey, string message) : base(message)
    {
        ReasonKey = reasonKey;
    }
}

/// <summary>
/// Firebase Realtime Database 的 WebSocket 即時協定（官方 JS SDK 用的那套，<c>/.ws?v=5</c>）用戶端，只做讀取／監聽；寫入照舊走 REST。
/// 所有監聽共用一條連線；token 換新時在同一條連線上重新驗證，不斷線。憑證只在連線內送，網址不帶憑證。
/// </summary>
/// <remarks>
/// 本檔假設與契約：
/// <list type="bullet">
/// <item>連線迴圈、送出、維護都在執行緒池；事件／錯誤／已連線回呼在接收迴圈的執行緒上依序呼叫，呼叫時不持有任何鎖。</item>
/// <item><c>_gate</c> 內只改集合與欄位，不做 I/O、不呼叫回呼、不 <c>Cancel()</c>（Cancel 會就地跑接續）。</item>
/// <item>推送給監聽的事件形狀和 SSE 一樣（path 相對於監聽位置、set＝put、merge＝patch），下游處理器不必分辨來源。</item>
/// </list>
/// </remarks>
public sealed class FirebaseRealtimeClient : IDisposable
{
    internal const string ProtocolVersion = "5";

    /// <summary>單一 WebSocket 訊息的字元上限；超過就照協定先送「段數」再分段（兩個方向都一樣）。</summary>
    internal const int MaxFrameChars = 16384;

    private const int InitialBackoffMs = 1000;
    private const int MaxBackoffMs = 30000;
    private const int ConnectTimeoutMs = 30000;
    private const int HandshakeTimeoutMs = 15000;
    private const int AuthTimeoutMs = 15000;
    private const int StableConnectionSeconds = 30;
    private const int MinReconnectDelayMs = 1000;
    private const int ConnectLogThrottleSeconds = 60;
    private const int CloseGraceMs = 2000;
    private const int MaxConsecutiveResets = 3;
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    /// <summary>這段時間沒送過東西就送一個 "0"（協定的 keepalive，伺服器不回）。</summary>
    private const int KeepAliveMs = 45000;

    /// <summary>這段時間沒收到任何東西就送 ping（伺服器回 pong），確認連線還活著。</summary>
    private const int PingAfterIdleMs = 45000;

    /// <summary>這段時間完全沒收到東西＝連線已死（半開的 TCP 不會自己報錯）。</summary>
    private const int StallTimeoutMs = 90000;

    /// <summary>最後一個監聽移除後，連線再留這麼久才關：離隊後馬上加入、單條被拒改三條時不必重連。</summary>
    internal static int LingerMs = 1500;

    /// <summary>多久問一次驗證用戶端 token 有沒有換新（何時換新由 <see cref="FirebaseAuthClient"/> 決定）。</summary>
    internal static TimeSpan AuthCheckInterval = TimeSpan.FromSeconds(30);

    private readonly Func<string> _dbUrl;
    private readonly FirebaseAuthClient _auth;
    private readonly object _gate = new();

    // 以下欄位只在 _gate 內改。
    private RealtimeListen[] _listens = Array.Empty<RealtimeListen>();
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private int _loopId;
    private int _lingerGeneration;
    private Session? _live;
    private CancellationTokenSource? _attemptCts;
    private bool _disposed;

    private string? _adoptedHostFor;
    private string? _adoptedHost;

    private int _manualRetryPending;
    private long _lastManualRetryTicks;
    private long _lastReceiveTicks;
    private long _lastConnectLogTicks;
    private volatile ConnState _conn = ConnState.Idle;

    private readonly ConcurrentDictionary<string, byte> _loggedOnce = new();

    /// <summary>成功握手的次數（測試用：同一條連線換證時不該增加）。</summary>
    internal int HandshakeCount;

    /// <summary>在既有連線上重新驗證成功的次數。</summary>
    internal int ReauthCount;

    /// <summary>收到「先報段數再分段」的訊息次數。</summary>
    internal int MultiFrameMessages;

    /// <param name="dbUrl">每次連線時讀一次目前的資料庫網址。</param>
    /// <param name="auth">取 token 用；必須與 <paramref name="dbUrl"/> 看同一個目前端點。</param>
    public FirebaseRealtimeClient(Func<string> dbUrl, FirebaseAuthClient auth)
    {
        _dbUrl = dbUrl;
        _auth = auth;
    }

    public string DatabaseUrl => _dbUrl().TrimEnd('/');

    public int ActiveListenCount => Volatile.Read(ref _listens).Length;

    /// <summary>開始監聽 <paramref name="path"/>；連線不存在就建立。回呼見類別契約。</summary>
    public ISyncSubscription Listen(string path, Action<StreamEvent> onEvent, Action<Exception>? onError = null, Action? onConnected = null)
    {
        var listen = new RealtimeListen(this, path, onEvent, onError, onConnected);
        Session? live;
        lock (_gate)
        {
            if (_disposed)
            {
                listen.MarkDisposed();
                return listen;
            }
            var next = new RealtimeListen[_listens.Length + 1];
            _listens.CopyTo(next, 0);
            next[^1] = listen;
            Volatile.Write(ref _listens, next);
            _lingerGeneration++;
            live = _live;
            if (live != null) listen.SentSession = live;
            EnsureLoopLocked();
        }
        if (live != null) SendListen(live, listen);
        return listen;
    }

    private void EnsureLoopLocked()
    {
        if (_loop is { IsCompleted: false } && _loopCts is { IsCancellationRequested: false }) return;
        var cts = new CancellationTokenSource();
        _loopCts = cts;
        var id = ++_loopId;
        _live = null;
        _loop = Task.Run(() => RunAsync(id, cts.Token));
    }

    internal void Remove(RealtimeListen listen)
    {
        Session? unlistenOn = null;
        var empty = false;
        int lingerGen = 0;
        lock (_gate)
        {
            var idx = Array.IndexOf(_listens, listen);
            if (idx < 0) return;
            var next = new RealtimeListen[_listens.Length - 1];
            Array.Copy(_listens, 0, next, 0, idx);
            Array.Copy(_listens, idx + 1, next, idx, _listens.Length - idx - 1);
            Volatile.Write(ref _listens, next);

            var live = _live;
            if (live != null && listen.SentSession == live && next.Length > 0
                && !Array.Exists(next, l => l.SentSession == live && l.NormalizedPath == listen.NormalizedPath))
                unlistenOn = live;
            listen.SentSession = null;
            empty = next.Length == 0;
            if (empty) lingerGen = ++_lingerGeneration;
        }

        if (unlistenOn != null)
            SendRequest(unlistenOn, "n", w => w.WriteString("p", listen.WirePath), null);
        if (empty)
            _ = CloseWhenStillEmptyAsync(lingerGen);
    }

    private async Task CloseWhenStillEmptyAsync(int lingerGen)
    {
        try
        {
            if (LingerMs > 0) await Task.Delay(LingerMs).ConfigureAwait(false);
            CancellationTokenSource? cts = null;
            lock (_gate)
            {
                if (_listens.Length == 0 && _lingerGeneration == lingerGen) cts = _loopCts;
            }
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Realtime] 關閉閒置連線失敗：{ex.Message}");
        }
    }

    /// <summary>手動重新連線：中斷目前這一輪（連線中或退避中）立刻重連；迴圈已停就重新啟動。一秒內重複呼叫只算一次。</summary>
    internal void RequestImmediateRetry()
    {
        var now = DateTime.UtcNow.Ticks;
        if (new TimeSpan(now - Interlocked.Read(ref _lastManualRetryTicks)).TotalSeconds < 1) return;
        Interlocked.Exchange(ref _lastManualRetryTicks, now);

        CancellationTokenSource? attempt;
        lock (_gate)
        {
            if (_disposed || _listens.Length == 0) return;
            Interlocked.Exchange(ref _manualRetryPending, 1);
            attempt = _attemptCts;
            if (_loop is { IsCompleted: true })
            {
                Plugin.Log.Information("[Realtime] 重連迴圈先前已停止，重新啟動");
                EnsureLoopLocked();
                attempt = null;
            }
        }
        try { attempt?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        RealtimeListen[] listens;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            cts = _loopCts;
            listens = _listens;
            Volatile.Write(ref _listens, Array.Empty<RealtimeListen>());
        }
        foreach (var l in listens) l.MarkDisposed();
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    // ───────────────────────── 連線迴圈 ─────────────────────────

    private bool IsCurrentLoop(int loopId) => Volatile.Read(ref _loopId) == loopId;

    private void Publish(int loopId, ConnState state)
    {
        if (IsCurrentLoop(loopId)) _conn = state;
    }

    private async Task RunAsync(int loopId, CancellationToken ct)
    {
        var backoffMs = InitialBackoffMs;
        var failedAttempts = 0;
        var consecutiveResets = 0;
        ConnState last = ConnState.Idle;

        try
        {
            while (!ct.IsCancellationRequested && ActiveListenCount > 0)
            {
                CancellationTokenSource attemptCts;
                lock (_gate)
                {
                    if (!IsCurrentLoop(loopId)) return;
                    attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _attemptCts = attemptCts;
                }

                Publish(loopId, last = last with { State = StreamConnectionState.Connecting, FailedAttempts = failedAttempts, NextRetryUtc = null, Live = null });

                var attempt = new Attempt();
                Exception? failure = null;
                var authRevoked = false;
                var manualInterrupt = false;
                var reset = false;
                try
                {
                    await ConnectAndServeAsync(loopId, failedAttempts, attempt, attemptCts.Token).ConfigureAwait(false);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception) when (attemptCts.IsCancellationRequested && Interlocked.Exchange(ref _manualRetryPending, 0) == 1)
                {
                    manualInterrupt = true;
                }
                catch (RealtimeResetException ex)
                {
                    reset = true;
                    failure = ex;
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
                finally
                {
                    lock (_gate)
                    {
                        if (_attemptCts == attemptCts) _attemptCts = null;
                        if (IsCurrentLoop(loopId)) _live = null;
                    }
                    attemptCts.Dispose();
                }

                var connectedSinceTicks = attempt.Session?.ConnectedSinceTicks ?? 0;
                if (ct.IsCancellationRequested) break;

                if (manualInterrupt || (failure == null && Interlocked.Exchange(ref _manualRetryPending, 0) == 1))
                {
                    Plugin.Log.Information("[Realtime] 收到手動重新連線要求，立刻重試（不等退避）");
                    backoffMs = InitialBackoffMs;
                    failedAttempts = 0;
                    continue;
                }

                var connectedSeconds = connectedSinceTicks == 0
                    ? 0d
                    : TimeSpan.FromTicks(DateTime.UtcNow.Ticks - connectedSinceTicks).TotalSeconds;
                var stable = connectedSeconds >= StableConnectionSeconds;
                if (stable)
                {
                    backoffMs = InitialBackoffMs;
                    failedAttempts = 0;
                }
                if (connectedSinceTicks != 0) consecutiveResets = 0;

                if (reset)
                {
                    if (++consecutiveResets <= MaxConsecutiveResets)
                    {
                        Plugin.Log.Information($"[Realtime] 伺服器要求改連另一台主機，立即重新連線（{failure!.Message}）");
                        continue;
                    }
                    failure = new FirebaseException($"伺服器連續 {consecutiveResets} 次要求改連別台主機，先退避再試");
                }

                if (authRevoked)
                {
                    _auth.InvalidateToken();
                    if (stable)
                    {
                        Plugin.Log.Information($"[Realtime] 伺服器撤銷登入憑證（已連線 {connectedSeconds / 60:F0} 分鐘），換新憑證後立即重新連線");
                        continue;
                    }
                    Plugin.Log.Information("[Realtime] 伺服器回報登入憑證已失效，重新取得憑證後再連線");
                }

                int delayMs;
                if (failure == null)
                {
                    if (stable)
                    {
                        delayMs = MinReconnectDelayMs;
                    }
                    else
                    {
                        failedAttempts++;
                        delayMs = backoffMs;
                        backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
                    }
                    Plugin.Log.Debug($"[Realtime] 伺服器關閉連線（已連線 {connectedSeconds:F0} 秒），{delayMs}ms 後重連");
                }
                else
                {
                    failedAttempts++;
                    // 「WebSocket 用不了」由訂閱端記一次 Information 並改走 SSE，這裡不再每次記 Warning。
                    if (failure is RealtimeUnavailableException)
                        Plugin.Log.Debug($"[Realtime] WebSocket 用不了，{backoffMs}ms 後重試: {failure.Message}");
                    else
                        Plugin.Log.Warning($"[Realtime] 連線中斷，{backoffMs}ms 後重連: {failure.Message}");
                    NotifyError(failure);
                    delayMs = backoffMs;
                    backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
                }

                Publish(loopId, last = new ConnState
                {
                    State = StreamConnectionState.Reconnecting,
                    FailedAttempts = failedAttempts,
                    NextRetryUtc = DateTime.UtcNow.AddMilliseconds(delayMs),
                    LastError = failure?.Message ?? last.LastError,
                    LastStatusCode = (failure as StreamHttpException)?.StatusCode,
                });

                CancellationTokenSource delayCts;
                lock (_gate)
                {
                    delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _attemptCts = delayCts;
                }
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
                        Plugin.Log.Information("[Realtime] 收到手動重新連線要求，不等退避直接重試");
                        backoffMs = InitialBackoffMs;
                        failedAttempts = 0;
                    }
                }
                finally
                {
                    lock (_gate)
                    {
                        if (_attemptCts == delayCts) _attemptCts = null;
                    }
                    delayCts.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Realtime] 重連迴圈非預期結束");
            Publish(loopId, new ConnState { State = StreamConnectionState.Stopped, FailedAttempts = failedAttempts, LastError = ex.Message });
            return;
        }

        Publish(loopId, ct.IsCancellationRequested || ActiveListenCount == 0
            ? ConnState.Idle
            : new ConnState { State = StreamConnectionState.Stopped, FailedAttempts = failedAttempts, LastError = last.LastError });
    }

    /// <summary>一輪連線的結果容器：例外路徑上迴圈也要讀得到這輪連了多久。</summary>
    private sealed class Attempt
    {
        public Session? Session;
    }

    private void NotifyError(Exception failure)
    {
        foreach (var l in Volatile.Read(ref _listens))
        {
            if (l.IsDisposed) continue;
            try { l.OnError?.Invoke(failure); }
            catch (Exception ex) { Plugin.Log.Warning($"[Realtime {l.Path}] 錯誤回呼失敗：{ex.Message}"); }
        }
    }

    /// <summary>連一次並服務到連線結束。正常結束（伺服器關閉）回傳；其餘一律擲出，由迴圈決定怎麼重連。</summary>
    private async Task ConnectAndServeAsync(int loopId, int previousFailures, Attempt attempt, CancellationToken ct)
    {
        var dbUrl = DatabaseUrl;
        var token = await _auth.EnsureSignedInAsync(ct).ConfigureAwait(false);
        if (DatabaseUrl != dbUrl)
            throw new FirebaseException("同步伺服器已切換，放棄這次連線");

        var origin = new Uri(dbUrl);
        var (target, usedAdoptedHost) = BuildSocketUri(origin);

        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.Zero;
        ws.Options.CollectHttpResponseDetails = true;
        var session = new Session(ws, origin, token);
        attempt.Session = session;
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(ConnectTimeoutMs);
                try
                {
                    await ws.ConnectAsync(target, connectCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    ForgetAdoptedHost(origin, usedAdoptedHost);
                    throw new TimeoutException($"建立連線逾時（{ConnectTimeoutMs / 1000} 秒內沒有回應）");
                }
                catch (WebSocketException ex) when (!ct.IsCancellationRequested)
                {
                    ForgetAdoptedHost(origin, usedAdoptedHost);
                    throw ClassifyUpgradeFailure(ws, ex);
                }
            }

            session.Touch(ref _lastReceiveTicks);
            session.SetPhase(SessionPhase.Handshake, HandshakeTimeoutMs);
            using var reg = ct.Register(static s => ((Session)s!).BeginClose(), session);
            var sender = Task.Run(() => SendLoopAsync(session));
            var upkeep = Task.Run(() => UpkeepLoopAsync(loopId, session));
            try
            {
                await ReceiveLoopAsync(loopId, previousFailures, session).ConfigureAwait(false);
            }
            finally
            {
                session.Finish();
                await CloseQuietlyAsync(session).ConfigureAwait(false);
                try { await Task.WhenAll(sender, upkeep).ConfigureAwait(false); } catch { }
            }
        }
        finally
        {
            ws.Dispose();
        }
    }

    private static Exception ClassifyUpgradeFailure(ClientWebSocket ws, WebSocketException ex)
    {
        var code = (int)ws.HttpStatusCode;
        if (code == 0)
            return new FirebaseException("連不上同步伺服器：" + ex.Message);
        // 連線已滿／限流：SSE 一樣會被擋，留在 WebSocket 退避重試，狀態列照 402 說明。
        if (code is 402 or 429)
            return new StreamHttpException(code, ((HttpStatusCode)code).ToString(), string.Empty);
        return new RealtimeUnavailableException($"upgrade-{code}", $"伺服器不接受 WebSocket 連線（HTTP {code}）");
    }

    private (Uri Target, bool UsedAdopted) BuildSocketUri(Uri origin)
    {
        string? adopted;
        lock (_gate) adopted = _adoptedHostFor == origin.Authority ? _adoptedHost : null;
        var host = adopted ?? origin.Authority;
        var scheme = origin.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        var ns = IsLiteralOrLocalHost(origin.Host) ? null : origin.Host.Split('.')[0].ToLowerInvariant();
        var url = $"{scheme}://{host}/.ws?v={ProtocolVersion}" + (ns == null ? string.Empty : "&ns=" + Uri.EscapeDataString(ns));
        return (new Uri(url), adopted != null);
    }

    private void ForgetAdoptedHost(Uri origin, bool usedAdopted)
    {
        if (!usedAdopted) return;
        lock (_gate)
        {
            if (_adoptedHostFor == origin.Authority) _adoptedHostFor = _adoptedHost = null;
        }
    }

    internal static bool IsLiteralOrLocalHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host.Trim('[', ']'), out _);

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>
    /// 伺服器指定的新主機能不能跟：網域名稱要和原本同一個上層網域（最後兩段，例 <c>firebasedatabase.app</c>）；
    /// 原本是 IP／localhost 時只准同一台主機或都在本機。拒絕帶路徑、帳密、空白的值。
    /// </summary>
    internal static bool IsAcceptableHost(Uri origin, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 260) return false;
        if (candidate.IndexOfAny(new[] { '/', '?', '#', '@', ' ', '\\' }) >= 0) return false;
        if (!Uri.TryCreate("wss://" + candidate, UriKind.Absolute, out var nu) || string.IsNullOrEmpty(nu.Host)) return false;

        if (IsLiteralOrLocalHost(origin.Host))
            return string.Equals(nu.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
                   || (IsLoopbackHost(origin.Host) && IsLoopbackHost(nu.Host));

        if (IsLiteralOrLocalHost(nu.Host)) return false;
        var labels = origin.Host.Split('.');
        if (labels.Length < 2) return string.Equals(nu.Host, origin.Host, StringComparison.OrdinalIgnoreCase);
        var suffix = "." + labels[^2] + "." + labels[^1];
        return string.Equals(nu.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
               || nu.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryAdoptHost(Uri origin, string? host, bool isReset)
    {
        if (string.IsNullOrEmpty(host) || string.Equals(host, origin.Authority, StringComparison.OrdinalIgnoreCase))
        {
            if (isReset && !string.IsNullOrEmpty(host))
                lock (_gate) _adoptedHostFor = _adoptedHost = null;
            return !string.IsNullOrEmpty(host);
        }
        // 握手帶的主機：連的是 IP／localhost（模擬器、本機測試）時照 JS SDK 的模擬器作法不採用。
        if (!isReset && IsLiteralOrLocalHost(origin.Host)) return false;
        if (!IsAcceptableHost(origin, host))
        {
            LogOnce($"host|{host}", $"[Realtime] 伺服器指定的主機 {host} 不在原本的網域底下，不跟（同一個主機只記這一次）");
            return false;
        }
        lock (_gate)
        {
            _adoptedHostFor = origin.Authority;
            _adoptedHost = host;
        }
        return true;
    }

    private void LogOnce(string key, string message)
    {
        if (_loggedOnce.TryAdd(key, 0)) Plugin.Log.Information(message);
    }

    // ───────────────────────── 接收 ─────────────────────────

    private async Task ReceiveLoopAsync(int loopId, int previousFailures, Session s)
    {
        var buffer = new byte[16384];
        using var bytes = new MemoryStream();
        var parts = new StringBuilder();
        var partsRemaining = 0;

        try
        {
            while (true)
            {
                bytes.SetLength(0);
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await s.Ws.ReceiveAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    bytes.Write(buffer, 0, r.Count);
                    if (bytes.Length > MaxMessageBytes)
                        throw new RealtimeUnavailableException("oversize", "伺服器送來的訊息大得不合理");
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Close)
                {
                    if (s.Failure != null) throw s.Failure;
                    return;
                }

                s.Touch(ref _lastReceiveTicks);
                var text = Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);

                if (partsRemaining > 0)
                {
                    parts.Append(text);
                    if (parts.Length > MaxMessageBytes)
                        throw new RealtimeUnavailableException("oversize", "伺服器送來的訊息大得不合理");
                    if (--partsRemaining > 0) continue;
                    text = parts.ToString();
                    parts.Clear();
                }
                else if (text.Length <= 6 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                {
                    if (count > 1)
                    {
                        partsRemaining = count;
                        Interlocked.Increment(ref MultiFrameMessages);
                    }
                    continue;
                }

                HandleMessage(loopId, previousFailures, s, text);
            }
        }
        catch (Exception) when (s.Failure != null)
        {
            throw s.Failure;
        }
    }

    private void HandleMessage(int loopId, int previousFailures, Session s, string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { throw new RealtimeUnavailableException("protocol-json", "伺服器送來看不懂的訊息（不是 JSON）"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("t", out var layer) || layer.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("d", out var body) || body.ValueKind != JsonValueKind.Object)
                throw new RealtimeUnavailableException("protocol-envelope", "伺服器訊息的外層格式看不懂");

            switch (layer.GetString())
            {
                case "c": HandleControl(loopId, previousFailures, s, body); break;
                case "d": HandleData(s, body); break;
                default: throw new RealtimeUnavailableException("protocol-layer", "伺服器訊息的種類看不懂");
            }
        }
    }

    private void HandleControl(int loopId, int previousFailures, Session s, JsonElement control)
    {
        var cmd = control.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        control.TryGetProperty("d", out var payload);

        switch (cmd)
        {
            case "h":
            {
                if (s.Phase != SessionPhase.Handshake) return;
                if (payload.ValueKind != JsonValueKind.Object)
                    throw new RealtimeUnavailableException("handshake-invalid", "伺服器的握手內容看不懂");
                var version = payload.TryGetProperty("v", out var v) ? v.ToString() : "?";
                if (version != ProtocolVersion)
                    throw new RealtimeUnavailableException($"version-{version}", $"伺服器的即時協定版本是 {version}，插件只認得 {ProtocolVersion}");
                TryAdoptHost(s.Origin, payload.TryGetProperty("h", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null, isReset: false);
                Interlocked.Increment(ref HandshakeCount);
                s.SetPhase(SessionPhase.Auth, AuthTimeoutMs);
                s.Handshaken = true;
                var token = s.AuthToken;
                SendRequest(s, "auth", w => w.WriteString("cred", token), (status, _) =>
                {
                    if (status != "ok")
                        throw new FirebaseAuthRevokedException($"Firebase 拒絕登入憑證（{status}）");
                    GoLive(loopId, previousFailures, s);
                });
                return;
            }
            case "r":
            {
                var host = payload.ValueKind == JsonValueKind.String ? payload.GetString() : null;
                if (TryAdoptHost(s.Origin, host, isReset: true))
                    throw new RealtimeResetException($"新主機 {host}");
                throw new FirebaseException("伺服器要求改連的主機不能用");
            }
            case "s":
            {
                var reason = payload.ValueKind == JsonValueKind.String ? payload.GetString() ?? "" : payload.ToString();
                LogOnce($"shutdown|{reason}", $"[Realtime] 伺服器要求關閉連線，原因：{StreamHttpException.Shorten(reason, 300)}（同一種原因只記這一次）");
                throw new FirebaseException("伺服器關閉了連線：" + StreamHttpException.Shorten(reason, 120));
            }
            case "e":
            {
                var msg = payload.ToString();
                LogOnce($"error|{msg}", $"[Realtime] 伺服器回報錯誤：{StreamHttpException.Shorten(msg, 300)}（同一種只記這一次）");
                return;
            }
            case "o":
            case "n":
                return;
            default:
                if (_loggedOnce.TryAdd($"ctl|{cmd}", 0)) Plugin.Log.Debug($"[Realtime] 略過不認得的控制訊息：{cmd}");
                return;
        }
    }

    /// <summary>驗證成功：這條連線開始服務，把目前所有監聽送出去。</summary>
    private void GoLive(int loopId, int previousFailures, Session s)
    {
        RealtimeListen[] toSend;
        lock (_gate)
        {
            if (!IsCurrentLoop(loopId) || s.Finished) return;
            s.Phase = SessionPhase.Live;
            s.ConnectedSinceTicks = DateTime.UtcNow.Ticks;
            _live = s;
            toSend = _listens;
            foreach (var l in toSend)
            {
                l.SentSession = s;
                l.State = ListenState.Pending;
            }
        }

        var previous = _conn;
        Publish(loopId, new ConnState
        {
            State = StreamConnectionState.Connected,
            ConnectedSinceTicks = s.ConnectedSinceTicks,
            Live = s,
        });
        LogConnected(previous, previousFailures);
        foreach (var l in toSend) SendListen(s, l);
    }

    private void LogConnected(ConnState previous, int previousFailures)
    {
        var recovered = previousFailures > 0 || previous.State == StreamConnectionState.Stopped;
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastConnectLogTicks);
        var quiet = last != 0 && new TimeSpan(now - last).TotalSeconds < ConnectLogThrottleSeconds;
        if (!recovered && quiet) return;
        Interlocked.Exchange(ref _lastConnectLogTicks, now);
        Plugin.Log.Information(recovered
            ? $"[Realtime] WebSocket 已重新連線（先前連續失敗 {previousFailures} 次）"
            : "[Realtime] WebSocket 已連線");
    }

    private void HandleData(Session s, JsonElement body)
    {
        if (body.TryGetProperty("r", out var rEl))
        {
            if (!rEl.TryGetInt64(out var r) || !s.Pending.TryRemove(r, out var cb)) return;
            var b = body.TryGetProperty("b", out var bEl) ? bEl : default;
            var status = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("s", out var st) && st.ValueKind == JsonValueKind.String
                ? st.GetString() ?? ""
                : "";
            var data = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("d", out var d) ? d : default;
            cb?.Invoke(status, data);
            return;
        }

        if (body.TryGetProperty("error", out var err))
            throw new FirebaseException("伺服器回報錯誤：" + StreamHttpException.Shorten(err.ToString(), 120));

        if (!body.TryGetProperty("a", out var aEl) || aEl.ValueKind != JsonValueKind.String) return;
        var action = aEl.GetString();
        var pb = body.TryGetProperty("b", out var bb) ? bb : default;
        var path = pb.ValueKind == JsonValueKind.Object && pb.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? ""
            : "";

        switch (action)
        {
            case "d":
            case "m":
                if (pb.ValueKind != JsonValueKind.Object || !pb.TryGetProperty("d", out var value)) return;
                DispatchData(s, path, value, action == "m");
                return;
            case "c":
                OnListenRevoked(s, path);
                return;
            case "ac":
            {
                var status = pb.ValueKind == JsonValueKind.Object && pb.TryGetProperty("s", out var st) ? st.ToString() : "?";
                throw new FirebaseAuthRevokedException($"Firebase 回報登入憑證已失效 (auth_revoked: {status})");
            }
            default:
                if (_loggedOnce.TryAdd($"act|{action}", 0)) Plugin.Log.Debug($"[Realtime] 略過不認得的推送：{action}");
                return;
        }
    }

    /// <summary>伺服器的 set／merge（path 是從資料庫根算起）轉成每個相關監聽的 put／patch（path 相對於監聽位置）。</summary>
    private void DispatchData(Session s, string pushPath, JsonElement value, bool merge)
    {
        var push = SplitPath(pushPath);
        foreach (var l in Volatile.Read(ref _listens))
        {
            if (l.SentSession != s) continue;
            foreach (var ev in RelativeEvents(l.Segments, push, value, merge))
            {
                try { l.OnEvent(ev); }
                catch (Exception ex) { Plugin.Log.Error(ex, $"[Realtime {l.Path}] 處理事件錯誤"); }
            }
        }
    }

    internal static string[] SplitPath(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// 一筆推送對某個監聽位置等於哪些 SSE 形狀的事件。推送在監聽位置或底下＝同型別、相對 path；
    /// 在上層（例如整個隊伍被刪）＝取出監聽位置那一段當 put "/"。
    /// </summary>
    internal static System.Collections.Generic.IEnumerable<StreamEvent> RelativeEvents(string[] listen, string[] push, JsonElement value, bool merge)
    {
        if (IsPrefix(listen, push))
        {
            yield return new StreamEvent
            {
                Type = merge ? StreamEventType.Patch : StreamEventType.Put,
                Path = "/" + string.Join('/', push, listen.Length, push.Length - listen.Length),
                DataJson = value.GetRawText(),
            };
            yield break;
        }
        if (!IsPrefix(push, listen)) yield break;

        if (!merge)
        {
            yield return new StreamEvent { Type = StreamEventType.Put, Path = "/", DataJson = Descend(value, listen, push.Length) };
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Object) yield break;
        var below = new System.Collections.Generic.List<(string Key, string Json)>();
        foreach (var prop in value.EnumerateObject())
        {
            var full = new string[push.Length];
            push.CopyTo(full, 0);
            full = Concat(full, SplitPath(prop.Name));
            if (IsPrefix(full, listen))
                yield return new StreamEvent { Type = StreamEventType.Put, Path = "/", DataJson = Descend(prop.Value, listen, full.Length) };
            else if (IsPrefix(listen, full))
                below.Add((string.Join('/', full, listen.Length, full.Length - listen.Length), prop.Value.GetRawText()));
        }
        if (below.Count > 0)
        {
            var sb = new StringBuilder("{");
            for (var i = 0; i < below.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonSerializer.Serialize(below[i].Key)).Append(':').Append(below[i].Json);
            }
            sb.Append('}');
            yield return new StreamEvent { Type = StreamEventType.Patch, Path = "/", DataJson = sb.ToString() };
        }
    }

    private static string[] Concat(string[] a, string[] b)
    {
        var r = new string[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    private static bool IsPrefix(string[] prefix, string[] path)
    {
        if (prefix.Length > path.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
            if (!string.Equals(prefix[i], path[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static string Descend(JsonElement value, string[] target, int from)
    {
        var cur = value;
        for (var i = from; i < target.Length; i++)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(target[i], out var next)) return "null";
            cur = next;
        }
        return cur.GetRawText();
    }

    // ───────────────────────── 監聽 ─────────────────────────

    private void SendListen(Session s, RealtimeListen l)
    {
        SendRequest(s, "q", w =>
        {
            w.WriteString("p", l.WirePath);
            w.WriteString("h", "");
        }, (status, data) => OnListenResponse(s, l, status, data));
    }

    private void OnListenResponse(Session s, RealtimeListen l, string status, JsonElement data)
    {
        if (l.IsDisposed || l.SentSession != s) return;
        if (status == "ok")
        {
            l.State = ListenState.Acked;
            try { l.OnConnected?.Invoke(); }
            catch (Exception ex) { Plugin.Log.Warning($"[Realtime {l.Path}] 已連線回呼失敗：{ex.Message}"); }
            return;
        }
        var detail = data.ValueKind == JsonValueKind.String ? data.GetString() ?? "" : data.ValueKind == JsonValueKind.Undefined ? "" : data.ToString();
        LogOnce($"deny|{status}|{detail}", $"[Realtime {l.Path}] 伺服器拒絕監聽：{status}（{StreamHttpException.Shorten(detail, 200)}）（同一種回應只記這一次）");
        Deny(s, l, $"伺服器拒絕監聽（{status}）");
    }

    private void OnListenRevoked(Session s, string path)
    {
        var target = string.Join('/', SplitPath(path));
        foreach (var l in Volatile.Read(ref _listens))
        {
            if (l.SentSession != s || l.NormalizedPath != target) continue;
            Deny(s, l, "伺服器主動取消監聽（規則不准再讀）");
        }
    }

    /// <summary>監聽被拒或被撤銷：和 SSE 串流被拒一樣退避重試（1→30 秒），並把錯誤交給訂閱端（單條被拒時它會改三條）。</summary>
    private void Deny(Session s, RealtimeListen l, string message)
    {
        int failed;
        lock (_gate)
        {
            if (l.SentSession != s) return;
            l.SentSession = null;
            failed = l.State.Failed + 1;
            var delay = Math.Min(InitialBackoffMs << Math.Min(failed - 1, 5), MaxBackoffMs);
            l.State = new ListenState { Failed = failed, Error = message, RetryUtc = DateTime.UtcNow.AddMilliseconds(delay) };
        }
        var retryIn = (int)Math.Max(0, (l.State.RetryUtc!.Value - DateTime.UtcNow).TotalMilliseconds);
        Plugin.Log.Warning($"[Realtime {l.Path}] 監聽中斷，{retryIn}ms 後重試: {message}");
        try { l.OnError?.Invoke(new StreamCancelledException(message)); }
        catch (Exception ex) { Plugin.Log.Warning($"[Realtime {l.Path}] 錯誤回呼失敗：{ex.Message}"); }
        _ = RelistenLaterAsync(s, l, retryIn);
    }

    private async Task RelistenLaterAsync(Session s, RealtimeListen l, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs, s.Ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (l.IsDisposed || _live != s || l.SentSession != null || Array.IndexOf(_listens, l) < 0) return;
                l.SentSession = s;
            }
            SendListen(s, l);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Realtime {l.Path}] 重試監聽失敗：{ex.Message}");
        }
    }

    // ───────────────────────── 送出 ─────────────────────────

    private static void SendRequest(Session s, string action, Action<Utf8JsonWriter> writeBody, Action<string, JsonElement>? onResponse)
    {
        if (s.Finished) return;
        var r = Interlocked.Increment(ref s.NextRequest);
        if (onResponse != null) s.Pending[r] = onResponse;

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("t", "d");
            w.WriteStartObject("d");
            w.WriteNumber("r", r);
            w.WriteString("a", action);
            w.WriteStartObject("b");
            writeBody(w);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        s.Outbox.Writer.TryWrite(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static void SendControl(Session s, string type)
    {
        s.Outbox.Writer.TryWrite($"{{\"t\":\"c\",\"d\":{{\"t\":\"{type}\",\"d\":{{}}}}}}");
    }

    private static async Task SendLoopAsync(Session s)
    {
        try
        {
            await foreach (var message in s.Outbox.Reader.ReadAllAsync(s.Ct).ConfigureAwait(false))
            {
                await s.SendLock.WaitAsync(s.Ct).ConfigureAwait(false);
                try
                {
                    if (message.Length > MaxFrameChars)
                    {
                        var count = (message.Length + MaxFrameChars - 1) / MaxFrameChars;
                        await SendTextAsync(s, count.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                        for (var i = 0; i < count; i++)
                            await SendTextAsync(s, message.Substring(i * MaxFrameChars, Math.Min(MaxFrameChars, message.Length - i * MaxFrameChars))).ConfigureAwait(false);
                    }
                    else
                    {
                        await SendTextAsync(s, message).ConfigureAwait(false);
                    }
                }
                finally
                {
                    s.SendLock.Release();
                }
            }
        }
        catch (OperationCanceledException) when (s.Ct.IsCancellationRequested) { }
        catch (Exception) when (s.Closing) { }
        catch (Exception ex)
        {
            s.Fail(new FirebaseException("送出失敗：" + ex.Message));
            s.Abort();
        }
    }

    private static async Task SendTextAsync(Session s, string text)
    {
        await s.Ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, s.Ct).ConfigureAwait(false);
        Interlocked.Exchange(ref s.LastSendTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>每秒一次：階段逾時、keepalive、ping、停滯判斷、token 換新後在同一條連線上重新驗證。</summary>
    private async Task UpkeepLoopAsync(int loopId, Session s)
    {
        var nextAuthCheck = DateTime.UtcNow + AuthCheckInterval;
        try
        {
            while (!s.Ct.IsCancellationRequested)
            {
                await Task.Delay(Math.Min(1000, Math.Max(50, (int)AuthCheckInterval.TotalMilliseconds)), s.Ct).ConfigureAwait(false);
                var now = DateTime.UtcNow.Ticks;

                if (s.Phase != SessionPhase.Live)
                {
                    if (now > Interlocked.Read(ref s.PhaseDeadlineTicks))
                    {
                        s.Fail(s.Phase == SessionPhase.Handshake
                            ? new RealtimeUnavailableException("handshake-timeout", $"連上後 {HandshakeTimeoutMs / 1000} 秒內沒有收到握手")
                            : new TimeoutException($"登入驗證 {AuthTimeoutMs / 1000} 秒內沒有回應"));
                        s.Abort();
                        return;
                    }
                    continue;
                }

                var idleIn = TimeSpan.FromTicks(now - Interlocked.Read(ref s.LastReceiveTicks)).TotalMilliseconds;
                if (idleIn >= StallTimeoutMs)
                {
                    s.Fail(new TimeoutException($"{StallTimeoutMs / 1000} 秒內沒有收到任何資料（含 ping 回應），判定連線已死"));
                    s.Abort();
                    return;
                }
                if (idleIn >= PingAfterIdleMs && now - Interlocked.Read(ref s.LastPingTicks) >= TimeSpan.FromMilliseconds(PingAfterIdleMs).Ticks)
                {
                    Interlocked.Exchange(ref s.LastPingTicks, now);
                    SendControl(s, "p");
                }
                else if (TimeSpan.FromTicks(now - Interlocked.Read(ref s.LastSendTicks)).TotalMilliseconds >= KeepAliveMs)
                {
                    s.Outbox.Writer.TryWrite("0");
                }

                if (DateTime.UtcNow >= nextAuthCheck)
                {
                    nextAuthCheck = DateTime.UtcNow + AuthCheckInterval;
                    await ReauthIfTokenChangedAsync(loopId, s).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (s.Ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Realtime] 連線維護工作失敗：{ex.Message}");
            s.Fail(new FirebaseException("連線維護工作失敗：" + ex.Message));
            s.Abort();
        }
    }

    private async Task ReauthIfTokenChangedAsync(int loopId, Session s)
    {
        if (Interlocked.CompareExchange(ref s.AuthInFlight, 1, 0) != 0) return;
        var keepFlag = false;
        try
        {
            string token;
            try
            {
                token = await _auth.EnsureSignedInAsync(s.Ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (s.Ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // 換證失敗時舊 token 在伺服器上仍有效到到期為止；到期後伺服器會撤銷，走重連路線。
                Plugin.Log.Debug($"[Realtime] 檢查 token 失敗（稍後再試）：{ex.Message}");
                return;
            }
            if (token == s.AuthToken || s.Finished || !IsCurrentLoop(loopId)) return;

            s.AuthToken = token;
            keepFlag = true;
            SendRequest(s, "auth", w => w.WriteString("cred", token), (status, _) =>
            {
                Interlocked.Exchange(ref s.AuthInFlight, 0);
                if (status != "ok")
                    throw new FirebaseAuthRevokedException($"Firebase 拒絕換新的登入憑證（{status}）");
                Interlocked.Increment(ref ReauthCount);
                Plugin.Log.Information("[Realtime] 登入憑證已換新，在同一條連線上重新驗證（沒有斷線）");
            });
        }
        finally
        {
            if (!keepFlag) Interlocked.Exchange(ref s.AuthInFlight, 0);
        }
    }

    /// <summary>連線已經結束接收時呼叫：補送關閉訊框（禮貌）後中止。</summary>
    private static async Task CloseQuietlyAsync(Session s)
    {
        await SendCloseAsync(s).ConfigureAwait(false);
        s.Abort();
    }

    private static async Task SendCloseAsync(Session s)
    {
        try
        {
            if (s.Ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(CloseGraceMs);
                await s.SendLock.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    await s.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    s.SendLock.Release();
                }
            }
        }
        catch
        {
            // 關閉訊框只是禮貌；送不出去由呼叫端中止。
        }
    }

    // ───────────────────────── 狀態 ─────────────────────────

    internal StreamStatus ComposeStatus(RealtimeListen l)
    {
        var c = _conn;
        var ls = l.State;
        var lastEvent = Interlocked.Read(ref _lastReceiveTicks);
        var cached = l.CachedStatus;
        if (cached != null && ReferenceEquals(cached.Conn, c) && ReferenceEquals(cached.Listen, ls) && cached.LastEventTicks == lastEvent
            && ReferenceEquals(cached.SentSession, l.SentSession))
            return cached.Status;

        StreamStatus status;
        var last = lastEvent == 0 ? (DateTime?)null : new DateTime(lastEvent, DateTimeKind.Utc);
        if (l.IsDisposed)
            status = new StreamStatus();
        else if (c.State != StreamConnectionState.Connected || c.Live == null)
            status = new StreamStatus
            {
                State = c.State,
                FailedAttempts = c.FailedAttempts,
                NextRetryUtc = c.NextRetryUtc,
                LastEventUtc = last,
                LastError = c.LastError,
                LastStatusCode = c.LastStatusCode,
            };
        else if (ls.IsAcked && l.SentSession == c.Live)
            status = new StreamStatus
            {
                State = StreamConnectionState.Connected,
                LastEventUtc = last,
                ConnectedSinceUtc = new DateTime(c.ConnectedSinceTicks, DateTimeKind.Utc),
            };
        else if (ls.Error != null)
            status = new StreamStatus
            {
                State = StreamConnectionState.Reconnecting,
                FailedAttempts = ls.Failed,
                NextRetryUtc = ls.RetryUtc,
                LastEventUtc = last,
                LastError = ls.Error,
            };
        else
            status = new StreamStatus { State = StreamConnectionState.Connecting, LastEventUtc = last };

        l.CachedStatus = new CachedStatus(c, ls, lastEvent, l.SentSession, status);
        return status;
    }

    internal sealed record CachedStatus(ConnState Conn, ListenState Listen, long LastEventTicks, Session? SentSession, StreamStatus Status);

    internal sealed record ConnState
    {
        public static readonly ConnState Idle = new();
        public StreamConnectionState State { get; init; } = StreamConnectionState.Idle;
        public int FailedAttempts { get; init; }
        public DateTime? NextRetryUtc { get; init; }
        public string? LastError { get; init; }
        public int? LastStatusCode { get; init; }
        public long ConnectedSinceTicks { get; init; }
        public Session? Live { get; init; }
    }

    internal sealed record ListenState
    {
        public static readonly ListenState Pending = new();
        public static readonly ListenState Acked = new() { IsAcked = true };
        public bool IsAcked { get; init; }
        public int Failed { get; init; }
        public string? Error { get; init; }
        public DateTime? RetryUtc { get; init; }
    }

    internal enum SessionPhase { Handshake, Auth, Live }

    /// <summary>一條 WebSocket 連線的全部狀態；連線結束就整個丟掉。</summary>
    internal sealed class Session
    {
        public readonly ClientWebSocket Ws;
        public readonly Uri Origin;
        public volatile string AuthToken;
        public readonly Channel<string> Outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        public readonly SemaphoreSlim SendLock = new(1, 1);
        public readonly ConcurrentDictionary<long, Action<string, JsonElement>> Pending = new();
        private readonly CancellationTokenSource _cts = new();
        public long NextRequest;
        public long LastReceiveTicks;
        public long LastSendTicks;
        public long LastPingTicks;
        public long PhaseDeadlineTicks;
        public long ConnectedSinceTicks;
        public int AuthInFlight;
        public volatile SessionPhase Phase;
        private volatile Exception? _failure;
        private int _finished;
        private int _closing;

        public Session(ClientWebSocket ws, Uri origin, string token)
        {
            Ws = ws;
            Origin = origin;
            AuthToken = token;
            LastSendTicks = DateTime.UtcNow.Ticks;
        }

        /// <summary>送出、維護、延後重試這些附屬工作的取消；連線結束時取消。</summary>
        public CancellationToken Ct => _cts.Token;

        public bool Closing => Volatile.Read(ref _closing) != 0;

        /// <summary>這條連線收過握手（給「改連的主機連不上就忘掉」判斷）。</summary>
        public volatile bool Handshaken;

        public Exception? Failure => _failure;
        public bool Finished => Volatile.Read(ref _finished) != 0;

        public void SetPhase(SessionPhase phase, int timeoutMs)
        {
            Phase = phase;
            Interlocked.Exchange(ref PhaseDeadlineTicks, DateTime.UtcNow.AddMilliseconds(timeoutMs).Ticks);
        }

        public void Touch(ref long clientLastReceive)
        {
            var now = DateTime.UtcNow.Ticks;
            Interlocked.Exchange(ref LastReceiveTicks, now);
            Interlocked.Exchange(ref clientLastReceive, now);
        }

        /// <summary>記下第一個失敗原因（之後的不覆蓋）。</summary>
        public void Fail(Exception ex) => Interlocked.CompareExchange(ref _failure, ex, null);

        /// <summary>從外部（取消、手動重連）要求禮貌關閉；伺服器沒回關閉就在寬限後中止。</summary>
        public void BeginClose()
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0) return;
            _ = Task.Run(async () =>
            {
                await SendCloseAsync(this).ConfigureAwait(false);
                try { await Task.Delay(CloseGraceMs).ConfigureAwait(false); } catch { }
                if (!Finished) Abort();
            });
        }

        public void Abort()
        {
            try { Ws.Abort(); } catch { }
        }

        public void Finish()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            Outbox.Writer.TryComplete();
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            Pending.Clear();
        }
    }

    /// <summary>伺服器要求改連另一台主機（控制訊息 reset）。</summary>
    private sealed class RealtimeResetException : FirebaseException
    {
        public RealtimeResetException(string message) : base(message) { }
    }
}

/// <summary>WebSocket 連線上的一個監聽；對外的樣子和 SSE 的一條串流相同。</summary>
public sealed class RealtimeListen : ISyncSubscription
{
    private readonly FirebaseRealtimeClient _owner;
    private int _disposed;

    internal RealtimeListen(FirebaseRealtimeClient owner, string path, Action<StreamEvent> onEvent, Action<Exception>? onError, Action? onConnected)
    {
        _owner = owner;
        Path = path;
        Segments = FirebaseRealtimeClient.SplitPath(path);
        NormalizedPath = string.Join('/', Segments);
        WirePath = "/" + NormalizedPath;
        OnEvent = onEvent;
        OnError = onError;
        OnConnected = onConnected;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Path { get; }
    internal string[] Segments { get; }
    internal string NormalizedPath { get; }
    internal string WirePath { get; }
    internal Action<StreamEvent> OnEvent { get; }
    internal Action<Exception>? OnError { get; }
    internal Action? OnConnected { get; }

    /// <summary>這個監聽送到了哪一條連線（null＝目前沒在那條連線上監聽）。</summary>
    internal volatile FirebaseRealtimeClient.Session? SentSession;

    internal volatile FirebaseRealtimeClient.ListenState State = FirebaseRealtimeClient.ListenState.Pending;

    internal volatile FirebaseRealtimeClient.CachedStatus? CachedStatus;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public StreamStatus Status => _owner.ComposeStatus(this);

    public void RequestImmediateRetry()
    {
        if (!IsDisposed) _owner.RequestImmediateRetry();
    }

    internal void MarkDisposed() => Interlocked.Exchange(ref _disposed, 1);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _owner.Remove(this);
    }
}
