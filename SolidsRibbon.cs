#nullable enable

using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using System.Windows.Input;

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
                ribbonAssembly, "Prepare\nMarkings", "MARKING_PREP"));
            GetCollection(panelSource, "Items").Add(CreateButton(
                ribbonAssembly, "Create\nSolids", "MARKING_SOLIDS"));
            GetCollection(panelSource, "Items").Add(CreateButton(
                ribbonAssembly, "Block\nSolids", "MARKING_BLOCK"));
            GetCollection(panelSource, "Items").Add(CreateButton(
                ribbonAssembly, "Assign\nBlock Layer", "MARKING_BLOCK_LAYER"));

            ribbonTabs.Add(tab);
            ribbonTab = tab;
            return true;
        }

        private static object CreateButton(
            Assembly ribbonAssembly,
            string text,
            string commandName)
        {
            object button = CreateRibbonObject(ribbonAssembly, "RibbonButton");
            SetProperty(button, "Text", text);
            SetProperty(button, "ShowText", true);
            SetProperty(button, "CommandParameter", $"_.{commandName} ");
            SetProperty(button, "CommandHandler", new RibbonCommandHandler());
            SetEnumProperty(button, "Size", "Large");
            return button;
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
            public bool CanExecute(object? parameter)
            {
                return true;
            }

            public void Execute(object? parameter)
            {
                if (parameter is not string command)
                {
                    throw new ArgumentException(
                        "A command name is required for a Solids ribbon button.",
                        nameof(parameter));
                }

                var document = Application.DocumentManager.MdiActiveDocument;
                if (document == null)
                {
                    Trace.TraceWarning(
                        $"Cannot run ribbon command '{command}' without an active drawing.");
                    return;
                }

                document.SendStringToExecute(command, true, false, true);
            }

            public event EventHandler? CanExecuteChanged
            {
                add { }
                remove { }
            }
        }
    }
}
