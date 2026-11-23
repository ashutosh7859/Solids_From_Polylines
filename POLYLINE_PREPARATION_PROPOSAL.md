# Batch polyline preparation proposal

## First implementation

The initial workflow is a separate `MARKING_PREP_BATCH` command with a ribbon
button. Users choose a selection or all lightweight polylines in the current
space. The form summarizes groups by layer, source linetype, and width, with
one row per group and counts instead of an entity list. Users can include
groups, set continuous or dashed output, and apply common dash/gap values to
multiple selected rows. Select Ready also highlights all eligible rows, while
Clear All clears both inclusion and row selection. Dash and gap values are in
drawing units. Toggling Include on any selected row applies that checkbox state
to all selected rows. Continuous output clears and disables the row's dash/gap
cells.

Positive constant widths and uniform per-segment widths are supported. Varying
widths, invalid geometry, and overlapping lines are flagged and retained for
review. Overlap detection runs only among included groups. A confirmation
summary appears before conversion because successful source polylines are
erased. Width normalization and automatic interpretation of source linetype
patterns remain outside this first pass.

## Current behavior

`MARKING_PREP` currently prepares a selected set of lightweight polylines using one explicit output choice: continuous or dashed. Dashed output prompts for dash and gap lengths. The source polyline's constant/global width becomes the marking width. The command supports horizontal WCS polylines with positive constant width; unsupported or invalid paths are retained and colored yellow, while overlapping straight segments are retained and colored magenta for review.

## Proposal

Add a batch preparation workflow that lets users apply different preparation rules to groups in one run. Keep the existing command available for simple one-rule preparation, and add a separate UI step for multi-rule preparation so its grouping, exception handling, and preview do not overload the simple path.

Users choose a scope (current selection, selected layers, or all eligible polylines), review a compact summary grouped by layer and source linetype, optionally split or filter by width, assign rules to one or more groups, and preview the result before applying it. Show counts per group instead of requiring users to configure every polyline individually. Rules should be reusable across groups.

For each group, users can choose continuous or dashed output. Dashed rules include dash length and gap with explicit drawing units. The preview reports which groups will be prepared, which settings apply, and how many entities will be skipped or need review.

## Width and geometry exceptions

Do not silently collapse varying segment widths into a single width. Detect such polylines and report them as an exception group. Candidate policies are preserve varying widths if the boundary algorithm supports them, apply a user-selected normalization rule, or skip for review. Decide this behavior before implementation.

Keep existing unsupported-geometry outcomes visible in the batch summary. In particular, distinguish invalid/unsupported paths, overlapping straight segments, non-horizontal geometry, zero or negative width, short paths where the dash/gap pattern cannot fit, and closed polylines if their dash placement differs from open paths. Group exceptions by reason and count; allow entity-level inspection as a drill-down. Users should be able to skip or apply a supported policy per exception type, with no silent conversion.

## Suggested workflow

1. Choose selection, layers, or all eligible polylines.
2. Review groups by layer and source linetype; optionally split/filter by constant width.
3. Assign continuous or dashed output and dash/gap values to groups. Allow one rule to cover several groups.
4. Review eligible, skipped, and exception counts and the settings that will be applied.
5. Run preparation and report actual results, retaining failed inputs as the current command does.

## Decisions needed

1. Should this be a new command and form, or a multi-rule mode added to `MARKING_PREP`?
2. For varying widths, can the boundary creation APIs preserve them, and if so what marking-width semantics should apply?
3. Should source linetype determine the default output rule, or should users always choose explicitly as they do today?
4. What minimum-length rule should apply when a dash and gap do not fit a path?
5. How should closed paths and non-horizontal paths be handled in batch mode?
6. Which width and linetype properties should form groups by default, and which should remain optional filters to avoid excessive group counts?

## Suggested first release

Start with explicit scope selection, grouping by layer and source linetype, optional constant-width filtering, per-group continuous/dashed settings, and a preview with exception counts. Reuse the existing geometry preparation logic and preserve its current failure-review behavior. Add varying-width conversion only after its geometry behavior is defined and verified.
