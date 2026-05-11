# Triad fork RCD recipes (`_Mono/RCD/rcd.yml`)

Quick reference so you do not have to reverse-engineer Git history.

## What lives here

- **Mono-only RCD modes** (shuttle airlocks, SMES/substation icons, hull shapes, shuttle windows / diagonal walls).
- **`availablePrototypes` for the RCD item** are listed in `Resources/Prototypes/Entities/Objects/Tools/tools.yml` (search `mono start` / `_Mono` entries).

## Upstream / port lineage (GitHub)

| Topic | Reference |
|-------|-----------|
| Hull-in-space / grid targeting for do-after | HardLight [#888](https://github.com/HardLightSector/HardLight/pull/888) (logic in `Content.Shared/RCD/Systems/RCDSystem.cs`, `MapGridData`, do-after) |
| Diagonal corner plating → one rotatable mode | HardLight [#1153](https://github.com/HardLightSector/HardLight/pull/1153) → **`PlatingDiagonal`** + `constructTileByDirection` |
| Extra hull shapes + shuttle window / diagonal walls in menu | HardLight [#1237](https://github.com/HardLightSector/HardLight/pull/1237) → consolidated rotatable tile recipes + `_Mono` radial PNGs |
| Same entity on one tile only once unless rotated | Wizard’s Den [#42556](https://github.com/space-wizards/space-station-14/pull/42556) → **`allowMultiDirection`** on prototype + duplicate check in `RCDSystem` |

## C# you will touch when changing behavior

| Area | File |
|------|------|
| Prototype fields (`constructTileByDirection`, `allowMultiDirection`, …) | `Content.Shared/RCD/RCDPrototype.cs` |
| Placement validation, do-after, finalize tile spawn | `Content.Shared/RCD/Systems/RCDSystem.cs` (`GetConstructTileTypeId`, `IsRCDOperationStillValid`, …) |
| Ghost preview / tile texture by direction | `Content.Client/RCD/AlignRCDConstruction.cs`, `Content.Client/RCD/RCDConstructionGhostSystem.cs` |

## Tile definitions (actual floor tile ids)

- **Authoritative tile prototypes** for funky hull shapes: `Resources/Prototypes/_Mono/Turf/generated.yml` (and parents like `Plating` in `Resources/Prototypes/Tiles/plating.yml`).
- RCD YAML only **selects** tile ids (often via `constructTileByDirection`); maps still reference **tile** ids, not RCD recipe ids.

## Merging Wizard’s Den later

- Expect **`AllowMultiDirection`** on upstream `RCDPrototype`; keep **`ConstructTileByDirection`** (Triad). Merge = **both fields**.
- Upstream **tile history / `ReplaceTile` / `baseWhitelist`** on tiles may still differ from Triad’s `TileSystem` / `ContentTileDefinition` — see comments near `GetConstructTileTypeId` in `RCDSystem.cs`.

## Atmos expectation

Partial hull tiles **do not** carve gas cells from polygon geometry; sealing is still from **entities** with `AirtightComponent`, not from tile `vertices`.

## Radial menu art

- Shuttle / diagonal wall window icons: `Resources/Textures/_Mono/Interface/Radial/RCD/` + `attributions.yml` (HardLight #1237 commit).
