using System;
using System.Collections.Generic;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.RevitEngine.Archetypes
{
    /// <summary>
    /// Verification rule checking modeled Revit BRep geometry against cut-sheet spec.
    /// </summary>
    public class ArchetypeVerificationRule
    {
        public string FeatureName { get; set; } = "Overall Dimension";
        public double TargetDimensionMm { get; set; }
        public double ToleranceMm { get; set; } = 2.0;
        public string AssociatedParameterName { get; set; } = "Width";
        public string DatumAnchor { get; set; } = DimensionDatums.Floor;

        public ArchetypeVerificationRule() { }

        public ArchetypeVerificationRule(string featureName, double targetMm, string paramName, double toleranceMm = 2.0, string datum = DimensionDatums.Floor)
        {
            FeatureName = featureName;
            TargetDimensionMm = targetMm;
            AssociatedParameterName = paramName;
            ToleranceMm = toleranceMm;
            DatumAnchor = datum;
        }
    }

    /// <summary>
    /// Contract for deterministic C# archetype rigs.
    /// Converts strictly constrained extracted specifications into watertight Revit FamilySchema geometry.
    /// </summary>
    public interface IArchetypeRig
    {
        /// <summary>
        /// Unique archetype rig identifier (e.g. "SemiPedestalBasin", "CountertopBasin", "WaterCloset", "SingleDoor").
        /// </summary>
        string ArchetypeName { get; }

        /// <summary>
        /// Evaluates whether this rig handles the given archetype identifier or category text.
        /// </summary>
        bool CanHandle(string archetypeOrCategory);

        /// <summary>
        /// Synthesizes complete, mathematically watertight, geometrically verified Revit FamilySchema.
        /// </summary>
        FamilySchema BuildSchema(ExtractedCutSheetSpec spec, Action<string>? onLog = null);

        /// <summary>
        /// Returns verification rules to audit Revit API BRep solids against the extracted cut-sheet dimensions.
        /// </summary>
        List<ArchetypeVerificationRule> GetVerificationRules(ExtractedCutSheetSpec spec);
    }
}
