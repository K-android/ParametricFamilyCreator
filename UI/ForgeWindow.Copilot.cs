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

        private void ForgeWindow_Copilot_Loaded(object sender, RoutedEventArgs e)
        {
            _handler.OnInspectionCompleted = (state) => Dispatcher.Invoke(() => UpdateLiveStateUi(state));
            _handler.OnPlanCompleted = (execResult, valResult) => Dispatcher.Invoke(() => UpdateValidationUi(execResult, valResult));

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
            string docTitle = string.IsNullOrEmpty(state.FamilyName) ? "Active Family" : state.FamilyName;
            TxtLiveDocName.Text = docTitle;
            int solidCount = state.Solids.Count(s => !s.IsVoid);
            TxtLiveDocParams.Text = $" | Category: {state.FamilyCategory} | {state.Parameters.Count} params | {solidCount} solids";
            LogMessage($"[COPILOT] Live Family State Updated: '{docTitle}' ({state.FamilyCategory}) — {state.Parameters.Count} parameters, {solidCount} solids, {state.Dimensions.Count} dimensions.");
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
                LogMessage($"[COPILOT PLANNER] Reasoning about design intent for: '{prompt}'...");

                string apiKey = TxtApiKey.Password.Trim();
                string model = (CmbModelEngine.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? GeminiVisionService.DefaultGeminiModel;
                var currentState = _latestFamilyState ?? new FamilyState();

                // 1. Generate structured execution plan
                var plan = await _geminiService.GenerateExecutionPlanAsync(apiKey, prompt, currentState, _selectedImagePath, model);
                _currentCopilotPlan = plan;

                ProgBar.Visibility = Visibility.Collapsed;
                DisplayExecutionPlan(plan);

                // Auto-scroll chat to bottom
                ChatScrollViewer.ScrollToEnd();
            }
            catch (Exception ex)
            {
                ProgBar.Visibility = Visibility.Collapsed;
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
                Summary = op.Summary ?? $"{op.Operation}: {op.Role ?? op.ElementName ?? op.ParameterName}"
            }).ToList();

            LstPlanOperations.ItemsSource = formattedOps;

            AddChatMessage($"Plan generated: {plan.Summary} ({plan.Operations.Count} operations). Ready to execute in Revit.", isUser: false);
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
                ? $"✅ Plan executed and verified: {valResult.Summary}"
                : $"⚠️ Plan executed in Revit, but intent check flagged: {valResult.Summary}";
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
                    ? Color.FromArgb(255, 40, 44, 52)
                    : Color.FromArgb(255, 30, 34, 42)),
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
                FontSize = 12
            };

            panel.Children.Add(header);
            panel.Children.Add(content);
            border.Child = panel;
            ChatStackPanel.Children.Add(border);
        }
    }
}
