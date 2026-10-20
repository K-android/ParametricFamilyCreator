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
                // If window already open, bring to front
                if (_forgeWindow != null && _forgeWindow.IsLoaded)
                {
                    _forgeWindow.Activate();
                    return Result.Succeeded;
                }

                // Initialize Revit ExternalEventHandler and ExternalEvent bridge
                var handler = new FamilyGenerationHandler();
                ExternalEvent externalEvent = ExternalEvent.Create(handler);

                // Create and show modeless WPF window
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
