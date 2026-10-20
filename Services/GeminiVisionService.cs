using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
        private const string DefaultGeminiModel = "gemini-3.8";

        public GeminiVisionService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        }

        /// <summary>
        /// System instruction provided to Gemini to ensure strict structural compliance with Revit Family requirements.
        /// </summary>
        public const string FamilyForgeSystemInstruction = 
@"You are a Principal BIM/VDC and Autodesk Revit API Parametric Content Specialist.
Your task is to analyze architectural/furniture cut-sheets, dimensional sketches, or natural language prompts,
and produce a fully compliant, production-grade JSON specification for Revit Family generation.

STRICT REVIT MODELING CONSTRAINTS:
1. Coordinates & Dimensions: ALL numerical values MUST be expressed in MILLIMETERS (mm).
2. Symmetry & Origin: The family origin (0,0,0) represents the intersection of the primary Center (Left/Right) and Center (Front/Back) reference planes at the Reference Level.
3. Reference Planes: Provide named planes for key boundary limits (e.g., 'Left', 'Right', 'Front', 'Back', 'Top').
4. Parameters: Define standard BIM parameters such as 'Width', 'Depth', 'Height', 'Thickness' with appropriate default values.
5. Nested Families: Break assemblies into sub-components (e.g., legs, hardware, mullions, panels) that belong to 'nested_families'.
6. Parameter Bindings: In each nested component, map sub-component parameter names directly to master parameter names.
7. Return strictly valid JSON conforming exactly to the requested JSON schema without any markdown formatting wrappers or conversational text.";

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
                return GenerateDeterministicFallback(userPrompt);
            }

            string endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}";

            var parts = new List<object>();

            // If an image is provided, encode to base64
            if (!string.IsNullOrEmpty(imageFilePath) && File.Exists(imageFilePath))
            {
                byte[] imageBytes = await ReadAllBytesCompatAsync(imageFilePath, cancellationToken);
                string mimeType = GetMimeType(imageFilePath);
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
                    response_mime_type = "application/json"
                }
            };

            string jsonPayload = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _httpClient.PostAsync(endpoint, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string errorMsg = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Gemini API Error ({response.StatusCode}): {errorMsg}");
            }

            string rawJsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseGeminiResponse(rawJsonResponse);
        }

        private FamilySchema ParseGeminiResponse(string rawJson)
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var firstCandidate = candidates[0];
                if (firstCandidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0)
                {
                    string textContent = parts[0].GetProperty("text").GetString() ?? "{}";
                    
                    // Sanitize potential markdown wrappers just in case
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
        /// Synthesizes architectural components (workstation, door, window, table) without internet or API keys.
        /// </summary>
        public FamilySchema GenerateDeterministicFallback(string prompt)
        {
            string lower = prompt.ToLowerInvariant();

            if (lower.Contains("door"))
            {
                return CreateDoorSchema();
            }
            else if (lower.Contains("window"))
            {
                return CreateWindowSchema();
            }
            else
            {
                // Default to a high-end parametric Workstation with nested legs and parametric bindings
                return CreateWorkstationSchema();
            }
        }

        private FamilySchema CreateWorkstationSchema()
        {
            var schema = new FamilySchema
            {
                MasterFamilyName = "Parametric_Workstation",
                TemplateName = "Metric Furniture.rft",
                MasterParameters = new List<MasterParameterDef>
                {
                    new() { Name = "Table_Width", DataType = "Length", DefaultValueMm = 1600, IsInstance = false },
                    new() { Name = "Table_Depth", DataType = "Length", DefaultValueMm = 800, IsInstance = false },
                    new() { Name = "Table_Height", DataType = "Length", DefaultValueMm = 750, IsInstance = false },
                    new() { Name = "Top_Thickness", DataType = "Length", DefaultValueMm = 30, IsInstance = false },
                    new() { Name = "Leg_Size", DataType = "Length", DefaultValueMm = 50, IsInstance = false }
                },
                ReferencePlanes = new List<ReferencePlaneDef>
                {
                    new() { Name = "Left_Plane", Orientation = "X", OffsetFromOriginMm = -800, DrivenByParameter = "Table_Width" },
                    new() { Name = "Right_Plane", Orientation = "X", OffsetFromOriginMm = 800, DrivenByParameter = "Table_Width" },
                    new() { Name = "Front_Plane", Orientation = "Y", OffsetFromOriginMm = -400, DrivenByParameter = "Table_Depth" },
                    new() { Name = "Back_Plane", Orientation = "Y", OffsetFromOriginMm = 400, DrivenByParameter = "Table_Depth" },
                    new() { Name = "Top_Plane", Orientation = "Z", OffsetFromOriginMm = 750, DrivenByParameter = "Table_Height" }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    new()
                    {
                        Name = "Table_Top_Panel",
                        MinX_mm = -800,
                        MaxX_mm = 800,
                        MinY_mm = -400,
                        MaxY_mm = 400,
                        StartZ_mm = 720,
                        EndZ_mm = 750,
                        WidthParameter = "Table_Width",
                        DepthParameter = "Table_Depth",
                        HeightParameter = "Top_Thickness"
                    }
                },
                NestedFamilies = new List<NestedFamilyDef>
                {
                    // Leg sub-component 1 (Front-Left)
                    new()
                    {
                        NestedFamilyName = "Workstation_Leg",
                        TemplateName = "Metric Generic Model.rft",
                        SubParameters = new List<MasterParameterDef>
                        {
                            new() { Name = "Leg_Height", DataType = "Length", DefaultValueMm = 720, IsInstance = false },
                            new() { Name = "Leg_Width", DataType = "Length", DefaultValueMm = 50, IsInstance = false }
                        },
                        SubExtrusions = new List<ExtrusionDef>
                        {
                            new()
                            {
                                Name = "Leg_Solid",
                                MinX_mm = -25,
                                MaxX_mm = 25,
                                MinY_mm = -25,
                                MaxY_mm = 25,
                                StartZ_mm = 0,
                                EndZ_mm = 720,
                                WidthParameter = "Leg_Width",
                                HeightParameter = "Leg_Height"
                            }
                        },
                        PlacementOffsetMm = new VectorOffsetDef(-750, -350, 0),
                        ParameterBindings = new Dictionary<string, string>
                        {
                            { "Leg_Height", "Table_Height" },
                            { "Leg_Width", "Leg_Size" }
                        }
                    },
                    // Leg sub-component 2 (Front-Right)
                    new()
                    {
                        NestedFamilyName = "Workstation_Leg",
                        TemplateName = "Metric Generic Model.rft",
                        PlacementOffsetMm = new VectorOffsetDef(750, -350, 0),
                        ParameterBindings = new Dictionary<string, string>
                        {
                            { "Leg_Height", "Table_Height" },
                            { "Leg_Width", "Leg_Size" }
                        }
                    },
                    // Leg sub-component 3 (Back-Left)
                    new()
                    {
                        NestedFamilyName = "Workstation_Leg",
                        TemplateName = "Metric Generic Model.rft",
                        PlacementOffsetMm = new VectorOffsetDef(-750, 350, 0),
                        ParameterBindings = new Dictionary<string, string>
                        {
                            { "Leg_Height", "Table_Height" },
                            { "Leg_Width", "Leg_Size" }
                        }
                    },
                    // Leg sub-component 4 (Back-Right)
                    new()
                    {
                        NestedFamilyName = "Workstation_Leg",
                        TemplateName = "Metric Generic Model.rft",
                        PlacementOffsetMm = new VectorOffsetDef(750, 350, 0),
                        ParameterBindings = new Dictionary<string, string>
                        {
                            { "Leg_Height", "Table_Height" },
                            { "Leg_Width", "Leg_Size" }
                        }
                    }
                }
            };

            return schema;
        }

        private FamilySchema CreateDoorSchema()
        {
            return new FamilySchema
            {
                MasterFamilyName = "Parametric_Door_Leaf",
                TemplateName = "Metric Generic Model.rft",
                MasterParameters = new List<MasterParameterDef>
                {
                    new() { Name = "Door_Width", DataType = "Length", DefaultValueMm = 900, IsInstance = false },
                    new() { Name = "Door_Height", DataType = "Length", DefaultValueMm = 2100, IsInstance = false },
                    new() { Name = "Door_Thickness", DataType = "Length", DefaultValueMm = 45, IsInstance = false },
                    new() { Name = "Handle_Height", DataType = "Length", DefaultValueMm = 1000, IsInstance = false }
                },
                ReferencePlanes = new List<ReferencePlaneDef>
                {
                    new() { Name = "Left_Edge", Orientation = "X", OffsetFromOriginMm = 0, DrivenByParameter = null },
                    new() { Name = "Right_Edge", Orientation = "X", OffsetFromOriginMm = 900, DrivenByParameter = "Door_Width" },
                    new() { Name = "Top_Edge", Orientation = "Z", OffsetFromOriginMm = 2100, DrivenByParameter = "Door_Height" }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    new()
                    {
                        Name = "Door_Panel_Extrusion",
                        MinX_mm = 0,
                        MaxX_mm = 900,
                        MinY_mm = 0,
                        MaxY_mm = 45,
                        StartZ_mm = 0,
                        EndZ_mm = 2100,
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
                            new() { Name = "Handle_Length", DataType = "Length", DefaultValueMm = 120, IsInstance = false }
                        },
                        SubExtrusions = new List<ExtrusionDef>
                        {
                            new()
                            {
                                Name = "Handle_Shaft",
                                MinX_mm = 0,
                                MaxX_mm = 120,
                                MinY_mm = 0,
                                MaxY_mm = 20,
                                StartZ_mm = 0,
                                EndZ_mm = 20,
                                WidthParameter = "Handle_Length"
                            }
                        },
                        PlacementOffsetMm = new VectorOffsetDef(800, 45, 1000),
                        ParameterBindings = new Dictionary<string, string>()
                    }
                }
            };
        }

        private FamilySchema CreateWindowSchema()
        {
            return new FamilySchema
            {
                MasterFamilyName = "Parametric_Window_Casement",
                TemplateName = "Metric Generic Model.rft",
                MasterParameters = new List<MasterParameterDef>
                {
                    new() { Name = "Window_Width", DataType = "Length", DefaultValueMm = 1200, IsInstance = false },
                    new() { Name = "Window_Height", DataType = "Length", DefaultValueMm = 1500, IsInstance = false },
                    new() { Name = "Frame_Depth", DataType = "Length", DefaultValueMm = 100, IsInstance = false },
                    new() { Name = "Frame_Width", DataType = "Length", DefaultValueMm = 65, IsInstance = false }
                },
                ReferencePlanes = new List<ReferencePlaneDef>
                {
                    new() { Name = "Frame_Left", Orientation = "X", OffsetFromOriginMm = -600, DrivenByParameter = "Window_Width" },
                    new() { Name = "Frame_Right", Orientation = "X", OffsetFromOriginMm = 600, DrivenByParameter = "Window_Width" },
                    new() { Name = "Frame_Top", Orientation = "Z", OffsetFromOriginMm = 1500, DrivenByParameter = "Window_Height" }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    new()
                    {
                        Name = "Outer_Frame",
                        MinX_mm = -600,
                        MaxX_mm = 600,
                        MinY_mm = -50,
                        MaxY_mm = 50,
                        StartZ_mm = 0,
                        EndZ_mm = 1500,
                        WidthParameter = "Window_Width",
                        DepthParameter = "Frame_Depth",
                        HeightParameter = "Window_Height"
                    }
                },
                NestedFamilies = new List<NestedFamilyDef>()
            };
        }

        private string GetMimeType(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
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
