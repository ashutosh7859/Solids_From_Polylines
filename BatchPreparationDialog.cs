using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using System.Runtime.Versioning;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SolidsFromPolylines
{
    internal sealed class BatchPreparationGroup
    {
        public BatchPreparationGroup(
            string layerName,
            string linetypeName,
            string widthLabel,
            string key,
            double width)
        {
            LayerName = layerName;
            LinetypeName = linetypeName;
            WidthLabel = widthLabel;
            Key = key;
            Width = width;
            EligibleIds = new List<ObjectId>();
            Exceptions = new Dictionary<ObjectId, string>();
            Included = false;
        }

        public string LayerName { get; }
        public string LinetypeName { get; }
        public string WidthLabel { get; }
        public string Key { get; }
        public double Width { get; }
        public List<ObjectId> EligibleIds { get; }
        public Dictionary<ObjectId, string> Exceptions { get; }
        public bool Included { get; set; }
        public bool Dashed { get; set; }
        public double DashLength { get; set; }
        public double GapLength { get; set; }

        public int Count => EligibleIds.Count + Exceptions.Count;

        public string ExceptionSummary
        {
            get
            {
                return string.Join(
                    "; ",
                    Exceptions.Values
                        .GroupBy(reason => reason)
                        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(group => $"{group.Count()} {group.Key}"));
            }
        }

        public void AddException(ObjectId objectId, string reason)
        {
            if (EligibleIds.Remove(objectId))
            {
                Exceptions[objectId] = reason;
            }
            else if (Exceptions.TryGetValue(objectId, out string existingReason))
            {
                if (existingReason.IndexOf(
                        reason, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    Exceptions[objectId] = existingReason + "; " + reason;
                }
            }
            else
            {
                Exceptions[objectId] = reason;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    internal sealed class BatchPreparationDialog : Forms.Form
    {
        private readonly Forms.DataGridView groupsGrid;
        private readonly Forms.Button applyButton;
        private readonly Forms.Button exportProfileButton;
        private readonly Forms.Button importProfileButton;
        private readonly Forms.Button refreshButton;
        private readonly Forms.Label statusLabel;
        private readonly Forms.Label summaryLabel;
        private List<Forms.DataGridViewRow> includeRowsBeforeClick;
        private bool includeValueAfterClick;
        private bool syncingIncludeValues;

        public BatchPreparationDialog(List<BatchPreparationGroup> groups)
        {
            Text = "Batch Polyline Preparation";
            FormBorderStyle = Forms.FormBorderStyle.Sizable;
            StartPosition = Forms.FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            AutoScaleMode = Forms.AutoScaleMode.Font;
            MinimumSize = new Drawing.Size(900, 440);
            ClientSize = new Drawing.Size(1120, 560);

            var instructions = new Forms.Label
            {
                AutoSize = false,
                Location = new Drawing.Point(14, 12),
                Size = new Drawing.Size(1088, 38),
                Text = "Choose the groups to prepare and set continuous or dashed output. " +
                    "Use Select to inspect a group in the drawing. Refresh after " +
                    "drawing edits; dash/gap use drawing units."
            };
            var selectAllButton = new Forms.Button
            {
                Location = new Drawing.Point(14, 54),
                Size = new Drawing.Size(90, 28),
                Text = "Select ready"
            };
            var clearButton = new Forms.Button
            {
                Location = new Drawing.Point(110, 54),
                Size = new Drawing.Size(90, 28),
                Text = "Clear all"
            };
            var bulkLabel = new Forms.Label
            {
                AutoSize = true,
                Location = new Drawing.Point(220, 61),
                Text = "Set selected rows:"
            };
            var bulkOutput = new Forms.ComboBox
            {
                DropDownStyle = Forms.ComboBoxStyle.DropDownList,
                Location = new Drawing.Point(322, 56),
                Size = new Drawing.Size(105, 24)
            };
            bulkOutput.Items.AddRange(new object[] { "Continuous", "Dash" });
            bulkOutput.SelectedIndex = 0;
            var bulkDash = new Forms.TextBox
            {
                Location = new Drawing.Point(440, 56),
                Size = new Drawing.Size(68, 24),
                PlaceholderText = "Dash"
            };
            var bulkGap = new Forms.TextBox
            {
                Location = new Drawing.Point(516, 56),
                Size = new Drawing.Size(68, 24),
                PlaceholderText = "Gap"
            };
            var bulkApplyButton = new Forms.Button
            {
                Location = new Drawing.Point(594, 54),
                Size = new Drawing.Size(170, 28),
                Text = "Apply output to rows"
            };
            refreshButton = new Forms.Button
            {
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Right,
                Location = new Drawing.Point(1012, 54),
                Size = new Drawing.Size(90, 28),
                Text = "Refresh"
            };

            groupsGrid = new Forms.DataGridView
            {
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Bottom |
                    Forms.AnchorStyles.Left | Forms.AnchorStyles.Right,
                AutoSizeColumnsMode = Forms.DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode =
                    Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                Location = new Drawing.Point(14, 90),
                MultiSelect = true,
                ReadOnly = false,
                RowHeadersVisible = false,
                SelectionMode = Forms.DataGridViewSelectionMode.FullRowSelect,
                Size = new Drawing.Size(1088, 376)
            };

            AddColumns();
            statusLabel = new Forms.Label
            {
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left |
                    Forms.AnchorStyles.Right,
                AutoSize = false,
                Location = new Drawing.Point(14, 470),
                Size = new Drawing.Size(1088, 20)
            };
            summaryLabel = new Forms.Label
            {
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left,
                AutoSize = true,
                Location = new Drawing.Point(14, 500)
            };
            exportProfileButton = new Forms.Button
            {
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                Location = new Drawing.Point(650, 515),
                Size = new Drawing.Size(96, 30),
                Text = "Export profile"
            };
            importProfileButton = new Forms.Button
            {
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                Location = new Drawing.Point(752, 515),
                Size = new Drawing.Size(96, 30),
                Text = "Import profile"
            };
            applyButton = new Forms.Button
            {
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                Location = new Drawing.Point(854, 515),
                Size = new Drawing.Size(118, 30),
                Text = "Prepare"
            };
            var cancelButton = new Forms.Button
            {
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                Location = new Drawing.Point(982, 515),
                Size = new Drawing.Size(120, 30),
                Text = "Cancel"
            };

            selectAllButton.Click += (_, _) =>
            {
                groupsGrid.ClearSelection();
                foreach (Forms.DataGridViewRow row in groupsGrid.Rows)
                {
                    var group = row.Tag as BatchPreparationGroup;
                    bool ready = group != null && group.EligibleIds.Count > 0;
                    row.Cells[0].Value = ready;
                    row.Selected = ready;
                }
            };
            clearButton.Click += (_, _) =>
            {
                groupsGrid.ClearSelection();
                foreach (Forms.DataGridViewRow row in groupsGrid.Rows)
                {
                    row.Cells[0].Value = false;
                }
            };
            groupsGrid.CellMouseDown += CaptureIncludeSelection;
            groupsGrid.CurrentCellDirtyStateChanged += CommitCurrentCell;
            groupsGrid.CellValueChanged += UpdateCellDependentState;
            groupsGrid.KeyDown += ToggleIncludeForSelectedRows;
            bulkApplyButton.Click += (_, _) => ApplyBulkSettings(
                bulkOutput.SelectedItem?.ToString(),
                bulkDash.Text,
                bulkGap.Text);
            bulkOutput.SelectedIndexChanged += (_, _) =>
                UpdateBulkDashGapAvailability(bulkOutput, bulkDash, bulkGap);
            applyButton.Click += (_, _) => ApplySettings();
            exportProfileButton.Click += (_, _) => ExportProfile();
            importProfileButton.Click += (_, _) => ImportProfile();
            refreshButton.Click += (_, _) => RefreshRequested?.Invoke();
            groupsGrid.CellContentClick += (_, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == 10 &&
                    groupsGrid.Rows[e.RowIndex].Tag is BatchPreparationGroup group)
                {
                    SelectGroupRequested?.Invoke(group);
                }
            };
            cancelButton.Click += (_, _) => Close();
            FormClosed += (_, _) => DialogClosed?.Invoke();

            Controls.Add(instructions);
            Controls.Add(selectAllButton);
            Controls.Add(clearButton);
            Controls.Add(bulkLabel);
            Controls.Add(bulkOutput);
            Controls.Add(bulkDash);
            Controls.Add(bulkGap);
            Controls.Add(bulkApplyButton);
            Controls.Add(refreshButton);
            Controls.Add(groupsGrid);
            Controls.Add(statusLabel);
            Controls.Add(summaryLabel);
            Controls.Add(exportProfileButton);
            Controls.Add(importProfileButton);
            Controls.Add(applyButton);
            Controls.Add(cancelButton);
            AcceptButton = applyButton;
            CancelButton = cancelButton;
            SetGroups(groups);
            SetStale(false);
            UpdateBulkDashGapAvailability(bulkOutput, bulkDash, bulkGap);
        }

        public event Action RefreshRequested;
        public event Action PrepareRequested;
        public event Action<BatchPreparationGroup> SelectGroupRequested;
        public event Action DialogClosed;

        public void SetGroups(List<BatchPreparationGroup> groups)
        {
            groupsGrid.Rows.Clear();
            foreach (BatchPreparationGroup group in groups)
            {
                int rowIndex = groupsGrid.Rows.Add(
                    group.Included,
                    group.LayerName,
                    group.LinetypeName,
                    group.WidthLabel,
                    group.Count,
                    group.EligibleIds.Count,
                    group.ExceptionSummary,
                    group.Dashed ? "Dash" : "Continuous",
                    group.DashLength > 0
                        ? group.DashLength.ToString("G", CultureInfo.InvariantCulture)
                        : string.Empty,
                    group.GapLength > 0
                        ? group.GapLength.ToString("G", CultureInfo.InvariantCulture)
                        : string.Empty,
                    "Select");
                groupsGrid.Rows[rowIndex].Tag = group;
                groupsGrid.Rows[rowIndex].Cells[1].ReadOnly = true;
                groupsGrid.Rows[rowIndex].Cells[2].ReadOnly = true;
                groupsGrid.Rows[rowIndex].Cells[3].ReadOnly = true;
                groupsGrid.Rows[rowIndex].Cells[4].ReadOnly = true;
                groupsGrid.Rows[rowIndex].Cells[5].ReadOnly = true;
                groupsGrid.Rows[rowIndex].Cells[6].ReadOnly = true;
                groupsGrid.Rows[rowIndex].Cells[10].ReadOnly = true;
                SetDashGapAvailability(
                    groupsGrid.Rows[rowIndex], group.Dashed, clearWhenContinuous: true);
            }

            int total = groups.Sum(group => group.Count);
            int eligible = groups.Sum(group => group.EligibleIds.Count);
            int exceptionCount = groups.Sum(group => group.Exceptions.Count);
            summaryLabel.Text = $"{groups.Count} group(s) · {total} polyline(s) · " +
                $"{eligible} ready · {exceptionCount} exception(s)";
            applyButton.Enabled = groups.Any(group => group.EligibleIds.Count > 0);
        }

        public void SetStale(bool stale)
        {
            statusLabel.Text = stale
                ? "Drawing changed. Refresh to review the updated groups before preparing."
                : "Drawing snapshot is current.";
            statusLabel.ForeColor = stale
                ? Drawing.Color.DarkGoldenrod
                : Drawing.SystemColors.ControlText;
            refreshButton.Enabled = true;
            exportProfileButton.Enabled = !stale;
            importProfileButton.Enabled = !stale;
            applyButton.Enabled = !stale &&
                groupsGrid.Rows.Cast<Forms.DataGridViewRow>().Any(
                    row => (row.Tag as BatchPreparationGroup)?.EligibleIds.Count > 0);
        }

        public void SetBusy(bool busy, string message = null)
        {
            Enabled = !busy;
            if (!string.IsNullOrEmpty(message))
            {
                statusLabel.Text = message;
            }
        }

        private void CaptureIncludeSelection(
            object sender,
            Forms.DataGridViewCellMouseEventArgs e)
        {
            includeRowsBeforeClick = null;
            if (e.RowIndex < 0 || e.ColumnIndex != 0)
            {
                return;
            }

            Forms.DataGridViewRow clickedRow = groupsGrid.Rows[e.RowIndex];
            if (!clickedRow.Selected || groupsGrid.SelectedRows.Count < 2)
            {
                return;
            }

            includeRowsBeforeClick = groupsGrid.SelectedRows
                .Cast<Forms.DataGridViewRow>()
                .ToList();
            includeValueAfterClick = !Convert.ToBoolean(
                clickedRow.Cells[0].Value ?? false, CultureInfo.InvariantCulture);
        }

        private void ToggleIncludeForSelectedRows(object sender, Forms.KeyEventArgs e)
        {
            if (e.KeyCode != Forms.Keys.Space ||
                groupsGrid.CurrentCell == null ||
                groupsGrid.CurrentCell.ColumnIndex != 0 ||
                groupsGrid.SelectedRows.Count < 2)
            {
                return;
            }

            e.Handled = true;
            e.SuppressKeyPress = true;
            bool include = !Convert.ToBoolean(
                groupsGrid.CurrentCell.Value ?? false,
                CultureInfo.InvariantCulture);
            foreach (Forms.DataGridViewRow row in groupsGrid.SelectedRows)
            {
                row.Cells[0].Value = include;
            }
        }

        private void CommitCurrentCell(object sender, EventArgs e)
        {
            if (!groupsGrid.IsCurrentCellDirty || groupsGrid.CurrentCell == null)
            {
                return;
            }

            int columnIndex = groupsGrid.CurrentCell.ColumnIndex;
            if (columnIndex == 0 || columnIndex == 7)
            {
                groupsGrid.CommitEdit(Forms.DataGridViewDataErrorContexts.Commit);
            }

            if (columnIndex != 0 || includeRowsBeforeClick == null ||
                syncingIncludeValues)
            {
                return;
            }

            syncingIncludeValues = true;
            foreach (Forms.DataGridViewRow row in includeRowsBeforeClick)
            {
                row.Cells[0].Value = includeValueAfterClick;
                row.Selected = true;
            }

            syncingIncludeValues = false;
            includeRowsBeforeClick = null;
        }

        private void UpdateCellDependentState(
            object sender,
            Forms.DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 7)
            {
                return;
            }

            Forms.DataGridViewRow row = groupsGrid.Rows[e.RowIndex];
            bool dashed = string.Equals(
                row.Cells[7].Value?.ToString(),
                "Dash",
                StringComparison.OrdinalIgnoreCase);
            SetDashGapAvailability(row, dashed, clearWhenContinuous: true);
        }

        private void SetDashGapAvailability(
            Forms.DataGridViewRow row,
            bool dashed,
            bool clearWhenContinuous)
        {
            foreach (int columnIndex in new[] { 8, 9 })
            {
                Forms.DataGridViewCell cell = row.Cells[columnIndex];
                cell.ReadOnly = !dashed;
                cell.Style.BackColor = dashed
                    ? groupsGrid.DefaultCellStyle.BackColor
                    : Drawing.SystemColors.Control;
                cell.Style.ForeColor = dashed
                    ? groupsGrid.DefaultCellStyle.ForeColor
                    : Drawing.SystemColors.GrayText;
                if (!dashed && clearWhenContinuous)
                {
                    cell.Value = string.Empty;
                }
            }

        }

        private static void UpdateBulkDashGapAvailability(
            Forms.ComboBox output,
            Forms.TextBox dash,
            Forms.TextBox gap)
        {
            bool enabled = string.Equals(
                output.SelectedItem?.ToString(),
                "Dash",
                StringComparison.OrdinalIgnoreCase);
            dash.Enabled = enabled;
            gap.Enabled = enabled;
            if (!enabled)
            {
                dash.Clear();
                gap.Clear();
            }
        }

        private void AddColumns()
        {
            groupsGrid.Columns.Add(new Forms.DataGridViewCheckBoxColumn
            {
                FillWeight = 52,
                HeaderText = "Include",
                Name = "Include"
            });
            groupsGrid.Columns.Add(TextColumn("Layer", 125));
            groupsGrid.Columns.Add(TextColumn("Linetype", 90));
            groupsGrid.Columns.Add(TextColumn("Width", 62));
            groupsGrid.Columns.Add(TextColumn("Count", 48));
            groupsGrid.Columns.Add(TextColumn("Ready", 48));
            groupsGrid.Columns.Add(TextColumn("Exceptions", 150));

            var outputColumn = new Forms.DataGridViewComboBoxColumn
            {
                DataSource = new[] { "Continuous", "Dash" },
                FillWeight = 84,
                FlatStyle = Forms.FlatStyle.Flat,
                HeaderText = "Output",
                Name = "Output"
            };
            groupsGrid.Columns.Add(outputColumn);
            groupsGrid.Columns.Add(TextColumn("Dash", 66));
            groupsGrid.Columns.Add(TextColumn("Gap", 66));
            groupsGrid.Columns.Add(new Forms.DataGridViewButtonColumn
            {
                FillWeight = 58,
                HeaderText = "DWG",
                Name = "Select",
                Text = "Select",
                UseColumnTextForButtonValue = true
            });
        }

        private static Forms.DataGridViewTextBoxColumn TextColumn(
            string name,
            float fillWeight)
        {
            return new Forms.DataGridViewTextBoxColumn
            {
                FillWeight = fillWeight,
                HeaderText = name,
                Name = name
            };
        }

        private void ExportProfile()
        {
            groupsGrid.CommitEdit(Forms.DataGridViewDataErrorContexts.Commit);
            groupsGrid.EndEdit();

            var profile = new BatchPreparationProfile
            {
                SchemaVersion = 1
            };
            var rulesByKey =
                new Dictionary<string, BatchPreparationProfileRule>(
                    StringComparer.OrdinalIgnoreCase);
            var invalidGroups = new List<string>();
            var conflictingGroups = new List<string>();
            var conflictingKeys = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (Forms.DataGridViewRow row in groupsGrid.Rows)
            {
                if (row.Tag is not BatchPreparationGroup group ||
                    group.EligibleIds.Count == 0)
                {
                    continue;
                }

                string outputMode = row.Cells[7].Value?.ToString() ?? string.Empty;
                bool dashed = string.Equals(
                    outputMode, "Dash", StringComparison.OrdinalIgnoreCase);
                if (!dashed &&
                    !string.Equals(
                        outputMode, "Continuous", StringComparison.OrdinalIgnoreCase))
                {
                    invalidGroups.Add(DescribeLayerWidth(group));
                    continue;
                }

                double? dashLength = null;
                double? gapLength = null;
                if (dashed)
                {
                    if (!TryParsePositive(row.Cells[8].Value, out double parsedDash) ||
                        !TryParsePositive(row.Cells[9].Value, out double parsedGap))
                    {
                        invalidGroups.Add(DescribeLayerWidth(group));
                        continue;
                    }

                    dashLength = parsedDash;
                    gapLength = parsedGap;
                }

                string key = CreateProfileRuleKey(group.LayerName, group.Width);
                if (rulesByKey.TryGetValue(
                        key, out BatchPreparationProfileRule existingRule))
                {
                    if (!HasSameOutput(existingRule, outputMode, dashLength, gapLength) &&
                        conflictingKeys.Add(key))
                    {
                        conflictingGroups.Add(DescribeLayerWidth(group));
                    }

                    continue;
                }

                rulesByKey.Add(key, new BatchPreparationProfileRule
                {
                    LayerName = group.LayerName,
                    SourceWidth = group.Width,
                    OutputMode = dashed ? "Dash" : "Continuous",
                    DashLength = dashLength,
                    GapLength = gapLength
                });
            }

            if (invalidGroups.Count > 0 || conflictingGroups.Count > 0)
            {
                var messages = new List<string>();
                if (invalidGroups.Count > 0)
                {
                    messages.Add(
                        "Set valid positive dash and gap values for these dashed " +
                        "groups, or choose Continuous:\n" +
                        FormatEntryList(invalidGroups));
                }

                if (conflictingGroups.Count > 0)
                {
                    messages.Add(
                        "Rows with the same layer and source width must use the " +
                        "same output settings:\n" +
                        FormatEntryList(conflictingGroups));
                }

                Forms.MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine + Environment.NewLine, messages),
                    "Profile cannot be exported",
                    Forms.MessageBoxButtons.OK,
                    Forms.MessageBoxIcon.Warning);
                return;
            }

            if (rulesByKey.Count == 0)
            {
                Forms.MessageBox.Show(
                    this,
                    "There are no processable groups to export.",
                    "Nothing to export",
                    Forms.MessageBoxButtons.OK,
                    Forms.MessageBoxIcon.Information);
                return;
            }

            profile.Rules = rulesByKey.Values
                .OrderBy(rule => rule.LayerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(rule => rule.SourceWidth)
                .ToList();

            using (var fileDialog = new Forms.SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = "xlsx",
                FileName = "BatchPreparationProfile.xlsx",
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                OverwritePrompt = true,
                Title = "Export Batch Preparation Profile"
            })
            {
                if (fileDialog.ShowDialog(this) != Forms.DialogResult.OK)
                {
                    return;
                }

                try
                {
                    BatchPreparationExcelProfile.Write(
                        fileDialog.FileName, profile);
                    Forms.MessageBox.Show(
                        this,
                        $"Exported {profile.Rules.Count} layer/width rule(s). " +
                        "Dash and gap values are stored as drawing-unit numbers " +
                        "and are used as-is when imported.",
                        "Profile exported",
                        Forms.MessageBoxButtons.OK,
                        Forms.MessageBoxIcon.Information);
                }
                catch (UnauthorizedAccessException exception)
                {
                    ShowProfileFileError("Unable to write the profile", exception);
                }
                catch (IOException exception)
                {
                    ShowProfileFileError("Unable to write the profile", exception);
                }
                catch (InvalidDataException exception)
                {
                    ShowProfileFileError("Unable to write the profile", exception);
                }
            }
        }

        private void ImportProfile()
        {
            groupsGrid.CommitEdit(Forms.DataGridViewDataErrorContexts.Commit);
            groupsGrid.EndEdit();

            using (var fileDialog = new Forms.OpenFileDialog
            {
                CheckFileExists = true,
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                Multiselect = false,
                Title = "Import Batch Preparation Profile"
            })
            {
                if (fileDialog.ShowDialog(this) != Forms.DialogResult.OK)
                {
                    return;
                }

                BatchPreparationProfile profile;
                try
                {
                    profile = BatchPreparationExcelProfile.Read(
                        fileDialog.FileName);
                    ValidateProfile(profile);
                }
                catch (UnauthorizedAccessException exception)
                {
                    ShowProfileFileError("Unable to read the profile", exception);
                    return;
                }
                catch (IOException exception)
                {
                    ShowProfileFileError("Unable to read the profile", exception);
                    return;
                }
                catch (InvalidDataException exception)
                {
                    ShowProfileFileError("The profile is invalid", exception);
                    return;
                }

                ApplyImportedProfile(profile);
            }
        }

        private void ApplyImportedProfile(BatchPreparationProfile profile)
        {
            var rulesByKey = profile.Rules.ToDictionary(
                rule => CreateProfileRuleKey(rule.LayerName, rule.SourceWidth),
                StringComparer.OrdinalIgnoreCase);
            var matchedRows = new List<Forms.DataGridViewRow>();
            var matchedRuleKeys = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var unmatchedCurrentKeys = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (Forms.DataGridViewRow row in groupsGrid.Rows)
            {
                if (row.Tag is not BatchPreparationGroup group ||
                    group.EligibleIds.Count == 0)
                {
                    continue;
                }

                string key = CreateProfileRuleKey(group.LayerName, group.Width);
                if (rulesByKey.ContainsKey(key))
                {
                    matchedRows.Add(row);
                    matchedRuleKeys.Add(key);
                }
                else
                {
                    unmatchedCurrentKeys.Add(key);
                }
            }

            List<BatchPreparationProfileRule> unmatchedProfileRules =
                profile.Rules.Where(rule =>
                    !matchedRuleKeys.Contains(
                        CreateProfileRuleKey(rule.LayerName, rule.SourceWidth)))
                    .ToList();
            string preview = BuildImportPreview(
                profile,
                matchedRows.Count,
                matchedRuleKeys,
                unmatchedProfileRules,
                unmatchedCurrentKeys);
            if (matchedRows.Count == 0)
            {
                Forms.MessageBox.Show(
                    this,
                    preview + Environment.NewLine + Environment.NewLine +
                    "No processable rows matched; nothing was changed.",
                    "No matching profile rules",
                    Forms.MessageBoxButtons.OK,
                    Forms.MessageBoxIcon.Information);
                return;
            }

            if (Forms.MessageBox.Show(
                    this,
                    preview + Environment.NewLine + Environment.NewLine +
                    "Apply the matching rules? Include selections and exception " +
                    "classifications will remain unchanged.",
                    "Preview Batch Preparation Profile",
                    Forms.MessageBoxButtons.YesNo,
                    Forms.MessageBoxIcon.Question) != Forms.DialogResult.Yes)
            {
                return;
            }

            foreach (Forms.DataGridViewRow row in matchedRows)
            {
                var group = (BatchPreparationGroup)row.Tag;
                BatchPreparationProfileRule rule = rulesByKey[
                    CreateProfileRuleKey(group.LayerName, group.Width)];
                bool dashed = string.Equals(
                    rule.OutputMode, "Dash", StringComparison.OrdinalIgnoreCase);

                group.Dashed = dashed;
                group.DashLength = rule.DashLength ?? 0;
                group.GapLength = rule.GapLength ?? 0;
                row.Cells[7].Value = dashed ? "Dash" : "Continuous";
                row.Cells[8].Value = dashed
                    ? rule.DashLength.Value.ToString(
                        "G", CultureInfo.InvariantCulture)
                    : string.Empty;
                row.Cells[9].Value = dashed
                    ? rule.GapLength.Value.ToString(
                        "G", CultureInfo.InvariantCulture)
                    : string.Empty;
                SetDashGapAvailability(
                    row, dashed, clearWhenContinuous: true);
            }

            Forms.MessageBox.Show(
                this,
                $"Applied {matchedRuleKeys.Count} profile rule(s) to " +
                $"{matchedRows.Count} processable row(s). Unmatched rows, " +
                "Include selections, and exception classifications were unchanged.",
                "Profile imported",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Information);
        }

        private static string BuildImportPreview(
            BatchPreparationProfile profile,
            int matchedRowCount,
            HashSet<string> matchedRuleKeys,
            List<BatchPreparationProfileRule> unmatchedProfileRules,
            HashSet<string> unmatchedCurrentKeys)
        {
            List<BatchPreparationProfileRule> matchedRules = profile.Rules
                .Where(rule => matchedRuleKeys.Contains(
                    CreateProfileRuleKey(rule.LayerName, rule.SourceWidth)))
                .ToList();
            var lines = new List<string>
            {
                $"Profile rules: {profile.Rules.Count}.",
                $"Matched rules: {matchedRuleKeys.Count}; " +
                    $"processable rows: {matchedRowCount}.",
                $"Unmatched profile rules: {unmatchedProfileRules.Count}.",
                $"Current layer/width groups without a rule: {unmatchedCurrentKeys.Count}."
            };

            if (matchedRules.Count > 0)
            {
                lines.Add(
                    "Rules to apply:\n" +
                    FormatEntryList(matchedRules.Select(
                        rule => DescribeLayerWidth(
                            rule.LayerName, rule.SourceWidth))));
            }

            if (unmatchedProfileRules.Count > 0)
            {
                lines.Add(
                    "Profile entries not found in this drawing:\n" +
                    FormatEntryList(unmatchedProfileRules.Select(
                        rule => DescribeLayerWidth(
                            rule.LayerName, rule.SourceWidth))));
            }

            if (unmatchedCurrentKeys.Count > 0)
            {
                lines.Add(
                    "Processable drawing groups left unchanged:\n" +
                    FormatEntryList(unmatchedCurrentKeys));
            }

            lines.Add(
                "Exception-only rows are not targeted. Dash and gap values are " +
                "stored as drawing-unit numbers and used as-is in this drawing.");
            return string.Join(Environment.NewLine, lines);
        }

        private static void ValidateProfile(BatchPreparationProfile profile)
        {
            if (profile.SchemaVersion != 1)
            {
                throw new InvalidDataException(
                    $"Unsupported profile schema version {profile.SchemaVersion}.");
            }

            if (profile.Rules == null || profile.Rules.Count == 0)
            {
                throw new InvalidDataException(
                    "The profile contains no layer/width rules.");
            }

            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BatchPreparationProfileRule rule in profile.Rules)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.LayerName))
                {
                    throw new InvalidDataException(
                        "Every profile rule must have a layer name.");
                }

                if (double.IsNaN(rule.SourceWidth) ||
                    double.IsInfinity(rule.SourceWidth) ||
                    rule.SourceWidth <= 0)
                {
                    throw new InvalidDataException(
                        $"Profile rule for layer '{rule.LayerName}' has an invalid width.");
                }

                bool continuous = string.Equals(
                    rule.OutputMode,
                    "Continuous",
                    StringComparison.OrdinalIgnoreCase);
                bool dashed = string.Equals(
                    rule.OutputMode,
                    "Dash",
                    StringComparison.OrdinalIgnoreCase);
                if (!continuous && !dashed)
                {
                    throw new InvalidDataException(
                        $"Profile rule for layer '{rule.LayerName}' has an unknown output mode.");
                }

                if (continuous &&
                    (rule.DashLength.HasValue || rule.GapLength.HasValue))
                {
                    throw new InvalidDataException(
                        $"Continuous profile rule for layer '{rule.LayerName}' " +
                        "must have null dash and gap values.");
                }

                if (dashed &&
                    (!TryParsePositive(rule.DashLength, out _) ||
                     !TryParsePositive(rule.GapLength, out _)))
                {
                    throw new InvalidDataException(
                        $"Dashed profile rule for layer '{rule.LayerName}' must " +
                        "have positive finite dash and gap values.");
                }

                string key = CreateProfileRuleKey(
                    rule.LayerName, rule.SourceWidth);
                if (!seenKeys.Add(key))
                {
                    throw new InvalidDataException(
                        $"The profile contains duplicate rules for " +
                        $"{DescribeLayerWidth(rule.LayerName, rule.SourceWidth)}.");
                }
            }
        }

        private static string CreateProfileRuleKey(string layerName, double width)
        {
            return string.Concat(
                layerName,
                "\u001f",
                width.ToString("R", CultureInfo.InvariantCulture));
        }

        private static bool HasSameOutput(
            BatchPreparationProfileRule rule,
            string outputMode,
            double? dashLength,
            double? gapLength)
        {
            if (!string.Equals(
                    rule.OutputMode, outputMode, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(
                    outputMode, "Continuous", StringComparison.OrdinalIgnoreCase) ||
                (rule.DashLength == dashLength && rule.GapLength == gapLength);
        }

        private static string DescribeLayerWidth(BatchPreparationGroup group)
        {
            return DescribeLayerWidth(group.LayerName, group.Width);
        }

        private static string DescribeLayerWidth(string layerName, double width)
        {
            return $"Layer '{layerName}' / width " +
                width.ToString("G", CultureInfo.InvariantCulture);
        }

        private static string FormatEntryList(IEnumerable<string> entries)
        {
            List<string> items = entries
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();
            const int maximumDisplayedEntries = 8;
            string display = string.Join(
                Environment.NewLine,
                items.Take(maximumDisplayedEntries).Select(item => $"- {item}"));
            if (items.Count > maximumDisplayedEntries)
            {
                display += Environment.NewLine +
                    $"- ... and {items.Count - maximumDisplayedEntries} more";
            }

            return display;
        }

        private void ShowProfileFileError(string title, Exception exception)
        {
            Forms.MessageBox.Show(
                this,
                exception.Message,
                title,
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
        }

        private void ApplySettings()
        {
            groupsGrid.EndEdit();
            bool anyIncluded = false;
            foreach (Forms.DataGridViewRow row in groupsGrid.Rows)
            {
                var group = row.Tag as BatchPreparationGroup;
                if (group == null)
                {
                    continue;
                }

                group.Included = Convert.ToBoolean(
                    row.Cells[0].Value ?? false, CultureInfo.InvariantCulture);
                anyIncluded |= group.Included && group.EligibleIds.Count > 0;
                group.Dashed = string.Equals(
                    row.Cells[7].Value?.ToString(),
                    "Dash",
                    StringComparison.OrdinalIgnoreCase);
                if (!group.Dashed)
                {
                    group.DashLength = 0;
                    group.GapLength = 0;
                    SetDashGapAvailability(row, dashed: false, clearWhenContinuous: true);
                    continue;
                }

                if (!group.Included || group.EligibleIds.Count == 0)
                {
                    continue;
                }

                if (!TryParsePositive(row.Cells[8].Value, out double dashLength) ||
                    !TryParsePositive(row.Cells[9].Value, out double gapLength))
                {
                    Forms.MessageBox.Show(
                        this,
                        $"Enter positive finite dash and gap values for layer " +
                        $"'{group.LayerName}' ({group.LinetypeName}).",
                        "Invalid dash settings",
                        Forms.MessageBoxButtons.OK,
                        Forms.MessageBoxIcon.Warning);
                    return;
                }

                group.DashLength = dashLength;
                group.GapLength = gapLength;
            }

            if (!anyIncluded)
            {
                Forms.MessageBox.Show(
                    this,
                    "Select at least one group with eligible polylines.",
                    "Nothing selected",
                    Forms.MessageBoxButtons.OK,
                    Forms.MessageBoxIcon.Information);
                return;
            }

            PrepareRequested?.Invoke();
        }

        public void CaptureSettings()
        {
            groupsGrid.EndEdit();
            foreach (Forms.DataGridViewRow row in groupsGrid.Rows)
            {
                if (row.Tag is not BatchPreparationGroup group)
                {
                    continue;
                }

                group.Included = Convert.ToBoolean(
                    row.Cells[0].Value ?? false, CultureInfo.InvariantCulture);
                group.Dashed = string.Equals(
                    row.Cells[7].Value?.ToString(),
                    "Dash",
                    StringComparison.OrdinalIgnoreCase);
                if (!group.Dashed)
                {
                    group.DashLength = 0;
                    group.GapLength = 0;
                    continue;
                }

                if (TryParsePositive(row.Cells[8].Value, out double dashLength))
                {
                    group.DashLength = dashLength;
                }

                if (TryParsePositive(row.Cells[9].Value, out double gapLength))
                {
                    group.GapLength = gapLength;
                }
            }
        }

        private void ApplyBulkSettings(
            string output,
            string dashText,
            string gapText)
        {
            groupsGrid.CommitEdit(Forms.DataGridViewDataErrorContexts.Commit);
            groupsGrid.EndEdit();

            List<Forms.DataGridViewRow> targetRows =
                groupsGrid.SelectedRows.Count > 0
                    ? groupsGrid.SelectedRows.Cast<Forms.DataGridViewRow>().ToList()
                    : groupsGrid.Rows
                        .Cast<Forms.DataGridViewRow>()
                        .Where(row => Convert.ToBoolean(row.Cells[0].Value ?? false))
                        .ToList();
            if (targetRows.Count == 0)
            {
                Forms.MessageBox.Show(
                    this,
                    "Select rows with Ctrl or Shift, or check their Include boxes.",
                    "No groups selected",
                    Forms.MessageBoxButtons.OK,
                    Forms.MessageBoxIcon.Information);
                return;
            }

            bool dashed = string.Equals(
                output, "Dash", StringComparison.OrdinalIgnoreCase);
            double dashLength = 0;
            double gapLength = 0;
            if (dashed &&
                (!TryParsePositive(dashText, out dashLength) ||
                 !TryParsePositive(gapText, out gapLength)))
            {
                Forms.MessageBox.Show(
                    this,
                    "Enter positive finite dash and gap values before applying " +
                    "dashed output to selected groups.",
                    "Invalid dash settings",
                    Forms.MessageBoxButtons.OK,
                    Forms.MessageBoxIcon.Warning);
                return;
            }

            foreach (Forms.DataGridViewRow row in targetRows)
            {
                row.Cells[0].Value = true;
                row.Cells[7].Value = dashed ? "Dash" : "Continuous";
                if (dashed)
                {
                    row.Cells[8].Value = dashLength.ToString(
                        "G", CultureInfo.InvariantCulture);
                    row.Cells[9].Value = gapLength.ToString(
                        "G", CultureInfo.InvariantCulture);
                }

                SetDashGapAvailability(
                    row, dashed, clearWhenContinuous: true);
            }
        }

        private static bool TryParsePositive(object value, out double result)
        {
            return double.TryParse(
                    value?.ToString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out result) &&
                !double.IsNaN(result) && !double.IsInfinity(result) && result > 0;
        }
    }

    internal sealed class BatchPreparationProfile
    {
        public BatchPreparationProfile()
        {
        }

        public int SchemaVersion { get; set; }
        public List<BatchPreparationProfileRule> Rules { get; set; } =
            new List<BatchPreparationProfileRule>();
    }

    internal sealed class BatchPreparationProfileRule
    {
        public BatchPreparationProfileRule()
        {
        }

        public string LayerName { get; set; } = string.Empty;
        public double SourceWidth { get; set; }
        public string OutputMode { get; set; } = string.Empty;
        public double? DashLength { get; set; }
        public double? GapLength { get; set; }
    }
}
