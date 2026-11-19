#!/usr/bin/env python3
"""
Fails when a change removes an entity prototype without a migration line for it.

Saved ships and stored drydock hulls hold prototype ids as plain text. The engine refuses the whole
load when any one of them no longer resolves, unless one of the migration files renames or deletes
it, so every removal needs a line. Also checks the migration files themselves, since a release build
never does: MapMigrationSystem only asserts its targets in DEBUG, and a key repeated across two files
throws on every map load.

Usage: check_prototype_migrations.py <base-ref> [<head-ref> | WORKTREE]
The head defaults to HEAD; WORKTREE checks uncommitted edits on disk.
"""

import re
import subprocess
import sys

import yaml

PROTOTYPES = "Resources/Prototypes"
# Keep in step with MapMigrationSystem.MigrationFiles.
MIGRATIONS = ["Resources/migration.yml", "Resources/nf_migration.yml",
              "Resources/mono_migration.yml", "Resources/triad_migration.yml"]
WORKTREE = "WORKTREE"


class _Loader(yaml.CSafeLoader if hasattr(yaml, "CSafeLoader") else yaml.SafeLoader):
    pass


# Prototype files use !type: tags; their payload never holds an entity id.
_Loader.add_multi_constructor("!", lambda loader, suffix, node: None)


def git(*args: str) -> str:
    return subprocess.run(["git", *args], check=True, encoding="utf-8", errors="replace",
                          stdout=subprocess.PIPE).stdout


_cat_file: subprocess.Popen | None = None


def read_blob(ref: str, path: str) -> str | None:
    """File contents at a ref, or on disk when ref is WORKTREE. One cat-file pipe serves every read."""
    if ref == WORKTREE:
        try:
            with open(path, encoding="utf-8", errors="replace") as f:
                raw = f.read()
        except FileNotFoundError:
            return None
    else:
        global _cat_file
        if _cat_file is None:
            _cat_file = subprocess.Popen(["git", "cat-file", "--batch"],
                                         stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        _cat_file.stdin.write(f"{ref}:{path}\n".encode())
        _cat_file.stdin.flush()
        header = _cat_file.stdout.readline().split()
        if len(header) != 3:
            return None
        raw = _cat_file.stdout.read(int(header[2])).decode("utf-8", errors="replace")
        _cat_file.stdout.read(1)
    return raw.lstrip("\ufeff").replace("\r\n", "\n")


def entity_ids(text: str) -> dict[str, bool]:
    """Every entity prototype id in one file, mapped to whether it is abstract."""
    try:
        docs = yaml.load(text, Loader=_Loader)
    except yaml.YAMLError:
        return _entity_ids_loose(text)

    ids = {}
    for doc in docs if isinstance(docs, list) else []:
        if isinstance(doc, dict) and doc.get("type") == "entity" and doc.get("id"):
            ids[str(doc["id"])] = bool(doc.get("abstract"))
    return ids


def _entity_ids_loose(text: str) -> dict[str, bool]:
    """Line scan for a file the YAML parser rejects, so a typo elsewhere cannot hide a removal."""
    ids = {}
    for chunk in re.split(r"^- ", text, flags=re.M)[1:]:
        fields = {}
        for i, line in enumerate(chunk.split("\n")):
            if i > 0:
                if not line.startswith("  ") or line.startswith("   "):
                    continue
                line = line[2:]
            match = re.match(r"^([A-Za-z]+):\s*(.*?)\s*(#.*)?$", line)
            if match:
                fields[match.group(1)] = match.group(2).strip("\"'")
        if fields.get("type") == "entity" and fields.get("id"):
            ids[fields["id"]] = fields.get("abstract", "").lower() == "true"
    return ids


def prototype_files(ref: str) -> list[str]:
    if ref == WORKTREE:
        out = git("ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", PROTOTYPES)
    else:
        out = git("ls-tree", "-r", "--name-only", "-z", ref, "--", PROTOTYPES)
    return [p for p in out.split("\0") if p.endswith((".yml", ".yaml"))]


def migration_entries(ref: str) -> list[tuple[str, int, str, str]]:
    """(file, line, key, value) for every entry, read line by line so a repeated key is not merged away."""
    entries = []
    for path in MIGRATIONS:
        text = read_blob(ref, path)
        if text is None:
            continue
        for number, line in enumerate(text.split("\n"), start=1):
            match = re.match(r"^([^\s#][^:]*?):\s*(.*?)\s*(#.*)?$", line)
            if match:
                entries.append((path, number, match.group(1).strip(), match.group(2).strip()))
    return entries


def main() -> int:
    if len(sys.argv) not in (2, 3):
        print(__doc__.strip())
        return 2

    base = sys.argv[1]
    head = sys.argv[2] if len(sys.argv) == 3 else "HEAD"

    head_ids: dict[str, bool] = {}
    for path in prototype_files(head):
        head_ids.update(entity_ids(read_blob(head, path) or ""))

    entries = migration_entries(head)
    migrated = {key for _, _, key, _ in entries}
    errors = []

    # Only a file the change touched can have lost an id; the id may have moved to any other file.
    diff_refs = [base] if head == WORKTREE else [base, head]
    changed = git("diff", "--name-only", "-z", "--no-renames", *diff_refs, "--", PROTOTYPES)
    for path in (p for p in changed.split("\0") if p.endswith((".yml", ".yaml"))):
        for proto_id, abstract in entity_ids(read_blob(base, path) or "").items():
            if abstract or proto_id in migrated:
                continue
            if proto_id not in head_ids:
                errors.append((path, f"Entity prototype '{proto_id}' was removed without a migration line. "
                               f"Add '{proto_id}: <NewId>' (or ': null') to Resources/triad_migration.yml; "
                               f"saved ships holding it will otherwise fail to load."))
            elif head_ids[proto_id]:
                errors.append((path, f"Entity prototype '{proto_id}' was made abstract without a migration line. "
                               f"Saved ships can still hold it; point it at a concrete prototype in "
                               f"Resources/triad_migration.yml."))

    first_seen: dict[str, tuple[str, int]] = {}
    for path, number, key, value in entries:
        where = f"{path}:{number}"
        if key in first_seen:
            other_path, other_number = first_seen[key]
            errors.append((path, f"{where}: '{key}' is already a key at {other_path}:{other_number}. "
                           f"MapMigrationSystem adds every file into one dictionary, so a repeat throws on every map load."))
            continue
        first_seen[key] = (path, number)

        if value in ("", "null"):
            continue
        if value in migrated:
            errors.append((path, f"{where}: '{key}' points at '{value}', which is itself migrated. "
                           f"Migrations do not chain; point it at the final id."))
        elif value not in head_ids:
            errors.append((path, f"{where}: '{key}' points at '{value}', which is not an entity prototype."))
        elif head_ids[value]:
            errors.append((path, f"{where}: '{key}' points at '{value}', which is abstract and cannot be spawned."))

    for path, message in errors:
        print(f"::error file={path},title=Prototype migration::{message}")

    print(f"Checked {len(head_ids)} entity prototypes and {len(entries)} migration entries against {base}: "
          f"{len(errors)} problem(s).")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
