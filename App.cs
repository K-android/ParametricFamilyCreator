using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace ParametricFamilyForge
{
    /// <summary>
    /// Revit Add-in entry point. Creates custom Ribbon Tab, Ribbon Panel, and registers Conversational Copilot Dockable Pane.
    /// </summary>
    public class App : IExternalApplication
    {
        public const string TabName = "AI Family Forge";
        public const string PanelName = "Conversational BIM Copilot";

        public static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("D8F5C4A2-9E1B-4C6A-8A2F-5B3D7E9C1A0F"));
        public static ParametricFamilyForge.UI.ForgeDockablePanel? DockablePanelInstance { get; private set; }
        public static ExternalEvent? GlobalExternalEvent { get; private set; }
        public static ParametricFamilyForge.RevitEngine.FamilyGenerationHandler? GlobalHandler { get; private set; }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // 1. Register Native Revit Dockable Pane (Conversational AI Copilot Dock)
                try
                {
                    GlobalHandler = new ParametricFamilyForge.RevitEngine.FamilyGenerationHandler();
                    GlobalExternalEvent = ExternalEvent.Create(GlobalHandler);
                    DockablePanelInstance = new ParametricFamilyForge.UI.ForgeDockablePanel(GlobalExternalEvent, GlobalHandler);
                    application.RegisterDockablePane(PaneId, "🤖 AI Family Forge Copilot", DockablePanelInstance);
                }
                catch { }

                // 2. Create Ribbon Tab
                try
                {
                    application.CreateRibbonTab(TabName);
                }
                catch
                {
                    // Tab may already exist
                }

                // 3. Create Ribbon Panel
                RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);

                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                PushButtonData buttonData = new PushButtonData(
                    "cmdLaunchForge",
                    "AI Family\nCopilot",
                    assemblyPath,
                    "ParametricFamilyForge.Commands.LaunchForgeCommand"
                )
                {
                    ToolTip = "Conversational AI Revit Family Copilot — CREATE, INSPECT, MODIFY, REPAIR & VALIDATE families.",
                    LongDescription = "Opens the Conversational AI Revit Family Copilot Studio and Dockable Panel for iterative natural-language BIM modeling, live family inspection, atomic operation execution, and BRep verification."
                };

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
