# Solids From Polylines

AutoCAD Civil 3D .NET plug-in targeting `net8.0-windows`.

Build `SolidsFromPolylines.csproj`. The build creates
`bin/Debug/SolidsFromPolylines.bundle`. Copy that entire `.bundle` folder into
`%AppData%\Autodesk\ApplicationPlugins`, then restart Civil 3D. The plug-in
loads automatically and creates a **Solids** ribbon tab with a button for each
command. No manual ribbon customization is needed.

A DLL by itself does not make Civil 3D load a plug-in just because it is copied
to an arbitrary add-in folder. For automatic loading, install the generated
`.bundle` folder under `ApplicationPlugins`. For a one-time manual load, use
`NETLOAD` to select the DLL; the Solids tab is created when the ribbon is ready.
Commands remain available by name at the Civil 3D command line:
`MARKING_PREP`, `MARKING_SOLIDS`, `MARKING_BLOCK`, and `MARKING_BLOCK_LAYER`.

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
- `MARKING_SOLIDS` opens a Windows Forms surface picker, selecting the first
  surface by default. It samples the chosen surface along each selected
  boundary group at its centroid, then directly extrudes the planar region by
  the signed thickness. Nested boundaries are cut from the region before
  extrusion. Boundaries are erased only after solid creation succeeds;
  failed boundaries are retained and colored yellow.
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
