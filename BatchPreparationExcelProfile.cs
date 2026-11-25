#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace SolidsFromPolylines
{
    internal static class BatchPreparationExcelProfile
    {
        private static readonly XNamespace SpreadsheetNamespace =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace DocumentRelationshipsNamespace =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PackageRelationshipsNamespace =
            "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace ContentTypesNamespace =
            "http://schemas.openxmlformats.org/package/2006/content-types";

        private static readonly string[] Headers =
        {
            "Schema Version",
            "Layer",
            "Source Width",
            "Output",
            "Dash Length",
            "Gap Length"
        };

        public static void Write(string filePath, BatchPreparationProfile profile)
        {
            using var output = new FileStream(
                filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(
                output, ZipArchiveMode.Create);
            WriteXml(
                archive,
                "[Content_Types].xml",
                new XDocument(
                    new XElement(
                        ContentTypesNamespace + "Types",
                        new XElement(
                            ContentTypesNamespace + "Default",
                            new XAttribute("Extension", "rels"),
                            new XAttribute(
                                "ContentType",
                                "application/vnd.openxmlformats-package.relationships+xml")),
                        new XElement(
                            ContentTypesNamespace + "Default",
                            new XAttribute("Extension", "xml"),
                            new XAttribute("ContentType", "application/xml")),
                        new XElement(
                            ContentTypesNamespace + "Override",
                            new XAttribute("PartName", "/xl/workbook.xml"),
                            new XAttribute(
                                "ContentType",
                                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                        new XElement(
                            ContentTypesNamespace + "Override",
                            new XAttribute("PartName", "/xl/worksheets/sheet1.xml"),
                            new XAttribute(
                                "ContentType",
                                "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")))));
            WriteXml(
                archive,
                "_rels/.rels",
                new XDocument(
                    new XElement(
                        PackageRelationshipsNamespace + "Relationships",
                        new XElement(
                            PackageRelationshipsNamespace + "Relationship",
                            new XAttribute("Id", "rId1"),
                            new XAttribute(
                                "Type",
                                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                            new XAttribute("Target", "xl/workbook.xml")))));
            WriteXml(
                archive,
                "xl/workbook.xml",
                new XDocument(
                    new XElement(
                        SpreadsheetNamespace + "workbook",
                        new XAttribute(
                            XNamespace.Xmlns + "r",
                            DocumentRelationshipsNamespace),
                        new XElement(
                            SpreadsheetNamespace + "sheets",
                            new XElement(
                                SpreadsheetNamespace + "sheet",
                                new XAttribute("name", "Rules"),
                                new XAttribute("sheetId", "1"),
                                new XAttribute(
                                    DocumentRelationshipsNamespace + "id",
                                    "rId1"))))));
            WriteXml(
                archive,
                "xl/_rels/workbook.xml.rels",
                new XDocument(
                    new XElement(
                        PackageRelationshipsNamespace + "Relationships",
                        new XElement(
                            PackageRelationshipsNamespace + "Relationship",
                            new XAttribute("Id", "rId1"),
                            new XAttribute(
                                "Type",
                                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                            new XAttribute(
                                "Target",
                                "worksheets/sheet1.xml")))));

            var rows = new List<XElement>
            {
                CreateRow(1, Headers.Select(
                    (header, index) => CreateTextCell(index + 1, 1, header)))
            };
            for (int index = 0; index < profile.Rules.Count; index++)
            {
                BatchPreparationProfileRule rule = profile.Rules[index];
                int rowNumber = index + 2;
                rows.Add(CreateRow(
                    rowNumber,
                    new[]
                    {
                        CreateNumberCell(1, rowNumber, profile.SchemaVersion),
                        CreateTextCell(2, rowNumber, rule.LayerName),
                        CreateTextCell(
                            3,
                            rowNumber,
                            rule.SourceWidth.ToString(
                                "R", CultureInfo.InvariantCulture)),
                        CreateTextCell(4, rowNumber, rule.OutputMode),
                        CreateOptionalNumberCell(5, rowNumber, rule.DashLength),
                        CreateOptionalNumberCell(6, rowNumber, rule.GapLength)
                    }));
            }

            WriteXml(
                archive,
                "xl/worksheets/sheet1.xml",
                new XDocument(
                    new XElement(
                        SpreadsheetNamespace + "worksheet",
                        new XElement(
                            SpreadsheetNamespace + "sheetData",
                            rows))));
        }

        public static BatchPreparationProfile Read(string filePath)
        {
            try
            {
                using ZipArchive archive = ZipFile.OpenRead(filePath);
                XDocument workbook = ReadXml(archive, "xl/workbook.xml");
                XDocument relationships = ReadXml(
                    archive, "xl/_rels/workbook.xml.rels");
                XElement sheet = workbook
                    .Root?
                    .Element(SpreadsheetNamespace + "sheets")?
                    .Elements(SpreadsheetNamespace + "sheet")
                    .FirstOrDefault()
                    ?? throw new InvalidDataException(
                        "The workbook does not contain a worksheet.");
                string relationshipId = (string?)sheet.Attribute(
                    DocumentRelationshipsNamespace + "id")
                    ?? throw new InvalidDataException(
                        "The workbook worksheet relationship is missing.");
                XElement relationship = relationships
                    .Root?
                    .Elements(PackageRelationshipsNamespace + "Relationship")
                    .FirstOrDefault(element =>
                        string.Equals(
                            (string?)element.Attribute("Id"),
                            relationshipId,
                            StringComparison.Ordinal))
                    ?? throw new InvalidDataException(
                        "The workbook worksheet relationship is invalid.");
                string target = (string?)relationship.Attribute("Target")
                    ?? throw new InvalidDataException(
                        "The workbook worksheet target is missing.");
                string worksheetPath = ResolveWorksheetPath(target);
                XDocument worksheet = ReadXml(archive, worksheetPath);
                List<string> sharedStrings = ReadSharedStrings(archive);
                return ReadRules(worksheet, sharedStrings);
            }
            catch (XmlException exception)
            {
                throw new InvalidDataException(
                    "The Excel workbook contains invalid XML.", exception);
            }
        }

        private static BatchPreparationProfile ReadRules(
            XDocument worksheet,
            List<string> sharedStrings)
        {
            IEnumerable<XElement> rows = worksheet
                .Root?
                .Element(SpreadsheetNamespace + "sheetData")?
                .Elements(SpreadsheetNamespace + "row")
                ?? throw new InvalidDataException(
                    "The workbook worksheet does not contain data.");
            var enumerator = rows.GetEnumerator();
            if (!enumerator.MoveNext())
            {
                throw new InvalidDataException(
                    "The workbook worksheet is empty.");
            }

            Dictionary<int, string> headerCells = ReadRow(
                enumerator.Current, sharedStrings);
            var columns = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<int, string> cell in headerCells)
            {
                if (string.IsNullOrWhiteSpace(cell.Value))
                {
                    continue;
                }

                if (!columns.TryAdd(cell.Value.Trim(), cell.Key))
                {
                    throw new InvalidDataException(
                        $"The workbook has a duplicate column named '{cell.Value}'.");
                }
            }

            foreach (string header in Headers)
            {
                if (!columns.ContainsKey(header))
                {
                    throw new InvalidDataException(
                        $"The workbook is missing the '{header}' column.");
                }
            }

            var profile = new BatchPreparationProfile { SchemaVersion = 1 };
            while (enumerator.MoveNext())
            {
                Dictionary<int, string> cells = ReadRow(
                    enumerator.Current, sharedStrings);
                if (cells.Values.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                int versionColumn = columns["Schema Version"];
                string versionText = GetCell(cells, versionColumn);
                if (!int.TryParse(
                        versionText,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int version) ||
                    version != profile.SchemaVersion)
                {
                    throw new InvalidDataException(
                        $"The workbook has an unsupported schema version '{versionText}'.");
                }

                string layer = GetCell(cells, columns["Layer"]).Trim();
                if (!double.TryParse(
                        GetCell(cells, columns["Source Width"]),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double sourceWidth))
                {
                    throw new InvalidDataException(
                            $"The source width in worksheet row " +
                            $"{GetRowNumber(enumerator.Current)} is invalid.");
                }

                string outputMode = GetCell(cells, columns["Output"]).Trim();
                string dashText = GetCell(cells, columns["Dash Length"]).Trim();
                string gapText = GetCell(cells, columns["Gap Length"]).Trim();
                profile.Rules.Add(new BatchPreparationProfileRule
                {
                    LayerName = layer,
                    SourceWidth = sourceWidth,
                    OutputMode = outputMode,
                    DashLength = ParseOptionalNumber(dashText, "dash length"),
                    GapLength = ParseOptionalNumber(gapText, "gap length")
                });
            }

            return profile;
        }

        private static int GetRowNumber(XElement row)
        {
            return int.TryParse(
                    (string?)row.Attribute("r"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int rowNumber)
                ? rowNumber
                : 0;
        }

        private static double? ParseOptionalNumber(string value, string columnName)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            if (!double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double result))
            {
                throw new InvalidDataException(
                    $"The {columnName} value '{value}' is invalid.");
            }

            return result;
        }

        private static string GetCell(
            Dictionary<int, string> cells,
            int column)
        {
            return cells.TryGetValue(column, out string? value)
                ? value
                : string.Empty;
        }

        private static Dictionary<int, string> ReadRow(
            XElement row,
            List<string> sharedStrings)
        {
            var cells = new Dictionary<int, string>();
            foreach (XElement cell in row.Elements(SpreadsheetNamespace + "c"))
            {
                string? reference = (string?)cell.Attribute("r");
                if (reference == null)
                {
                    throw new InvalidDataException(
                        "A worksheet cell is missing its address.");
                }

                int column = GetColumnNumber(reference);
                string type = (string?)cell.Attribute("t") ?? string.Empty;
                string value;
                if (type == "inlineStr")
                {
                    value = string.Concat(
                        cell.Descendants(SpreadsheetNamespace + "t")
                            .Select(text => text.Value));
                }
                else
                {
                    string rawValue = cell.Element(SpreadsheetNamespace + "v")?.Value
                        ?? string.Empty;
                    if (type == "s")
                    {
                        if (!int.TryParse(
                                rawValue,
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out int sharedStringIndex) ||
                            sharedStringIndex < 0 ||
                            sharedStringIndex >= sharedStrings.Count)
                        {
                            throw new InvalidDataException(
                                "A worksheet cell refers to a missing shared string.");
                        }

                        value = sharedStrings[sharedStringIndex];
                    }
                    else
                    {
                        value = rawValue;
                    }
                }

                if (!cells.TryAdd(column, value))
                {
                    throw new InvalidDataException(
                        $"The worksheet contains duplicate cell '{reference}'.");
                }
            }

            return cells;
        }

        private static int GetColumnNumber(string reference)
        {
            int column = 0;
            foreach (char character in reference)
            {
                if (character < 'A' || character > 'Z')
                {
                    break;
                }

                column = checked((column * 26) + (character - 'A' + 1));
            }

            if (column == 0)
            {
                throw new InvalidDataException(
                    $"The worksheet cell address '{reference}' is invalid.");
            }

            return column;
        }

        private static List<string> ReadSharedStrings(ZipArchive archive)
        {
            ZipArchiveEntry? entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
            {
                return new List<string>();
            }

            using Stream stream = entry.Open();
            using XmlReader reader = XmlReader.Create(stream, SafeXmlSettings());
            XDocument document = XDocument.Load(reader);
            return document
                .Root?
                .Elements(SpreadsheetNamespace + "si")
                .Select(item => string.Concat(
                    item.Descendants(SpreadsheetNamespace + "t")
                        .Select(text => text.Value)))
                .ToList()
                ?? new List<string>();
        }

        private static string ResolveWorksheetPath(string target)
        {
            string normalized = target.Replace('\\', '/').TrimStart('/');
            if (!normalized.StartsWith("xl/", StringComparison.Ordinal))
            {
                normalized = "xl/" + normalized;
            }

            if (normalized.Split('/').Any(segment => segment == ".."))
            {
                throw new InvalidDataException(
                    "The workbook worksheet path is invalid.");
            }

            return normalized;
        }

        private static XDocument ReadXml(
            ZipArchive archive,
            string entryName)
        {
            ZipArchiveEntry entry = archive.GetEntry(entryName)
                ?? throw new InvalidDataException(
                    $"The workbook is missing '{entryName}'.");
            using Stream stream = entry.Open();
            using XmlReader reader = XmlReader.Create(stream, SafeXmlSettings());
            return XDocument.Load(reader);
        }

        private static XmlReaderSettings SafeXmlSettings()
        {
            return new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
        }

        private static void WriteXml(
            ZipArchive archive,
            string entryName,
            XDocument document)
        {
            ZipArchiveEntry entry = archive.CreateEntry(
                entryName, CompressionLevel.Optimal);
            using Stream stream = entry.Open();
            using XmlWriter writer = XmlWriter.Create(
                stream,
                new XmlWriterSettings
                {
                    Encoding = new System.Text.UTF8Encoding(false)
                });
            document.Save(writer);
        }

        private static XElement CreateRow(
            int rowNumber,
            IEnumerable<XElement> cells)
        {
            return new XElement(
                SpreadsheetNamespace + "row",
                new XAttribute("r", rowNumber),
                cells);
        }

        private static XElement CreateTextCell(
            int column,
            int row,
            string value)
        {
            return new XElement(
                SpreadsheetNamespace + "c",
                new XAttribute("r", GetCellReference(column, row)),
                new XAttribute("t", "inlineStr"),
                new XElement(
                    SpreadsheetNamespace + "is",
                    new XElement(
                        SpreadsheetNamespace + "t",
                        new XAttribute(XNamespace.Xml + "space", "preserve"),
                        value)));
        }

        private static XElement CreateNumberCell(
            int column,
            int row,
            double value)
        {
            return new XElement(
                SpreadsheetNamespace + "c",
                new XAttribute("r", GetCellReference(column, row)),
                new XAttribute("t", "n"),
                new XElement(
                    SpreadsheetNamespace + "v",
                    value.ToString("R", CultureInfo.InvariantCulture)));
        }

        private static XElement CreateNumberCell(
            int column,
            int row,
            int value)
        {
            return new XElement(
                SpreadsheetNamespace + "c",
                new XAttribute("r", GetCellReference(column, row)),
                new XAttribute("t", "n"),
                new XElement(
                    SpreadsheetNamespace + "v",
                    value.ToString(CultureInfo.InvariantCulture)));
        }

        private static XElement CreateOptionalNumberCell(
            int column,
            int row,
            double? value)
        {
            return value.HasValue
                ? CreateNumberCell(column, row, value.Value)
                : new XElement(
                    SpreadsheetNamespace + "c",
                    new XAttribute("r", GetCellReference(column, row)));
        }

        private static string GetCellReference(int column, int row)
        {
            string columnName = string.Empty;
            while (column > 0)
            {
                column--;
                columnName = ((char)('A' + (column % 26))).ToString() +
                    columnName;
                column /= 26;
            }

            return columnName + row.ToString(CultureInfo.InvariantCulture);
        }
    }
}
