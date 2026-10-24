using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

        private void BtnValidate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string jsonText = TxtJsonPreview.Text.Trim();
                var schema = JsonSerializer.Deserialize<FamilySchema>(jsonText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var report = SchemaValidator.Validate(schema);

                RunQuickValidation(schema);

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
