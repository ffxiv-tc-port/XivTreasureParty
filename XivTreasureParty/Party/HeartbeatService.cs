using System;
using System.Threading;
using System.Threading.Tasks;
using XivTreasureParty.Firebase;

namespace XivTreasureParty.Party;

/// <summary>
/// 因 Firebase REST 無 onDisconnect，改用心跳 (每 30 秒寫入 lastSeen) 讓其他成員可判斷線上狀態。
/// 實際「移除」只在使用者手動離開或關閉插件時執行。
/// </summary>
public sealed class HeartbeatService
{
    private readonly FirebaseDatabaseClient _db;
    private readonly PartyService _party;
    private readonly FirebaseAuthClient _auth;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public HeartbeatService(FirebaseDatabaseClient db, PartyService party, FirebaseAuthClient auth)
    {
        _db = db;
        _party = party;
        _auth = auth;
    }

    /// <summary>重開心跳迴圈；插件已開始卸載時不開並回 false。</summary>
    public bool Start() => Plugin.RunIfNotUnloading(StartCore);

    private void StartCore()
    {
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _loop = Task.Run(() => LoopAsync(cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_party.IsInParty)
                    await _party.HeartbeatAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug($"Heartbeat 失敗 (可忽略): {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
