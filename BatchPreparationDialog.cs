using System;
using System.Collections.Generic;
using System.Globalization;
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
                    "Dash/gap use drawing units. Selected-group overlaps are checked " +
                    "before preparation."
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
                Size = new Drawing.Size(1088, 398)
            };

            AddColumns();
            foreach (BatchPreparationGroup group in groups)
            {
                int rowIndex = groupsGrid.Rows.Add(
                    group.EligibleIds.Count > 0,
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
                        : string.Empty);
                groupsGrid.Rows[rowIndex].Tag = group;
                groupsGrid.Rows[rowIndex].Cells[1].ReadOnly = true;
                groupsGrid.Rows[rowIndex].Cells[2].ReadOnly = true;
                groupsGrid.Rows[rowIndex].Cells[3].ReadOnly = true;
                groupsGrid.Rows[rowIndex].Cells[4].ReadOnly = true;
                groupsGrid.Rows[rowIndex].Cells[5].ReadOnly = true;
                groupsGrid.Rows[rowIndex].Cells[6].ReadOnly = true;
            }

            int total = groups.Sum(group => group.Count);
            int eligible = groups.Sum(group => group.EligibleIds.Count);
            int exceptionCount = groups.Sum(group => group.Exceptions.Count);
            var summary = new Forms.Label
            {
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left,
                AutoSize = true,
                Location = new Drawing.Point(14, 504),
                Text = $"{groups.Count} group(s) · {total} polyline(s) · " +
                    $"{eligible} ready · {exceptionCount} exception(s)"
            };
            var applyButton = new Forms.Button
            {
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                Location = new Drawing.Point(854, 515),
                Size = new Drawing.Size(118, 30),
                Text = "Prepare"
            };
            var cancelButton = new Forms.Button
            {
                Anchor = Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Right,
                DialogResult = Forms.DialogResult.Cancel,
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
            bulkApplyButton.Click += (_, _) => ApplyBulkSettings(
                bulkOutput.SelectedItem?.ToString(),
                bulkDash.Text,
                bulkGap.Text);
            applyButton.Click += (_, _) => ApplySettings();

            Controls.Add(instructions);
            Controls.Add(selectAllButton);
            Controls.Add(clearButton);
            Controls.Add(bulkLabel);
            Controls.Add(bulkOutput);
            Controls.Add(bulkDash);
            Controls.Add(bulkGap);
            Controls.Add(bulkApplyButton);
            Controls.Add(groupsGrid);
            Controls.Add(summary);
            Controls.Add(applyButton);
            Controls.Add(cancelButton);
            AcceptButton = applyButton;
            CancelButton = cancelButton;
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
                if (!group.Included || group.EligibleIds.Count == 0 || !group.Dashed)
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
                    DialogResult = Forms.DialogResult.None;
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
                DialogResult = Forms.DialogResult.None;
                return;
            }

            DialogResult = Forms.DialogResult.OK;
            Close();
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
}
