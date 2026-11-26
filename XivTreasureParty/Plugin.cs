using System;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivTreasureParty.Firebase;
using XivTreasureParty.Game;
using XivTreasureParty.Party;
using XivTreasureParty.UI;

namespace XivTreasureParty;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "XIV 藏寶圖工具小幫手";

    private const string CommandMain = "/pth";

    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    public static ICommandManager CommandManager { get; private set; } = null!;
    public static IClientState ClientState { get; private set; } = null!;

    // API13 把 IClientState.LocalPlayer 標為過時，替代品是 IObjectTable.LocalPlayer。
    // Dalamud 端 ClientState.LocalPlayer 本身就是 => this.objectTable.LocalPlayer 的純轉發。
    public static IObjectTable ObjectTable { get; private set; } = null!;
    public static IDataManager DataManager { get; private set; } = null!;
    public static IPluginLog Log { get; private set; } = null!;
    public static IChatGui ChatGui { get; private set; } = null!;
    public static IFramework Framework { get; private set; } = null!;
    public static IGameGui GameGui { get; private set; } = null!;

    public static Configuration Config { get; private set; } = null!;

    /// <summary>目前的同步端點；切換只換它裡面的端點，下面的服務物件永遠不重建。</summary>
    public static BackendSelector Backend { get; private set; } = null!;

    public static FirebaseAuthClient FirebaseAuth { get; private set; } = null!;
    public static FirebaseDatabaseClient FirebaseDb { get; private set; } = null!;
    public static FirebaseStreamClient FirebaseStream { get; private set; } = null!;
    public static FirebaseRealtimeClient FirebaseRealtime { get; private set; } = null!;

    public static PartyService PartyService { get; private set; } = null!;
    public static SyncService SyncService { get; private set; } = null!;
    public static HeartbeatService Heartbeat { get; private set; } = null!;

    public static ChatSender ChatSender { get; private set; } = null!;
    public static TreasureHuntReader HuntReader { get; private set; } = null!;
    public static HuntAutoCapture HuntAutoCapture { get; private set; } = null!;
    public static MapLinkAutoConverter MapLinkAutoConverter { get; private set; } = null!;
    public static MappyMarkerBridge MappyMarkers { get; private set; } = null!;

    public static PluginWindow Window { get; private set; } = null!;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IDataManager dataManager,
        IPluginLog pluginLog,
        IChatGui chatGui,
        IFramework framework,
        IGameInteropProvider gameInterop,
        IGameGui gameGui,
        IObjectTable objectTable)
    {
        PluginInterface = pluginInterface;
        CommandManager = commandManager;
        ClientState = clientState;
        ObjectTable = objectTable;
        DataManager = dataManager;
        Log = pluginLog;
        ChatGui = chatGui;
        Framework = framework;
        GameGui = gameGui;

        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        string? backendError = null;
        var initial = BackendEndpoint.Cycleapple;
        if (Config.Backend == BackendKind.Custom)
        {
            initial = BackendEndpoint.TryCreateCustom(Config.CustomServerUrl, Config.CustomApiKey, Config.CustomAuthUrl, out var err)
                      ?? BackendEndpoint.Cycleapple;
            if (initial.Kind != BackendKind.Custom)
                backendError = $"設定選的是臨時伺服器，但網址不能用（{err}），這次先用 cycleapple；請到「進階：臨時伺服器」修正";
        }
        Backend = new BackendSelector(initial);

        FirebaseAuth = new FirebaseAuthClient(Backend);
        FirebaseDb = new FirebaseDatabaseClient(Backend, FirebaseAuth);
        FirebaseStream = new FirebaseStreamClient(() => Backend.Current.DatabaseUrl, FirebaseAuth);
        FirebaseRealtime = new FirebaseRealtimeClient(() => Backend.Current.DatabaseUrl, FirebaseAuth);

        PartyService = new PartyService(FirebaseDb, FirebaseAuth, Backend);
        SyncService = new SyncService(FirebaseStream, realtime: FirebaseRealtime);
        Heartbeat = new HeartbeatService(FirebaseDb, PartyService, FirebaseAuth);
        ChatSender = new ChatSender();
        HuntReader = new TreasureHuntReader(gameInterop);
        HuntAutoCapture = new HuntAutoCapture(framework);
        MapLinkAutoConverter = new MapLinkAutoConverter(gameInterop);
        MappyMarkers = new MappyMarkerBridge(framework, SyncService);

        Window = new PluginWindow();
        PluginInterface.UiBuilder.Draw += Window.Draw;
        PluginInterface.UiBuilder.OpenMainUi += () => Window.IsOpen = true;
        PluginInterface.UiBuilder.OpenConfigUi += () => Window.IsOpen = true;

        CommandManager.AddHandler(CommandMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "開啟 XIV 藏寶圖工具小幫手主視窗"
        });

        // 載入時不碰網路：登入延到建立／加入隊伍那一刻（FirebaseDatabaseClient 每次請求前自己確保登入）。
        if (backendError != null)
        {
            AutoRejoinError = backendError;
            Log.Warning(backendError);
        }
        else if (Config.AutoRejoinOnStart && !string.IsNullOrWhiteSpace(Config.GetLastPartyCode(Backend.Current.Kind)))
        {
            AutoRejoinInProgress = true;
            _ = System.Threading.Tasks.Task.Run(AutoRejoinAsync);
        }
    }

    /// <summary>啟動時自動重新加入進行中；UI 用它擋住同時手動建立／加入。</summary>
    public static volatile bool AutoRejoinInProgress;

    /// <summary>最近一次啟動時自動重新加入失敗的原因（給隊伍面板顯示）；成功、或之後手動建立／加入成功時清成 null。</summary>
    public static volatile string? AutoRejoinError;

    private static volatile bool _unloading;

    // 契約：Dispose 只在這把鎖內把 _unloading 設成 true；串流與心跳的 Start 都在鎖內判旗標後才開，
    // 所以 Dispose 拿到鎖之後不會再有新的 Start。鎖內只建立工作與取消，不做 I/O、不等網路。
    private static readonly object StartGate = new();

    /// <summary>Dispose 已開始；背景工作等完網路後先看它，再決定要不要開串流與心跳。</summary>
    public static bool IsUnloading => _unloading;

    /// <summary>插件沒在卸載時才執行 <paramref name="start"/>（在 <c>StartGate</c> 內）；卸載中回 false、什麼都不做。</summary>
    internal static bool RunIfNotUnloading(Action start)
    {
        lock (StartGate)
        {
            if (_unloading) return false;
            start();
            return true;
        }
    }

    /// <summary>開串流與心跳，兩者同在一次閘門內（要嘛都開、要嘛都不開）；卸載中回 false。</summary>
    /// <summary>
    /// 換同步後端。只有沒在隊伍裡、沒在自動重新加入、沒有任何串流訂閱時才換，
    /// 所以不會同時連兩邊，也不會有請求拿 A 的憑證打 B。
    /// </summary>
    internal static bool TrySwitchBackend(BackendEndpoint endpoint, out string reason)
    {
        lock (StartGate)
        {
            if (_unloading) { reason = "插件正在卸載"; return false; }
            if (PartyService.IsInParty) { reason = "先離開隊伍才能切換"; return false; }
            if (AutoRejoinInProgress) { reason = "正在自動重新加入上次的隊伍，請稍候"; return false; }
            if (SyncService.HasSubscriptions || FirebaseStream.ActiveSubscriptions.Count > 0 || FirebaseRealtime.ActiveListenCount > 0)
            { reason = "同步連線還沒關閉，請稍候再試"; return false; }
            if (Backend.Current.SlotId != endpoint.SlotId)
            {
                Backend.Set(endpoint);
                Log.Information(endpoint.Kind == BackendKind.Custom ? "同步後端已切換到臨時伺服器" : "同步後端已切換回 cycleapple");
            }
            reason = string.Empty;
            return true;
        }
    }

    internal static bool TryStartSession(string partyCode) =>
        RunIfNotUnloading(() =>
        {
            SyncService.Start(partyCode);
            Heartbeat.Start();
        });

    private static async System.Threading.Tasks.Task AutoRejoinAsync()
    {
        try
        {
            var kind = Backend.Current.Kind;
            var lastCode = Config.GetLastPartyCode(kind);
            if (!string.IsNullOrWhiteSpace(lastCode))
            {
                try
                {
                    var nickname = Config.GetNickname(kind);
                    await PartyService.TryRejoinAsync(lastCode, string.IsNullOrWhiteSpace(nickname) ? null : nickname).ConfigureAwait(false);
                    // 等回應期間插件已卸載：不再開串流與心跳，否則重連迴圈會在卸載後繼續跑。
                    if (!TryStartSession(PartyService.CurrentPartyCode!)) return;
                    AutoRejoinError = null;
                    Log.Information($"啟動時自動重新加入隊伍 {lastCode} 成功");
                }
                catch (PartyGoneException ex)
                {
                    AutoRejoinError = $"自動重新加入隊伍 {lastCode} 失敗：{ex.Message}";
                    // 只在設定檔仍記著這一隊時才清：等待回應期間使用者手動加入的新隊伍不能被一起忘掉。
                    if (Config.GetLastPartyCode(kind) == lastCode)
                    {
                        Config.SetLastPartyCode(kind, null);
                        Config.Save();
                        Log.Info($"自動重新加入隊伍 {lastCode} 失敗：{ex.Message}；已清除記住的隊伍代碼，之後啟動不再重試");
                    }
                    else
                    {
                        Log.Info($"自動重新加入隊伍 {lastCode} 失敗：{ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    AutoRejoinError = $"自動重新加入隊伍 {lastCode} 失敗：{ex.Message}";
                    Log.Warning($"自動重新加入隊伍失敗：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "啟動時自動重新加入隊伍失敗");
        }
        finally
        {
            AutoRejoinInProgress = false;
        }
    }

    private void OnCommand(string command, string args)
    {
        Window.IsOpen = !Window.IsOpen;
    }

    public void Dispose()
    {
        lock (StartGate) _unloading = true;
        try { MappyMarkers.Dispose(); } catch { }
        try { MapLinkAutoConverter.Dispose(); } catch { }
        try { HuntAutoCapture.Dispose(); } catch { }
        try { Heartbeat.Stop(); } catch { }
        try { SyncService.Stop(); } catch { }
        try { FirebaseStream.Dispose(); } catch { }
        try { FirebaseRealtime.Dispose(); } catch { }
        try { FirebaseAuth.Dispose(); } catch { }

        CommandManager.RemoveHandler(CommandMain);

        PluginInterface.UiBuilder.Draw -= Window.Draw;
    }
}
