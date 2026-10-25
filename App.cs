using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace ParametricFamilyForge
{
    /// <summary>
    /// Revit Add-in entry point. Creates custom Ribbon Tab, Ribbon Panel, and registers Dockable Pane.
    /// </summary>
    public class App : IExternalApplication
    {
        public const string TabName = "AI Family Forge";
        public const string PanelName = "Parametric Generation";

        public static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("D8F5C4A2-9E1B-4C6A-8A2F-5B3D7E9C1A0F"));
        public static ParametricFamilyForge.UI.ForgeDockablePanel? DockablePanelInstance { get; private set; }
        public static ExternalEvent? GlobalExternalEvent { get; private set; }
        public static ParametricFamilyForge.RevitEngine.FamilyGenerationHandler? GlobalHandler { get; private set; }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // 1. Register Native Revit Dockable Pane
                try
                {
                    GlobalHandler = new ParametricFamilyForge.RevitEngine.FamilyGenerationHandler();
                    GlobalExternalEvent = ExternalEvent.Create(GlobalHandler);
                    DockablePanelInstance = new ParametricFamilyForge.UI.ForgeDockablePanel(GlobalExternalEvent, GlobalHandler);
                    application.RegisterDockablePane(PaneId, "AI Family Forge (Step Modeler)", DockablePanelInstance);
                }
                catch { }

                // 2. Create Ribbon Tab
                try
                {
                    application.CreateRibbonTab(TabName);
                }
                catch
                {
                    // Tab may already exist if shared across tools
                }

                // 3. Create Ribbon Panel
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
                    ToolTip = "Step-by-step verified parametric family builder with boolean void cuts and AI spec rescan.",
                    LongDescription = "Opens the native dockable panel to create parametric families step-by-step: Specs -> Skeleton -> Solids -> Voids -> Details -> PDF Rescan Review."
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
