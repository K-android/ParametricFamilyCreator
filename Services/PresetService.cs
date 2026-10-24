using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.Services
{
    public class PresetInfo
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Manages built-in architectural family presets, JSON schema export/import,
    /// and Revit Type Catalog CSV schedule generation.
    /// </summary>
    public static class PresetService
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static IReadOnlyList<PresetInfo> GetAvailablePresets()
        {
            return new List<PresetInfo>
            {
                new PresetInfo
                {
                    Key = "workstation",
                    DisplayName = "Ergonomic Office Workstation (Desk + 4 Nested Legs)",
                    Category = "Furniture",
                    Description = "Modern office desk with 25mm desktop slab, modesty panel, 4 nested steel tubular legs, and 4 size variations."
                },
                new PresetInfo
                {
                    Key = "commercial_door",
                    DisplayName = "Commercial Flush Door (Leaf, Frame & Lever)",
                    Category = "Doors",
                    Description = "Single-swing flush door with 50mm perimeter frame, door leaf, vision lite glazing, and nested lever handle."
                },
                new PresetInfo
                {
                    Key = "casework_cabinet",
                    DisplayName = "Casework Base Cabinet (Countertop & Pull)",
                    Category = "Casework",
                    Description = "Kitchen/laboratory base cabinet carcass with toe kick, 30mm countertop, and nested bar pull hardware."
                },
                new PresetInfo
                {
                    Key = "column",
                    DisplayName = "Architectural Tuscan Column (Plinth, Shaft, Capital)",
                    Category = "Columns",
                    Description = "Classic architectural column with stepped plinth base, cylindrical shaft, and molded capital."
                },
                new PresetInfo
                {
                    Key = "conference_table",
                    DisplayName = "Executive Conference Table (Top & Twin Pedestals)",
                    Category = "Furniture",
                    Description = "Heavy conference table with beveled perimeter top slab, modesty structure, and dual pedestal supports."
                }
            };
        }

        public static FamilySchema GetPresetSchema(string key)
        {
            switch (key.ToLowerInvariant())
            {
                case "commercial_door":
                    return CreateDoorPreset();
                case "casework_cabinet":
                    return CreateCaseworkPreset();
                case "column":
                    return CreateColumnPreset();
                case "conference_table":
                    return CreateConferenceTablePreset();
                case "workstation":
                default:
                    return CreateWorkstationPreset();
            }
        }

        #region Built-In Preset Factories

        private static FamilySchema CreateWorkstationPreset()
        {
            return new FamilySchema
            {
                MasterFamilyName = "Workstation_Desk_Ergonomic",
                FamilyCategory = "Furniture",
                TemplateName = "Metric Generic Model.rft",
                Description = "High-performance modular office workstation with nested steel tubular legs and multiple standard footprint sizes.",
                Manufacturer = "Forge AEC Furnishings",
                MasterParameters = new List<MasterParameterDef>
                {
                    new MasterParameterDef { Name = "Width", DataType = "Length", DefaultValueMm = 1600, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Depth", DataType = "Length", DefaultValueMm = 800, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Height", DataType = "Length", DefaultValueMm = 750, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Top_Thickness", DataType = "Length", DefaultValueMm = 25, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Leg_Diameter", DataType = "Length", DefaultValueMm = 50, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Desktop_Material", DataType = "Material", GroupType = "Materials" },
                    new MasterParameterDef { Name = "Leg_Material", DataType = "Material", GroupType = "Materials" },
                    new MasterParameterDef { Name = "Show_Modesty_Panel", DataType = "YesNo", DefaultValue = 1, GroupType = "Visibility" }
                },
                FamilyTypes = new List<FamilyTypeDef>
                {
                    new FamilyTypeDef
                    {
                        Name = "1200 x 600 mm",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "Width", 1200.0 }, { "Depth", 600.0 }, { "Height", 750.0 }
                        }
                    },
                    new FamilyTypeDef
                    {
                        Name = "1400 x 700 mm",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "Width", 1400.0 }, { "Depth", 700.0 }, { "Height", 750.0 }
                        }
                    },
                    new FamilyTypeDef
                    {
                        Name = "1600 x 800 mm (Standard)",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "Width", 1600.0 }, { "Depth", 800.0 }, { "Height", 750.0 }
                        }
                    },
                    new FamilyTypeDef
                    {
                        Name = "1800 x 800 mm (Executive)",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "Width", 1800.0 }, { "Depth", 800.0 }, { "Height", 750.0 }
                        }
                    }
                },
                ReferencePlanes = new List<ReferencePlaneDef>
                {
                    new ReferencePlaneDef { Name = "Left", Orientation = "X", OffsetFromOriginMm = -800, DrivenByParameter = "Width" },
                    new ReferencePlaneDef { Name = "Right", Orientation = "X", OffsetFromOriginMm = 800, DrivenByParameter = "Width" },
                    new ReferencePlaneDef { Name = "Front", Orientation = "Y", OffsetFromOriginMm = -400, DrivenByParameter = "Depth" },
                    new ReferencePlaneDef { Name = "Back", Orientation = "Y", OffsetFromOriginMm = 400, DrivenByParameter = "Depth" },
                    new ReferencePlaneDef { Name = "Top", Orientation = "Z", OffsetFromOriginMm = 750, DrivenByParameter = "Height" }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    // Tabletop Slab
                    new ExtrusionDef
                    {
                        Name = "Workstation_Desktop",
                        ProfileType = "Rectangle",
                        MinX_mm = -800, MaxX_mm = 800,
                        MinY_mm = -400, MaxY_mm = 400,
                        StartZ_mm = 725, EndZ_mm = 750,
                        HeightParameter = "Height",
                        WidthParameter = "Width",
                        DepthParameter = "Depth",
                        MaterialParameter = "Desktop_Material"
                    },
                    // Modesty Panel
                    new ExtrusionDef
                    {
                        Name = "Modesty_Panel",
                        ProfileType = "Rectangle",
                        MinX_mm = -700, MaxX_mm = 700,
                        MinY_mm = 280, MaxY_mm = 300,
                        StartZ_mm = 350, EndZ_mm = 725,
                        MaterialParameter = "Desktop_Material",
                        VisibilityParameter = "Show_Modesty_Panel"
                    }
                },
                NestedFamilies = new List<NestedFamilyDef>
                {
                    // Cylindrical Steel Leg
                    new NestedFamilyDef
                    {
                        NestedFamilyName = "Workstation_Tubular_Leg",
                        TemplateName = "Metric Generic Model.rft",
                        IsShared = false,
                        SubParameters = new List<MasterParameterDef>
                        {
                            new MasterParameterDef { Name = "Leg_Height", DataType = "Length", DefaultValueMm = 725 },
                            new MasterParameterDef { Name = "Leg_Radius", DataType = "Length", DefaultValueMm = 25 }
                        },
                        SubExtrusions = new List<ExtrusionDef>
                        {
                            new ExtrusionDef
                            {
                                Name = "Leg_Column",
                                ProfileType = "Cylinder",
                                MinX_mm = -25, MaxX_mm = 25,
                                MinY_mm = -25, MaxY_mm = 25,
                                Radius_mm = 25,
                                StartZ_mm = 0, EndZ_mm = 725,
                                HeightParameter = "Leg_Height"
                            }
                        },
                        PlacementOffsetMm = new VectorOffsetDef(-740, -340, 0),
                        ParameterBindings = new Dictionary<string, string>
                        {
                            { "Leg_Height", "Height" }
                        }
                    }
                }
            };
        }

        private static FamilySchema CreateDoorPreset()
        {
            return new FamilySchema
            {
                MasterFamilyName = "Commercial_Flush_Door",
                FamilyCategory = "Doors",
                TemplateName = "Metric Generic Model.rft",
                Description = "Commercial grade flush door with peripheral jamb frame, vision lite glazing, and lever handle.",
                Manufacturer = "Forge Architectural Openings",
                MasterParameters = new List<MasterParameterDef>
                {
                    new MasterParameterDef { Name = "Width", DataType = "Length", DefaultValueMm = 900, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Height", DataType = "Length", DefaultValueMm = 2100, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Thickness", DataType = "Length", DefaultValueMm = 45, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Frame_Width", DataType = "Length", DefaultValueMm = 50, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Door_Material", DataType = "Material", GroupType = "Materials" },
                    new MasterParameterDef { Name = "Frame_Material", DataType = "Material", GroupType = "Materials" },
                    new MasterParameterDef { Name = "Glazing_Material", DataType = "Material", GroupType = "Materials" },
                    new MasterParameterDef { Name = "Show_Vision_Lite", DataType = "YesNo", DefaultValue = 1, GroupType = "Visibility" }
                },
                FamilyTypes = new List<FamilyTypeDef>
                {
                    new FamilyTypeDef
                    {
                        Name = "800 x 2100 mm",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "Width", 800.0 }, { "Height", 2100.0 }
                        }
                    },
                    new FamilyTypeDef
                    {
                        Name = "900 x 2100 mm (Standard)",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "Width", 900.0 }, { "Height", 2100.0 }
                        }
                    },
                    new FamilyTypeDef
                    {
                        Name = "1000 x 2100 mm (ADA Compliant)",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "Width", 1000.0 }, { "Height", 2100.0 }
                        }
                    }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    // Outer Frame (Hollow perimeter)
                    new ExtrusionDef
                    {
                        Name = "Door_Jamb_Frame",
                        ProfileType = "HollowBox",
                        MinX_mm = -450, MaxX_mm = 450,
                        MinY_mm = -50, MaxY_mm = 50,
                        WallThickness_mm = 50,
                        StartZ_mm = 0, EndZ_mm = 2150,
                        HeightParameter = "Height",
                        WidthParameter = "Width",
                        MaterialParameter = "Frame_Material"
                    },
                    // Door Leaf Slab
                    new ExtrusionDef
                    {
                        Name = "Door_Leaf",
                        ProfileType = "Rectangle",
                        MinX_mm = -400, MaxX_mm = 400,
                        MinY_mm = -22, MaxY_mm = 22,
                        StartZ_mm = 0, EndZ_mm = 2100,
                        HeightParameter = "Height",
                        WidthParameter = "Width",
                        MaterialParameter = "Door_Material"
                    },
                    // Vision Lite Glass Insert
                    new ExtrusionDef
                    {
                        Name = "Vision_Lite_Glass",
                        ProfileType = "Rectangle",
                        MinX_mm = -100, MaxX_mm = 100,
                        MinY_mm = -6, MaxY_mm = 6,
                        StartZ_mm = 1200, EndZ_mm = 1800,
                        MaterialParameter = "Glazing_Material",
                        VisibilityParameter = "Show_Vision_Lite"
                    }
                },
                NestedFamilies = new List<NestedFamilyDef>
                {
                    new NestedFamilyDef
                    {
                        NestedFamilyName = "Door_Lever_Handle",
                        TemplateName = "Metric Generic Model.rft",
                        IsShared = false,
                        SubParameters = new List<MasterParameterDef>
                        {
                            new MasterParameterDef { Name = "Handle_Height", DataType = "Length", DefaultValueMm = 1000 }
                        },
                        SubExtrusions = new List<ExtrusionDef>
                        {
                            new ExtrusionDef
                            {
                                Name = "Lever_Bar",
                                ProfileType = "Rectangle",
                                MinX_mm = 0, MaxX_mm = 120,
                                MinY_mm = -10, MaxY_mm = 10,
                                StartZ_mm = 0, EndZ_mm = 20
                            }
                        },
                        PlacementOffsetMm = new VectorOffsetDef(320, 30, 1000)
                    }
                }
            };
        }

        private static FamilySchema CreateCaseworkPreset()
        {
            return new FamilySchema
            {
                MasterFamilyName = "Casework_Base_Cabinet",
                FamilyCategory = "Casework",
                TemplateName = "Metric Generic Model.rft",
                Description = "Parametric base storage unit with solid surface countertop, carcass, toe kick, and hardware pull.",
                Manufacturer = "Forge Millwork Systems",
                MasterParameters = new List<MasterParameterDef>
                {
                    new MasterParameterDef { Name = "Width", DataType = "Length", DefaultValueMm = 800, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Depth", DataType = "Length", DefaultValueMm = 600, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Height", DataType = "Length", DefaultValueMm = 860, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Countertop_Thickness", DataType = "Length", DefaultValueMm = 30, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Cabinet_Material", DataType = "Material", GroupType = "Materials" },
                    new MasterParameterDef { Name = "Countertop_Material", DataType = "Material", GroupType = "Materials" }
                },
                FamilyTypes = new List<FamilyTypeDef>
                {
                    new FamilyTypeDef
                    {
                        Name = "600 mm Single Base",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { { "Width", 600.0 } }
                    },
                    new FamilyTypeDef
                    {
                        Name = "800 mm Double Base (Standard)",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { { "Width", 800.0 } }
                    },
                    new FamilyTypeDef
                    {
                        Name = "1000 mm Wide Base",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { { "Width", 1000.0 } }
                    }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    // Toe Kick Recess
                    new ExtrusionDef
                    {
                        Name = "Toe_Kick_Base",
                        ProfileType = "Rectangle",
                        MinX_mm = -390, MaxX_mm = 390,
                        MinY_mm = -250, MaxY_mm = 270,
                        StartZ_mm = 0, EndZ_mm = 100,
                        MaterialParameter = "Cabinet_Material"
                    },
                    // Cabinet Main Carcass Box
                    new ExtrusionDef
                    {
                        Name = "Cabinet_Carcass",
                        ProfileType = "Rectangle",
                        MinX_mm = -400, MaxX_mm = 400,
                        MinY_mm = -290, MaxY_mm = 290,
                        StartZ_mm = 100, EndZ_mm = 830,
                        HeightParameter = "Height",
                        WidthParameter = "Width",
                        DepthParameter = "Depth",
                        MaterialParameter = "Cabinet_Material"
                    },
                    // Countertop Slab
                    new ExtrusionDef
                    {
                        Name = "Solid_Countertop",
                        ProfileType = "Rectangle",
                        MinX_mm = -410, MaxX_mm = 410,
                        MinY_mm = -310, MaxY_mm = 300,
                        StartZ_mm = 830, EndZ_mm = 860,
                        WidthParameter = "Width",
                        MaterialParameter = "Countertop_Material"
                    }
                }
            };
        }

        private static FamilySchema CreateColumnPreset()
        {
            return new FamilySchema
            {
                MasterFamilyName = "Architectural_Tuscan_Column",
                FamilyCategory = "Columns",
                TemplateName = "Metric Generic Model.rft",
                Description = "Classical architectural column with pedestal plinth, cylindrical column shaft, and abacus capital.",
                MasterParameters = new List<MasterParameterDef>
                {
                    new MasterParameterDef { Name = "Height", DataType = "Length", DefaultValueMm = 3000, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Shaft_Diameter", DataType = "Length", DefaultValueMm = 400, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Plinth_Width", DataType = "Length", DefaultValueMm = 550, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Column_Material", DataType = "Material", GroupType = "Materials" }
                },
                FamilyTypes = new List<FamilyTypeDef>
                {
                    new FamilyTypeDef
                    {
                        Name = "Small (Dia 300 x 2400 mm)",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "Height", 2400.0 }, { "Shaft_Diameter", 300.0 }, { "Plinth_Width", 450.0 }
                        }
                    },
                    new FamilyTypeDef
                    {
                        Name = "Medium (Dia 400 x 3000 mm)",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "Height", 3000.0 }, { "Shaft_Diameter", 400.0 }, { "Plinth_Width", 550.0 }
                        }
                    },
                    new FamilyTypeDef
                    {
                        Name = "Colonnade (Dia 500 x 3600 mm)",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "Height", 3600.0 }, { "Shaft_Diameter", 500.0 }, { "Plinth_Width", 650.0 }
                        }
                    }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    // Plinth Base Block
                    new ExtrusionDef
                    {
                        Name = "Base_Plinth",
                        ProfileType = "Rectangle",
                        MinX_mm = -275, MaxX_mm = 275,
                        MinY_mm = -275, MaxY_mm = 275,
                        StartZ_mm = 0, EndZ_mm = 250,
                        MaterialParameter = "Column_Material"
                    },
                    // Column Shaft
                    new ExtrusionDef
                    {
                        Name = "Cylindrical_Shaft",
                        ProfileType = "Cylinder",
                        MinX_mm = -200, MaxX_mm = 200,
                        MinY_mm = -200, MaxY_mm = 200,
                        Radius_mm = 200,
                        StartZ_mm = 250, EndZ_mm = 2800,
                        HeightParameter = "Height",
                        MaterialParameter = "Column_Material"
                    },
                    // Top Capital Abacus
                    new ExtrusionDef
                    {
                        Name = "Top_Capital",
                        ProfileType = "Rectangle",
                        MinX_mm = -260, MaxX_mm = 260,
                        MinY_mm = -260, MaxY_mm = 260,
                        StartZ_mm = 2800, EndZ_mm = 3000,
                        MaterialParameter = "Column_Material"
                    }
                }
            };
        }

        private static FamilySchema CreateConferenceTablePreset()
        {
            return new FamilySchema
            {
                MasterFamilyName = "Executive_Conference_Table",
                FamilyCategory = "Furniture",
                TemplateName = "Metric Generic Model.rft",
                Description = "Executive boardroom table with dual pedestal towers, center wire raceway, and 45mm beveled wood top.",
                Manufacturer = "Forge Executive Workspaces",
                MasterParameters = new List<MasterParameterDef>
                {
                    new MasterParameterDef { Name = "Length", DataType = "Length", DefaultValueMm = 3000, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Width", DataType = "Length", DefaultValueMm = 1200, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Height", DataType = "Length", DefaultValueMm = 750, GroupType = "Dimensions" },
                    new MasterParameterDef { Name = "Table_Material", DataType = "Material", GroupType = "Materials" },
                    new MasterParameterDef { Name = "Pedestal_Material", DataType = "Material", GroupType = "Materials" }
                },
                FamilyTypes = new List<FamilyTypeDef>
                {
                    new FamilyTypeDef
                    {
                        Name = "8 Person (2400 x 1100 mm)",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { { "Length", 2400.0 }, { "Width", 1100.0 } }
                    },
                    new FamilyTypeDef
                    {
                        Name = "12 Person (3000 x 1200 mm)",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { { "Length", 3000.0 }, { "Width", 1200.0 } }
                    },
                    new FamilyTypeDef
                    {
                        Name = "16 Person (3800 x 1350 mm)",
                        ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { { "Length", 3800.0 }, { "Width", 1350.0 } }
                    }
                },
                HostExtrusions = new List<ExtrusionDef>
                {
                    // Conference Tabletop
                    new ExtrusionDef
                    {
                        Name = "Conference_Top_Slab",
                        ProfileType = "Rectangle",
                        MinX_mm = -1500, MaxX_mm = 1500,
                        MinY_mm = -600, MaxY_mm = 600,
                        StartZ_mm = 705, EndZ_mm = 750,
                        MaterialParameter = "Table_Material"
                    },
                    // Left Pedestal
                    new ExtrusionDef
                    {
                        Name = "Left_Pedestal",
                        ProfileType = "Rectangle",
                        MinX_mm = -900, MaxX_mm = -700,
                        MinY_mm = -250, MaxY_mm = 250,
                        StartZ_mm = 0, EndZ_mm = 705,
                        MaterialParameter = "Pedestal_Material"
                    },
                    // Right Pedestal
                    new ExtrusionDef
                    {
                        Name = "Right_Pedestal",
                        ProfileType = "Rectangle",
                        MinX_mm = 700, MaxX_mm = 900,
                        MinY_mm = -250, MaxY_mm = 250,
                        StartZ_mm = 0, EndZ_mm = 705,
                        MaterialParameter = "Pedestal_Material"
                    }
                }
            };
        }

        #endregion

        #region Serialization & Export Methods

        public static void ExportSchemaToJson(FamilySchema schema, string filePath)
        {
            string json = JsonSerializer.Serialize(schema, JsonOptions);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        public static FamilySchema? ImportSchemaFromJson(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            string json = File.ReadAllText(filePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<FamilySchema>(json, JsonOptions);
        }

        /// <summary>
        /// Exports the family schema into a Revit Type Catalog (.txt/.csv format),
        /// formatted specifically so Revit can import it directly when loading the family.
        /// </summary>
        public static void ExportCatalogToCsv(FamilySchema schema, string filePath)
        {
            var sb = new StringBuilder();

            // Header line format in Revit Type Catalog:
            // ,Param1##length##millimeters,Param2##length##millimeters...
            var headerParams = new List<MasterParameterDef>();
            if (schema.MasterParameters != null)
            {
                headerParams.AddRange(schema.MasterParameters);
            }

            var headerParts = new List<string> { "" }; // First column is Type Name
            foreach (var p in headerParams)
            {
                string unitSpec = p.DataType.ToLowerInvariant() switch
                {
                    "length" => "length##millimeters",
                    "angle" => "angle##degrees",
                    "area" => "area##square_meters",
                    "volume" => "volume##cubic_meters",
                    "yesno" or "boolean" => "other##",
                    "integer" or "int" => "other##",
                    "number" or "double" => "number##",
                    _ => "other##"
                };
                headerParts.Add($"{p.Name}##{unitSpec}");
            }

            sb.AppendLine(string.Join(",", headerParts));

            // Types rows
            if (schema.FamilyTypes != null && schema.FamilyTypes.Count > 0)
            {
                foreach (var ft in schema.FamilyTypes)
                {
                    var rowValues = new List<string> { ft.Name };
                    foreach (var p in headerParams)
                    {
                        if (ft.ParameterValues != null && ft.ParameterValues.TryGetValue(p.Name, out var val))
                        {
                            rowValues.Add(FormatValForCatalog(val));
                        }
                        else
                        {
                            rowValues.Add(FormatValForCatalog(p.GetEffectiveNumericValue()));
                        }
                    }
                    sb.AppendLine(string.Join(",", rowValues));
                }
            }
            else
            {
                // Single default type
                var rowValues = new List<string> { schema.MasterFamilyName };
                foreach (var p in headerParams)
                {
                    rowValues.Add(FormatValForCatalog(p.GetEffectiveNumericValue()));
                }
                sb.AppendLine(string.Join(",", rowValues));
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        private static string FormatValForCatalog(object? val)
        {
            if (val == null) return "0";
            if (val is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Number) return je.GetRawText();
                if (je.ValueKind == JsonValueKind.True) return "1";
                if (je.ValueKind == JsonValueKind.False) return "0";
                return je.GetString() ?? "";
            }
            if (val is bool b) return b ? "1" : "0";
            return val.ToString() ?? "0";
        }

        #endregion
    }
}
