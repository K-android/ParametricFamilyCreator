using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParametricFamilyForge.RevitEngine;
using ParametricFamilyForge.UI;

namespace ParametricFamilyForge.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LaunchForgeCommand : IExternalCommand
    {
        private static ForgeWindow? _forgeWindow;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // 1. Ensure the Dockable Copilot Pane is visible if registered
                try
                {
                    DockablePane pane = commandData.Application.GetDockablePane(App.PaneId);
                    if (pane != null && !pane.IsShown())
                    {
                        pane.Show();
                    }
                }
                catch { }

                // 2. Open or activate the Full Dual-Pane Conversational AI Copilot Studio Window
                if (_forgeWindow != null && _forgeWindow.IsLoaded)
                {
                    _forgeWindow.Activate();
                    return Result.Succeeded;
                }

                var handler = App.GlobalHandler ?? new FamilyGenerationHandler();
                ExternalEvent externalEvent = App.GlobalExternalEvent ?? ExternalEvent.Create(handler);

                _forgeWindow = new ForgeWindow(externalEvent, handler);
                _forgeWindow.Closed += (s, e) => _forgeWindow = null;
                _forgeWindow.Show();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
