using Content.Server.Power.EntitySystems;

namespace Content.Server.Power.Components
{
    [RegisterComponent]
    [Access(typeof(ExtensionCableSystem))]
    public sealed partial class ExtensionCableReceiverComponent : Component
    {
        /// <summary>
        ///     The provider currently feeding this receiver, if any. Pure runtime state - rebuilt by
        ///     <see cref="ExtensionCableSystem"/> on connect/disconnect, never serialized or networked.
        /// </summary>
        [ViewVariables]
        public Entity<ExtensionCableProviderComponent>? Provider { get; set; }

        [ViewVariables]
        public bool Connectable = false;

        /// <summary>
        ///     The max distance from a <see cref="ExtensionCableProviderComponent"/> that this can receive power from.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        [DataField("receptionRange")]
        public int ReceptionRange { get; set; } = 3;
    }
}
