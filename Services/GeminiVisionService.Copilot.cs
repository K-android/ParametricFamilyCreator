using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.Services
{
    public partial class GeminiVisionService
    {
        /// <summary>
        /// Conversational Copilot Planner: Analyzes user prompt and live family state to produce a structured, typed ExecutionPlan.
        /// Enriched with ArchSmarterFamFab's 3-Tier Detail Level taxonomy, (u,v) sketch plane coordinate mapping,
        /// parametric arithmetic expression syntax, N-division even spacing formula, and standard architectural dimensions.
        /// </summary>
        public async Task<ExecutionPlan> GenerateExecutionPlanAsync(
            string apiKey,
            string userPrompt,
            FamilyState currentState,
            string? imageFilePath = null,
            string modelName = DefaultGeminiModel,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return GenerateDeterministicExecutionPlan(userPrompt, currentState);
            }

            try
            {
                var parts = new List<object>();

                if (!string.IsNullOrEmpty(imageFilePath) && File.Exists(imageFilePath))
                {
                    byte[] imageBytes = OptimizeImageForVision(imageFilePath!, out string mimeType);
                    parts.Add(new
                    {
                        inline_data = new
                        {
                            mime_type = mimeType,
                            data = Convert.ToBase64String(imageBytes)
                        }
                    });
                }

                string stateJson = JsonSerializer.Serialize(currentState, new JsonSerializerOptions { WriteIndented = false });

                string plannerSystemPrompt = $@"You are the Conversational AI Revit Family Copilot Planner.
Your role is to reason about Revit family design intent and emit a strictly-typed machine-readable ExecutionPlan JSON.

CURRENT OPEN REVIT FAMILY STATE:
{stateJson}

CRITICAL CONVERSATIONAL & ARCHITECTURAL RULES:
1. INCREMENTAL MODIFICATION FIRST:
   - If the family already contains geometry/parameters and the user wants to modify an existing attribute (e.g. 'Make the frame 100 mm'):
     DO NOT recreate the family or delete existing parts. Emit a SET_PARAMETER operation.
2. INCREMENTAL COMPONENT ADDITION:
   - If the user asks to add a component (e.g. 'Add a 75 mm projecting sill' or 'Add 3 shelves'):
     Add reference planes and CREATE_EXTRUSION / CREATE_SWEEP / CREATE_BLEND operations for ONLY the requested component(s).
3. 3-TIER ARCHITECTURAL DETAIL LEVELS (when creating a new family):
   - QUICK (1-3 solids): Only when user explicitly asks for 'simple', 'massing', or 'placeholder'.
   - STANDARD (5-15 solids, DEFAULT): Decompose into real architectural sub-components:
     * Window: 4 distinct frame extrusions (Left Jamb, Right Jamb, Bottom Sill, Top Head) + 1 Glazing panel (+ Mullions/Sill if requested).
     * Door: Left/Right/Head frame jambs + Door panel + Handle hardware.
     * Cabinet/Casework: Left/Right side panels, Top/Bottom panels, Back panel, Toe kick, Shelves, Door/Drawer fronts with 3mm reveal gaps.
     * Table/Desk: Tabletop + 4 legs (extrusion or tapered CREATE_BLEND) + Apron rails / modesty panel.
   - DETAILED (10-25 solids): Include hardware pulls, edge banding, trim moldings (CREATE_SWEEP), and recessed panels (CREATE_VOID).
4. PARAMETRIC ARITHMETIC EXPRESSIONS (Eliminate mental math errors!):
   - You can provide exact millimeter numbers OR parametric expressions referencing parameter names in:
     `offset_expr`, `min_x_expr`, `max_x_expr`, `min_y_expr`, `max_y_expr`, `start_z_expr`, `end_z_expr`,
     `origin_u_expr`, `origin_v_expr`, `width_expr`, `height_expr`, `radius_expr`, `top_radius_expr`.
   - Examples: `""-Width / 2""`, `""-Width / 2 + Frame_Width""`, `""Width - 2 * Material_Thickness""`, `""Height - Top_Thickness""`.
5. SKETCH-PLANE (u, v) COORDINATE SYSTEM (Optional `sketch_plane` on CREATE_EXTRUSION, CREATE_SWEEP, CREATE_BLEND):
   - `Z` plane (`Ref. Level`, `Top`, `Bottom`): u = X (left-to-right), v = Y (front-to-back), extrudes along Z.
   - `Y` plane (`Front`, `Back`, `Center (Front/Back)`): u = X (left-to-right), v = Z (bottom-to-top), extrudes along Y.
   - `X` plane (`Left`, `Right`, `Center (Left/Right)`): u = Y (front-to-back), v = Z (bottom-to-top), extrudes along X.
6. EVEN SPACING FORMULA FOR N INTERNAL DIVISIONS (Shelves, Mullions, Drawers):
   - For N divisions inside `total_span` with `thickness` (including top & bottom panels of `thickness`):
     `spacing = (total_span - (N + 2) * thickness) / (N + 1)`
     Division K (1 to N) starts at: `offset_K = K * spacing + (K + 1) * thickness`
7. STANDARD ARCHITECTURAL DIMENSIONS (mm):
   - Window: Width 1200, Height 1500, Sill Height 900, Frame_Width 75, Frame_Depth 100, Glass_Thickness 10
   - Desk: Width 1525, Depth 760, Height 760, Top_Thickness 30, Leg_Width 50
   - Base Cabinet: Width 915, Depth 610, Height 875, Toe_Kick_Height 100, Toe_Kick_Depth 75, Material_Thickness 19
   - Bookshelf: Width 915, Depth 305, Height 1830, Material_Thickness 19
8. CONSTRAINTS & ALIGNMENT:
   - Use `CREATE_DIMENSION` between `plane_a` and `plane_b` with `label_parameter` to drive reference planes.
   - Use `ALIGN` with `plane_a`, `target_ref` (element name), and `is_locked: true` to lock geometry faces to reference planes.

OUTPUT FORMAT (JSON only, no markdown, no prose):
{{
  ""intent"": ""create_geometry"",
  ""summary"": ""Concise 1-sentence description"",
  ""expected_components"": [""window_frame_left"", ""window_frame_right"", ""window_frame_top"", ""window_frame_bottom"", ""glazing_panel""],
  ""operations"": [
    {{
      ""operation"": ""CREATE_PARAMETER"",
      ""parameter_name"": ""Width"",
      ""parameter_type"": ""Length"",
      ""group_type"": ""Dimensions"",
      ""value"": 1200,
      ""unit"": ""mm""
    }},
    {{
      ""operation"": ""CREATE_REFERENCE_PLANE"",
      ""plane_name"": ""Left"",
      ""orientation"": ""X"",
      ""offset_mm"": -600,
      ""offset_expr"": ""-Width / 2"",
      ""driven_by_parameter"": ""Width""
    }},
    {{
      ""operation"": ""CREATE_DIMENSION"",
      ""plane_a"": ""Left"",
      ""plane_b"": ""Right"",
      ""label_parameter"": ""Width"",
      ""orientation"": ""X""
    }},
    {{
      ""operation"": ""CREATE_EXTRUSION"",
      ""role"": ""window_frame_left"",
      ""element_name"": ""Window_Frame_Left"",
      ""profile_type"": ""Rectangle"",
      ""is_void"": false,
      ""min_x_mm"": -600,
      ""min_x_expr"": ""-Width / 2"",
      ""max_x_mm"": -525,
      ""max_x_expr"": ""-Width / 2 + Frame_Width"",
      ""min_y_mm"": -50,
      ""min_y_expr"": ""-Frame_Depth / 2"",
      ""max_y_mm"": 50,
      ""max_y_expr"": ""Frame_Depth / 2"",
      ""start_z_mm"": 0,
      ""end_z_mm"": 1500,
      ""end_z_expr"": ""Height"",
      ""subcategory"": ""Frame/Mullion""
    }}
  ]
}}";

                parts.Add(new { text = $"{plannerSystemPrompt}\n\nUSER REQUEST: {userPrompt}" });

                string cleanModel = NormalizeModelName(modelName);
                List<string> availableModels = await GetAvailableModelsAsync(apiKey, cancellationToken);
                var candidateModels = BuildCandidateList(cleanModel, availableModels);

                for (int idx = 0; idx < candidateModels.Count; idx++)
                {
                    var targetModel = candidateModels[idx];
                    string jsonPayload = BuildRequestPayload(parts, 0.2);

                    try
                    {
                        string endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{targetModel}:generateContent?key={apiKey}";
                        using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                        HttpResponseMessage response = await _httpClient.PostAsync(endpoint, content, linkedCts.Token);
                        string body = await response.Content.ReadAsStringAsync();

                        if (response.IsSuccessStatusCode)
                        {
                            using (var doc = JsonDocument.Parse(body))
                            {
                                if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
                                    candidates.GetArrayLength() > 0 &&
                                    candidates[0].TryGetProperty("content", out var candContent) &&
                                    candContent.TryGetProperty("parts", out var candParts) &&
                                    candParts.GetArrayLength() > 0)
                                {
                                    string? rawText = null;
                                    foreach (var p in candParts.EnumerateArray())
                                    {
                                        bool isThought = p.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True;
                                        if (!isThought && p.TryGetProperty("text", out var tx))
                                        {
                                            rawText = tx.GetString();
                                            break;
                                        }
                                    }

                                    if (!string.IsNullOrWhiteSpace(rawText))
                                    {
                                        string textContent = rawText!.Trim();
                                        if (textContent.StartsWith("```json")) textContent = textContent.Substring(7);
                                        else if (textContent.StartsWith("```")) textContent = textContent.Substring(3);
                                        if (textContent.EndsWith("```")) textContent = textContent.Substring(0, textContent.Length - 3);
                                        textContent = textContent.Trim();

                                        int firstBrace = textContent.IndexOf('{');
                                        int lastBrace = textContent.LastIndexOf('}');
                                        if (firstBrace >= 0 && lastBrace > firstBrace)
                                        {
                                            textContent = textContent.Substring(firstBrace, lastBrace - firstBrace + 1);
                                        }

                                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                                        var plan = JsonSerializer.Deserialize<ExecutionPlan>(textContent, options);
                                        if (plan != null && plan.Operations != null && plan.Operations.Count > 0)
                                        {
                                            LastUsedModel = targetModel;
                                            return plan;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return GenerateDeterministicExecutionPlan(userPrompt, currentState);
        }

        /// <summary>
        /// Fallback deterministic copilot planner: handles creation, modification, addition, and flex requests without external LLM.
        /// Uses parametric expressions and N-division spacing formulas.
        /// </summary>
        public static ExecutionPlan GenerateDeterministicExecutionPlan(string userPrompt, FamilyState currentState)
        {
            string p = userPrompt?.ToLowerInvariant() ?? "";
            var plan = new ExecutionPlan();

            // 1. Modification check: "Make the frame 100 mm" or "Change width to 1400"
            if (p.Contains("make") || p.Contains("change") || p.Contains("set") || p.Contains("modify"))
            {
                plan.Intent = "modify_family";
                var matchMm = System.Text.RegularExpressions.Regex.Match(p, @"(\d+(\.\d+)?)(\s*mm)?");
                double newVal = matchMm.Success && double.TryParse(matchMm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsedVal)
                    ? parsedVal
                    : 100.0;

                string targetParam = "Frame_Width";
                if (p.Contains("width") && !p.Contains("frame")) targetParam = "Width";
                else if (p.Contains("height")) targetParam = "Height";
                else if (p.Contains("depth")) targetParam = "Frame_Depth";
                else if (p.Contains("glass") || p.Contains("glazing")) targetParam = "Glass_Thickness";

                plan.Summary = $"Modify parameter '{targetParam}' to {newVal:0} mm";
                plan.Operations.Add(new FamilyOperation
                {
                    Operation = FamilyOperationType.SET_PARAMETER,
                    ParameterName = targetParam,
                    Value = newVal,
                    Unit = "mm",
                    Summary = $"Set {targetParam} = {newVal:0} mm"
                });
                return plan;
            }

            // 2. Add Component check: "Add a 75 mm projecting sill"
            if (p.Contains("add") && (p.Contains("sill") || p.Contains("trim")))
            {
                plan.Intent = "add_component";
                plan.Summary = "Add projecting window sill to existing family";
                double sillProj = 75.0;
                double sillThk = 30.0;
                double sillW = 1300.0;

                var curW = currentState?.Parameters?.FirstOrDefault(x => x.Name.Equals("Width", StringComparison.OrdinalIgnoreCase));
                if (curW != null && curW.ValueNumeric.HasValue) sillW = curW.ValueNumeric.Value + 100.0;

                plan.Operations.Add(new FamilyOperation
                {
                    Operation = FamilyOperationType.CREATE_PARAMETER,
                    ParameterName = "Sill_Projection",
                    Value = sillProj,
                    ParameterType = "Length"
                });
                plan.Operations.Add(new FamilyOperation
                {
                    Operation = FamilyOperationType.CREATE_EXTRUSION,
                    Role = "window_sill",
                    ElementName = "Window_Sill",
                    ProfileType = "Rectangle",
                    MinX_mm = -sillW / 2.0,
                    MinX_Expr = "-Width / 2 - 50",
                    MaxX_mm = sillW / 2.0,
                    MaxX_Expr = "Width / 2 + 50",
                    MinY_mm = -50 - sillProj,
                    MinY_Expr = "-Frame_Depth / 2 - Sill_Projection",
                    MaxY_mm = 50,
                    MaxY_Expr = "Frame_Depth / 2",
                    StartZ_mm = -sillThk,
                    EndZ_mm = 0.0,
                    Subcategory = "Frame/Mullion",
                    MaterialParameter = "Frame_Material"
                });
                return plan;
            }

            // 3. Flex Test check: "Why isn't the width parameter flexing correctly?"
            if (p.Contains("flex") || p.Contains("why isn't") || p.Contains("not flexing") || p.Contains("diagnose"))
            {
                plan.Intent = "flex_test";
                string paramToFlex = p.Contains("height") ? "Height" : (p.Contains("frame") ? "Frame_Width" : "Width");
                plan.Summary = $"Inspect and flex test parameter '{paramToFlex}'";
                plan.Operations.Add(new FamilyOperation
                {
                    Operation = FamilyOperationType.FLEX_PARAMETER,
                    ParameterName = paramToFlex,
                    FlexTestValues = new List<double> { 900, 1200, 1500, 1800 },
                    Summary = $"Flex test '{paramToFlex}' at 900, 1200, 1500, 1800 mm"
                });
                return plan;
            }

            // 4. Creation check (Window benchmark or generic)
            plan.Intent = "create_geometry";
            double w = 1200, h = 1500, fW = 75, fD = 100, gT = 10;

            var allNumbers = System.Text.RegularExpressions.Regex.Matches(p, @"(\d{2,4})")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
                .ToList();

            if (allNumbers.Count >= 2)
            {
                w = allNumbers[0];
                h = allNumbers[1];
            }
            if (p.Contains("75")) fW = 75;
            if (p.Contains("100")) fD = 100;
            if (p.Contains("10")) gT = 10;

            plan.Summary = $"Create {w:0} x {h:0} mm parametric window with {fW:0} mm frame";
            plan.ExpectedComponents = new List<string>
            {
                "window_frame_left",
                "window_frame_right",
                "window_frame_top",
                "window_frame_bottom",
                "glazing_panel"
            };

            // Parameters
            plan.Operations.Add(new FamilyOperation { Operation = FamilyOperationType.CREATE_PARAMETER, ParameterName = "Width", Value = w, ParameterType = "Length" });
            plan.Operations.Add(new FamilyOperation { Operation = FamilyOperationType.CREATE_PARAMETER, ParameterName = "Height", Value = h, ParameterType = "Length" });
            plan.Operations.Add(new FamilyOperation { Operation = FamilyOperationType.CREATE_PARAMETER, ParameterName = "Frame_Width", Value = fW, ParameterType = "Length" });
            plan.Operations.Add(new FamilyOperation { Operation = FamilyOperationType.CREATE_PARAMETER, ParameterName = "Frame_Depth", Value = fD, ParameterType = "Length" });
            plan.Operations.Add(new FamilyOperation { Operation = FamilyOperationType.CREATE_PARAMETER, ParameterName = "Glass_Thickness", Value = gT, ParameterType = "Length" });

            // Reference Planes with parametric expressions
            double halfW = w / 2.0;
            plan.Operations.Add(new FamilyOperation { Operation = FamilyOperationType.CREATE_REFERENCE_PLANE, PlaneName = "Left", Orientation = "X", OffsetMm = -halfW, OffsetExpr = "-Width / 2", DrivenByParameter = "Width" });
            plan.Operations.Add(new FamilyOperation { Operation = FamilyOperationType.CREATE_REFERENCE_PLANE, PlaneName = "Right", Orientation = "X", OffsetMm = halfW, OffsetExpr = "Width / 2", DrivenByParameter = "Width" });
            plan.Operations.Add(new FamilyOperation { Operation = FamilyOperationType.CREATE_REFERENCE_PLANE, PlaneName = "Frame_Left_Inner", Orientation = "X", OffsetMm = -halfW + fW, OffsetExpr = "-Width / 2 + Frame_Width" });
            plan.Operations.Add(new FamilyOperation { Operation = FamilyOperationType.CREATE_REFERENCE_PLANE, PlaneName = "Frame_Right_Inner", Orientation = "X", OffsetMm = halfW - fW, OffsetExpr = "Width / 2 - Frame_Width" });
            plan.Operations.Add(new FamilyOperation { Operation = FamilyOperationType.CREATE_REFERENCE_PLANE, PlaneName = "Window_Head", Orientation = "Z", OffsetMm = h, OffsetExpr = "Height", DrivenByParameter = "Height" });
            plan.Operations.Add(new FamilyOperation { Operation = FamilyOperationType.CREATE_REFERENCE_PLANE, PlaneName = "Frame_Head_Inner", Orientation = "Z", OffsetMm = h - fW, OffsetExpr = "Height - Frame_Width" });
            plan.Operations.Add(new FamilyOperation { Operation = FamilyOperationType.CREATE_REFERENCE_PLANE, PlaneName = "Frame_Sill_Inner", Orientation = "Z", OffsetMm = fW, OffsetExpr = "Frame_Width" });

            // Dimensions & Constraints
            plan.Operations.Add(new FamilyOperation { Operation = FamilyOperationType.CREATE_DIMENSION, PlaneA = "Left", PlaneB = "Right", LabelParameter = "Width", Orientation = "X" });
            plan.Operations.Add(new FamilyOperation { Operation = FamilyOperationType.CREATE_DIMENSION, PlaneA = "Left", PlaneB = "Frame_Left_Inner", LabelParameter = "Frame_Width", Orientation = "X" });
            plan.Operations.Add(new FamilyOperation { Operation = FamilyOperationType.CREATE_DIMENSION, PlaneA = "Frame_Right_Inner", PlaneB = "Right", LabelParameter = "Frame_Width", Orientation = "X" });

            // 5 Distinct Extrusions using both literal mm and parametric expressions
            // Member 1: Left Jamb
            plan.Operations.Add(new FamilyOperation
            {
                Operation = FamilyOperationType.CREATE_EXTRUSION,
                Role = "window_frame_left",
                ElementName = "Window_Frame_Left",
                ProfileType = "Rectangle",
                MinX_mm = -halfW,
                MinX_Expr = "-Width / 2",
                MaxX_mm = -halfW + fW,
                MaxX_Expr = "-Width / 2 + Frame_Width",
                MinY_mm = -fD / 2.0,
                MinY_Expr = "-Frame_Depth / 2",
                MaxY_mm = fD / 2.0,
                MaxY_Expr = "Frame_Depth / 2",
                StartZ_mm = 0.0,
                EndZ_mm = h,
                EndZ_Expr = "Height",
                HeightParameter = "Height",
                Subcategory = "Frame/Mullion",
                MaterialParameter = "Frame_Material"
            });

            // Member 2: Right Jamb
            plan.Operations.Add(new FamilyOperation
            {
                Operation = FamilyOperationType.CREATE_EXTRUSION,
                Role = "window_frame_right",
                ElementName = "Window_Frame_Right",
                ProfileType = "Rectangle",
                MinX_mm = halfW - fW,
                MinX_Expr = "Width / 2 - Frame_Width",
                MaxX_mm = halfW,
                MaxX_Expr = "Width / 2",
                MinY_mm = -fD / 2.0,
                MinY_Expr = "-Frame_Depth / 2",
                MaxY_mm = fD / 2.0,
                MaxY_Expr = "Frame_Depth / 2",
                StartZ_mm = 0.0,
                EndZ_mm = h,
                EndZ_Expr = "Height",
                HeightParameter = "Height",
                Subcategory = "Frame/Mullion",
                MaterialParameter = "Frame_Material"
            });

            // Member 3: Bottom Sill Frame
            plan.Operations.Add(new FamilyOperation
            {
                Operation = FamilyOperationType.CREATE_EXTRUSION,
                Role = "window_frame_bottom",
                ElementName = "Window_Frame_Bottom",
                ProfileType = "Rectangle",
                MinX_mm = -halfW + fW,
                MinX_Expr = "-Width / 2 + Frame_Width",
                MaxX_mm = halfW - fW,
                MaxX_Expr = "Width / 2 - Frame_Width",
                MinY_mm = -fD / 2.0,
                MinY_Expr = "-Frame_Depth / 2",
                MaxY_mm = fD / 2.0,
                MaxY_Expr = "Frame_Depth / 2",
                StartZ_mm = 0.0,
                EndZ_mm = fW,
                EndZ_Expr = "Frame_Width",
                Subcategory = "Frame/Mullion",
                MaterialParameter = "Frame_Material"
            });

            // Member 4: Top Head Frame
            plan.Operations.Add(new FamilyOperation
            {
                Operation = FamilyOperationType.CREATE_EXTRUSION,
                Role = "window_frame_top",
                ElementName = "Window_Frame_Top",
                ProfileType = "Rectangle",
                MinX_mm = -halfW + fW,
                MinX_Expr = "-Width / 2 + Frame_Width",
                MaxX_mm = halfW - fW,
                MaxX_Expr = "Width / 2 - Frame_Width",
                MinY_mm = -fD / 2.0,
                MinY_Expr = "-Frame_Depth / 2",
                MaxY_mm = fD / 2.0,
                MaxY_Expr = "Frame_Depth / 2",
                StartZ_mm = h - fW,
                StartZ_Expr = "Height - Frame_Width",
                EndZ_mm = h,
                EndZ_Expr = "Height",
                Subcategory = "Frame/Mullion",
                MaterialParameter = "Frame_Material"
            });

            // Member 5: Central Glazing Panel
            plan.Operations.Add(new FamilyOperation
            {
                Operation = FamilyOperationType.CREATE_EXTRUSION,
                Role = "glazing_panel",
                ElementName = "Glazing_Panel",
                ProfileType = "Rectangle",
                MinX_mm = -halfW + fW,
                MinX_Expr = "-Width / 2 + Frame_Width",
                MaxX_mm = halfW - fW,
                MaxX_Expr = "Width / 2 - Frame_Width",
                MinY_mm = -gT / 2.0,
                MinY_Expr = "-Glass_Thickness / 2",
                MaxY_mm = gT / 2.0,
                MaxY_Expr = "Glass_Thickness / 2",
                StartZ_mm = fW,
                StartZ_Expr = "Frame_Width",
                EndZ_mm = h - fW,
                EndZ_Expr = "Height - Frame_Width",
                Subcategory = "Glazing",
                MaterialParameter = "Glass_Material"
            });

            // Face Alignments locking Jambs to Left/Right Reference Planes
            plan.Operations.Add(new FamilyOperation
            {
                Operation = FamilyOperationType.ALIGN,
                PlaneA = "Left",
                TargetRef = "Window_Frame_Left",
                IsLocked = true,
                Summary = "Lock Left Jamb exterior face to Left Reference Plane"
            });
            plan.Operations.Add(new FamilyOperation
            {
                Operation = FamilyOperationType.ALIGN,
                PlaneA = "Right",
                TargetRef = "Window_Frame_Right",
                IsLocked = true,
                Summary = "Lock Right Jamb exterior face to Right Reference Plane"
            });

            return plan;
        }
    }
}
