using System;
using Dalamud.Plugin.Services;

namespace XivTreasureParty.Game;

/// <summary>
/// 每個 framework tick 輪詢 TreasureHuntManager 的 rank+spot，偵測到變化就：
///   1. 若 Config.AutoCaptureOnDecode，呼叫 AddTreasurePanel.ApplyDecoded 預選欄位
///   2. 若 Config.AutoOpenMapOnCapture，在遊戲內開啟對應地圖並打旗標
/// 兩個選項彼此獨立；只要其中一項開啟就會偵測。不會自動推送到 Firebase。
/// </summary>
public sealed class HuntAutoCapture : IDisposable
{
    private readonly IFramework _framework;
    private uint _lastRank;
    private ushort _lastSpot;
    private DateTime _nextPollUtc = DateTime.MinValue;
    private bool _running;

    public HuntAutoCapture(IFramework framework)
    {
        _framework = framework;
        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        if (_running) return;
        if (!Plugin.Config.AutoCaptureOnDecode && !Plugin.Config.AutoOpenMapOnCapture) return;
        if (!Plugin.HuntReader.IsAvailable) return;

        var now = DateTime.UtcNow;
        if (now < _nextPollUtc) return;
        _nextPollUtc = now.AddSeconds(1);  // 1 Hz 已經非常夠

        _running = true;
        try
        {
            var read = Plugin.HuntReader.Read();
            if (read == null)
            {
                _lastRank = 0;
                _lastSpot = 0;
                return;
            }

            var (rank, spot) = read.Value;
            if (rank == _lastRank && spot == _lastSpot) return;

            _lastRank = rank;
            _lastSpot = spot;

            var decoded = Plugin.HuntReader.Resolve(rank, spot);
            if (decoded == null) return;

            if (Plugin.Config.AutoCaptureOnDecode)
                Plugin.Window.AddPanel.ApplyDecoded(decoded);

            if (Plugin.Config.AutoOpenMapOnCapture)
            {
                var fakeTreasure = new Party.Models.Treasure
                {
                    MapId = decoded.MapId,
                    Coords = new Party.Models.TreasureCoords { X = decoded.X, Y = decoded.Y }
                };
                TreasureLocator.OpenInGame(fakeTreasure);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[HuntAutoCapture] tick 例外");
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>強制重置 snapshot，下一次 tick 若有值就會重新 apply 一次（例如使用者手動清除後想再觸發）。</summary>
    public void Reset()
    {
        _lastRank = 0;
        _lastSpot = 0;
    }
}
