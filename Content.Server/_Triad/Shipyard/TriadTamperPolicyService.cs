using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Content.Server._Triad.Shipyard.Persistence;
using Content.Server.Database;
using Content.Shared._Triad.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Player;

namespace Content.Server._Triad.Shipyard;

public readonly record struct LoadDecision(
    bool Allow,
    TriadShipyardEventType ResolvedEvent,
    string? PopupReasonLocId);

public sealed partial class TriadTamperPolicyService : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private ITriadShipyardKeyStore _keyStore = default!;
    [Dependency] private ITriadShipyardAuditLog _auditLog = default!;
    [Dependency] private ITriadShipyardPermitStore _permitStore = default!;
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private Admin.TriadTamperAdminEuiRegistry _euiRegistry = default!;

    private ISawmill _sawmill = default!;

    // F8 fix: audit events go through a bounded channel + background consumer instead of
    // fire-and-forget _auditLog.RecordAsync calls that previously dropped silently on DB error.
    private const int AuditChannelCapacity = 10_000;
    private const int AuditBatchSize = 64;
    private Channel<TriadShipyardAuditEvent>? _auditChannel;
    private CancellationTokenSource? _auditCts;
    private Task? _auditConsumerTask;

    // Live-feed signal: the background audit consumer flips this after committing a batch; the
    // main-thread Update tick reads and clears it, then pokes open admin panels. Marshaling through
    // Update keeps the EUI StateDirty/QueueStateUpdate calls on the main thread instead of the
    // consumer's thread-pool thread. A set that races just past an Update read simply lands next tick.
    private volatile bool _auditCommittedSinceTick;

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = Logger.GetSawmill("triad.tamper");

        _auditChannel = Channel.CreateBounded<TriadShipyardAuditEvent>(new BoundedChannelOptions(AuditChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropNewest,
            SingleReader = true,
            SingleWriter = false,
        });
        _auditCts = new CancellationTokenSource();
        _auditConsumerTask = Task.Run(() => ConsumeAuditAsync(_auditCts.Token));

        // Seed the own-key and permit caches the import checks read from.
        // Fire-and-forget; failure logs an error but does not block server start.
        _ = BootstrapCachesAsync();

        // Per-player permits are a one-session legacy-onboarding bypass: clear a player's permit on
        // disconnect (admin revoke is the other end condition). This re-adds the disconnect-clear the
        // F15 change removed, which is correct now that permits are per-player, not per-(player, hash).
        _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _playerManager.PlayerStatusChanged -= OnPlayerStatusChanged;

        // Block the tick thread up to 5s to let the audit consumer drain committed-but-unwritten events
        // before exit. This is shutdown-only (nothing else is ticking), so the synchronous wait is
        // intentional: losing queued audit rows on a clean shutdown is worse than a brief stall.
        // Review #9 (WONTFIX).
        _auditChannel?.Writer.TryComplete();
        if (_auditConsumerTask != null)
        {
            if (!_auditConsumerTask.Wait(TimeSpan.FromSeconds(5)))
            {
                _sawmill.Warning("Audit consumer did not drain within 5s; cancelling.");
                _auditCts?.Cancel();
            }
        }
        _auditCts?.Dispose();
        _auditCts = null;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Drain the commit signal on the main thread and tail the live feed for every open panel.
        // Coalesced: many committed batches between ticks collapse into a single fan-out.
        if (!_auditCommittedSinceTick)
            return;

        _auditCommittedSinceTick = false;
        _euiRegistry.NotifyAuditChanged();
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        // Clear a player's permit when their session ends; admin revoke is the other end condition.
        if (e.Session != null && e.NewStatus == SessionStatus.Disconnected)
            _ = _permitStore.RevokeAsync(e.Session.UserId.UserId, default);
    }

    private async Task BootstrapCachesAsync()
    {
        // Seed the own-key authority set from the signing-keys table (every key this server once
        // signed with). After this completes, EvaluateLoad answers "is this our key" from memory;
        // until then IsOwnKey falls back to the database.
        try
        {
            await _keyStore.PopulateOwnKeysAsync(default);
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to populate own-key set: {ex}");
        }

        // F15 fix: seed the in-memory permit cache. After this completes, the EvaluateLoad
        // hot path resolves permit checks from memory. Failure leaves the cache marked
        // un-populated; IsPermittedAsync falls back to per-call DB queries (degraded perf,
        // never wrong answers) while HasPermitFor returns false (conservative - means a
        // legitimately-permitted player would get rejected in enforce mode until the cache
        // populates, but the admin can retry).
        try
        {
            await _permitStore.PopulateAsync(default);
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to populate migration-permit cache: {ex}");
        }
    }

    private async Task ConsumeAuditAsync(CancellationToken ct)
    {
        if (_auditChannel == null)
            return;

        var reader = _auditChannel.Reader;
        var batch = new List<TriadShipyardAuditEvent>(AuditBatchSize);

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < AuditBatchSize && reader.TryRead(out var ev))
                    batch.Add(ev);
                if (batch.Count == 0)
                    continue;
                await WriteBatchWithRetryAsync(batch, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Audit consumer loop crashed: {ex}");
        }
    }

    private async Task WriteBatchWithRetryAsync(IReadOnlyList<TriadShipyardAuditEvent> batch, CancellationToken ct)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _auditLog.RecordBatchAsync(batch, ct).ConfigureAwait(false);
                // New rows are now durable; flag the main-thread Update tick to refresh open panels.
                _auditCommittedSinceTick = true;
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == maxAttempts)
                {
                    _sawmill.Error(
                        $"Audit batch insert failed after {attempt} attempts; dropping {batch.Count} events. {ex}");
                    return;
                }
                var delayMs = 100 * (1 << attempt);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }
    }

    private void Enqueue(TriadShipyardAuditEvent ev)
    {
        var channel = _auditChannel;
        if (channel == null)
        {
            _sawmill.Warning(
                $"Audit channel not initialized; dropping event (EventType={ev.EventType}, Player={ev.PlayerUserId}).");
            return;
        }

        if (!channel.Writer.TryWrite(ev))
        {
            _sawmill.Warning(
                $"Audit channel full ({AuditChannelCapacity}); dropping event (EventType={ev.EventType}, Player={ev.PlayerUserId}).");
        }
    }

    private TamperMode ResolveMode()
    {
        var raw = _cfg.GetCVar(TriadCCVars.TamperMode);
        return raw switch
        {
            "off" => TamperMode.Off,
            "enforce" => TamperMode.Enforce,
            _ => TamperMode.Notify,
        };
    }

    public LoadDecision EvaluateLoad(AuthenticatedShipFile envelope, NetUserId player, string? shipName)
    {
        var mode = ResolveMode();

        if (mode == TamperMode.Off)
            return new LoadDecision(true, TriadShipyardEventType.LoadVerifiedTrusted, null);

        var hasSignature = envelope.GetInstancePublicKeyInfo() != null;
        var signatureValid = hasSignature && envelope.IsShipSigned();
        var pubkey = envelope.GetInstancePublicKeyInfo();
        // Authority is a key this server once signed saves with, not an admin trust flag.
        // "Valid but foreign" = a player forged their own keypair to sign a ship; it is rejected
        // under enforce and only ever flagged (never trusted) under notify.
        var ours = signatureValid && pubkey != null && _keyStore.IsOwnKey(SHA256.HashData(pubkey));

        // Ships signed by our own key are always allowed and never logged as migrated.
        if (ours)
            return new LoadDecision(true, TriadShipyardEventType.LoadVerifiedTrusted, null);

        // In notify mode every state is allowed and logged with the descriptive event. A valid
        // signature from a key that isn't ours is the forged-key flare: keep LoadVerifiedUntrusted
        // as its label here (notify only).
        if (mode == TamperMode.Notify)
        {
            var ev = !hasSignature ? TriadShipyardEventType.LoadUnsigned
                : !signatureValid ? TriadShipyardEventType.LoadInvalidSignature
                : TriadShipyardEventType.LoadVerifiedUntrusted;
            return new LoadDecision(true, ev, null);
        }

        // Enforce. Permit is the per-player legacy-onboarding bypass: a permitted player may import a
        // non-our-key ship, which is filed into the drydock like any other and never re-signed. The
        // LoadMigrated label records that the permit let it through. HasPermitFor reads the cache.
        if (_permitStore.HasPermitFor(player.UserId))
            return new LoadDecision(true, TriadShipyardEventType.LoadMigrated, null);

        // Reject reasons get distinct event types so the admin feed can name why the load was blocked.
        if (!hasSignature)
            return new LoadDecision(false, TriadShipyardEventType.LoadRejectedUnsigned, "shipyard-tamper-blocked-unsigned");
        if (!signatureValid)
            return new LoadDecision(false, TriadShipyardEventType.LoadRejectedInvalidSignature, "shipyard-tamper-blocked-invalid-signature");

        // Signature valid but not our key (forged keypair). Always rejected under enforce.
        return new LoadDecision(false, TriadShipyardEventType.LoadRejectedForeignKey, "shipyard-tamper-blocked-untrusted-key");
    }

    /// <summary>
    /// Whether the tamper check is enforcing. On import it gates two things: capping the offered list
    /// at the account's remaining budget, and retiring the player's local file to backup, which off
    /// and notify skip so a test box can rehearse without eating a save that has to work elsewhere.
    /// The hash burn and the budget refusal themselves are the consume ledger's, and answer in every
    /// mode.
    /// </summary>
    public bool IsEnforcing() => ResolveMode() == TamperMode.Enforce;

    /// <summary>
    /// Whether a legacy save should be offered for import, judged from the envelope's public key
    /// alone. The client cannot hash its own files - System.Security.Cryptography is not on the
    /// engine's sandbox whitelist - so a manifest carries the key and signature it claims and the
    /// server hashes the key here.
    ///
    /// <para>This decides listing, never authority. A forged envelope carrying one of our public
    /// keys is listed and then fails <see cref="EvaluateLoad"/> at import, when the payload is
    /// actually present to verify against. Deciding the other way would mean shipping every save on
    /// disk up the wire on tab open.</para>
    /// </summary>
    public bool ShouldOfferForImport(byte[]? publicKey, NetUserId player)
    {
        var mode = ResolveMode();

        // Off and notify permit every load, so they permit every import: a server that is not
        // enforcing is rehearsing, and a list filtered tighter than the load path would make the
        // test box disagree with the live one about which ships exist.
        if (mode != TamperMode.Enforce)
            return true;

        if (publicKey != null && _keyStore.IsOwnKey(SHA256.HashData(publicKey)))
            return true;

        // The permit is the same straggler bypass EvaluateLoad honours; those ships file into the
        // drydock as they are, with no re-signing.
        return _permitStore.HasPermitFor(player.UserId);
    }

    public Task RecordLoadAsync(
        AuthenticatedShipFile envelope,
        NetUserId player,
        string? playerName,
        string? shipName,
        TriadShipyardEventType eventType,
        int? loadTimeAppraisal,
        int? roundId,
        string? serverName,
        string? vesselId,
        string? mapId,
        string? sourceFilePath,
        string? deedHolderEntity)
    {
        if (ResolveMode() == TamperMode.Off)
            return Task.CompletedTask;

        Enqueue(new TriadShipyardAuditEvent
        {
            At = DateTime.UtcNow,
            EventType = eventType,
            PlayerUserId = player.UserId,
            PlayerName = playerName,
            ShipName = shipName,
            ShipHash = envelope.GetHash(),
            PublicKey = envelope.GetInstancePublicKeyInfo(),
            SaveTimeAppraisal = envelope.Appraisal,
            LoadTimeAppraisal = loadTimeAppraisal,
            RoundId = roundId,
            ServerName = serverName,
            VesselId = vesselId,
            MapId = mapId,
            SourceFilePath = sourceFilePath,
            DeedHolderEntity = deedHolderEntity,
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Record an admin permit action (grant/revoke) into the audit feed. Routed through the same
    /// channel as load events so it batches and triggers the live-update fan-out. The acting
    /// admin lands in AdminUserId; the target player + ship hash identify what the permit covers.
    /// </summary>
    public void RecordPermitAction(
        TriadShipyardEventType eventType,
        Guid targetPlayer,
        string? targetPlayerName,
        Guid adminUserId,
        int? roundId)
    {
        Enqueue(new TriadShipyardAuditEvent
        {
            At = DateTime.UtcNow,
            EventType = eventType,
            PlayerUserId = targetPlayer,
            PlayerName = targetPlayerName,
            // Per-player permits aren't ship-specific; the audit ShipHash column is non-null, so
            // permit-action rows carry an empty hash.
            ShipHash = Array.Empty<byte>(),
            AdminUserId = adminUserId,
            RoundId = roundId,
        });
    }
}

internal enum TamperMode
{
    Off,
    Notify,
    Enforce,
}
