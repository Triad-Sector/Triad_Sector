# How to mark changes in this repository

The full guide is [Attributing Your Changes](https://github.com/Triad-Sector/Triad_Sector/wiki/Attributing-Your-Changes) on the wiki. The short version:

- Edits to inherited files (anything not under a `_Triad/` path) get `// Triad:` markers, `# Triad:` in YAML, closed with `// End Triad` for blocks.
- Replaced inherited code is commented out with a `// Triad: removed` reason, never deleted, so upstream merges surface conflicts instead of silently reverting us.
- New original work lives under `_Triad/` and needs no license header; `LEGAL.md` licenses it AGPL-3.0-or-later by default.
- Content ported from another fork keeps its origin namespace and its `meta.json` license and authors verbatim.
- Never remove an existing license notice, in a file header or a `meta.json`.
