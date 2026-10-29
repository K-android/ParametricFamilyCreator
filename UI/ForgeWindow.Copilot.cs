using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ParametricFamilyForge.Models;
using ParametricFamilyForge.Services;
using ValidationResult = ParametricFamilyForge.Models.ValidationResult;

namespace ParametricFamilyForge.UI
{
    public partial class ForgeWindow
    {
        private ExecutionPlan? _currentCopilotPlan;
        private FamilyState? _latestFamilyState;

        public class LiveParamDisplayItem
        {
            public string Name { get; set; } = string.Empty;
            public string DisplayValue { get; set; } = string.Empty;
        }

        public class LiveSolidDisplayItem
        {
            public string Label { get; set; } = string.Empty;
            public string SizeSummary { get; set; } = string.Empty;
        }

        private void ForgeWindow_Copilot_Loaded(object sender, RoutedEventArgs e)
        {
            _handler.OnInspectionCompleted += (state) => Dispatcher.Invoke(() => UpdateLiveStateUi(state));
            _handler.OnPlanCompleted += (execResult, valResult) => Dispatcher.Invoke(() => UpdateValidationUi(execResult, valResult));

            // Proactively query live Revit document state upon launch
            TriggerLiveInspection();
        }

        private void BtnInspectLive_Click(object sender, RoutedEventArgs e)
        {
            TriggerLiveInspection();
        }

        private void TriggerLiveInspection()
        {
            LogMessage("[COPILOT] Querying active Revit document for live inspection snapshot...");
            _handler.RequestInspectionOnly = true;
            _externalEvent.Raise();
        }

        private void UpdateLiveStateUi(FamilyState state)
        {
            _latestFamilyState = state;
            string docTitle = string.IsNullOrEmpty(state.FamilyName) ? "Active Revit Document" : state.FamilyName;
            TxtLiveDocName.Text = docTitle;
            int solidCount = state.Solids.Count(s => !s.IsVoid);
            int voidCount = state.Solids.Count(s => s.IsVoid);
            TxtLiveDocParams.Text = $"Category: {state.FamilyCategory} | {state.Parameters.Count} Params | {solidCount} Solids | {state.ReferencePlanes.Count} Planes";

            // Populate Live Parameters list in Left Sidebar
            var paramItems = state.Parameters
                .OrderBy(p => p.Name)
                .Select(p => new LiveParamDisplayItem
                {
                    Name = p.Name,
                    DisplayValue = !string.IsNullOrEmpty(p.Formula)
                        ? $"{p.ValueString} (= {p.Formula})"
                        : (p.ValueNumeric.HasValue ? $"{p.ValueNumeric.Value:0.#} mm" : p.ValueString)
                })
                .ToList();

            if (paramItems.Count == 0)
            {
                paramItems.Add(new LiveParamDisplayItem
                {
                    Name = "(No custom parameters yet)",
                    DisplayValue = "Ask Copilot to create"
                });
            }
            LstLiveParameters.ItemsSource = paramItems;

            // Populate Live 3D Solids & Reference Planes list in Left Sidebar
            var solidItems = new List<LiveSolidDisplayItem>();
            foreach (var s in state.Solids)
            {
                double dx = Math.Max(0, s.MaxX_mm - s.MinX_mm);
                double dy = Math.Max(0, s.MaxY_mm - s.MinY_mm);
                double dz = Math.Max(0, s.MaxZ_mm - s.MinZ_mm);
                string kindIcon = s.IsVoid ? "✂️ Void" : "🧱 Solid";
                string roleText = !string.IsNullOrEmpty(s.Role) ? s.Role! : s.Name;
                solidItems.Add(new LiveSolidDisplayItem
                {
                    Label = $"{kindIcon}: {roleText}",
                    SizeSummary = $"{dx:0}×{dy:0}×{dz:0} mm"
                });
            }

            foreach (var rp in state.ReferencePlanes.Take(10))
            {
                solidItems.Add(new LiveSolidDisplayItem
                {
                    Label = $"📐 Plane: {rp.Name}",
                    SizeSummary = $"({rp.BubbleEndX:0},{rp.BubbleEndY:0},{rp.BubbleEndZ:0}) mm"
                });
            }

            if (solidItems.Count == 0)
            {
                solidItems.Add(new LiveSolidDisplayItem
                {
                    Label = "(Empty Family Document)",
                    SizeSummary = "Ready to build"
                });
            }
            LstLiveSolids.ItemsSource = solidItems;

            LogMessage($"[COPILOT] Live Family State Updated: '{docTitle}' ({state.FamilyCategory}) — {state.Parameters.Count} parameters, {solidCount} solids ({voidCount} voids), {state.Dimensions.Count} dimensions.");
        }

        private void LiveParamItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is LiveParamDisplayItem item && !item.Name.StartsWith("("))
            {
                TxtCopilotInput.Text = $"Set {item.Name} to ";
                TxtCopilotInput.Focus();
                TxtCopilotInput.CaretIndex = TxtCopilotInput.Text.Length;
            }
        }

        private void QuickChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string prompt)
            {
                TxtCopilotInput.Text = prompt;
                ProcessCopilotPromptAsync(prompt);
            }
        }

        private void TxtCopilotInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string prompt = TxtCopilotInput.Text.Trim();
                if (!string.IsNullOrEmpty(prompt))
                {
                    ProcessCopilotPromptAsync(prompt);
                }
            }
        }

        private void BtnSendCopilot_Click(object sender, RoutedEventArgs e)
        {
            string prompt = TxtCopilotInput.Text.Trim();
            if (!string.IsNullOrEmpty(prompt))
            {
                ProcessCopilotPromptAsync(prompt);
            }
        }

        private async void ProcessCopilotPromptAsync(string prompt)
        {
            try
            {
                AddChatMessage(prompt, isUser: true);
                TxtCopilotInput.Text = string.Empty;
                ProgBar.Visibility = Visibility.Visible;
                AiStatusBanner.Visibility = Visibility.Visible;
                TxtAiStatus.Text = $"Copilot planning operations for: \"{prompt}\"...";
                LogMessage($"[COPILOT PLANNER] Reasoning about design intent for: '{prompt}'...");

                string apiKey = TxtApiKey.Password.Trim();
                string model = (CmbModelEngine.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? GeminiVisionService.DefaultGeminiModel;
                var currentState = _latestFamilyState ?? new FamilyState();

                // 1. Generate structured execution plan
                var plan = await _geminiService.GenerateExecutionPlanAsync(apiKey, prompt, currentState, _selectedImagePath, model);
                _currentCopilotPlan = plan;

                ProgBar.Visibility = Visibility.Collapsed;
                AiStatusBanner.Visibility = Visibility.Collapsed;
                DisplayExecutionPlan(plan);

                // Auto-scroll chat to bottom
                ChatScrollViewer.ScrollToEnd();

                // If Auto-Execute is checked, immediately run the plan in Revit!
                if (ChkAutoExecutePlan.IsChecked == true)
                {
                    BtnExecuteCopilotPlan_Click(this, new RoutedEventArgs());
                }
            }
            catch (Exception ex)
            {
                ProgBar.Visibility = Visibility.Collapsed;
                AiStatusBanner.Visibility = Visibility.Collapsed;
                LogMessage($"[COPILOT ERROR] {ex.Message}");
            }
        }

        private void DisplayExecutionPlan(ExecutionPlan plan)
        {
            PlanCard.Visibility = Visibility.Visible;
            TxtPlanIntent.Text = plan.Intent.ToUpperInvariant();
            TxtPlanSummary.Text = plan.Summary;

            var formattedOps = plan.Operations.Select(op => new
            {
                Operation = op.Operation.ToString(),
                Summary = op.Summary ?? $"{op.Operation}: {op.Role ?? op.ElementName ?? op.ParameterName ?? op.PlaneName}"
            }).ToList();

            LstPlanOperations.ItemsSource = formattedOps;

            AddChatMessage($"📋 Plan generated: {plan.Summary} ({plan.Operations.Count} atomic Revit operations).", isUser: false);
        }

        private void BtnExecuteCopilotPlan_Click(object sender, RoutedEventArgs e)
        {
            if (_currentCopilotPlan == null) return;

            SaveUiToSettings();
            _handler.CurrentPlan = _currentCopilotPlan;
            _handler.TemplateDirectory = TxtTemplateDir.Text.Trim();
            _handler.OutputDirectory = TxtOutputDir.Text.Trim();

            ProgBar.Visibility = Visibility.Visible;
            BtnExecuteCopilotPlan.IsEnabled = false;

            LogMessage($"[COPILOT] Raising Revit ExternalEvent to execute plan deterministically...");
            _externalEvent.Raise();
        }

        private void UpdateValidationUi(ExecutionResult execResult, ValidationResult valResult)
        {
            BtnExecuteCopilotPlan.IsEnabled = true;
            ProgBar.Visibility = Visibility.Collapsed;

            ValidationCard.Visibility = Visibility.Visible;
            if (valResult.IntentSatisfied)
            {
                ValidationCard.Background = new SolidColorBrush(Color.FromArgb(255, 27, 43, 36));
                ValidationCard.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 152, 195, 121));
                TxtValBadge.Text = "INTENT VERIFIED";
                TxtValBadge.Foreground = new SolidColorBrush(Color.FromArgb(255, 152, 195, 121));
                BtnAutoRepair.Visibility = Visibility.Collapsed;
            }
            else
            {
                ValidationCard.Background = new SolidColorBrush(Color.FromArgb(255, 43, 27, 27));
                ValidationCard.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 224, 108, 117));
                TxtValBadge.Text = "INTENT DISCREPANCY";
                TxtValBadge.Foreground = new SolidColorBrush(Color.FromArgb(255, 224, 108, 117));
                BtnAutoRepair.Visibility = valResult.SuggestedRepairPlan != null ? Visibility.Visible : Visibility.Collapsed;
            }

            TxtValSummary.Text = valResult.Summary;
            TxtValComponents.Text = $"Components: {valResult.ActualComponentsCount} distinct solid(s) verified in Revit BRep (expected >= {valResult.ExpectedComponentsCount})";
            TxtValFlex.Text = valResult.FlexTestResults.Count > 0 ? string.Join(" | ", valResult.FlexTestResults) : "No flex tests recorded.";
            LstDiscrepancies.ItemsSource = valResult.Discrepancies;

            string botMsg = valResult.IntentSatisfied
                ? $"✅ Executed & BRep Verified in Revit: {valResult.Summary}"
                : $"⚠️ Executed in Revit, but intent check flagged: {valResult.Summary}";
            AddChatMessage(botMsg, isUser: false);

            ChatScrollViewer.ScrollToEnd();
        }

        private void BtnAutoRepair_Click(object sender, RoutedEventArgs e)
        {
            if (_handler.LastValidationResult?.SuggestedRepairPlan != null)
            {
                _currentCopilotPlan = _handler.LastValidationResult.SuggestedRepairPlan;
                DisplayExecutionPlan(_currentCopilotPlan);
                BtnExecuteCopilotPlan_Click(sender, e);
            }
        }

        private void AddChatMessage(string message, bool isUser)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(isUser
                    ? Color.FromArgb(255, 37, 42, 52)
                    : Color.FromArgb(255, 29, 33, 42)),
                BorderBrush = new SolidColorBrush(isUser
                    ? Color.FromArgb(255, 97, 175, 239)
                    : Color.FromArgb(255, 152, 195, 121)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var panel = new StackPanel();
            var header = new TextBlock
            {
                Text = isUser ? "👤 You" : "🤖 Family Forge Copilot",
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Foreground = new SolidColorBrush(isUser
                    ? Color.FromArgb(255, 97, 175, 239)
                    : Color.FromArgb(255, 152, 195, 121)),
                Margin = new Thickness(0, 0, 0, 3)
            };
            var content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 224, 226, 232)),
                FontSize = 12.5
            };

            panel.Children.Add(header);
            panel.Children.Add(content);
            border.Child = panel;

            // Insert before PlanCard so PlanCard and ValidationCard stay anchored at the bottom of the stream
            int insertIdx = ChatStackPanel.Children.IndexOf(PlanCard);
            if (insertIdx >= 0)
            {
                ChatStackPanel.Children.Insert(insertIdx, border);
            }
            else
            {
                ChatStackPanel.Children.Add(border);
            }
        }
    }
}
