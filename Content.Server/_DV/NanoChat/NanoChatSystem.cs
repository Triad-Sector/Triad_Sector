using System.Linq;
using Content.Server.Access.Systems;
using Content.Server.Administration.Logs;
using Content.Server.NameIdentifier;
using Content.Shared.GameTicking; // Triad: PlayerSpawnCompleteEvent
using Content.Shared.Inventory; // Triad
using Content.Shared.Database;
using Content.Shared._DV.CartridgeLoader.Cartridges;
using Content.Shared._DV.NanoChat;
using Content.Server.Kitchen.Components; // Triad: microwaves are still server-side here, not Content.Shared.Kitchen
using Content.Shared.NameIdentifier;
using Content.Shared.PDA;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._DV.NanoChat;

/// <summary>
///     Handles NanoChat features that are specific to the server but not related to the cartridge itself.
/// </summary>
public sealed class NanoChatSystem : SharedNanoChatSystem
{
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly NameIdentifierSystem _name = default!;
    [Dependency] private readonly InventorySystem _inventory = default!; // Triad

    private readonly ProtoId<NameIdentifierGroupPrototype> _nameIdentifierGroup = "NanoChat";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NanoChatCardComponent, EntGotInsertedIntoContainerMessage>(OnInserted);
        SubscribeLocalEvent<NanoChatCardComponent, EntGotRemovedFromContainerMessage>(OnRemoved);

        SubscribeLocalEvent<NanoChatCardComponent, MapInitEvent>(OnCardInit);
        SubscribeLocalEvent<NanoChatCardComponent, ComponentShutdown>(OnCardShutdown); // Triad
        SubscribeLocalEvent<NanoChatCardComponent, BeingMicrowavedEvent>(OnMicrowaved, after: [typeof(IdCardSystem)]);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawn); // Triad
    }

    // Triad begin
    /// <summary>
    ///     Marks the ID a player spawns with as a legally issued card, which is what makes it eligible for the
    ///     directory. Everything else (spares in lockers, console printouts, agent IDs) stays off the books.
    /// </summary>
    /// <remarks>
    ///     Deliberately not keyed off station records. A card carries exactly one StationRecordKey and three
    ///     different writers stamp it (station spawn, sector spawn, and the shipyard purchase fallback in
    ///     ShipyardSystem.Consoles), so record keying is too unstable to hang a player-visible directory on.
    /// </remarks>
    private void OnPlayerSpawn(PlayerSpawnCompleteEvent args)
    {
        if (!_inventory.TryGetSlotEntity(args.Mob, "id", out var idUid))
            return;

        // The id slot usually holds a PDA rather than the card itself.
        var cardUid = idUid.Value;
        if (TryComp<PdaComponent>(cardUid, out var pda) && pda.ContainedId is { } containedId)
            cardUid = containedId;

        if (!TryComp<NanoChatCardComponent>(cardUid, out var card))
            return;

        SetRegistered((cardUid, card), true);
    }
    // Triad end

    // Triad: hand the number back when the card dies. Upstream never does this because Delta-V is a station round
    // where ID cards persist; here ships are bought and sold all round and each sale deletes its cards, so without
    // this the 0-9999 pool drains monotonically and eventually every new card gets the silent #0000 fallback.
    private void OnCardShutdown(Entity<NanoChatCardComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.Number is not { } number)
            return;

        _name.ReleaseUniqueName(_nameIdentifierGroup, (int)number);
        ent.Comp.Number = null;
    }

    private void OnInserted(Entity<NanoChatCardComponent> ent, ref EntGotInsertedIntoContainerMessage args)
    {
        if (args.Container.ID != PdaComponent.PdaIdSlotId)
            return;

        ent.Comp.PdaUid = args.Container.Owner;
        Dirty(ent);
    }

    private void OnRemoved(Entity<NanoChatCardComponent> ent, ref EntGotRemovedFromContainerMessage args)
    {
        if (args.Container.ID != PdaComponent.PdaIdSlotId)
            return;

        ent.Comp.PdaUid = null;
        Dirty(ent);
    }

    private void OnMicrowaved(Entity<NanoChatCardComponent> ent, ref BeingMicrowavedEvent args)
    {
        // Skip if the entity was deleted (e.g., by ID card system burning it)
        if (Deleted(ent))
            return;

        if (!TryComp<MicrowaveComponent>(args.Microwave, out var micro) || micro.Broken)
            return;

        var randomPick = _random.NextFloat();

        // Super lucky - erase all messages (10% chance)
        if (randomPick <= 0.10f)
        {
            ent.Comp.Messages.Clear();
            // TODO: these shouldn't be shown at the same time as the popups from IdCardSystem
            // _popup.PopupEntity(Loc.GetString("nanochat-card-microwave-erased", ("card", ent)),
            //     ent,
            //     PopupType.Medium);

            _adminLogger.Add(LogType.TriTalk, // Triad: LogType.Action<LogType.TriTalk
                LogImpact.Medium,
                $"{ToPrettyString(args.Microwave)} erased all messages on {ToPrettyString(ent)}");
        }
        else
        {
            // Scramble random messages for random recipients
            ScrambleMessages(ent);
            // _popup.PopupEntity(Loc.GetString("nanochat-card-microwave-scrambled", ("card", ent)),
            //     ent,
            //     PopupType.Medium);

            _adminLogger.Add(LogType.TriTalk, // Triad: LogType.Action<LogType.TriTalk
                LogImpact.Medium,
                $"{ToPrettyString(args.Microwave)} scrambled messages on {ToPrettyString(ent)}");
        }

        Dirty(ent);
    }

    private void ScrambleMessages(NanoChatCardComponent component)
    {
        foreach (var (recipientNumber, messages) in component.Messages)
        {
            for (var i = 0; i < messages.Count; i++)
            {
                // 50% chance to scramble each message
                if (!_random.Prob(0.5f))
                    continue;

                var message = messages[i];
                message.Content = ScrambleText(message.Content);
                messages[i] = message;
            }

            // 25% chance to reassign the conversation to a random recipient
            if (_random.Prob(0.25f) && component.Recipients.Count > 0)
            {
                var newRecipient = _random.Pick(component.Recipients.Keys.ToList());
                if (newRecipient == recipientNumber)
                    continue;

                if (!component.Messages.ContainsKey(newRecipient))
                    component.Messages[newRecipient] = new List<NanoChatMessage>();

                component.Messages[newRecipient].AddRange(messages);
                component.Messages[recipientNumber].Clear();
            }
        }
    }

    private string ScrambleText(string text)
    {
        var chars = text.ToCharArray();
        var n = chars.Length;

        // Fisher-Yates shuffle of characters
        while (n > 1)
        {
            n--;
            var k = _random.Next(n + 1);
            (chars[k], chars[n]) = (chars[n], chars[k]);
        }

        return new string(chars);
    }

    private void OnCardInit(Entity<NanoChatCardComponent> ent, ref MapInitEvent args)
    {
        if (ent.Comp.Number != null)
            return;

        // Assign a random number
        _name.GenerateUniqueName(ent, _nameIdentifierGroup, out var number);

        // Triad: GenerateUniqueName silently returns 0 when the pool is empty. Upstream assigns it anyway, which
        // would hand every subsequent card the same #0000 and cross-deliver all their messages. Leave the card
        // numberless instead so it simply has no TriTalk service, and say so in the log.
        if (number == 0)
        {
            Log.Warning($"Ran out of TriTalk numbers, {ToPrettyString(ent)} will not be assigned one.");
            return;
        }
        // End Triad

        ent.Comp.Number = (uint)number;
        Dirty(ent);
    }
}
