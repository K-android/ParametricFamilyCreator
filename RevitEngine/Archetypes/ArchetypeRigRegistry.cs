using System;
using System.Collections.Generic;
using System.Linq;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.RevitEngine.Archetypes
{
    /// <summary>
    /// Central registry and factory for all deterministic C# parametric archetype rigs.
    /// Resolves the optimal rig based on archetype name, category, or cut-sheet specifications.
    /// </summary>
    public static class ArchetypeRigRegistry
    {
        private static readonly List<IArchetypeRig> _rigs = new()
        {
            new SemiPedestalBasinRig(),
            new CountertopBasinRig(),
            new WaterClosetRig(),
            new SingleDoorRig(),
            new CaseworkCabinetRig(),
            new BaseplateJointRig(),
            new UniversalArchetypeRig() // Fallback
        };

        public static IReadOnlyList<IArchetypeRig> RegisteredRigs => _rigs;

        /// <summary>
        /// Selects the best archetype rig for a given cut-sheet spec or text prompt.
        /// </summary>
        public static IArchetypeRig ResolveRig(string? archetypeOrText, string? category = null)
        {
            string query = $"{archetypeOrText} {category}".Trim();

            // Check specialized rigs first (skip UniversalArchetypeRig which matches all)
            foreach (var rig in _rigs)
            {
                if (rig is not UniversalArchetypeRig && rig.CanHandle(query))
                {
                    return rig;
                }
            }

            // Fallback to Universal
            return _rigs.First(r => r is UniversalArchetypeRig);
        }

        /// <summary>
        /// Synthesizes a verified, watertight Revit FamilySchema using the appropriate deterministic rig.
        /// </summary>
        public static FamilySchema BuildFromSpec(ExtractedCutSheetSpec spec, Action<string>? onLog = null)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));

            var rig = ResolveRig(spec.Archetype, spec.RevitCategory);
            onLog?.Invoke($"[ARCHETYPE DISPATCH] Selected Rig: '{rig.ArchetypeName}' for Archetype='{spec.Archetype}', Category='{spec.RevitCategory}'");

            return rig.BuildSchema(spec, onLog);
        }
    }
}
