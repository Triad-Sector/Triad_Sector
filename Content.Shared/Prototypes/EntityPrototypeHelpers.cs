using JetBrains.Annotations;
using Robust.Shared.Prototypes;

namespace Content.Shared.Prototypes
{
    // Triad: the instance-side HasComponent extensions are gone; the engine's EntityPrototype.HasComp
    // overloads cover them with an explicit factory and a [ForbidLiteral] guard on the string form.
    // These two remain because they resolve a prototype id first, which the engine's do not.
    [UsedImplicitly]
    public static class EntityPrototypeHelpers
    {
        public static bool HasComponent<T>(string prototype, IPrototypeManager? prototypeManager = null, IComponentFactory? componentFactory = null) where T : IComponent
        {
            return HasComponent(prototype, typeof(T), prototypeManager, componentFactory);
        }

        public static bool HasComponent(string prototype, Type component, IPrototypeManager? prototypeManager = null, IComponentFactory? componentFactory = null)
        {
            prototypeManager ??= IoCManager.Resolve<IPrototypeManager>();
            componentFactory ??= IoCManager.Resolve<IComponentFactory>();

            return prototypeManager.TryIndex(prototype, out EntityPrototype? proto) && proto.HasComp(component, componentFactory);
        }
    }
}
