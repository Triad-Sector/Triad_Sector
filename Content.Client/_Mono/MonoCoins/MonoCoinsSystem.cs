using Content.Shared._Mono.MonoCoins;
using Robust.Shared.Network;

namespace Content.Client._Mono.MonoCoins;

/// <summary>
/// Client-side system for handling MonoCoins balance requests and responses.
/// </summary>
public sealed partial class MonoCoinsSystem : EntitySystem
{
    [Dependency] private INetManager _netManager = default!;

    /// <summary>
    /// The last known MonoCoins balance. -1 indicates balance hasn't been fetched yet.
    /// </summary>
    private int _lastKnownBalance = -1;

    /// <summary>
    /// Event raised when MonoCoins balance is updated.
    /// </summary>
    public event Action<int>? BalanceUpdated;

    public override void Initialize()
    {
        base.Initialize();
        
        // Subscribe to network messages
        SubscribeNetworkEvent<MonoCoinsBalanceResponseMessage>(OnMonoCoinsBalanceResponse);
    }

    /// <summary>
    /// Handles MonoCoins balance response from server.
    /// </summary>
    private void OnMonoCoinsBalanceResponse(MonoCoinsBalanceResponseMessage message)
    {
        _lastKnownBalance = message.Balance;
        BalanceUpdated?.Invoke(_lastKnownBalance);
    }

    /// <summary>
    /// Requests the current MonoCoins balance from the server.
    /// </summary>
    public void RequestBalance()
    {
        var message = new RequestMonoCoinsBalanceMessage();
        RaiseNetworkEvent(message);
    }

    /// <summary>
    /// Gets the last known MonoCoins balance.
    /// Returns -1 if balance hasn't been fetched yet.
    /// </summary>
    public int GetLastKnownBalance()
    {
        return _lastKnownBalance;
    }
}
