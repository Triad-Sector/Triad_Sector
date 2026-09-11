using Content.Shared.Explosion.Components;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.Payload.Components;

/// <summary>
///     Component for providing the means of triggering an explosive payload. Used in grenade construction.
/// </summary>
/// <remarks>
///     This component performs two functions. Firstly, it will add or remove other components to some entity when this
///     item is installed inside of it. This is intended for use with constructible grenades. For example, this allows
///     you to add things like <see cref="OnUseTimerTriggerComponent"/>, or <see cref="TriggerOnProximityComponent"/>.
///     This is required because otherwise you would have to forward arbitrary interaction directed at the casing
///     through to the trigger, which would be quite complicated. Also proximity triggers don't really work inside of
///     containers.
///
///     Secondly, if the entity that this component is attached to is ever triggered directly (e.g., via a device
///     network message), the trigger will be forwarded to the device that this entity is installed in (if any).
/// </remarks>
[RegisterComponent, NetworkedComponent]
public sealed partial class PayloadTriggerComponent : Component
{
    /// <summary>
    ///     If true, triggering this entity will also cause the parent of this entity to be triggered.
    /// </summary>
    public bool Active = false;

    /// <summary>
    ///     List of components to add or remove from an entity when this trigger is (un)installed.
    /// </summary>
    [DataField("components", serverOnly:true, readOnly: true)]
    public ComponentRegistry? Components = null;

    /// <summary>
    ///     Keeps track of what components this trigger has granted to the payload case.
    /// </summary>
    /// <remarks>
    ///     This is required in case someone creates a construction graph that accepts more than one trigger, and those
    ///     trigger grant the same type of component (or the case just innately has that component). This list is used
    ///     when removing the component, to ensure that removal of this trigger only removes the components that it was
    ///     responsible for adding.
    /// </remarks>
    // Triad: System.Type has no data definition and no type serializer, so once a trigger is installed
    // in a case and this set is non-empty, persisting it throws "No data definition found for type
    // System.RuntimeType" and kills the whole ship-grid save. Runtime-only now.
    // This never round-tripped anyway: the write threw, and a load raises no container-insert event
    // (SharedContainerSystem.OnStartupValidation re-flags contents without re-inserting them), so
    // after a load PayloadSystem.OnEntityInserted has not run and both this set and Active are empty.
    // The case keeps the components it was granted, since they serialize as its own, but uninstalling
    // the trigger will not remove them and direct triggers no longer forward to the case. Both are
    // pre-existing: Active was never persisted either. Not changed here.
    /*
    [DataField("grantedComponents", serverOnly: true)]
    */
    [ViewVariables]
    // End Triad
    public HashSet<Type> GrantedComponents = new();
}
