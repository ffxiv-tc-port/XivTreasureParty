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

    public static FirebaseAuthClient FirebaseAuth { get; private set; } = null!;
    public static FirebaseDatabaseClient FirebaseDb { get; private set; } = null!;
    public static FirebaseStreamClient FirebaseStream { get; private set; } = null!;

    public static PartyService PartyService { get; private set; } = null!;
    public static SyncService SyncService { get; private set; } = null!;
    public static HeartbeatService Heartbeat { get; private set; } = null!;

    public static ChatSender ChatSender { get; private set; } = null!;
    public static TreasureHuntReader HuntReader { get; private set; } = null!;
    public static HuntAutoCapture HuntAutoCapture { get; private set; } = null!;
    public static MapLinkAutoConverter MapLinkAutoConverter { get; private set; } = null!;

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

        FirebaseAuth = new FirebaseAuthClient(FirebaseConfig.ApiKey);
        FirebaseDb = new FirebaseDatabaseClient(FirebaseConfig.DatabaseUrl, FirebaseAuth);
        FirebaseStream = new FirebaseStreamClient(FirebaseConfig.DatabaseUrl, FirebaseAuth);

        PartyService = new PartyService(FirebaseDb, FirebaseAuth);
        SyncService = new SyncService(FirebaseStream);
        Heartbeat = new HeartbeatService(FirebaseDb, PartyService, FirebaseAuth);
        ChatSender = new ChatSender();
        HuntReader = new TreasureHuntReader(gameInterop);
        HuntAutoCapture = new HuntAutoCapture(framework);
        MapLinkAutoConverter = new MapLinkAutoConverter(gameInterop);

        Window = new PluginWindow();
        PluginInterface.UiBuilder.Draw += Window.Draw;
        PluginInterface.UiBuilder.OpenMainUi += () => Window.IsOpen = true;
        PluginInterface.UiBuilder.OpenConfigUi += () => Window.IsOpen = true;

        CommandManager.AddHandler(CommandMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "開啟 XIV 藏寶圖工具小幫手主視窗"
        });

        _ = InitAsync();
    }

    private async System.Threading.Tasks.Task InitAsync()
    {
        try
        {
            await FirebaseAuth.EnsureSignedInAsync().ConfigureAwait(false);

            if (Config.AutoRejoinOnStart && !string.IsNullOrWhiteSpace(Config.LastPartyCode))
            {
                try
                {
                    await PartyService.TryRejoinAsync(Config.LastPartyCode!, Config.Nickname).ConfigureAwait(false);
                    SyncService.Start(PartyService.CurrentPartyCode!);
                    Heartbeat.Start();
                }
                catch (Exception ex)
                {
                    Log.Warning($"自動重新加入隊伍失敗：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化 Firebase 失敗");
        }
    }

    private void OnCommand(string command, string args)
    {
        Window.IsOpen = !Window.IsOpen;
    }

    public void Dispose()
    {
        try { MapLinkAutoConverter.Dispose(); } catch { }
        try { HuntAutoCapture.Dispose(); } catch { }
        try { Heartbeat.Stop(); } catch { }
        try { SyncService.Stop(); } catch { }
        try { FirebaseStream.Dispose(); } catch { }
        try { FirebaseAuth.Dispose(); } catch { }

        CommandManager.RemoveHandler(CommandMain);

        PluginInterface.UiBuilder.Draw -= Window.Draw;
    }
}
