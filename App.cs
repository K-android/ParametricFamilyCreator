using System;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace ParametricFamilyForge
{
    /// <summary>
    /// Revit Add-in entry point. Creates custom Ribbon Tab and Ribbon Panel.
    /// </summary>
    public class App : IExternalApplication
    {
        public const string TabName = "AI Family Forge";
        public const string PanelName = "Parametric Generation";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // Create Ribbon Tab
                try
                {
                    application.CreateRibbonTab(TabName);
                }
                catch
                {
                    // Tab may already exist if shared across tools
                }

                // Create Ribbon Panel
                RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);

                // Add Push Button
                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                PushButtonData buttonData = new PushButtonData(
                    "cmdLaunchForge",
                    "Parametric\nFamily Forge",
                    assemblyPath,
                    "ParametricFamilyForge.Commands.LaunchForgeCommand"
                )
                {
                    ToolTip = "Generate AI-driven parametric Revit families with nested sub-components and parameter bindings.",
                    LongDescription = "Input a sketch or prompt to synthesize complete Revit .rfa families using Gemini Vision or deterministic AEC rules."
                };

                panel.AddItem(buttonData);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Family Forge Startup Error", ex.ToString());
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
