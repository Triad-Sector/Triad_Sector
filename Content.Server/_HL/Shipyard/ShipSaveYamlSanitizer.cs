using System;
using System.Collections.Generic;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._HL.Shipyard;

/// <summary>
/// Centralized ship-save YAML sanitization logic used by shipyard save/export flows.
/// </summary>
public static class ShipSaveYamlSanitizer
{
    // Implants that should not persist when found inside implanters during ship save.
    private static readonly HashSet<string> BlockedContainedImplantPrototypes = new(StringComparer.Ordinal)
    {
        "DeathRattleImplantColcomm",
        "RadioImplantColcomm",
        "UplinkImplant",
    };

    // Components stripped from all entities during ship-save export.
    // Add new always-remove component types here.
    private static readonly HashSet<string> FilteredTypes = new(StringComparer.Ordinal)
    {
        "Joint",
        "StationMember",
        "NavMap",
        "ShuttleDeed",
        "IFF",
        "LinkedLifecycleGridParent",
        "AccessReader",
        "DeviceNetwork",
        "DeviceNetworkComponent",
        "UserInterface",
        "Docking",
        "ActionGrant",
        "Mind",
        "MindContainer",
        "VendingMachine",
        "Forensics",
    };

    // Fill components that are normally removed from ship saves.
    private static readonly HashSet<string> FillComponentTypes = new(StringComparer.Ordinal)
    {
        "StorageFill",
        "ContainerFill",
        "EntityTableContainerFill",
        "SurplusBundle",
    };

    // Prototype-level exceptions that are allowed to keep fill components.
    // Add prototype IDs here when forced fill removal breaks a specific entity type.
    private static readonly HashSet<string> FillComponentWhitelistPrototypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AirAlarm",
    };

    // Prototype IDs that should never be included in ship exports.
    // Add non-ship entities here to drop them entirely.
    private static readonly HashSet<string> FilteredPrototypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Machines & circuitboards
        // While moving this into a SavingContraband component remove it one by one
        "CommsComputerCircuitboard",
        "ComputerDNAScanner",
        "ComputerExpeditionDiskPrinter",
        "ComputerFundingAllocation",
        "ComputerPsionicsRecords",
        "ComputerRoboticsControl",
        "ComputerShuttleRecords",
        "ComputerTabletopShuttleAntag",
        "DnaScannerConsoleComputerCircuitboard",
        "IDComputerCircuitboard",
        "StationAiUploadComputer",
        // Vending machines
        "DEBUGVendingMachineAmmoBoxes",
        "DEBUGVendingMachineMagazines",
        "DEBUGVendingMachineRangedWeapons",
        "VendingMachineAmmoPOI",
        "VendingMachineAstroVendPOI",
        "VendingMachineBoozePOI",
        "VendingMachineBountyVendPOI",
        "VendingMachineCigsPOI",
        "VendingMachineEngivendPOI",
        "VendingMachineExpeditionaryFlatpackVend",
        "VendingMachineFlatpackVend",
        "VendingMachineFuelVend",
        "VendingMachineGamesPOI",
        "LessLethalVendingMachinePOI",
        "VendingMachineMediDrobePOI",
        "VendingMachineMercVend",
        "VendingMachinePickNPackPOI",
        "VendingMachinePottedPlantVendPOI",
        "VendingMachineSalvagePOI",
        "VendingMachineSyndieContraband",
        "VendingMachineTankDispenserEVAPOI",
        "VendingMachineVendomatPOI",
        "VendingMachineYouToolPOI",
        // Everything else
        "PortalBlue",
        "PortalRed",
        "ReactorGasPipe",
    };

    // Entity-level exclusion by component signature.
    // If an entity has any of these components it is removed from export,
    // unless allowed by ComponentExclusionExceptions below.
    private static readonly HashSet<string> FilteredEntityByComponentTypes = new(StringComparer.Ordinal)
    {
        "CommunicationsConsole",
        "ContrabandPalletConsole",
        "CriminalRecordsConsole",
        "DnaSequenceInjector",
        "DoorRemote",
        "EmergencyShuttleConsole",
        // "GeneralStationRecordConsole",
        "GeneticAnalyzer",
        "Ghost",
        "GhostRole",
        "HumanoidAppearance",
        "IdCard",
        "IdCardConsole",
        "MarketConsole",
        "NFCargoOrderConsole",
        "Pda",
        "ShipyardConsole",
        "Store",
    };

    // Component exclusion exceptions: keep entity when both component and its paired exception component exist.
    private static readonly Dictionary<string, string> ComponentExclusionExceptions = new(StringComparer.Ordinal)
    {
        ["ShipyardConsole"] = "ShipyardListing",
    };

    // Triad: serialized entity references outside ContainerContainer and Storage, which are the only
    // two the prune below originally knew about. Anything dropped during sanitation, such as a mob
    // filtered out by HumanoidAppearance while still buckled to a chair, left its uid behind in these
    // fields and the loader then reported an invalid EntityUid reference for that component.
    //
    // Unordered sets and lists: the entry is removed outright.
    private static readonly Dictionary<string, string> UidSetFields = new(StringComparer.Ordinal)
    {
        ["EmbeddedContainer"] = "embeddedObjects",
        ["Strap"] = "buckledEntities",
        ["ShipGuestAccess"] = "guestIdCards",
        ["FactionException"] = "ignored",
        ["TargetSeekerAlertGrid"] = "alerters",
    };

    // Components carrying a second uid collection, since the table above is keyed by component name.
    private static readonly Dictionary<string, string> UidSetFieldsSecondary = new(StringComparer.Ordinal)
    {
        ["ShipGuestAccess"] = "guestCyborgs",
        ["FactionException"] = "hostiles",
    };

    // Positional sequences: the entry is nulled in place, never removed. A revolver's ammo slots are
    // indexed by cylinder position and the component asserts the count matches its capacity on init,
    // so shortening the sequence would rotate every remaining round onto the wrong chamber.
    private static readonly Dictionary<string, string> UidSlotFields = new(StringComparer.Ordinal)
    {
        ["RevolverAmmoProvider"] = "ammoSlots",
    };

    // Single optional references: the field is nulled. Only nullable fields belong here.
    // Action entities live in the acting mob's container, and the mob is not what gets saved, so these
    // are dangling by construction on any ship that had someone holding the item when it was saved.
    private static readonly Dictionary<string, string[]> UidScalarFields = new(StringComparer.Ordinal)
    {
        ["EmbeddableProjectile"] = ["embeddedIntoUid"],
        ["CombatMode"] = ["combatToggleActionEntity"],
        ["HealOnBuckle"] = ["sleepAction"],
        ["HandheldLight"] = ["toggleActionEntity", "selfToggleActionEntity"],
        ["ShuttleConsoleJobSlots"] = ["owningStation"],
        ["StoreRefund"] = ["storeEntity"],
        ["Jukebox"] = ["audioStream"],
        ["Organ"] = ["body", "originalBody"],
    };

    // Non-nullable references, where nulling the field would just trade one load error for another.
    // The reference is the component's whole reason to exist, so if it points at nothing the component
    // is meaningless and goes with it. VendingMachinePurchase records which grid bought from a machine;
    // a purchase belonging to a grid that is not in the save cannot be acted on.
    private static readonly Dictionary<string, string> UidRequiredFieldsDropComponent = new(StringComparer.Ordinal)
    {
        ["VendingMachinePurchase"] = "purchaseGrid",
    };

    public static void SanitizeShipSaveNode(MappingDataNode root, IPrototypeManager prototypeManager)
    {
        // Keep serialized nullspace empty so ship exports stay scoped to the grid payload.
        try
        {
            root["nullspace"] = new SequenceDataNode();
        }
        catch
        {
            // Best effort: if nullspace cannot be overwritten, continue with entity-level sanitation.
        }

        if (!root.TryGet("entities", out SequenceDataNode? protoSeq) || protoSeq == null)
            return;

        // Track entity UIDs removed during sanitation so we can prune stale container/storage references.
        var removedEntityUids = new HashSet<string>(StringComparer.Ordinal);
        var blockedContainedImplantEntityUids = CollectBlockedContainedImplantEntityUids(protoSeq);

        foreach (var protoNode in protoSeq)
        {
            if (protoNode is not MappingDataNode protoMap)
                continue;

            if (!protoMap.TryGet("entities", out SequenceDataNode? entitiesSeq) || entitiesSeq == null)
                continue;

            for (var i = 0; i < entitiesSeq.Count; i++)
            {
                if (entitiesSeq[i] is not MappingDataNode entMap)
                    continue;

                // Remove runtime-only map flags from exported entities.
                entMap.Remove("mapInit");
                entMap.Remove("paused");

                var hasProtoGroup = false;
                var allowFillComponents = false;
                HashSet<string>? protoMissing = null;
                var dropByPrototypeComponent = false;
                EntityPrototype? entityProto = null;

                if (protoMap.TryGet("proto", out ValueDataNode? protoIdNode) && protoIdNode != null)
                {
                    hasProtoGroup = true;
                    var protoId = protoIdNode.Value;
                    allowFillComponents = FillComponentWhitelistPrototypes.Contains(protoId);

                    if (FilteredPrototypes.Contains(protoId))
                    {
                        if (entMap.TryGet("uid", out ValueDataNode? removedUidNode) && removedUidNode != null && !removedUidNode.IsNull)
                            removedEntityUids.Add(removedUidNode.Value);

                        entitiesSeq.RemoveAt(i);
                        i--;
                        continue;
                    }

                    if (prototypeManager.TryIndex<EntityPrototype>(protoId, out var proto))
                    {
                        entityProto = proto;

                        foreach (var componentName in FilteredEntityByComponentTypes)
                        {
                            if (!proto.Components.ContainsKey(componentName))
                                continue;

                            if (ComponentExclusionExceptions.TryGetValue(componentName, out var exceptionComponent)
                                && proto.Components.ContainsKey(exceptionComponent))
                                continue;

                            dropByPrototypeComponent = true;
                            break;
                        }

                        if (!allowFillComponents && proto.Components.ContainsKey("Door"))
                            allowFillComponents = true;

                        if (!allowFillComponents)
                        {
                            foreach (var name in FillComponentTypes)
                            {
                                if (!proto.Components.ContainsKey(name))
                                    continue;

                                protoMissing ??= new HashSet<string>(StringComparer.Ordinal);
                                protoMissing.Add(name);
                            }
                        }
                    }
                }

                if (!entMap.TryGet("components", out SequenceDataNode? comps) || comps == null)
                {
                    if (entMap.TryGet("uid", out ValueDataNode? removedUidNode) && removedUidNode != null && !removedUidNode.IsNull)
                        removedEntityUids.Add(removedUidNode.Value);

                    entitiesSeq.RemoveAt(i);
                    i--;
                    continue;
                }

                // Remove implanters containing blocked implant entities.
                var isImplanter = entityProto?.Components.ContainsKey("Implanter") == true || HasComponentNode(comps, "Implanter");
                if (isImplanter && HasBlockedContainedImplant(entMap, blockedContainedImplantEntityUids))
                {
                    if (entMap.TryGet("uid", out ValueDataNode? removedUidNode) && removedUidNode != null && !removedUidNode.IsNull)
                        removedEntityUids.Add(removedUidNode.Value);

                    entitiesSeq.RemoveAt(i);
                    i--;
                    continue;
                }

                var dropByComponent = false;
                foreach (var c in comps)
                {
                    if (c is not MappingDataNode cm)
                        continue;

                    if (!cm.TryGet("type", out ValueDataNode? t) || t == null)
                        continue;

                    if (!FilteredEntityByComponentTypes.Contains(t.Value))
                        continue;

                    if (ComponentExclusionExceptions.TryGetValue(t.Value, out var exceptionComponent)
                        && (HasComponentNode(comps, exceptionComponent)
                            || entityProto?.Components.ContainsKey(exceptionComponent) == true))
                        continue;

                    dropByComponent = true;
                    break;
                }

                if (dropByComponent)
                {
                    if (entMap.TryGet("uid", out ValueDataNode? removedUidNode) && removedUidNode != null && !removedUidNode.IsNull)
                        removedEntityUids.Add(removedUidNode.Value);

                    entitiesSeq.RemoveAt(i);
                    i--;
                    continue;
                }

                if (dropByPrototypeComponent)
                {
                    if (entMap.TryGet("uid", out ValueDataNode? removedUidNode) && removedUidNode != null && !removedUidNode.IsNull)
                        removedEntityUids.Add(removedUidNode.Value);

                    entitiesSeq.RemoveAt(i);
                    i--;
                    continue;
                }

                // Grid root gets slightly different Transform sanitation.
                var hasMapGrid = false;
                var compsNotNull = comps;
                var paintStylePrototype = GetPaintStylePrototype(compsNotNull);

                foreach (var c in compsNotNull)
                {
                    if (c is MappingDataNode cm && cm.TryGet("type", out ValueDataNode? t) && t != null && t.Value == "MapGrid")
                    {
                        hasMapGrid = true;
                        break;
                    }
                }

                var hasDoorComponent = false;
                foreach (var c in compsNotNull)
                {
                    if (c is MappingDataNode cm && cm.TryGet("type", out ValueDataNode? t) && t != null && t.Value == "Door")
                    {
                        hasDoorComponent = true;
                        break;
                    }
                }

                if (hasDoorComponent)
                {
                    allowFillComponents = true;
                    protoMissing = null;
                }

                // Build sanitized component list for this entity.
                var newComps = new SequenceDataNode();
                var removedFromPrototype = hasProtoGroup ? new HashSet<string>(StringComparer.Ordinal) : null;

                foreach (var compNode in compsNotNull)
                {
                    if (compNode is not MappingDataNode compMap)
                        continue;

                    if (!compMap.TryGet("type", out ValueDataNode? typeNode) || typeNode == null)
                    {
                        newComps.Add(compMap);
                        continue;
                    }

                    var typeName = typeNode.Value;

                    if (FilteredTypes.Contains(typeName))
                    {
                        if (allowFillComponents && FillComponentTypes.Contains(typeName))
                        {
                            newComps.Add(compMap);
                            continue;
                        }

                        if (FillComponentTypes.Contains(typeName))
                            removedFromPrototype?.Add(typeName);

                        continue;
                    }

                    if (typeName == "Transform" && hasMapGrid)
                        compMap.Remove("rot");

                    if (typeName == "Appearance" && paintStylePrototype != null)
                        ApplyPaintStyleToAppearance(compMap, paintStylePrototype);

                    if (typeName == "SpreaderGrid")
                    {
                        compMap.Remove("updateAccumulator");
                        compMap.Remove("UpdateAccumulator");
                    }

                    if (typeName == "VendingMachine")
                    {
                        compMap.Remove("Inventory");
                        compMap.Remove("EmaggedInventory");
                        compMap.Remove("ContrabandInventory");
                        compMap.Remove("Contraband");
                        compMap.Remove("EjectEnd");
                        compMap.Remove("DenyEnd");
                        compMap.Remove("DispenseOnHitEnd");
                        compMap.Remove("NextEmpEject");
                        compMap.Remove("EjectRandomCounter");
                    }

                    if (typeName == "ResearchServer")
                    {
                        compMap.Remove("points");
                        compMap.Remove("Points");
                        compMap.Remove("pointsPerSecond");
                        compMap.Remove("PointsPerSecond");
                    }

                    if (typeName == "TechnologyDatabase")
                    {
                        compMap.Remove("unlockedTechnologies");
                        compMap.Remove("UnlockedTechnologies");
                        compMap.Remove("unlockedRecipes");
                        compMap.Remove("UnlockedRecipes");
                        compMap.Remove("currentTechnologyCards");
                        compMap.Remove("CurrentTechnologyCards");
                        compMap.Remove("mainDiscipline");
                        compMap.Remove("MainDiscipline");
                    }

                    if (typeName == "Battery")
                    {
                        compMap["currentCharge"] = new ValueDataNode("0");
                        compMap["CurrentCharge"] = new ValueDataNode("0");
                    }

                    if (typeName == "DeviceNetwork")
                    {
                        compMap.Remove("devices");
                        compMap.Remove("Devices");
                    }

                    if (typeName == "Solution")
                    {
                        if (compMap.TryGetValue("solution", out var solutionNode)
                            && solutionNode is MappingDataNode solutionMap
                            && solutionMap.TryGetValue("name", out var nameNode)
                            && nameNode is ValueDataNode nameValue
                            && nameValue.Value == "buffer")
                        {
                            solutionMap["canReact"] = new ValueDataNode("false");
                        }
                    }

                    if (typeName == "ReagentDispenser")
                    {
                        compMap.Remove("storageSlots");
                        compMap.Remove("storageSlotIds");
                        compMap.Remove("autoLabel");
                    }

                    newComps.Add(compMap);
                }

                // If SprayPainted exists without Appearance, synthesize Appearance so paint style persists in saves.
                if (paintStylePrototype != null && !HasComponentNode(newComps, "Appearance"))
                {
                    var appearanceComp = new MappingDataNode
                    {
                        ["type"] = new ValueDataNode("Appearance")
                    };

                    ApplyPaintStyleToAppearance(appearanceComp, paintStylePrototype);
                    newComps.Add(appearanceComp);
                }

                if ((removedFromPrototype != null && removedFromPrototype.Count > 0) || (protoMissing != null && protoMissing.Count > 0))
                {
                    var existingMissing = new HashSet<string>(StringComparer.Ordinal);
                    if (entMap.TryGet("missingComponents", out SequenceDataNode? missingNode) && missingNode != null)
                    {
                        foreach (var missing in missingNode)
                        {
                            if (missing is ValueDataNode value)
                                existingMissing.Add(value.Value);
                        }
                    }

                    var mergedSet = new HashSet<string>(existingMissing, StringComparer.Ordinal);
                    if (removedFromPrototype != null)
                    {
                        foreach (var name in removedFromPrototype)
                            mergedSet.Add(name);
                    }

                    if (protoMissing != null)
                    {
                        foreach (var name in protoMissing)
                            mergedSet.Add(name);
                    }

                    if (allowFillComponents)
                    {
                        foreach (var name in FillComponentTypes)
                            mergedSet.Remove(name);
                    }

                    if (mergedSet.Count > 0)
                    {
                        var mergedMissing = new SequenceDataNode();
                        foreach (var name in mergedSet)
                            mergedMissing.Add(new ValueDataNode(name));

                        entMap["missingComponents"] = mergedMissing;
                    }
                }

                if (newComps.Count > 0)
                {
                    entMap["components"] = newComps;
                }
                else
                {
                    if (entMap.TryGet("uid", out ValueDataNode? removedUidNode) && removedUidNode != null && !removedUidNode.IsNull)
                        removedEntityUids.Add(removedUidNode.Value);

                    entitiesSeq.RemoveAt(i);
                    i--;
                }
            }
        }

        // Final pass: remove stale container/storage references to entities dropped above.
        PruneContainerReferencesToRemovedEntities(protoSeq, removedEntityUids);
    }

    private static string? GetPaintStylePrototype(SequenceDataNode components)
    {
        foreach (var compNode in components)
        {
            if (compNode is not MappingDataNode compMap)
                continue;

            if (!compMap.TryGet("type", out ValueDataNode? typeNode) || typeNode == null || typeNode.Value != "SprayPainted")
                continue;

            if (!compMap.TryGet("paintedPrototype", out ValueDataNode? styleNode) || styleNode == null)
                continue;

            if (!string.IsNullOrWhiteSpace(styleNode.Value))
                return styleNode.Value;
        }

        return null;
    }

    private static bool HasComponentNode(SequenceDataNode components, string componentType)
    {
        foreach (var compNode in components)
        {
            if (compNode is not MappingDataNode compMap)
                continue;

            if (!compMap.TryGet("type", out ValueDataNode? typeNode) || typeNode == null)
                continue;

            if (typeNode.Value == componentType)
                return true;
        }

        return false;
    }

    private static HashSet<string> CollectBlockedContainedImplantEntityUids(SequenceDataNode protoSeq)
    {
        // Resolve concrete entity UIDs for blocked implant prototypes so implanter checks are cheap per entity.
        var blockedImplantUids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var protoNode in protoSeq)
        {
            if (protoNode is not MappingDataNode protoMap)
                continue;

            if (!protoMap.TryGet("entities", out SequenceDataNode? entitiesSeq) || entitiesSeq == null)
                continue;

            var protoIsBlockedImplant = false;
            if (protoMap.TryGet("proto", out ValueDataNode? protoIdNode)
                && protoIdNode != null
                && !protoIdNode.IsNull)
            {
                protoIsBlockedImplant = BlockedContainedImplantPrototypes.Contains(protoIdNode.Value);
            }

            foreach (var entityNode in entitiesSeq)
            {
                if (entityNode is not MappingDataNode entMap)
                    continue;

                if (!entMap.TryGet("uid", out ValueDataNode? uidNode) || uidNode == null || uidNode.IsNull)
                    continue;

                if (protoIsBlockedImplant)
                    blockedImplantUids.Add(uidNode.Value);
            }
        }

        return blockedImplantUids;
    }

    private static bool HasBlockedContainedImplant(MappingDataNode entMap, HashSet<string> blockedContainedImplantEntityUids)
    {
        if (blockedContainedImplantEntityUids.Count == 0)
            return false;

        if (!entMap.TryGet("components", out SequenceDataNode? components) || components == null)
            return false;

        foreach (var compNode in components)
        {
            if (compNode is not MappingDataNode compMap)
                continue;

            if (!compMap.TryGet("type", out ValueDataNode? typeNode) || typeNode == null || typeNode.Value != "ContainerContainer")
                continue;

            if (!compMap.TryGet("containers", out MappingDataNode? containersMap) || containersMap == null)
                continue;

            if (!containersMap.TryGet("implanter_slot", out MappingDataNode? slotMap) || slotMap == null)
                continue;

            if (slotMap.TryGet("ent", out ValueDataNode? entNode) && entNode != null && !entNode.IsNull && blockedContainedImplantEntityUids.Contains(entNode.Value))
                return true;

            if (!slotMap.TryGet("ents", out SequenceDataNode? entsNode) || entsNode == null)
                continue;

            foreach (var entry in entsNode)
            {
                if (entry is not ValueDataNode valueNode || valueNode.IsNull)
                    continue;

                if (blockedContainedImplantEntityUids.Contains(valueNode.Value))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Drops dangling entries from a serialized sequence of entity uids.
    /// </summary>
    private static void PruneUidSequence(MappingDataNode compMap, string field, Func<string, bool> isDangling)
    {
        if (!compMap.TryGet(field, out SequenceDataNode? seq) || seq == null)
            return;

        for (var idx = seq.Count - 1; idx >= 0; idx--)
        {
            if (seq[idx] is ValueDataNode entry && !entry.IsNull && isDangling(entry.Value))
                seq.RemoveAt(idx);
        }
    }

    /// <summary>
    /// Collects every entity uid that actually survives into the exported file.
    /// </summary>
    private static HashSet<string> CollectPresentEntityUids(SequenceDataNode protoSeq)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var protoNode in protoSeq)
        {
            if (protoNode is not MappingDataNode protoMap)
                continue;

            if (!protoMap.TryGet("entities", out SequenceDataNode? entitiesSeq) || entitiesSeq == null)
                continue;

            foreach (var entityNode in entitiesSeq)
            {
                if (entityNode is MappingDataNode entMap
                    && entMap.TryGet("uid", out ValueDataNode? uidNode)
                    && uidNode != null
                    && !uidNode.IsNull)
                {
                    present.Add(uidNode.Value);
                }
            }
        }

        return present;
    }

    private static void PruneContainerReferencesToRemovedEntities(SequenceDataNode protoSeq, HashSet<string> removedEntityUids)
    {
        // Triad: this used to prune only what the sanitizer itself dropped, which missed the larger
        // half of the problem. Most dangling references point at something that was never a candidate
        // for the file at all: the station a console is registered to, the mob whose container held an
        // action entity, an audio stream, a grid on the other side of the sector. Asking "is this uid
        // in the finished file" covers both cases at once, and the removed set is a subset of it.
        var presentUids = CollectPresentEntityUids(protoSeq);

        // The removed set is already a subset of "not present", since dropping an entity takes its uid
        // entry with it. It stays in the check so the sanitizer's own removals remain explicit rather
        // than depending on that reasoning holding for every future removal path.
        bool IsDangling(string uid) => removedEntityUids.Contains(uid) || !presentUids.Contains(uid);

        // Remove dangling references from both ContainerContainer and Storage serialized structures.
        foreach (var protoNode in protoSeq)
        {
            if (protoNode is not MappingDataNode protoMap)
                continue;

            if (!protoMap.TryGet("entities", out SequenceDataNode? entitiesSeq) || entitiesSeq == null)
                continue;

            foreach (var entityNode in entitiesSeq)
            {
                if (entityNode is not MappingDataNode entMap)
                    continue;

                if (!entMap.TryGet("components", out SequenceDataNode? comps) || comps == null)
                    continue;

                // Triad: descending, because the required-reference case removes the component itself.
                for (var compIdx = comps.Count - 1; compIdx >= 0; compIdx--)
                {
                    if (comps[compIdx] is not MappingDataNode compMap)
                        continue;

                    if (!compMap.TryGet("type", out ValueDataNode? typeNode) || typeNode == null)
                        continue;

                    var componentType = typeNode.Value;

                    if (componentType == "ContainerContainer")
                    {
                        if (!compMap.TryGet("containers", out MappingDataNode? containersMap) || containersMap == null)
                            continue;

                        foreach (var (_, containerNode) in containersMap)
                        {
                            if (containerNode is not MappingDataNode containerMap)
                                continue;

                            if (containerMap.TryGet("ents", out SequenceDataNode? entsNode) && entsNode != null)
                            {
                                for (var idx = entsNode.Count - 1; idx >= 0; idx--)
                                {
                                    if (entsNode[idx] is not ValueDataNode entValue || entValue.IsNull)
                                        continue;

                                    if (IsDangling(entValue.Value))
                                        entsNode.RemoveAt(idx);
                                }
                            }

                            if (containerMap.TryGet("ent", out ValueDataNode? entNode) && entNode != null && !entNode.IsNull)
                            {
                                if (IsDangling(entNode.Value))
                                    containerMap["ent"] = ValueDataNode.Null();
                            }
                        }

                        continue;
                    }

                    if (componentType == "Storage" && compMap.TryGet("storedItems", out MappingDataNode? storedItemsMap) && storedItemsMap != null)
                    {
                        var removeKeys = new List<string>();
                        foreach (var (itemUid, _) in storedItemsMap)
                        {
                            if (IsDangling(itemUid))
                                removeKeys.Add(itemUid);
                        }

                        foreach (var key in removeKeys)
                            storedItemsMap.Remove(key);

                        continue;
                    }

                    // Triad: the reference shapes described above the field tables.
                    if (UidRequiredFieldsDropComponent.TryGetValue(componentType, out var requiredField)
                        && compMap.TryGet(requiredField, out ValueDataNode? requiredNode)
                        && requiredNode != null
                        && !requiredNode.IsNull
                        && IsDangling(requiredNode.Value))
                    {
                        comps.RemoveAt(compIdx);
                        continue;
                    }

                    if (UidSetFields.TryGetValue(componentType, out var setField))
                        PruneUidSequence(compMap, setField, IsDangling);

                    if (UidSetFieldsSecondary.TryGetValue(componentType, out var setField2))
                        PruneUidSequence(compMap, setField2, IsDangling);

                    if (UidSlotFields.TryGetValue(componentType, out var slotField)
                        && compMap.TryGet(slotField, out SequenceDataNode? slotSeq)
                        && slotSeq != null)
                    {
                        for (var idx = 0; idx < slotSeq.Count; idx++)
                        {
                            if (slotSeq[idx] is ValueDataNode entry && !entry.IsNull && IsDangling(entry.Value))
                                slotSeq[idx] = ValueDataNode.Null();
                        }
                    }

                    if (!UidScalarFields.TryGetValue(componentType, out var scalarFields))
                        continue;

                    foreach (var scalarField in scalarFields)
                    {
                        if (compMap.TryGet(scalarField, out ValueDataNode? scalarNode)
                            && scalarNode != null
                            && !scalarNode.IsNull
                            && IsDangling(scalarNode.Value))
                        {
                            compMap[scalarField] = ValueDataNode.Null();
                        }
                    }
                }
            }
        }
    }

    private static void ApplyPaintStyleToAppearance(MappingDataNode appearanceComp, string stylePrototype)
    {
        MappingDataNode appearanceDataInit;
        if (appearanceComp.TryGet("appearanceDataInit", out MappingDataNode? existing) && existing != null)
        {
            appearanceDataInit = existing;
        }
        else
        {
            appearanceDataInit = new MappingDataNode();
            appearanceComp["appearanceDataInit"] = appearanceDataInit;
        }

        appearanceDataInit["enum.PaintableVisuals.Prototype"] = new ValueDataNode(stylePrototype);
    }
}
