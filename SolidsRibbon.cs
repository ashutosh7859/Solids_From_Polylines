#nullable enable

using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace SolidsFromPolylines
{
    internal static class SolidsRibbon
    {
        private const string TabId = "SolidsFromPolylines.Solids";
        private static IList? ribbonTabs;
        private static object? ribbonTab;
        private static bool waitingForRibbon;

        public static void Initialize()
        {
            if (waitingForRibbon)
            {
                return;
            }

            waitingForRibbon = true;
            Application.Idle += OnIdle;
        }

        public static void Terminate()
        {
            if (waitingForRibbon)
            {
                Application.Idle -= OnIdle;
                waitingForRibbon = false;
            }

            if (ribbonTabs != null && ribbonTab != null)
            {
                ribbonTabs.Remove(ribbonTab);
                ribbonTabs = null;
                ribbonTab = null;
            }
        }

        private static void OnIdle(object? sender, EventArgs e)
        {
            try
            {
                if (!TryAddRibbonTab())
                {
                    return;
                }

                Application.Idle -= OnIdle;
                waitingForRibbon = false;
            }
            catch (Exception exception)
            {
                Application.Idle -= OnIdle;
                waitingForRibbon = false;
                string message =
                    $"\nUnable to create the Solids ribbon tab: {exception.Message}";
                var editor = Application.DocumentManager.MdiActiveDocument?.Editor;
                if (editor != null)
                {
                    editor.WriteMessage(message);
                }
                else
                {
                    Trace.TraceError(message);
                }
            }
        }

        private static bool TryAddRibbonTab()
        {
            Assembly ribbonAssembly = Assembly.Load("AdWindows");
            Type componentManager = GetRibbonType(ribbonAssembly, "ComponentManager");
            object? ribbon = componentManager.GetProperty("Ribbon")?.GetValue(null);
            if (ribbon == null)
            {
                return false;
            }

            ribbonTabs = GetCollection(ribbon, "Tabs");
            object tab = CreateRibbonObject(ribbonAssembly, "RibbonTab");
            SetProperty(tab, "Id", TabId);
            SetProperty(tab, "Title", "Solids");

            object panelSource = CreateRibbonObject(ribbonAssembly, "RibbonPanelSource");
            SetProperty(panelSource, "Title", "Marking Tools");

            object panel = CreateRibbonObject(ribbonAssembly, "RibbonPanel");
            SetProperty(panel, "Source", panelSource);

            GetCollection(tab, "Panels").Add(panel);
            GetCollection(panelSource, "Items").Add(CreateButton(
                ribbonAssembly, "Prepare\nMarkings", "MARKING_PREP",
                "01_prepare_markings",
                "Create closed marking boundaries from selected polylines."));
            GetCollection(panelSource, "Items").Add(CreateButton(
                ribbonAssembly, "Create\nSolids", "MARKING_SOLIDS",
                "02_create_solids",
                "Create 3D solids from marking boundaries on a selected surface."));
            GetCollection(panelSource, "Items").Add(CreateButton(
                ribbonAssembly, "Block\nSolids", "MARKING_BLOCK",
                "03_block_solids",
                "Combine model-space solids on the selected solid's layer into a block."));
            GetCollection(panelSource, "Items").Add(CreateButton(
                ribbonAssembly, "Assign\nBlock Layer", "MARKING_BLOCK_LAYER",
                "04_assign_block_layer",
                "Change the layer of selected block references and optionally their contents."));

            for (int index = ribbonTabs.Count - 1; index >= 0; index--)
            {
                object existingTab = ribbonTabs[index]!;
                if (string.Equals(
                    GetProperty(existingTab, "Id") as string,
                    TabId,
                    StringComparison.Ordinal))
                {
                    ribbonTabs.RemoveAt(index);
                }
            }

            ribbonTabs.Add(tab);
            ribbonTab = tab;
            return true;
        }

        private static object CreateButton(
            Assembly ribbonAssembly,
            string text,
            string commandName,
            string iconName,
            string toolTip)
        {
            object button = CreateRibbonObject(ribbonAssembly, "RibbonButton");
            SetProperty(button, "Text", text);
            SetProperty(button, "ShowText", true);
            TrySetProperty(button, "ToolTip", toolTip);
            TrySetIcon(button, "SmallImage", $"{iconName}_16.png");
            TrySetIcon(button, "LargeImage", $"{iconName}_32.png");
            string command = $"_.{commandName} ";
            SetProperty(button, "CommandParameter", command);
            SetProperty(button, "CommandHandler", new RibbonCommandHandler(command));
            SetEnumProperty(button, "Size", "Large");
            return button;
        }

        private static void TrySetProperty(
            object target,
            string propertyName,
            object value)
        {
            try
            {
                SetProperty(target, propertyName, value);
            }
            catch (Exception exception)
            {
                Report(
                    $"Unable to set ribbon property '{propertyName}': " +
                    exception.Message);
                Trace.TraceError(exception.ToString());
            }
        }

        private static void TrySetIcon(
            object button,
            string propertyName,
            string fileName)
        {
            try
            {
                SetProperty(button, propertyName, LoadIcon(fileName));
            }
            catch (Exception exception)
            {
                Report(
                    $"Unable to set ribbon icon '{fileName}' on " +
                    $"'{propertyName}': {exception.Message}");
                Trace.TraceError(exception.ToString());
            }
        }

        private static BitmapImage LoadIcon(string fileName)
        {
            string resourceName = $"SolidsFromPolylines.{fileName}";
            using Stream stream = typeof(SolidsRibbon).Assembly
                .GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded ribbon icon '{resourceName}' was not found.");

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }

        private static void Report(string message)
        {
            var editor = Application.DocumentManager.MdiActiveDocument?.Editor;
            if (editor != null)
            {
                editor.WriteMessage($"\n{message}");
            }
            else
            {
                Trace.TraceWarning(message);
            }
        }

        private static IList GetCollection(object target, string propertyName)
        {
            object? collection = GetProperty(target, propertyName);
            return collection as IList
                ?? throw new InvalidOperationException(
                    $"Ribbon property '{propertyName}' is not a mutable list.");
        }

        private static object CreateRibbonObject(Assembly assembly, string typeName)
        {
            Type type = GetRibbonType(assembly, typeName);
            return Activator.CreateInstance(type)
                ?? throw new InvalidOperationException(
                    $"Could not create ribbon type '{typeName}'.");
        }

        private static Type GetRibbonType(Assembly assembly, string typeName)
        {
            return assembly.GetType($"Autodesk.Windows.{typeName}", true)!;
        }

        private static object? GetProperty(object target, string propertyName)
        {
            PropertyInfo property = target.GetType().GetProperty(propertyName)
                ?? throw new InvalidOperationException(
                    $"Ribbon type '{target.GetType().Name}' has no '{propertyName}' property.");
            return property.GetValue(target);
        }

        private static void SetProperty(
            object target,
            string propertyName,
            object value)
        {
            PropertyInfo property = target.GetType().GetProperty(propertyName)
                ?? throw new InvalidOperationException(
                    $"Ribbon type '{target.GetType().Name}' has no '{propertyName}' property.");
            property.SetValue(target, value);
        }

        private static void SetEnumProperty(
            object target,
            string propertyName,
            string value)
        {
            PropertyInfo property = target.GetType().GetProperty(propertyName)
                ?? throw new InvalidOperationException(
                    $"Ribbon type '{target.GetType().Name}' has no '{propertyName}' property.");
            object enumValue = Enum.Parse(property.PropertyType, value);
            property.SetValue(target, enumValue);
        }

        private sealed class RibbonCommandHandler : ICommand
        {
            private readonly string command;

            public RibbonCommandHandler(string command)
            {
                this.command = command;
            }

            public bool CanExecute(object? parameter)
            {
                return true;
            }

            public void Execute(object? parameter)
            {
                var document = Application.DocumentManager.MdiActiveDocument;
                if (document == null)
                {
                    Trace.TraceWarning(
                        $"Cannot run ribbon command '{command}' without an active drawing.");
                    return;
                }

                try
                {
                    document.SendStringToExecute(command, true, false, true);
                }
                catch (Exception exception)
                {
                    document.Editor.WriteMessage(
                        $"\nUnable to start ribbon command '{command.Trim()}': " +
                        exception.Message);
                    Trace.TraceError(exception.ToString());
                }
            }

            public event EventHandler? CanExecuteChanged
            {
                add { }
                remove { }
            }
        }
    }
}
