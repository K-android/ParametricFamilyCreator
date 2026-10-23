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
        /// Two-pass multimodal reasoning system instruction for Gemini Vision.
        /// </summary>
        public const string FamilyForgeSystemInstruction = 
@"You are a Principal BIM/VDC Lead and Autodesk Revit API Parametric Content Specialist.
Your objective is to analyze architectural cut-sheets (PDF or image), submittal CAD prints, or user prompts,
and synthesize a millimeter-accurate, production-ready Revit Family specification JSON.

HYBRID PARAMETER EXTRACTION RULES:
1. USER EXPLICIT PRIORITY (HIGHEST): If the user's prompt explicitly mentions any parameter or dimension (e.g. 'Width=1800mm', 'Depth=900mm', 'Height=750mm', 'Leg_Diameter=40mm'), YOU MUST ENFORCE THOSE EXACT VALUES. User overrides supersede any conflicting numbers visible on the image/PDF.
2. AUTOMATIC PDF/IMAGE INFERENCE: For any parameter NOT explicitly dictated by the user:
   - Carefully inspect dimension lines, leader arrows, dimension strings, and schedules in the PDF/image.
   - If the drawing uses Imperial units (feet/inches, e.g. 3'-0"", 36"", 1/2""), automatically convert precisely to millimeters (1 inch = 25.4mm).
   - If a dimension is omitted on the cut-sheet, infer standard architectural/ergonomic conventions (e.g. standard desk height=720-750mm, table depth=700-800mm, door leaf thickness=44mm).
3. REFERENCE PLANES & ORIGIN:
   - The family origin (0,0,0) represents the intersection of Center (Left/Right) and Center (Front/Back) on the Reference Level.
   - Center-symmetric families should bound MinX to -Width/2 and MaxX to +Width/2.
4. GEOMETRY & PROFILES:
   - Supported profile_type values: 'Rectangle', 'Cylinder' (specify radius_mm, min_x_mm/min_y_mm as center), and 'HollowBox' (specify wall_thickness_mm for frames/tubes).
   - StartZ and EndZ define vertical extrusion limits.
5. NESTED SUB-COMPONENTS & HARDWARE:
   - Decompose distinct sub-assemblies (e.g., door handles, locks, sashes, table legs, casters) into 'nested_families'.
   - Shared Families: Set 'is_shared: true' on nested families when the sub-component should be independently schedulable in Revit project schedules.
   - Parameter Bindings: Map sub-component parameters to host master parameters (e.g., {'Handle_Length': 'Lever_Length', 'Leg_Height': 'Table_Height'}).
6. RETURN ONLY VALID JSON strictly following the requested schema without any markdown formatting or commentary.";

        /// <summary>
        /// Sends prompt and optional image to Gemini API and receives a validated FamilySchema.
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
                // Fallback to deterministic local engine if no API key provided
                return GenerateDeterministicFallback(userPrompt, imageFilePath);
            }

            var parts = new List<object>();

            // If an image or PDF is provided, encode to base64
            if (!string.IsNullOrEmpty(imageFilePath) && File.Exists(imageFilePath))
            {
                byte[] imageBytes = await ReadAllBytesCompatAsync(imageFilePath!, cancellationToken);
                string mimeType = GetMimeType(imageFilePath!);
                parts.Add(new
                {
                    inline_data = new
                    {
                        mime_type = mimeType,
                        data = Convert.ToBase64String(imageBytes)
                    }
                });
            }

            // User prompt
            parts.Add(new
            {
                text = $"Generate Revit Parametric Family specification for: {userPrompt}"
            });

            // JSON Schema declaration for Gemini Structured Outputs
            var requestBody = new
            {
                system_instruction = new
                {
                    parts = new[]
                    {
                        new { text = FamilyForgeSystemInstruction }
                    }
                },
                contents = new[]
                {
                    new { parts = parts.ToArray() }
                },
                generationConfig = new
                {
                    temperature = ClampDouble(temperature, 0.0, 1.0),
                    response_mime_type = "application/json",
                    max_output_tokens = 2500
                }
            };

            string jsonPayload = JsonSerializer.Serialize(requestBody);

            // Clean up and normalize model name
            string cleanModel = string.IsNullOrWhiteSpace(modelName) ? DefaultGeminiModel : modelName.Trim();
            if (cleanModel.Equals("gemini-3.8", StringComparison.OrdinalIgnoreCase))
            {
                cleanModel = "gemini-3.8-flash";
            }
            else if (cleanModel.Contains("3.6"))
            {
                cleanModel = "gemini-3.8-flash";
            }

            // Candidate models: prioritize the user's selected model, followed by reliable fallbacks
            var candidateModels = new List<string> { cleanModel };
            if (!cleanModel.Equals("gemini-3.8-flash", StringComparison.OrdinalIgnoreCase))
                candidateModels.Add("gemini-3.8-flash");
            if (!cleanModel.Equals("gemini-2.5-flash", StringComparison.OrdinalIgnoreCase))
                candidateModels.Add("gemini-2.5-flash");
            if (!cleanModel.Equals("gemini-2.0-flash", StringComparison.OrdinalIgnoreCase))
                candidateModels.Add("gemini-2.0-flash");

            string primaryError = "";

            foreach (var targetModel in candidateModels)
            {
                try
                {
                    string endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{targetModel}:generateContent?key={apiKey}";
                    using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    HttpResponseMessage response = await _httpClient.PostAsync(endpoint, content, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        string rawJsonResponse = await response.Content.ReadAsStringAsync();
                        return ParseGeminiResponse(rawJsonResponse);
                    }

                    string errorMsg = await response.Content.ReadAsStringAsync();
                    
                    if (string.IsNullOrEmpty(primaryError))
                    {
                        primaryError = $"[Model: {targetModel} - Status: {response.StatusCode}]: {errorMsg}";
                    }

                    // If API key authentication error, DO NOT attempt other models. Throw immediately so the user knows.
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                        response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                        response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        throw new InvalidOperationException(
                            $"Gemini API Authentication Failed for model '{targetModel}':\n{errorMsg}\n\n" +
                            "Please check your API key in the Settings panel (get a free key at https://aistudio.google.com)."
                        );
                    }

                    // If 404 (model not found on API), try next candidate model
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        continue;
                    }
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrEmpty(primaryError))
                    {
                        primaryError = ex.Message;
                    }
                }
            }

            throw new InvalidOperationException($"Gemini API Error for '{cleanModel}':\n{primaryError}\n\nPlease verify your API key at aistudio.google.com.");
        }

        private FamilySchema ParseGeminiResponse(string rawJson)
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
                if (firstCandidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0)
                {
                    string textContent = parts[0].GetProperty("text").GetString() ?? "{}";
                    
                    textContent = textContent.Trim();
                    if (textContent.StartsWith("```json"))
                    {
                        textContent = textContent.Substring(7);
                    }
                    else if (textContent.StartsWith("```"))
                    {
                        textContent = textContent.Substring(3);
                    }
                    if (textContent.EndsWith("```"))
                    {
                        textContent = textContent.Substring(0, textContent.Length - 3);
                    }
                    textContent = textContent.Trim();

                    int firstBrace = textContent.IndexOf('{');
                    int lastBrace = textContent.LastIndexOf('}');
                    if (firstBrace >= 0 && lastBrace > firstBrace)
                    {
                        textContent = textContent.Substring(firstBrace, lastBrace - firstBrace + 1);
                    }

                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };

                    var schema = JsonSerializer.Deserialize<FamilySchema>(textContent, options);
                    if (schema != null)
                    {
                        return schema;
                    }
                }
            }

            throw new InvalidOperationException("Failed to extract valid FamilySchema from Gemini response.");
        }

        /// <summary>
        /// Robust deterministic local rule-based engine.
        /// Synthesizes architectural components from prompts, PDF cut-sheets, and images offline without an API key.
        /// </summary>
        public FamilySchema GenerateDeterministicFallback(string prompt, string? imageOrPdfPath = null)
        {
            string pdfText = "";
            string imageText = "";

            if (!string.IsNullOrEmpty(imageOrPdfPath) && File.Exists(imageOrPdfPath))
            {
                string ext = Path.GetExtension(imageOrPdfPath).ToLowerInvariant();
                if (ext == ".pdf")
                {
                    pdfText = ExtractTextFromPdf(imageOrPdfPath!);
                }
                else
                {
                    imageText = ExtractTextFromImageFilename(imageOrPdfPath!);
                }
            }

            string combinedText = $"{prompt} {pdfText} {imageText}".Trim();
            string lower = combinedText.ToLowerInvariant();
            var dims = ExtractDimensionsFromPrompt(combinedText);

            // 1. Doors
            if (lower.Contains("door") || lower.Contains("gate") || lower.Contains("entryway"))
            {
                return CreateDoorSchema(dims.w ?? 900, dims.h ?? 2100, dims.thk ?? 44);
            }
            // 2. Windows & Louvers
            else if (lower.Contains("window") || lower.Contains("louver") || lower.Contains("glazing") || lower.Contains("casement"))
            {
                return CreateWindowSchema(dims.w ?? 1200, dims.h ?? 1500, dims.d ?? 100);
            }
            // 3. Chairs, Armchairs, Sofas & Seating
            else if (lower.Contains("chair") || lower.Contains("armchair") || lower.Contains("sofa") || lower.Contains("couch") || lower.Contains("stool") || lower.Contains("bench") || lower.Contains("ottoman") || lower.Contains("seating"))
            {
                return CreateChairSchema(dims.w ?? 600, dims.d ?? 600, dims.h ?? 850, dims.thk ?? 50);
            }
            // 4. Tables, Desks & Workstations
            else if (lower.Contains("table") || lower.Contains("desk") || lower.Contains("workstation") || lower.Contains("counter") || lower.Contains("credenza"))
            {
                return CreateWorkstationSchema(dims.w ?? 1600, dims.d ?? 800, dims.h ?? 750, dims.thk ?? 30);
            }
            // 5. Casework, Cabinets & Shelving
            else if (lower.Contains("cabinet") || lower.Contains("shelf") || lower.Contains("shelving") || lower.Contains("bookcase") || lower.Contains("wardrobe") || lower.Contains("cupboard") || lower.Contains("drawer") || lower.Contains("casework") || lower.Contains("storage") || lower.Contains("locker"))
            {
                return CreateCaseworkSchema(dims.w ?? 900, dims.d ?? 450, dims.h ?? 1800, dims.thk ?? 20);
            }
            // 6. Structural Connections & Baseplates
            else if (lower.Contains("baseplate") || lower.Contains("joint") || lower.Contains("bracket") || lower.Contains("plate") || lower.Contains("connection") || lower.Contains("gusset") || lower.Contains("flange"))
            {
                return CreateBaseplateJointSchema(dims.w ?? 300, dims.d ?? 300, dims.thk ?? 25, dims.h ?? 300);
            }
            // 7. Canopies, Trellises & Facades
            else if (lower.Contains("canopy") || lower.Contains("trellis") || lower.Contains("pergola") || lower.Contains("sunshade") || lower.Contains("shading") || lower.Contains("awning") || lower.Contains("overhang"))
            {
                return CreateCanopySchema(dims.w ?? 2000, dims.d ?? 1200, dims.thk ?? 100, dims.h ?? 400);
            }
            // 8. Columns & Posts
            else if (lower.Contains("column") || lower.Contains("post") || lower.Contains("pillar") || lower.Contains("pier") || lower.Contains("stanchion"))
            {
                return CreateColumnSchema(dims.w ?? 400, dims.d ?? 400, dims.h ?? 3000);
            }
            // 9. Universal Dynamic Fallback (Handles ANY custom architectural object, FF&E, or prompt)
            else
            {
                return CreateDynamicUniversalSchema(combinedText, dims.w ?? 1000, dims.d ?? 600, dims.h ?? 900, dims.thk ?? 25);
            }
        }

        private static string ExtractTextFromPdf(string pdfPath)
        {
            try
            {
                if (!File.Exists(pdfPath)) return "";
                byte[] bytes = File.ReadAllBytes(pdfPath);
                string raw = Encoding.ASCII.GetString(bytes);

                var sb = new StringBuilder();

                // Extract parenthesized text streams in PDF: (text) Tj
                var matches = Regex.Matches(raw, @"\(([^)]+)\)\s*T[jJ]");
                foreach (Match m in matches)
                {
                    if (m.Groups.Count > 1)
                    {
                        sb.Append(m.Groups[1].Value).Append(" ");
                    }
                }

                // Also match dimension tokens e.g. 1200x600, W=1500, H:2100
                var dimMatches = Regex.Matches(raw, @"\b\d{2,5}\s*(?:mm|cm|m|in|\""|x|by|\*)\b", RegexOptions.IgnoreCase);
                foreach (Match dm in dimMatches)
                {
                    sb.Append(dm.Value).Append(" ");
                }

                return sb.ToString();
            }
            catch
            {
                return "";
            }
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

        private static (double? w, double? d, double? h, double? thk) ExtractDimensionsFromPrompt(string prompt)
        {
            double? w = null, d = null, h = null, thk = null;

            // Pattern: 1800x900x750 or 1800 x 900 x 750 or 1800*900*750
            var tripleMatch = Regex.Match(prompt, @"(\d{2,5})\s*(?:x|by|\*)\s*(\d{2,5})\s*(?:x|by|\*)\s*(\d{2,5})", RegexOptions.IgnoreCase);
            if (tripleMatch.Success)
            {
                if (double.TryParse(tripleMatch.Groups[1].Value, out double val1)) w = val1;
                if (double.TryParse(tripleMatch.Groups[2].Value, out double val2)) d = val2;
                if (double.TryParse(tripleMatch.Groups[3].Value, out double val3)) h = val3;
            }
            else
            {
                // Pattern: 900x2100 (Width x Height)
                var doubleMatch = Regex.Match(prompt, @"(\d{2,5})\s*(?:x|by|\*)\s*(\d{2,5})", RegexOptions.IgnoreCase);
                if (doubleMatch.Success)
                {
                    if (double.TryParse(doubleMatch.Groups[1].Value, out double val1)) w = val1;
                    if (double.TryParse(doubleMatch.Groups[2].Value, out double val2)) h = val2;
                }
            }

            // Keyword matches
            var wm = Regex.Match(prompt, @"(?:width|w|len|length)[:=\s]+(\d{2,5})", RegexOptions.IgnoreCase);
            if (wm.Success && double.TryParse(wm.Groups[1].Value, out double vw)) w = vw;

            var dm = Regex.Match(prompt, @"(?:depth|d)[:=\s]+(\d{2,5})", RegexOptions.IgnoreCase);
            if (dm.Success && double.TryParse(dm.Groups[1].Value, out double vd)) d = vd;

            var hm = Regex.Match(prompt, @"(?:height|h)[:=\s]+(\d{2,5})", RegexOptions.IgnoreCase);
            if (hm.Success && double.TryParse(hm.Groups[1].Value, out double vh)) h = vh;

            var thkm = Regex.Match(prompt, @"(?:thickness|thick|thk)[:=\s]+(\d{1,4})", RegexOptions.IgnoreCase);
            if (thkm.Success && double.TryParse(thkm.Groups[1].Value, out double vthk)) thk = vthk;

            return (w, d, h, thk);
        }

        private static string CleanPromptToFamilyName(string prompt)
        {
            string cleaned = Regex.Replace(prompt, @"\b\d+[\w]*\b", "");
            cleaned = Regex.Replace(cleaned, @"\b(?:mm|cm|m|width|height|depth|thick|thickness|with|and|the|a|an|parametric|revit|family)\b", "", RegexOptions.IgnoreCase);
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
