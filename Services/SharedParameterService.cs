using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.Services
{
    /// <summary>
    /// Represents a single parameter entry in an Autodesk Revit Shared Parameter definition file.
    /// Format: PARAM	GUID	NAME	DATATYPE	DATACAT	GROUP	VISIBLE	DESCRIPTION	USERMODIFIABLE	HIDEWHENNOVALUE
    /// </summary>
    public class SharedParameterRecord
    {
        public Guid Guid { get; set; }
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = "LENGTH";
        public string DataCat { get; set; } = string.Empty;
        public int GroupId { get; set; } = 1;
        public int Visible { get; set; } = 1;
        public string Description { get; set; } = string.Empty;
        public int UserModifiable { get; set; } = 1;
        public int HideWhenNoValue { get; set; } = 0;

        public override string ToString()
        {
            // Tab-delimited record compliant with Autodesk Revit shared parameter format
            return $"PARAM\t{Guid:D}\t{Name}\t{DataType}\t{DataCat}\t{GroupId}\t{Visible}\t{Description}\t{UserModifiable}\t{HideWhenNoValue}";
        }
    }

    /// <summary>
    /// Service for generating, mapping, and exporting Autodesk Revit Shared Parameter definition files (.txt).
    /// Complies strictly with the Autodesk Revit Shared Parameter File Specification.
    /// </summary>
    public static class SharedParameterService
    {
        public const string DefaultGroupName = "ParametricFamilyForge Parameters";
        public const int DefaultGroupId = 1;

        /// <summary>
        /// Generates the complete Autodesk Revit Shared Parameter file content from a FamilySchema.
        /// </summary>
        public static string GenerateSharedParameterFileContent(FamilySchema schema, string groupName = DefaultGroupName)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));

            var records = ExtractSharedParameters(schema);
            var sb = new StringBuilder();

            // Header comments
            sb.AppendLine("# This is a Revit shared parameter file.");
            sb.AppendLine("# Do not edit manually.");
            sb.AppendLine("*META\tVERSION\tMINVERSION");
            sb.AppendLine("META\t2\t1");

            // Parameter group definition
            sb.AppendLine("*GROUP\tID\tNAME");
            sb.AppendLine($"GROUP\t{DefaultGroupId}\t{groupName}");

            // Parameters table header
            sb.AppendLine("*PARAM\tGUID\tNAME\tDATATYPE\tDATACAT\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE");

            // Individual parameter rows
            foreach (var rec in records)
            {
                sb.AppendLine(rec.ToString());
            }

            return sb.ToString();
        }

        /// <summary>
        /// Exports a Revit Shared Parameter file (.txt) directly to disk for a given FamilySchema.
        /// </summary>
        public static void ExportSharedParameterFile(FamilySchema schema, string filePath, string groupName = DefaultGroupName)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be empty.", nameof(filePath));

            string directory = Path.GetDirectoryName(filePath) ?? "";
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string content = GenerateSharedParameterFileContent(schema, groupName);
            File.WriteAllText(filePath, content, Encoding.UTF8);
        }

        /// <summary>
        /// Extracts all unique parameters defined in the FamilySchema and maps them to SharedParameterRecord instances.
        /// Deterministic GUIDs are computed from parameter names to ensure consistent GUID reuse across exports.
        /// </summary>
        public static List<SharedParameterRecord> ExtractSharedParameters(FamilySchema schema)
        {
            var result = new List<SharedParameterRecord>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Process Master Parameters
            if (schema.MasterParameters != null)
            {
                foreach (var mp in schema.MasterParameters)
                {
                    if (string.IsNullOrWhiteSpace(mp.Name)) continue;
                    if (seenNames.Contains(mp.Name)) continue;

                    seenNames.Add(mp.Name);
                    string revitDataType = MapToRevitSharedParamType(mp.DataType, mp.Name);
                    Guid guid = GenerateDeterministicGuid(mp.Name);

                    string desc = !string.IsNullOrWhiteSpace(mp.Formula) 
                        ? $"Calculated formula: {mp.Formula}" 
                        : $"ParametricFamilyForge parameter for {schema.MasterFamilyName}";

                    result.Add(new SharedParameterRecord
                    {
                        Guid = guid,
                        Name = mp.Name,
                        DataType = revitDataType,
                        DataCat = string.Empty,
                        GroupId = DefaultGroupId,
                        Visible = 1,
                        Description = desc,
                        UserModifiable = 1,
                        HideWhenNoValue = 0
                    });
                }
            }

            // 2. Process Reference Plane driven parameters if not already in master parameters
            if (schema.ReferencePlanes != null)
            {
                foreach (var rp in schema.ReferencePlanes)
                {
                    if (!string.IsNullOrWhiteSpace(rp.DrivenByParameter) && !seenNames.Contains(rp.DrivenByParameter!))
                    {
                        seenNames.Add(rp.DrivenByParameter!);
                        result.Add(new SharedParameterRecord
                        {
                            Guid = GenerateDeterministicGuid(rp.DrivenByParameter!),
                            Name = rp.DrivenByParameter!,
                            DataType = "LENGTH",
                            DataCat = string.Empty,
                            GroupId = DefaultGroupId,
                            Visible = 1,
                            Description = $"Offset constraint for reference plane '{rp.Name}'",
                            UserModifiable = 1,
                            HideWhenNoValue = 0
                        });
                    }
                }
            }

            // 3. Process Extrusions parameter links (Height, Width, Depth, Material, Visibility)
            if (schema.HostExtrusions != null)
            {
                foreach (var ext in schema.HostExtrusions)
                {
                    CheckAndAddParam(ext.HeightParameter, "LENGTH", $"Height constraint for {ext.Name}");
                    CheckAndAddParam(ext.WidthParameter, "LENGTH", $"Width constraint for {ext.Name}");
                    CheckAndAddParam(ext.DepthParameter, "LENGTH", $"Depth constraint for {ext.Name}");
                    CheckAndAddParam(ext.MaterialParameter, "MATERIAL", $"Material parameter for {ext.Name}");
                    CheckAndAddParam(ext.VisibilityParameter, "YESNO", $"Visibility switch for {ext.Name}");
                }
            }

            // 4. Process Nested Families parameters and bindings
            if (schema.NestedFamilies != null)
            {
                foreach (var nf in schema.NestedFamilies)
                {
                    CheckAndAddParam(nf.VisibilityParameter, "YESNO", $"Visibility switch for nested {nf.NestedFamilyName}");

                    if (nf.ParameterBindings != null)
                    {
                        foreach (var kvp in nf.ParameterBindings)
                        {
                            CheckAndAddParam(kvp.Value, "LENGTH", $"Binding parameter for nested {nf.NestedFamilyName}.{kvp.Key}");
                        }
                    }

                    if (nf.SubParameters != null)
                    {
                        foreach (var sp in nf.SubParameters)
                        {
                            if (string.IsNullOrWhiteSpace(sp.Name)) continue;
                            if (seenNames.Contains(sp.Name)) continue;

                            seenNames.Add(sp.Name);
                            string revitDataType = MapToRevitSharedParamType(sp.DataType, sp.Name);
                            result.Add(new SharedParameterRecord
                            {
                                Guid = GenerateDeterministicGuid(sp.Name),
                                Name = sp.Name,
                                DataType = revitDataType,
                                DataCat = string.Empty,
                                GroupId = DefaultGroupId,
                                Visible = 1,
                                Description = $"Nested sub-parameter for {nf.NestedFamilyName}",
                                UserModifiable = 1,
                                HideWhenNoValue = 0
                            });
                        }
                    }
                }
            }

            void CheckAndAddParam(string? paramName, string fallbackType, string description)
            {
                if (string.IsNullOrWhiteSpace(paramName)) return;
                if (seenNames.Contains(paramName!)) return;

                seenNames.Add(paramName!);
                string dataType = MapToRevitSharedParamType(fallbackType, paramName);
                result.Add(new SharedParameterRecord
                {
                    Guid = GenerateDeterministicGuid(paramName!),
                    Name = paramName!,
                    DataType = dataType,
                    DataCat = string.Empty,
                    GroupId = DefaultGroupId,
                    Visible = 1,
                    Description = description,
                    UserModifiable = 1,
                    HideWhenNoValue = 0
                });
            }

            return result;
        }

        /// <summary>
        /// Maps a schema data type string to standard Autodesk Revit Shared Parameter types:
        /// TEXT, LENGTH, ANGLE, AREA, VOLUME, NUMBER, INTEGER, YESNO, MATERIAL, URL.
        /// </summary>
        public static string MapToRevitSharedParamType(string? dataType, string? paramName = null)
        {
            if (!string.IsNullOrWhiteSpace(dataType))
            {
                string dt = dataType!.Trim().ToLowerInvariant();

                if (dt.Contains("len") || dt.Contains("dim") || dt == "distance") return "LENGTH";
                if (dt.Contains("yes") || dt.Contains("bool")) return "YESNO";
                if (dt.Contains("mat")) return "MATERIAL";
                if (dt.Contains("ang")) return "ANGLE";
                if (dt.Contains("area")) return "AREA";
                if (dt.Contains("vol")) return "VOLUME";
                if (dt.Contains("int")) return "INTEGER";
                if (dt.Contains("num") || dt.Contains("double") || dt.Contains("float")) return "NUMBER";
                if (dt.Contains("url")) return "URL";
                if (dt.Contains("text") || dt.Contains("str")) return "TEXT";
            }

            // Heuristic fallback based on parameter name
            if (!string.IsNullOrWhiteSpace(paramName))
            {
                string pn = paramName!.Trim().ToLowerInvariant();
                if (pn.Contains("width") || pn.Contains("depth") || pn.Contains("height") || 
                    pn.Contains("thick") || pn.Contains("radius") || pn.Contains("offset") || 
                    pn.Contains("len") || pn.Contains("dim") || pn.Contains("clearance") ||
                    pn.Contains("plinth") || pn.Contains("inset") || pn.Contains("gap"))
                {
                    return "LENGTH";
                }
                if (pn.StartsWith("is_") || pn.StartsWith("show_") || pn.StartsWith("has_") || pn.Contains("visible"))
                {
                    return "YESNO";
                }
                if (pn.Contains("mat") || pn.Contains("finish"))
                {
                    return "MATERIAL";
                }
                if (pn.Contains("angle") || pn.Contains("slope"))
                {
                    return "ANGLE";
                }
                if (pn.Contains("count") || pn.Contains("qty") || pn.Contains("number_of"))
                {
                    return "INTEGER";
                }
            }

            return "TEXT";
        }

        /// <summary>
        /// Computes a stable, deterministic GUID for any parameter name using MD5 hashing.
        /// Ensures that repeated exports or shared parameters shared across models have consistent GUIDs.
        /// </summary>
        public static Guid GenerateDeterministicGuid(string paramName, string namespacePrefix = "ParametricFamilyForge")
        {
            if (string.IsNullOrWhiteSpace(paramName))
            {
                return Guid.NewGuid();
            }

            string combined = $"{namespacePrefix}:{paramName.Trim().ToLowerInvariant()}";
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(combined));
            return new Guid(hash);
        }
    }
}
