using Content.Shared.Research.Components;

namespace Content.Shared.Research.Systems;

// Triad: research does not travel with a stored ship.
public abstract partial class SharedResearchSystem
{
    /// <summary>
    /// Returns a technology database to the state a freshly built machine has: no unlocked
    /// technologies or recipes, no main discipline, and a new hand of technology cards.
    ///
    /// <para>No prototype declares unlocked technologies, recipes or a main discipline, so empty is
    /// the prototype default. That makes this the same result the legacy ship save produced by
    /// stripping those fields from the file.</para>
    /// </summary>
    public void ResetDatabase(Entity<TechnologyDatabaseComponent> ent)
    {
        ent.Comp.UnlockedTechnologies.Clear();
        ent.Comp.UnlockedRecipes.Clear();
        ent.Comp.MainDiscipline = null;

        // Map init deals the cards and never re-fires on a loaded entity; this also dirties.
        UpdateTechnologyCards(ent, ent.Comp);

        var ev = new TechnologyDatabaseModifiedEvent();
        RaiseLocalEvent(ent, ref ev);
    }
}
