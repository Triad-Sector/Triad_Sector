#!/usr/bin/env pwsh
# One-off audit script (NOT shipped) - finds entities whose FTL translation keys are missing.
# Surveys markings, materials, and emote prototypes against Resources/Locale/en-US.

param(
    [string]$Repo = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'

$LocaleRoot = Join-Path $Repo 'Resources/Locale/en-US'
$ProtoRoot  = Join-Path $Repo 'Resources/Prototypes'

Write-Host "Indexing FTL keys..." -ForegroundColor Cyan
$ftlKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
Get-ChildItem -Path $LocaleRoot -Recurse -Filter *.ftl | ForEach-Object {
    foreach ($line in [IO.File]::ReadLines($_.FullName)) {
        # FTL message: `identifier = value` at start of line (no leading whitespace = key)
        if ($line -match '^([a-zA-Z][a-zA-Z0-9_-]*)\s*=') {
            [void]$ftlKeys.Add($Matches[1])
        }
    }
}
Write-Host ("  found {0} unique FTL keys" -f $ftlKeys.Count)

# Parse YAML files crudely for `- type: marking`, `- type: material`, `- type: emote` blocks.
# We don't need a full YAML parser - we just walk lines and reset on `- type:`.
function Get-PrototypeBlocks {
    param([string]$ProtoType, [string[]]$Files)

    $out = [System.Collections.Generic.List[hashtable]]::new()
    foreach ($f in $Files) {
        $current = $null
        foreach ($raw in [IO.File]::ReadLines($f)) {
            $line = $raw -replace '\s*#.*$', ''
            if ($line -match '^\s*-\s*type:\s*(\S+)') {
                if ($null -ne $current) { $out.Add($current) }
                if ($Matches[1] -eq $ProtoType) {
                    $current = @{ File = $f; Id = $null; Name = $null; Sprites = [System.Collections.Generic.List[string]]::new(); ChatMessages = [System.Collections.Generic.List[string]]::new(); Whitelist = [System.Collections.Generic.List[string]]::new() }
                } else {
                    $current = $null
                }
                continue
            }
            if ($null -eq $current) { continue }
            if ($line -match '^\s+id:\s*(\S+)')   { $current.Id   = $Matches[1].Trim('"', "'") }
            if ($line -match '^\s+name:\s*(\S+)') { $current.Name = $Matches[1].Trim('"', "'") }
            # marking sprite states
            if ($line -match '^\s+state:\s*(\S+)') { $current.Sprites.Add($Matches[1].Trim('"', "'")) }
            # emote chatMessages (single-line inline list form)
            if ($line -match '^\s+chatMessages:\s*\[(.+?)\]') {
                $list = $Matches[1] -split ','
                foreach ($m in $list) { $current.ChatMessages.Add($m.Trim().Trim('"', "'")) }
            }
            # crude whitelist component capture - we only need Silicon/BorgChassis flag
            if ($line -match '^\s+-\s*(Silicon|BorgChassis|HumanoidAppearance)') {
                $current.Whitelist.Add($Matches[1])
            }
        }
        if ($null -ne $current) { $out.Add($current) }
    }
    return $out
}

Write-Host "`nScanning markings..." -ForegroundColor Cyan
$markingFiles = Get-ChildItem -Path $ProtoRoot -Recurse -Filter *.yml |
    Where-Object { $_.FullName -match 'Markings' } | Select-Object -ExpandProperty FullName
$markings = Get-PrototypeBlocks -ProtoType 'marking' -Files $markingFiles

$missingMarkings = [System.Collections.Generic.List[string]]::new()
$missingMarkingStates = [System.Collections.Generic.List[string]]::new()
foreach ($m in $markings) {
    if (-not $m.Id) { continue }
    $primaryKey = "marking-$($m.Id)"
    if (-not $ftlKeys.Contains($primaryKey)) {
        $missingMarkings.Add(("{0,-60} <- {1}" -f $primaryKey, (Resolve-Path -Relative $m.File)))
    }
    # Multi-sprite markings need per-sprite-state keys (colour picker labels)
    if ($m.Sprites.Count -gt 1) {
        foreach ($s in $m.Sprites) {
            $sk = "marking-$($m.Id)-$s"
            if (-not $ftlKeys.Contains($sk)) {
                $missingMarkingStates.Add(("{0,-60} <- {1}" -f $sk, (Resolve-Path -Relative $m.File)))
            }
        }
    }
}
Write-Host ("  {0} marking IDs total; {1} missing primary key, {2} missing sprite-state keys" -f $markings.Count, $missingMarkings.Count, $missingMarkingStates.Count)

Write-Host "`nScanning materials..." -ForegroundColor Cyan
$materialFiles = Get-ChildItem -Path $ProtoRoot -Recurse -Filter *.yml |
    Where-Object { $_.FullName -match 'Reagents|Materials|Stacks' } | Select-Object -ExpandProperty FullName
$materials = Get-PrototypeBlocks -ProtoType 'material' -Files $materialFiles

$missingMaterials = [System.Collections.Generic.List[string]]::new()
foreach ($m in $materials) {
    if (-not $m.Id -or -not $m.Name) { continue }
    if ($m.Name -notmatch '^[a-zA-Z][a-zA-Z0-9_-]*$') { continue } # skip literal strings, only check LocId-shaped names
    if (-not $ftlKeys.Contains($m.Name)) {
        $missingMaterials.Add(("{0,-50} (id={1,-25}) <- {2}" -f $m.Name, $m.Id, (Resolve-Path -Relative $m.File)))
    }
}
Write-Host ("  {0} material prototypes total; {1} missing FTL name key" -f $materials.Count, $missingMaterials.Count)

Write-Host "`nScanning emotes..." -ForegroundColor Cyan
$emoteFiles = Get-ChildItem -Path $ProtoRoot -Recurse -Filter *.yml |
    Where-Object { $_.FullName -match 'Voice|emote' } | Select-Object -ExpandProperty FullName
$emotes = Get-PrototypeBlocks -ProtoType 'emote' -Files $emoteFiles

$missingEmoteNames    = [System.Collections.Generic.List[string]]::new()
$missingEmoteMessages = [System.Collections.Generic.List[string]]::new()
$literalEmoteMessages = [System.Collections.Generic.List[string]]::new()

foreach ($e in $emotes) {
    if (-not $e.Id) { continue }
    if ($e.Name -and $e.Name -match '^[a-zA-Z][a-zA-Z0-9_-]*$' -and -not $ftlKeys.Contains($e.Name)) {
        $isSilicon = ($e.Whitelist -contains 'Silicon') -or ($e.Whitelist -contains 'BorgChassis')
        $tag = if ($isSilicon) { '[IPC]' } else { '     ' }
        $missingEmoteNames.Add(("{0} {1,-40} (id={2,-30}) <- {3}" -f $tag, $e.Name, $e.Id, (Resolve-Path -Relative $e.File)))
    }
    foreach ($msg in $e.ChatMessages) {
        $clean = $msg.Trim()
        if (-not $clean) { continue }
        $isSilicon = ($e.Whitelist -contains 'Silicon') -or ($e.Whitelist -contains 'BorgChassis')
        $tag = if ($isSilicon) { '[IPC]' } else { '     ' }
        # chat-emote-msg-* style key
        if ($clean -match '^[a-zA-Z][a-zA-Z0-9_-]*$') {
            if (-not $ftlKeys.Contains($clean)) {
                $missingEmoteMessages.Add(("{0} {1,-40} (id={2,-30}) <- {3}" -f $tag, $clean, $e.Id, (Resolve-Path -Relative $e.File)))
            }
        } else {
            # likely literal English (e.g. "boops.")
            $literalEmoteMessages.Add(("{0} '{1}' (id={2,-30}) <- {3}" -f $tag, $clean, $e.Id, (Resolve-Path -Relative $e.File)))
        }
    }
}
Write-Host ("  {0} emote prototypes total" -f $emotes.Count)
Write-Host ("  {0} missing name keys, {1} missing chatMessages keys, {2} literal (non-key) chatMessages" -f $missingEmoteNames.Count, $missingEmoteMessages.Count, $literalEmoteMessages.Count)

$reportPath = Join-Path $Repo 'Tools/ftl-audit-report.txt'
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("FTL Audit Report - $(Get-Date -Format o)")
[void]$sb.AppendLine("Repo: $Repo")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("=== Missing marking-* primary keys ($($missingMarkings.Count)) ===")
foreach ($l in $missingMarkings) { [void]$sb.AppendLine($l) }
[void]$sb.AppendLine("")
[void]$sb.AppendLine("=== Missing marking-*-<sprite> sublayer keys ($($missingMarkingStates.Count)) ===")
foreach ($l in $missingMarkingStates) { [void]$sb.AppendLine($l) }
[void]$sb.AppendLine("")
[void]$sb.AppendLine("=== Missing material name keys ($($missingMaterials.Count)) ===")
foreach ($l in $missingMaterials) { [void]$sb.AppendLine($l) }
[void]$sb.AppendLine("")
[void]$sb.AppendLine("=== Missing emote name keys ($($missingEmoteNames.Count)) ===")
foreach ($l in $missingEmoteNames) { [void]$sb.AppendLine($l) }
[void]$sb.AppendLine("")
[void]$sb.AppendLine("=== Missing emote chatMessages keys ($($missingEmoteMessages.Count)) ===")
foreach ($l in $missingEmoteMessages) { [void]$sb.AppendLine($l) }
[void]$sb.AppendLine("")
[void]$sb.AppendLine("=== Literal (non-key) emote chatMessages ($($literalEmoteMessages.Count)) ===")
foreach ($l in $literalEmoteMessages) { [void]$sb.AppendLine($l) }

[IO.File]::WriteAllText($reportPath, $sb.ToString())
Write-Host "`nReport saved to: $reportPath" -ForegroundColor Green
