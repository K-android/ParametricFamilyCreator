using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.Services
{
    /// <summary>
    /// Service responsible for communicating with Google's Gemini Vision API using Structured JSON Output
    /// or providing a deterministic rule-based fallback for generating valid FamilySchema models.
    /// </summary>
    public class GeminiVisionService
    {
        private readonly HttpClient _httpClient;
        public const string DefaultGeminiModel = "gemini-3.8-flash";

        /// <summary>
        /// Real-time token consumption metrics from the last Gemini API call.
        /// </summary>
        public string? LastUsageSummary { get; private set; }

        public GeminiVisionService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        }

        /// <summary>
        /// Optional live status callback for UI progress reporting during AI reasoning and PDF extraction.
        /// </summary>
        public Action<string>? OnStatusUpdate { get; set; }

        private void ReportStatus(string message)
        {
            try { OnStatusUpdate?.Invoke(message); } catch { }
        }

        /// <summary>
        /// Two-pass multimodal reasoning system instruction for Gemini 3 & 4 Vision.
        /// Explicitly defines the exact JSON schema and requires non-empty 3D extrusions.
        /// </summary>
        public const string FamilyForgeSystemInstruction = 
@"You are a Principal BIM/VDC Lead and Autodesk Revit API Parametric Content Specialist.
Your objective is to analyze architectural cut-sheets (PDF or image), submittal CAD prints, or user prompts (or both),
and synthesize a complete, millimeter-accurate, non-empty 3D Revit Family specification JSON.

HYBRID PARAMETER & GEOMETRY EXTRACTION RULES:
1. INPUT PRIORITY:
   - If the user provides BOTH a prompt and a PDF/image: use the PDF/image for full component geometry and dimensions, while letting explicit numbers in the user's prompt override specific dimensions.
   - If the user provides ONLY a PDF/image (empty or brief prompt): extract the product name, category, overall Width, Depth, Height, thickness, frame/panel/leg profiles, and hardware directly from the cut-sheet drawing and tables.
   - If the user provides ONLY a text prompt: synthesize the complete architectural component and infer standard ergonomic/architectural dimensions for any unspecified values.
2. UNIT CONVERSION:
   - Convert all dimensions to millimeters (1 inch = 25.4 mm, 1 foot = 304.8 mm, 1 cm = 10 mm, 1 m = 1000 mm).
3. NON-EMPTY 3D GEOMETRY REQUIREMENT (CRITICAL):
   - 'host_extrusions' MUST NEVER BE EMPTY. You MUST decompose the component into 2 to 10 realistic 3D solid extrusions (e.g. frames, panels, glazing, tabletop, legs, shelves, baseplates, shafts, capitals) with explicit non-zero bounding boxes (min_x_mm < max_x_mm, min_y_mm < max_y_mm, start_z_mm < end_z_mm).
   - Center-symmetric families have origin (0,0,0) at Center Left/Right and Center Front/Back on Reference Level (min_x_mm = -Width/2, max_x_mm = +Width/2, min_y_mm = -Depth/2, max_y_mm = +Depth/2).
4. EXACT JSON SCHEMA FORMAT (USE THESE EXACT SNAKE_CASE FIELD NAMES):
{
  ""master_family_name"": ""Parametric_Component_Name"",
  ""family_category"": ""Doors | Windows | Furniture | Casework | Columns | Generic Models | Specialty Equipment"",
  ""template_name"": ""Metric Generic Model.rft"",
  ""master_parameters"": [
    { ""name"": ""Width"", ""data_type"": ""Length"", ""default_value_mm"": 1200, ""is_instance"": false, ""formula"": null },
    { ""name"": ""Depth"", ""data_type"": ""Length"", ""default_value_mm"": 600, ""is_instance"": false, ""formula"": null },
    { ""name"": ""Height"", ""data_type"": ""Length"", ""default_value_mm"": 750, ""is_instance"": false, ""formula"": null },
    { ""name"": ""Thickness"", ""data_type"": ""Length"", ""default_value_mm"": 30, ""is_instance"": false, ""formula"": null }
  ],
  ""reference_planes"": [
    { ""name"": ""Left_Plane"", ""orientation"": ""X"", ""offset_from_origin_mm"": -600, ""driven_by_parameter"": ""Width"" },
    { ""name"": ""Right_Plane"", ""orientation"": ""X"", ""offset_from_origin_mm"": 600, ""driven_by_parameter"": ""Width"" },
    { ""name"": ""Front_Plane"", ""orientation"": ""Y"", ""offset_from_origin_mm"": -300, ""driven_by_parameter"": ""Depth"" },
    { ""name"": ""Back_Plane"", ""orientation"": ""Y"", ""offset_from_origin_mm"": 300, ""driven_by_parameter"": ""Depth"" },
    { ""name"": ""Top_Plane"", ""orientation"": ""Z"", ""offset_from_origin_mm"": 750, ""driven_by_parameter"": ""Height"" }
  ],
  ""host_extrusions"": [
    {
      ""name"": ""Main_Panel_Or_Frame"",
      ""profile_type"": ""Rectangle | Cylinder | HollowBox"",
      ""min_x_mm"": -600,
      ""max_x_mm"": 600,
      ""min_y_mm"": -300,
      ""max_y_mm"": 300,
      ""radius_mm"": 0,
      ""wall_thickness_mm"": 0,
      ""start_z_mm"": 720,
      ""end_z_mm"": 750,
      ""width_parameter"": ""Width"",
      ""depth_parameter"": ""Depth"",
      ""height_parameter"": ""Height""
    }
  ],
  ""nested_families"": [
    {
      ""nested_family_name"": ""SubComponent_Hardware_Or_Leg"",
      ""template_name"": ""Metric Generic Model.rft"",
      ""is_shared"": true,
      ""visibility_parameter"": null,
      ""sub_parameters"": [
        { ""name"": ""Sub_Height"", ""data_type"": ""Length"", ""default_value_mm"": 720, ""is_instance"": false }
      ],
      ""sub_extrusions"": [
        {
          ""name"": ""Sub_Solid"",
          ""profile_type"": ""Cylinder"",
          ""min_x_mm"": -25,
          ""max_x_mm"": 25,
          ""min_y_mm"": -25,
          ""max_y_mm"": 25,
          ""radius_mm"": 25,
          ""start_z_mm"": 0,
          ""end_z_mm"": 720,
          ""height_parameter"": ""Sub_Height""
        }
      ],
      ""placement_offset_mm"": { ""x"": -550, ""y"": -250, ""z"": 0 },
      ""parameter_bindings"": { ""Sub_Height"": ""Height"" }
    }
  ]
}
RETURN ONLY VALID JSON matching this exact structure without markdown commentary.";

        /// <summary>
        /// Sends prompt and optional PDF/image to Gemini 3/4 API and receives a validated, non-empty FamilySchema.
        /// </summary>
        public async Task<FamilySchema> GenerateFamilySchemaAsync(
            string apiKey, 
            string userPrompt, 
            string? imageFilePath = null, 
            string modelName = DefaultGeminiModel,
            double temperature = 0.2,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return GenerateDeterministicFallback(userPrompt, imageFilePath);
            }

            var parts = new List<object>();
            string extractedDocContext = "";

            // 1. Attach PDF / Image for multimodal vision AND pre-extract text from PDF cut-sheets
            if (!string.IsNullOrEmpty(imageFilePath) && File.Exists(imageFilePath))
            {
                string ext = Path.GetExtension(imageFilePath).ToLowerInvariant();
                ReportStatus($"📄 Reading attached {(ext == ".pdf" ? "PDF cut-sheet" : "reference image")}: {Path.GetFileName(imageFilePath)}...");

                byte[] imageBytes = OptimizeImageForVision(imageFilePath!, out string mimeType);
                parts.Add(new
                {
                    inline_data = new
                    {
                        mime_type = mimeType,
                        data = Convert.ToBase64String(imageBytes)
                    }
                });

                if (ext == ".pdf")
                {
                    string pdfText = ExtractTextFromPdf(imageFilePath!);
                    if (!string.IsNullOrWhiteSpace(pdfText))
                    {
                        string snippet = pdfText.Length > 4000 ? pdfText.Substring(0, 4000) : pdfText;
                        extractedDocContext = $"\n\n[EXTRACTED PDF CUT-SHEET TEXT & DIMENSIONS]:\n{snippet}";
                    }
                }
                else
                {
                    extractedDocContext = $"\n\n[ATTACHED IMAGE FILENAME]: {Path.GetFileNameWithoutExtension(imageFilePath)}";
                }
            }

            // 2. Construct rich hybrid prompt (works with PDF-only, Prompt-only, or Both)
            string effectivePrompt = string.IsNullOrWhiteSpace(userPrompt)
                ? "Analyze the attached cut-sheet / image in complete detail. Identify the architectural product type, all overall and component dimensions (converting to mm), and generate the complete 3D parametric Revit family with all solid extrusions, reference planes, master parameters, and nested sub-components."
                : userPrompt.Trim();

            parts.Add(new
            {
                text = $"Generate a complete, non-empty 3D Revit Parametric Family JSON specification.\nUser Design Request: {effectivePrompt}{extractedDocContext}\n\nIMPORTANT: Populate 'host_extrusions' with all 3D solid parts (min_x_mm, max_x_mm, min_y_mm, max_y_mm, start_z_mm, end_z_mm in mm) and 'master_parameters' with non-zero default_value_mm."
            });

            // 3. Clean up and normalize model name
            string cleanModel = NormalizeModelName(modelName);

            ReportStatus($"🔍 Checking available Gemini 3 / 4 models for your API key...");
            List<string> availableModels = await GetAvailableModelsAsync(apiKey, cancellationToken);

            var errors = new List<string>();
            var candidateModels = BuildCandidateList(cleanModel, availableModels);

            for (int idx = 0; idx < candidateModels.Count; idx++)
            {
                var targetModel = candidateModels[idx];
                ReportStatus($"🧠 AI Active Reasoning & 3D BIM Modelling with '{targetModel}' (attempt {idx + 1}/{candidateModels.Count})...");

                string jsonPayload = BuildRequestPayload(parts, temperature);
                try
                {
                    string endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{targetModel}:generateContent?key={apiKey}";
                    using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                    HttpResponseMessage response = await _httpClient.PostAsync(endpoint, content, linkedCts.Token);
                    string body = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        try
                        {
                            ReportStatus($"🔧 Parsing & verifying 3D geometry from '{targetModel}'...");
                            var schema = ParseGeminiResponse(body, userPrompt, imageFilePath);
                            LastUsedModel = targetModel;
                            ReportStatus($"✅ AI Synthesis complete ({schema.HostExtrusions.Count} host extrusions, {schema.NestedFamilies.Count} nested components).");
                            return schema;
                        }
                        catch (Exception parseEx)
                        {
                            errors.Add($"[{targetModel}] responded but output was unusable: {parseEx.Message}");
                            continue;
                        }
                    }

                    string apiMessage = ExtractApiErrorMessage(body);

                    if (IsAuthError(response.StatusCode, body))
                    {
                        throw new GeminiAuthException(
                            $"Gemini API key rejected ({(int)response.StatusCode}): {apiMessage}\n\n" +
                            "Check your API key in Settings (get a free key at https://aistudio.google.com/apikey).");
                    }

                    errors.Add($"[{targetModel}] HTTP {(int)response.StatusCode} {DescribeStatus((int)response.StatusCode)}: {apiMessage}");
                }
                catch (GeminiAuthException)
                {
                    throw;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    errors.Add($"[{targetModel}] timed out after 120s (model overloaded or very large PDF).");
                }
                catch (HttpRequestException httpEx)
                {
                    errors.Add($"[{targetModel}] network error: {httpEx.Message}");
                }
            }

            throw new InvalidOperationException(
                "All Gemini 3 / Gemini 4 models failed:\n  • " + string.Join("\n  • ", errors));
        }

        /// <summary>Model that produced the last successful AI schema.</summary>
        public string? LastUsedModel { get; private set; }

        private sealed class GeminiAuthException : InvalidOperationException
        {
            public GeminiAuthException(string message) : base(message) { }
        }

        private static string NormalizeModelName(string? modelName)
        {
            string m = string.IsNullOrWhiteSpace(modelName) ? DefaultGeminiModel : modelName!.Trim();
            int paren = m.IndexOf(' ');
            if (paren > 0) m = m.Substring(0, paren);
            if (m.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) m = m.Substring(7);
            if (m.Equals("gemini-3.8", StringComparison.OrdinalIgnoreCase) || m.Equals("gemini-3", StringComparison.OrdinalIgnoreCase))
                m = "gemini-3.8-flash";
            else if (m.Equals("gemini-3.6", StringComparison.OrdinalIgnoreCase))
                m = "gemini-3.6-flash";
            else if (m.Equals("gemini-4", StringComparison.OrdinalIgnoreCase))
                m = "gemini-4-argon";
            else if (m.Contains("1.5") || m.Contains("2.0") || m.Contains("2.5"))
                m = "gemini-3.8-flash";
            return m;
        }

        private static readonly Dictionary<string, List<string>> _modelCache = new Dictionary<string, List<string>>();

        private async Task<List<string>> GetAvailableModelsAsync(string apiKey, CancellationToken ct)
        {
            lock (_modelCache)
            {
                if (_modelCache.TryGetValue(apiKey, out var cached)) return cached;
            }

            var result = new List<string>();
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                var resp = await _httpClient.GetAsync(
                    $"https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000&key={apiKey}", linked.Token);
                string body = await resp.Content.ReadAsStringAsync();

                if (IsAuthError(resp.StatusCode, body))
                {
                    throw new GeminiAuthException(
                        $"Gemini API key rejected ({(int)resp.StatusCode}): {ExtractApiErrorMessage(body)}\n\n" +
                        "Check your API key in Settings (get a free key at https://aistudio.google.com/apikey).");
                }
                if (!resp.IsSuccessStatusCode) return result;

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("models", out var models))
                {
                    foreach (var m in models.EnumerateArray())
                    {
                        string name = m.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                        if (name.StartsWith("models/")) name = name.Substring(7);
                        bool canGenerate = false;
                        if (m.TryGetProperty("supportedGenerationMethods", out var methods))
                        {
                            foreach (var meth in methods.EnumerateArray())
                            {
                                if (meth.GetString() == "generateContent") { canGenerate = true; break; }
                            }
                        }
                        if (canGenerate && (name.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase) ||
                                            name.StartsWith("gemini-4", StringComparison.OrdinalIgnoreCase)))
                        {
                            result.Add(name);
                        }
                    }
                }

                lock (_modelCache) { _modelCache[apiKey] = result; }
            }
            catch (GeminiAuthException) { throw; }
            catch { /* discovery is best-effort */ }
            return result;
        }

        private static readonly string[] Gemini3And4Models =
        {
            "gemini-3.8-flash",
            "gemini-4-argon",
            "gemini-3.1-flash-lite",
            "gemini-3-flash-preview",
            "gemini-3-pro-preview",
            "gemini-3.6-flash"
        };

        private static bool IsUnsuitableModel(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("tts") || n.Contains("image") || n.Contains("live") || n.Contains("audio") ||
                   n.Contains("embedding") || n.Contains("robotics") || n.Contains("computer-use") || n.Contains("gemma") || n.Contains("cyber");
        }

        private static List<string> BuildCandidateList(string userModel, List<string> available)
        {
            var list = new List<string> { userModel };

            foreach (var m in Gemini3And4Models.Where(p => available.Contains(p, StringComparer.OrdinalIgnoreCase))
                         .Concat(available.Where(a => !IsUnsuitableModel(a)).OrderByDescending(a => a)))
            {
                if (!list.Contains(m, StringComparer.OrdinalIgnoreCase))
                    list.Add(m);
            }

            foreach (var m in Gemini3And4Models)
            {
                if (list.Count >= 5) break;
                if (!list.Contains(m, StringComparer.OrdinalIgnoreCase))
                    list.Add(m);
            }
            return list;
        }

        private static string BuildRequestPayload(List<object> parts, double temperature)
        {
            var generationConfig = new
            {
                temperature = ClampDouble(temperature, 0.0, 1.0),
                responseMimeType = "application/json",
                maxOutputTokens = 16384
            };

            var requestBody = new
            {
                systemInstruction = new { parts = new[] { new { text = FamilyForgeSystemInstruction } } },
                contents = new[] { new { role = "user", parts = parts.ToArray() } },
                generationConfig
            };
            return JsonSerializer.Serialize(requestBody);
        }

        private static bool IsAuthError(System.Net.HttpStatusCode code, string body)
        {
            if (code == System.Net.HttpStatusCode.Unauthorized || code == System.Net.HttpStatusCode.Forbidden) return true;
            return code == System.Net.HttpStatusCode.BadRequest &&
                   (body.Contains("API_KEY_INVALID") || body.Contains("API key not valid") || body.Contains("API key expired"));
        }

        private static string ExtractApiErrorMessage(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg))
                {
                    string s = msg.GetString() ?? "";
                    return s.Length > 220 ? s.Substring(0, 220) + "…" : s;
                }
            }
            catch { }
            return body.Length > 220 ? body.Substring(0, 220) + "…" : body;
        }

        private static string DescribeStatus(int code) => code switch
        {
            404 => "(model not found / retired)",
            429 => "(quota or rate limit exceeded)",
            500 => "(Google internal error)",
            503 => "(model overloaded / high demand)",
            504 => "(Google gateway timeout)",
            _ => ""
        };

        private FamilySchema ParseGeminiResponse(string rawJson, string userPrompt = "", string? imageFilePath = null)
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("usageMetadata", out var usage))
            {
                int pt = usage.TryGetProperty("promptTokenCount", out var pProp) ? pProp.GetInt32() : 0;
                int ct = usage.TryGetProperty("candidatesTokenCount", out var cProp) ? cProp.GetInt32() : 0;
                int tt = usage.TryGetProperty("totalTokenCount", out var tProp) ? tProp.GetInt32() : 0;
                LastUsageSummary = $"Total: {tt} tokens (Input: {pt}, Output: {ct})";
            }
            else
            {
                LastUsageSummary = null;
            }

            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var firstCandidate = candidates[0];
                string finishReason = firstCandidate.TryGetProperty("finishReason", out var fr) ? (fr.GetString() ?? "") : "";
                if (finishReason == "MAX_TOKENS")
                    throw new InvalidOperationException("output was truncated (MAX_TOKENS) - JSON incomplete");
                if (finishReason == "SAFETY" || finishReason == "RECITATION" || finishReason == "PROHIBITED_CONTENT")
                    throw new InvalidOperationException($"response blocked by Google ({finishReason})");

                if (firstCandidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0)
                {
                    // Skip "thought" parts emitted by thinking models; use the first real text part.
                    string? found = null;
                    foreach (var p in parts.EnumerateArray())
                    {
                        bool isThought = p.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True;
                        if (!isThought && p.TryGetProperty("text", out var tx)) { found = tx.GetString(); break; }
                    }
                    string textContent = found ?? "{}";

                    textContent = textContent.Trim();
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
                    var schema = JsonSerializer.Deserialize<FamilySchema>(textContent, options) ?? new FamilySchema();

                    // Also map alternative/camelCase property names if the AI model used them
                    using (var schemaDoc = JsonDocument.Parse(textContent))
                    {
                        MapAlternativeJsonFields(schemaDoc.RootElement, schema);
                    }

                    // Ensure non-empty geometry: repair bounding boxes and synthesize extrusions from AI parameters if needed
                    EnsureNonEmptyGeometry(schema, userPrompt, imageFilePath, textContent);

                    return schema;
                }
            }

            throw new InvalidOperationException("Failed to extract valid FamilySchema from Gemini response.");
        }

        /// <summary>
        /// Captures alternative/camelCase JSON keys if a model returned camelCase or shorthand names.
        /// </summary>
        private static void MapAlternativeJsonFields(JsonElement root, FamilySchema schema)
        {
            if (root.ValueKind != JsonValueKind.Object) return;

            string? TryStr(JsonElement el, params string[] keys)
            {
                foreach (var k in keys)
                {
                    foreach (var prop in el.EnumerateObject())
                    {
                        if (prop.Name.Equals(k, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                            return prop.Value.GetString();
                    }
                }
                return null;
            }

            double? TryNum(JsonElement el, params string[] keys)
            {
                foreach (var k in keys)
                {
                    foreach (var prop in el.EnumerateObject())
                    {
                        if (prop.Name.Equals(k, StringComparison.OrdinalIgnoreCase))
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out double d)) return d;
                            if (prop.Value.ValueKind == JsonValueKind.String && double.TryParse(prop.Value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ds)) return ds;
                        }
                    }
                }
                return null;
            }

            if (schema.MasterFamilyName == "ParametricHostFamily" || string.IsNullOrWhiteSpace(schema.MasterFamilyName))
            {
                string? name = TryStr(root, "masterFamilyName", "family_name", "familyName", "name", "product_name", "productName", "title");
                if (!string.IsNullOrWhiteSpace(name)) schema.MasterFamilyName = Regex.Replace(name!.Trim(), @"[^\w]", "_");
            }

            if (schema.FamilyCategory == "Generic Models" || string.IsNullOrWhiteSpace(schema.FamilyCategory))
            {
                string? cat = TryStr(root, "familyCategory", "category", "revit_category", "revitCategory", "type");
                if (!string.IsNullOrWhiteSpace(cat)) schema.FamilyCategory = cat!;
            }

            // Map parameters if empty
            if (schema.MasterParameters.Count == 0)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name.Equals("masterParameters", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Equals("parameters", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Equals("family_parameters", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Equals("dimensions", StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in prop.Value.EnumerateArray())
                            {
                                if (item.ValueKind != JsonValueKind.Object) continue;
                                string? pName = TryStr(item, "name", "parameter_name", "parameterName", "label");
                                double? pVal = TryNum(item, "default_value_mm", "defaultValueMm", "default_value", "defaultValue", "value_mm", "valueMm", "value");
                                if (!string.IsNullOrWhiteSpace(pName) && pVal.HasValue && pVal.Value > 0)
                                {
                                    schema.MasterParameters.Add(new MasterParameterDef
                                    {
                                        Name = Regex.Replace(pName!.Trim(), @"[^\w]", "_"),
                                        DataType = TryStr(item, "data_type", "dataType", "type") ?? "Length",
                                        DefaultValueMm = pVal.Value
                                    });
                                }
                            }
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var kv in prop.Value.EnumerateObject())
                            {
                                if (kv.Value.ValueKind == JsonValueKind.Number && kv.Value.TryGetDouble(out double d) && d > 0)
                                {
                                    schema.MasterParameters.Add(new MasterParameterDef
                                    {
                                        Name = Regex.Replace(kv.Name.Trim(), @"[^\w]", "_"),
                                        DataType = "Length",
                                        DefaultValueMm = d
                                    });
                                }
                            }
                        }
                    }
                }
            }

            // Map extrusions if empty
            if (schema.HostExtrusions.Count == 0)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name.Equals("hostExtrusions", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Equals("extrusions", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Equals("geometry", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Equals("solids", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Equals("elements", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Equals("parts", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Equals("components", StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in prop.Value.EnumerateArray())
                            {
                                if (item.ValueKind != JsonValueKind.Object) continue;
                                double? w = TryNum(item, "width_mm", "widthMm", "width", "w");
                                double? d = TryNum(item, "depth_mm", "depthMm", "depth", "d", "length_mm", "length");
                                double? h = TryNum(item, "height_mm", "heightMm", "height", "h", "thickness_mm", "thickness");

                                double minX = TryNum(item, "min_x_mm", "minXMm", "min_x", "minX", "x_min") ?? (w.HasValue ? -w.Value / 2.0 : 0);
                                double maxX = TryNum(item, "max_x_mm", "maxXMm", "max_x", "maxX", "x_max") ?? (w.HasValue ? w.Value / 2.0 : 0);
                                double minY = TryNum(item, "min_y_mm", "minYMm", "min_y", "minY", "y_min") ?? (d.HasValue ? -d.Value / 2.0 : 0);
                                double maxY = TryNum(item, "max_y_mm", "maxYMm", "max_y", "maxY", "y_max") ?? (d.HasValue ? d.Value / 2.0 : 0);
                                double startZ = TryNum(item, "start_z_mm", "startZMm", "start_z", "startZ", "z_min", "base_z", "bottom_z") ?? 0;
                                double endZ = TryNum(item, "end_z_mm", "endZMm", "end_z", "endZ", "z_max", "top_z") ?? (h.HasValue ? startZ + h.Value : 0);

                                if (maxX > minX || maxY > minY || endZ > startZ)
                                {
                                    schema.HostExtrusions.Add(new ExtrusionDef
                                    {
                                        Name = TryStr(item, "name", "id", "label", "part") ?? $"Part_{schema.HostExtrusions.Count + 1}",
                                        ProfileType = TryStr(item, "profile_type", "profileType", "profile", "shape") ?? "Rectangle",
                                        MinX_mm = minX,
                                        MaxX_mm = maxX,
                                        MinY_mm = minY,
                                        MaxY_mm = maxY,
                                        Radius_mm = TryNum(item, "radius_mm", "radiusMm", "radius", "r") ?? 0,
                                        WallThickness_mm = TryNum(item, "wall_thickness_mm", "wallThicknessMm", "wall_thickness", "wallThickness") ?? 0,
                                        StartZ_mm = startZ,
                                        EndZ_mm = endZ > startZ ? endZ : startZ + 50
                                    });
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Guarantees that a schema returned by Gemini NEVER has empty geometry or zero-sized extrusions.
        /// If the model extracted dimensions/parameters or category but omitted extrusion coordinates,
        /// synthesizes complete 3D parametric geometry from the AI-extracted dimensions.
        /// </summary>
        private void EnsureNonEmptyGeometry(FamilySchema schema, string userPrompt, string? imageFilePath, string rawAiJson)
        {
            double? FindParam(params string[] keywords)
            {
                foreach (var kw in keywords)
                {
                    var p = schema.MasterParameters.FirstOrDefault(m => m.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 && m.DefaultValueMm > 0);
                    if (p != null) return p.DefaultValueMm;
                }
                return null;
            }

            double aiW = FindParam("width", "length", "span") ?? 0;
            double aiD = FindParam("depth", "projection") ?? 0;
            double aiH = FindParam("height", "tall") ?? 0;
            double aiT = FindParam("thick", "thk", "panel", "top", "frame") ?? 0;

            // Repair any extrusion that has 0 X/Y span using the AI's master parameters
            foreach (var ext in schema.HostExtrusions)
            {
                if (Math.Abs(ext.MaxX_mm - ext.MinX_mm) < 1.0)
                {
                    double w = ext.Radius_mm > 0 ? ext.Radius_mm * 2 : (aiW > 0 ? aiW : 1000);
                    ext.MinX_mm = -w / 2.0;
                    ext.MaxX_mm = w / 2.0;
                }
                if (Math.Abs(ext.MaxY_mm - ext.MinY_mm) < 1.0)
                {
                    double d = ext.Radius_mm > 0 ? ext.Radius_mm * 2 : (aiD > 0 ? aiD : 600);
                    ext.MinY_mm = -d / 2.0;
                    ext.MaxY_mm = d / 2.0;
                }
                if (ext.EndZ_mm <= ext.StartZ_mm)
                {
                    ext.EndZ_mm = ext.StartZ_mm + (aiH > 0 ? aiH : 50);
                }
            }

            // If HostExtrusions AND NestedFamilies are still empty, build complete 3D geometry from the AI's extracted parameters + cut-sheet
            if (schema.HostExtrusions.Count == 0 && schema.NestedFamilies.Count == 0)
            {
                string combinedHint = $"{userPrompt} {schema.MasterFamilyName} {schema.FamilyCategory} {rawAiJson}";
                var enriched = GenerateDeterministicFallback(combinedHint, imageFilePath);

                // Override with any explicit AI-extracted dimensions
                if (aiW >= 50 || aiD >= 50 || aiH >= 50)
                {
                    string cat = DetectCategory($"{userPrompt} {schema.MasterFamilyName} {schema.FamilyCategory}") ?? "generic";
                    var defs = CategoryDefaults[cat];
                    double w = aiW >= 50 ? aiW : defs.W;
                    double d = aiD >= 50 ? aiD : defs.D;
                    double h = aiH >= 50 ? aiH : defs.H;
                    double t = aiT >= 2 ? aiT : defs.T;

                    enriched = cat switch
                    {
                        "door" => CreateDoorSchema(w, h, t),
                        "window" => CreateWindowSchema(w, h, d),
                        "chair" => CreateChairSchema(w, d, h, t),
                        "table" => CreateWorkstationSchema(w, d, h, t),
                        "casework" => CreateCaseworkSchema(w, d, h, t),
                        "baseplate" => CreateBaseplateJointSchema(w, d, t, h),
                        "canopy" => CreateCanopySchema(w, d, t, h),
                        "column" => CreateColumnSchema(w, d, h),
                        _ => CreateDynamicUniversalSchema(schema.MasterFamilyName, w, d, h, t)
                    };
                }

                if (!string.IsNullOrWhiteSpace(schema.MasterFamilyName) && schema.MasterFamilyName != "ParametricHostFamily")
                    enriched.MasterFamilyName = schema.MasterFamilyName;
                if (!string.IsNullOrWhiteSpace(schema.FamilyCategory) && schema.FamilyCategory != "Generic Models")
                    enriched.FamilyCategory = schema.FamilyCategory;
                if (schema.MasterParameters.Count == 0)
                    schema.MasterParameters = enriched.MasterParameters;
                if (schema.ReferencePlanes.Count == 0)
                    schema.ReferencePlanes = enriched.ReferencePlanes;

                schema.HostExtrusions = enriched.HostExtrusions;
                schema.NestedFamilies = enriched.NestedFamilies;
            }
        }

        /// <summary>
        /// Robust deterministic local rule-based engine.
        /// Synthesizes architectural components from prompts, PDF cut-sheets, and images offline without an API key.
        /// </summary>
        public FamilySchema GenerateDeterministicFallback(string prompt, string? imageOrPdfPath = null)
        {
            prompt ??= "";
            string docText = "";
            var report = new StringBuilder();

            if (!string.IsNullOrEmpty(imageOrPdfPath) && File.Exists(imageOrPdfPath))
            {
                string ext = Path.GetExtension(imageOrPdfPath).ToLowerInvariant();
                if (ext == ".pdf")
                {
                    docText = ExtractTextFromPdf(imageOrPdfPath!);
                    if (docText.Trim().Length < 20)
                    {
                        report.AppendLine("⚠ PDF contains no extractable text (scanned image, outlined CAD text, or custom font encoding). " +
                                          "Offline mode can't read it; use AI Synthesis or type key dimensions in the prompt.");
                    }
                    else
                    {
                        report.AppendLine($"PDF text extracted: {docText.Length} chars.");
                    }
                }
                else
                {
                    docText = ExtractTextFromImageFilename(imageOrPdfPath!);
                    report.AppendLine("Image attached: offline mode can only read its filename (pixels need AI Synthesis).");
                }
            }

            // Category: prompt wins; only consult the document if the prompt doesn't name a type.
            string category = DetectCategory(prompt) ?? DetectCategory(docText) ?? "generic";
            report.AppendLine($"Detected type: {category}");

            var d = CategoryDefaults[category];
            var promptDims = ParseDimensions(prompt, category);
            var docDims = ParseDimensions(docText, category);

            double w = Pick("Width", promptDims.W, docDims.W, d.W, d.MinW, d.MaxW, report);
            double dep = Pick("Depth", promptDims.D, docDims.D, d.D, d.MinD, d.MaxD, report);
            double h = Pick("Height", promptDims.H, docDims.H, d.H, d.MinH, d.MaxH, report);
            double t = Pick("Thickness", promptDims.T, docDims.T, d.T, 2, Math.Max(d.T * 6, 200), report);

            LastExtractionReport = report.ToString().TrimEnd();

            string nameSource = !string.IsNullOrWhiteSpace(prompt) ? prompt : docText;
            switch (category)
            {
                case "door": return CreateDoorSchema(w, h, t);
                case "window": return CreateWindowSchema(w, h, dep);
                case "chair": return CreateChairSchema(w, dep, h, t);
                case "table": return CreateWorkstationSchema(w, dep, h, t);
                case "casework": return CreateCaseworkSchema(w, dep, h, t);
                case "baseplate": return CreateBaseplateJointSchema(w, dep, t, h);
                case "canopy": return CreateCanopySchema(w, dep, t, h);
                case "column": return CreateColumnSchema(w, dep, h);
                default: return CreateDynamicUniversalSchema(nameSource, w, dep, h, t);
            }
        }

        /// <summary>Human-readable log of what the offline engine extracted and why.</summary>
        public string? LastExtractionReport { get; private set; }

        private sealed class DimDefaults
        {
            public double W, D, H, T, MinW, MaxW, MinD, MaxD, MinH, MaxH;
            public DimDefaults(double w, double d, double h, double t, double minW, double maxW, double minD, double maxD, double minH, double maxH)
            { W = w; D = d; H = h; T = t; MinW = minW; MaxW = maxW; MinD = minD; MaxD = maxD; MinH = minH; MaxH = maxH; }
        }

        // Typical size + plausible architectural range (mm) for each category. Values outside range are rejected as mis-parses.
        private static readonly Dictionary<string, DimDefaults> CategoryDefaults = new Dictionary<string, DimDefaults>
        {
            ["door"]      = new DimDefaults(900, 44, 2100, 44,     500, 3600,   25, 300,  1800, 4000),
            ["window"]    = new DimDefaults(1200, 100, 1500, 50,   250, 6000,   40, 400,   250, 4000),
            ["chair"]     = new DimDefaults(600, 600, 850, 50,     350, 3000,  350, 1200,  400, 1400),
            ["table"]     = new DimDefaults(1600, 800, 750, 30,    400, 6000,  300, 2500,  300, 1300),
            ["casework"]  = new DimDefaults(900, 450, 1800, 20,    200, 4000,  150, 1000,  150, 3000),
            ["baseplate"] = new DimDefaults(300, 300, 300, 25,      80, 1500,   80, 1500,   50, 2000),
            ["canopy"]    = new DimDefaults(2000, 1200, 400, 100,  500, 15000, 300, 6000,   50, 3000),
            ["column"]    = new DimDefaults(400, 400, 3000, 50,     75, 2000,   75, 2000,  500, 15000),
            ["generic"]   = new DimDefaults(1000, 600, 900, 25,     10, 20000,  10, 20000,  10, 20000),
        };

        private static readonly (string cat, string[] words)[] CategoryKeywords =
        {
            ("door", new[] { "door", "gate", "entryway", "doorset", "door leaf" }),
            ("window", new[] { "window", "louver", "louvre", "glazing", "casement", "skylight", "fenestration" }),
            ("chair", new[] { "chair", "armchair", "sofa", "couch", "stool", "bench", "ottoman", "seating", "lounge" }),
            ("table", new[] { "table", "desk", "workstation", "counter", "worktop" }),
            ("casework", new[] { "cabinet", "shelf", "shelving", "bookcase", "wardrobe", "cupboard", "drawer", "casework", "storage", "locker", "credenza", "vanity" }),
            ("baseplate", new[] { "baseplate", "base plate", "bracket", "gusset", "flange", "anchor bolt", "end plate" }),
            ("canopy", new[] { "canopy", "trellis", "pergola", "sunshade", "brise", "awning", "overhang" }),
            ("column", new[] { "column", "pillar", "pier", "stanchion", "bollard" }),
        };

        /// <summary>Picks the category with the most keyword hits (whole words), or null if none.</summary>
        private static string? DetectCategory(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string lower = text.ToLowerInvariant();
            string? best = null;
            int bestHits = 0;
            foreach (var (cat, words) in CategoryKeywords)
            {
                int hits = 0;
                foreach (var w in words)
                {
                    hits += Regex.Matches(lower, @"\b" + Regex.Escape(w) + @"s?\b").Count;
                }
                if (hits > bestHits) { bestHits = hits; best = cat; }
            }
            return best;
        }

        private static double Pick(string label, double? fromPrompt, double? fromDoc, double fallback, double min, double max, StringBuilder report)
        {
            if (fromPrompt.HasValue)
            {
                if (fromPrompt.Value >= min && fromPrompt.Value <= max)
                {
                    report.AppendLine($"  {label} = {fromPrompt.Value:0.#} mm (from prompt)");
                    return fromPrompt.Value;
                }
                report.AppendLine($"  ⚠ {label} {fromPrompt.Value:0.#} mm from prompt is outside plausible range {min:0}-{max:0} mm; ignored.");
            }
            if (fromDoc.HasValue)
            {
                if (fromDoc.Value >= min && fromDoc.Value <= max)
                {
                    report.AppendLine($"  {label} = {fromDoc.Value:0.#} mm (from attached sheet)");
                    return fromDoc.Value;
                }
                report.AppendLine($"  ⚠ {label} {fromDoc.Value:0.#} mm from sheet is outside plausible range {min:0}-{max:0} mm; ignored.");
            }
            report.AppendLine($"  {label} = {fallback:0.#} mm (standard default)");
            return fallback;
        }

        private struct ParsedDims { public double? W, D, H, T; }

        private const string UnitRx = @"(mm|cm|m|in|inch|inches|""|ft|feet|')";

        /// <summary>
        /// Parses W/D/H/T (in mm) from free text. Order of precedence:
        /// labeled values ("Width: 36 in", "H=2100") override generic "A x B x C" strings.
        /// </summary>
        private static ParsedDims ParseDimensions(string rawText, string category)
        {
            var r = new ParsedDims();
            if (string.IsNullOrWhiteSpace(rawText)) return r;

            string text = NormalizeImperial(rawText);
            string? docUnit = DominantUnit(text);
            var defs = CategoryDefaults[category];

            double? Conv(string num, string unit, double typical)
            {
                if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) || v <= 0)
                    return null;
                if (!string.IsNullOrEmpty(unit)) return ConvertUnitToMm(v, unit);
                if (docUnit != null) return ConvertUnitToMm(v, docUnit);
                return InferUnitless(v, typical);
            }

            // A) "A x B x C" / "A x B" sequences, honouring an explicit order label like "(W x D x H)" or "HxWxD".
            string seqNum = @"(\d+(?:\.\d+)?)\s*" + UnitRx + "?";
            var triple = Regex.Match(text, seqNum + @"\s*(?:x|×|by|\*)\s*" + seqNum + @"\s*(?:x|×|by|\*)\s*" + seqNum, RegexOptions.IgnoreCase);
            if (triple.Success)
            {
                string order = DetectOrder(text, 3) ?? "WDH";
                string lastUnit = triple.Groups[6].Value;
                AssignByOrder(ref r, order[0], Conv(triple.Groups[1].Value, Fill(triple.Groups[2].Value, lastUnit), Typical(order[0], defs)));
                AssignByOrder(ref r, order[1], Conv(triple.Groups[3].Value, Fill(triple.Groups[4].Value, lastUnit), Typical(order[1], defs)));
                AssignByOrder(ref r, order[2], Conv(triple.Groups[5].Value, lastUnit, Typical(order[2], defs)));
            }
            else
            {
                var dub = Regex.Match(text, seqNum + @"\s*(?:x|×|by|\*)\s*" + seqNum, RegexOptions.IgnoreCase);
                if (dub.Success)
                {
                    bool vertical = category == "door" || category == "window" || category == "casework";
                    string order = DetectOrder(text, 2) ?? (vertical ? "WH" : "WD");
                    string lastUnit = dub.Groups[4].Value;
                    AssignByOrder(ref r, order[0], Conv(dub.Groups[1].Value, Fill(dub.Groups[2].Value, lastUnit), Typical(order[0], defs)));
                    AssignByOrder(ref r, order[1], Conv(dub.Groups[3].Value, lastUnit, Typical(order[1], defs)));
                }
            }

            // B) Labeled values override sequences. Full words allow plain whitespace; single letters require ':' or '='.
            void Labeled(string wordRx, string letterRx, char slot)
            {
                var m = Regex.Match(text, @"\b(?:overall\s+|nominal\s+|total\s+)?(?:" + wordRx + @")\b\s*(?:\(\w+\))?\s*[:=]?\s*(\d+(?:\.\d+)?)\s*" + UnitRx + "?", RegexOptions.IgnoreCase);
                if (!m.Success && letterRx.Length > 0)
                    m = Regex.Match(text, @"\b(?:" + letterRx + @")\s*[:=]\s*(\d+(?:\.\d+)?)\s*" + UnitRx + "?", RegexOptions.IgnoreCase);
                if (!m.Success && slot != 'T')  // "1200 mm W" / "2100(H)"
                    m = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*" + UnitRx + @"?\s*\(?\s*(?:" + (letterRx.Length > 0 ? letterRx : wordRx) + @")\s*\)?(?![a-z])", RegexOptions.IgnoreCase);
                if (!m.Success)  // "1800 wide" / "2.1m high" / "25mm thick"
                {
                    string adj = slot switch { 'W' => "wide|long", 'D' => "deep", 'H' => "high|tall", _ => "thick" };
                    m = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*" + UnitRx + @"?\s*(?:" + adj + @")\b", RegexOptions.IgnoreCase);
                }
                if (m.Success)
                {
                    var val = Conv(m.Groups[1].Value, m.Groups[2].Value, Typical(slot, defs));
                    if (val.HasValue) AssignByOrder(ref r, slot, val);
                }
            }
            Labeled("width|length|span", "w|l", 'W');
            Labeled("depth|projection|leaf thickness|frame depth", "d", 'D');
            Labeled("height|clear height|ht", "h", 'H');
            Labeled("thickness|thick|thk", "t", 'T');

            return r;
        }

        private static string Fill(string unit, string fallbackUnit) => string.IsNullOrEmpty(unit) ? fallbackUnit : unit;

        private static double Typical(char slot, DimDefaults d) => slot switch { 'W' => d.W, 'D' => d.D, 'H' => d.H, _ => d.T };

        private static void AssignByOrder(ref ParsedDims r, char slot, double? val)
        {
            if (!val.HasValue) return;
            switch (slot)
            {
                case 'W': r.W = val; break;
                case 'D': r.D = val; break;
                case 'H': r.H = val; break;
                case 'T': r.T = val; break;
            }
        }

        /// <summary>Finds an explicit dimension order label such as "W x D x H", "(HxWxD)", "L x W x H".</summary>
        private static string? DetectOrder(string text, int count)
        {
            string l = @"\b([WDHL])\s*(?:x|×|\*)\s*([WDHL])";
            if (count == 3) l += @"\s*(?:x|×|\*)\s*([WDHL])\b";
            else l += @"\b";
            var m = Regex.Match(text, l, RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var sb = new StringBuilder();
            for (int i = 1; i <= count; i++)
            {
                char c = char.ToUpperInvariant(m.Groups[i].Value[0]);
                sb.Append(c == 'L' ? 'W' : c);
            }
            return sb.ToString();
        }

        /// <summary>Converts architectural imperial notation (3'-6", 36 1/2", 7') into explicit "NNN.Nmm" tokens.</summary>
        private static string NormalizeImperial(string text)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string Mm(double v) => v.ToString("0.#", inv) + "mm";
            double Frac(Group whole, Group num, Group den)
            {
                double v = whole.Success && whole.Value.Length > 0 ? double.Parse(whole.Value, inv) : 0;
                if (num.Success && den.Success && num.Value.Length > 0 && double.Parse(den.Value, inv) != 0)
                    v += double.Parse(num.Value, inv) / double.Parse(den.Value, inv);
                return v;
            }

            // Unicode primes → ASCII
            text = text.Replace('′', '\'').Replace('″', '"').Replace('’', '\'').Replace('”', '"');

            // 3'-6 1/2"  /  3' 6"  /  3'-0"
            text = Regex.Replace(text, @"(\d+)\s*'\s*-?\s*(\d+)?(?:\s+(\d+)/(\d+))?\s*""", m =>
                Mm(double.Parse(m.Groups[1].Value, inv) * 304.8 + Frac(m.Groups[2], m.Groups[3], m.Groups[4]) * 25.4));
            // 36 1/2"
            text = Regex.Replace(text, @"(\d+)\s+(\d+)/(\d+)\s*(?:""|in\b|inch)", m =>
                Mm(Frac(m.Groups[1], m.Groups[2], m.Groups[3]) * 25.4));
            // 7'  (feet alone, not followed by inches)
            text = Regex.Replace(text, @"(\d+(?:\.\d+)?)\s*'(?!\s*-?\s*\d)", m =>
                Mm(double.Parse(m.Groups[1].Value, inv) * 304.8));
            return text;
        }

        /// <summary>Most frequent explicit unit in a document; used for unitless numbers in the same sheet.</summary>
        private static string? DominantUnit(string text)
        {
            int mm = Regex.Matches(text, @"\d\s*mm\b", RegexOptions.IgnoreCase).Count;
            int cm = Regex.Matches(text, @"\d\s*cm\b", RegexOptions.IgnoreCase).Count;
            int inch = Regex.Matches(text, @"\d\s*(?:""|in\b|inch)", RegexOptions.IgnoreCase).Count;
            int max = Math.Max(mm, Math.Max(cm, inch));
            if (max == 0) return null;
            if (max == mm) return "mm";
            if (max == inch) return "in";
            return "cm";
        }

        private static double ConvertUnitToMm(double val, string unit)
        {
            string u = unit.Trim().ToLowerInvariant();
            if (u == "'" || u == "ft" || u == "feet") return val * 304.8;
            if (u == "\"" || u == "in" || u == "inch" || u == "inches") return val * 25.4;
            if (u == "cm") return val * 10.0;
            if (u == "m") return val * 1000.0;
            return val; // mm
        }

        /// <summary>
        /// For numbers with no unit anywhere: choose the unit (mm, cm, m, in, ft) whose result is closest
        /// (on a log scale) to the typical size for that dimension. "36" for a door width → inches (914 mm),
        /// "0.9" → metres, "900" → mm, "210" for door height → cm. Slight bias toward mm.
        /// </summary>
        private static double InferUnitless(double v, double typical)
        {
            var options = new (double factor, double bias)[] { (1, 0.0), (10, 0.25), (1000, 0.15), (25.4, 0.1), (304.8, 0.2) };
            double best = v, bestScore = double.MaxValue;
            foreach (var (f, bias) in options)
            {
                double mm = v * f;
                double score = Math.Abs(Math.Log(mm / typical)) + bias;
                if (score < bestScore) { bestScore = score; best = mm; }
            }
            return best;
        }

        /// <summary>
        /// Extracts text from a PDF without external libraries:
        /// Pass 1 collects /ToUnicode CMap tables (beginbfchar / beginbfrange) for subsetted CID fonts;
        /// Pass 2 inflates FlateDecode content streams and decodes text operators (Tj, TJ, ', ").
        /// </summary>
        private static string ExtractTextFromPdf(string pdfPath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(pdfPath);
                var latin1 = Encoding.GetEncoding(28591);
                string raw = latin1.GetString(bytes);
                var sb = new StringBuilder();
                var cmap = new Dictionary<int, string>();
                var textStreams = new List<string>();

                int pos = 0;
                while (true)
                {
                    int s = raw.IndexOf("stream", pos, StringComparison.Ordinal);
                    if (s < 0) break;
                    if (s >= 3 && raw.Substring(s - 3, 3) == "end") { pos = s + 6; continue; }

                    int dataStart = s + 6;
                    if (dataStart < raw.Length && raw[dataStart] == '\r') dataStart++;
                    if (dataStart < raw.Length && raw[dataStart] == '\n') dataStart++;
                    int e = raw.IndexOf("endstream", dataStart, StringComparison.Ordinal);
                    if (e < 0) break;

                    int dictStart = raw.LastIndexOf("<<", s, Math.Min(s, 2000), StringComparison.Ordinal);
                    string dict = dictStart >= 0 ? raw.Substring(dictStart, s - dictStart) : "";
                    pos = e + 9;

                    if (dict.Contains("/Image") || dict.Contains("/FontFile") || dict.Contains("/XRef"))
                        continue;

                    int len = e - dataStart;
                    if (len <= 0) continue;
                    string content;
                    if (dict.Contains("/FlateDecode"))
                    {
                        byte[]? inflated = Inflate(bytes, dataStart, len);
                        if (inflated == null) continue;
                        content = latin1.GetString(inflated);
                    }
                    else if (dict.Contains("/Filter"))
                    {
                        continue;
                    }
                    else
                    {
                        content = raw.Substring(dataStart, len);
                    }

                    if (content.Contains("beginbfchar") || content.Contains("beginbfrange"))
                    {
                        ParseToUnicodeCMap(content, cmap);
                    }
                    if (content.Contains("BT"))
                    {
                        textStreams.Add(content);
                    }
                }

                foreach (var content in textStreams)
                {
                    ExtractTextOperators(content, sb, cmap);
                }

                // Always include the PDF filename as additional context (e.g. "Double_Door_1800x2100.pdf")
                string fileHint = Path.GetFileNameWithoutExtension(pdfPath).Replace('_', ' ').Replace('-', ' ');
                sb.Append(' ').Append(fileHint);

                return Regex.Replace(sb.ToString(), @"[ \t]+", " ");
            }
            catch
            {
                return "";
            }
        }

        private static void ParseToUnicodeCMap(string cmapContent, Dictionary<int, string> cmap)
        {
            foreach (Match block in Regex.Matches(cmapContent, @"beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
            {
                foreach (Match m in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
                {
                    int srcCode = Convert.ToInt32(m.Groups[1].Value, 16);
                    string dst = HexToUnicodeString(m.Groups[2].Value);
                    if (!string.IsNullOrEmpty(dst)) cmap[srcCode] = dst;
                }
            }
            foreach (Match block in Regex.Matches(cmapContent, @"beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
            {
                foreach (Match m in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
                {
                    int start = Convert.ToInt32(m.Groups[1].Value, 16);
                    int end = Convert.ToInt32(m.Groups[2].Value, 16);
                    int target = Convert.ToInt32(m.Groups[3].Value, 16);
                    for (int c = start; c <= end && (c - start) < 512; c++)
                    {
                        cmap[c] = char.ConvertFromUtf32(target + (c - start));
                    }
                }
            }
        }

        private static string HexToUnicodeString(string hex)
        {
            if (hex.Length % 4 == 0)
            {
                var bytes = new byte[hex.Length / 2];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                return Encoding.BigEndianUnicode.GetString(bytes);
            }
            int code = Convert.ToInt32(hex, 16);
            return code > 0 ? char.ConvertFromUtf32(code) : "";
        }

        private static byte[]? Inflate(byte[] src, int offset, int length)
        {
            for (int skip = 2; skip >= 0; skip -= 2)
            {
                try
                {
                    using var input = new MemoryStream(src, offset + skip, length - skip);
                    using var deflate = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    deflate.CopyTo(output);
                    if (output.Length > 0) return output.ToArray();
                }
                catch { }
            }
            return null;
        }

        private static readonly Regex PdfTokenRx = new Regex(
            @"\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f\s]*>|-?\d*\.?\d+|[A-Za-z*'""]+|\[|\]",
            RegexOptions.Compiled);

        private static void ExtractTextOperators(string content, StringBuilder sb, Dictionary<int, string> cmap)
        {
            foreach (Match block in Regex.Matches(content, @"\bBT\b(.*?)\bET\b", RegexOptions.Singleline))
            {
                bool inArray = false;
                foreach (Match tok in PdfTokenRx.Matches(block.Groups[1].Value))
                {
                    string t = tok.Value;
                    if (t[0] == '(') sb.Append(UnescapePdfString(t.Substring(1, t.Length - 2), cmap));
                    else if (t[0] == '<') sb.Append(DecodeHexString(t.Substring(1, t.Length - 2), cmap));
                    else if (t == "[") inArray = true;
                    else if (t == "]") inArray = false;
                    else if (inArray && (char.IsDigit(t[t.Length - 1])))
                    {
                        if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double kern) && kern < -180)
                            sb.Append(' ');
                    }
                    else if (t == "Td" || t == "TD" || t == "T*" || t == "Tm" || t == "'" || t == "\"") sb.Append('\n');
                    else if (t == "Tj" || t == "TJ") sb.Append(' ');
                }
                sb.Append('\n');
            }
        }

        private static string UnescapePdfString(string s, Dictionary<int, string>? cmap = null)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': case 'f': break;
                    case '(': case ')': case '\\': sb.Append(n); break;
                    default:
                        if (n >= '0' && n <= '7')
                        {
                            int val = n - '0', k = 0;
                            while (k < 2 && i + 1 < s.Length && s[i + 1] >= '0' && s[i + 1] <= '7') { val = val * 8 + (s[++i] - '0'); k++; }
                            sb.Append((char)val);
                        }
                        else if (n != '\r' && n != '\n')
                        {
                            sb.Append(n);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        private static string DecodeHexString(string hex, Dictionary<int, string> cmap)
        {
            hex = Regex.Replace(hex, @"\s", "");
            if (hex.Length == 0) return "";
            if (hex.Length % 2 == 1) hex += "0";

            // 1. Try 2-byte CID lookup in /ToUnicode CMap first
            if (cmap.Count > 0 && hex.Length % 4 == 0)
            {
                var sb = new StringBuilder();
                bool any = false;
                for (int i = 0; i < hex.Length; i += 4)
                {
                    int cid = Convert.ToInt32(hex.Substring(i, 4), 16);
                    if (cmap.TryGetValue(cid, out string? mapped)) { sb.Append(mapped); any = true; }
                }
                if (any) return sb.ToString();
            }

            // 2. Try 1-byte lookup in /ToUnicode CMap
            if (cmap.Count > 0)
            {
                var sb = new StringBuilder();
                bool any = false;
                for (int i = 0; i < hex.Length; i += 2)
                {
                    int cid = Convert.ToInt32(hex.Substring(i, 2), 16);
                    if (cmap.TryGetValue(cid, out string? mapped)) { sb.Append(mapped); any = true; }
                }
                if (any) return sb.ToString();
            }

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

            if (bytes.Length >= 2 && bytes.Length % 2 == 0)
            {
                bool utf16 = true;
                for (int i = 0; i < bytes.Length; i += 2) if (bytes[i] != 0) { utf16 = false; break; }
                if (utf16) return Encoding.BigEndianUnicode.GetString(bytes);
            }
            foreach (var b in bytes) if (b < 32 || b > 126) return "";
            return Encoding.ASCII.GetString(bytes);
        }

        private static string ExtractTextFromImageFilename(string imagePath)
        {
            try
            {
                if (string.IsNullOrEmpty(imagePath)) return "";
                string name = Path.GetFileNameWithoutExtension(imagePath);
                return name.Replace('_', ' ').Replace('-', ' ');
            }
            catch
            {
                return "";
            }
        }

        private static string CleanPromptToFamilyName(string prompt)
        {
            string cleaned = Regex.Replace(prompt, @"\b\d+[\w]*\b", "");
            cleaned = Regex.Replace(cleaned, @"\b(?:mm|cm|m|in|inch|feet|width|height|depth|thick|thickness|with|and|the|a|an|parametric|revit|family|create|make|generate)\b", "", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"[^\w\s]", "");
            var words = cleaned.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "Parametric_Custom_Component";

            var sb = new StringBuilder("Parametric_");
            int count = 0;
            foreach (var w in words)
            {
                if (w.Length > 1 && count < 3)
                {
                    sb.Append(char.ToUpperInvariant(w[0])).Append(w.Substring(1).ToLowerInvariant()).Append("_");
                    count++;
                }
            }
            return sb.ToString().TrimEnd('_');
        }
        private FamilySchema CreateWorkstationSchema(double width = 1600, double depth = 800, double height = 750, double topThk = 30)
        {
            double halfW = width / 2.0;
            double halfD = depth / 2.0;
            double legOffsetW = halfW - 50;
            double legOffsetD = halfD - 50;

            return new FamilySchema
            {
                MasterFamilyName = "Parametric_Workstation",
                FamilyCategory = "Furniture",
                TemplateName = "Metric Generic Model.rft",
                MasterParameters = new List<MasterParameterDef>
                {
                    new() { Name = "Table_Width", DataType = "Length", DefaultValueMm = width },
                    new() { Name = "Table_Depth", DataType = "Length", DefaultValueMm = depth },
                    new() { Name = "Table_Height", DataType = "Length", DefaultValueMm = height },
                    new() { Name = "Top_Thickness", DataType = "Length", DefaultValueMm = topThk },
                    new() { Name = "Leg_Size", DataType = "Length", DefaultValueMm = 50 }
                },
                ReferencePlanes = new List<ReferencePlaneDef>
                {
                    new() { Name = "Left_Plane", Orientation = "X", OffsetFromOriginMm = -halfW, DrivenByParameter = "Table_Width" },
                    new() { Name = "Right_Plane", Orientation = "X", OffsetFromOriginMm = halfW, DrivenByParameter = "Table_Width" },
                    new() { Name = "Front_Plane", Orientation = "Y", OffsetFromOriginMm = -halfD, DrivenByParameter = "Table_Depth" },
                    new() { Name = "Back_Plane", Orientation = "Y", OffsetFromOriginMm = halfD, DrivenByParameter = "Table_Depth" },
                    new() { Name = "Top_Plane", Orientation = "Z", OffsetFromOriginMm = height, DrivenByParameter = "Table_Height" }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    new()
                    {
                        Name = "Table_Top_Panel",
                        MinX_mm = -halfW, MaxX_mm = halfW,
                        MinY_mm = -halfD, MaxY_mm = halfD,
                        StartZ_mm = height - topThk, EndZ_mm = height,
                        WidthParameter = "Table_Width",
                        DepthParameter = "Table_Depth",
                        HeightParameter = "Top_Thickness"
                    }
                },
                NestedFamilies = new List<NestedFamilyDef>
                {
                    new()
                    {
                        NestedFamilyName = "Workstation_Leg",
                        TemplateName = "Metric Generic Model.rft",
                        SubParameters = new List<MasterParameterDef>
                        {
                            new() { Name = "Leg_Height", DataType = "Length", DefaultValueMm = height - topThk },
                            new() { Name = "Leg_Width", DataType = "Length", DefaultValueMm = 50 }
                        },
                        SubExtrusions = new List<ExtrusionDef>
                        {
                            new()
                            {
                                Name = "Leg_Solid",
                                ProfileType = "Cylinder",
                                Radius_mm = 25,
                                MinX_mm = -25, MaxX_mm = 25,
                                MinY_mm = -25, MaxY_mm = 25,
                                StartZ_mm = 0, EndZ_mm = height - topThk,
                                HeightParameter = "Leg_Height"
                            }
                        },
                        PlacementOffsetMm = new VectorOffsetDef(-legOffsetW, -legOffsetD, 0),
                        ParameterBindings = new Dictionary<string, string> { { "Leg_Height", "Table_Height" } }
                    },
                    new()
                    {
                        NestedFamilyName = "Workstation_Leg",
                        TemplateName = "Metric Generic Model.rft",
                        PlacementOffsetMm = new VectorOffsetDef(legOffsetW, -legOffsetD, 0),
                        ParameterBindings = new Dictionary<string, string> { { "Leg_Height", "Table_Height" } }
                    },
                    new()
                    {
                        NestedFamilyName = "Workstation_Leg",
                        TemplateName = "Metric Generic Model.rft",
                        PlacementOffsetMm = new VectorOffsetDef(-legOffsetW, legOffsetD, 0),
                        ParameterBindings = new Dictionary<string, string> { { "Leg_Height", "Table_Height" } }
                    },
                    new()
                    {
                        NestedFamilyName = "Workstation_Leg",
                        TemplateName = "Metric Generic Model.rft",
                        PlacementOffsetMm = new VectorOffsetDef(legOffsetW, legOffsetD, 0),
                        ParameterBindings = new Dictionary<string, string> { { "Leg_Height", "Table_Height" } }
                    }
                }
            };
        }

        private FamilySchema CreateChairSchema(double width = 600, double depth = 600, double height = 850, double cushionThk = 50)
        {
            double halfW = width / 2.0;
            double halfD = depth / 2.0;
            double seatZ = 450;
            double legOffsetW = halfW - 40;
            double legOffsetD = halfD - 40;

            return new FamilySchema
            {
                MasterFamilyName = "Parametric_Chair",
                FamilyCategory = "Furniture",
                TemplateName = "Metric Generic Model.rft",
                MasterParameters = new List<MasterParameterDef>
                {
                    new() { Name = "Chair_Width", DataType = "Length", DefaultValueMm = width },
                    new() { Name = "Chair_Depth", DataType = "Length", DefaultValueMm = depth },
                    new() { Name = "Total_Height", DataType = "Length", DefaultValueMm = height },
                    new() { Name = "Seat_Height", DataType = "Length", DefaultValueMm = seatZ }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    // Seat Cushion
                    new()
                    {
                        Name = "Seat_Cushion",
                        MinX_mm = -halfW, MaxX_mm = halfW,
                        MinY_mm = -halfD, MaxY_mm = halfD,
                        StartZ_mm = seatZ - cushionThk, EndZ_mm = seatZ,
                        WidthParameter = "Chair_Width",
                        DepthParameter = "Chair_Depth"
                    },
                    // Backrest
                    new()
                    {
                        Name = "Backrest_Panel",
                        MinX_mm = -halfW, MaxX_mm = halfW,
                        MinY_mm = halfD - 40, MaxY_mm = halfD,
                        StartZ_mm = seatZ, EndZ_mm = height,
                        WidthParameter = "Chair_Width",
                        HeightParameter = "Total_Height"
                    }
                },
                NestedFamilies = new List<NestedFamilyDef>
                {
                    new()
                    {
                        NestedFamilyName = "Chair_Leg",
                        TemplateName = "Metric Generic Model.rft",
                        SubParameters = new List<MasterParameterDef>
                        {
                            new() { Name = "Leg_Height", DataType = "Length", DefaultValueMm = seatZ - cushionThk }
                        },
                        SubExtrusions = new List<ExtrusionDef>
                        {
                            new()
                            {
                                Name = "Leg_Cylinder",
                                ProfileType = "Cylinder",
                                Radius_mm = 18,
                                MinX_mm = -18, MaxX_mm = 18,
                                MinY_mm = -18, MaxY_mm = 18,
                                StartZ_mm = 0, EndZ_mm = seatZ - cushionThk
                            }
                        },
                        PlacementOffsetMm = new VectorOffsetDef(-legOffsetW, -legOffsetD, 0),
                        ParameterBindings = new Dictionary<string, string> { { "Leg_Height", "Seat_Height" } }
                    },
                    new()
                    {
                        NestedFamilyName = "Chair_Leg",
                        TemplateName = "Metric Generic Model.rft",
                        PlacementOffsetMm = new VectorOffsetDef(legOffsetW, -legOffsetD, 0),
                        ParameterBindings = new Dictionary<string, string> { { "Leg_Height", "Seat_Height" } }
                    },
                    new()
                    {
                        NestedFamilyName = "Chair_Leg",
                        TemplateName = "Metric Generic Model.rft",
                        PlacementOffsetMm = new VectorOffsetDef(-legOffsetW, legOffsetD, 0),
                        ParameterBindings = new Dictionary<string, string> { { "Leg_Height", "Seat_Height" } }
                    },
                    new()
                    {
                        NestedFamilyName = "Chair_Leg",
                        TemplateName = "Metric Generic Model.rft",
                        PlacementOffsetMm = new VectorOffsetDef(legOffsetW, legOffsetD, 0),
                        ParameterBindings = new Dictionary<string, string> { { "Leg_Height", "Seat_Height" } }
                    }
                }
            };
        }

        private FamilySchema CreateDoorSchema(double width = 900, double height = 2100, double thickness = 44)
        {
            return new FamilySchema
            {
                MasterFamilyName = "Parametric_Door_Leaf",
                FamilyCategory = "Doors",
                TemplateName = "Metric Generic Model.rft",
                MasterParameters = new List<MasterParameterDef>
                {
                    new() { Name = "Door_Width", DataType = "Length", DefaultValueMm = width },
                    new() { Name = "Door_Height", DataType = "Length", DefaultValueMm = height },
                    new() { Name = "Door_Thickness", DataType = "Length", DefaultValueMm = thickness }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    new()
                    {
                        Name = "Door_Panel",
                        MinX_mm = 0, MaxX_mm = width,
                        MinY_mm = 0, MaxY_mm = thickness,
                        StartZ_mm = 0, EndZ_mm = height,
                        WidthParameter = "Door_Width",
                        DepthParameter = "Door_Thickness",
                        HeightParameter = "Door_Height"
                    }
                },
                NestedFamilies = new List<NestedFamilyDef>
                {
                    new()
                    {
                        NestedFamilyName = "Door_Lever_Handle",
                        TemplateName = "Metric Generic Model.rft",
                        SubParameters = new List<MasterParameterDef>
                        {
                            new() { Name = "Handle_Length", DataType = "Length", DefaultValueMm = 120 }
                        },
                        SubExtrusions = new List<ExtrusionDef>
                        {
                            new()
                            {
                                Name = "Handle_Lever",
                                ProfileType = "Cylinder",
                                Radius_mm = 10,
                                MinX_mm = 0, MaxX_mm = 120,
                                MinY_mm = 0, MaxY_mm = 20,
                                StartZ_mm = 0, EndZ_mm = 20
                            }
                        },
                        PlacementOffsetMm = new VectorOffsetDef(width - 80, thickness + 10, 1000),
                        ParameterBindings = new Dictionary<string, string>()
                    }
                }
            };
        }

        private FamilySchema CreateWindowSchema(double width = 1200, double height = 1500, double depth = 100)
        {
            double halfW = width / 2.0;

            return new FamilySchema
            {
                MasterFamilyName = "Parametric_Window_Casement",
                FamilyCategory = "Windows",
                TemplateName = "Metric Generic Model.rft",
                MasterParameters = new List<MasterParameterDef>
                {
                    new() { Name = "Window_Width", DataType = "Length", DefaultValueMm = width },
                    new() { Name = "Window_Height", DataType = "Length", DefaultValueMm = height },
                    new() { Name = "Frame_Depth", DataType = "Length", DefaultValueMm = depth }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    new()
                    {
                        Name = "Outer_Frame",
                        ProfileType = "HollowBox",
                        WallThickness_mm = 50,
                        MinX_mm = -halfW, MaxX_mm = halfW,
                        MinY_mm = -depth / 2.0, MaxY_mm = depth / 2.0,
                        StartZ_mm = 0, EndZ_mm = height,
                        WidthParameter = "Window_Width",
                        DepthParameter = "Frame_Depth",
                        HeightParameter = "Window_Height"
                    },
                    new()
                    {
                        Name = "Glass_Infill",
                        MinX_mm = -halfW + 45, MaxX_mm = halfW - 45,
                        MinY_mm = -6, MaxY_mm = 6,
                        StartZ_mm = 45, EndZ_mm = height - 45
                    }
                },
                NestedFamilies = new List<NestedFamilyDef>
                {
                    new()
                    {
                        NestedFamilyName = "Window_Lock_Handle",
                        TemplateName = "Metric Generic Model.rft",
                        SubExtrusions = new List<ExtrusionDef>
                        {
                            new() { Name = "Lock_Knob", ProfileType = "Cylinder", Radius_mm = 12, StartZ_mm = 0, EndZ_mm = 25 }
                        },
                        PlacementOffsetMm = new VectorOffsetDef(halfW - 55, 0, height / 2.0)
                    }
                }
            };
        }

        private FamilySchema CreateCaseworkSchema(double width = 900, double depth = 450, double height = 1800, double thk = 20)
        {
            double halfW = width / 2.0;

            return new FamilySchema
            {
                MasterFamilyName = "Parametric_Cabinet_Shelving",
                FamilyCategory = "Casework",
                TemplateName = "Metric Generic Model.rft",
                MasterParameters = new List<MasterParameterDef>
                {
                    new() { Name = "Cabinet_Width", DataType = "Length", DefaultValueMm = width },
                    new() { Name = "Cabinet_Depth", DataType = "Length", DefaultValueMm = depth },
                    new() { Name = "Cabinet_Height", DataType = "Length", DefaultValueMm = height },
                    new() { Name = "Panel_Thickness", DataType = "Length", DefaultValueMm = thk }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    // Plinth / Kickplate
                    new()
                    {
                        Name = "Base_Plinth",
                        MinX_mm = -halfW, MaxX_mm = halfW,
                        MinY_mm = 0, MaxY_mm = depth - 30,
                        StartZ_mm = 0, EndZ_mm = 100
                    },
                    // Outer Carcass (Hollow)
                    new()
                    {
                        Name = "Carcass_Body",
                        ProfileType = "HollowBox",
                        WallThickness_mm = thk,
                        MinX_mm = -halfW, MaxX_mm = halfW,
                        MinY_mm = 0, MaxY_mm = depth,
                        StartZ_mm = 100, EndZ_mm = height,
                        WidthParameter = "Cabinet_Width",
                        DepthParameter = "Cabinet_Depth",
                        HeightParameter = "Cabinet_Height"
                    },
                    // Mid Shelf 1
                    new()
                    {
                        Name = "Interior_Shelf_1",
                        MinX_mm = -halfW + thk, MaxX_mm = halfW - thk,
                        MinY_mm = thk, MaxY_mm = depth - 10,
                        StartZ_mm = height * 0.4, EndZ_mm = height * 0.4 + thk
                    },
                    // Mid Shelf 2
                    new()
                    {
                        Name = "Interior_Shelf_2",
                        MinX_mm = -halfW + thk, MaxX_mm = halfW - thk,
                        MinY_mm = thk, MaxY_mm = depth - 10,
                        StartZ_mm = height * 0.7, EndZ_mm = height * 0.7 + thk
                    }
                },
                NestedFamilies = new List<NestedFamilyDef>
                {
                    new()
                    {
                        NestedFamilyName = "Cabinet_Pull_Handle",
                        TemplateName = "Metric Generic Model.rft",
                        SubExtrusions = new List<ExtrusionDef>
                        {
                            new() { Name = "Handle_Bar", ProfileType = "Cylinder", Radius_mm = 8, StartZ_mm = 0, EndZ_mm = 120 }
                        },
                        PlacementOffsetMm = new VectorOffsetDef(0, depth + 10, height * 0.5)
                    }
                }
            };
        }

        private FamilySchema CreateBaseplateJointSchema(double width = 300, double depth = 300, double plateThk = 25, double stubHeight = 300)
        {
            double halfW = width / 2.0;
            double halfD = depth / 2.0;
            double boltOffsetW = halfW - 45;
            double boltOffsetD = halfD - 45;

            return new FamilySchema
            {
                MasterFamilyName = "Parametric_Column_Baseplate",
                FamilyCategory = "Generic Models",
                TemplateName = "Metric Generic Model.rft",
                MasterParameters = new List<MasterParameterDef>
                {
                    new() { Name = "Plate_Width", DataType = "Length", DefaultValueMm = width },
                    new() { Name = "Plate_Depth", DataType = "Length", DefaultValueMm = depth },
                    new() { Name = "Plate_Thickness", DataType = "Length", DefaultValueMm = plateThk }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    new()
                    {
                        Name = "Steel_Baseplate",
                        MinX_mm = -halfW, MaxX_mm = halfW,
                        MinY_mm = -halfD, MaxY_mm = halfD,
                        StartZ_mm = 0, EndZ_mm = plateThk
                    },
                    new()
                    {
                        Name = "Column_Hollow_Stub",
                        ProfileType = "HollowBox",
                        WallThickness_mm = 8,
                        MinX_mm = -halfW + 75, MaxX_mm = halfW - 75,
                        MinY_mm = -halfD + 75, MaxY_mm = halfD - 75,
                        StartZ_mm = plateThk, EndZ_mm = plateThk + stubHeight
                    }
                },
                NestedFamilies = new List<NestedFamilyDef>
                {
                    new()
                    {
                        NestedFamilyName = "Anchor_Bolt",
                        TemplateName = "Metric Generic Model.rft",
                        SubExtrusions = new List<ExtrusionDef>
                        {
                            new() { Name = "Bolt_Shaft", ProfileType = "Cylinder", Radius_mm = 10, StartZ_mm = 0, EndZ_mm = plateThk + 50 }
                        },
                        PlacementOffsetMm = new VectorOffsetDef(-boltOffsetW, -boltOffsetD, 0)
                    },
                    new()
                    {
                        NestedFamilyName = "Anchor_Bolt",
                        TemplateName = "Metric Generic Model.rft",
                        PlacementOffsetMm = new VectorOffsetDef(boltOffsetW, -boltOffsetD, 0)
                    },
                    new()
                    {
                        NestedFamilyName = "Anchor_Bolt",
                        TemplateName = "Metric Generic Model.rft",
                        PlacementOffsetMm = new VectorOffsetDef(-boltOffsetW, boltOffsetD, 0)
                    },
                    new()
                    {
                        NestedFamilyName = "Anchor_Bolt",
                        TemplateName = "Metric Generic Model.rft",
                        PlacementOffsetMm = new VectorOffsetDef(boltOffsetW, boltOffsetD, 0)
                    }
                }
            };
        }

        private FamilySchema CreateCanopySchema(double width = 2000, double depth = 1200, double thk = 100, double height = 400)
        {
            double halfW = width / 2.0;

            return new FamilySchema
            {
                MasterFamilyName = "Parametric_Canopy_Awning",
                FamilyCategory = "Generic Models",
                TemplateName = "Metric Generic Model.rft",
                MasterParameters = new List<MasterParameterDef>
                {
                    new() { Name = "Canopy_Width", DataType = "Length", DefaultValueMm = width },
                    new() { Name = "Canopy_Projection", DataType = "Length", DefaultValueMm = depth },
                    new() { Name = "Frame_Thickness", DataType = "Length", DefaultValueMm = thk }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    // Perimeter frame
                    new()
                    {
                        Name = "Canopy_Frame",
                        ProfileType = "HollowBox",
                        WallThickness_mm = 40,
                        MinX_mm = -halfW, MaxX_mm = halfW,
                        MinY_mm = 0, MaxY_mm = depth,
                        StartZ_mm = height - thk, EndZ_mm = height,
                        WidthParameter = "Canopy_Width",
                        DepthParameter = "Canopy_Projection"
                    },
                    // Glass Roof
                    new()
                    {
                        Name = "Canopy_Glazing",
                        MinX_mm = -halfW + 35, MaxX_mm = halfW - 35,
                        MinY_mm = 35, MaxY_mm = depth - 35,
                        StartZ_mm = height - 12, EndZ_mm = height
                    }
                },
                NestedFamilies = new List<NestedFamilyDef>
                {
                    new()
                    {
                        NestedFamilyName = "Canopy_Strut_Arm",
                        TemplateName = "Metric Generic Model.rft",
                        SubExtrusions = new List<ExtrusionDef>
                        {
                            new() { Name = "Strut_Bar", ProfileType = "Cylinder", Radius_mm = 15, StartZ_mm = 0, EndZ_mm = height }
                        },
                        PlacementOffsetMm = new VectorOffsetDef(-halfW + 100, depth * 0.7, 0)
                    },
                    new()
                    {
                        NestedFamilyName = "Canopy_Strut_Arm",
                        TemplateName = "Metric Generic Model.rft",
                        PlacementOffsetMm = new VectorOffsetDef(halfW - 100, depth * 0.7, 0)
                    }
                }
            };
        }

        private FamilySchema CreateColumnSchema(double width = 400, double depth = 400, double height = 3000)
        {
            double halfW = width / 2.0;
            double halfD = depth / 2.0;

            return new FamilySchema
            {
                MasterFamilyName = "Parametric_Architectural_Column",
                FamilyCategory = "Columns",
                TemplateName = "Metric Generic Model.rft",
                MasterParameters = new List<MasterParameterDef>
                {
                    new() { Name = "Column_Width", DataType = "Length", DefaultValueMm = width },
                    new() { Name = "Column_Depth", DataType = "Length", DefaultValueMm = depth },
                    new() { Name = "Column_Height", DataType = "Length", DefaultValueMm = height }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    // Plinth Footing
                    new()
                    {
                        Name = "Column_Footing",
                        MinX_mm = -halfW - 50, MaxX_mm = halfW + 50,
                        MinY_mm = -halfD - 50, MaxY_mm = halfD + 50,
                        StartZ_mm = 0, EndZ_mm = 150
                    },
                    // Main Shaft
                    new()
                    {
                        Name = "Column_Shaft",
                        MinX_mm = -halfW, MaxX_mm = halfW,
                        MinY_mm = -halfD, MaxY_mm = halfD,
                        StartZ_mm = 150, EndZ_mm = height - 150,
                        WidthParameter = "Column_Width",
                        DepthParameter = "Column_Depth",
                        HeightParameter = "Column_Height"
                    },
                    // Capital at top
                    new()
                    {
                        Name = "Column_Capital",
                        MinX_mm = -halfW - 50, MaxX_mm = halfW + 50,
                        MinY_mm = -halfD - 50, MaxY_mm = halfD + 50,
                        StartZ_mm = height - 150, EndZ_mm = height
                    }
                },
                NestedFamilies = new List<NestedFamilyDef>()
            };
        }

        private FamilySchema CreateDynamicUniversalSchema(string prompt, double width, double depth, double height, double thk)
        {
            string familyName = CleanPromptToFamilyName(prompt);
            double halfW = width / 2.0;
            double halfD = depth / 2.0;

            return new FamilySchema
            {
                MasterFamilyName = familyName,
                FamilyCategory = "Generic Models",
                TemplateName = "Metric Generic Model.rft",
                MasterParameters = new List<MasterParameterDef>
                {
                    new() { Name = "Width", DataType = "Length", DefaultValueMm = width },
                    new() { Name = "Depth", DataType = "Length", DefaultValueMm = depth },
                    new() { Name = "Height", DataType = "Length", DefaultValueMm = height },
                    new() { Name = "Thickness", DataType = "Length", DefaultValueMm = thk }
                },
                ReferencePlanes = new List<ReferencePlaneDef>
                {
                    new() { Name = "Left_Plane", Orientation = "X", OffsetFromOriginMm = -halfW, DrivenByParameter = "Width" },
                    new() { Name = "Right_Plane", Orientation = "X", OffsetFromOriginMm = halfW, DrivenByParameter = "Width" },
                    new() { Name = "Front_Plane", Orientation = "Y", OffsetFromOriginMm = -halfD, DrivenByParameter = "Depth" },
                    new() { Name = "Back_Plane", Orientation = "Y", OffsetFromOriginMm = halfD, DrivenByParameter = "Depth" },
                    new() { Name = "Top_Plane", Orientation = "Z", OffsetFromOriginMm = height, DrivenByParameter = "Height" }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    // Plinth / Base
                    new()
                    {
                        Name = "Component_Base",
                        MinX_mm = -halfW, MaxX_mm = halfW,
                        MinY_mm = -halfD, MaxY_mm = halfD,
                        StartZ_mm = 0, EndZ_mm = thk,
                        WidthParameter = "Width",
                        DepthParameter = "Depth",
                        HeightParameter = "Thickness"
                    },
                    // Main Body
                    new()
                    {
                        Name = "Component_Body",
                        MinX_mm = -halfW + 20, MaxX_mm = halfW - 20,
                        MinY_mm = -halfD + 20, MaxY_mm = halfD - 20,
                        StartZ_mm = thk, EndZ_mm = height - thk,
                        HeightParameter = "Height"
                    },
                    // Top Cap
                    new()
                    {
                        Name = "Component_Top",
                        MinX_mm = -halfW, MaxX_mm = halfW,
                        MinY_mm = -halfD, MaxY_mm = halfD,
                        StartZ_mm = height - thk, EndZ_mm = height,
                        WidthParameter = "Width",
                        DepthParameter = "Depth",
                        HeightParameter = "Thickness"
                    }
                },
                NestedFamilies = new List<NestedFamilyDef>
                {
                    new()
                    {
                        NestedFamilyName = $"{familyName}_Hardware",
                        TemplateName = "Metric Generic Model.rft",
                        SubExtrusions = new List<ExtrusionDef>
                        {
                            new() { Name = "Hardware_Fitting", ProfileType = "Cylinder", Radius_mm = 15, StartZ_mm = 0, EndZ_mm = 30 }
                        },
                        PlacementOffsetMm = new VectorOffsetDef(halfW - 50, -halfD + 50, height)
                    }
                }
            };
        }

        private static byte[] OptimizeImageForVision(string filePath, out string mimeType)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".pdf")
            {
                mimeType = "application/pdf";
                return File.ReadAllBytes(filePath);
            }

            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count > 0)
                    {
                        var frame = decoder.Frames[0];
                        int origW = frame.PixelWidth;
                        int origH = frame.PixelHeight;
                        int maxDim = 1280;

                        BitmapSource source = frame;
                        if (origW > maxDim || origH > maxDim)
                        {
                            double scale = Math.Min((double)maxDim / origW, (double)maxDim / origH);
                            source = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
                        }

                        var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
                        encoder.Frames.Add(BitmapFrame.Create(source));

                        using (var ms = new MemoryStream())
                        {
                            encoder.Save(ms);
                            mimeType = "image/jpeg";
                            return ms.ToArray();
                        }
                    }
                }
            }
            catch
            {
                // Fallback to raw file read
            }

            mimeType = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "application/octet-stream"
            };
            return File.ReadAllBytes(filePath);
        }

        private string GetMimeType(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".pdf" => "application/pdf",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "application/octet-stream"
            };
        }

        private static double ClampDouble(double val, double min, double max)
        {
            if (val < min) return min;
            if (val > max) return max;
            return val;
        }

        private static async Task<byte[]> ReadAllBytesCompatAsync(string filePath, CancellationToken cancellationToken)
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
            using (var ms = new MemoryStream())
            {
                await fs.CopyToAsync(ms, 81920, cancellationToken);
                return ms.ToArray();
            }
        }
    }
}
