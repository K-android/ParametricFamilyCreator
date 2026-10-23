using System;
using System.IO;
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
                    LongDescription = "Input a sketch, cut-sheet PDF, or prompt to synthesize complete Revit .rfa families using Gemini Vision or deterministic AEC rules."
                };

                // Configure Ribbon Icons (32x32 for large button, 16x16 for small button)
                try
                {
                    string assemblyDir = Path.GetDirectoryName(assemblyPath) ?? "";
                    string icon32 = Path.Combine(assemblyDir, "Resources", "icon32.png");
                    string icon16 = Path.Combine(assemblyDir, "Resources", "icon16.png");

                    if (File.Exists(icon32))
                    {
                        buttonData.LargeImage = new BitmapImage(new Uri(icon32, UriKind.Absolute));
                    }
                    else
                    {
                        buttonData.LargeImage = new BitmapImage(new Uri("pack://application:,,,/ParametricFamilyForge;component/Resources/icon32.png", UriKind.Absolute));
                    }

                    if (File.Exists(icon16))
                    {
                        buttonData.Image = new BitmapImage(new Uri(icon16, UriKind.Absolute));
                    }
                    else
                    {
                        buttonData.Image = new BitmapImage(new Uri("pack://application:,,,/ParametricFamilyForge;component/Resources/icon16.png", UriKind.Absolute));
                    }
                }
                catch { }

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
