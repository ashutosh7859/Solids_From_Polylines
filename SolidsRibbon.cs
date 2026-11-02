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
                "01_prepare_markings"));
            GetCollection(panelSource, "Items").Add(CreateButton(
                ribbonAssembly, "Create\nSolids", "MARKING_SOLIDS",
                "02_create_solids"));
            GetCollection(panelSource, "Items").Add(CreateButton(
                ribbonAssembly, "Block\nSolids", "MARKING_BLOCK",
                "03_block_solids"));
            GetCollection(panelSource, "Items").Add(CreateButton(
                ribbonAssembly, "Assign\nBlock Layer", "MARKING_BLOCK_LAYER",
                "04_assign_block_layer"));

            ribbonTabs.Add(tab);
            ribbonTab = tab;
            return true;
        }

        private static object CreateButton(
            Assembly ribbonAssembly,
            string text,
            string commandName,
            string iconName)
        {
            object button = CreateRibbonObject(ribbonAssembly, "RibbonButton");
            SetProperty(button, "Text", text);
            SetProperty(button, "ShowText", true);
            SetProperty(button, "SmallImage", LoadIcon($"{iconName}_16.png"));
            SetProperty(button, "LargeImage", LoadIcon($"{iconName}_32.png"));
            string command = $"_.{commandName} ";
            SetProperty(button, "CommandParameter", command);
            SetProperty(button, "CommandHandler", new RibbonCommandHandler(command));
            SetEnumProperty(button, "Size", "Large");
            return button;
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
