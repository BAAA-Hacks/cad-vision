using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Core.Primitives.DataStructures.MechanicalGraph
{
    public enum MechanicalCoverage { Complete, Partial, Unavailable, Invalid }

    public sealed class MechanicalScope
    {
        public string ScopeAssemblyId { get; }
        public string Configuration { get; }
        public MechanicalCoverage MembershipCoverage { get; }
        public MechanicalCoverage MateCoverage { get; }
        public string Source { get; }
        public string? Reason { get; }
        public GraphDataState State { get; }
        public MechanicalGraph? Graph { get; }
        public IReadOnlyList<GraphDiagnostic> Diagnostics { get; }
        internal MechanicalScope(string assembly, string configuration, MechanicalCoverage membership, MechanicalCoverage mates,
            string source, string? reason, GraphDataState state, MechanicalGraph? graph, IEnumerable<GraphDiagnostic> diagnostics)
        {
            ScopeAssemblyId = assembly; Configuration = configuration; MembershipCoverage = membership; MateCoverage = mates;
            Source = source; Reason = reason; State = state; Graph = graph; Diagnostics = diagnostics.ToList().AsReadOnly();
        }
    }
}
