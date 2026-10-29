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
    public partial class ForgeDockablePanel
    {
        private ExecutionPlan? _currentDockCopilotPlan;
        private FamilyState? _latestDockFamilyState;
        private static ForgeWindow? _studioWindow;

        private void ForgeDockablePanel_Loaded(object sender, RoutedEventArgs e)
        {
            _handler.OnInspectionCompleted += (state) => Dispatcher.Invoke(() => UpdateDockLiveStateUi(state));
            _handler.OnPlanCompleted += (execResult, valResult) => Dispatcher.Invoke(() => UpdateDockValidationUi(execResult, valResult));

            // Proactively inspect the active Revit document
            TriggerDockLiveInspection();
        }

        private void BtnOpenFullStudio_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_studioWindow != null && _studioWindow.IsLoaded)
                {
                    _studioWindow.Activate();
                    return;
                }

                _studioWindow = new ForgeWindow(_externalEvent, _handler);
                _studioWindow.Closed += (s, args) => _studioWindow = null;
                _studioWindow.Show();
            }
            catch (Exception ex)
            {
                LogMessage($"[STUDIO WINDOW ERROR] {ex.Message}");
            }
        }

        private void BtnDockInspect_Click(object sender, RoutedEventArgs e)
        {
            TriggerDockLiveInspection();
        }

        private void TriggerDockLiveInspection()
        {
            LogMessage("[COPILOT] Inspecting active Revit Family Document...");
            _handler.RequestInspectionOnly = true;
            _externalEvent.Raise();
        }

        private void UpdateDockLiveStateUi(FamilyState state)
        {
            _latestDockFamilyState = state;
            string docTitle = string.IsNullOrEmpty(state.FamilyName) ? "Active Revit Document" : state.FamilyName;
            int solidCount = state.Solids.Count(s => !s.IsVoid);
            int voidCount = state.Solids.Count(s => s.IsVoid);

            TxtDockLiveFamilyName.Text = $"🏛️ {docTitle} ({state.FamilyCategory})";
            TxtDockLiveFamilyStats.Text = $"{state.Parameters.Count} Params | {solidCount} Solids | {state.ReferencePlanes.Count} Planes | {state.Dimensions.Count} Dims";

            var paramItems = state.Parameters
                .OrderBy(p => p.Name)
                .Select(p => new ForgeWindow.LiveParamDisplayItem
                {
                    Name = p.Name,
                    DisplayValue = !string.IsNullOrEmpty(p.Formula)
                        ? $"{p.ValueString} (= {p.Formula})"
                        : (p.ValueNumeric.HasValue ? $"{p.ValueNumeric.Value:0.#} mm" : p.ValueString)
                })
                .ToList();

            if (paramItems.Count == 0)
            {
                paramItems.Add(new ForgeWindow.LiveParamDisplayItem
                {
                    Name = "(No custom parameters yet)",
                    DisplayValue = "Ask Copilot to create"
                });
            }
            LstDockLiveParams.ItemsSource = paramItems;

            var solidItems = new List<ForgeWindow.LiveSolidDisplayItem>();
            foreach (var s in state.Solids)
            {
                double dx = Math.Max(0, s.MaxX_mm - s.MinX_mm);
                double dy = Math.Max(0, s.MaxY_mm - s.MinY_mm);
                double dz = Math.Max(0, s.MaxZ_mm - s.MinZ_mm);
                string kindIcon = s.IsVoid ? "✂️ Void" : "🧱 Solid";
                string roleText = !string.IsNullOrEmpty(s.Role) ? s.Role! : s.Name;
                solidItems.Add(new ForgeWindow.LiveSolidDisplayItem
                {
                    Label = $"{kindIcon}: {roleText}",
                    SizeSummary = $"{dx:0}×{dy:0}×{dz:0} mm"
                });
            }

            foreach (var rp in state.ReferencePlanes.Take(8))
            {
                solidItems.Add(new ForgeWindow.LiveSolidDisplayItem
                {
                    Label = $"📐 Plane: {rp.Name}",
                    SizeSummary = $"({rp.BubbleEndX:0},{rp.BubbleEndY:0},{rp.BubbleEndZ:0}) mm"
                });
            }

            if (solidItems.Count == 0)
            {
                solidItems.Add(new ForgeWindow.LiveSolidDisplayItem
                {
                    Label = "(Empty Family Document)",
                    SizeSummary = "Ready to build"
                });
            }
            LstDockLiveSolids.ItemsSource = solidItems;

            LogMessage($"[COPILOT INSPECTOR] '{docTitle}': {state.Parameters.Count} params, {solidCount} solids ({voidCount} voids).");
        }

        private void DockLiveParam_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is ForgeWindow.LiveParamDisplayItem item && !item.Name.StartsWith("("))
            {
                TxtDockCopilotInput.Text = $"Set {item.Name} to ";
                TxtDockCopilotInput.Focus();
                TxtDockCopilotInput.CaretIndex = TxtDockCopilotInput.Text.Length;
            }
        }

        private void DockQuickChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string prompt)
            {
                TxtDockCopilotInput.Text = prompt;
                ProcessDockCopilotPromptAsync(prompt);
            }
        }

        private void TxtDockCopilotInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string prompt = TxtDockCopilotInput.Text.Trim();
                if (!string.IsNullOrEmpty(prompt))
                {
                    ProcessDockCopilotPromptAsync(prompt);
                }
            }
        }

        private void BtnDockSendCopilot_Click(object sender, RoutedEventArgs e)
        {
            string prompt = TxtDockCopilotInput.Text.Trim();
            if (!string.IsNullOrEmpty(prompt))
            {
                ProcessDockCopilotPromptAsync(prompt);
            }
        }

        private async void ProcessDockCopilotPromptAsync(string prompt)
        {
            try
            {
                AddDockChatMessage(prompt, isUser: true);
                TxtDockCopilotInput.Text = string.Empty;
                BtnDockSendCopilot.IsEnabled = false;
                LogMessage($"[COPILOT PLANNER] Planning operations for: \"{prompt}\"...");

                string apiKey = TxtDockApiKey.Password.Trim();
                string model = (CmbDockModel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? GeminiVisionService.DefaultGeminiModel;
                if (model == "offline") apiKey = string.Empty;

                var currentState = _latestDockFamilyState ?? new FamilyState();

                var plan = await _geminiService.GenerateExecutionPlanAsync(apiKey, prompt, currentState, _selectedFilePath, model);
                _currentDockCopilotPlan = plan;

                BtnDockSendCopilot.IsEnabled = true;
                DisplayDockExecutionPlan(plan);
                DockChatScroll.ScrollToEnd();

                if (ChkDockAutoExecute.IsChecked == true)
                {
                    BtnDockExecutePlan_Click(this, new RoutedEventArgs());
                }
            }
            catch (Exception ex)
            {
                BtnDockSendCopilot.IsEnabled = true;
                LogMessage($"[COPILOT ERROR] {ex.Message}");
            }
        }

        private void DisplayDockExecutionPlan(ExecutionPlan plan)
        {
            DockPlanCard.Visibility = Visibility.Visible;
            TxtDockPlanIntent.Text = plan.Intent.ToUpperInvariant();
            TxtDockPlanSummary.Text = plan.Summary;

            var formattedOps = plan.Operations.Select(op => new
            {
                Operation = op.Operation.ToString(),
                Summary = op.Summary ?? $"{op.Role ?? op.ElementName ?? op.ParameterName ?? op.PlaneName}"
            }).ToList();

            LstDockPlanOps.ItemsSource = formattedOps;
            AddDockChatMessage($"📋 Plan: {plan.Summary} ({plan.Operations.Count} atomic Revit operations).", isUser: false);
        }

        private void BtnDockExecutePlan_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDockCopilotPlan == null) return;

            _handler.CurrentPlan = _currentDockCopilotPlan;
            _handler.TemplateDirectory = _settings.TemplateDirectory;
            _handler.OutputDirectory = _settings.OutputDirectory;

            BtnDockExecutePlan.IsEnabled = false;
            LogMessage($"[COPILOT EXECUTOR] Dispatching {_currentDockCopilotPlan.Operations.Count} atomic operations to Revit...");
            _externalEvent.Raise();
        }

        private void UpdateDockValidationUi(ExecutionResult execResult, ValidationResult valResult)
        {
            BtnDockExecutePlan.IsEnabled = true;
            DockValidationCard.Visibility = Visibility.Visible;

            if (valResult.IntentSatisfied)
            {
                DockValidationCard.Background = new SolidColorBrush(Color.FromArgb(255, 27, 43, 36));
                DockValidationCard.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 152, 195, 121));
                TxtDockValBadge.Text = "INTENT VERIFIED";
                TxtDockValBadge.Foreground = new SolidColorBrush(Color.FromArgb(255, 152, 195, 121));
                BtnDockAutoRepair.Visibility = Visibility.Collapsed;
            }
            else
            {
                DockValidationCard.Background = new SolidColorBrush(Color.FromArgb(255, 43, 27, 27));
                DockValidationCard.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 224, 108, 117));
                TxtDockValBadge.Text = "DISCREPANCY";
                TxtDockValBadge.Foreground = new SolidColorBrush(Color.FromArgb(255, 224, 108, 117));
                BtnDockAutoRepair.Visibility = valResult.SuggestedRepairPlan != null ? Visibility.Visible : Visibility.Collapsed;
            }

            TxtDockValSummary.Text = valResult.Summary;
            TxtDockValComponents.Text = $"BRep Solids: {valResult.ActualComponentsCount} verified (expected >= {valResult.ExpectedComponentsCount})";
            TxtDockValFlex.Text = valResult.FlexTestResults.Count > 0 ? string.Join(" | ", valResult.FlexTestResults) : "Ready for flex test.";

            string botMsg = valResult.IntentSatisfied
                ? $"✅ Executed & Verified: {valResult.Summary}"
                : $"⚠️ Executed with notice: {valResult.Summary}";
            AddDockChatMessage(botMsg, isUser: false);
            DockChatScroll.ScrollToEnd();
        }

        private void BtnDockAutoRepair_Click(object sender, RoutedEventArgs e)
        {
            if (_handler.LastValidationResult?.SuggestedRepairPlan != null)
            {
                _currentDockCopilotPlan = _handler.LastValidationResult.SuggestedRepairPlan;
                DisplayDockExecutionPlan(_currentDockCopilotPlan);
                BtnDockExecutePlan_Click(sender, e);
            }
        }

        private void AddDockChatMessage(string message, bool isUser)
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
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var panel = new StackPanel();
            var header = new TextBlock
            {
                Text = isUser ? "👤 You" : "🤖 Family Forge Copilot",
                FontWeight = FontWeights.Bold,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(isUser
                    ? Color.FromArgb(255, 97, 175, 239)
                    : Color.FromArgb(255, 152, 195, 121)),
                Margin = new Thickness(0, 0, 0, 2)
            };
            var content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 224, 226, 232)),
                FontSize = 11.5
            };

            panel.Children.Add(header);
            panel.Children.Add(content);
            border.Child = panel;

            int insertIdx = DockChatStack.Children.IndexOf(DockPlanCard);
            if (insertIdx >= 0)
            {
                DockChatStack.Children.Insert(insertIdx, border);
            }
            else
            {
                DockChatStack.Children.Add(border);
            }
        }
    }
}
