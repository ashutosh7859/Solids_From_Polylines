# Solids From Polylines

AutoCAD Civil 3D .NET plug-in targeting `net8.0-windows`.

Build `SolidsFromPolylines.csproj`. The build creates
`bin/Debug/SolidsFromPolylines.bundle`. Copy that entire `.bundle` folder into
`%AppData%\Autodesk\ApplicationPlugins`, then restart Civil 3D. The plug-in
loads automatically and creates a **Solids** ribbon tab with a button for each
command. The ribbon icons are embedded in the DLL. No manual ribbon
customization is needed.

A DLL by itself does not make Civil 3D load a plug-in just because it is copied
to an arbitrary add-in folder. For automatic loading, install the generated
`.bundle` folder under `ApplicationPlugins`. For a one-time manual load, use
`NETLOAD` to select the DLL; the Solids tab is created when the ribbon is ready.
Commands remain available by name at the Civil 3D command line:
`MARKING_PREP`, `MARKING_PREP_BATCH`, `MARKING_CURVES`, `MARKING_SOLIDS`,
`MARKING_BLOCK`, and `MARKING_BLOCK_LAYER`.

## Commands

- `MARKING_PREP` creates closed marking-boundary polylines from selected
  lightweight polylines. Explicitly choose continuous or dash output; pressing
  Enter does not select a default. Dashed output asks for dash and gap lengths.
  The source polyline's constant/global width is used as the marking width.
  Boundaries are created on the `Marking_Prep` layer,
  which is created if needed. Selected lines with overlapping straight
  segments are retained and colored magenta for review. Other source polylines
  are erased only when boundary creation succeeds; failed inputs are retained
  and colored yellow.
- `MARKING_PREP_BATCH` prepares selected polylines or groups from the current
  space by layer, linetype, and width. Its modeless Windows Forms dialog lets
  users continue navigating the drawing, select a group's polylines for visual
  review, and refresh the table. Changes to source objects mark the snapshot
  stale; preparation stays disabled until refresh, which preserves settings for
  unchanged groups and leaves new groups unchecked. The dialog lets users
  include groups and choose continuous or dashed output, with dash and gap
  lengths for dashed groups. Select Ready highlights all processable groups;
  Ctrl/Shift row selections let users apply the toolbar output and dash/gap
  settings to multiple groups. Toggling Include on a selected row toggles all
  selected rows. Continuous output clears and disables that row's dash/gap
  cells. Unsupported widths and invalid geometry are summarized by reason;
  overlaps are checked only among included groups. Exceptions are retained for
  review, and a confirmation summarizes the operation before source polylines
  are erased. Uniform per-segment widths are supported. Varying widths are
  flagged and skipped.
- `MARKING_CURVES` converts selected standalone circles and full ellipses into
  closed lightweight polylines. Circles use exact arc segments; ellipses use
  adaptive chord sampling at the requested maximum deviation. Only horizontal
  circles and full ellipses are supported. Choose whether to keep or delete
  each source curve; unsupported curves are retained and reported.
- `MARKING_SOLIDS` opens a Windows Forms surface picker, selecting the first
  surface by default. It samples the chosen surface along each selected
  boundary group at its centroid, then directly extrudes the planar region by
  the signed thickness. When nested boundaries are detected, choose whether to
  create the existing annular solid (the Enter/default choice), create that
  solid plus a separately thickened inner solid, or skip nested groups while
  processing unrelated boundaries. Open or non-horizontal polylines are
  skipped and retained while other valid boundaries continue. If an open
  polyline's extents contain or are contained by a selected closed boundary at
  the same elevation, both are skipped as a possibly incomplete annular pair.
  Boundaries are erased only after solid creation succeeds; failed or skipped
  boundaries are retained and colored yellow.
- `MARKING_BLOCK` prompts for one 3D solid, then collects all 3D solids
  directly in Model Space on that solid's layer. It replaces them in place
  with one block reference using the origin as the block base point; no base
  point is requested.
- `MARKING_BLOCK_LAYER` lets you select block references and choose an existing
  layer or type a new one in a dialog. It changes the selected references'
  layer. An optional checkbox also changes the contents' layers; definitions
  are copied for the selected instances so other insertions are unaffected.
  When changing contents, dynamic blocks and external references are skipped.
  Nested dynamic blocks and external references inside copied definitions are
  left unchanged.

Line and circular-arc segments are handled by AutoCAD's exact polyline offset
and region APIs during boundary preparation. Open paths produce capped
boundaries. Closed paths produce inner and outer loops. The preparation command
supports horizontal WCS polylines with positive constant width; unsupported or
invalid paths are retained and colored yellow.

Codespaces runs Linux and cannot load the plug-in into Civil 3D. Load it into a
Civil 3D release whose managed host supports .NET 8 and validate runtime
behavior there.
