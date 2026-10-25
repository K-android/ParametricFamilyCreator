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
                // 1. Try to toggle native Revit Dockable Pane
                try
                {
                    DockablePane pane = commandData.Application.GetDockablePane(App.PaneId);
                    if (pane != null)
                    {
                        if (pane.IsShown())
                        {
                            pane.Hide();
                        }
                        else
                        {
                            pane.Show();
                        }
                        return Result.Succeeded;
                    }
                }
                catch
                {
                    // If dockable pane was not registered (e.g. running outside startup lifecycle), fall back to window
                }

                // 2. Fallback to modeless WPF Window if pane not available
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
