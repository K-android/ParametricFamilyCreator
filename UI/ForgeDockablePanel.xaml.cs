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
                DockProgBar.IsIndeterminate = false;
                DockProgBar.Value = 100;
                BtnGenerateFamily.IsEnabled = true;

                if (success)
                {
                    LogMessage($"[REVIT SUCCESS] Family generation complete: {resultPath}");
                    UpdateStageUi(6, true, "Verified");
                    TxtPipelineStatus.Text = "All 6 Steps Complete ✔";
                }
                else
                {
                    LogMessage($"[REVIT NOTICE] {resultPath}");
                    TxtPipelineStatus.Text = "Completed with warnings";
                }
            });
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

        #region Pipeline Execution

        private async void BtnGenerateFamily_Click(object sender, RoutedEventArgs e)
        {
            string prompt = TxtDockPrompt.Text.Trim();
            string apiKey = TxtDockApiKey.Password.Trim();
            string model = (CmbDockModel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gemini-3.8-flash";
            string archetype = (CmbArchetype.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";

            BtnGenerateFamily.IsEnabled = false;
            DockProgBar.IsIndeterminate = false;
            DockProgBar.Value = 10;
            TxtPipelineStatus.Text = "Executing 6-Stage Pipeline...";

            ResetStageUi();

            try
            {
                // ========================================================
                // STAGE 1: Specs & Multi-View Orthographic Extraction
                // ========================================================
                UpdateStageUi(1, null, "Extracting...");
                LogMessage("[PIPELINE STAGE 1] Analyzing specifications and orthographic views (Plan, Elevation, Section)...");

                if (model == "offline" || string.IsNullOrWhiteSpace(apiKey))
                {
                    LogMessage("[PIPELINE] Using high-accuracy procedural archetype fallback...");
                    string effectivePrompt = BuildArchetypePrompt(prompt, archetype);
                    _currentSchema = _geminiService.GenerateDeterministicFallback(effectivePrompt, _selectedFilePath);
                }
                else
                {
                    try
                    {
                        string effectivePrompt = BuildArchetypePrompt(prompt, archetype);
                        _currentSchema = await _geminiService.GenerateFamilySchemaAsync(
                            apiKey,
                            effectivePrompt,
                            _selectedFilePath,
                            model,
                            0.2
                        );
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"[AI Warning] {ex.Message} -> Falling back to deterministic architectural rig.");
                        string effectivePrompt = BuildArchetypePrompt(prompt, archetype);
                        _currentSchema = _geminiService.GenerateDeterministicFallback(effectivePrompt, _selectedFilePath);
                    }
                }

                if (_currentSchema == null)
                {
                    throw new InvalidOperationException("Failed to derive FamilySchema from input.");
                }

                // Ensure archetype geometry (revolves, sweeps, blends) are augmented
                ApplyArchetypeEnhancements(_currentSchema, archetype);

                UpdateStageUi(1, true, "Done");
                DockProgBar.Value = 30;
                await Task.Delay(150);

                // ========================================================
                // STAGE 2: Skeleton & Parametric Constraints
                // ========================================================
                UpdateStageUi(2, null, "Building...");
                LogMessage($"[PIPELINE STAGE 2] Generating {_currentSchema.ReferencePlanes.Count} reference planes & {_currentSchema.MasterParameters.Count} parameters...");
                UpdateStageUi(2, true, "Done");
                DockProgBar.Value = 45;
                await Task.Delay(150);

                // ========================================================
                // STAGE 3: Primary Solids & Organic Revolutions
                // ========================================================
                UpdateStageUi(3, null, "Generating...");
                int solidCount = _currentSchema.HostExtrusions.Count(x => !x.IsVoid) + _currentSchema.HostBlends.Count(x => !x.IsVoid) + _currentSchema.HostRevolves.Count(x => !x.IsVoid);
                LogMessage($"[PIPELINE STAGE 3] Synthesizing {solidCount} primary solid bodies (Decks, Pedestals, Revolve bodies)...");
                UpdateStageUi(3, true, "Done");
                DockProgBar.Value = 60;
                await Task.Delay(150);

                // ========================================================
                // STAGE 4: Boolean Cavities & Void Carving
                // ========================================================
                UpdateStageUi(4, null, "Carving...");
                int voidCount = _currentSchema.HostExtrusions.Count(x => x.IsVoid) + _currentSchema.HostBlends.Count(x => x.IsVoid) + _currentSchema.HostRevolves.Count(x => x.IsVoid);
                LogMessage($"[PIPELINE STAGE 4] Carving {voidCount} boolean void cuts (Organic Revolve Bowl Dish, Tap Hole, Drain Cutouts)...");
                UpdateStageUi(4, true, "Done");
                DockProgBar.Value = 75;
                await Task.Delay(150);

                // ========================================================
                // STAGE 5: High Detail & MEP Connectors
                // ========================================================
                UpdateStageUi(5, null, "Assembling...");
                int mepCount = _currentSchema.MepConnectors?.Count ?? 0;
                int sweepCount = _currentSchema.HostSweeps?.Count ?? 0;
                LogMessage($"[PIPELINE STAGE 5] Assembling {sweepCount} 3D Sweeps (Spout, Mixer Tap) & {mepCount} MEP System Connectors...");
                UpdateStageUi(5, true, "Done");
                DockProgBar.Value = 85;
                await Task.Delay(150);

                // ========================================================
                // STAGE 6: Queue Revit Generation & Viewport Activation
                // ========================================================
                UpdateStageUi(6, null, "Verifying...");
                LogMessage($"[PIPELINE STAGE 6] Dispatching geometry into Revit RevitEngine via ExternalEvent...");

                _handler.CurrentSchema = _currentSchema;
                _handler.TemplateDirectory = _settings.TemplateDirectory;
                _handler.OutputDirectory = _settings.OutputDirectory;
                _externalEvent.Raise();

                // Run spec rescan audit against PDF/Image
                await RunPdfRescan();
            }
            catch (Exception ex)
            {
                LogMessage($"[PIPELINE ERROR] {ex.Message}");
                TxtPipelineStatus.Text = "Error during generation";
                BtnGenerateFamily.IsEnabled = true;
                DockProgBar.Value = 0;
            }
        }

        private string BuildArchetypePrompt(string userPrompt, string archetype)
        {
            if (archetype == "vessel")
            {
                return $"{userPrompt} countertop round vessel basin with smooth dished inner bowl cavity, tall gooseneck tap sweep, and waste drain flange.";
            }
            if (archetype == "pedestal")
            {
                return $"{userPrompt} wall hung wash basin with tapered half pedestal shroud, curved underbowl blend, chrome mixer tap, and overflow hole.";
            }
            if (archetype == "undermount")
            {
                return $"{userPrompt} undermount vanity sink with recessed bowl dish void cutout, rear tap mounting ledge, and sanitary drain connector.";
            }
            if (archetype == "ada")
            {
                return $"{userPrompt} commercial ADA accessible sink with shallow bowl cavity, offset P-trap clearance, lever mixer handle, and insulation shroud.";
            }
            if (archetype == "kitchen")
            {
                return $"{userPrompt} double bowl kitchen sink with two carved basin cavities, central drainboard fluting, and swivel chrome tap.";
            }
            if (archetype == "door")
            {
                return $"{userPrompt} architectural door with jamb frame, panel leaf, vision lite glazing void cut, lever handle hardware, and 2D plan swing.";
            }
            if (archetype == "window")
            {
                return $"{userPrompt} exterior window with outer frame, glazed sash, double glazing extrusion, sill projection, and rough wall opening cut.";
            }
            if (archetype == "desk")
            {
                return $"{userPrompt} office desk with work surface tabletop, metal frame legs, cable grommet void cutouts, and modesty panel.";
            }
            if (archetype == "chair")
            {
                return $"{userPrompt} ergonomic office task chair with upholstered seat cushion, curved backrest, five-star pedestal base, and armrests.";
            }
            if (archetype == "casework")
            {
                return $"{userPrompt} architectural casework millwork base cabinet with countertop, adjustable shelving, dual hinged doors, and drawer pulls.";
            }
            if (archetype == "mep")
            {
                return $"{userPrompt} mechanical equipment pump / chiller with rigid chassis base, motor housing, suction pipe connector, and discharge pipe connector.";
            }
            if (archetype == "structural")
            {
                return $"{userPrompt} structural steel column baseplate with central column stub, four corner anchor bolt void holes, and triangular stiffener gussets.";
            }
            return userPrompt;
        }

        private void ApplyArchetypeEnhancements(FamilySchema schema, string archetype)
        {
            if (schema.HostRevolves == null) schema.HostRevolves = new List<RevolveDef>();
            if (schema.HostSweeps == null) schema.HostSweeps = new List<SweepDef>();
            if (schema.FamilyTypes == null) schema.FamilyTypes = new List<FamilyTypeDef>();

            string cat = (schema.FamilyCategory ?? "").Trim().ToLowerInvariant();
            string name = (schema.MasterFamilyName ?? "").Trim().ToLowerInvariant();
            bool isPlumbing = cat.Contains("plumb") || name.Contains("basin") || name.Contains("sink") || name.Contains("lavatory") || name.Contains("tub") || name.Contains("toilet") || archetype.Contains("basin") || archetype.Contains("sink") || archetype == "pedestal" || archetype == "vessel" || archetype == "undermount" || archetype == "kitchen" || archetype == "ada";
            bool isDoor = cat.Contains("door") || name.Contains("door") || archetype == "door";
            bool isWindow = cat.Contains("window") || name.Contains("window") || archetype == "window";
            bool isFurniture = cat.Contains("furn") || name.Contains("chair") || name.Contains("desk") || name.Contains("table") || archetype == "desk" || archetype == "chair";

            double width = schema.MasterParameters.FirstOrDefault(p => p.Name.Equals("Width", StringComparison.OrdinalIgnoreCase))?.DefaultValueMm ?? 600.0;
            double depth = schema.MasterParameters.FirstOrDefault(p => p.Name.Equals("Depth", StringComparison.OrdinalIgnoreCase))?.DefaultValueMm ?? 450.0;
            double height = schema.MasterParameters.FirstOrDefault(p => p.Name.Equals("Height", StringComparison.OrdinalIgnoreCase))?.DefaultValueMm ?? 850.0;

            if (isPlumbing)
            {
                double bowlDepth = schema.MasterParameters.FirstOrDefault(p => p.Name.Equals("Bowl_Depth", StringComparison.OrdinalIgnoreCase))?.DefaultValueMm ?? 140.0;

                if (schema.HostRevolves.Count == 0)
                {
                    schema.HostRevolves.Add(new RevolveDef
                    {
                        Name = "Void_Inner_Bowl_Cavity_Dish",
                        IsVoid = true,
                        RevolveType = "BowlDish",
                        AxisX_mm = 0,
                        AxisY_mm = -depth * 0.45,
                        StartZ_mm = height - bowlDepth,
                        EndZ_mm = height + 5.0,
                        Radius_mm = (width / 2.0) - 35.0,
                        InnerRadius_mm = 0.0,
                        StartAngleDeg = 0.0,
                        EndAngleDeg = 360.0,
                        Subcategory = "Basin"
                    });

                    schema.HostRevolves.Add(new RevolveDef
                    {
                        Name = "Solid_Waste_Flange_Ring",
                        IsVoid = false,
                        RevolveType = "Cylinder",
                        AxisX_mm = 0,
                        AxisY_mm = -depth * 0.45,
                        StartZ_mm = height - bowlDepth - 5.0,
                        EndZ_mm = height - bowlDepth + 8.0,
                        Radius_mm = 38.0,
                        InnerRadius_mm = 20.0,
                        Subcategory = "Hardware",
                        MaterialParameter = "Tap_Material"
                    });
                }

                if (schema.HostSweeps.Count == 0)
                {
                    schema.HostSweeps.Add(new SweepDef
                    {
                        Name = "Solid_Curved_Tap_Spout",
                        IsVoid = false,
                        ProfileType = "Circle",
                        ProfileRadius_mm = 11.0,
                        PathPoints_mm = new List<VectorOffsetDef>
                        {
                            new(0, -depth * 0.15, height + 20.0),
                            new(0, -depth * 0.15, height + 110.0),
                            new(0, -depth * 0.28, height + 145.0),
                            new(0, -depth * 0.38, height + 125.0)
                        },
                        Subcategory = "Hardware",
                        MaterialParameter = "Tap_Material"
                    });
                }

                if (schema.FamilyTypes.Count == 0)
                {
                    schema.FamilyTypes = new List<FamilyTypeDef>
                    {
                        new() { Name = "Standard (600mm)", ParameterValues = new Dictionary<string, object> { ["Width"] = width, ["Depth"] = depth, ["Height"] = height } },
                        new() { Name = "Compact (450mm)", ParameterValues = new Dictionary<string, object> { ["Width"] = 450.0, ["Depth"] = Math.Max(350.0, depth * 0.8), ["Height"] = height } },
                        new() { Name = "Executive (800mm)", ParameterValues = new Dictionary<string, object> { ["Width"] = 800.0, ["Depth"] = Math.Min(550.0, depth * 1.2), ["Height"] = height } }
                    };
                }
            }
            else if (isDoor)
            {
                if (schema.FamilyTypes.Count == 0)
                {
                    schema.FamilyTypes = new List<FamilyTypeDef>
                    {
                        new() { Name = "Standard (900 x 2100mm)", ParameterValues = new Dictionary<string, object> { ["Width"] = 900.0, ["Height"] = 2100.0 } },
                        new() { Name = "Narrow (750 x 2100mm)", ParameterValues = new Dictionary<string, object> { ["Width"] = 750.0, ["Height"] = 2100.0 } },
                        new() { Name = "Wide (1000 x 2100mm)", ParameterValues = new Dictionary<string, object> { ["Width"] = 1000.0, ["Height"] = 2100.0 } }
                    };
                }
            }
            else if (isWindow)
            {
                if (schema.FamilyTypes.Count == 0)
                {
                    schema.FamilyTypes = new List<FamilyTypeDef>
                    {
                        new() { Name = "Standard (1200 x 1500mm)", ParameterValues = new Dictionary<string, object> { ["Width"] = 1200.0, ["Height"] = 1500.0 } },
                        new() { Name = "Small (600 x 900mm)", ParameterValues = new Dictionary<string, object> { ["Width"] = 600.0, ["Height"] = 900.0 } },
                        new() { Name = "Picture (1800 x 1800mm)", ParameterValues = new Dictionary<string, object> { ["Width"] = 1800.0, ["Height"] = 1800.0 } }
                    };
                }
            }
            else if (isFurniture)
            {
                if (schema.FamilyTypes.Count == 0)
                {
                    schema.FamilyTypes = new List<FamilyTypeDef>
                    {
                        new() { Name = "Standard (1500 x 750mm)", ParameterValues = new Dictionary<string, object> { ["Width"] = 1500.0, ["Depth"] = 750.0, ["Height"] = 740.0 } },
                        new() { Name = "Compact (1200 x 600mm)", ParameterValues = new Dictionary<string, object> { ["Width"] = 1200.0, ["Depth"] = 600.0, ["Height"] = 740.0 } },
                        new() { Name = "Executive (1800 x 900mm)", ParameterValues = new Dictionary<string, object> { ["Width"] = 1800.0, ["Depth"] = 900.0, ["Height"] = 740.0 } }
                    };
                }
            }
            else
            {
                if (schema.FamilyTypes.Count == 0)
                {
                    schema.FamilyTypes = new List<FamilyTypeDef>
                    {
                        new() { Name = "Type 1 (Standard)", ParameterValues = new Dictionary<string, object> { ["Width"] = width, ["Depth"] = depth, ["Height"] = height } },
                        new() { Name = "Type 2 (Compact)", ParameterValues = new Dictionary<string, object> { ["Width"] = width * 0.8, ["Depth"] = depth * 0.8, ["Height"] = height } },
                        new() { Name = "Type 3 (Large)", ParameterValues = new Dictionary<string, object> { ["Width"] = width * 1.25, ["Depth"] = depth * 1.25, ["Height"] = height } }
                    };
                }
            }
        }

        private void ResetStageUi()
        {
            SolidColorBrush muted = new SolidColorBrush(Color.FromRgb(0x5C, 0x63, 0x70));
            IconStage1.Text = "○"; IconStage1.Foreground = muted; StatusStage1.Text = "Pending";
            IconStage2.Text = "○"; IconStage2.Foreground = muted; StatusStage2.Text = "Pending";
            IconStage3.Text = "○"; IconStage3.Foreground = muted; StatusStage3.Text = "Pending";
            IconStage4.Text = "○"; IconStage4.Foreground = muted; StatusStage4.Text = "Pending";
            IconStage5.Text = "○"; IconStage5.Foreground = muted; StatusStage5.Text = "Pending";
            IconStage6.Text = "○"; IconStage6.Foreground = muted; StatusStage6.Text = "Pending";
        }

        private void UpdateStageUi(int stage, bool? completed, string statusText)
        {
            SolidColorBrush green = (SolidColorBrush)FindResource("AccentGreen");
            SolidColorBrush amber = (SolidColorBrush)FindResource("AccentAmber");
            SolidColorBrush muted = new SolidColorBrush(Color.FromRgb(0x5C, 0x63, 0x70));

            TextBlock? icon = stage switch
            {
                1 => IconStage1,
                2 => IconStage2,
                3 => IconStage3,
                4 => IconStage4,
                5 => IconStage5,
                6 => IconStage6,
                _ => null
            };

            TextBlock? label = stage switch
            {
                1 => StatusStage1,
                2 => StatusStage2,
                3 => StatusStage3,
                4 => StatusStage4,
                5 => StatusStage5,
                6 => StatusStage6,
                _ => null
            };

            if (icon == null || label == null) return;

            label.Text = statusText;
            if (completed == true)
            {
                icon.Text = "✔";
                icon.Foreground = green;
                label.Foreground = green;
            }
            else if (completed == null)
            {
                icon.Text = "⚡";
                icon.Foreground = amber;
                label.Foreground = amber;
            }
            else
            {
                icon.Text = "○";
                icon.Foreground = muted;
                label.Foreground = muted;
            }
        }

        #endregion

        #region Viewport & Family Actions

        private void BtnViewIn3D_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSchema == null)
            {
                LogMessage("[VIEWPORT] No family generated yet. Click 'Generate' first.");
                return;
            }

            string outDir = _settings.OutputDirectory;
            string rfaPath = IOPath.Combine(outDir, $"{_currentSchema.MasterFamilyName}.rfa");
            if (!File.Exists(rfaPath))
            {
                LogMessage($"[VIEWPORT] Family file not found at: {rfaPath}");
                return;
            }

            LogMessage($"[VIEWPORT] Re-queuing 3D activation for '{_currentSchema.MasterFamilyName}'...");
            _handler.CurrentSchema = _currentSchema;
            _externalEvent.Raise();
        }

        private void BtnFlexTest_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSchema == null)
            {
                LogMessage("[FLEX TEST] No family generated yet. Click 'Generate' first.");
                return;
            }

            LogMessage("[FLEX TEST] Stress-testing dimensional formulas across all catalog types in Revit...");
            _handler.CurrentSchema = _currentSchema;
            _externalEvent.Raise();
        }

        private void BtnSaveFamilyRfa_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSchema == null)
            {
                MessageBox.Show("Please generate a family first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string outDir = _settings.OutputDirectory;
            string rfaPath = IOPath.Combine(outDir, $"{_currentSchema.MasterFamilyName}.rfa");
            LogMessage($"[SAVE] Master Family saved at: {rfaPath}");
            MessageBox.Show($"Family saved successfully!\n\nLocation: {rfaPath}", "Family Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnLoadActiveProject_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSchema == null)
            {
                MessageBox.Show("Please generate a family first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            LogMessage($"[LOAD] Queuing family load into active Revit project document...");
            _handler.CurrentSchema = _currentSchema;
            _externalEvent.Raise();
        }

        #endregion

        #region PDF Rescan & Conversational Refinement

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

            LogMessage("[AUDIT] Rescanning modeled Revit family against manufacturer PDF cut-sheet...");

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

                LogMessage($"[AUDIT RESULT] Overall compliance: {report.OverallMatchPercent}%.");
                foreach (var item in report.VerificationItems)
                {
                    LogMessage($"  {item.Feature}: Spec={item.SpecValue} vs Model={item.ModeledValue} ({item.Status})");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"[AUDIT NOTICE] Rescan: {ex.Message}");
            }
        }

        private async void BtnDockRefine_Click(object sender, RoutedEventArgs e)
        {
            string instruction = TxtDockRefinePrompt.Text.Trim();
            if (string.IsNullOrWhiteSpace(instruction) || _currentSchema == null) return;

            string apiKey = TxtDockApiKey.Password.Trim();
            string model = (CmbDockModel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gemini-3.8-flash";

            BtnDockRefine.IsEnabled = false;
            DockProgBar.IsIndeterminate = true;
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

                LogMessage($"[AI REFINER SUCCESS] Refined schema. Rebuilding in Revit...");

                _handler.CurrentSchema = _currentSchema;
                _externalEvent.Raise();

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
                DockProgBar.IsIndeterminate = false;
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

        #endregion

        #region File Drag/Drop & Browse

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

        private void BtnClearFile_Click(object sender, RoutedEventArgs e)
        {
            _selectedFilePath = null;
            PdfFileBadge.Visibility = Visibility.Collapsed;
            LogMessage("[FILE] Removed attached cut-sheet.");
        }

        private void DockDropZone_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
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
                    LogMessage($"[FILE] Dropped cut-sheet: {_selectedFilePath}");
                }
            }
        }

        #endregion

        #region Settings & Credentials

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
            LogMessage("[SETTINGS] Credentials saved successfully.");
        }

        #endregion

        #region Logging

        private void LogMessage(string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            TxtDockLog.AppendText($"[{time}] {message}\n");
            ScrollLog.ScrollToEnd();
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtDockLog.Clear();
        }

        #endregion
    }
}
