# Standalone MongoDB storage

LastSeen2 uses one collection on a standalone MongoDB server. Multi-document transactions and replica-set infrastructure are not required. The API still exposes an ordered board snapshot with a revision.

## Schema version 3

- One document per bookmark: `_id` is the GUID string, `kind` is `video` or `channel`, `schemaVersion` is 3, and `versions` maps unique operation tokens to `{ generation, value }`.
- Each value contains that item's fields, timestamps, and zero-based position; channels also contain their group. Bookmark content is stored only in its own document.
- One `_id: "board"` metadata document contains `kind: "board"`, schema version, API revision, internal generation, and `items: [{ id, version }]`. These references contain no bookmark content. Positions live in item values, so reference-array order is irrelevant.
- Normal cleanup leaves one committed version per live item and removes deleted items. Failed or concurrent saves may temporarily leave unreferenced versions or item documents; these are invisible to the application.

## Save and read protocol

1. Load metadata and its referenced item versions. Verify metadata generation/revision again after loading. Retry if changed; if unchanged but a referenced version is missing, fail safely instead of returning a partial board.
2. Validate the proposed API snapshot. Reuse references for unchanged values. Stage changed values under a new random operation token in each affected item document. The generation is the loaded metadata generation plus one. Distinct writers never overwrite each other's version keys.
3. Publish the complete new reference list using a single-document compare-and-swap matching metadata schema, revision and generation. This is the commit point. Deletes omit references; moves and reorders publish their new positions together.
4. An acknowledged compare-and-swap loss returns a conflict and can remove the loser's own staged token. Failure before attempting publication can also discard its staged token. A timeout or unknown outcome during publication must NOT discard those versions: the commit might already have happened. Reload to discover the outcome.
5. After an acknowledged commit, prune obsolete versions. Cleanup is best effort; its failure cannot undo the committed save. The next successful save can collect leftovers.

All writes require `w=1, j=true`, including staging, publication, and cleanup. Retryable writes are disabled. No correctness guarantee depends on a process-local lock, lease expiry, server clock, or background job. Multiple application instances can race the same revision; only one metadata replacement wins.

## Pruning invariants

A cleanup pass uses an acknowledged committed manifest and generation G. It must keep every version referenced by that manifest and every version with generation greater than G. Only unreferenced versions with generation at most G may be removed: their writers cannot publish after the committed generation advanced. Unique version tokens are never reused.

Cleanup unsets individual version keys, never replaces an entire item or its versions map. It deletes an item only if its versions map is still empty at deletion time. A delayed cleanup from an earlier save therefore cannot erase newly staged data. A delayed staging write after a losing/failed save may leave an orphan, but its old metadata comparison cannot publish it; a later pass can collect it.

A reader may have loaded an older manifest whose versions are pruned concurrently. It rechecks metadata and retries; it must never silently omit missing items. Unknown publication results retain versions until a later committed generation proves they are unreachable. Repeated failed saves without a subsequent successful save can retain invisible storage until normal cleanup resumes.

## Migration and operations

Schema 1 (a single board document) stages each existing item and publishes schema-3 references in place of the old board. Schema 2 (flat items plus revision metadata) stages versions in those same item documents before publishing references; cleanup removes the obsolete flat fields. The public revision is preserved, while the internal generation advances. Competing migrations use independent tokens and the same atomic publication check.

Stop old application versions and back up the collection before migration. Do not run old writers against the upgraded collection or edit internal metadata/version fields manually. Restoring the pre-migration backup is required to roll back the schema. Migration occurs on the first authorized board operation; development tests never use the configured application database.

Direct database inspection during a write can see staging or cleanup in progress; consistent reads are provided by the application protocol, not by a MongoDB multi-document snapshot. Back up the collection with the application stopped, or use a backup method that provides a consistent database snapshot. Keep journal durability enabled.

Tests use unique `lastseen2_test_<guid>` databases on standalone MongoDB. They verify CRUD, ordering, cross-instance conflicts, read/cleanup races, migrations, collision safety, and persisted states at interrupted-save boundaries. See README for commands.
