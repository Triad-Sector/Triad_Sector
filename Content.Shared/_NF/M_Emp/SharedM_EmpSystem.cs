using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NF.M_Emp;

public abstract partial class SharedM_EmpSystem : EntitySystem
{
    [Dependency] private ILocalizationManager _loc = default!;
    [Dependency] private IPrototypeManager _proto = default!;
}
