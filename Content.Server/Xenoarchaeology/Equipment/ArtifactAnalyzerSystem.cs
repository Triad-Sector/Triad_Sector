using Content.Server.Research.Systems;
using Content.Server.Xenoarchaeology.Artifact;
using Content.Shared.Popups;
using Content.Shared.Xenoarchaeology.Equipment;
using Content.Shared.Xenoarchaeology.Equipment.Components;
using Robust.Shared.Audio.Systems;

namespace Content.Server.Xenoarchaeology.Equipment;

/// <inheritdoc />
public sealed class ArtifactAnalyzerSystem : SharedArtifactAnalyzerSystem
{
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly ResearchSystem _research = default!;
    [Dependency] private readonly XenoArtifactSystem _xenoArtifact = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<AnalysisConsoleComponent, AnalysisConsoleExtractButtonPressedMessage>(OnExtractButtonPressed);
    }

    private void OnExtractButtonPressed(Entity<AnalysisConsoleComponent> ent, ref AnalysisConsoleExtractButtonPressedMessage args)
    {
        if (!TryGetArtifactFromConsole(ent, out var artifact))
            return;

        // Triad: the client renders the whole breakdown and a total the moment the button is pressed,
        // so bailing quietly here told the crew they had banked points they never got. Ships carry
        // consoles far more often than they carry a research server, so say so out loud.
        if (!_research.TryGetClientServer(ent, out var server, out var serverComponent))
        {
            _popup.PopupEntity(Loc.GetString("analyzer-artifact-extract-no-server"), ent, PopupType.MediumCaution);
            return;
        }

        var sumResearch = 0;
        foreach (var node in _xenoArtifact.GetAllNodes(artifact.Value))
        {
            var research = _xenoArtifact.GetResearchValue(node);
            _xenoArtifact.SetConsumedResearchValue(node, node.Comp.ConsumedResearchValue + research);
            sumResearch += research;
        }

        // 4-16-25: It's a sad day when a scientist makes negative 5k research
        if (sumResearch <= 0)
            return;

        // Triad: the completion multiplier applies to RP as well as credits. ConsumedResearchValue
        // records the raw node value, so points drained early bank at the low multiplier and are
        // never reclaimable at the full-solve one. That is the incentive to hold, and the console
        // shows the pending bonus so it reads as a choice rather than a trap.
        sumResearch = (int)(sumResearch * _xenoArtifact.GetArtifactPayoutMultiplier(artifact.Value));

        _research.ModifyServerPoints(server.Value, sumResearch, serverComponent);
        _audio.PlayPvs(ent.Comp.ExtractSound, artifact.Value);
        _popup.PopupEntity(Loc.GetString("analyzer-artifact-extract-popup"), artifact.Value, PopupType.Large);
    }
}

