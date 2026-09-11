using System.Threading;
using Content.Shared.Mobs;

namespace Content.Server.Explosion.Components;

/// <summary>
/// Use where you want something to trigger on mobstate change
/// </summary>
[RegisterComponent]
public sealed partial class TriggerOnMobstateChangeComponent : Component
{
    /// <summary>
    /// What state should trigger this?
    /// </summary>
    [ViewVariables]
    [DataField("mobState", required: true)]
    public List<MobState> MobState = new();

    /// <summary>
    /// If true, prevents suicide attempts for the trigger to prevent cheese.
    /// </summary>
    [ViewVariables]
    [DataField("preventSuicide")]
    public bool PreventSuicide = false;

    // the timer cancel token
    [ViewVariables]
    public CancellationTokenSource RattleCancelToken = new();

    // The delay before the implant sends the message again
    [DataField]
    public TimeSpan RattleRefireDelay = TimeSpan.FromMinutes(20);
}
