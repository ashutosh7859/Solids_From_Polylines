using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(SolidsFromPolylines.MarkingCommands))]

namespace SolidsFromPolylines
{
    [SupportedOSPlatform("windows")]
    public class MarkingCommands : IExtensionApplication
    {
        private const double GeometryTolerance = 1e-9;
        private const string PreparationLayerName = "Marking_Prep";
        private static BatchPreparationSession activeBatchPreparation;

        public void Initialize()
        {
            var document = Autodesk.AutoCAD.ApplicationServices.Application
                .DocumentManager.MdiActiveDocument;
            document?.Editor.WriteMessage(
                "\nSolidsFromPolylines loaded. Commands: MARKING_PREP, " +
                "MARKING_PREP_BATCH, " +
                "MARKING_CURVES, " +
                "MARKING_SOLIDS, MARKING_BLOCK, MARKING_BLOCK_LAYER.");
#if !NO_RIBBON
            SolidsRibbon.Initialize();
#endif
        }

        public void Terminate()
        {
#if !NO_RIBBON
            SolidsRibbon.Terminate();
#endif
        }

        [CommandMethod("MARKING_PREP")]
        public void PrepareMarkings()
        {
            var document = Autodesk.AutoCAD.ApplicationServices.Application
                .DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            Editor editor = document.Editor;
            var modeOptions = new PromptKeywordOptions(
                "\nCreate [Continuous/Dash] marking boundaries: ");
            modeOptions.Keywords.Add("Continuous");
            modeOptions.Keywords.Add("Dash");
            PromptResult modeResult = editor.GetKeywords(modeOptions);
            if (modeResult.Status != PromptStatus.OK)
            {
                return;
            }

            bool dashed = modeResult.StringResult.Equals(
                "Dash", StringComparison.OrdinalIgnoreCase);
            double dashLength = 0;
            double gapLength = 0;
            if (dashed)
            {
                PromptDoubleResult dashResult = editor.GetDouble(
                    new PromptDoubleOptions("\nEnter dash length: ")
                    {
                        AllowZero = false,
                        AllowNegative = false
                    });
                if (dashResult.Status != PromptStatus.OK ||
                    !IsFinite(dashResult.Value))
                {
                    editor.WriteMessage("\nDash length must be a positive finite number.");
                    return;
                }

                PromptDoubleResult gapResult = editor.GetDouble(
                    new PromptDoubleOptions("\nEnter gap length: ")
                    {
                        AllowZero = false,
                        AllowNegative = false
                    });
                if (gapResult.Status != PromptStatus.OK ||
                    !IsFinite(gapResult.Value))
                {
                    editor.WriteMessage("\nGap length must be a positive finite number.");
                    return;
                }

                dashLength = dashResult.Value;
                gapLength = gapResult.Value;
            }

            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect marking polylines: "
                },
                new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
                }));
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            var selectedIds = new List<ObjectId>();
            foreach (SelectedObject selected in selection.Value)
            {
                if (selected != null && !selected.ObjectId.IsNull)
                {
                    selectedIds.Add(selected.ObjectId);
                }
            }

            HashSet<ObjectId> overlappingIds = FindOverlappingPolylines(
                document.Database, selectedIds);
            foreach (ObjectId overlappingId in overlappingIds)
            {
                MarkPolylineAsOverlapping(
                    document.Database, overlappingId, editor);
                editor.WriteMessage(
                    $"\nPolyline {overlappingId.Handle} overlaps another selected " +
                    "polyline; it was retained and colored magenta.");
            }

            int succeeded = 0;
            int failed = 0;
            int boundariesCreated = 0;
            foreach (ObjectId selectedId in selectedIds)
            {
                if (overlappingIds.Contains(selectedId))
                {
                    continue;
                }

                try
                {
                    int made = PrepareOne(
                        document.Database,
                        selectedId,
                        dashed,
                        dashLength,
                        gapLength,
                        out bool needsReview);
                    boundariesCreated += made;
                    succeeded++;
                    if (needsReview)
                    {
                        editor.WriteMessage(
                            $"\nPolyline {selectedId.Handle} was prepared using " +
                            "closed-offset overlap repair; the original was retained " +
                            "and colored magenta for review.");
                    }
                }
                catch (System.Exception exception)
                {
                    failed++;
                    MarkPolylineAsFailed(
                        document.Database, selectedId, editor);
                    editor.WriteMessage(
                        $"\nPolyline {selectedId.Handle} was retained and " +
                        $"colored yellow: {exception.Message}");
                }
            }

            editor.WriteMessage(
                $"\nPrepared {succeeded} polyline(s) into {boundariesCreated} " +
                $"closed boundary/boundaries; {failed} failed, " +
                $"{overlappingIds.Count} skipped for overlap review.");
        }

        [CommandMethod("MARKING_PREP_BATCH")]
        public void PrepareMarkingsBatch()
        {
            var document = Autodesk.AutoCAD.ApplicationServices.Application
                .DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            if (activeBatchPreparation != null)
            {
                activeBatchPreparation.Focus();
                return;
            }

            Editor editor = document.Editor;
            var scopeOptions = new PromptKeywordOptions(
                "\nBatch source [Selection/Layers]: ");
            scopeOptions.Keywords.Add("Selection");
            scopeOptions.Keywords.Add("Layers");
            PromptResult scopeResult = editor.GetKeywords(scopeOptions);
            if (scopeResult.Status != PromptStatus.OK)
            {
                return;
            }

            var polylineFilter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
            });
            bool allPolylinesScope = scopeResult.StringResult.Equals(
                "Layers", StringComparison.OrdinalIgnoreCase);
            PromptSelectionResult selection = allPolylinesScope
                ? editor.SelectAll(polylineFilter)
                : editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect marking polylines: "
                    },
                    polylineFilter);
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            var sourceIds = new List<ObjectId>();
            foreach (SelectedObject selected in selection.Value)
            {
                if (selected != null && !selected.ObjectId.IsNull)
                {
                    sourceIds.Add(selected.ObjectId);
                }
            }

            if (sourceIds.Count == 0)
            {
                editor.WriteMessage("\nNo lightweight polylines were found.");
                return;
            }

            List<BatchPreparationGroup> groups = BuildBatchPreparationGroups(
                document.Database, sourceIds);
            foreach (BatchPreparationGroup group in groups)
            {
                group.Included = group.EligibleIds.Count > 0;
            }

            activeBatchPreparation = new BatchPreparationSession(
                document, sourceIds, allPolylinesScope, groups);
            activeBatchPreparation.Show();
        }

        [CommandMethod("MARKING_PREP_BATCH_REFRESH")]
        public void RefreshBatchPreparation()
        {
            activeBatchPreparation?.Refresh();
        }

        [CommandMethod("MARKING_PREP_BATCH_SELECT")]
        public void SelectBatchPreparationGroup()
        {
            activeBatchPreparation?.SelectRequestedGroup();
        }

        [CommandMethod("MARKING_PREP_BATCH_APPLY")]
        public void ApplyBatchPreparation()
        {
            activeBatchPreparation?.Apply();
        }

        private static List<BatchPreparationGroup> BuildBatchPreparationGroups(
            Database database,
            IEnumerable<ObjectId> sourceIds)
        {
            var groupsByKey = new Dictionary<string, BatchPreparationGroup>(
                StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId sourceId in sourceIds)
            {
                if (sourceId.IsNull || !sourceId.IsValid || sourceId.IsErased)
                {
                    continue;
                }

                string layerName = "(unknown)";
                string linetypeName = "(unknown)";
                string widthLabel = "Unsupported";
                string widthKey = "unsupported";
                double width = 0;
                string issue = null;

                try
                {
                    using (Transaction transaction =
                           database.TransactionManager.StartTransaction())
                    {
                        var source = transaction.GetObject(
                            sourceId, OpenMode.ForRead) as Polyline;
                        if (source == null)
                        {
                            issue = "not a lightweight polyline";
                        }
                        else
                        {
                            layerName = source.Layer;
                            linetypeName = string.IsNullOrWhiteSpace(source.Linetype)
                                ? "ByLayer"
                                : source.Linetype;
                            if (!TryGetBatchPolylineWidth(
                                    source, out width, out issue))
                            {
                                widthLabel = issue == "Varying width is not supported"
                                    ? "Varies"
                                    : "Unsupported";
                                widthKey = widthLabel.ToLowerInvariant();
                            }
                            else
                            {
                                widthLabel = width.ToString(
                                    "0.###", CultureInfo.InvariantCulture);
                                widthKey = width.ToString(
                                    "R", CultureInfo.InvariantCulture);
                                issue = GetBatchGeometryIssue(source);
                            }
                        }
                    }
                }
                catch (System.Exception exception)
                {
                    issue = $"Invalid geometry: {exception.Message}";
                }

                string key = string.Concat(
                    layerName, "\u001f", linetypeName, "\u001f", widthKey);
                if (!groupsByKey.TryGetValue(
                        key, out BatchPreparationGroup group))
                {
                    group = new BatchPreparationGroup(
                        layerName, linetypeName, widthLabel, key, width);
                    groupsByKey.Add(key, group);
                }

                if (string.IsNullOrEmpty(issue))
                {
                    group.EligibleIds.Add(sourceId);
                }
                else
                {
                    group.AddException(sourceId, issue);
                }
            }

            var groups = new List<BatchPreparationGroup>(groupsByKey.Values);
            groups.Sort((first, second) =>
            {
                int layerComparison = StringComparer.OrdinalIgnoreCase.Compare(
                    first.LayerName, second.LayerName);
                if (layerComparison != 0)
                {
                    return layerComparison;
                }

                int linetypeComparison = StringComparer.OrdinalIgnoreCase.Compare(
                    first.LinetypeName, second.LinetypeName);
                return linetypeComparison != 0
                    ? linetypeComparison
                    : StringComparer.OrdinalIgnoreCase.Compare(
                        first.WidthLabel, second.WidthLabel);
            });
            return groups;
        }

        private static List<ObjectId> GetAllBatchPolylineIds(Editor editor)
        {
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
            });
            PromptSelectionResult selection = editor.SelectAll(filter);
            var ids = new List<ObjectId>();
            if (selection.Status != PromptStatus.OK)
            {
                return ids;
            }

            foreach (SelectedObject selected in selection.Value)
            {
                if (selected != null && !selected.ObjectId.IsNull)
                {
                    ids.Add(selected.ObjectId);
                }
            }

            return ids;
        }

        private static bool HaveSameBatchSnapshot(
            List<BatchPreparationGroup> first,
            List<BatchPreparationGroup> second)
        {
            if (first.Count != second.Count)
            {
                return false;
            }

            var secondByKey = second.ToDictionary(
                group => group.Key, StringComparer.OrdinalIgnoreCase);
            foreach (BatchPreparationGroup firstGroup in first)
            {
                if (!secondByKey.TryGetValue(
                        firstGroup.Key, out BatchPreparationGroup secondGroup) ||
                    !firstGroup.EligibleIds.ToHashSet()
                        .SetEquals(secondGroup.EligibleIds) ||
                    !firstGroup.Exceptions.Keys.ToHashSet()
                        .SetEquals(secondGroup.Exceptions.Keys))
                {
                    return false;
                }

                foreach (KeyValuePair<ObjectId, string> exception
                         in firstGroup.Exceptions)
                {
                    if (!secondGroup.Exceptions.TryGetValue(
                            exception.Key, out string currentReason) ||
                        !string.Equals(
                            exception.Value, currentReason,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool ExecuteBatchPreparation(
            Autodesk.AutoCAD.ApplicationServices.Document document,
            List<BatchPreparationGroup> groups,
            Dictionary<ObjectId, BatchPreparationGroup> groupByObjectId)
        {
            Editor editor = document.Editor;
            var includedGroups = new List<BatchPreparationGroup>();
            var includedIds = new List<ObjectId>();
            foreach (BatchPreparationGroup group in groups)
            {
                if (!group.Included)
                {
                    continue;
                }

                includedGroups.Add(group);
                includedIds.AddRange(group.EligibleIds);
            }

            HashSet<ObjectId> overlappingIds = FindOverlappingPolylines(
                document.Database, includedIds);

            int readyCount = 0;
            int exceptionCount = 0;
            int dashedGroupCount = 0;
            foreach (BatchPreparationGroup group in includedGroups)
            {
                int groupOverlapCount = group.EligibleIds.Count(
                    overlappingIds.Contains);
                readyCount += group.EligibleIds.Count - groupOverlapCount;
                exceptionCount += group.Exceptions.Count + groupOverlapCount;
                if (group.Dashed && group.EligibleIds.Count > groupOverlapCount)
                {
                    dashedGroupCount++;
                }
            }

            string preview =
                $"Prepare {readyCount} polyline(s) across {includedGroups.Count} " +
                $"group(s)?\n\n{dashedGroupCount} group(s) use dashed output; " +
                $"{overlappingIds.Count} overlapping polyline(s) and " +
                $"{exceptionCount - overlappingIds.Count} other exception(s) " +
                "will be retained for review. Successfully prepared sources are " +
                "normally erased; closed-offset overlap repairs retain and color " +
                "their source magenta for review.";
            if (Forms.MessageBox.Show(
                    preview,
                    "Confirm batch preparation",
                    Forms.MessageBoxButtons.YesNo,
                    Forms.MessageBoxIcon.Warning) != Forms.DialogResult.Yes)
            {
                return false;
            }

            foreach (ObjectId overlappingId in overlappingIds)
            {
                if (groupByObjectId.TryGetValue(
                        overlappingId, out BatchPreparationGroup group))
                {
                    group.AddException(overlappingId, "overlap needs review");
                }
            }

            int prepared = 0;
            int failed = 0;
            int overlapSkipped = 0;
            int repairedForReview = 0;
            int boundariesCreated = 0;
            foreach (BatchPreparationGroup group in groups)
            {
                if (!group.Included)
                {
                    continue;
                }

                foreach (KeyValuePair<ObjectId, string> exception in group.Exceptions)
                {
                    if (exception.Value.IndexOf(
                            "overlap", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        MarkPolylineAsOverlapping(
                            document.Database, exception.Key, editor);
                        overlapSkipped++;
                    }
                    else
                    {
                        MarkPolylineAsFailed(
                            document.Database, exception.Key, editor);
                        failed++;
                    }

                    editor.WriteMessage(
                        $"\nPolyline {exception.Key.Handle} was retained for review: " +
                        exception.Value + ".");
                }

                foreach (ObjectId sourceId in group.EligibleIds)
                {
                    try
                    {
                        int made = PrepareOne(
                            document.Database,
                            sourceId,
                            group.Dashed,
                            group.DashLength,
                            group.GapLength,
                            out bool needsReview,
                            group.Width);
                        boundariesCreated += made;
                        prepared++;
                        if (needsReview)
                        {
                            repairedForReview++;
                            editor.WriteMessage(
                                $"\nPolyline {sourceId.Handle} was prepared using " +
                                "closed-offset overlap repair; the original was " +
                                "retained and colored magenta for review.");
                        }
                    }
                    catch (System.Exception exception)
                    {
                        failed++;
                        MarkPolylineAsFailed(
                            document.Database, sourceId, editor);
                        editor.WriteMessage(
                            $"\nPolyline {sourceId.Handle} was retained and " +
                            $"colored yellow: {exception.Message}");
                    }
                }
            }

            editor.WriteMessage(
                $"\nBatch preparation created {boundariesCreated} closed " +
                $"boundary/boundaries from {prepared} polyline(s); {failed} " +
                $"failed or flagged, {overlapSkipped} skipped for source overlap " +
                $"review, {repairedForReview} repaired output(s) retained for review.");
            return true;
        }

        private sealed class BatchPreparationSession : IDisposable
        {
            private readonly Autodesk.AutoCAD.ApplicationServices.Document document;
            private readonly bool allPolylinesScope;
            private readonly BatchPreparationDialog dialog;
            private readonly HashSet<ObjectId> trackedIds;
            private List<ObjectId> sourceIds;
            private List<BatchPreparationGroup> groups;
            private BatchPreparationGroup requestedGroup;
            private bool stale;
            private bool requestQueued;
            private bool applying;
            private bool disposed;

            public BatchPreparationSession(
                Autodesk.AutoCAD.ApplicationServices.Document document,
                List<ObjectId> sourceIds,
                bool allPolylinesScope,
                List<BatchPreparationGroup> groups)
            {
                this.document = document;
                this.sourceIds = new List<ObjectId>(sourceIds);
                this.allPolylinesScope = allPolylinesScope;
                this.groups = groups;
                trackedIds = new HashSet<ObjectId>(sourceIds);
                dialog = new BatchPreparationDialog(groups);
                dialog.RefreshRequested += RequestRefresh;
                dialog.PrepareRequested += RequestApply;
                dialog.SelectGroupRequested += RequestSelectGroup;
                dialog.DialogClosed += Dispose;
                document.Database.ObjectModified += OnObjectModified;
                document.Database.ObjectErased += OnObjectErased;
                document.Database.ObjectAppended += OnObjectAppended;
            }

            public void Show()
            {
                try
                {
                    Autodesk.AutoCAD.ApplicationServices.Application
                        .ShowModelessDialog(dialog);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Focus()
            {
                if (dialog.IsDisposed)
                {
                    return;
                }

                if (dialog.WindowState == Forms.FormWindowState.Minimized)
                {
                    dialog.WindowState = Forms.FormWindowState.Normal;
                }

                dialog.Activate();
            }

            public void Refresh()
            {
                if (disposed)
                {
                    return;
                }

                try
                {
                    dialog.CaptureSettings();
                    Dictionary<string, BatchPreparationGroup> previous =
                        groups.ToDictionary(
                            group => group.Key,
                            StringComparer.OrdinalIgnoreCase);
                    List<ObjectId> refreshedIds = GetCurrentSourceIds();
                    List<BatchPreparationGroup> refreshedGroups =
                        BuildBatchPreparationGroups(document.Database, refreshedIds);
                    foreach (BatchPreparationGroup group in refreshedGroups)
                    {
                        if (!previous.TryGetValue(
                                group.Key, out BatchPreparationGroup oldGroup))
                        {
                            continue;
                        }

                        group.Included = oldGroup.Included;
                        group.Dashed = oldGroup.Dashed;
                        group.DashLength = oldGroup.DashLength;
                        group.GapLength = oldGroup.GapLength;
                    }

                    sourceIds = refreshedIds;
                    trackedIds.Clear();
                    trackedIds.UnionWith(refreshedIds);
                    groups = refreshedGroups;
                    stale = false;
                    dialog.SetGroups(groups);
                    dialog.SetStale(false);
                }
                catch (System.Exception exception)
                {
                    ShowError("Unable to refresh the batch groups", exception);
                }
                finally
                {
                    CompleteRequest();
                }
            }

            public void SelectRequestedGroup()
            {
                try
                {
                    if (requestedGroup == null)
                    {
                        return;
                    }

                    ObjectId[] ids = requestedGroup.EligibleIds
                        .Concat(requestedGroup.Exceptions.Keys)
                        .Where(id => id.IsValid && !id.IsErased)
                        .Distinct()
                        .ToArray();
                    document.Editor.SetImpliedSelection(ids);
                    document.Editor.UpdateScreen();
                    document.Editor.WriteMessage(
                        $"\nSelected {ids.Length} polyline(s) from layer " +
                        $"'{requestedGroup.LayerName}'.");
                }
                catch (System.Exception exception)
                {
                    ShowError("Unable to select the group in the drawing", exception);
                }
                finally
                {
                    requestedGroup = null;
                    CompleteRequest();
                }
            }

            public void Apply()
            {
                if (disposed)
                {
                    return;
                }

                try
                {
                    if (stale)
                    {
                        dialog.SetStale(true);
                        return;
                    }

                    List<ObjectId> currentIds = GetCurrentSourceIds();
                    List<BatchPreparationGroup> currentGroups =
                        BuildBatchPreparationGroups(document.Database, currentIds);
                    if (!HaveSameBatchSnapshot(groups, currentGroups))
                    {
                        MarkStale();
                        return;
                    }

                    applying = true;
                    var groupByObjectId =
                        new Dictionary<ObjectId, BatchPreparationGroup>();
                    foreach (BatchPreparationGroup group in groups)
                    {
                        foreach (ObjectId id in group.EligibleIds)
                        {
                            groupByObjectId[id] = group;
                        }

                        foreach (ObjectId id in group.Exceptions.Keys)
                        {
                            groupByObjectId[id] = group;
                        }
                    }

                    if (ExecuteBatchPreparation(
                            document, groups, groupByObjectId))
                    {
                        Dispose();
                        dialog.Close();
                        return;
                    }

                    applying = false;
                }
                catch (System.Exception exception)
                {
                    applying = false;
                    ShowError("Unable to prepare the selected groups", exception);
                }
                finally
                {
                    CompleteRequest();
                }
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                document.Database.ObjectModified -= OnObjectModified;
                document.Database.ObjectErased -= OnObjectErased;
                document.Database.ObjectAppended -= OnObjectAppended;
                dialog.RefreshRequested -= RequestRefresh;
                dialog.PrepareRequested -= RequestApply;
                dialog.SelectGroupRequested -= RequestSelectGroup;
                dialog.DialogClosed -= Dispose;
                if (ReferenceEquals(activeBatchPreparation, this))
                {
                    activeBatchPreparation = null;
                }
            }

            private void RequestRefresh()
            {
                QueueCommand("MARKING_PREP_BATCH_REFRESH", "Refreshing groups...");
            }

            private void RequestSelectGroup(BatchPreparationGroup group)
            {
                requestedGroup = group;
                QueueCommand("MARKING_PREP_BATCH_SELECT", "Selecting group...");
            }

            private void RequestApply()
            {
                QueueCommand("MARKING_PREP_BATCH_APPLY", "Checking the drawing...");
            }

            private void QueueCommand(string command, string message)
            {
                if (disposed || requestQueued)
                {
                    return;
                }

                requestQueued = true;
                dialog.SetBusy(true, message);
                try
                {
                    document.SendStringToExecute(
                        $"._{command}\n", true, false, true);
                }
                catch (System.Exception exception)
                {
                    CompleteRequest();
                    ShowError("Unable to start the batch action", exception);
                }
            }

            private List<ObjectId> GetCurrentSourceIds()
            {
                if (allPolylinesScope)
                {
                    return GetAllBatchPolylineIds(document.Editor);
                }

                return sourceIds
                    .Where(id => id.IsValid && !id.IsErased)
                    .ToList();
            }

            private void OnObjectModified(object sender, ObjectEventArgs e)
            {
                if (!applying && e.DBObject != null &&
                    trackedIds.Contains(e.DBObject.ObjectId))
                {
                    MarkStale();
                }
            }

            private void OnObjectErased(object sender, ObjectErasedEventArgs e)
            {
                if (!applying && e.DBObject != null &&
                    trackedIds.Contains(e.DBObject.ObjectId))
                {
                    MarkStale();
                }
            }

            private void OnObjectAppended(object sender, ObjectEventArgs e)
            {
                if (!applying && allPolylinesScope &&
                    e.DBObject is Polyline)
                {
                    MarkStale();
                }
            }

            private void MarkStale()
            {
                if (disposed || stale)
                {
                    return;
                }

                stale = true;
                dialog.SetStale(true);
            }

            private void CompleteRequest()
            {
                requestQueued = false;
                if (!disposed && !dialog.IsDisposed)
                {
                    dialog.SetBusy(false);
                    if (stale)
                    {
                        dialog.SetStale(true);
                    }
                }
            }

            private void ShowError(string title, System.Exception exception)
            {
                document.Editor.WriteMessage(
                    $"\n{title}: {exception.Message}");
                if (!dialog.IsDisposed)
                {
                    Forms.MessageBox.Show(
                        dialog,
                        $"{title}:\n{exception.Message}",
                        "Batch Polyline Preparation",
                        Forms.MessageBoxButtons.OK,
                        Forms.MessageBoxIcon.Error);
                }
            }
        }

        [CommandMethod("MARKING_CURVES")]
        public void ConvertCirclesAndEllipses()
        {
            var document = Autodesk.AutoCAD.ApplicationServices.Application
                .DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            Editor editor = document.Editor;
            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect standalone circles and full ellipses: "
                },
                new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "CIRCLE,ELLIPSE")
                }));
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            var selectedIds = new List<ObjectId>();
            int ellipseCount = 0;
            foreach (SelectedObject selected in selection.Value)
            {
                if (selected == null || selected.ObjectId.IsNull)
                {
                    continue;
                }

                selectedIds.Add(selected.ObjectId);
                using (Transaction transaction = document.Database
                           .TransactionManager.StartOpenCloseTransaction())
                {
                    if (transaction.GetObject(selected.ObjectId, OpenMode.ForRead)
                        is Ellipse)
                    {
                        ellipseCount++;
                    }
                }
            }

            if (selectedIds.Count == 0)
            {
                editor.WriteMessage("\nNo circles or ellipses were selected.");
                return;
            }

            PromptDoubleResult widthResult = editor.GetDouble(
                new PromptDoubleOptions("\nEnter marking width: ")
                {
                    AllowZero = true,
                    AllowNegative = false
                });
            if (widthResult.Status != PromptStatus.OK ||
                !IsFinite(widthResult.Value) ||
                widthResult.Value < 0)
            {
                editor.WriteMessage(
                    "\nMarking width must be a non-negative finite number.");
                return;
            }

            double tolerance = 0.01;
            if (ellipseCount > 0)
            {
                PromptDoubleResult toleranceResult = editor.GetDouble(
                    new PromptDoubleOptions(
                        "\nMaximum ellipse chord deviation <0.01>: ")
                    {
                        AllowNegative = false,
                        AllowZero = false,
                        DefaultValue = 0.01,
                        UseDefaultValue = true
                    });
                if (toleranceResult.Status != PromptStatus.OK ||
                    !IsFinite(toleranceResult.Value) ||
                    toleranceResult.Value <= GeometryTolerance)
                {
                    return;
                }

                tolerance = toleranceResult.Value;
            }

            PromptKeywordOptions keepOptions = new PromptKeywordOptions(
                "\nOriginal curves [Keep/Delete] <Keep>: ")
            {
                AllowNone = true
            };
            keepOptions.Keywords.Add("Keep");
            keepOptions.Keywords.Add("Delete");
            PromptResult keepResult = editor.GetKeywords(keepOptions);
            if (keepResult.Status != PromptStatus.OK &&
                keepResult.Status != PromptStatus.None)
            {
                return;
            }

            bool deleteOriginals = string.Equals(
                keepResult.StringResult, "Delete", StringComparison.OrdinalIgnoreCase);
            int converted = 0;
            int skipped = 0;
            foreach (ObjectId id in selectedIds)
            {
                try
                {
                    using (Transaction transaction = document.Database
                               .TransactionManager.StartTransaction())
                    {
                        Entity source = transaction.GetObject(id, OpenMode.ForRead)
                            as Entity;
                        Polyline convertedPolyline = source switch
                        {
                            Circle circle => CircleToPolyline(
                                circle, widthResult.Value),
                            Ellipse ellipse => EllipseToPolyline(
                                ellipse, tolerance, widthResult.Value),
                            _ => null
                        };

                        if (convertedPolyline == null)
                        {
                            skipped++;
                            continue;
                        }

                        BlockTableRecord owner = (BlockTableRecord)transaction
                            .GetObject(source.OwnerId, OpenMode.ForWrite);
                        owner.AppendEntity(convertedPolyline);
                        transaction.AddNewlyCreatedDBObject(convertedPolyline, true);
                        if (deleteOriginals)
                        {
                            source.UpgradeOpen();
                            source.Erase();
                        }

                        transaction.Commit();
                        converted++;
                    }
                }
                catch (System.Exception exception)
                {
                    skipped++;
                    editor.WriteMessage(
                        $"\nCurve {id.Handle} was retained and skipped: " +
                        exception.Message);
                }
            }

            editor.WriteMessage(
                $"\nConverted {converted} circle/ellipse curve(s); " +
                $"{skipped} skipped. Originals were " +
                (deleteOriginals ? "deleted after successful conversion." : "kept."));
        }

        [CommandMethod("MARKING_SOLIDS")]
        public void CreateMarkingSolids()
        {
            var document = Autodesk.AutoCAD.ApplicationServices.Application
                .DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            Editor editor = document.Editor;
            List<SurfaceChoice> surfaces = GetSurfaces(document.Database);

            PromptDoubleResult heightResult = editor.GetDouble(
                new PromptDoubleOptions(
                    "\nEnter signed marking thickness (positive above surface, negative below): ")
                {
                    AllowZero = false,
                    AllowNegative = true
                });
            if (heightResult.Status != PromptStatus.OK ||
                !IsFinite(heightResult.Value) ||
                Math.Abs(heightResult.Value) <= GeometryTolerance)
            {
                editor.WriteMessage("\nMarking thickness must be finite and non-zero.");
                return;
            }

            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding =
                        "\nSelect marking boundary polylines (open ones will be skipped): "
                },
                new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
                }));
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            var selectedIds = new List<ObjectId>();
            foreach (SelectedObject selected in selection.Value)
            {
                if (selected != null && !selected.ObjectId.IsNull)
                {
                    selectedIds.Add(selected.ObjectId);
                }
            }

            if (selectedIds.Count == 0)
            {
                editor.WriteMessage("\nNo valid polylines were selected.");
                return;
            }

            BoundarySelectionResult boundarySelection =
                GetProcessableBoundarySelection(document.Database, selectedIds);
            foreach (BoundaryFailure failure in boundarySelection.Failures)
            {
                MarkPolylineAsFailed(
                    document.Database, failure.BoundaryId, editor);
                editor.WriteMessage(
                    $"\nBoundary {failure.BoundaryId.Handle} was skipped and " +
                    $"retained: {failure.Message}");
            }

            selectedIds = boundarySelection.ProcessableIds;
            if (selectedIds.Count == 0)
            {
                editor.WriteMessage(
                    "\nNo eligible closed horizontal boundaries remain to process.");
                return;
            }

            string nestedMode = "Annular";
            double innerHeight = 0;
            if (HasNestedBoundaryGroups(document.Database, selectedIds))
            {
                var nestedOptions = new PromptKeywordOptions(
                    "\nNested boundaries [Annular/AnnularInner/Skip] <Annular>: ")
                {
                    AllowNone = true
                };
                nestedOptions.Keywords.Add("Annular");
                nestedOptions.Keywords.Add("AnnularInner");
                nestedOptions.Keywords.Add("Skip");
                PromptResult nestedResult = editor.GetKeywords(nestedOptions);
                if (nestedResult.Status != PromptStatus.OK &&
                    nestedResult.Status != PromptStatus.None)
                {
                    return;
                }

                if (nestedResult.Status == PromptStatus.OK)
                {
                    nestedMode = nestedResult.StringResult;
                }

                if (nestedMode == "AnnularInner")
                {
                    PromptDoubleResult innerHeightResult = editor.GetDouble(
                        new PromptDoubleOptions(
                            "\nEnter signed thickness for inner solid: ")
                        {
                            AllowZero = false,
                            AllowNegative = true
                        });
                    if (innerHeightResult.Status != PromptStatus.OK ||
                        !IsFinite(innerHeightResult.Value) ||
                        Math.Abs(innerHeightResult.Value) <= GeometryTolerance)
                    {
                        return;
                    }

                    innerHeight = innerHeightResult.Value;
                }
            }

            // Complete all interactive Editor prompts before showing the modal
            // Windows form. Re-entering Editor.GetDouble/GetSelection directly
            // after a modal form closes can destabilize the AutoCAD command loop.
            SurfaceChoice chosenSurface;
            double? customElevation;
            using (var surfaceDialog = new SurfaceSelectionDialog(surfaces))
            {
                if (Autodesk.AutoCAD.ApplicationServices.Application
                        .ShowModalDialog(surfaceDialog) != Forms.DialogResult.OK)
                {
                    return;
                }

                if (surfaceDialog.UseCustomElevation)
                {
                    chosenSurface = null;
                    customElevation = surfaceDialog.CustomElevation;
                }
                else
                {
                    chosenSurface = surfaceDialog.SelectedSurface;
                    customElevation = null;
                    if (chosenSurface == null)
                    {
                        editor.WriteMessage("\nNo surface was selected.");
                        return;
                    }
                }
            }

            ObjectId? surfaceId = chosenSurface?.ObjectId;
            string elevationSource = customElevation.HasValue
                ? $"Custom elevation: {customElevation.Value}."
                : $"Surface: {chosenSurface!.Name}.";

            try
            {
                SolidCreationResult creationResult = CreateSolidsForBoundaries(
                    document.Database,
                    selectedIds,
                    surfaceId,
                    customElevation,
                    heightResult.Value,
                    nestedMode,
                    innerHeight);
                foreach (BoundaryFailure failure in creationResult.Failures)
                {
                    MarkPolylineAsFailed(
                        document.Database, failure.BoundaryId, editor);
                    editor.WriteMessage(
                        $"\nBoundary {failure.BoundaryId.Handle} failed and was " +
                        $"retained: {failure.Message}");
                }

                editor.WriteMessage(
                    $"\nCreated {creationResult.SolidsCreated} solid(s); " +
                    $"{boundarySelection.Failures.Count + creationResult.Failures.Count} " +
                    "boundary/boundary group(s) were skipped or failed. " +
                    $"{elevationSource} " +
                    "Boundaries were erased only for successful solid groups.");
            }

            catch (System.Exception exception)
            {
                foreach (ObjectId boundaryId in selectedIds)
                {
                    MarkPolylineAsFailed(document.Database, boundaryId, editor);
                }

                editor.WriteMessage(
                    $"\nSolid creation failed; selected boundaries were retained " +
                    $"and colored yellow: {exception.Message}");
            }
        }

        [CommandMethod("MARKING_BLOCK")]
        public void BlockSolidsOnLayer()
        {
            var document = Autodesk.AutoCAD.ApplicationServices.Application
                .DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            Editor editor = document.Editor;
            var entityOptions = new PromptEntityOptions(
                "\nSelect a 3D solid to identify its layer: ");
            entityOptions.SetRejectMessage("\nSelected object must be a 3D solid.");
            entityOptions.AddAllowedClass(typeof(Solid3d), true);
            PromptEntityResult entityResult = editor.GetEntity(entityOptions);
            if (entityResult.Status != PromptStatus.OK)
            {
                return;
            }

            Database database = document.Database;
            try
            {
                using (Transaction transaction =
                       database.TransactionManager.StartTransaction())
                {
                    var blockTable = (BlockTable)transaction.GetObject(
                        database.BlockTableId, OpenMode.ForRead);
                    ObjectId modelSpaceId =
                        blockTable[BlockTableRecord.ModelSpace];
                    var selectedSolid = transaction.GetObject(
                        entityResult.ObjectId, OpenMode.ForRead) as Solid3d;
                    if (selectedSolid == null)
                    {
                        editor.WriteMessage("\nThe selected object is not a 3D solid.");
                        return;
                    }

                    if (selectedSolid.OwnerId != modelSpaceId)
                    {
                        editor.WriteMessage(
                            "\nSelect a 3D solid directly in Model Space.");
                        return;
                    }

                    ObjectId layerId = selectedSolid.LayerId;
                    string layerName = selectedSolid.Layer;
                    var solidIds = new List<ObjectId>();
                    var modelSpace = (BlockTableRecord)transaction.GetObject(
                        modelSpaceId, OpenMode.ForRead);
                    foreach (ObjectId objectId in modelSpace)
                    {
                        if (transaction.GetObject(objectId, OpenMode.ForRead)
                                is Solid3d solid &&
                            solid.LayerId == layerId)
                        {
                            solidIds.Add(objectId);
                        }
                    }

                    if (solidIds.Count == 0)
                    {
                        editor.WriteMessage(
                            "\nNo 3D solids were found on the selected layer.");
                        return;
                    }

                    string blockName =
                        $"MARKING_LAYER_SOLIDS_{Guid.NewGuid():N}";
                    var blockDefinition = new BlockTableRecord
                    {
                        Name = blockName,
                        Origin = Point3d.Origin
                    };
                    blockTable.UpgradeOpen();
                    ObjectId blockDefinitionId = blockTable.Add(blockDefinition);
                    transaction.AddNewlyCreatedDBObject(blockDefinition, true);

                    foreach (ObjectId solidId in solidIds)
                    {
                        var original = (Solid3d)transaction.GetObject(
                            solidId, OpenMode.ForRead);
                        var clone = (Solid3d)original.Clone();
                        blockDefinition.AppendEntity(clone);
                        transaction.AddNewlyCreatedDBObject(clone, true);
                    }

                    modelSpace.UpgradeOpen();
                    var blockReference = new BlockReference(
                        Point3d.Origin, blockDefinitionId);
                    blockReference.SetDatabaseDefaults(database);
                    blockReference.LayerId = layerId;
                    modelSpace.AppendEntity(blockReference);
                    transaction.AddNewlyCreatedDBObject(blockReference, true);

                    foreach (ObjectId solidId in solidIds)
                    {
                        var original = (Solid3d)transaction.GetObject(
                            solidId, OpenMode.ForWrite);
                        original.Erase();
                    }

                    transaction.Commit();
                    editor.WriteMessage(
                        $"\nReplaced {solidIds.Count} 3D solid(s) on layer " +
                        $"\"{layerName}\" with block \"{blockName}\".");
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    $"\nCould not create the layer solids block: {exception.Message}");
            }
        }

        [CommandMethod("MARKING_BLOCK_LAYER")]
        public void AssignLayerToBlocks()
        {
            var document = Autodesk.AutoCAD.ApplicationServices.Application
                .DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            Editor editor = document.Editor;
            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect block references: "
                },
                new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "INSERT")
                }));
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            var selectedIds = new List<ObjectId>();
            foreach (SelectedObject selected in selection.Value)
            {
                if (selected != null && !selected.ObjectId.IsNull)
                {
                    selectedIds.Add(selected.ObjectId);
                }
            }

            if (selectedIds.Count == 0)
            {
                editor.WriteMessage("\nNo block references were selected.");
                return;
            }

            List<string> layerNames = GetLayerNames(document.Database);
            string commonLayerName = GetCommonBlockLayer(
                document.Database, selectedIds);
            using (var dialog = new BlockLayerAssignmentDialog(
                       layerNames, selectedIds.Count, commonLayerName))
            {
                if (Autodesk.AutoCAD.ApplicationServices.Application
                        .ShowModalDialog(dialog) != Forms.DialogResult.OK)
                {
                    return;
                }

                string targetLayerName = dialog.TargetLayerName;
                bool changeContents = dialog.ChangeContents;
                bool targetLayerExists = layerNames.Exists(
                    layerName => layerName.Equals(
                        targetLayerName, StringComparison.OrdinalIgnoreCase));
                if (!targetLayerExists && !IsValidLayerName(targetLayerName))
                {
                    editor.WriteMessage(
                        "\nThe target layer name is empty or contains invalid characters.");
                    return;
                }

                int updated = 0;
                int skipped = 0;
                int definitionsCloned = 0;
                int nestedDefinitionsSkipped = 0;
                try
                {
                    using (Transaction transaction =
                           document.Database.TransactionManager.StartTransaction())
                    {
                        ObjectId targetLayerId = GetOrCreateLayer(
                            transaction,
                            document.Database,
                            targetLayerName);
                        var definitionCopies =
                            new Dictionary<ObjectId, ObjectId>();

                        foreach (ObjectId selectedId in selectedIds)
                        {
                            var blockReference = transaction.GetObject(
                                selectedId, OpenMode.ForWrite) as BlockReference;
                            if (blockReference == null)
                            {
                                skipped++;
                                continue;
                            }

                            if (changeContents &&
                                (blockReference.IsDynamicBlock ||
                                 IsExternalBlockDefinition(
                                     transaction,
                                     blockReference.BlockTableRecord)))
                            {
                                skipped++;
                                continue;
                            }

                            if (changeContents)
                            {
                                blockReference.BlockTableRecord =
                                    CloneBlockDefinitionForLayer(
                                        transaction,
                                        document.Database,
                                        blockReference.BlockTableRecord,
                                        targetLayerId,
                                        definitionCopies,
                                        ref definitionsCloned,
                                        ref nestedDefinitionsSkipped);
                                SetBlockAttributeLayers(
                                    transaction,
                                    blockReference,
                                    targetLayerId);
                            }

                            blockReference.LayerId = targetLayerId;
                            updated++;
                        }

                        if (updated == 0)
                        {
                            editor.WriteMessage(
                                "\nNo compatible block references were updated. " +
                                "Dynamic blocks and external references cannot have " +
                                "their contents isolated.");
                            return;
                        }

                        transaction.Commit();
                    }

                    editor.WriteMessage(
                        $"\nAssigned layer \"{targetLayerName}\" to {updated} " +
                        $"block reference(s); {skipped} skipped.");
                    if (changeContents)
                    {
                        editor.WriteMessage(
                            $"\nCreated {definitionsCloned} selected-instance block " +
                            $"definition(s).");
                        if (nestedDefinitionsSkipped > 0)
                        {
                            editor.WriteMessage(
                                $"\n{nestedDefinitionsSkipped} nested dynamic or " +
                                "external block definition(s) were left unchanged.");
                        }
                    }
                }
                catch (System.Exception exception)
                {
                    editor.WriteMessage(
                        $"\nCould not assign the block layer: {exception.Message}");
                }
            }
        }

        private static List<string> GetLayerNames(Database database)
        {
            var layerNames = new List<string>();
            using (Transaction transaction =
                   database.TransactionManager.StartTransaction())
            {
                var layerTable = (LayerTable)transaction.GetObject(
                    database.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId layerId in layerTable)
                {
                    var layer = (LayerTableRecord)transaction.GetObject(
                        layerId, OpenMode.ForRead);
                    layerNames.Add(layer.Name);
                }
            }

            layerNames.Sort(StringComparer.OrdinalIgnoreCase);
            return layerNames;
        }

        private static string GetCommonBlockLayer(
            Database database,
            List<ObjectId> blockIds)
        {
            string commonLayerName = null;
            using (Transaction transaction =
                   database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId blockId in blockIds)
                {
                    if (transaction.GetObject(blockId, OpenMode.ForRead)
                        is not BlockReference blockReference)
                    {
                        continue;
                    }

                    if (commonLayerName == null)
                    {
                        commonLayerName = blockReference.Layer;
                    }
                    else if (!commonLayerName.Equals(
                                 blockReference.Layer,
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        return string.Empty;
                    }
                }
            }

            return commonLayerName ?? string.Empty;
        }

        private static bool IsValidLayerName(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return false;
            }

            const string invalidCharacters = "<>/\\\":;?*|=,";
            foreach (char character in layerName)
            {
                if (char.IsControl(character) ||
                    invalidCharacters.IndexOf(character) >= 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsExternalBlockDefinition(
            Transaction transaction,
            ObjectId blockTableRecordId)
        {
            var definition = (BlockTableRecord)transaction.GetObject(
                blockTableRecordId, OpenMode.ForRead);
            return definition.IsFromExternalReference ||
                definition.IsFromOverlayReference;
        }

        private static ObjectId CloneBlockDefinitionForLayer(
            Transaction transaction,
            Database database,
            ObjectId sourceDefinitionId,
            ObjectId layerId,
            Dictionary<ObjectId, ObjectId> definitionCopies,
            ref int definitionsCloned,
            ref int nestedDefinitionsSkipped)
        {
            if (definitionCopies.TryGetValue(
                    sourceDefinitionId, out ObjectId copiedDefinitionId))
            {
                return copiedDefinitionId;
            }

            var sourceDefinition = (BlockTableRecord)transaction.GetObject(
                sourceDefinitionId, OpenMode.ForRead);
            if (IsExternalBlockDefinition(transaction, sourceDefinitionId))
            {
                throw new InvalidOperationException(
                    "External block definitions cannot be copied for selected instances.");
            }

            var copiedDefinition = new BlockTableRecord
            {
                Name = $"LayerAssign_{Guid.NewGuid():N}",
                Origin = sourceDefinition.Origin,
                Units = sourceDefinition.Units,
                Comments = sourceDefinition.Comments,
                Explodable = sourceDefinition.Explodable,
                BlockScaling = sourceDefinition.BlockScaling
            };
            var blockTable = (BlockTable)transaction.GetObject(
                database.BlockTableId, OpenMode.ForWrite);
            copiedDefinitionId = blockTable.Add(copiedDefinition);
            transaction.AddNewlyCreatedDBObject(copiedDefinition, true);
            definitionCopies.Add(sourceDefinitionId, copiedDefinitionId);
            definitionsCloned++;

            foreach (ObjectId entityId in sourceDefinition)
            {
                var sourceEntity = transaction.GetObject(
                    entityId, OpenMode.ForRead) as Entity;
                if (sourceEntity == null)
                {
                    continue;
                }

                var copiedEntity = sourceEntity.Clone() as Entity;
                if (copiedEntity == null)
                {
                    throw new InvalidOperationException(
                        $"Could not copy an object in block \"{sourceDefinition.Name}\".");
                }

                copiedEntity.LayerId = layerId;
                if (sourceEntity is BlockReference sourceNestedReference &&
                    copiedEntity is BlockReference copiedNestedReference)
                {
                    if (sourceNestedReference.IsDynamicBlock ||
                        IsExternalBlockDefinition(
                            transaction,
                            sourceNestedReference.BlockTableRecord))
                    {
                        nestedDefinitionsSkipped++;
                    }
                    else
                    {
                        copiedNestedReference.BlockTableRecord =
                            CloneBlockDefinitionForLayer(
                                transaction,
                                database,
                                sourceNestedReference.BlockTableRecord,
                                layerId,
                                definitionCopies,
                                ref definitionsCloned,
                                ref nestedDefinitionsSkipped);
                    }
                }

                copiedDefinition.AppendEntity(copiedEntity);
                transaction.AddNewlyCreatedDBObject(copiedEntity, true);

                if (sourceEntity is BlockReference sourceBlockReference &&
                    copiedEntity is BlockReference copiedBlockReference)
                {
                    CloneBlockAttributes(
                        transaction,
                        sourceBlockReference,
                        copiedBlockReference,
                        layerId);
                }
            }

            return copiedDefinitionId;
        }

        private static void CloneBlockAttributes(
            Transaction transaction,
            BlockReference sourceBlockReference,
            BlockReference copiedBlockReference,
            ObjectId layerId)
        {
            foreach (ObjectId attributeId in sourceBlockReference.AttributeCollection)
            {
                var sourceAttribute = transaction.GetObject(
                    attributeId, OpenMode.ForRead) as AttributeReference;
                if (sourceAttribute == null)
                {
                    continue;
                }

                var copiedAttribute = sourceAttribute.Clone() as AttributeReference;
                if (copiedAttribute == null)
                {
                    throw new InvalidOperationException(
                        "Could not copy a block attribute.");
                }

                copiedAttribute.LayerId = layerId;
                copiedBlockReference.AttributeCollection.AppendAttribute(
                    copiedAttribute);
                transaction.AddNewlyCreatedDBObject(copiedAttribute, true);
            }
        }

        private static void SetBlockAttributeLayers(
            Transaction transaction,
            BlockReference blockReference,
            ObjectId layerId)
        {
            foreach (ObjectId attributeId in blockReference.AttributeCollection)
            {
                if (transaction.GetObject(
                        attributeId, OpenMode.ForWrite) is AttributeReference attribute)
                {
                    attribute.LayerId = layerId;
                }
            }
        }

        private static List<SurfaceChoice> GetSurfaces(Database database)
        {
            var surfaces = new List<SurfaceChoice>();
            ObjectIdCollection surfaceIds =
                CivilApplication.ActiveDocument.GetSurfaceIds();

            using (Transaction transaction =
                   database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in surfaceIds)
                {
                    if (transaction.GetObject(id, OpenMode.ForRead) is CivilSurface surface)
                    {
                        surfaces.Add(new SurfaceChoice(id, surface.Name));
                    }
                }
            }

            return surfaces;
        }

        private static bool TryGetBatchPolylineWidth(
            Polyline source,
            out double width,
            out string issue)
        {
            width = Math.Abs(source.ConstantWidth);
            issue = null;
            if (IsFinite(width) && width > GeometryTolerance)
            {
                return true;
            }

            double uniformWidth = 0;
            bool foundPositiveWidth = false;
            bool foundZeroWidth = false;
            int segmentCount = source.Closed
                ? source.NumberOfVertices
                : source.NumberOfVertices - 1;
            for (int index = 0; index < segmentCount; index++)
            {
                double startWidth = Math.Abs(source.GetStartWidthAt(index));
                double endWidth = Math.Abs(source.GetEndWidthAt(index));
                if (!IsFinite(startWidth) || !IsFinite(endWidth))
                {
                    issue = "Invalid width";
                    return false;
                }

                if (Math.Abs(startWidth - endWidth) > GeometryTolerance)
                {
                    issue = "Varying width is not supported";
                    return false;
                }

                if (startWidth <= GeometryTolerance)
                {
                    if (foundPositiveWidth)
                    {
                        issue = "Varying width is not supported";
                        return false;
                    }

                    foundZeroWidth = true;
                    continue;
                }

                if (foundZeroWidth)
                {
                    issue = "Varying width is not supported";
                    return false;
                }

                if (foundPositiveWidth &&
                    Math.Abs(startWidth - uniformWidth) > GeometryTolerance)
                {
                    issue = "Varying width is not supported";
                    return false;
                }

                uniformWidth = startWidth;
                foundPositiveWidth = true;
            }

            if (!foundPositiveWidth)
            {
                issue = "The source polyline must have a positive width";
                return false;
            }

            width = uniformWidth;
            return true;
        }

        private static string GetBatchGeometryIssue(Polyline source)
        {
            if (!source.Normal.IsEqualTo(Vector3d.ZAxis))
            {
                return "non-horizontal WCS geometry";
            }

            if (source.NumberOfVertices < (source.Closed ? 3 : 2))
            {
                return "too few vertices";
            }

            try
            {
                double totalLength = source.GetDistanceAtParameter(source.EndParam);
                if (!IsFinite(totalLength) || totalLength <= GeometryTolerance)
                {
                    return "no measurable length";
                }
            }
            catch
            {
                return "invalid geometry";
            }

            return null;
        }

        private static int PrepareOne(
            Database database,
            ObjectId sourceId,
            bool dashed,
            double dashLength,
            double gapLength,
            out bool needsReview,
            double? widthOverride = null)
        {
            needsReview = false;
            using (Transaction transaction =
                   database.TransactionManager.StartTransaction())
            {
                var source = transaction.GetObject(
                    sourceId, OpenMode.ForRead) as Polyline;
                if (source == null)
                {
                    throw new InvalidOperationException(
                        "The selected object is not a lightweight polyline.");
                }

                if (!source.Normal.IsEqualTo(Vector3d.ZAxis))
                {
                    throw new InvalidOperationException(
                        "Only polylines in a horizontal WCS plane are supported.");
                }

                if (source.NumberOfVertices < (source.Closed ? 3 : 2))
                {
                    throw new InvalidOperationException(
                        "The polyline has too few vertices.");
                }

                double width = Math.Abs(widthOverride ?? source.ConstantWidth);
                if (!IsFinite(width) || width <= GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        "The source polyline must have a positive global width.");
                }

                double totalLength =
                    source.GetDistanceAtParameter(source.EndParam);
                if (!IsFinite(totalLength) || totalLength <= GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        "The polyline has no measurable length.");
                }

                var boundaries = new List<Polyline>();
                try
                {
                    if (TryGetConvertedCircleGeometry(
                            source, out Point2d circleCenter,
                            out double circleRadius, out double circleStartAngle,
                            out double circleDirection))
                    {
                        boundaries.AddRange(dashed
                            ? CreateCircularDashBoundaries(
                                source, circleCenter, circleRadius,
                                circleStartAngle, circleDirection, width,
                                dashLength, gapLength)
                            : CreateCircularRingBoundaries(
                                source, circleCenter, circleRadius,
                                circleStartAngle, circleDirection, width));
                    }
                    else if (!dashed)
                    {
                        boundaries.AddRange(
                            CreateBoundariesFromPath(
                                source, source, width / 2.0, out needsReview));
                    }
                    else
                    {
                        double station = 0;
                        while (station < totalLength - GeometryTolerance)
                        {
                            double finish = Math.Min(totalLength, station + dashLength);
                            using (Polyline dashPath =
                                   CreatePathSlice(source, station, finish))
                            {
                                boundaries.AddRange(
                                    CreateBoundariesFromPath(
                                        dashPath, source, width / 2.0, out _));
                            }

                            station += dashLength + gapLength;
                        }
                    }

                    if (boundaries.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "No valid marking boundaries were generated.");
                    }

                    ObjectId preparationLayerId =
                        GetOrCreateLayer(transaction, database, PreparationLayerName);
                    var owner = (BlockTableRecord)transaction.GetObject(
                        source.OwnerId, OpenMode.ForWrite);
                    foreach (Polyline boundary in boundaries)
                    {
                        boundary.LayerId = preparationLayerId;
                        owner.AppendEntity(boundary);
                        transaction.AddNewlyCreatedDBObject(boundary, true);
                    }

                    source.UpgradeOpen();
                    if (needsReview)
                    {
                        source.Color =
                            Color.FromColorIndex(ColorMethod.ByAci, 6);
                    }
                    else
                    {
                        source.Erase();
                    }

                    transaction.Commit();
                    return boundaries.Count;
                }
                catch
                {
                    foreach (Polyline boundary in boundaries)
                    {
                        if (boundary.ObjectId.IsNull)
                        {
                            boundary.Dispose();
                        }
                    }

                    throw;
                }
            }
        }

        private static bool TryGetConvertedCircleGeometry(
            Polyline source,
            out Point2d center,
            out double radius,
            out double startAngle,
            out double direction)
        {
            center = Point2d.Origin;
            radius = 0;
            startAngle = 0;
            direction = 0;
            if (!source.Closed || source.NumberOfVertices != 3)
            {
                return false;
            }

            double expectedBulge = Math.Tan(Math.PI / 6.0);
            double firstBulge = source.GetBulgeAt(0);
            if (Math.Abs(Math.Abs(firstBulge) - expectedBulge) > 1e-8)
            {
                return false;
            }

            direction = Math.Sign(firstBulge);
            for (int index = 1; index < source.NumberOfVertices; index++)
            {
                double bulge = source.GetBulgeAt(index);
                if (Math.Sign(bulge) != direction ||
                    Math.Abs(Math.Abs(bulge) - expectedBulge) > 1e-8)
                {
                    return false;
                }
            }

            Point2d first = source.GetPoint2dAt(0);
            Point2d second = source.GetPoint2dAt(1);
            Point2d third = source.GetPoint2dAt(2);
            center = new Point2d(
                (first.X + second.X + third.X) / 3.0,
                (first.Y + second.Y + third.Y) / 3.0);
            radius = center.GetDistanceTo(first);
            double tolerance = Math.Max(GeometryTolerance, radius * 1e-8);
            if (!IsFinite(radius) || radius <= GeometryTolerance ||
                Math.Abs(center.GetDistanceTo(second) - radius) > tolerance ||
                Math.Abs(center.GetDistanceTo(third) - radius) > tolerance)
            {
                return false;
            }

            double firstChord = first.GetDistanceTo(second);
            double secondChord = second.GetDistanceTo(third);
            double thirdChord = third.GetDistanceTo(first);
            if (Math.Abs(firstChord - secondChord) > tolerance ||
                Math.Abs(secondChord - thirdChord) > tolerance)
            {
                return false;
            }

            startAngle = Math.Atan2(first.Y - center.Y, first.X - center.X);
            return true;
        }

        private static List<Polyline> CreateCircularRingBoundaries(
            Polyline source,
            Point2d center,
            double radius,
            double startAngle,
            double direction,
            double width)
        {
            double halfWidth = width / 2.0;
            double innerRadius = radius - halfWidth;
            if (innerRadius <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "The circular marking width consumes the circle's interior.");
            }

            var boundaries = new List<Polyline>();
            try
            {
                boundaries.Add(CreateCircularLoop(
                    source, center, radius + halfWidth, startAngle, direction));
                boundaries.Add(CreateCircularLoop(
                    source, center, innerRadius, startAngle, -direction));
                return boundaries;
            }
            catch
            {
                DisposeAllPolylines(boundaries);
                throw;
            }
        }

        private static List<Polyline> CreateCircularDashBoundaries(
            Polyline source,
            Point2d center,
            double radius,
            double startAngle,
            double direction,
            double width,
            double dashLength,
            double gapLength)
        {
            if (!IsFinite(dashLength) || dashLength <= GeometryTolerance ||
                !IsFinite(gapLength) || gapLength <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "Dash and gap lengths must be positive finite numbers.");
            }

            double circumference = 2.0 * Math.PI * radius;
            var boundaries = new List<Polyline>();
            try
            {
                double station = 0;
                while (station < circumference - GeometryTolerance)
                {
                    double finish = Math.Min(circumference, station + dashLength);
                    double currentDashLength = finish - station;
                    if (!IsFinite(currentDashLength) ||
                        currentDashLength <= GeometryTolerance)
                    {
                        throw new InvalidOperationException(
                            "A circular dash is too short to create valid geometry.");
                    }

                    if (currentDashLength >= circumference - GeometryTolerance)
                    {
                        boundaries.AddRange(CreateCircularRingBoundaries(
                            source, center, radius, startAngle, direction, width));
                    }
                    else
                    {
                        boundaries.Add(CreateCircularDashBoundary(
                            source, center, radius, startAngle, direction,
                            width, station, currentDashLength));
                    }

                    double nextStation = station + dashLength + gapLength;
                    if (nextStation <= station)
                    {
                        throw new InvalidOperationException(
                            "The dash and gap are too small to advance along the circle.");
                    }

                    station = nextStation;
                }

                return boundaries;
            }
            catch
            {
                DisposeAllPolylines(boundaries);
                throw;
            }
        }

        private static Polyline CreateCircularDashBoundary(
            Polyline source,
            Point2d center,
            double radius,
            double startAngle,
            double direction,
            double width,
            double station,
            double dashLength)
        {
            double halfWidth = width / 2.0;
            double innerRadius = radius - halfWidth;
            if (innerRadius <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "The circular marking width consumes the circle's interior.");
            }

            double sweep = direction * dashLength / radius;
            if (!IsFinite(sweep) ||
                Math.Abs(sweep) >= (2.0 * Math.PI) - GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "The circular dash sweep is not a valid open arc.");
            }

            double dashStartAngle = startAngle + direction * station / radius;
            double dashEndAngle = dashStartAngle + sweep;
            var points = new[]
            {
                PointOnCircle(center, radius + halfWidth, dashStartAngle),
                PointOnCircle(center, radius + halfWidth, dashEndAngle),
                PointOnCircle(center, innerRadius, dashEndAngle),
                PointOnCircle(center, innerRadius, dashStartAngle)
            };

            var boundary = new Polyline();
            try
            {
                boundary.SetPropertiesFrom(source);
                boundary.ConstantWidth = 0;
                boundary.Elevation = source.Elevation;
                boundary.Normal = Vector3d.ZAxis;
                boundary.AddVertexAt(
                    0, points[0], Math.Tan(sweep / 4.0), 0, 0);
                boundary.AddVertexAt(1, points[1], 0, 0, 0);
                boundary.AddVertexAt(
                    2, points[2], -Math.Tan(sweep / 4.0), 0, 0);
                boundary.AddVertexAt(3, points[3], 0, 0, 0);
                boundary.Closed = true;
                return boundary;
            }
            catch
            {
                boundary.Dispose();
                throw;
            }
        }

        private static Polyline CreateCircularLoop(
            Polyline source,
            Point2d center,
            double radius,
            double startAngle,
            double direction)
        {
            var boundary = new Polyline();
            try
            {
                boundary.SetPropertiesFrom(source);
                boundary.ConstantWidth = 0;
                boundary.Elevation = source.Elevation;
                boundary.Normal = Vector3d.ZAxis;
                double sweep = direction * (2.0 * Math.PI / 3.0);
                double bulge = Math.Tan(sweep / 4.0);
                for (int index = 0; index < 3; index++)
                {
                    boundary.AddVertexAt(
                        index,
                        PointOnCircle(
                            center, radius,
                            startAngle + direction * (2.0 * Math.PI * index / 3.0)),
                        bulge,
                        0,
                        0);
                }

                boundary.Closed = true;
                return boundary;
            }
            catch
            {
                boundary.Dispose();
                throw;
            }
        }

        private static Point2d PointOnCircle(
            Point2d center,
            double radius,
            double angle)
        {
            return new Point2d(
                center.X + radius * Math.Cos(angle),
                center.Y + radius * Math.Sin(angle));
        }

        private static ObjectId GetOrCreateLayer(
            Transaction transaction,
            Database database,
            string layerName)
        {
            var layerTable = (LayerTable)transaction.GetObject(
                database.LayerTableId, OpenMode.ForRead);
            if (layerTable.Has(layerName))
            {
                return layerTable[layerName];
            }

            layerTable.UpgradeOpen();
            var layer = new LayerTableRecord { Name = layerName };
            ObjectId layerId = layerTable.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return layerId;
        }

        private static List<Polyline> CreateBoundariesFromPath(
            Polyline path,
            Polyline source,
            double halfWidth,
            out bool needsReview)
        {
            needsReview = false;
            List<Polyline> boundaries = null;
            try
            {
                boundaries = CreateBoundariesFromOffsets(
                    path, source, halfWidth);
                ValidateOffsetBoundaries(boundaries, path.Closed);
                return boundaries;
            }
            catch (System.Exception offsetException)
            {
                if (boundaries != null)
                {
                    DisposeAllPolylines(boundaries);
                }

                System.Exception overlapRepairException = null;
                if (path.Closed)
                {
                    try
                    {
                        boundaries = TryCreateClosedOffsetOverlapRepair(
                            path, source, halfWidth);
                        if (boundaries != null)
                        {
                            needsReview = true;
                            return boundaries;
                        }
                    }
                    catch (System.Exception exception)
                    {
                        overlapRepairException = exception;
                    }
                }

                try
                {
                    return CreateBeveledBufferBoundaries(path, source, halfWidth);
                }
                catch (System.Exception repairException)
                {
                    string overlapRepairMessage = overlapRepairException == null
                        ? string.Empty
                        : $" Closed-offset overlap repair failed: " +
                          $"{overlapRepairException.Message}";
                    throw new InvalidOperationException(
                        $"Offset preparation failed: {offsetException.Message} " +
                        $"{overlapRepairMessage} " +
                        $"Beveled-join repair failed: {repairException.Message}",
                        overlapRepairException == null
                            ? new AggregateException(offsetException, repairException)
                            : new AggregateException(
                                offsetException,
                                overlapRepairException,
                                repairException));
                }
            }
        }

        private static List<Polyline> TryCreateClosedOffsetOverlapRepair(
            Polyline path,
            Polyline source,
            double halfWidth)
        {
            DBObjectCollection leftCurves = null;
            DBObjectCollection rightCurves = null;
            Region leftRegion = null;
            Region rightRegion = null;
            Region difference = null;
            Vector3d coordinateOffset =
                GetBoundaryCoordinateOffset(new List<Polyline> { path });
            try
            {
                leftCurves = path.GetOffsetCurves(halfWidth);
                rightCurves = path.GetOffsetCurves(-halfWidth);
                leftRegion = CreateUnionOfClosedOffsetLoops(
                    leftCurves, coordinateOffset);
                rightRegion = CreateUnionOfClosedOffsetLoops(
                    rightCurves, coordinateOffset);

                double leftArea = GetRegionArea(leftRegion, 0);
                double rightArea = GetRegionArea(rightRegion, 0);
                Region larger = leftArea >= rightArea ? leftRegion : rightRegion;
                Region smaller = leftArea >= rightArea ? rightRegion : leftRegion;
                double smallerArea = Math.Min(leftArea, rightArea);
                double tolerance = Math.Max(
                    GeometryTolerance, smallerArea * 1e-8);

                using (var intersection = (Region)larger.Clone())
                using (var smallerCopy = (Region)smaller.Clone())
                {
                    intersection.BooleanOperation(
                        BooleanOperationType.BoolIntersect, smallerCopy);
                    double overlapArea = intersection.IsNull
                        ? 0
                        : GetRegionArea(intersection, 0);
                    if (overlapArea <= tolerance ||
                        smallerArea - overlapArea <= tolerance)
                    {
                        return null;
                    }
                }

                difference = (Region)larger.Clone();
                using (var smallerCopy = (Region)smaller.Clone())
                {
                    difference.BooleanOperation(
                        BooleanOperationType.BoolSubtract, smallerCopy);
                }

                if (difference.IsNull ||
                    GetRegionArea(difference, 0) <= tolerance)
                {
                    throw new InvalidOperationException(
                        "The intersecting offset loops did not leave a usable outline.");
                }

                List<Polyline> repaired = CreatePolylinesFromRegion(
                    difference, source, coordinateOffset);
                try
                {
                    if (repaired.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "The overlap repair did not produce closed boundaries.");
                    }

                    ValidateOffsetBoundaries(repaired, closedPath: false);
                    return repaired;
                }
                catch
                {
                    DisposeAllPolylines(repaired);
                    throw;
                }
            }
            finally
            {
                leftRegion?.Dispose();
                rightRegion?.Dispose();
                difference?.Dispose();
                if (leftCurves != null)
                {
                    DisposeAll(leftCurves);
                }

                if (rightCurves != null)
                {
                    DisposeAll(rightCurves);
                }
            }
        }

        private static Region CreateUnionOfClosedOffsetLoops(
            DBObjectCollection offsetCurves,
            Vector3d coordinateOffset)
        {
            if (offsetCurves == null || offsetCurves.Count == 0)
            {
                throw new InvalidOperationException(
                    "An offset side did not produce any curves.");
            }

            Region combined = null;
            try
            {
                foreach (DBObject curveObject in offsetCurves)
                {
                    if (curveObject is not Polyline offset || !offset.Closed)
                    {
                        throw new InvalidOperationException(
                            "Overlap repair requires closed lightweight offset loops.");
                    }

                    using (var localOffset = (Polyline)offset.Clone())
                    {
                        localOffset.TransformBy(
                            Matrix3d.Displacement(-coordinateOffset));
                        DBObjectCollection curves =
                            new DBObjectCollection { localOffset };
                        DBObjectCollection createdRegions =
                            Region.CreateFromCurves(curves);
                        try
                        {
                            if (createdRegions.Count != 1 ||
                                createdRegions[0] is not Region candidate)
                            {
                                throw new InvalidOperationException(
                                    "An offset loop is self-intersecting or invalid.");
                            }

                            if (combined == null)
                            {
                                combined = (Region)candidate.Clone();
                            }
                            else
                            {
                                combined.BooleanOperation(
                                    BooleanOperationType.BoolUnite, candidate);
                                if (combined.IsNull)
                                {
                                    throw new InvalidOperationException(
                                        "Offset loops could not be combined.");
                                }
                            }
                        }
                        finally
                        {
                            DisposeAll(createdRegions);
                        }
                    }
                }

                if (combined == null)
                {
                    throw new InvalidOperationException(
                        "An offset side did not produce a valid region.");
                }

                return combined;
            }
            catch
            {
                combined?.Dispose();
                throw;
            }
        }

        private static List<Polyline> CreatePolylinesFromRegion(
            Region region,
            Polyline source,
            Vector3d coordinateOffset)
        {
            var exploded = new DBObjectCollection();
            var curves = new List<Curve>();
            var polylines = new List<Polyline>();
            try
            {
                region.Explode(exploded);
                foreach (DBObject item in exploded)
                {
                    if (item is not Curve curve ||
                        (curve is not Line && curve is not Arc))
                    {
                        throw new InvalidOperationException(
                            "The overlap repair produced an unsupported boundary curve.");
                    }

                    curves.Add(curve);
                }

                bool[] used = new bool[curves.Count];
                for (int firstIndex = 0; firstIndex < curves.Count; firstIndex++)
                {
                    if (used[firstIndex])
                    {
                        continue;
                    }

                    Curve first = curves[firstIndex];
                    Point3d loopStart = first.StartPoint;
                    Point3d currentEnd = first.EndPoint;
                    var edges = new List<(Curve Curve, bool Reversed)>
                    {
                        (first, false)
                    };
                    used[firstIndex] = true;

                    while (currentEnd.DistanceTo(loopStart) > 1e-7)
                    {
                        int nextIndex = -1;
                        bool reverseNext = false;
                        for (int candidateIndex = 0;
                             candidateIndex < curves.Count;
                             candidateIndex++)
                        {
                            if (used[candidateIndex])
                            {
                                continue;
                            }

                            Curve candidate = curves[candidateIndex];
                            bool startsAtEnd =
                                candidate.StartPoint.DistanceTo(currentEnd) <= 1e-7;
                            bool endsAtEnd =
                                candidate.EndPoint.DistanceTo(currentEnd) <= 1e-7;
                            if (startsAtEnd == endsAtEnd)
                            {
                                continue;
                            }

                            if (nextIndex >= 0)
                            {
                                throw new InvalidOperationException(
                                    "The overlap repair has an ambiguous junction.");
                            }

                            nextIndex = candidateIndex;
                            reverseNext = endsAtEnd;
                        }

                        if (nextIndex < 0)
                        {
                            throw new InvalidOperationException(
                                "The overlap repair produced an open boundary.");
                        }

                        Curve next = curves[nextIndex];
                        edges.Add((next, reverseNext));
                        used[nextIndex] = true;
                        currentEnd = reverseNext
                            ? next.StartPoint
                            : next.EndPoint;
                    }

                    var boundary = new Polyline();
                    try
                    {
                        foreach ((Curve curve, bool reversed) in edges)
                        {
                            Point3d start = reversed
                                ? curve.EndPoint
                                : curve.StartPoint;
                            boundary.AddVertexAt(
                                boundary.NumberOfVertices,
                                new Point2d(start.X, start.Y),
                                GetBoundaryBulge(curve, reversed),
                                0,
                                0);
                        }

                        boundary.Closed = true;
                        boundary.TransformBy(
                            Matrix3d.Displacement(coordinateOffset));
                        boundary.SetPropertiesFrom(source);
                        boundary.ConstantWidth = 0;
                        boundary.Elevation = source.Elevation;
                        boundary.Normal = source.Normal;
                        polylines.Add(boundary);
                    }
                    catch
                    {
                        boundary.Dispose();
                        throw;
                    }
                }

                return polylines;
            }
            catch
            {
                DisposeAllPolylines(polylines);
                throw;
            }
            finally
            {
                DisposeAll(exploded);
            }
        }

        private static double GetBoundaryBulge(Curve curve, bool reversed)
        {
            if (curve is Line)
            {
                return 0;
            }

            if (curve is not Arc arc)
            {
                throw new InvalidOperationException(
                    "The overlap repair produced an unsupported boundary curve.");
            }

            double sweep = arc.EndAngle - arc.StartAngle;
            while (sweep <= 0)
            {
                sweep += Math.PI * 2.0;
            }

            while (sweep > Math.PI * 2.0)
            {
                sweep -= Math.PI * 2.0;
            }

            if (Math.Abs(sweep - Math.PI * 2.0) <= 1e-8)
            {
                throw new InvalidOperationException(
                    "The overlap repair produced a full-circle arc segment.");
            }

            if (arc.Normal.Z < 0)
            {
                sweep = -sweep;
            }

            if (reversed)
            {
                sweep = -sweep;
            }

            return Math.Tan(sweep / 4.0);
        }

        private static List<Polyline> CreateBoundariesFromOffsets(
            Polyline path,
            Polyline source,
            double halfWidth)
        {
            DBObjectCollection leftCurves = null;
            DBObjectCollection rightCurves = null;
            try
            {
                leftCurves = path.GetOffsetCurves(halfWidth);
                rightCurves = path.GetOffsetCurves(-halfWidth);
                Polyline left = RequireSingleOffset(leftCurves);
                Polyline right = RequireSingleOffset(rightCurves);

                if (path.Closed)
                {
                    if (!left.Closed || !right.Closed ||
                        Math.Abs(left.Area - right.Area) <= GeometryTolerance)
                    {
                        throw new InvalidOperationException(
                            "The closed offset did not produce two distinct loops.");
                    }

                    var boundaries = new List<Polyline>();
                    try
                    {
                        boundaries.Add(CopyClosedOffset(left, source));
                        boundaries.Add(CopyClosedOffset(right, source));
                        return boundaries;
                    }
                    catch
                    {
                        DisposeAllPolylines(boundaries);
                        throw;
                    }
                }

                if (left.Closed || right.Closed)
                {
                    throw new InvalidOperationException(
                        "An open marking path produced a closed offset.");
                }

                return new List<Polyline>
                {
                    JoinOpenOffsets(left, right, source)
                };
            }
            finally
            {
                if (leftCurves != null)
                {
                    DisposeAll(leftCurves);
                }

                if (rightCurves != null)
                {
                    DisposeAll(rightCurves);
                }
            }
        }

        private static void ValidateOffsetBoundaries(
            List<Polyline> boundaries,
            bool closedPath)
        {
            if (boundaries.Count == 0)
            {
                throw new InvalidOperationException(
                    "The offsets did not produce any boundaries.");
            }

            var regions = new List<Region>();
            Vector3d coordinateOffset = GetBoundaryCoordinateOffset(boundaries);
            try
            {
                foreach (Polyline boundary in boundaries)
                {
                    using (var localBoundary = (Polyline)boundary.Clone())
                    {
                        localBoundary.TransformBy(
                            Matrix3d.Displacement(-coordinateOffset));
                        DBObjectCollection curves =
                            new DBObjectCollection { localBoundary };
                        DBObjectCollection createdRegions =
                            Region.CreateFromCurves(curves);
                        if (createdRegions.Count != 1 ||
                            createdRegions[0] is not Region region)
                        {
                            DisposeAll(createdRegions);
                            throw new InvalidOperationException(
                                "An offset boundary is self-intersecting or invalid.");
                        }

                        regions.Add(region);
                    }
                }

                if (closedPath && regions.Count == 2)
                {
                    using (var intersection = (Region)regions[0].Clone())
                    using (var other = (Region)regions[1].Clone())
                    {
                        intersection.BooleanOperation(
                            BooleanOperationType.BoolIntersect, other);
                        double firstArea = GetRegionArea(
                            regions[0], boundaries[0].Elevation - coordinateOffset.Z);
                        double secondArea = GetRegionArea(
                            regions[1], boundaries[1].Elevation - coordinateOffset.Z);
                        double smallerArea = Math.Min(firstArea, secondArea);
                        double overlapArea = intersection.IsNull
                            ? 0
                            : GetRegionArea(
                                intersection,
                                boundaries[0].Elevation - coordinateOffset.Z);
                        double tolerance = Math.Max(
                            GeometryTolerance, smallerArea * 1e-8);
                        if (Math.Abs(overlapArea - smallerArea) > tolerance)
                        {
                            throw new InvalidOperationException(
                                "The closed offsets cross instead of forming nested loops.");
                        }
                    }
                }
            }
            finally
            {
                foreach (Region region in regions)
                {
                    region.Dispose();
                }
            }
        }

        private static List<Polyline> CreateBeveledBufferBoundaries(
            Polyline path,
            Polyline source,
            double halfWidth)
        {
            List<Point3d> centerlinePoints =
                SamplePolylineForBuffer(path, halfWidth);
            if (centerlinePoints.Count < 2)
            {
                throw new InvalidOperationException(
                    "The path does not contain enough points for a bevel repair.");
            }

            bool closed = path.Closed;
            List<Point2d> left = CreateBeveledOffsetSide(
                centerlinePoints, closed, halfWidth, halfWidth * 4.0);
            List<Point2d> right = CreateBeveledOffsetSide(
                centerlinePoints, closed, -halfWidth, halfWidth * 4.0);
            var boundaries = new List<Polyline>();
            try
            {
                if (closed)
                {
                    boundaries.Add(CreateBeveledBoundary(
                        source, left, closed: true));
                    boundaries.Add(CreateBeveledBoundary(
                        source, right, closed: true));
                }
                else
                {
                    var outline = new List<Point2d>(
                        left.Count + right.Count);
                    outline.AddRange(left);
                    for (int index = right.Count - 1; index >= 0; index--)
                    {
                        outline.Add(right[index]);
                    }

                    boundaries.Add(CreateBeveledBoundary(
                        source, outline, closed: true));
                }

                if (closed &&
                    (boundaries.Count != 2 ||
                     Math.Abs(boundaries[0].Area) <= GeometryTolerance ||
                     Math.Abs(boundaries[1].Area) <= GeometryTolerance ||
                     Math.Abs(Math.Abs(boundaries[0].Area) -
                              Math.Abs(boundaries[1].Area)) <= GeometryTolerance))
                {
                    throw new InvalidOperationException(
                        "The closed path's inner offset collapsed at the requested " +
                        "marking width; the source was retained.");
                }

                ValidateOffsetBoundaries(boundaries, closed);
                return boundaries;
            }
            catch
            {
                DisposeAllPolylines(boundaries);
                throw;
            }
        }

        private static List<Point2d> CreateBeveledOffsetSide(
            List<Point3d> centerline,
            bool closed,
            double offset,
            double maximumMiterLength)
        {
            int pointCount = centerline.Count;
            int firstJoint = closed ? 0 : 1;
            int lastJoint = closed ? pointCount - 1 : pointCount - 2;
            var result = new List<Point2d>(pointCount * 2);

            if (!closed)
            {
                AppendOffsetPoint(
                    result,
                    centerline[0],
                    GetUnitDirection(centerline[0], centerline[1]),
                    offset);
            }

            for (int index = firstJoint; index <= lastJoint; index++)
            {
                Point3d vertex = centerline[index];
                Point3d previous = centerline[
                    (index - 1 + pointCount) % pointCount];
                Point3d next = centerline[(index + 1) % pointCount];
                Vector2d incoming = GetUnitDirection(previous, vertex);
                Vector2d outgoing = GetUnitDirection(vertex, next);
                Vector2d incomingNormal = GetLeftNormal(incoming);
                Vector2d outgoingNormal = GetLeftNormal(outgoing);
                Point2d firstOffset = OffsetPoint(vertex, incomingNormal, offset);
                Point2d secondOffset = OffsetPoint(vertex, outgoingNormal, offset);
                double turn = Cross(incoming, outgoing);

                if (Math.Abs(turn) <= GeometryTolerance)
                {
                    if (incoming.DotProduct(outgoing) > 0)
                    {
                        result.Add(new Point2d(
                            (firstOffset.X + secondOffset.X) / 2.0,
                            (firstOffset.Y + secondOffset.Y) / 2.0));
                    }
                    else
                    {
                        result.Add(firstOffset);
                        result.Add(secondOffset);
                    }

                    continue;
                }

                bool outerSide = turn * offset < 0;
                if (outerSide ||
                    !TryGetOffsetLineIntersection(
                        firstOffset, incoming, secondOffset, outgoing,
                        out Point2d intersection) ||
                    intersection.GetDistanceTo(new Point2d(vertex.X, vertex.Y)) >
                        maximumMiterLength)
                {
                    result.Add(firstOffset);
                    result.Add(secondOffset);
                }
                else
                {
                    result.Add(intersection);
                }
            }

            if (!closed)
            {
                AppendOffsetPoint(
                    result,
                    centerline[pointCount - 1],
                    GetUnitDirection(
                        centerline[pointCount - 2],
                        centerline[pointCount - 1]),
                    offset);
            }

            return result;
        }

        private static Polyline CreateBeveledBoundary(
            Polyline source,
            List<Point2d> points,
            bool closed)
        {
            if (points.Count < (closed ? 3 : 2))
            {
                throw new InvalidOperationException(
                    "The bevel boundary has too few vertices.");
            }

            var boundary = new Polyline();
            try
            {
                boundary.SetPropertiesFrom(source);
                boundary.ConstantWidth = 0;
                boundary.Elevation = source.Elevation;
                boundary.Normal = Vector3d.ZAxis;
                for (int index = 0; index < points.Count; index++)
                {
                    boundary.AddVertexAt(index, points[index], 0, 0, 0);
                }

                boundary.Closed = closed;
                return boundary;
            }
            catch
            {
                boundary.Dispose();
                throw;
            }
        }

        private static void AppendOffsetPoint(
            List<Point2d> points,
            Point3d vertex,
            Vector2d direction,
            double offset)
        {
            points.Add(OffsetPoint(vertex, GetLeftNormal(direction), offset));
        }

        private static Point2d OffsetPoint(
            Point3d point,
            Vector2d normal,
            double offset)
        {
            return new Point2d(
                point.X + normal.X * offset,
                point.Y + normal.Y * offset);
        }

        private static Vector2d GetUnitDirection(Point3d start, Point3d end)
        {
            var direction = new Vector2d(end.X - start.X, end.Y - start.Y);
            if (direction.Length <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "The path contains a zero-length segment.");
            }

            return direction.GetNormal();
        }

        private static Vector2d GetLeftNormal(Vector2d direction)
        {
            return new Vector2d(-direction.Y, direction.X);
        }

        private static double Cross(Vector2d first, Vector2d second)
        {
            return first.X * second.Y - first.Y * second.X;
        }

        private static bool TryGetOffsetLineIntersection(
            Point2d firstPoint,
            Vector2d firstDirection,
            Point2d secondPoint,
            Vector2d secondDirection,
            out Point2d intersection)
        {
            double denominator = Cross(firstDirection, secondDirection);
            if (Math.Abs(denominator) <= GeometryTolerance)
            {
                intersection = Point2d.Origin;
                return false;
            }

            var between = new Vector2d(
                secondPoint.X - firstPoint.X,
                secondPoint.Y - firstPoint.Y);
            double parameter = Cross(between, secondDirection) / denominator;
            intersection = new Point2d(
                firstPoint.X + parameter * firstDirection.X,
                firstPoint.Y + parameter * firstDirection.Y);
            return IsFinite(intersection.X) && IsFinite(intersection.Y);
        }

        private static List<Point3d> SamplePolylineForBuffer(
            Polyline path,
            double halfWidth)
        {
            double maximumDeviation = Math.Max(
                GeometryTolerance,
                Math.Min(halfWidth * 0.02, 0.01));
            const int maximumSamplesPerArc = 4096;
            int segmentCount = path.Closed
                ? path.NumberOfVertices
                : path.NumberOfVertices - 1;
            var points = new List<Point3d>();
            for (int segmentIndex = 0;
                 segmentIndex < segmentCount;
                 segmentIndex++)
            {
                double startDistance =
                    path.GetDistanceAtParameter(segmentIndex);
                double finishDistance =
                    path.GetDistanceAtParameter(segmentIndex + 1);
                double bulge = path.GetBulgeAt(segmentIndex);
                int subdivisions = 1;
                if (Math.Abs(bulge) > GeometryTolerance)
                {
                    Point3d start = path.GetPoint3dAt(segmentIndex);
                    Point3d finish = path.GetPoint3dAt(
                        (segmentIndex + 1) % path.NumberOfVertices);
                    double chordLength = start.DistanceTo(finish);
                    double arcRadius = chordLength * (1 + bulge * bulge) /
                                       (4 * Math.Abs(bulge));
                    double maximumAngle = Math.PI / 12.0;
                    if (arcRadius > maximumDeviation)
                    {
                        maximumAngle = Math.Min(
                            maximumAngle,
                            2.0 * Math.Acos(Math.Max(
                                -1.0, 1.0 - maximumDeviation / arcRadius)));
                    }

                    if (!IsFinite(maximumAngle) || maximumAngle <= 0)
                    {
                        throw new InvalidOperationException(
                            "An arc could not be sampled at the requested precision.");
                    }

                    double sweep = 4.0 * Math.Atan(Math.Abs(bulge));
                    double required = Math.Ceiling(sweep / maximumAngle);
                    if (!IsFinite(required) || required > maximumSamplesPerArc)
                    {
                        throw new InvalidOperationException(
                            "An arc requires too many samples for a reliable fallback.");
                    }

                    subdivisions = Math.Max(1, (int)required);
                }

                if (points.Count == 0)
                {
                    points.Add(path.GetPointAtDist(startDistance));
                }

                for (int subdivision = 1;
                     subdivision <= subdivisions;
                     subdivision++)
                {
                    double distance = startDistance +
                                      (finishDistance - startDistance) *
                                      subdivision / subdivisions;
                    Point3d point = path.GetPointAtDist(distance);
                    if (points[points.Count - 1].DistanceTo(point) >
                        GeometryTolerance)
                    {
                        points.Add(point);
                    }
                }
            }

            if (path.Closed && points.Count > 1 &&
                points[0].DistanceTo(points[points.Count - 1]) <=
                GeometryTolerance)
            {
                points.RemoveAt(points.Count - 1);
            }

            return points;
        }

        private static void DisposeAllPolylines(List<Polyline> polylines)
        {
            foreach (Polyline polyline in polylines)
            {
                polyline.Dispose();
            }
        }

        private static Polyline RequireSingleOffset(DBObjectCollection curves)
        {
            if (curves.Count != 1 || !(curves[0] is Polyline polyline))
            {
                throw new InvalidOperationException(
                    "The offset did not produce one lightweight polyline. " +
                    "Check for tight curves, self-intersections, or invalid geometry.");
            }

            return polyline;
        }

        private static Polyline CopyClosedOffset(
            Polyline offset,
            Polyline source)
        {
            var result = (Polyline)offset.Clone();
            result.SetPropertiesFrom(source);
            result.ConstantWidth = 0;
            result.Elevation = source.Elevation;
            result.Normal = source.Normal;
            result.Closed = true;
            return result;
        }

        private static Polyline JoinOpenOffsets(
            Polyline left,
            Polyline right,
            Polyline source)
        {
            if (left.NumberOfVertices < 2 || right.NumberOfVertices < 2)
            {
                throw new InvalidOperationException(
                    "The offset paths do not contain enough vertices.");
            }

            bool reverseRight =
                PairingCost(left, right, true) < PairingCost(left, right, false);

            var result = new Polyline();
            result.SetPropertiesFrom(source);
            result.ConstantWidth = 0;
            result.Elevation = source.Elevation;
            result.Normal = source.Normal;

            for (int i = 0; i < left.NumberOfVertices; i++)
            {
                result.AddVertexAt(
                    result.NumberOfVertices,
                    left.GetPoint2dAt(i),
                    i == left.NumberOfVertices - 1 ? 0 : left.GetBulgeAt(i),
                    0,
                    0);
            }

            if (reverseRight)
            {
                AppendReversedOpenPath(result, right);
            }
            else
            {
                AppendOpenPath(result, right);
            }

            result.Closed = true;
            if (Math.Abs(result.Area) <= GeometryTolerance)
            {
                result.Dispose();
                throw new InvalidOperationException(
                    "The offset boundary has zero or near-zero area.");
            }

            return result;
        }

        private static void AppendReversedOpenPath(
            Polyline destination,
            Polyline path)
        {
            for (int i = path.NumberOfVertices - 1; i >= 0; i--)
            {
                double bulge = i > 0 ? -path.GetBulgeAt(i - 1) : 0;
                destination.AddVertexAt(
                    destination.NumberOfVertices,
                    path.GetPoint2dAt(i),
                    bulge,
                    0,
                    0);
            }
        }

        private static void AppendOpenPath(
            Polyline destination,
            Polyline path)
        {
            for (int i = 0; i < path.NumberOfVertices; i++)
            {
                destination.AddVertexAt(
                    destination.NumberOfVertices,
                    path.GetPoint2dAt(i),
                    i == path.NumberOfVertices - 1 ? 0 : path.GetBulgeAt(i),
                    0,
                    0);
            }
        }

        private static double PairingCost(
            Polyline first,
            Polyline second,
            bool pairSameEnds)
        {
            Point2d firstStart = first.GetPoint2dAt(0);
            Point2d firstEnd = first.GetPoint2dAt(first.NumberOfVertices - 1);
            Point2d secondStart = second.GetPoint2dAt(0);
            Point2d secondEnd = second.GetPoint2dAt(second.NumberOfVertices - 1);
            return pairSameEnds
                ? firstStart.GetDistanceTo(secondStart) +
                  firstEnd.GetDistanceTo(secondEnd)
                : firstStart.GetDistanceTo(secondEnd) +
                  firstEnd.GetDistanceTo(secondStart);
        }

        private static Polyline CreatePathSlice(
            Polyline source,
            double startDistance,
            double finishDistance)
        {
            var distances = new List<double> { startDistance };
            int segmentCount = source.Closed
                ? source.NumberOfVertices
                : source.NumberOfVertices - 1;
            for (int i = 1; i < segmentCount; i++)
            {
                double vertexDistance = source.GetDistanceAtParameter(i);
                if (vertexDistance > startDistance + GeometryTolerance &&
                    vertexDistance < finishDistance - GeometryTolerance)
                {
                    distances.Add(vertexDistance);
                }
            }

            distances.Add(finishDistance);
            var result = new Polyline();
            result.SetPropertiesFrom(source);
            result.ConstantWidth = 0;
            result.Elevation = source.Elevation;
            result.Normal = source.Normal;

            for (int i = 0; i < distances.Count; i++)
            {
                double distance = distances[i];
                Point3d point = source.GetPointAtDist(distance);
                double bulge = 0;
                if (i < distances.Count - 1)
                {
                    double midpoint = (distance + distances[i + 1]) / 2.0;
                    int segment = FindSegmentAtDistance(source, midpoint);
                    double segmentStart = source.GetDistanceAtParameter(segment);
                    double segmentEnd = source.GetDistanceAtParameter(segment + 1);
                    double segmentLength = segmentEnd - segmentStart;
                    if (segmentLength <= GeometryTolerance)
                    {
                        result.Dispose();
                        throw new InvalidOperationException(
                            "The source contains a zero-length segment.");
                    }

                    double fraction =
                        (distances[i + 1] - distance) / segmentLength;
                    bulge = Math.Tan(Math.Atan(source.GetBulgeAt(segment)) * fraction);
                }

                result.AddVertexAt(
                    result.NumberOfVertices,
                    new Point2d(point.X, point.Y),
                    bulge,
                    0,
                    0);
            }

            if (result.NumberOfVertices < 2)
            {
                result.Dispose();
                throw new InvalidOperationException(
                    "A dash contains too few points to form a boundary.");
            }

            return result;
        }

        private static int FindSegmentAtDistance(
            Polyline source,
            double distance)
        {
            int segmentCount = source.Closed
                ? source.NumberOfVertices
                : source.NumberOfVertices - 1;
            for (int i = 0; i < segmentCount; i++)
            {
                double start = source.GetDistanceAtParameter(i);
                double end = source.GetDistanceAtParameter(i + 1);
                if (distance >= start - GeometryTolerance &&
                    distance < end - GeometryTolerance)
                {
                    return i;
                }
            }

            return segmentCount - 1;
        }

        private static BoundarySelectionResult GetProcessableBoundarySelection(
            Database database,
            List<ObjectId> selectedIds)
        {
            using (Transaction transaction =
                   database.TransactionManager.StartTransaction())
            {
                var processableIds = new List<ObjectId>();
                var failures = new List<BoundaryFailure>();
                var closedCandidates = new List<BoundaryCandidate>();
                var openCandidates = new List<BoundaryCandidate>();

                foreach (ObjectId id in selectedIds)
                {
                    if (transaction.GetObject(id, OpenMode.ForRead) is not Polyline polyline)
                    {
                        failures.Add(new BoundaryFailure(
                            id, "The selected object is not a lightweight polyline."));
                        continue;
                    }

                    if (polyline.Closed &&
                        polyline.Normal.IsEqualTo(Vector3d.ZAxis))
                    {
                        processableIds.Add(id);
                        try
                        {
                            closedCandidates.Add(new BoundaryCandidate(
                                id, polyline, polyline.GeometricExtents));
                        }
                        catch
                        {
                            // The solid creation pass reports invalid closed geometry.
                        }

                        continue;
                    }

                    if (!polyline.Closed &&
                        polyline.Normal.IsEqualTo(Vector3d.ZAxis))
                    {
                        try
                        {
                            openCandidates.Add(new BoundaryCandidate(
                                id, polyline, polyline.GeometricExtents));
                        }
                        catch (System.Exception exception)
                        {
                            failures.Add(new BoundaryFailure(
                                id, $"The open polyline could not be checked: " +
                                    exception.Message));
                        }

                        continue;
                    }

                    failures.Add(new BoundaryFailure(
                        id, "The boundary must be horizontal and closed."));
                }

                var pairedOpenIds = new HashSet<ObjectId>();
                var pairedClosedIds = new HashSet<ObjectId>();
                foreach (BoundaryCandidate open in openCandidates)
                {
                    foreach (BoundaryCandidate closed in closedCandidates)
                    {
                        if (Math.Abs(open.Polyline.Elevation -
                                closed.Polyline.Elevation) > GeometryTolerance ||
                            (!ExtentsContain(open.Extents, closed.Extents) &&
                             !ExtentsContain(closed.Extents, open.Extents)))
                        {
                            continue;
                        }

                        pairedOpenIds.Add(open.ObjectId);
                        pairedClosedIds.Add(closed.ObjectId);
                        failures.Add(new BoundaryFailure(
                            open.ObjectId,
                            $"Its extents match closed boundary {closed.ObjectId.Handle}; " +
                            "both were skipped because the annular pair may be incomplete."));
                        failures.Add(new BoundaryFailure(
                            closed.ObjectId,
                            $"It may be paired with open boundary {open.ObjectId.Handle}; " +
                            "both were skipped because the annular pair may be incomplete."));
                    }
                }

                foreach (BoundaryCandidate open in openCandidates)
                {
                    if (!pairedOpenIds.Contains(open.ObjectId))
                    {
                        failures.Add(new BoundaryFailure(
                            open.ObjectId,
                            "An open polyline cannot bound a solid and was skipped."));
                    }
                }

                processableIds.RemoveAll(id => pairedClosedIds.Contains(id));
                return new BoundarySelectionResult(processableIds, failures);
            }
        }

        private static SolidCreationResult CreateSolidsForBoundaries(
            Database database,
            List<ObjectId> boundaryIds,
            ObjectId? surfaceId,
            double? customElevation,
            double height,
            string nestedMode,
            double innerHeight)
        {
            using (Transaction transaction =
                   database.TransactionManager.StartTransaction())
            {
                var boundaries = new List<Polyline>(boundaryIds.Count);
                foreach (ObjectId id in boundaryIds)
                {
                    var boundary = transaction.GetObject(
                        id, OpenMode.ForRead) as Polyline;
                    if (boundary == null)
                    {
                        throw new InvalidOperationException(
                            "The selection contains a non-polyline object.");
                    }

                    if (!boundary.Closed ||
                        !boundary.Normal.IsEqualTo(Vector3d.ZAxis))
                    {
                        throw new InvalidOperationException(
                            "Every selected boundary must be closed and horizontal.");
                    }

                    boundaries.Add(boundary);
                }

                CivilSurface surface = null;
                if (customElevation.HasValue)
                {
                    if (!IsFinite(customElevation.Value))
                    {
                        throw new InvalidOperationException(
                            "The custom elevation must be finite.");
                    }
                }
                else
                {
                    if (!surfaceId.HasValue)
                    {
                        throw new InvalidOperationException(
                            "A surface or custom elevation must be selected.");
                    }

                    surface = transaction.GetObject(
                        surfaceId.Value, OpenMode.ForRead) as CivilSurface;
                    if (surface == null)
                    {
                        throw new InvalidOperationException(
                            "The selected object is no longer a Civil 3D surface.");
                    }
                }

                Vector3d coordinateOffset =
                    GetBoundaryCoordinateOffset(boundaries);
                var boundaryRegions = new List<BoundaryRegion>();
                var failures = new List<BoundaryFailure>();
                try
                {
                    foreach (Polyline boundary in boundaries)
                    {
                        Region region = null;
                        try
                        {
                            using (var localBoundary = (Polyline)boundary.Clone())
                            {
                                localBoundary.TransformBy(
                                    Matrix3d.Displacement(-coordinateOffset));
                                var curves = new DBObjectCollection { localBoundary };
                                DBObjectCollection regions =
                                    Region.CreateFromCurves(curves);
                                if (regions.Count != 1 ||
                                    !(regions[0] is Region createdRegion))
                                {
                                    DisposeAll(regions);
                                    throw new InvalidOperationException(
                                        "A selected boundary did not produce exactly one planar region.");
                                }

                                region = createdRegion;
                            }

                            Vector3d localDisplacement = -coordinateOffset;
                            boundaryRegions.Add(new BoundaryRegion(
                                boundary,
                                region,
                                GetRegionArea(
                                    region, boundary.Elevation - coordinateOffset.Z),
                                TranslateExtents(
                                    boundary.GeometricExtents, localDisplacement),
                                boundary.Elevation - coordinateOffset.Z));
                            region = null;
                        }
                        catch (System.Exception exception)
                        {
                            region?.Dispose();
                            failures.Add(new BoundaryFailure(
                                boundary.ObjectId,
                                $"Could not create its planar region: {exception.Message}. " +
                                DescribeBoundary(boundary)));
                        }
                    }

                    AssignContainingRegions(boundaryRegions);
                    var owner = (BlockTableRecord)transaction.GetObject(
                        database.CurrentSpaceId, OpenMode.ForWrite);
                    int solidsCreated = 0;
                    var erasedBoundaryIds = new HashSet<ObjectId>();
                    var nestedGroupRoots = new HashSet<int>();
                    if (nestedMode == "Skip")
                    {
                        foreach (BoundaryRegion boundary in boundaryRegions)
                        {
                            if (boundary.ParentIndex >= 0)
                            {
                                int rootIndex = boundary.Index;
                                while (boundaryRegions[rootIndex].ParentIndex >= 0)
                                {
                                    rootIndex = boundaryRegions[rootIndex].ParentIndex;
                                }

                                nestedGroupRoots.Add(rootIndex);
                            }
                        }
                    }

                    foreach (BoundaryRegion boundaryRegion in boundaryRegions)
                    {
                        int rootIndex = boundaryRegion.Index;
                        while (boundaryRegions[rootIndex].ParentIndex >= 0)
                        {
                            rootIndex = boundaryRegions[rootIndex].ParentIndex;
                        }

                        if (nestedGroupRoots.Contains(rootIndex))
                        {
                            continue;
                        }

                        if (boundaryRegion.Depth % 2 != 0)
                        {
                            continue;
                        }

                        List<BoundaryRegion> holes = boundaryRegions.FindAll(
                            candidate => candidate.ParentIndex == boundaryRegion.Index);
                        Solid3d solid = null;
                        try
                        {
                            solid = CreateBoundaryExtrusion(
                                boundaryRegion,
                                holes,
                                surface,
                                customElevation,
                                height,
                                coordinateOffset);

                            using (solid)
                            {
                                solid.SetDatabaseDefaults(database);
                                solid.Layer = boundaryRegion.Boundary.Layer;
                                owner.AppendEntity(solid);
                                transaction.AddNewlyCreatedDBObject(solid, true);
                                solidsCreated++;
                                erasedBoundaryIds.Add(
                                    boundaryRegion.Boundary.ObjectId);
                                if (nestedMode != "AnnularInner")
                                {
                                    foreach (BoundaryRegion hole in holes)
                                    {
                                        erasedBoundaryIds.Add(hole.Boundary.ObjectId);
                                    }
                                }
                                else
                                {
                                    foreach (BoundaryRegion hole in holes)
                                    {
                                        Solid3d innerSolid = null;
                                        try
                                        {
                                            innerSolid = CreateBoundaryExtrusion(
                                                hole,
                                                new List<BoundaryRegion>(),
                                                surface,
                                                customElevation,
                                                innerHeight,
                                                coordinateOffset);
                                            using (innerSolid)
                                            {
                                                innerSolid.SetDatabaseDefaults(database);
                                                innerSolid.Layer = hole.Boundary.Layer;
                                                owner.AppendEntity(innerSolid);
                                                transaction.AddNewlyCreatedDBObject(
                                                    innerSolid, true);
                                                solidsCreated++;
                                                erasedBoundaryIds.Add(
                                                    hole.Boundary.ObjectId);
                                            }
                                        }
                                        catch (System.Exception innerException)
                                        {
                                            innerSolid?.Dispose();
                                            failures.Add(new BoundaryFailure(
                                                hole.Boundary.ObjectId,
                                                $"The annular solid was created, but " +
                                                $"the inner solid failed: {innerException.Message}"));
                                        }
                                    }
                                }
                            }
                        }
                        catch (System.Exception exception)
                        {
                            solid?.Dispose();
                            failures.Add(new BoundaryFailure(
                                boundaryRegion.Boundary.ObjectId,
                                exception.Message));
                            foreach (BoundaryRegion hole in holes)
                            {
                                failures.Add(new BoundaryFailure(
                                    hole.Boundary.ObjectId,
                                    $"Its boundary group failed at " +
                                    $"{boundaryRegion.Boundary.Handle}: {exception.Message}"));
                            }

                            continue;
                        }
                    }

                    foreach (Polyline boundary in boundaries)
                    {
                        if (!erasedBoundaryIds.Contains(boundary.ObjectId))
                        {
                            continue;
                        }

                        boundary.UpgradeOpen();
                        boundary.Erase();
                    }

                    transaction.Commit();
                    return new SolidCreationResult(solidsCreated, failures);
                }
                finally
                {
                    foreach (BoundaryRegion boundaryRegion in boundaryRegions)
                    {
                        boundaryRegion.Region.Dispose();
                    }
                }
            }
        }

        private static bool HasNestedBoundaryGroups(
            Database database,
            List<ObjectId> boundaryIds)
        {
            using (Transaction transaction =
                   database.TransactionManager.StartTransaction())
            {
                var boundaries = new List<Polyline>();
                foreach (ObjectId id in boundaryIds)
                {
                    if (transaction.GetObject(id, OpenMode.ForRead) is Polyline boundary &&
                        boundary.Closed &&
                        boundary.Normal.IsEqualTo(Vector3d.ZAxis))
                    {
                        boundaries.Add(boundary);
                    }
                }

                Vector3d coordinateOffset = GetBoundaryCoordinateOffset(boundaries);
                var regions = new List<BoundaryRegion>();
                try
                {
                    foreach (Polyline boundary in boundaries)
                    {
                        Region unownedRegion = null;
                        try
                        {
                            using (var localBoundary = (Polyline)boundary.Clone())
                            {
                                localBoundary.TransformBy(
                                    Matrix3d.Displacement(-coordinateOffset));
                                var curves = new DBObjectCollection { localBoundary };
                                DBObjectCollection created = Region.CreateFromCurves(curves);
                                if (created.Count != 1 || created[0] is not Region region)
                                {
                                    DisposeAll(created);
                                    continue;
                                }

                                unownedRegion = region;
                                regions.Add(new BoundaryRegion(
                                    boundary,
                                    region,
                                    GetRegionArea(region,
                                        boundary.Elevation - coordinateOffset.Z),
                                    TranslateExtents(boundary.GeometricExtents,
                                        -coordinateOffset),
                                    boundary.Elevation - coordinateOffset.Z));
                                unownedRegion = null;
                            }
                        }
                        catch
                        {
                            unownedRegion?.Dispose();
                            // Invalid boundaries are reported by the solid creation pass.
                        }
                    }

                    AssignContainingRegions(regions);
                    return regions.Exists(region => region.ParentIndex >= 0);
                }
                finally
                {
                    foreach (BoundaryRegion region in regions)
                    {
                        region.Region.Dispose();
                    }
                }
            }
        }

        private static Polyline CircleToPolyline(Circle circle, double width)
        {
            if (Math.Abs(Math.Abs(circle.Normal.DotProduct(Vector3d.ZAxis)) - 1.0) >
                GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "Only circles in a horizontal WCS plane can be converted.");
            }

            var polyline = new Polyline();
            polyline.SetPropertiesFrom(circle);
            polyline.Normal = Vector3d.ZAxis;
            polyline.Elevation = circle.Center.Z;
            double radius = circle.Radius;
            double arcBulge = Math.Tan(Math.PI / 6.0);
            for (int index = 0; index < 3; index++)
            {
                double angle = index * (2.0 * Math.PI / 3.0);
                polyline.AddVertexAt(
                    index,
                    new Point2d(
                        circle.Center.X + radius * Math.Cos(angle),
                        circle.Center.Y + radius * Math.Sin(angle)),
                    arcBulge,
                    0,
                    0);
            }

            polyline.Closed = true;
            polyline.ConstantWidth = width;
            return polyline;
        }

        private static Polyline EllipseToPolyline(
            Ellipse ellipse,
            double tolerance,
            double width)
        {
            double start = ellipse.StartParam;
            double end = ellipse.EndParam;
            double span = end - start;
            if (Math.Abs(Math.Abs(ellipse.Normal.DotProduct(Vector3d.ZAxis)) - 1.0) >
                    GeometryTolerance ||
                Math.Abs(span - (2.0 * Math.PI)) > 1e-6)
            {
                throw new InvalidOperationException(
                    "Only full ellipses in a horizontal WCS plane can be converted.");
            }

            var points = new List<Point3d>();
            Point3d first = ellipse.GetPointAtParameter(start);
            points.Add(first);
            const int initialSegments = 16;
            for (int index = 0; index < initialSegments; index++)
            {
                double t1 = start + span * index / initialSegments;
                double t2 = start + span * (index + 1) / initialSegments;
                Point3d p1 = ellipse.GetPointAtParameter(t1);
                Point3d p2 = ellipse.GetPointAtParameter(t2);
                AppendEllipseSegment(
                    ellipse, t1, t2, p1, p2, tolerance, 12, points);
            }

            // The last sampled endpoint duplicates the first vertex of a closed loop.
            if (points.Count > 1 && points[points.Count - 1].DistanceTo(first) <=
                GeometryTolerance)
            {
                points.RemoveAt(points.Count - 1);
            }

            if (points.Count < 4 || points.Exists(point =>
                    Math.Abs(point.Z - first.Z) > GeometryTolerance))
            {
                throw new InvalidOperationException(
                    "The ellipse did not produce a valid planar outline.");
            }

            var polyline = new Polyline();
            polyline.SetPropertiesFrom(ellipse);
            polyline.Normal = Vector3d.ZAxis;
            polyline.Elevation = first.Z;
            for (int index = 0; index < points.Count; index++)
            {
                polyline.AddVertexAt(index,
                    new Point2d(points[index].X, points[index].Y), 0, 0, 0);
            }

            polyline.Closed = true;
            polyline.ConstantWidth = width;
            return polyline;
        }

        private static void AppendEllipseSegment(
            Ellipse ellipse,
            double t1,
            double t2,
            Point3d p1,
            Point3d p2,
            double tolerance,
            int depth,
            List<Point3d> points)
        {
            double middleParameter = (t1 + t2) / 2.0;
            Point3d middle = ellipse.GetPointAtParameter(middleParameter);
            if (depth > 0 && DistanceFromPointToSegment(middle, p1, p2) > tolerance)
            {
                AppendEllipseSegment(
                    ellipse, t1, middleParameter, p1, middle,
                    tolerance, depth - 1, points);
                AppendEllipseSegment(
                    ellipse, middleParameter, t2, middle, p2,
                    tolerance, depth - 1, points);
                return;
            }

            points.Add(p2);
        }

        private static double DistanceFromPointToSegment(
            Point3d point,
            Point3d start,
            Point3d end)
        {
            Vector3d segment = end - start;
            double lengthSquared = segment.DotProduct(segment);
            if (lengthSquared <= GeometryTolerance * GeometryTolerance)
            {
                return point.DistanceTo(start);
            }

            double parameter = Math.Max(0.0, Math.Min(1.0,
                (point - start).DotProduct(segment) / lengthSquared));
            return point.DistanceTo(start + (segment * parameter));
        }

        private static Solid3d CreateBoundaryExtrusion(
            BoundaryRegion boundaryRegion,
            List<BoundaryRegion> holes,
            CivilSurface surface,
            double? customElevation,
            double height,
            Vector3d coordinateOffset)
        {
            using (var region = (Region)boundaryRegion.Region.Clone())
            {
                foreach (BoundaryRegion hole in holes)
                {
                    using (var holeRegion = (Region)hole.Region.Clone())
                    {
                        try
                        {
                            region.BooleanOperation(
                                BooleanOperationType.BoolSubtract, holeRegion);
                        }
                        catch (System.Exception exception)
                        {
                            throw new InvalidOperationException(
                                $"Could not subtract hole boundary " +
                                $"{hole.Boundary.Handle} from its planar outline: " +
                                exception.Message, exception);
                        }
                    }
                }

                if (region.IsNull)
                {
                    throw new InvalidOperationException(
                        "The marking region is empty after applying its holes.");
                }

                Point2d centroid = GetRegionCentroid(
                    region, boundaryRegion.Elevation);
                double baseElevation;
                if (customElevation.HasValue)
                {
                    baseElevation = customElevation.Value;
                }
                else
                {
                    if (surface == null)
                    {
                        throw new InvalidOperationException(
                            "A surface or custom elevation must be selected.");
                    }

                    try
                    {
                        baseElevation = surface.FindElevationAtXY(
                            centroid.X + coordinateOffset.X,
                            centroid.Y + coordinateOffset.Y);
                    }
                    catch (System.Exception exception)
                    {
                        double worldX = centroid.X + coordinateOffset.X;
                        double worldY = centroid.Y + coordinateOffset.Y;
                        throw new InvalidOperationException(
                            $"The surface has no elevation at the marking-region " +
                            $"centroid ({worldX}, {worldY}): {exception.Message}",
                            exception);
                    }
                }

                if (!IsFinite(baseElevation))
                {
                    throw new InvalidOperationException(
                        customElevation.HasValue
                            ? "The custom elevation is invalid."
                            : "The finished-level surface returned an invalid elevation.");
                }

                double worldBoundaryElevation =
                    boundaryRegion.Elevation + coordinateOffset.Z;
                region.TransformBy(Matrix3d.Displacement(new Vector3d(
                    0, 0, baseElevation - worldBoundaryElevation)));
                region.TransformBy(Matrix3d.Displacement(coordinateOffset));

                var solid = new Solid3d();
                try
                {
                    solid.Extrude(region, height, 0);
                    return solid;
                }
                catch (System.Exception exception)
                {
                    solid.Dispose();
                    throw new InvalidOperationException(
                        $"Could not extrude the marking region: {exception.Message}",
                        exception);
                }
            }
        }

        private static Vector3d GetBoundaryCoordinateOffset(
            List<Polyline> boundaries)
        {
            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double minZ = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;
            double maxZ = double.MinValue;
            bool foundExtents = false;

            foreach (Polyline boundary in boundaries)
            {
                try
                {
                    Extents3d extents = boundary.GeometricExtents;
                    minX = Math.Min(minX, extents.MinPoint.X);
                    minY = Math.Min(minY, extents.MinPoint.Y);
                    minZ = Math.Min(minZ, extents.MinPoint.Z);
                    maxX = Math.Max(maxX, extents.MaxPoint.X);
                    maxY = Math.Max(maxY, extents.MaxPoint.Y);
                    maxZ = Math.Max(maxZ, extents.MaxPoint.Z);
                    foundExtents = true;
                }
                catch
                {
                    // The individual boundary reports its extents error later.
                }
            }

            if (!foundExtents)
            {
                return new Vector3d(0, 0, 0);
            }

            return new Vector3d(
                minX + (maxX - minX) / 2.0,
                minY + (maxY - minY) / 2.0,
                minZ + (maxZ - minZ) / 2.0);
        }

        private static Extents3d TranslateExtents(
            Extents3d extents,
            Vector3d displacement)
        {
            return new Extents3d(
                extents.MinPoint + displacement,
                extents.MaxPoint + displacement);
        }

        private static string DescribeBoundary(Polyline boundary)
        {
            try
            {
                Extents3d extents = boundary.GeometricExtents;
                return $"Vertices: {boundary.NumberOfVertices}; " +
                       $"WCS bounds X [{extents.MinPoint.X}, {extents.MaxPoint.X}], " +
                       $"Y [{extents.MinPoint.Y}, {extents.MaxPoint.Y}], " +
                       $"Z [{extents.MinPoint.Z}, {extents.MaxPoint.Z}].";
            }
            catch
            {
                return $"Vertices: {boundary.NumberOfVertices}; " +
                       "WCS bounds unavailable.";
            }
        }

        private static Point2d GetRegionCentroid(Region region, double elevation)
        {
            Point3d origin = new Point3d(0, 0, elevation);
            Vector3d xAxis = Vector3d.XAxis;
            Vector3d yAxis = Vector3d.YAxis;
            RegionAreaProperties properties = region.AreaProperties(
                ref origin, ref xAxis, ref yAxis);
            Point2d centroid = properties.Centroid;
            if (!IsFinite(centroid.X) || !IsFinite(centroid.Y))
            {
                throw new InvalidOperationException(
                    "The marking region has an invalid centroid.");
            }

            return centroid;
        }

        private static void AssignContainingRegions(
            List<BoundaryRegion> regions)
        {
            for (int childIndex = 0; childIndex < regions.Count; childIndex++)
            {
                BoundaryRegion child = regions[childIndex];
                double smallestParentArea = double.MaxValue;
                for (int candidateIndex = 0;
                     candidateIndex < regions.Count;
                     candidateIndex++)
                {
                    if (candidateIndex == childIndex)
                    {
                        continue;
                    }

                    BoundaryRegion candidate = regions[candidateIndex];
                    if (Math.Abs(candidate.Elevation - child.Elevation) >
                            GeometryTolerance ||
                        candidate.Area <= child.Area + GeometryTolerance ||
                        candidate.Area >= smallestParentArea ||
                        !ExtentsContain(candidate.Extents, child.Extents))
                    {
                        continue;
                    }

                    if (ContainsRegion(candidate, child))
                    {
                        child.ParentIndex = candidateIndex;
                        smallestParentArea = candidate.Area;
                    }
                }
            }

            for (int i = 0; i < regions.Count; i++)
            {
                int parentIndex = regions[i].ParentIndex;
                var visited = new HashSet<int> { i };
                int depth = 0;
                while (parentIndex >= 0)
                {
                    if (!visited.Add(parentIndex))
                    {
                        throw new InvalidOperationException(
                            "The selected boundaries have cyclic nesting.");
                    }

                    depth++;
                    parentIndex = regions[parentIndex].ParentIndex;
                }

                regions[i].Index = i;
                regions[i].Depth = depth;
            }
        }

        private static bool ContainsRegion(
            BoundaryRegion outer,
            BoundaryRegion inner)
        {
            try
            {
                using (var intersection = (Region)outer.Region.Clone())
                using (var candidate = (Region)inner.Region.Clone())
                {
                    intersection.BooleanOperation(
                        BooleanOperationType.BoolIntersect, candidate);
                    if (intersection.IsNull)
                    {
                        return false;
                    }

                    double overlapArea =
                        GetRegionArea(intersection, outer.Elevation);
                    double tolerance = Math.Max(
                        GeometryTolerance, inner.Area * 1e-9);
                    return Math.Abs(overlapArea - inner.Area) <= tolerance;
                }
            }
            catch (System.Exception)
            {
                // Overlapping or touching outlines can make the CAD kernel reject
                // a Boolean intersection. Such outlines are independent solids,
                // not nested hole boundaries; keep processing the rest of the set.
                return false;
            }
        }

        private static double GetRegionArea(Region region, double elevation)
        {
            Point3d origin = new Point3d(0, 0, elevation);
            Vector3d xAxis = Vector3d.XAxis;
            Vector3d yAxis = Vector3d.YAxis;
            RegionAreaProperties properties = region.AreaProperties(
                ref origin, ref xAxis, ref yAxis);
            double area = Math.Abs(properties.Area);
            if (!IsFinite(area) || area <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "A selected boundary has zero or invalid area.");
            }

            return area;
        }

        private static bool ExtentsContain(Extents3d outer, Extents3d inner)
        {
            return inner.MinPoint.X >= outer.MinPoint.X - GeometryTolerance &&
                   inner.MinPoint.Y >= outer.MinPoint.Y - GeometryTolerance &&
                   inner.MaxPoint.X <= outer.MaxPoint.X + GeometryTolerance &&
                   inner.MaxPoint.Y <= outer.MaxPoint.Y + GeometryTolerance;
        }

        private static HashSet<ObjectId> FindOverlappingPolylines(
            Database database,
            List<ObjectId> polylineIds)
        {
            const double overlapTolerance = 1e-6;
            var candidates = new List<PolylineOverlapCandidate>();
            var overlappingIds = new HashSet<ObjectId>();

            using (Transaction transaction =
                   database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in polylineIds)
                {
                    if (transaction.GetObject(id, OpenMode.ForRead) is not Polyline polyline)
                    {
                        continue;
                    }

                    try
                    {
                        candidates.Add(new PolylineOverlapCandidate(
                            id, polyline, polyline.GeometricExtents));
                    }
                    catch
                    {
                        // Preparation reports malformed geometry with its usual error.
                    }
                }

                for (int firstIndex = 0;
                     firstIndex < candidates.Count;
                     firstIndex++)
                {
                    PolylineOverlapCandidate first = candidates[firstIndex];
                    if (HasSelfOverlappingSegments(first.Polyline, overlapTolerance))
                    {
                        overlappingIds.Add(first.ObjectId);
                    }

                    for (int secondIndex = firstIndex + 1;
                         secondIndex < candidates.Count;
                         secondIndex++)
                    {
                        PolylineOverlapCandidate second = candidates[secondIndex];
                        if (!ExtentsOverlap(
                                first.Extents, second.Extents, overlapTolerance) ||
                            !HaveOverlappingLinearSegments(
                                first.Polyline, second.Polyline, overlapTolerance))
                        {
                            continue;
                        }

                        overlappingIds.Add(first.ObjectId);
                        overlappingIds.Add(second.ObjectId);
                    }
                }
            }

            return overlappingIds;
        }

        private static bool HasSelfOverlappingSegments(
            Polyline polyline,
            double tolerance)
        {
            int segmentCount = GetSegmentCount(polyline);
            for (int firstIndex = 0; firstIndex < segmentCount; firstIndex++)
            {
                if (Math.Abs(polyline.GetBulgeAt(firstIndex)) > GeometryTolerance)
                {
                    continue;
                }

                GetSegmentPoints(polyline, firstIndex, out Point3d firstStart,
                    out Point3d firstEnd);
                for (int secondIndex = firstIndex + 1;
                     secondIndex < segmentCount;
                     secondIndex++)
                {
                    if (Math.Abs(polyline.GetBulgeAt(secondIndex)) > GeometryTolerance)
                    {
                        continue;
                    }

                    GetSegmentPoints(polyline, secondIndex, out Point3d secondStart,
                        out Point3d secondEnd);
                    if (SegmentsOverlap(
                            firstStart, firstEnd, secondStart, secondEnd, tolerance))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HaveOverlappingLinearSegments(
            Polyline first,
            Polyline second,
            double tolerance)
        {
            int firstSegmentCount = GetSegmentCount(first);
            int secondSegmentCount = GetSegmentCount(second);
            for (int firstIndex = 0;
                 firstIndex < firstSegmentCount;
                 firstIndex++)
            {
                if (Math.Abs(first.GetBulgeAt(firstIndex)) > GeometryTolerance)
                {
                    continue;
                }

                GetSegmentPoints(first, firstIndex, out Point3d firstStart,
                    out Point3d firstEnd);
                for (int secondIndex = 0;
                     secondIndex < secondSegmentCount;
                     secondIndex++)
                {
                    if (Math.Abs(second.GetBulgeAt(secondIndex)) > GeometryTolerance)
                    {
                        continue;
                    }

                    GetSegmentPoints(second, secondIndex, out Point3d secondStart,
                        out Point3d secondEnd);
                    if (SegmentsOverlap(
                            firstStart, firstEnd, secondStart, secondEnd, tolerance))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static int GetSegmentCount(Polyline polyline)
        {
            return polyline.Closed
                ? polyline.NumberOfVertices
                : polyline.NumberOfVertices - 1;
        }

        private static void GetSegmentPoints(
            Polyline polyline,
            int segmentIndex,
            out Point3d start,
            out Point3d end)
        {
            start = polyline.GetPoint3dAt(segmentIndex);
            end = polyline.GetPoint3dAt(
                (segmentIndex + 1) % polyline.NumberOfVertices);
        }

        private static bool SegmentsOverlap(
            Point3d firstStart,
            Point3d firstEnd,
            Point3d secondStart,
            Point3d secondEnd,
            double minimumTolerance)
        {
            Vector3d firstDirection = firstEnd - firstStart;
            Vector3d secondDirection = secondEnd - secondStart;
            double firstLength = firstDirection.Length;
            double secondLength = secondDirection.Length;
            if (firstLength <= minimumTolerance ||
                secondLength <= minimumTolerance)
            {
                return false;
            }

            Vector3d firstUnit = firstDirection / firstLength;
            Vector3d secondUnit = secondDirection / secondLength;
            if (firstUnit.CrossProduct(secondUnit).Length > 1e-8)
            {
                return false;
            }

            Vector3d firstToSecondStart = secondStart - firstStart;
            Vector3d firstToSecondEnd = secondEnd - firstStart;
            double tolerance = Math.Max(
                minimumTolerance,
                Math.Max(firstLength, secondLength) * 1e-10);
            if (firstToSecondStart.CrossProduct(firstUnit).Length > tolerance ||
                firstToSecondEnd.CrossProduct(firstUnit).Length > tolerance)
            {
                return false;
            }

            double secondStartProjection = firstToSecondStart.DotProduct(firstUnit);
            double secondEndProjection = firstToSecondEnd.DotProduct(firstUnit);
            double overlapLength =
                Math.Min(firstLength,
                    Math.Max(secondStartProjection, secondEndProjection)) -
                Math.Max(0,
                    Math.Min(secondStartProjection, secondEndProjection));
            return overlapLength > tolerance;
        }

        private static bool ExtentsOverlap(
            Extents3d first,
            Extents3d second,
            double tolerance)
        {
            return first.MinPoint.X <= second.MaxPoint.X + tolerance &&
                   first.MaxPoint.X + tolerance >= second.MinPoint.X &&
                   first.MinPoint.Y <= second.MaxPoint.Y + tolerance &&
                   first.MaxPoint.Y + tolerance >= second.MinPoint.Y &&
                   first.MinPoint.Z <= second.MaxPoint.Z + tolerance &&
                   first.MaxPoint.Z + tolerance >= second.MinPoint.Z;
        }

        private static void MarkPolylineAsOverlapping(
            Database database,
            ObjectId polylineId,
            Editor editor)
        {
            try
            {
                using (Transaction transaction =
                       database.TransactionManager.StartTransaction())
                {
                    if (transaction.GetObject(
                            polylineId, OpenMode.ForWrite, false) is Entity entity)
                    {
                        entity.Color = Color.FromColorIndex(ColorMethod.ByAci, 6);
                        transaction.Commit();
                    }
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    $"\nCould not color overlapping polyline " +
                    $"{polylineId.Handle} magenta: {exception.Message}");
            }
        }

        private static void MarkPolylineAsFailed(
            Database database,
            ObjectId polylineId,
            Editor editor)
        {
            try
            {
                using (Transaction transaction =
                       database.TransactionManager.StartTransaction())
                {
                    if (transaction.GetObject(
                            polylineId, OpenMode.ForWrite, false) is Entity entity)
                    {
                        entity.Color =
                            Color.FromColorIndex(ColorMethod.ByAci, 2);
                        transaction.Commit();
                    }
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    $"\nCould not color failed polyline {polylineId.Handle} yellow: " +
                    exception.Message);
            }
        }

        private static void DisposeAll(DBObjectCollection objects)
        {
            foreach (DBObject item in objects)
            {
                item.Dispose();
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private sealed class SolidCreationResult
        {
            public SolidCreationResult(
                int solidsCreated,
                List<BoundaryFailure> failures)
            {
                SolidsCreated = solidsCreated;
                Failures = failures;
            }

            public int SolidsCreated { get; }
            public List<BoundaryFailure> Failures { get; }
        }

        private sealed class BoundaryFailure
        {
            public BoundaryFailure(ObjectId boundaryId, string message)
            {
                BoundaryId = boundaryId;
                Message = message;
            }

            public ObjectId BoundaryId { get; }
            public string Message { get; }
        }

        private sealed class BoundarySelectionResult
        {
            public BoundarySelectionResult(
                List<ObjectId> processableIds,
                List<BoundaryFailure> failures)
            {
                ProcessableIds = processableIds;
                Failures = failures;
            }

            public List<ObjectId> ProcessableIds { get; }
            public List<BoundaryFailure> Failures { get; }
        }

        private sealed class BoundaryCandidate
        {
            public BoundaryCandidate(
                ObjectId objectId,
                Polyline polyline,
                Extents3d extents)
            {
                ObjectId = objectId;
                Polyline = polyline;
                Extents = extents;
            }

            public ObjectId ObjectId { get; }
            public Polyline Polyline { get; }
            public Extents3d Extents { get; }
        }

        private sealed class PolylineOverlapCandidate
        {
            public PolylineOverlapCandidate(
                ObjectId objectId,
                Polyline polyline,
                Extents3d extents)
            {
                ObjectId = objectId;
                Polyline = polyline;
                Extents = extents;
            }

            public ObjectId ObjectId { get; }
            public Polyline Polyline { get; }
            public Extents3d Extents { get; }
        }

        private sealed class BoundaryRegion
        {
            public BoundaryRegion(
                Polyline boundary,
                Region region,
                double area,
                Extents3d extents,
                double elevation)
            {
                Boundary = boundary;
                Region = region;
                Area = area;
                Extents = extents;
                Elevation = elevation;
                ParentIndex = -1;
            }

            public Polyline Boundary { get; }
            public Region Region { get; }
            public double Area { get; }
            public Extents3d Extents { get; }
            public double Elevation { get; }
            public int ParentIndex { get; set; }
            public int Index { get; set; }
            public int Depth { get; set; }
        }
    }

    internal sealed class SurfaceChoice
    {
        public SurfaceChoice(ObjectId objectId, string name)
        {
            ObjectId = objectId;
            Name = name;
        }

        public ObjectId ObjectId { get; }
        public string Name { get; }
    }

    [SupportedOSPlatform("windows")]
    internal sealed class SurfaceSelectionDialog : Forms.Form
    {
        private readonly Forms.CheckBox customElevationCheckBox;
        private readonly Forms.NumericUpDown elevationInput;

        public SurfaceSelectionDialog(List<SurfaceChoice> surfaces)
        {
            Text = "Select Finished-Level Surface";
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
            StartPosition = Forms.FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = Forms.AutoScaleMode.Font;
            ClientSize = new Drawing.Size(460, 320);

            var instructionLabel = new Forms.Label
            {
                AutoSize = true,
                Location = new Drawing.Point(14, 14),
                Text = "Choose a finished-level surface or set a custom elevation:"
            };
            var surfaceList = new Forms.ListBox
            {
                Location = new Drawing.Point(14, 44),
                Size = new Drawing.Size(432, 155),
                Anchor = Forms.AnchorStyles.Top |
                    Forms.AnchorStyles.Left |
                    Forms.AnchorStyles.Right,
                DisplayMember = nameof(SurfaceChoice.Name)
            };
            customElevationCheckBox = new Forms.CheckBox
            {
                AutoSize = true,
                Location = new Drawing.Point(14, 207),
                Text = "Use custom elevation"
            };
            var elevationLabel = new Forms.Label
            {
                AutoSize = true,
                Location = new Drawing.Point(34, 239),
                Text = "Elevation:"
            };
            elevationInput = new Forms.NumericUpDown
            {
                DecimalPlaces = 6,
                Increment = 0.1M,
                Minimum = decimal.MinValue,
                Maximum = decimal.MaxValue,
                Location = new Drawing.Point(105, 235),
                Size = new Drawing.Size(150, 23),
                Enabled = false
            };
            var resultLabel = new Forms.Label
            {
                AutoSize = true,
                Location = new Drawing.Point(14, 274),
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left,
                Text = $"{surfaces.Count} surface(s)"
            };
            var okButton = new Forms.Button
            {
                DialogResult = Forms.DialogResult.OK,
                Location = new Drawing.Point(290, 278),
                Size = new Drawing.Size(75, 28),
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                Text = "OK"
            };
            var cancelButton = new Forms.Button
            {
                DialogResult = Forms.DialogResult.Cancel,
                Location = new Drawing.Point(371, 278),
                Size = new Drawing.Size(75, 28),
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                Text = "Cancel"
            };

            Controls.Add(instructionLabel);
            Controls.Add(surfaceList);
            Controls.Add(customElevationCheckBox);
            Controls.Add(elevationLabel);
            Controls.Add(elevationInput);
            Controls.Add(resultLabel);
            Controls.Add(okButton);
            Controls.Add(cancelButton);
            AcceptButton = okButton;
            CancelButton = cancelButton;

            foreach (SurfaceChoice surface in surfaces)
            {
                surfaceList.Items.Add(surface);
            }

            if (surfaceList.Items.Count > 0)
            {
                surfaceList.SelectedIndex = 0;
            }

            SelectedSurface = surfaceList.SelectedItem as SurfaceChoice;
            okButton.Enabled = SelectedSurface != null;
            int previousSurfaceIndex = surfaceList.SelectedIndex;
            surfaceList.SelectedIndexChanged += (_, _) =>
            {
                if (!customElevationCheckBox.Checked)
                {
                    SelectedSurface = surfaceList.SelectedItem as SurfaceChoice;
                    okButton.Enabled = SelectedSurface != null;
                }
            };
            customElevationCheckBox.CheckedChanged += (_, _) =>
            {
                if (customElevationCheckBox.Checked)
                {
                    if (surfaceList.SelectedIndex >= 0)
                    {
                        previousSurfaceIndex = surfaceList.SelectedIndex;
                    }

                    surfaceList.SelectedIndex = -1;
                    SelectedSurface = null;
                    surfaceList.Enabled = false;
                    elevationInput.Enabled = true;
                }
                else
                {
                    surfaceList.Enabled = true;
                    elevationInput.Enabled = false;
                    if (previousSurfaceIndex >= 0 &&
                        previousSurfaceIndex < surfaceList.Items.Count)
                    {
                        surfaceList.SelectedIndex = previousSurfaceIndex;
                    }
                }

                okButton.Enabled = customElevationCheckBox.Checked ||
                    surfaceList.SelectedItem is SurfaceChoice;
            };
        }

        public SurfaceChoice SelectedSurface { get; private set; }
        public bool UseCustomElevation => customElevationCheckBox.Checked;
        public double CustomElevation => (double)elevationInput.Value;
    }

    [SupportedOSPlatform("windows")]
    internal sealed class BlockLayerAssignmentDialog : Forms.Form
    {
        private readonly Forms.ComboBox targetLayerCombo;
        private readonly Forms.CheckBox changeContentsCheckBox;

        public BlockLayerAssignmentDialog(
            List<string> layerNames,
            int selectedBlockCount,
            string commonLayerName)
        {
            Text = "Assign Layer to Blocks";
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
            StartPosition = Forms.FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = Forms.AutoScaleMode.Font;
            ClientSize = new Drawing.Size(540, 330);

            var headingLabel = new Forms.Label
            {
                AutoSize = true,
                Font = new Drawing.Font(
                    Forms.SystemInformation.MenuFont,
                    Drawing.FontStyle.Bold),
                Location = new Drawing.Point(18, 16),
                Text = "Choose a layer for the selected block references"
            };
            var instructionLabel = new Forms.Label
            {
                Location = new Drawing.Point(18, 46),
                Size = new Drawing.Size(504, 36),
                Text = $"{selectedBlockCount} block reference(s) selected. " +
                    "Choose an existing layer or type a new layer name."
            };
            var layerGroup = new Forms.GroupBox
            {
                Location = new Drawing.Point(16, 88),
                Size = new Drawing.Size(508, 98),
                Text = "Target layer"
            };
            var layerLabel = new Forms.Label
            {
                AutoSize = true,
                Location = new Drawing.Point(12, 24),
                Text = "Existing layer or new name:"
            };
            targetLayerCombo = new Forms.ComboBox
            {
                Anchor = Forms.AnchorStyles.Top |
                    Forms.AnchorStyles.Left |
                    Forms.AnchorStyles.Right,
                DropDownStyle = Forms.ComboBoxStyle.DropDown,
                Location = new Drawing.Point(15, 47),
                Size = new Drawing.Size(478, 24),
                AutoCompleteMode = Forms.AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = Forms.AutoCompleteSource.ListItems
            };
            foreach (string layerName in layerNames)
            {
                targetLayerCombo.Items.Add(layerName);
            }

            targetLayerCombo.Text = commonLayerName;
            layerGroup.Controls.Add(layerLabel);
            layerGroup.Controls.Add(targetLayerCombo);

            changeContentsCheckBox = new Forms.CheckBox
            {
                AutoSize = true,
                Location = new Drawing.Point(20, 202),
                Text = "Also assign this layer to objects inside the selected blocks"
            };
            var contentsInfoLabel = new Forms.Label
            {
                Location = new Drawing.Point(42, 226),
                Size = new Drawing.Size(480, 48),
                Text = "Block contents are copied for the selected instances, so " +
                    "other insertions remain unchanged. Dynamic blocks and " +
                    "external references are skipped when this option is enabled."
            };
            var applyButton = new Forms.Button
            {
                DialogResult = Forms.DialogResult.OK,
                Location = new Drawing.Point(350, 285),
                Size = new Drawing.Size(82, 30),
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                Text = "Apply"
            };
            var cancelButton = new Forms.Button
            {
                DialogResult = Forms.DialogResult.Cancel,
                Location = new Drawing.Point(440, 285),
                Size = new Drawing.Size(82, 30),
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                Text = "Cancel"
            };

            Controls.Add(headingLabel);
            Controls.Add(instructionLabel);
            Controls.Add(layerGroup);
            Controls.Add(changeContentsCheckBox);
            Controls.Add(contentsInfoLabel);
            Controls.Add(applyButton);
            Controls.Add(cancelButton);
            AcceptButton = applyButton;
            CancelButton = cancelButton;
            applyButton.Enabled = !string.IsNullOrWhiteSpace(targetLayerCombo.Text);
            targetLayerCombo.TextChanged += (_, _) =>
            {
                applyButton.Enabled =
                    !string.IsNullOrWhiteSpace(targetLayerCombo.Text);
            };
        }

        public string TargetLayerName => targetLayerCombo.Text.Trim();

        public bool ChangeContents => changeContentsCheckBox.Checked;
    }
}
