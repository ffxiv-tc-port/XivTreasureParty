using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using XivTreasureParty.Firebase;
using XivTreasureParty.Party.Models;

namespace XivTreasureParty.Party;

/// <summary>
/// 對應網頁版 js/party/party-service.js 的核心功能。
/// 資料結構完全沿用網頁版 path:
///   parties/{CODE}/meta
///   parties/{CODE}/members/{uid}
///   parties/{CODE}/treasures/{pushKey}
/// </summary>
public sealed class PartyService
{
    public const int MaxMembers = 8;
    public const int PartyExpiryHours = 12;

    private readonly FirebaseDatabaseClient _db;
    private readonly FirebaseAuthClient _auth;

    public string? CurrentPartyCode { get; private set; }
    public string? CurrentUserId { get; private set; }
    public string? Nickname { get; private set; }
    public long? ExpiresAt { get; private set; }
    public bool IsLeader { get; private set; }
    public bool OrderLocked { get; private set; }

    public bool IsInParty => !string.IsNullOrEmpty(CurrentPartyCode);

    public PartyService(FirebaseDatabaseClient db, FirebaseAuthClient auth)
    {
        _db = db;
        _auth = auth;
    }

    public async Task<string> CreatePartyAsync(string? nickname = null)
    {
        await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        var uid = _auth.LocalId ?? throw new Exception("使用者未登入");

        string code = "";
        for (var attempt = 0; attempt < 10; attempt++)
        {
            code = PartyCodeGenerator.Generate();
            if (!await _db.ExistsAsync($"parties/{code}").ConfigureAwait(false)) break;
            code = "";
        }
        if (string.IsNullOrEmpty(code))
            throw new Exception("無法生成唯一的隊伍代碼，請稍後再試");

        Nickname = ResolveNickname(nickname, uid);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(PartyExpiryHours).ToUnixTimeMilliseconds();

        var payload = new Dictionary<string, object?>
        {
            ["meta"] = new Dictionary<string, object?>
            {
                ["createdAt"] = ServerTimestamp.Instance,
                ["createdBy"] = uid,
                ["expiresAt"] = expiresAt
            },
            ["members"] = new Dictionary<string, object?>
            {
                [uid] = new Dictionary<string, object?>
                {
                    ["joinedAt"] = ServerTimestamp.Instance,
                    ["nickname"] = Nickname,
                    ["isLeader"] = true,
                    ["lastSeen"] = ServerTimestamp.Instance
                }
            },
            ["treasures"] = new Dictionary<string, object?>()
        };

        await _db.SetAsync($"parties/{code}", payload).ConfigureAwait(false);

        CurrentPartyCode = code;
        CurrentUserId = uid;
        ExpiresAt = expiresAt;
        IsLeader = true;
        OrderLocked = false;
        PersistState();

        Plugin.Log.Info($"隊伍建立成功：{code}");
        return code;
    }

    public async Task JoinPartyAsync(string partyCode, string? nickname = null)
    {
        partyCode = partyCode.Trim().ToUpperInvariant();
        if (!PartyCodeGenerator.IsValidFormat(partyCode))
            throw new Exception("隊伍代碼格式不正確");

        await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        var uid = _auth.LocalId ?? throw new Exception("使用者未登入");

        // GetAsync 回 null 只發生在 2xx 且內容是 null／空白（節點不存在）；認證失敗、網路錯誤都擲例外。
        var root = await _db.GetAsync<PartyRoot>($"parties/{partyCode}").ConfigureAwait(false);
        if (root == null)
            throw new PartyGoneException("找不到此隊伍，請確認代碼是否正確");

        if (root.Meta?.ExpiresAt is { } exp && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > exp)
        {
            try { await _db.RemoveAsync($"parties/{partyCode}").ConfigureAwait(false); } catch { }
            throw new PartyGoneException("此隊伍已過期");
        }

        var memberCount = root.Members?.Count ?? 0;
        if (memberCount >= MaxMembers && !(root.Members?.ContainsKey(uid) ?? false))
            throw new Exception($"隊伍已滿 ({MaxMembers}/{MaxMembers})");

        Nickname = ResolveNickname(nickname, uid);

        var member = new Dictionary<string, object?>
        {
            ["joinedAt"] = ServerTimestamp.Instance,
            ["nickname"] = Nickname,
            ["isLeader"] = root.Members?.ContainsKey(uid) == true && (root.Members[uid].IsLeader ?? false),
            ["lastSeen"] = ServerTimestamp.Instance
        };
        await _db.SetAsync($"parties/{partyCode}/members/{uid}", member).ConfigureAwait(false);

        CurrentPartyCode = partyCode;
        CurrentUserId = uid;
        ExpiresAt = root.Meta?.ExpiresAt;
        IsLeader = (bool)(member["isLeader"] ?? false);
        OrderLocked = root.Meta?.OrderLocked ?? false;
        PersistState();

        Plugin.Log.Info($"已加入隊伍：{partyCode}");
    }

    public Task TryRejoinAsync(string partyCode, string? nickname = null) =>
        JoinPartyAsync(partyCode, nickname);

    public async Task LeavePartyAsync()
    {
        if (!IsInParty) return;
        var code = CurrentPartyCode!;
        var uid = CurrentUserId!;

        try { await _db.RemoveAsync($"parties/{code}/members/{uid}").ConfigureAwait(false); }
        catch (Exception ex) { Plugin.Log.Warning($"移除成員失敗：{ex.Message}"); }

        try
        {
            var remaining = await _db.GetAsync<Dictionary<string, PartyMember>>($"parties/{code}/members").ConfigureAwait(false);
            if (remaining == null || remaining.Count == 0)
            {
                await _db.RemoveAsync($"parties/{code}").ConfigureAwait(false);
                Plugin.Log.Info($"隊伍 {code} 已解散");
            }
        }
        catch (Exception ex) { Plugin.Log.Warning($"檢查剩餘成員失敗：{ex.Message}"); }

        ClearState();
        Plugin.Log.Info($"已離開隊伍：{code}");
    }

    public async Task<string> AddTreasureAsync(Treasure treasure)
    {
        EnsureInParty();
        var code = CurrentPartyCode!;
        var uid = CurrentUserId!;

        var treasures = await _db.GetAsync<Dictionary<string, Treasure>>($"parties/{code}/treasures").ConfigureAwait(false);
        var maxOrder = 0;
        if (treasures != null)
        {
            foreach (var t in treasures.Values)
                if (t.Order > maxOrder) maxOrder = t.Order;
        }
        var order = maxOrder + 1;

        var payload = TreasureFactory.BuildNewTreasurePayload(treasure, order, uid, Nickname ?? "");
        var key = await _db.PushAsync($"parties/{code}/treasures", payload).ConfigureAwait(false);

        // Optimistic local add：避免 SSE 延遲/靜默斷線時 UI 看不到剛新增的圖。
        // 過去用戶回報「按下加入清單，狀態顯示已加入但右側清單沒有」就是這個情境。
        Plugin.SyncService.UpsertTreasureLocal(key, new Treasure
        {
            Id = treasure.Id,
            Coords = new TreasureCoords { X = treasure.Coords.X, Y = treasure.Coords.Y },
            MapId = treasure.MapId,
            GradeItemId = treasure.GradeItemId,
            PartySize = treasure.PartySize,
            AddedBy = uid,
            AddedByNickname = Nickname ?? "",
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Order = order,
            Completed = false,
            Player = Nickname,
        });

        // 延長隊伍過期時間
        var newExpires = DateTimeOffset.UtcNow.AddHours(PartyExpiryHours).ToUnixTimeMilliseconds();
        await _db.SetAsync($"parties/{code}/meta/expiresAt", newExpires).ConfigureAwait(false);
        ExpiresAt = newExpires;

        Plugin.Log.Debug($"新增藏寶圖 key={key}");
        return key;
    }

    public async Task RemoveTreasureAsync(string firebaseKey)
    {
        EnsureInParty();
        await _db.RemoveAsync($"parties/{CurrentPartyCode}/treasures/{firebaseKey}").ConfigureAwait(false);
        // Optimistic local remove，同 Add 的理由
        Plugin.SyncService.RemoveTreasureLocal(firebaseKey);
    }

    public async Task ToggleTreasureCompleteAsync(string firebaseKey)
    {
        EnsureInParty();
        var path = $"parties/{CurrentPartyCode}/treasures/{firebaseKey}/completed";
        var current = await _db.GetAsync<bool?>(path).ConfigureAwait(false) ?? false;
        await _db.SetAsync(path, !current).ConfigureAwait(false);
    }

    public Task UpdateTreasureNoteAsync(string firebaseKey, string note)
    {
        EnsureInParty();
        return _db.SetAsync($"parties/{CurrentPartyCode}/treasures/{firebaseKey}/note", note);
    }

    public Task UpdateTreasurePlayerAsync(string firebaseKey, string player)
    {
        EnsureInParty();
        return _db.SetAsync($"parties/{CurrentPartyCode}/treasures/{firebaseKey}/player", player);
    }

    public Task UpdateTreasureOrderAsync(string firebaseKey, int newOrder)
    {
        EnsureInParty();
        return _db.SetAsync($"parties/{CurrentPartyCode}/treasures/{firebaseKey}/order", newOrder);
    }

    public async Task SwapTreasureOrderAsync(string key1, string key2)
    {
        EnsureInParty();
        // 因為 REST 沒有 transaction，先讀兩個 order 再分別 PUT
        // 這裡的競態風險和網頁版相同 (有隊員同時調整時)
        var o1 = await _db.GetAsync<int?>($"parties/{CurrentPartyCode}/treasures/{key1}/order").ConfigureAwait(false);
        var o2 = await _db.GetAsync<int?>($"parties/{CurrentPartyCode}/treasures/{key2}/order").ConfigureAwait(false);
        if (o1 == null || o2 == null) return;
        await _db.UpdateAsync($"parties/{CurrentPartyCode}/treasures", new Dictionary<string, object?>
        {
            [$"{key1}/order"] = o2,
            [$"{key2}/order"] = o1
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 對應網頁版 PartyService.autoOptimizeRoute：地圖分組 + 最近鄰居法。
    /// REST 沒有 transaction，這裡先 GET 全部 → optimize → PATCH 多路徑 order 更新。
    /// 競態風險：別人在 GET~PATCH 之間新增的圖會保留它原本的 order，不會被覆寫。
    /// 本機 dict 也直接更新，避免 SSE 延遲時 UI 看不到順序變化。
    /// </summary>
    public async Task<(double Before, double After, int Reordered)> AutoOptimizeRouteAsync(bool useMapGrouping = true)
    {
        EnsureInParty();
        if (!CanModifyOrder()) throw new Exception("順序已被房主鎖定，無法優化");

        var code = CurrentPartyCode!;
        var treasuresMap = await _db.GetAsync<Dictionary<string, Treasure>>($"parties/{code}/treasures").ConfigureAwait(false);
        if (treasuresMap == null || treasuresMap.Count <= 1)
            return (0, 0, 0);

        // 把 firebaseKey 帶到物件上，optimize 完才知道要更新哪些 path
        foreach (var (k, t) in treasuresMap) t.FirebaseKey = k;

        var byOrder = treasuresMap.Values.OrderBy(t => t.Order).ToList();
        var beforeStats = RouteOptimizer.Analyze(byOrder);

        var optimized = RouteOptimizer.Optimize(byOrder, useMapGrouping);
        var afterStats = RouteOptimizer.Analyze(optimized);

        var updates = new Dictionary<string, object?>();
        var reordered = 0;
        for (var i = 0; i < optimized.Count; i++)
        {
            var newOrder = i + 1;
            var t = optimized[i];
            if (t.Order != newOrder) reordered++;
            updates[$"{t.FirebaseKey}/order"] = newOrder;
            // optimistic 本地 patch — SSE 之後送回同一個值會無作用
            if (Plugin.SyncService.Treasures.TryGetValue(t.FirebaseKey, out var local))
                local.Order = newOrder;
        }

        if (updates.Count > 0)
            await _db.UpdateAsync($"parties/{code}/treasures", updates).ConfigureAwait(false);

        Plugin.SyncService.NotifyTreasuresChanged();

        return (beforeStats.TotalDistance, afterStats.TotalDistance, reordered);
    }

    public async Task ClearCompletedAsync()
    {
        EnsureInParty();
        var treasures = await _db.GetAsync<Dictionary<string, Treasure>>($"parties/{CurrentPartyCode}/treasures").ConfigureAwait(false);
        if (treasures == null) return;
        foreach (var (key, t) in treasures)
        {
            if (t.Completed)
                await _db.RemoveAsync($"parties/{CurrentPartyCode}/treasures/{key}").ConfigureAwait(false);
        }
    }

    public async Task UpdateNicknameAsync(string newNickname)
    {
        EnsureInParty();
        Nickname = newNickname;
        await _db.SetAsync($"parties/{CurrentPartyCode}/members/{CurrentUserId}/nickname", newNickname).ConfigureAwait(false);
        Plugin.Config.Nickname = newNickname;
        Plugin.Config.Save();
    }

    public async Task<bool> ToggleOrderLockAsync()
    {
        EnsureInParty();
        if (!IsLeader) throw new Exception("只有房主可以鎖定 / 解鎖順序");
        var newLocked = !OrderLocked;
        await _db.SetAsync($"parties/{CurrentPartyCode}/meta/orderLocked", newLocked).ConfigureAwait(false);
        OrderLocked = newLocked;
        return newLocked;
    }

    /// <summary>ct 取消＝離開隊伍或卸載：連同還在等登入或等回應的那一次一起中止。</summary>
    public Task HeartbeatAsync(System.Threading.CancellationToken ct = default)
    {
        if (!IsInParty) return Task.CompletedTask;
        return _db.SetAsync($"parties/{CurrentPartyCode}/members/{CurrentUserId}/lastSeen",
                            ServerTimestamp.Instance, ct);
    }

    public bool CanModifyOrder() => IsLeader || !OrderLocked;

    public void SetIsLeader(bool isLeader) => IsLeader = isLeader;
    public void SetOrderLocked(bool locked) => OrderLocked = locked;
    public void SetExpiresAt(long? expiresAt) => ExpiresAt = expiresAt;

    private static string ResolveNickname(string? explicitName, string uid)
    {
        if (!string.IsNullOrWhiteSpace(explicitName)) return explicitName!;
        var auto = XivTreasureParty.Game.PlayerInfo.GetAutoNickname();
        if (!string.IsNullOrWhiteSpace(auto)) return auto!;
        return $"玩家{uid[..Math.Min(4, uid.Length)]}";
    }

    private void EnsureInParty()
    {
        if (!IsInParty) throw new Exception("尚未加入隊伍");
    }

    private void PersistState()
    {
        Plugin.Config.LastPartyCode = CurrentPartyCode;
        if (!string.IsNullOrWhiteSpace(Nickname))
            Plugin.Config.Nickname = Nickname!;
        Plugin.Config.Save();
    }

    private void ClearState()
    {
        CurrentPartyCode = null;
        CurrentUserId = null;
        ExpiresAt = null;
        IsLeader = false;
        OrderLocked = false;
        Plugin.Config.LastPartyCode = null;
        Plugin.Config.Save();
    }
}

/// <summary>
/// 伺服器明確回報隊伍不存在或已過期，重試也不會成功；網路、認證、隊伍已滿都不會擲這個。
/// </summary>
public sealed class PartyGoneException : Exception
{
    public PartyGoneException(string message) : base(message) { }
}
