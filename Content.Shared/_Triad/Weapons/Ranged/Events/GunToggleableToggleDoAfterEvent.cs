using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Weapons.Ranged.Events;

[Serializable, NetSerializable]
public sealed partial class GunToggleableToggleDoAfterEvent : SimpleDoAfterEvent;
