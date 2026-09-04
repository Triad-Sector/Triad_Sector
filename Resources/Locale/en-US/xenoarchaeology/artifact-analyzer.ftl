analysis-console-menu-title = Broad-Spectrum Mark 3 Analysis Console
analysis-console-server-list-button = Server
analysis-console-extract-button = Extract points

analysis-console-info-no-scanner = No analyzer connected! Please connect one using a multitool.
analysis-console-info-no-artifact = No artifact present! Place one on the pad to view node information.
analysis-console-info-ready = Systems operational. Ready to scan.

analysis-console-no-node = Select node to view
analysis-console-info-id = [font="Monospace" size=11]ID:[/font]
analysis-console-info-id-value = [font="Monospace" size=11][color=yellow]{$id}[/color][/font]
analysis-console-info-class = [font="Monospace" size=11]Class:[/font]
analysis-console-info-class-value = [font="Monospace" size=11]{$class}[/font]
analysis-console-info-locked = [font="Monospace" size=11]Status:[/font]
analysis-console-info-locked-value = [font="Monospace" size=11][color={ $state ->
    [0] red]Locked
    [1] lime]Unlocked
    *[2] plum]Active
}[/color][/font]
analysis-console-info-durability = [font="Monospace" size=11]Durability:[/font]
analysis-console-info-durability-value = [font="Monospace" size=11][color={$color}]{$current}/{$max}[/color][/font]
analysis-console-info-effect = [font="Monospace" size=11]Effect:[/font]
analysis-console-info-effect-value = [font="Monospace" size=11][color=gray]{ $state ->
    [true] {$info}
    *[false] Unlock nodes to gain info
}[/color][/font]
analysis-console-info-trigger = [font="Monospace" size=11]Triggers:[/font]
analysis-console-info-triggered-value = [font="Monospace" size=11][color=gray]{$triggers}[/color][/font]
analysis-console-info-scanner = Scanning...
analysis-console-info-scanner-paused = Paused.
analysis-console-progress-text = {$seconds ->
    [one] T-{$seconds} second
    *[other] T-{$seconds} seconds
}

analysis-console-extract-value = [font="Monospace" size=11][color=orange]Node {$id} (+{$value})[/color][/font]
analysis-console-extract-none = [font="Monospace" size=11][color=orange] No unlocked nodes have any points left to extract [/color][/font]
analysis-console-extract-sum = [font="Monospace" size=11][color=orange]Total Research: {$value}[/color][/font]
# Triad: artifact-wide readout. The extraction bonus is always live; the severity rows reveal as
# solved nodes give the console data to extrapolate from.
analysis-console-info-extraction = [font="Monospace" size=11]Extraction Bonus:[/font]
analysis-console-info-extraction-value = [font="Monospace" size=11][color=orange]x{$current}[/color][/font]
analysis-console-info-profile = [font="Monospace" size=11]Severity Profile:[/font]
analysis-console-info-profile-linear = [font="Monospace" size=11][color=lime]Steady Climb[/color][/font]
analysis-console-info-profile-log = [font="Monospace" size=11][color=yellow]Early Spike[/color][/font]
analysis-console-info-profile-exp = [font="Monospace" size=11][color=red]Late Cliff[/color][/font]
analysis-console-info-peak = [font="Monospace" size=11]Severity Peak:[/font]
analysis-console-info-peak-value = [font="Monospace" size=11][color={ $cap ->
    [2] lime]Class 2
    [3] yellow]Class 3
    [4] orange]Class 4
    *[other] red]Class {$cap}
}[/color][/font]
analysis-console-info-unknown-value = [font="Monospace" size=11][color=gray]Insufficient data[/color][/font]

analyzer-artifact-extract-popup = Energy shimmers on the artifact's surface!
# Triad: the console will happily total up points with no research server on the other end.
analyzer-artifact-extract-no-server = The console has no research server to bank the data to.
