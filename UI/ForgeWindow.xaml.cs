using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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

        public ForgeWindow(ExternalEvent externalEvent, FamilyGenerationHandler handler)
        {
            InitializeComponent();

            _externalEvent = externalEvent ?? throw new ArgumentNullException(nameof(externalEvent));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _geminiService = new GeminiVisionService();

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
                    MessageBox.Show(this, $"Family successfully created and loaded into active project!\n\nPath:\n{resultMsg}", 
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
            var defaultSchema = _geminiService.GenerateDeterministicFallback("workstation");
            UpdateJsonPreview(defaultSchema);
        }

        private void UpdateJsonPreview(FamilySchema schema)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            TxtJsonPreview.Text = JsonSerializer.Serialize(schema, options);
        }

        private void LogMessage(string message)
        {
            TxtLogs.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            TxtLogs.ScrollToEnd();
        }

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
                    ImgPreview.Visibility = Visibility.Collapsed;
                    PdfBadge.Visibility = Visibility.Visible;
                    BtnClearImage.Visibility = Visibility.Visible;
                    EmptyDropContent.Visibility = Visibility.Collapsed;
                    _selectedImagePath = path;
                    TxtImageStatus.Text = $"PDF: {Path.GetFileName(path)} (Ready for Multimodal Analysis)";
                    LogMessage($"Cut-Sheet PDF attached: {path}");
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

            if (string.IsNullOrEmpty(prompt))
            {
                MessageBox.Show(this, "Please enter a design prompt describing the desired Revit Family.", "Prompt Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                BtnSynthesizeAi.IsEnabled = false;
                ProgBar.Visibility = Visibility.Visible;
                LogMessage($"Calling Gemini Vision API ({model}, temp={temp:0.00}) with structured schema constraints...");

                FamilySchema schema = await _geminiService.GenerateFamilySchemaAsync(
                    apiKey,
                    prompt,
                    _selectedImagePath,
                    model,
                    temp
                );

                UpdateJsonPreview(schema);
                LogMessage($"Successfully synthesized schema for '{schema.MasterFamilyName}'!");

                if (!string.IsNullOrEmpty(_geminiService.LastUsageSummary))
                {
                    LogMessage($"[Token Economy] {_geminiService.LastUsageSummary} (~$0.0001 per run).");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"[AI Error] {ex.Message}");
                MessageBox.Show(this, $"Gemini API request failed:\n{ex.Message}\n\nFalling back to deterministic rule engine.", "Synthesis Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                var fallbackSchema = _geminiService.GenerateDeterministicFallback(prompt);
                UpdateJsonPreview(fallbackSchema);
            }
            finally
            {
                BtnSynthesizeAi.IsEnabled = true;
                ProgBar.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnDeterministic_Click(object sender, RoutedEventArgs e)
        {
            string prompt = TxtPrompt.Text.Trim();
            LogMessage("Generating procedural schema via local deterministic AEC rule engine...");
            var schema = _geminiService.GenerateDeterministicFallback(prompt);
            UpdateJsonPreview(schema);
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
                LogMessage("Raising Revit ExternalEvent to execute family generation on Revit main UI thread...");

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
