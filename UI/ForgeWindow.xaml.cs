using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using ParametricFamilyForge.Models;
using ParametricFamilyForge.RevitEngine;
using ParametricFamilyForge.Services;

namespace ParametricFamilyForge.UI
{
    public partial class ForgeWindow : Window
    {
        private readonly ExternalEvent _externalEvent;
        private readonly FamilyGenerationHandler _handler;
        private readonly GeminiVisionService _geminiService;
        private ForgeSettings _settings;
        private string? _selectedImagePath;
        private bool _isUpdatingPreview = false;

        public ForgeWindow(ExternalEvent externalEvent, FamilyGenerationHandler handler)
        {
            InitializeComponent();

            _externalEvent = externalEvent ?? throw new ArgumentNullException(nameof(externalEvent));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _geminiService = new GeminiVisionService();
            _geminiService.OnStatusUpdate = (status) => Dispatcher.Invoke(() =>
            {
                TxtAiStatus.Text = status;
                LogMessage(status);
            });

            // Set window icon
            try
            {
                string assemblyDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
                string iconPath = Path.Combine(assemblyDir, "Resources", "icon64.png");
                if (File.Exists(iconPath))
                {
                    this.Icon = new BitmapImage(new Uri(iconPath, UriKind.Absolute));
                }
            }
            catch { }

            // Load persistent settings
            _settings = SettingsService.LoadSettings();
            ApplySettingsToUi();

            // Wire up real-time Revit log callbacks to UI Dispatcher
            _handler.OnLog = (msg) => Dispatcher.Invoke(() => LogMessage(msg));
            _handler.OnCompleted = (success, resultMsg) => Dispatcher.Invoke(() =>
            {
                ProgBar.Visibility = Visibility.Collapsed;
                BtnGenerateRevitFamily.IsEnabled = true;
                if (success)
                {
                    MessageBox.Show(this, $"Family successfully created and saved!\n\nPath:\n{resultMsg}\n\n(If a Revit project was open, it was also loaded directly into the model)", 
                        "Family Forge - Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(this, $"Family generation failed:\n\n{resultMsg}", 
                        "Family Forge - Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });

            // Initialize default preview schema
            LoadDefaultPreviewSchema();
        }

        private void ApplySettingsToUi()
        {
            if (string.IsNullOrWhiteSpace(_settings.TemplateDirectory) || !Directory.Exists(_settings.TemplateDirectory))
            {
                _settings.TemplateDirectory = TemplateResolver.GetBestDefaultTemplateDirectory();
                SettingsService.SaveSettings(_settings);
            }

            TxtApiKey.Password = _settings.ApiKey;
            TxtTemplateDir.Text = _settings.TemplateDirectory;
            TxtOutputDir.Text = _settings.OutputDirectory;
            SliderTemperature.Value = _settings.Temperature;
            TxtTempValue.Text = _settings.Temperature.ToString("0.00");

            // Select matching model engine or set custom text
            bool matched = false;
            foreach (ComboBoxItem item in CmbModelEngine.Items)
            {
                if (string.Equals(item.Tag?.ToString(), _settings.ModelEngine, StringComparison.OrdinalIgnoreCase))
                {
                    CmbModelEngine.SelectedItem = item;
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                CmbModelEngine.Text = _settings.ModelEngine;
            }
        }

        private void SaveUiToSettings()
        {
            _settings.ApiKey = TxtApiKey.Password.Trim();
            _settings.TemplateDirectory = TxtTemplateDir.Text.Trim();
            _settings.OutputDirectory = TxtOutputDir.Text.Trim();
            _settings.Temperature = SliderTemperature.Value;

            if (CmbModelEngine.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag != null)
            {
                _settings.ModelEngine = selectedItem.Tag.ToString()!;
            }
            else if (!string.IsNullOrWhiteSpace(CmbModelEngine.Text))
            {
                _settings.ModelEngine = CmbModelEngine.Text.Trim();
            }

            SettingsService.SaveSettings(_settings);
        }

        private void BtnToggleSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SliderTemperature_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtTempValue != null)
            {
                TxtTempValue.Text = e.NewValue.ToString("0.00");
            }
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            SaveUiToSettings();
            MessageBox.Show(this, "Settings successfully saved to local configuration!", "Config Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            LogMessage("Configuration settings saved.");
        }

        private void LoadDefaultPreviewSchema()
        {
            var defaultSchema = PresetService.GetPresetSchema("workstation");
            UpdateJsonPreview(defaultSchema);
        }

        private void UpdateJsonPreview(FamilySchema schema)
        {
            _isUpdatingPreview = true;
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                TxtJsonPreview.Text = JsonSerializer.Serialize(schema, options);
                RunQuickValidation(schema);
                RedrawGeometryPreview(schema);
            }
            finally
            {
                _isUpdatingPreview = false;
            }
        }

        private void RunQuickValidation(FamilySchema? schema = null)
        {
            if (schema == null)
            {
                try
                {
                    string json = TxtJsonPreview.Text.Trim();
                    if (string.IsNullOrEmpty(json)) return;
                    schema = JsonSerializer.Deserialize<FamilySchema>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    TxtValidationStatus.Text = "❌ Invalid JSON syntax";
                    TxtValidationStatus.Foreground = new SolidColorBrush(Color.FromRgb(224, 108, 117));
                    TxtValidationBadge.Text = "Syntax Error";
                    TxtValidationBadge.Foreground = new SolidColorBrush(Color.FromRgb(224, 108, 117));
                    ValidationBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(224, 108, 117));
                    return;
                }
            }

            var report = SchemaValidator.Validate(schema);
            if (report.IsValid)
            {
                string warnSuffix = report.Warnings.Count > 0 ? $" ({report.Warnings.Count} warnings)" : "";
                TxtValidationStatus.Text = $"✅ Ready: {report.ParameterCount} Params, {report.ExtrusionCount} Extrusions, {report.NestedFamilyCount} Nested, {report.TypeCount} Types{warnSuffix}";
                TxtValidationStatus.Foreground = report.Warnings.Count > 0 ? new SolidColorBrush(Color.FromRgb(229, 192, 123)) : new SolidColorBrush(Color.FromRgb(152, 195, 121));
                TxtValidationBadge.Text = report.Warnings.Count > 0 ? "Validated (Warnings)" : "Valid Schema";
                TxtValidationBadge.Foreground = report.Warnings.Count > 0 ? new SolidColorBrush(Color.FromRgb(229, 192, 123)) : new SolidColorBrush(Color.FromRgb(152, 195, 121));
                ValidationBanner.BorderBrush = report.Warnings.Count > 0 ? new SolidColorBrush(Color.FromRgb(229, 192, 123)) : new SolidColorBrush(Color.FromRgb(152, 195, 121));
            }
            else
            {
                TxtValidationStatus.Text = $"❌ Errors detected: {string.Join(" | ", report.Errors)}";
                TxtValidationStatus.Foreground = new SolidColorBrush(Color.FromRgb(224, 108, 117));
                TxtValidationBadge.Text = $"{report.Errors.Count} Error(s)";
                TxtValidationBadge.Foreground = new SolidColorBrush(Color.FromRgb(224, 108, 117));
                ValidationBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(224, 108, 117));
            }

            RedrawGeometryPreview(schema);
        }

        private void TxtJsonPreview_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingPreview) return;
            RunQuickValidation();
        }

        private void LogMessage(string message)
        {
            TxtLogs.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            TxtLogs.ScrollToEnd();
        }

        private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
        {
            TxtLogs.Clear();
            LogMessage("Execution logs cleared.");
        }

        #region Presets and Schema Tools (Import, Export, Catalog)

        private void CmbPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbPresets.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                string key = item.Tag.ToString()!;
                if (!string.IsNullOrEmpty(key))
                {
                    var schema = PresetService.GetPresetSchema(key);
                    UpdateJsonPreview(schema);
                    LogMessage($"Loaded built-in architectural preset: '{schema.MasterFamilyName}' ({schema.HostExtrusions.Count} extrusions, {schema.FamilyTypes.Count} types).");
                    if (!string.IsNullOrEmpty(schema.Description))
                    {
                        TxtPrompt.Text = schema.Description;
                    }
                }
            }
        }

        private void BtnImportJson_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Import Family Schema JSON",
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*"
            };

            if (dlg.ShowDialog(this) == true)
            {
                try
                {
                    var schema = PresetService.ImportSchemaFromJson(dlg.FileName);
                    if (schema != null)
                    {
                        UpdateJsonPreview(schema);
                        LogMessage($"Successfully imported FamilySchema from '{dlg.FileName}'.");
                    }
                    else
                    {
                        MessageBox.Show(this, "The selected file is not a valid FamilySchema JSON file.", "Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Failed to import JSON: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnExportJson_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string jsonText = TxtJsonPreview.Text.Trim();
                var schema = JsonSerializer.Deserialize<FamilySchema>(jsonText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (schema == null) throw new InvalidOperationException("Current schema in editor is invalid.");

                var dlg = new SaveFileDialog
                {
                    Title = "Export Family Schema JSON",
                    Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                    FileName = $"{schema.MasterFamilyName}_Schema.json"
                };

                if (dlg.ShowDialog(this) == true)
                {
                    PresetService.ExportSchemaToJson(schema, dlg.FileName);
                    LogMessage($"Saved FamilySchema specification to '{dlg.FileName}'.");
                    MessageBox.Show(this, $"Family specification successfully saved to:\n{dlg.FileName}", "Schema Exported", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to export JSON: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string jsonText = TxtJsonPreview.Text.Trim();
                var schema = JsonSerializer.Deserialize<FamilySchema>(jsonText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (schema == null) throw new InvalidOperationException("Current schema in editor is invalid.");

                var dlg = new SaveFileDialog
                {
                    Title = "Export Revit Type Catalog (CSV Schedule)",
                    Filter = "CSV / Type Catalog Files (*.txt;*.csv)|*.txt;*.csv|All Files (*.*)|*.*",
                    FileName = $"{schema.MasterFamilyName}.txt"
                };

                if (dlg.ShowDialog(this) == true)
                {
                    PresetService.ExportCatalogToCsv(schema, dlg.FileName);
                    LogMessage($"Exported Revit Type Catalog schedule to '{dlg.FileName}'.");
                    MessageBox.Show(this, $"Revit Type Catalog successfully generated at:\n{dlg.FileName}\n\n(Place this file alongside the .rfa with the identical base name for Revit's automatic Type Catalog prompt)", "Catalog Exported", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to export Type Catalog: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnExportSharedParams_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string jsonText = TxtJsonPreview.Text.Trim();
                var schema = JsonSerializer.Deserialize<FamilySchema>(jsonText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (schema == null) throw new InvalidOperationException("Current schema in editor is invalid.");

                var dlg = new SaveFileDialog
                {
                    Title = "Export Autodesk Revit Shared Parameter File",
                    Filter = "Revit Shared Parameters (*.txt)|*.txt|All Files (*.*)|*.*",
                    FileName = $"{schema.MasterFamilyName}_SharedParameters.txt"
                };

                if (dlg.ShowDialog(this) == true)
                {
                    SharedParameterService.ExportSharedParameterFile(schema, dlg.FileName);
                    var records = SharedParameterService.ExtractSharedParameters(schema);
                    LogMessage($"Exported Revit Shared Parameter file with {records.Count} parameters to '{dlg.FileName}'.");
                    MessageBox.Show(this, 
                        $"Autodesk Revit Shared Parameter file successfully generated!\n\nParameters: {records.Count}\nPath:\n{dlg.FileName}\n\n(Import this file into Revit under Manage > Shared Parameters to link shared parameters to family and schedule fields)", 
                        "Shared Parameters Exported", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to export Shared Parameters: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnPreview3D_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string jsonText = TxtJsonPreview.Text.Trim();
                if (string.IsNullOrWhiteSpace(jsonText))
                {
                    MessageBox.Show(this, "JSON Schema is empty. Please synthesize or enter a schema first.", "Schema Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                using var testDoc = JsonDocument.Parse(jsonText);

                string assemblyDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
                string viewerTemplatePath = Path.Combine(assemblyDir, "Resources", "revit-family-viewer.html");
                string htmlContent;

                if (File.Exists(viewerTemplatePath))
                {
                    htmlContent = File.ReadAllText(viewerTemplatePath, System.Text.Encoding.UTF8);
                }
                else
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    string resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("revit-family-viewer.html", StringComparison.OrdinalIgnoreCase)) ?? "";
                    if (!string.IsNullOrEmpty(resName))
                    {
                        using var s = asm.GetManifestResourceStream(resName);
                        using var r = new StreamReader(s!);
                        htmlContent = r.ReadToEnd();
                    }
                    else
                    {
                        throw new FileNotFoundException("Could not locate 'revit-family-viewer.html' template in Resources.");
                    }
                }

                // Inject initial JSON specification into the Three.js viewer
                string injectedScript = $"window.INITIAL_FAMILY_DATA = {jsonText};";
                htmlContent = htmlContent.Replace("/* __FAMILY_JSON_INJECTION__ */", injectedScript);

                string tempHtmlPath = Path.Combine(Path.GetTempPath(), $"FamilyForge_3D_{DateTime.Now:yyyyMMdd_HHmmss}.html");
                File.WriteAllText(tempHtmlPath, htmlContent, System.Text.Encoding.UTF8);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tempHtmlPath,
                    UseShellExecute = true
                });

                LogMessage($"[3D Orbit] Launched Three.js 3D Interactive Web Viewer in browser: {Path.GetFileName(tempHtmlPath)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to launch 3D preview: {ex.Message}", "3D Viewer Error", MessageBoxButton.OK, MessageBoxImage.Error);
                LogMessage($"[3D Viewer Error] {ex.Message}");
            }
        }

        private void BtnValidate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string jsonText = TxtJsonPreview.Text.Trim();
                var schema = JsonSerializer.Deserialize<FamilySchema>(jsonText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var report = SchemaValidator.Validate(schema);

                RunQuickValidation(schema);
                RedrawGeometryPreview(schema);

                LogMessage("=============================================");
                LogMessage($"[Schema Validation] {report.SummaryText}");
                if (report.Errors.Count > 0)
                {
                    foreach (var err in report.Errors)
                    {
                        LogMessage($"  [ERROR] {err}");
                    }
                }
                if (report.Warnings.Count > 0)
                {
                    foreach (var warn in report.Warnings)
                    {
                        LogMessage($"  [WARN] {warn}");
                    }
                }
                LogMessage("=============================================");

                if (report.IsValid)
                {
                    string msg = $"Schema verification succeeded!\n\nParameters: {report.ParameterCount}\nExtrusions: {report.ExtrusionCount}\nNested Components: {report.NestedFamilyCount}\nFamily Types: {report.TypeCount}";
                    if (report.Warnings.Count > 0) msg += $"\n\nWarnings ({report.Warnings.Count}):\n- " + string.Join("\n- ", report.Warnings);
                    MessageBox.Show(this, msg, "Schema Verification Passed", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(this, $"Schema verification failed with {report.Errors.Count} error(s):\n\n- " + string.Join("\n- ", report.Errors), "Schema Verification Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Schema validation error: {ex.Message}", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Interactive 2D Geometry Preview (Plan & Elevation)

        private class ExtrusionBounds
        {
            public string Name { get; set; } = string.Empty;
            public string ProfileType { get; set; } = "Rectangle";
            public double MinX { get; set; }
            public double MaxX { get; set; }
            public double MinY { get; set; }
            public double MaxY { get; set; }
            public double MinZ { get; set; }
            public double MaxZ { get; set; }
            public double Radius { get; set; }
            public bool IsNested { get; set; }
        }

        private void RightTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source == RightTabControl)
            {
                Dispatcher.BeginInvoke(new Action(() => RedrawGeometryPreview()), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void CmbPreviewLayout_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ColPlan == null || ColElevation == null || CmbPreviewLayout == null) return;
            if (CmbPreviewLayout.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                string tag = item.Tag.ToString()!;
                switch (tag)
                {
                    case "plan":
                        ColPlan.Width = new GridLength(1, GridUnitType.Star);
                        ColSplitter.Width = new GridLength(0);
                        ColElevation.Width = new GridLength(0);
                        BorderPlan.Visibility = Visibility.Visible;
                        BorderElevation.Visibility = Visibility.Collapsed;
                        PreviewSplitter.Visibility = Visibility.Collapsed;
                        break;
                    case "elevation":
                        ColPlan.Width = new GridLength(0);
                        ColSplitter.Width = new GridLength(0);
                        ColElevation.Width = new GridLength(1, GridUnitType.Star);
                        BorderPlan.Visibility = Visibility.Collapsed;
                        BorderElevation.Visibility = Visibility.Visible;
                        PreviewSplitter.Visibility = Visibility.Collapsed;
                        break;
                    case "both":
                    default:
                        ColPlan.Width = new GridLength(1, GridUnitType.Star);
                        ColSplitter.Width = new GridLength(6);
                        ColElevation.Width = new GridLength(1, GridUnitType.Star);
                        BorderPlan.Visibility = Visibility.Visible;
                        BorderElevation.Visibility = Visibility.Visible;
                        PreviewSplitter.Visibility = Visibility.Visible;
                        break;
                }
                Dispatcher.BeginInvoke(new Action(() => RedrawGeometryPreview()), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void BtnRefreshPreview_Click(object sender, RoutedEventArgs e)
        {
            RedrawGeometryPreview();
            LogMessage("Redrew 2D geometry preview.");
        }

        private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 40 && e.NewSize.Height > 40)
            {
                RedrawGeometryPreview();
            }
        }

        private void RedrawGeometryPreview(FamilySchema? schema = null)
        {
            if (CanvasPlan == null || CanvasElevation == null) return;

            if (schema == null)
            {
                try
                {
                    string json = TxtJsonPreview?.Text?.Trim() ?? "";
                    if (string.IsNullOrEmpty(json)) return;
                    schema = JsonSerializer.Deserialize<FamilySchema>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    return;
                }
            }

            if (schema == null) return;

            var boxes = ExtractExtrusions(schema);
            if (boxes.Count == 0)
            {
                CanvasPlan.Children.Clear();
                CanvasElevation.Children.Clear();
                if (TxtPreviewDimensions != null) TxtPreviewDimensions.Text = "No geometric extrusions found in schema.";
                return;
            }

            double minX = boxes.Min(b => b.MinX);
            double maxX = boxes.Max(b => b.MaxX);
            double minY = boxes.Min(b => b.MinY);
            double maxY = boxes.Max(b => b.MaxY);
            double minZ = boxes.Min(b => b.MinZ);
            double maxZ = boxes.Max(b => b.MaxZ);

            double totalWidth = Math.Max(maxX - minX, 1.0);
            double totalDepth = Math.Max(maxY - minY, 1.0);
            double totalHeight = Math.Max(maxZ - minZ, 1.0);

            int hostCount = boxes.Count(b => !b.IsNested);
            int nestedCount = boxes.Count(b => b.IsNested);

            if (TxtPreviewDimensions != null)
            {
                TxtPreviewDimensions.Text = $"Bounding: {totalWidth:0.#} × {totalDepth:0.#} × {totalHeight:0.#} mm | {hostCount} Host, {nestedCount} Nested Components";
            }

            DrawPlanView(boxes, minX, maxX, minY, maxY, totalWidth, totalDepth);
            DrawElevationView(boxes, minX, maxX, minZ, maxZ, totalWidth, totalHeight);
        }

        private List<ExtrusionBounds> ExtractExtrusions(FamilySchema schema)
        {
            var list = new List<ExtrusionBounds>();
            if (schema.HostExtrusions != null)
            {
                foreach (var ext in schema.HostExtrusions)
                {
                    var b = new ExtrusionBounds
                    {
                        Name = ext.Name,
                        ProfileType = ext.ProfileType,
                        IsNested = false,
                        MinZ = Math.Min(ext.StartZ_mm, ext.EndZ_mm),
                        MaxZ = Math.Max(ext.StartZ_mm, ext.EndZ_mm)
                    };

                    if (string.Equals(ext.ProfileType, "Cylinder", StringComparison.OrdinalIgnoreCase) && ext.Radius_mm > 0)
                    {
                        double r = ext.Radius_mm;
                        double cx = (ext.MinX_mm + ext.MaxX_mm) / 2.0;
                        double cy = (ext.MinY_mm + ext.MaxY_mm) / 2.0;
                        b.MinX = cx - r;
                        b.MaxX = cx + r;
                        b.MinY = cy - r;
                        b.MaxY = cy + r;
                        b.Radius = r;
                    }
                    else
                    {
                        b.MinX = Math.Min(ext.MinX_mm, ext.MaxX_mm);
                        b.MaxX = Math.Max(ext.MinX_mm, ext.MaxX_mm);
                        b.MinY = Math.Min(ext.MinY_mm, ext.MaxY_mm);
                        b.MaxY = Math.Max(ext.MinY_mm, ext.MaxY_mm);
                    }
                    list.Add(b);
                }
            }

            if (schema.NestedFamilies != null)
            {
                foreach (var nf in schema.NestedFamilies)
                {
                    double offX = nf.PlacementOffsetMm?.X ?? 0.0;
                    double offY = nf.PlacementOffsetMm?.Y ?? 0.0;
                    double offZ = nf.PlacementOffsetMm?.Z ?? 0.0;

                    if (nf.SubExtrusions != null)
                    {
                        foreach (var sub in nf.SubExtrusions)
                        {
                            var b = new ExtrusionBounds
                            {
                                Name = string.IsNullOrEmpty(sub.Name) ? nf.NestedFamilyName : $"{nf.NestedFamilyName}: {sub.Name}",
                                ProfileType = sub.ProfileType,
                                IsNested = true,
                                MinZ = offZ + Math.Min(sub.StartZ_mm, sub.EndZ_mm),
                                MaxZ = offZ + Math.Max(sub.StartZ_mm, sub.EndZ_mm)
                            };

                            if (string.Equals(sub.ProfileType, "Cylinder", StringComparison.OrdinalIgnoreCase) && sub.Radius_mm > 0)
                            {
                                double r = sub.Radius_mm;
                                double cx = offX + (sub.MinX_mm + sub.MaxX_mm) / 2.0;
                                double cy = offY + (sub.MinY_mm + sub.MaxY_mm) / 2.0;
                                b.MinX = cx - r;
                                b.MaxX = cx + r;
                                b.MinY = cy - r;
                                b.MaxY = cy + r;
                                b.Radius = r;
                            }
                            else
                            {
                                b.MinX = offX + Math.Min(sub.MinX_mm, sub.MaxX_mm);
                                b.MaxX = offX + Math.Max(sub.MinX_mm, sub.MaxX_mm);
                                b.MinY = offY + Math.Min(sub.MinY_mm, sub.MaxY_mm);
                                b.MaxY = offY + Math.Max(sub.MinY_mm, sub.MaxY_mm);
                            }
                            list.Add(b);
                        }
                    }
                }
            }

            if (schema.HostBlends != null)
            {
                foreach (var bl in schema.HostBlends)
                {
                    double minX = Math.Min(Math.Min(bl.BottomMinX_mm, bl.BottomMaxX_mm), Math.Min(bl.TopMinX_mm, bl.TopMaxX_mm));
                    double maxX = Math.Max(Math.Max(bl.BottomMinX_mm, bl.BottomMaxX_mm), Math.Max(bl.TopMinX_mm, bl.TopMaxX_mm));
                    double minY = Math.Min(Math.Min(bl.BottomMinY_mm, bl.BottomMaxY_mm), Math.Min(bl.TopMinY_mm, bl.TopMaxY_mm));
                    double maxY = Math.Max(Math.Max(bl.BottomMinY_mm, bl.BottomMaxY_mm), Math.Max(bl.TopMinY_mm, bl.TopMaxY_mm));
                    list.Add(new ExtrusionBounds
                    {
                        Name = bl.Name,
                        ProfileType = bl.TopProfileType,
                        IsNested = false,
                        MinX = minX, MaxX = maxX,
                        MinY = minY, MaxY = maxY,
                        MinZ = Math.Min(bl.BottomOffset_mm, bl.TopOffset_mm),
                        MaxZ = Math.Max(bl.BottomOffset_mm, bl.TopOffset_mm)
                    });
                }
            }

            if (schema.HostSweeps != null)
            {
                foreach (var sw in schema.HostSweeps)
                {
                    if (sw.PathPoints_mm != null && sw.PathPoints_mm.Count > 0)
                    {
                        double pad = sw.ProfileRadius_mm > 0 ? sw.ProfileRadius_mm : Math.Max(sw.ProfileWidth_mm, sw.ProfileHeight_mm) / 2.0;
                        list.Add(new ExtrusionBounds
                        {
                            Name = sw.Name,
                            ProfileType = sw.ProfileType,
                            IsNested = false,
                            MinX = sw.PathPoints_mm.Min(p => p.X) - pad,
                            MaxX = sw.PathPoints_mm.Max(p => p.X) + pad,
                            MinY = sw.PathPoints_mm.Min(p => p.Y) - pad,
                            MaxY = sw.PathPoints_mm.Max(p => p.Y) + pad,
                            MinZ = sw.PathPoints_mm.Min(p => p.Z) - pad,
                            MaxZ = sw.PathPoints_mm.Max(p => p.Z) + pad
                        });
                    }
                }
            }

            return list;
        }

        private void DrawPlanView(List<ExtrusionBounds> boxes, double minX, double maxX, double minY, double maxY, double totalWidth, double totalDepth)
        {
            CanvasPlan.Children.Clear();
            double W = CanvasPlan.ActualWidth;
            double H = CanvasPlan.ActualHeight;
            if (W < 40 || H < 40) return;

            double margin = 48.0;
            double availW = Math.Max(W - (margin * 2), 20.0);
            double availH = Math.Max(H - (margin * 2), 20.0);

            double spanX = Math.Max(totalWidth, 50.0);
            double spanY = Math.Max(totalDepth, 50.0);

            double scale = Math.Min(availW / spanX, availH / spanY);
            if (scale <= 0) scale = 0.1;

            double modelCenterX = (minX + maxX) / 2.0;
            double modelCenterY = (minY + maxY) / 2.0;
            double canvasCenterX = W / 2.0;
            double canvasCenterY = H / 2.0;

            double ToPxX(double mx) => canvasCenterX + (mx - modelCenterX) * scale;
            double ToPxY(double my) => canvasCenterY - (my - modelCenterY) * scale;

            // Reference centerlines
            double clX = ToPxX(0.0);
            if (clX >= 0 && clX <= W)
            {
                CanvasPlan.Children.Add(new Line
                {
                    X1 = clX, Y1 = 6, X2 = clX, Y2 = H - 6,
                    Stroke = new SolidColorBrush(Color.FromArgb(90, 97, 175, 239)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 }
                });
            }

            double clY = ToPxY(0.0);
            if (clY >= 0 && clY <= H)
            {
                CanvasPlan.Children.Add(new Line
                {
                    X1 = 6, Y1 = clY, X2 = W - 6, Y2 = clY,
                    Stroke = new SolidColorBrush(Color.FromArgb(90, 97, 175, 239)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 }
                });
            }

            // Extrusions
            foreach (var b in boxes)
            {
                double x1 = ToPxX(b.MinX);
                double x2 = ToPxX(b.MaxX);
                double y1 = ToPxY(b.MinY); // larger pixel Y (lower)
                double y2 = ToPxY(b.MaxY); // smaller pixel Y (higher)

                double rx = Math.Min(x1, x2);
                double ry = Math.Min(y1, y2);
                double rw = Math.Max(Math.Abs(x2 - x1), 2.0);
                double rh = Math.Max(Math.Abs(y1 - y2), 2.0);

                Color mainColor = b.IsNested ? Color.FromRgb(152, 195, 121) : Color.FromRgb(97, 175, 239);
                Color fillColor = Color.FromArgb(65, mainColor.R, mainColor.G, mainColor.B);

                if (string.Equals(b.ProfileType, "Cylinder", StringComparison.OrdinalIgnoreCase))
                {
                    var ellipse = new Ellipse
                    {
                        Width = rw, Height = rh,
                        Fill = new SolidColorBrush(fillColor),
                        Stroke = new SolidColorBrush(mainColor),
                        StrokeThickness = 1.2
                    };
                    Canvas.SetLeft(ellipse, rx);
                    Canvas.SetTop(ellipse, ry);
                    CanvasPlan.Children.Add(ellipse);
                }
                else
                {
                    var rect = new Rectangle
                    {
                        Width = rw, Height = rh,
                        Fill = new SolidColorBrush(fillColor),
                        Stroke = new SolidColorBrush(mainColor),
                        StrokeThickness = 1.2,
                        RadiusX = 2, RadiusY = 2
                    };
                    Canvas.SetLeft(rect, rx);
                    Canvas.SetTop(rect, ry);
                    CanvasPlan.Children.Add(rect);
                }

                if (rw > 50 && rh > 18)
                {
                    var txt = new TextBlock
                    {
                        Text = b.Name,
                        FontSize = 9.5,
                        Foreground = new SolidColorBrush(Color.FromRgb(210, 215, 225)),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = rw - 4,
                        TextAlignment = TextAlignment.Center
                    };
                    Canvas.SetLeft(txt, rx + 2);
                    Canvas.SetTop(txt, ry + (rh / 2.0) - 7);
                    CanvasPlan.Children.Add(txt);
                }
            }

            // Dimensions
            double bottomY = ToPxY(minY);
            double dimY = Math.Min(bottomY + 22.0, H - 14.0);
            DrawHorizontalDimension(CanvasPlan, ToPxX(minX), ToPxX(maxX), dimY, bottomY, $"Width: {totalWidth:0.#} mm");

            double rightX = ToPxX(maxX);
            double dimX = Math.Min(rightX + 22.0, W - 14.0);
            DrawVerticalDimension(CanvasPlan, ToPxY(minY), ToPxY(maxY), dimX, rightX, $"Depth: {totalDepth:0.#} mm");

            // Title badge
            var title = new TextBlock
            {
                Text = $"📐 Plan View (Top-Down X-Y)\nFootprint: {totalWidth:0.#} × {totalDepth:0.#} mm",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(97, 175, 239)),
                Margin = new Thickness(6, 6, 0, 0)
            };
            Canvas.SetLeft(title, 6);
            Canvas.SetTop(title, 4);
            CanvasPlan.Children.Add(title);
        }

        private void DrawElevationView(List<ExtrusionBounds> boxes, double minX, double maxX, double minZ, double maxZ, double totalWidth, double totalHeight)
        {
            CanvasElevation.Children.Clear();
            double W = CanvasElevation.ActualWidth;
            double H = CanvasElevation.ActualHeight;
            if (W < 40 || H < 40) return;

            double margin = 48.0;
            double availW = Math.Max(W - (margin * 2), 20.0);
            double availH = Math.Max(H - (margin * 2), 20.0);

            double spanX = Math.Max(totalWidth, 50.0);
            double spanZ = Math.Max(totalHeight, 50.0);

            double scale = Math.Min(availW / spanX, availH / spanZ);
            if (scale <= 0) scale = 0.1;

            double modelCenterX = (minX + maxX) / 2.0;
            double modelCenterZ = (minZ + maxZ) / 2.0;
            double canvasCenterX = W / 2.0;
            double canvasCenterY = H / 2.0;

            double ToPxX(double mx) => canvasCenterX + (mx - modelCenterX) * scale;
            double ToPxZ(double mz) => canvasCenterY - (mz - modelCenterZ) * scale;

            // Reference Level (Z=0)
            double groundZ = ToPxZ(0.0);
            if (groundZ >= 0 && groundZ <= H)
            {
                CanvasElevation.Children.Add(new Line
                {
                    X1 = 6, Y1 = groundZ, X2 = W - 6, Y2 = groundZ,
                    Stroke = new SolidColorBrush(Color.FromArgb(160, 152, 195, 121)),
                    StrokeThickness = 1.2
                });

                var lvlLabel = new TextBlock
                {
                    Text = "Ref Level (Z=0.0)",
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(152, 195, 121))
                };
                Canvas.SetLeft(lvlLabel, 8);
                Canvas.SetTop(lvlLabel, groundZ - 13);
                CanvasElevation.Children.Add(lvlLabel);
            }

            // Vertical Centerline (X=0)
            double clX = ToPxX(0.0);
            if (clX >= 0 && clX <= W)
            {
                CanvasElevation.Children.Add(new Line
                {
                    X1 = clX, Y1 = 6, X2 = clX, Y2 = H - 6,
                    Stroke = new SolidColorBrush(Color.FromArgb(90, 97, 175, 239)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 }
                });
            }

            // Extrusions
            foreach (var b in boxes)
            {
                double x1 = ToPxX(b.MinX);
                double x2 = ToPxX(b.MaxX);
                double z1 = ToPxZ(b.MinZ); // bottom
                double z2 = ToPxZ(b.MaxZ); // top

                double rx = Math.Min(x1, x2);
                double rz = Math.Min(z1, z2);
                double rw = Math.Max(Math.Abs(x2 - x1), 2.0);
                double rh = Math.Max(Math.Abs(z1 - z2), 2.0);

                Color mainColor = b.IsNested ? Color.FromRgb(152, 195, 121) : Color.FromRgb(97, 175, 239);
                Color fillColor = Color.FromArgb(65, mainColor.R, mainColor.G, mainColor.B);

                var rect = new Rectangle
                {
                    Width = rw, Height = rh,
                    Fill = new SolidColorBrush(fillColor),
                    Stroke = new SolidColorBrush(mainColor),
                    StrokeThickness = 1.2,
                    RadiusX = 1, RadiusY = 1
                };
                Canvas.SetLeft(rect, rx);
                Canvas.SetTop(rect, rz);
                CanvasElevation.Children.Add(rect);

                if (rw > 50 && rh > 18)
                {
                    var txt = new TextBlock
                    {
                        Text = b.Name,
                        FontSize = 9.5,
                        Foreground = new SolidColorBrush(Color.FromRgb(210, 215, 225)),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = rw - 4,
                        TextAlignment = TextAlignment.Center
                    };
                    Canvas.SetLeft(txt, rx + 2);
                    Canvas.SetTop(txt, rz + (rh / 2.0) - 7);
                    CanvasElevation.Children.Add(txt);
                }
            }

            // Dimensions
            double bottomZ = ToPxZ(minZ);
            double dimZ = Math.Min(bottomZ + 22.0, H - 14.0);
            DrawHorizontalDimension(CanvasElevation, ToPxX(minX), ToPxX(maxX), dimZ, bottomZ, $"Width: {totalWidth:0.#} mm");

            double rightX = ToPxX(maxX);
            double dimX = Math.Min(rightX + 22.0, W - 14.0);
            DrawVerticalDimension(CanvasElevation, ToPxZ(minZ), ToPxZ(maxZ), dimX, rightX, $"Height: {totalHeight:0.#} mm");

            // Title badge
            var title = new TextBlock
            {
                Text = $"📐 Front Elevation View (X-Z)\nHeight: {totalHeight:0.#} mm",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(97, 175, 239)),
                Margin = new Thickness(6, 6, 0, 0)
            };
            Canvas.SetLeft(title, 6);
            Canvas.SetTop(title, 4);
            CanvasElevation.Children.Add(title);
        }

        private void DrawHorizontalDimension(Canvas canvas, double x1, double x2, double y, double extYTop, string label)
        {
            double minPx = Math.Min(x1, x2);
            double maxPx = Math.Max(x1, x2);
            var stroke = new SolidColorBrush(Color.FromRgb(229, 192, 123)); // Amber #E5C07B
            var extStroke = new SolidColorBrush(Color.FromArgb(120, 229, 192, 123));

            // Extension lines
            canvas.Children.Add(new Line { X1 = minPx, Y1 = extYTop, X2 = minPx, Y2 = y + 4, Stroke = extStroke, StrokeThickness = 1 });
            canvas.Children.Add(new Line { X1 = maxPx, Y1 = extYTop, X2 = maxPx, Y2 = y + 4, Stroke = extStroke, StrokeThickness = 1 });

            // Dimension line
            canvas.Children.Add(new Line { X1 = minPx, Y1 = y, X2 = maxPx, Y2 = y, Stroke = stroke, StrokeThickness = 1.2 });

            // Ticks (45 degree slash marks)
            canvas.Children.Add(new Line { X1 = minPx - 3, Y1 = y + 3, X2 = minPx + 3, Y2 = y - 3, Stroke = stroke, StrokeThickness = 1.5 });
            canvas.Children.Add(new Line { X1 = maxPx - 3, Y1 = y + 3, X2 = maxPx + 3, Y2 = y - 3, Stroke = stroke, StrokeThickness = 1.5 });

            // Label with badge
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 34, 39)),
                BorderBrush = stroke,
                BorderThickness = new Thickness(0.5),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 1, 4, 1)
            };
            border.Child = new TextBlock
            {
                Text = label,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = stroke
            };
            border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double labelW = border.DesiredSize.Width;
            double labelH = border.DesiredSize.Height;
            Canvas.SetLeft(border, (minPx + maxPx) / 2.0 - (labelW / 2.0));
            Canvas.SetTop(border, y - (labelH / 2.0));
            canvas.Children.Add(border);
        }

        private void DrawVerticalDimension(Canvas canvas, double y1, double y2, double x, double extXLeft, string label)
        {
            double minPy = Math.Min(y1, y2);
            double maxPy = Math.Max(y1, y2);
            var stroke = new SolidColorBrush(Color.FromRgb(229, 192, 123));
            var extStroke = new SolidColorBrush(Color.FromArgb(120, 229, 192, 123));

            // Extension lines
            canvas.Children.Add(new Line { X1 = extXLeft, Y1 = minPy, X2 = x + 4, Y2 = minPy, Stroke = extStroke, StrokeThickness = 1 });
            canvas.Children.Add(new Line { X1 = extXLeft, Y1 = maxPy, X2 = x + 4, Y2 = maxPy, Stroke = extStroke, StrokeThickness = 1 });

            // Dimension line
            canvas.Children.Add(new Line { X1 = x, Y1 = minPy, X2 = x, Y2 = maxPy, Stroke = stroke, StrokeThickness = 1.2 });

            // Ticks
            canvas.Children.Add(new Line { X1 = x - 3, Y1 = minPy + 3, X2 = x + 3, Y2 = minPy - 3, Stroke = stroke, StrokeThickness = 1.5 });
            canvas.Children.Add(new Line { X1 = x - 3, Y1 = maxPy + 3, X2 = x + 3, Y2 = maxPy - 3, Stroke = stroke, StrokeThickness = 1.5 });

            // Label with badge
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 34, 39)),
                BorderBrush = stroke,
                BorderThickness = new Thickness(0.5),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 1, 4, 1)
            };
            border.Child = new TextBlock
            {
                Text = label,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = stroke
            };
            border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double labelW = border.DesiredSize.Width;
            double labelH = border.DesiredSize.Height;
            Canvas.SetLeft(border, x - (labelW / 2.0));
            Canvas.SetTop(border, (minPy + maxPy) / 2.0 - (labelH / 2.0));
            canvas.Children.Add(border);
        }

        #endregion

        #region Image Handling (Browse & Drag-and-Drop)

        private void BtnBrowseImage_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Title = "Select Architectural Cut-Sheet (PDF) or Reference Image",
                Filter = "All Supported (*.pdf;*.png;*.jpg;*.jpeg;*.webp)|*.pdf;*.png;*.jpg;*.jpeg;*.webp|PDF Documents (*.pdf)|*.pdf|Images (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|All Files (*.*)|*.*"
            };

            if (dlg.ShowDialog(this) == true)
            {
                SetSelectedImage(dlg.FileName);
            }
        }

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    SetSelectedImage(files[0]);
                }
            }
        }

        private void SetSelectedImage(string path)
        {
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();

                if (ext == ".pdf")
                {
                    var embeddedImgs = GeminiVisionService.ExtractImagesFromPdf(path);
                    if (embeddedImgs.Count > 0)
                    {
                        try
                        {
                            using var ms = new MemoryStream(embeddedImgs[0].Data);
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.StreamSource = ms;
                            bmp.EndInit();
                            ImgPreview.Source = bmp;
                            ImgPreview.Visibility = Visibility.Visible;
                        }
                        catch
                        {
                            ImgPreview.Visibility = Visibility.Collapsed;
                        }
                    }
                    else
                    {
                        ImgPreview.Visibility = Visibility.Collapsed;
                    }

                    PdfBadge.Visibility = Visibility.Visible;
                    BtnClearImage.Visibility = Visibility.Visible;
                    EmptyDropContent.Visibility = Visibility.Collapsed;
                    _selectedImagePath = path;
                    TxtImageStatus.Text = embeddedImgs.Count > 0
                        ? $"PDF: {Path.GetFileName(path)} ({embeddedImgs.Count} embedded image(s) + text ready)"
                        : $"PDF: {Path.GetFileName(path)} (Multimodal Vision + Text ready)";
                    LogMessage($"Cut-Sheet PDF attached: {path} (extracted {embeddedImgs.Count} embedded raster image(s) for visual inspection)");
                }
                else
                {
                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(path);
                    bitmap.EndInit();

                    ImgPreview.Source = bitmap;
                    ImgPreview.Visibility = Visibility.Visible;
                    PdfBadge.Visibility = Visibility.Collapsed;
                    BtnClearImage.Visibility = Visibility.Visible;
                    EmptyDropContent.Visibility = Visibility.Collapsed;
                    _selectedImagePath = path;
                    TxtImageStatus.Text = Path.GetFileName(path);
                    LogMessage($"Image loaded: {path}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load file: {ex.Message}", "File Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnClearImage_Click(object sender, RoutedEventArgs e)
        {
            ImgPreview.Source = null;
            ImgPreview.Visibility = Visibility.Collapsed;
            PdfBadge.Visibility = Visibility.Collapsed;
            BtnClearImage.Visibility = Visibility.Collapsed;
            EmptyDropContent.Visibility = Visibility.Visible;
            _selectedImagePath = null;
            TxtImageStatus.Text = "No file selected (Supports PDF, PNG, JPG, WEBP)";
        }

        #endregion

        #region Directory Pickers

        private void BtnBrowseTemplateDir_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Title = "Select Any Template in your Family Template Directory",
                Filter = "Revit Family Template (*.rft)|*.rft|All Files (*.*)|*.*",
                CheckFileExists = false
            };

            if (dlg.ShowDialog(this) == true)
            {
                string? folder = Path.GetDirectoryName(dlg.FileName);
                if (!string.IsNullOrEmpty(folder))
                {
                    TxtTemplateDir.Text = folder;
                    _settings.TemplateDirectory = folder;
                    SettingsService.SaveSettings(_settings);
                }
            }
        }

        private void BtnBrowseOutputDir_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Title = "Select Destination Folder for Output Families",
                CheckFileExists = false,
                FileName = "SelectFolder"
            };

            if (dlg.ShowDialog(this) == true)
            {
                string? folder = Path.GetDirectoryName(dlg.FileName);
                if (!string.IsNullOrEmpty(folder))
                {
                    TxtOutputDir.Text = folder;
                    _settings.OutputDirectory = folder;
                    SettingsService.SaveSettings(_settings);
                }
            }
        }

        #endregion

        #region Schema Synthesis & Execution

        private async void BtnSynthesizeAi_Click(object sender, RoutedEventArgs e)
        {
            SaveUiToSettings();

            string apiKey = _settings.ApiKey;
            string prompt = TxtPrompt.Text.Trim();
            string model = _settings.ModelEngine;
            double temp = _settings.Temperature;

            if (string.IsNullOrEmpty(prompt) && string.IsNullOrEmpty(_selectedImagePath))
            {
                MessageBox.Show(this, "Please enter a design prompt or attach a reference cut-sheet/image.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                LogMessage("[AI Notice] No Gemini API key provided. Using offline deterministic AEC rule engine with cut-sheet analysis...");
                var fallback = _geminiService.GenerateDeterministicFallback(prompt, _selectedImagePath);
                UpdateJsonPreview(fallback);
                LogExtractionReport();
                LogMessage($"Offline schema created: '{fallback.MasterFamilyName}' ({fallback.HostExtrusions.Count} extrusions, {fallback.NestedFamilies.Count} nested components).");
                return;
            }

            var startTime = DateTime.UtcNow;
            var elapsedTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            elapsedTimer.Tick += (s, args) =>
            {
                int sec = (int)(DateTime.UtcNow - startTime).TotalSeconds;
                TxtAiElapsed.Text = $"{sec}s";
                BtnSynthesizeAi.Content = $"⏳ AI Modelling... ({sec}s)";
            };

            try
            {
                BtnSynthesizeAi.IsEnabled = false;
                BtnSynthesizeAi.Content = "⏳ AI Modelling... (0s)";
                TxtAiElapsed.Text = "0s";
                TxtAiStatus.Text = $"Starting Gemini multimodal synthesis ({model})...";
                AiStatusBanner.Visibility = Visibility.Visible;
                ProgBar.Visibility = Visibility.Visible;
                elapsedTimer.Start();

                LogMessage($"Calling Gemini Vision API ({model}, temp={temp:0.00}) with structured 3D schema constraints...");

                FamilySchema schema = await _geminiService.GenerateFamilySchemaAsync(
                    apiKey,
                    prompt,
                    _selectedImagePath,
                    model,
                    temp
                );

                UpdateJsonPreview(schema);
                LogMessage($"Successfully synthesized schema for '{schema.MasterFamilyName}' ({schema.HostExtrusions.Count} extrusions, {schema.NestedFamilies.Count} nested components) using {_geminiService.LastUsedModel ?? model}.");
                if (!string.IsNullOrEmpty(_geminiService.LastUsedModel) && !_geminiService.LastUsedModel!.Equals(model, StringComparison.OrdinalIgnoreCase))
                {
                    LogMessage($"[AI Notice] '{model}' was unavailable, so the fallback model '{_geminiService.LastUsedModel}' was used.");
                }

                if (!string.IsNullOrEmpty(_geminiService.LastUsageSummary))
                {
                    LogMessage($"[Token Economy] {_geminiService.LastUsageSummary}");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"[AI Error] {ex.Message}");
                MessageBox.Show(this, $"{ex.Message}\n\nThe offline rule engine will be used instead.", "AI Synthesis Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                var fallbackSchema = _geminiService.GenerateDeterministicFallback(prompt, _selectedImagePath);
                UpdateJsonPreview(fallbackSchema);
                LogExtractionReport();
            }
            finally
            {
                elapsedTimer.Stop();
                BtnSynthesizeAi.Content = "Synthesize AI (Gemini)";
                BtnSynthesizeAi.IsEnabled = true;
                AiStatusBanner.Visibility = Visibility.Collapsed;
                ProgBar.Visibility = Visibility.Collapsed;
            }
        }

        private void LogExtractionReport()
        {
            string? report = _geminiService.LastExtractionReport;
            if (string.IsNullOrEmpty(report)) return;
            foreach (var line in report!.Split('\n'))
            {
                LogMessage("[Offline] " + line.TrimEnd('\r'));
            }
        }

        private void BtnDeterministic_Click(object sender, RoutedEventArgs e)
        {
            string prompt = TxtPrompt.Text.Trim();
            LogMessage("Generating procedural schema via local deterministic AEC rule engine...");
            var schema = _geminiService.GenerateDeterministicFallback(prompt, _selectedImagePath);
            UpdateJsonPreview(schema);
            LogExtractionReport();
            LogMessage($"Deterministic schema created: '{schema.MasterFamilyName}' ({schema.HostExtrusions.Count} extrusions, {schema.NestedFamilies.Count} nested components).");
        }

        private void BtnGenerateRevitFamily_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveUiToSettings();

                string jsonText = TxtJsonPreview.Text.Trim();
                if (string.IsNullOrEmpty(jsonText))
                {
                    MessageBox.Show(this, "JSON Schema is empty. Please synthesize or enter a schema first.", "Schema Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                FamilySchema? schema = JsonSerializer.Deserialize<FamilySchema>(jsonText, options);

                if (schema == null)
                {
                    MessageBox.Show(this, "Failed to parse JSON Schema. Please verify format.", "Invalid JSON", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Run pre-generation diagnostic check
                var validation = SchemaValidator.Validate(schema);
                if (!validation.IsValid)
                {
                    string errList = string.Join("\n- ", validation.Errors);
                    MessageBox.Show(this, $"Cannot generate Revit family due to validation errors:\n\n- {errList}", "Validation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    LogMessage($"[Generation Blocked] {validation.Errors.Count} validation error(s) prevented Revit API execution.");
                    return;
                }

                if (validation.Warnings.Count > 0)
                {
                    foreach (var warn in validation.Warnings)
                    {
                        LogMessage($"[Pre-Build Notice] {warn}");
                    }
                }

                string templateDir = TxtTemplateDir.Text.Trim();
                string outputDir = TxtOutputDir.Text.Trim();

                if (!Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                _handler.CurrentSchema = schema;
                _handler.TemplateDirectory = templateDir;
                _handler.OutputDirectory = outputDir;

                BtnGenerateRevitFamily.IsEnabled = false;
                ProgBar.Visibility = Visibility.Visible;
                LogMessage($"Raising Revit ExternalEvent for '{schema.MasterFamilyName}' ({schema.FamilyTypes.Count} types, {schema.HostExtrusions.Count} extrusions, {schema.NestedFamilies.Count} nested)...");

                _externalEvent.Raise();
            }
            catch (Exception ex)
            {
                LogMessage($"[Setup Error] {ex.Message}");
                MessageBox.Show(this, $"Error preparing generation: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnGenerateRevitFamily.IsEnabled = true;
                ProgBar.Visibility = Visibility.Collapsed;
            }
        }

        #endregion
    }
}
