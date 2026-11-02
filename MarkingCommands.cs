using System;
using System.Collections.Generic;
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
    public class MarkingCommands : IExtensionApplication
    {
        private const double GeometryTolerance = 1e-9;
        private const string PreparationLayerName = "Marking_Prep";

        public void Initialize()
        {
            var document = Autodesk.AutoCAD.ApplicationServices.Application
                .DocumentManager.MdiActiveDocument;
            document?.Editor.WriteMessage(
                "\nSolidsFromPolylines loaded. Commands: MARKING_PREP, " +
                "MARKING_SOLIDS, MARKING_BLOCK, MARKING_BLOCK_LAYER.");
        }

        public void Terminate()
        {
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
                        gapLength);
                    boundariesCreated += made;
                    succeeded++;
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
            if (surfaces.Count == 0)
            {
                editor.WriteMessage("\nThis drawing contains no Civil 3D surfaces.");
                return;
            }

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
                        "\nSelect closed marking boundaries for surface-based solids: "
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

            // Complete all interactive Editor prompts before showing the modal
            // Windows form. Re-entering Editor.GetDouble/GetSelection directly
            // after a modal form closes can destabilize the AutoCAD command loop.
            SurfaceChoice chosenSurface;
            using (var surfaceDialog = new SurfaceSelectionDialog(surfaces))
            {
                if (Autodesk.AutoCAD.ApplicationServices.Application
                        .ShowModalDialog(surfaceDialog) != Forms.DialogResult.OK)
                {
                    return;
                }

                chosenSurface = surfaceDialog.SelectedSurface;
                if (chosenSurface == null)
                {
                    editor.WriteMessage("\nNo surface was selected.");
                    return;
                }
            }

            ObjectId surfaceId = chosenSurface.ObjectId;
            string surfaceName = chosenSurface.Name;

            try
            {
                SolidCreationResult creationResult = CreateSolidsForBoundaries(
                    document.Database,
                    selectedIds,
                    surfaceId,
                    heightResult.Value);
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
                    $"{creationResult.Failures.Count} boundary group(s) failed. " +
                    $"Surface: {surfaceName}. " +
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

        private static int PrepareOne(
            Database database,
            ObjectId sourceId,
            bool dashed,
            double dashLength,
            double gapLength)
        {
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

                double width = Math.Abs(source.ConstantWidth);
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
                    if (!dashed)
                    {
                        boundaries.AddRange(
                            CreateBoundariesFromPath(source, source, width / 2.0));
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
                                        dashPath, source, width / 2.0));
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
                    source.Erase();
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
            double halfWidth)
        {
            DBObjectCollection leftCurves = path.GetOffsetCurves(halfWidth);
            DBObjectCollection rightCurves = path.GetOffsetCurves(-halfWidth);
            try
            {
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

                    return new List<Polyline>
                    {
                        CopyClosedOffset(left, source),
                        CopyClosedOffset(right, source)
                    };
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
                DisposeAll(leftCurves);
                DisposeAll(rightCurves);
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

        private static SolidCreationResult CreateSolidsForBoundaries(
            Database database,
            List<ObjectId> boundaryIds,
            ObjectId surfaceId,
            double height)
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

                CivilSurface surface = transaction.GetObject(
                    surfaceId, OpenMode.ForRead) as CivilSurface;
                if (surface == null)
                {
                    throw new InvalidOperationException(
                        "The selected object is no longer a Civil 3D surface.");
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
                    foreach (BoundaryRegion boundaryRegion in boundaryRegions)
                    {
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
                                foreach (BoundaryRegion hole in holes)
                                {
                                    erasedBoundaryIds.Add(hole.Boundary.ObjectId);
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

        private static Solid3d CreateBoundaryExtrusion(
            BoundaryRegion boundaryRegion,
            List<BoundaryRegion> holes,
            CivilSurface surface,
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
                double surfaceElevation;
                try
                {
                    surfaceElevation = surface.FindElevationAtXY(
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

                if (!IsFinite(surfaceElevation))
                {
                    throw new InvalidOperationException(
                        "The finished-level surface returned an invalid elevation.");
                }

                double worldBoundaryElevation =
                    boundaryRegion.Elevation + coordinateOffset.Z;
                region.TransformBy(Matrix3d.Displacement(new Vector3d(
                    0, 0, surfaceElevation - worldBoundaryElevation)));
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

    internal sealed class SurfaceSelectionDialog : Forms.Form
    {
        public SurfaceSelectionDialog(List<SurfaceChoice> surfaces)
        {
            Text = "Select Finished-Level Surface";
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
            StartPosition = Forms.FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = Forms.AutoScaleMode.Font;
            ClientSize = new Drawing.Size(460, 270);

            var instructionLabel = new Forms.Label
            {
                AutoSize = true,
                Location = new Drawing.Point(14, 14),
                Text = "Choose the finished-level surface to use:"
            };
            var surfaceList = new Forms.ListBox
            {
                Location = new Drawing.Point(14, 44),
                Size = new Drawing.Size(432, 170),
                Anchor = Forms.AnchorStyles.Top |
                    Forms.AnchorStyles.Bottom |
                    Forms.AnchorStyles.Left |
                    Forms.AnchorStyles.Right,
                DisplayMember = nameof(SurfaceChoice.Name)
            };
            var resultLabel = new Forms.Label
            {
                AutoSize = true,
                Location = new Drawing.Point(14, 222),
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left
            };
            var okButton = new Forms.Button
            {
                DialogResult = Forms.DialogResult.OK,
                Location = new Drawing.Point(290, 230),
                Size = new Drawing.Size(75, 28),
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                Text = "OK"
            };
            var cancelButton = new Forms.Button
            {
                DialogResult = Forms.DialogResult.Cancel,
                Location = new Drawing.Point(371, 230),
                Size = new Drawing.Size(75, 28),
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                Text = "Cancel"
            };

            Controls.Add(instructionLabel);
            Controls.Add(surfaceList);
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
            resultLabel.Text = $"{surfaceList.Items.Count} surface(s)";
            surfaceList.SelectedIndexChanged += (_, _) =>
            {
                SelectedSurface = surfaceList.SelectedItem as SurfaceChoice;
                okButton.Enabled = SelectedSurface != null;
            };
        }

        public SurfaceChoice SelectedSurface { get; private set; }
    }

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
