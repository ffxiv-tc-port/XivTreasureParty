namespace XivTreasureParty.Firebase;

/// <summary>
/// 目前使用中的同步端點。驗證、資料庫、串流三個用戶端每次請求都從這裡即時讀，
/// 所以切換後端不必重建任何服務物件（Mappy 標記橋等訂閱者持有的引用一直有效）。
/// </summary>
/// <remarks>契約：只有在沒有隊伍、沒有串流訂閱時才可以呼叫 <see cref="Set"/>（由 <c>Plugin.TrySwitchBackend</c> 把關）。</remarks>
public sealed class BackendSelector
{
    private volatile BackendEndpoint _current;

    public BackendSelector(BackendEndpoint initial)
    {
        _current = initial;
    }

    public BackendEndpoint Current => _current;

    public bool IsCustom => _current.Kind == BackendKind.Custom;

    public void Set(BackendEndpoint endpoint) => _current = endpoint;
}
