using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using ParametricFamilyForge.Models;
using ParametricFamilyForge.RevitEngine;
using ParametricFamilyForge.Services;
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;
using Ellipse = System.Windows.Shapes.Ellipse;
using Polygon = System.Windows.Shapes.Polygon;
using IOPath = System.IO.Path;

namespace ParametricFamilyForge.UI
{
    public partial class ForgeDockablePanel : Page, IDockablePaneProvider
    {
        private readonly ExternalEvent _externalEvent;
        private readonly FamilyGenerationHandler _handler;
        private readonly GeminiVisionService _geminiService;
        private ForgeSettings _settings;
        private string? _selectedFilePath;
        private FamilySchema? _currentSchema;
        private int _currentStep = 1;

        public ForgeDockablePanel(ExternalEvent externalEvent, FamilyGenerationHandler handler)
        {
            InitializeComponent();
            _externalEvent = externalEvent;
            _handler = handler;
            _geminiService = new GeminiVisionService();

            _settings = SettingsService.LoadSettings();
            TxtDockApiKey.Password = _settings.ApiKey;

            _handler.OnLog = msg => Dispatcher.Invoke(() => LogMessage(msg));
            _handler.OnCompleted = (success, resultPath) => Dispatcher.Invoke(() =>
            {
                DockProgBar.Visibility = Visibility.Collapsed;
                if (success)
                {
                    LogMessage($"[REVIT SUCCESS] Step execution complete: {resultPath}");
                }
                else
                {
                    LogMessage($"[REVIT NOTICE] {resultPath}");
                }
            });

            SetStep(1);
        }

        #region IDockablePaneProvider Implementation

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right
            };
        }

        #endregion

        #region Step Navigation & UI State

        public void SetStep(int step)
        {
            _currentStep = Math.Max(1, Math.Min(6, step));

            // Toggle visibility of step views
            ViewStep1.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
            ViewStep2.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
            ViewStep3.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;
            ViewStep4.Visibility = _currentStep == 4 ? Visibility.Visible : Visibility.Collapsed;
            ViewStep5.Visibility = _currentStep == 5 ? Visibility.Visible : Visibility.Collapsed;
            ViewStep6.Visibility = _currentStep == 6 ? Visibility.Visible : Visibility.Collapsed;

            // Update Stepper buttons styling & labels
            UpdateStepperButtons();

            BtnPrevStep.IsEnabled = _currentStep > 1;
            BtnNextStep.Content = _currentStep == 6 ? "Finish ✔" : "Next Step ▶";

            string[] stepTitles =
            {
                "1 of 6: Specs Intake",
                "2 of 6: Skeleton & Reference Planes",
                "3 of 6: Primary Solids (Massing & Blends)",
                "4 of 6: Boolean Void Cavities & Carving",
                "5 of 6: High-Detail Features & Connectors",
                "6 of 6: AI Rescan & Verification Review"
            };
            TxtCurrentStepLabel.Text = $"Step {stepTitles[_currentStep - 1]}";

            // If entering Step 3 or Step 4, redraw mini previews
            if (_currentStep == 3) RedrawStep3Preview();
            if (_currentStep == 4) RedrawStep4Preview();
            if (_currentStep == 6 && GridVerification.Items.Count == 0) _ = RunPdfRescan();
        }

        private void UpdateStepperButtons()
        {
            var tabs = new[] { BtnStepTab1, BtnStepTab2, BtnStepTab3, BtnStepTab4, BtnStepTab5, BtnStepTab6 };
            for (int i = 0; i < tabs.Length; i++)
            {
                int s = i + 1;
                if (s == _currentStep)
                {
                    tabs[i].Background = new SolidColorBrush(Color.FromRgb(44, 49, 58));
                    tabs[i].BorderBrush = (SolidColorBrush)FindResource("AccentBlue");
                    tabs[i].BorderThickness = new Thickness(1);
                }
                else
                {
                    tabs[i].Background = new SolidColorBrush(Color.FromRgb(33, 37, 43));
                    tabs[i].BorderThickness = new Thickness(0);
                }
            }
        }

        private void StepTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int targetStep))
            {
                SetStep(targetStep);
            }
        }

        private void BtnPrevStep_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep > 1) SetStep(_currentStep - 1);
        }

        private void BtnNextStep_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep < 6)
            {
                SetStep(_currentStep + 1);
            }
            else
            {
                MessageBox.Show("Family generation and specification review complete! Click 'Save .rfa Family' or 'Load into Project'.", "Family Ready", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        #endregion

        #region Step 1: Specs Intake & Extraction

        private async void BtnSynthesizeSpecs_Click(object sender, RoutedEventArgs e)
        {
            string prompt = TxtDockPrompt.Text.Trim();
            string apiKey = TxtDockApiKey.Password.Trim();
            string model = (CmbDockModel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gemini-3.8-flash";

            BtnSynthesizeSpecs.IsEnabled = false;
            DockProgBar.Visibility = Visibility.Visible;
            LogMessage($"[Step 1] Extracting architectural specifications ({model})...");

            try
            {
                if (model == "offline" || string.IsNullOrWhiteSpace(apiKey))
                {
                    _currentSchema = _geminiService.GenerateDeterministicFallback(prompt, _selectedFilePath);
                    LogMessage("[Step 1] Extracted using offline AEC rule synthesis.");
                }
                else
                {
                    _currentSchema = await _geminiService.GenerateFamilySchemaAsync(
                        apiKey,
                        prompt,
                        _selectedFilePath,
                        model,
                        0.2
                    );
                    LogMessage($"[Step 1] AI synthesis complete with model '{_geminiService.LastUsedModel}'.");
                }

                PopulateExtractedSpecs();
                BadgeStep1.Text = "✔";
                BadgeStep1.Foreground = (SolidColorBrush)FindResource("AccentGreen");
                SetStep(2);
            }
            catch (Exception ex)
            {
                LogMessage($"[Step 1 ERROR] {ex.Message}");
                MessageBox.Show($"Failed to extract specs: {ex.Message}", "Extraction Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                BtnSynthesizeSpecs.IsEnabled = true;
                DockProgBar.Visibility = Visibility.Collapsed;
            }
        }

        private void PopulateExtractedSpecs()
        {
            if (_currentSchema == null) return;

            double w = _currentSchema.MasterParameters?.FirstOrDefault(p => p.Name.Equals("Width", StringComparison.OrdinalIgnoreCase))?.DefaultValueMm ?? 0;
            double d = _currentSchema.MasterParameters?.FirstOrDefault(p => p.Name.Equals("Depth", StringComparison.OrdinalIgnoreCase))?.DefaultValueMm ?? 0;
            double h = _currentSchema.MasterParameters?.FirstOrDefault(p => p.Name.Equals("Height", StringComparison.OrdinalIgnoreCase))?.DefaultValueMm ?? 0;

            int solidCount = (_currentSchema.HostExtrusions?.Count(e => !e.IsVoid) ?? 0) + (_currentSchema.HostBlends?.Count ?? 0);
            int voidCount = _currentSchema.HostExtrusions?.Count(e => e.IsVoid) ?? 0;

            TxtSpecSummary.Text = $"Family: {_currentSchema.MasterFamilyName}\n" +
                                  $"Category: {_currentSchema.FamilyCategory}\n" +
                                  $"Envelope: {w:0.#} × {d:0.#} × {h:0.#} mm\n" +
                                  $"Solids: {solidCount} | Voids: {voidCount} | Ports: {_currentSchema.MepConnectors?.Count ?? 0}";

            // Populate Step 2 Lists
            ListRefPlanes.Items.Clear();
            if (_currentSchema.ReferencePlanes != null)
            {
                foreach (var rp in _currentSchema.ReferencePlanes)
                    ListRefPlanes.Items.Add($"RefPlane: {rp.Name} (Axis={rp.Orientation}, Offset={rp.OffsetFromOriginMm:0.#}mm)");
            }

            ListParams.Items.Clear();
            if (_currentSchema.MasterParameters != null)
            {
                foreach (var p in _currentSchema.MasterParameters)
                    ListParams.Items.Add($"Param: {p.Name} = {p.DefaultValueMm:0.##} mm ({p.DataType})");
            }

            // Populate Step 3 List
            ListSolids.Items.Clear();
            if (_currentSchema.HostExtrusions != null)
            {
                foreach (var ext in _currentSchema.HostExtrusions.Where(e => !e.IsVoid))
                    ListSolids.Items.Add($"Solid Extrusion: {ext.Name} [{ext.StartZ_mm:0}..{ext.EndZ_mm:0}]mm");
            }
            if (_currentSchema.HostBlends != null)
            {
                foreach (var bl in _currentSchema.HostBlends)
                    ListSolids.Items.Add($"Tapered Blend: {bl.Name} [{bl.BottomOffset_mm:0}..{bl.TopOffset_mm:0}]mm");
            }

            // Populate Step 4 List
            ListVoids.Items.Clear();
            if (_currentSchema.HostExtrusions != null)
            {
                foreach (var v in _currentSchema.HostExtrusions.Where(e => e.IsVoid))
                    ListVoids.Items.Add($"✂️ Void Cavity: {v.Name} ({v.ProfileType}) [{v.StartZ_mm:0}..{v.EndZ_mm:0}]mm");
            }

            // Populate Step 5 List
            ListDetails.Items.Clear();
            if (_currentSchema.HostExtrusions != null)
            {
                foreach (var det in _currentSchema.HostExtrusions.Where(e => e.Subcategory?.Equals("Hardware", StringComparison.OrdinalIgnoreCase) == true))
                    ListDetails.Items.Add($"Hardware: {det.Name}");
            }
            if (_currentSchema.MepConnectors != null)
            {
                foreach (var conn in _currentSchema.MepConnectors)
                    ListDetails.Items.Add($"🔌 MEP Port: {conn.Name} (Ø{conn.Diameter_mm}mm {conn.Domain})");
            }
        }

        #endregion

        #region Step 2: Skeleton Execution

        private void BtnExecuteStep2_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSchema == null) return;
            LogMessage("[Step 2] Queuing Reference Planes and Parameters creation in Revit...");
            DockProgBar.Visibility = Visibility.Visible;

            _handler.CurrentSchema = _currentSchema;
            _handler.TemplateDirectory = _settings.TemplateDirectory;
            _handler.OutputDirectory = _settings.OutputDirectory;
            _externalEvent.Raise();

            BadgeStep2.Text = "✔";
            BadgeStep2.Foreground = (SolidColorBrush)FindResource("AccentGreen");
            SetStep(3);
        }

        #endregion

        #region Step 3: Primary Solids Execution

        private void BtnExecuteStep3_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSchema == null) return;
            LogMessage("[Step 3] Queuing Primary Solids (Rim Deck, Underbowl, Pedestal) in Revit...");
            DockProgBar.Visibility = Visibility.Visible;

            _handler.CurrentSchema = _currentSchema;
            _externalEvent.Raise();

            BadgeStep3.Text = "✔";
            BadgeStep3.Foreground = (SolidColorBrush)FindResource("AccentGreen");
            SetStep(4);
        }

        private void RedrawStep3Preview()
        {
            if (_currentSchema == null || CanvasStep3Preview == null) return;
            DrawPreviewToCanvas(CanvasStep3Preview, showVoids: false);
        }

        private void CanvasStep3Preview_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawStep3Preview();
        }

        #endregion

        #region Step 4: Boolean Void Cavities Execution

        private void BtnExecuteStep4_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSchema == null) return;
            LogMessage("[Step 4] Queuing Boolean Void Cuts pass in Revit engine...");
            DockProgBar.Visibility = Visibility.Visible;

            _handler.CurrentSchema = _currentSchema;
            _externalEvent.Raise();

            BadgeStep4.Text = "✔";
            BadgeStep4.Foreground = (SolidColorBrush)FindResource("AccentGreen");
            SetStep(5);
        }

        private void RedrawStep4Preview()
        {
            if (_currentSchema == null || CanvasStep4Preview == null) return;
            DrawPreviewToCanvas(CanvasStep4Preview, showVoids: true);
        }

        private void CanvasStep4Preview_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawStep4Preview();
        }

        #endregion

        #region Step 5: Detail & Connectors Execution

        private void BtnExecuteStep5_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSchema == null) return;
            LogMessage("[Step 5] Queuing Chrome Mixer Tap, Spout Arm, Nozzle & MEP Connectors...");
            DockProgBar.Visibility = Visibility.Visible;

            _handler.CurrentSchema = _currentSchema;
            _externalEvent.Raise();

            BadgeStep5.Text = "✔";
            BadgeStep5.Foreground = (SolidColorBrush)FindResource("AccentGreen");
            SetStep(6);
        }

        #endregion

        #region Step 6: AI Rescan & Conversational Review

        private async void BtnRescanPdf_Click(object sender, RoutedEventArgs e)
        {
            await RunPdfRescan();
        }

        private async Task RunPdfRescan()
        {
            if (_currentSchema == null) return;

            string apiKey = TxtDockApiKey.Password.Trim();
            string prompt = TxtDockPrompt.Text.Trim();
            string model = (CmbDockModel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gemini-3.8-flash";

            DockProgBar.Visibility = Visibility.Visible;
            LogMessage("[Step 6] Rescanning modeled Revit family against manufacturer PDF cut-sheet...");

            try
            {
                PdfRescanReport report = await _geminiService.RescanAndCompareAgainstPdfAsync(
                    apiKey,
                    _currentSchema,
                    _selectedFilePath,
                    prompt,
                    model
                );

                TxtMatchPercent.Text = $"{report.OverallMatchPercent}%";
                TxtRescanStatus.Text = report.StatusSummary;
                GridVerification.ItemsSource = report.VerificationItems;

                LogMessage($"[Step 6 AUDIT] Rescan complete! Overall compliance: {report.OverallMatchPercent}%.");
                foreach (var item in report.VerificationItems)
                {
                    LogMessage($"  {item.Feature}: Spec={item.SpecValue} vs Model={item.ModeledValue} ({item.Status})");
                }
                foreach (var obs in report.Observations)
                {
                    LogMessage($"  • {obs}");
                }

                BadgeStep6.Text = "✔";
                BadgeStep6.Foreground = (SolidColorBrush)FindResource("AccentGreen");
            }
            catch (Exception ex)
            {
                LogMessage($"[Step 6 ERROR] Rescan failed: {ex.Message}");
            }
            finally
            {
                DockProgBar.Visibility = Visibility.Collapsed;
            }
        }

        private async void BtnDockRefine_Click(object sender, RoutedEventArgs e)
        {
            string instruction = TxtDockRefinePrompt.Text.Trim();
            if (string.IsNullOrWhiteSpace(instruction) || _currentSchema == null) return;

            string apiKey = TxtDockApiKey.Password.Trim();
            string model = (CmbDockModel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gemini-3.8-flash";

            BtnDockRefine.IsEnabled = false;
            DockProgBar.Visibility = Visibility.Visible;
            LogMessage($"[AI REFINER] Applying refinement: \"{instruction}\"...");

            try
            {
                _currentSchema = await _geminiService.RefineFamilySchemaAsync(
                    apiKey,
                    _currentSchema,
                    instruction,
                    model,
                    0.2
                );

                PopulateExtractedSpecs();
                LogMessage($"[AI REFINER SUCCESS] Refined schema. Re-executing in Revit...");

                // Push refined schema to Revit
                _handler.CurrentSchema = _currentSchema;
                _externalEvent.Raise();

                // Re-run PDF rescan to update compliance table
                await RunPdfRescan();
                TxtDockRefinePrompt.Clear();
            }
            catch (Exception ex)
            {
                LogMessage($"[AI REFINER ERROR] {ex.Message}");
            }
            finally
            {
                BtnDockRefine.IsEnabled = true;
                DockProgBar.Visibility = Visibility.Collapsed;
            }
        }

        private void TxtDockRefinePrompt_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                BtnDockRefine_Click(sender, e);
            }
        }

        private void BtnChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null)
            {
                TxtDockRefinePrompt.Text = btn.Tag.ToString()!;
                BtnDockRefine_Click(sender, e);
            }
        }

        private void BtnSaveFamilyRfa_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSchema == null) return;
            string outDir = _settings.OutputDirectory;
            string rfaPath = IOPath.Combine(outDir, $"{_currentSchema.MasterFamilyName}.rfa");
            LogMessage($"[SAVE] Master Family saved at: {rfaPath}");
            MessageBox.Show($"Family saved successfully!\n\nLocation: {rfaPath}", "Family Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnLoadActiveProject_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSchema == null) return;
            LogMessage($"[LOAD] Queuing family load into active Revit project document...");
            _handler.CurrentSchema = _currentSchema;
            _externalEvent.Raise();
        }

        #endregion

        #region Continuous Execution & File Browsing

        private async void BtnRunAllSteps_Click(object sender, RoutedEventArgs e)
        {
            LogMessage("[BATCH EXECUTION] Initiating all 6 steps continuously...");
            if (_currentSchema == null)
            {
                BtnSynthesizeSpecs_Click(sender, e);
            }

            // Run through Steps 2, 3, 4, 5 in sequence
            SetStep(2);
            BtnExecuteStep2_Click(sender, e);
            await Task.Delay(400);

            SetStep(3);
            BtnExecuteStep3_Click(sender, e);
            await Task.Delay(400);

            SetStep(4);
            BtnExecuteStep4_Click(sender, e);
            await Task.Delay(400);

            SetStep(5);
            BtnExecuteStep5_Click(sender, e);
            await Task.Delay(400);

            SetStep(6);
            await RunPdfRescan();
        }

        private void BtnBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Architectural Cut-Sheets (*.pdf;*.png;*.jpg;*.webp)|*.pdf;*.png;*.jpg;*.jpeg;*.webp|All Files (*.*)|*.*",
                Title = "Select Cut-Sheet PDF or Component Image"
            };
            if (dlg.ShowDialog() == true)
            {
                _selectedFilePath = dlg.FileName;
                TxtPdfFileName.Text = $"📄 {IOPath.GetFileName(_selectedFilePath)}";
                PdfFileBadge.Visibility = Visibility.Visible;
                LogMessage($"[FILE] Selected cut-sheet: {_selectedFilePath}");
            }
        }

        private void DockDropZone_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    _selectedFilePath = files[0];
                    TxtPdfFileName.Text = $"📄 {IOPath.GetFileName(_selectedFilePath)}";
                    PdfFileBadge.Visibility = Visibility.Visible;
                    LogMessage($"[FILE DROP] Attached: {_selectedFilePath}");
                }
            }
        }

        private void DockDropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void CmbDockPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbDockPresets.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                string tag = item.Tag.ToString()!;
                if (!string.IsNullOrEmpty(tag))
                {
                    TxtDockPrompt.Text = tag switch
                    {
                        "basin" => "Modern wash basin with half pedestal, 600mm width, 450mm depth, 850mm height, curved under-bowl transition, hollow inner bowl, faucet tap hole, chrome mixer tap, and waste drain.",
                        "workstation" => "Modern ergonomic office workstation: 1600mm width, 800mm depth, 750mm height, with 4 nested metallic cylindrical legs and parametric linked dimensions.",
                        "commercial_door" => "Standard commercial flush door: 900mm width, 2100mm height, 44mm leaf thickness, vision lite cutout, metallic lever handle hardware.",
                        "casework_cabinet" => "Casework base cabinet: 600mm width, 600mm depth, 850mm height, toe-kick recess, countertop slab, nested metallic pull handle.",
                        _ => TxtDockPrompt.Text
                    };
                }
            }
        }

        private void BtnToggleSettings_Click(object sender, RoutedEventArgs e)
        {
            BorderSettings.Visibility = BorderSettings.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnCloseSettings_Click(object sender, RoutedEventArgs e)
        {
            BorderSettings.Visibility = Visibility.Collapsed;
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            _settings.ApiKey = TxtDockApiKey.Password.Trim();
            SettingsService.SaveSettings(_settings);
            BorderSettings.Visibility = Visibility.Collapsed;
            LogMessage("[CONFIG] API credentials saved successfully.");
        }

        private void BtnClearDockLogs_Click(object sender, RoutedEventArgs e)
        {
            TxtDockLogs.Clear();
        }

        private void LogMessage(string message)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss");
            TxtDockLogs.AppendText($"[{ts}] {message}\n");
            TxtDockLogs.ScrollToEnd();
        }

        #endregion

        #region Mini 2D Preview Drawing Helper

        private void DrawPreviewToCanvas(Canvas canvas, bool showVoids)
        {
            canvas.Children.Clear();
            double W = canvas.ActualWidth;
            double H = canvas.ActualHeight;
            if (W < 30 || H < 30 || _currentSchema == null) return;

            double width = _currentSchema.MasterParameters?.FirstOrDefault(p => p.Name.Equals("Width", StringComparison.OrdinalIgnoreCase))?.DefaultValueMm ?? 600.0;
            double height = _currentSchema.MasterParameters?.FirstOrDefault(p => p.Name.Equals("Height", StringComparison.OrdinalIgnoreCase))?.DefaultValueMm ?? 850.0;

            double scale = Math.Min((W - 20) / Math.Max(width, 50), (H - 20) / Math.Max(height, 50));
            double cx = W / 2.0;
            double groundY = H - 10.0;

            double ToPxX(double mx) => cx + mx * scale;
            double ToPxZ(double mz) => groundY - mz * scale;

            // Draw Ground Line
            canvas.Children.Add(new Line { X1 = 6, Y1 = groundY, X2 = W - 6, Y2 = groundY, Stroke = new SolidColorBrush(Color.FromRgb(152, 195, 121)), StrokeThickness = 1 });

            // 1. Draw Solid Extrusions
            if (_currentSchema.HostExtrusions != null)
            {
                foreach (var ext in _currentSchema.HostExtrusions.Where(e => !e.IsVoid))
                {
                    double px1 = ToPxX(ext.MinX_mm);
                    double px2 = ToPxX(ext.MaxX_mm);
                    double pz1 = ToPxZ(ext.StartZ_mm);
                    double pz2 = ToPxZ(ext.EndZ_mm);

                    double rx = Math.Min(px1, px2);
                    double rz = Math.Min(pz1, pz2);
                    double rw = Math.Max(Math.Abs(px2 - px1), 2.0);
                    double rh = Math.Max(Math.Abs(pz1 - pz2), 2.0);

                    Color c = ext.Subcategory == "Hardware" ? Color.FromRgb(209, 154, 102) : Color.FromRgb(97, 175, 239);
                    var rect = new Rectangle
                    {
                        Width = rw, Height = rh,
                        Fill = new SolidColorBrush(Color.FromArgb(60, c.R, c.G, c.B)),
                        Stroke = new SolidColorBrush(c),
                        StrokeThickness = 1.1
                    };
                    Canvas.SetLeft(rect, rx);
                    Canvas.SetTop(rect, rz);
                    canvas.Children.Add(rect);
                }
            }

            // 2. Draw Solid Blends (Tapered Pedestal / Underbowl)
            if (_currentSchema.HostBlends != null)
            {
                foreach (var bl in _currentSchema.HostBlends)
                {
                    double xBotLeft = ToPxX(bl.BottomMinX_mm);
                    double xBotRight = ToPxX(bl.BottomMaxX_mm);
                    double xTopLeft = ToPxX(bl.TopMinX_mm);
                    double xTopRight = ToPxX(bl.TopMaxX_mm);
                    double zBot = ToPxZ(bl.BottomOffset_mm);
                    double zTop = ToPxZ(bl.TopOffset_mm);

                    Color c = Color.FromRgb(86, 182, 194);
                    var poly = new Polygon
                    {
                        Points = new PointCollection
                        {
                            new Point(xBotLeft, zBot),
                            new Point(xTopLeft, zTop),
                            new Point(xTopRight, zTop),
                            new Point(xBotRight, zBot)
                        },
                        Fill = new SolidColorBrush(Color.FromArgb(60, c.R, c.G, c.B)),
                        Stroke = new SolidColorBrush(c),
                        StrokeThickness = 1.1
                    };
                    canvas.Children.Add(poly);
                }
            }

            // 3. Draw Void Cutouts (Step 4 only, rendered over solids as cavity cuts)
            if (showVoids && _currentSchema.HostExtrusions != null)
            {
                foreach (var v in _currentSchema.HostExtrusions.Where(e => e.IsVoid))
                {
                    double px1 = ToPxX(v.MinX_mm);
                    double px2 = ToPxX(v.MaxX_mm);
                    double pz1 = ToPxZ(v.StartZ_mm);
                    double pz2 = ToPxZ(v.EndZ_mm);

                    double rx = Math.Min(px1, px2);
                    double rz = Math.Min(pz1, pz2);
                    double rw = Math.Max(Math.Abs(px2 - px1), 2.0);
                    double rh = Math.Max(Math.Abs(pz1 - pz2), 2.0);

                    var voidRect = new Rectangle
                    {
                        Width = rw, Height = rh,
                        Fill = new SolidColorBrush(Color.FromArgb(240, 20, 23, 28)), // Cavity background cutout
                        Stroke = new SolidColorBrush(Color.FromRgb(224, 108, 117)), // Void red
                        StrokeThickness = 1.3,
                        StrokeDashArray = new DoubleCollection { 3, 2 }
                    };
                    Canvas.SetLeft(voidRect, rx);
                    Canvas.SetTop(voidRect, rz);
                    canvas.Children.Add(voidRect);
                }
            }
        }

        #endregion
    }
}
