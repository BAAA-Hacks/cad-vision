using Core.Primitives.DataStructures.MechanicalGraph;
using Core.Primitives.Operations.MechanicalGraph;
using Newtonsoft.Json.Linq;

internal static class MechanicalGraphChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static JObject Node(string id, JToken? mass = null, bool? suppressed = false, string type = "part") => new()
    {
        ["id"] = id, ["name"] = id, ["type"] = type, ["mass"] = mass, ["volume"] = 1000,
        ["suppressed"] = suppressed, ["fixed"] = null, ["definitionStatus"] = "unknown",
        ["material"] = new JObject { ["assigned"] = true, ["name"] = "Steel" }, ["issueIds"] = new JArray("shared", "node-" + id)
    };
    private static JObject Mate(string id, string a, string b, string type = "concentric", bool? suppressed = false) => new()
    {
        ["id"] = id, ["componentIds"] = new JArray(a, b), ["type"] = type, ["status"] = "solved", ["suppressed"] = suppressed,
        ["references"] = new JArray(new JObject { ["componentId"] = a, ["entityId"] = "face-1", ["entityType"] = "face" },
            new JObject { ["componentId"] = a, ["entityId"] = "face-2", ["entityType"] = "face" },
            new JObject { ["componentId"] = b, ["entityId"] = "face-3", ["entityType"] = "face" }),
        ["axis"] = new JArray(0, 0, 1), ["limits"] = new JObject { ["enabled"] = true, ["minimum"] = 0, ["maximum"] = 90 },
        ["issueIds"] = new JArray("shared", "mate-" + id)
    };
    private static JObject Document() => new()
    {
        ["schemaVersion"] = "1.0", ["project"] = new JObject { ["units"] = new JObject { ["mass"] = "kg", ["length"] = "mm" } },
        ["objects"] = new JArray(Node("A", 1), Node("B", 2), Node("C"), Node("D", "bad"), Node("S", 100, true), Node("U", 3, null), Node("ROOT", 1000, false, "assembly")),
        ["mates"] = new JArray(Mate("AB1", "A", "B"), Mate("AB2", "A", "B", "coincident"), Mate("AC", "A", "C"),
            Mate("BC", "B", "C"), Mate("CD", "C", "D"), Mate("AS", "A", "S"), Mate("SU", "S", "U"),
            Mate("BU", "B", "U", "concentric", true), Mate("AR", "A", "ROOT"))
    };
    private static MechanicalGraph Build(JObject doc)
    {
        var result = BuildMechanicalGraph.Build(doc.ToString(), GraphDataState.Available);
        Require(result.State == GraphDataState.Available && result.Graph != null, "Expected valid graph: " + string.Join(",", result.Errors.Select(e => e.Code)));
        return result.Graph!;
    }
    private static GraphReturnSpec All() => new()
    {
        IncludeNodes = true, IncludeEdges = true, IncludeDepths = true, IncludeIssueIds = true, CountComponents = true, CountMates = true,
        SumMass = true, SumVolume = true, GroupMaterials = true, GroupMateTypes = true, GroupConstraintStates = true
    };
    private static GraphQuery Query(int? depth = null, params string[] starts) => new() { StartIds = starts.Length > 0 ? starts.ToList() : new() { "A" }, MaxDepth = depth, Return = All() };
    private static GraphQueryResult Traverse(MechanicalGraph graph, GraphQuery query)
    { var result = TraverseMechanicalGraph.Traverse(graph, query); Require(result.Success, "Unexpected query errors."); return result; }
    private static void Invalid(Action<JObject> edit, string code)
    {
        var doc = Document(); edit(doc); var result = BuildMechanicalGraph.Build(doc.ToString(), GraphDataState.Available);
        Require(result.State == GraphDataState.Invalid && result.Graph == null && result.Errors.Any(e => e.Code == code), "Atomic error missing: " + code);
    }
    public static void Run()
    {
        var graph = Build(Document());
        Require(graph.NodesById.Count == 7 && graph.MatesById.Count == 9, "Index counts incorrect.");
        var edge = graph.MatesById["AB1"];
        Require(ReferenceEquals(edge, graph.NodesById["A"].Edges.Single(e => e.Id == "AB1")) && ReferenceEquals(edge, graph.NodesById["B"].Edges.Single(e => e.Id == "AB1")), "Adjacency did not share edge identity.");
        Require(edge.References.Count == 3 && edge.Axis?.CoordinateFrame == null && edge.Axis?.Unit == null && edge.Limits?.Unit == null, "References or unknown spatial semantics lost.");
        Require(edge.GetOtherEndpoint("A") == "B" && edge.GetOtherEndpoint("B") == "A", "Endpoint helper failed.");
        try { edge.GetOtherEndpoint("C"); throw new Exception("Unrelated endpoint accepted."); } catch (ArgumentException) { }
        Require(graph.NodesById["A"].Fixed == null && graph.NodesById["A"].ConstraintStatus == null, "Unknown state became a fact.");
        var bounded = Traverse(graph, Query(1));
        Require(bounded.Nodes!.Select(n => n.Id).ToHashSet().SetEquals(new[] { "A", "B", "C", "ROOT" }), "Bounded node set incorrect.");
        Require(bounded.Edges!.Select(e => e.Id).ToHashSet().SetEquals(new[] { "AB1", "AB2", "AC", "BC", "AR" }), "Boundary cross-edge/parallel edges lost.");
        Require(bounded.DepthByNodeId!["A"] == 0 && bounded.DepthByNodeId["C"] == 1, "Depths incorrect.");
        Require(bounded.Aggregates.ComponentCount == 4 && bounded.Aggregates.MateCount == 5, "Double counted graph entities.");
        Require(bounded.IssueIds!.Count == 10 && bounded.IssueIds.Count(s => s == "shared") == 1, "Issues not deduplicated across nodes/mates.");
        Require(bounded.Aggregates.Mass!.TotalKnown == 3 && bounded.Aggregates.Mass.KnownCount == 2 && bounded.Aggregates.Mass.MissingCount == 1 && !bounded.Aggregates.Mass.Complete && bounded.Aggregates.Mass.ExcludedAssemblyCount == 1, "Partial mass or assembly double count.");
        Require(Math.Abs(bounded.Aggregates.Volume!.TotalKnown!.Value - 0.000003) < 1e-15 && bounded.Aggregates.Volume.Complete, "Volume conversion incorrect.");
        Require(bounded.Aggregates.MateTypeCounts!.Counts["concentric"] == 4 && bounded.Aggregates.ConstraintStatusCounts!.UnknownCount == 4, "Grouping incorrect.");
        var complete = Traverse(graph, Query());
        Require(complete.Aggregates.Mass!.InvalidCount == 1 && complete.Aggregates.ComponentCount == 5, "Suppressed bridge or invalid mass handling failed.");
        var multi = Traverse(graph, Query(0, "A", "B", "A"));
        Require(multi.Aggregates.ComponentCount == 2 && multi.Aggregates.MateCount == 2 && multi.DepthByNodeId!.Values.All(d => d == 0), "Simultaneous depth-zero roots failed.");
        var nearest = Traverse(graph, Query(2, "A", "D")); Require(nearest.DepthByNodeId!["C"] == 1, "Nearest-start distance failed.");
        var suppressedStart = Traverse(graph, Query(null, "S")); Require(suppressedStart.Aggregates.ComponentCount == 0 && suppressedStart.ExcludedStartIds.SequenceEqual(new[] { "S" }), "Suppressed start was silently traversed.");
        var allQuery = Query(); allQuery.IncludeSuppressedComponents = true; allQuery.EdgeFilter.IncludeSuppressed = true;
        var all = Traverse(graph, allQuery); Require(all.Aggregates.ComponentCount == 6 && all.Aggregates.MateCount == 7 && all.UnknownSuppressionNodeCount == 1 && !all.SuppressionInformationComplete, "Suppression opt-in must still exclude unknown state.");
        var types = Query(); types.EdgeFilter.AllowedMateTypes = new() { "COINCIDENT" };
        var filtered = Traverse(graph, types); Require(filtered.Aggregates.ComponentCount == 2 && filtered.Aggregates.MateCount == 1, "Type filter failed.");
        types.EdgeFilter.AllowedMateStatuses = new(); Require(Traverse(graph, types).Aggregates.MateCount == 0, "Empty filter should allow no mates.");
        var countsOnly = Traverse(graph, new GraphQuery { StartIds = new() { "A" }, Return = new() { CountComponents = true } });
        Require(countsOnly.Nodes == null && countsOnly.Edges == null && countsOnly.DepthByNodeId == null && countsOnly.IssueIds == null && countsOnly.Aggregates.Mass == null && countsOnly.Aggregates.MateCount == null, "Unrequested return data collected/exposed.");
        Require(!TraverseMechanicalGraph.Traverse(graph, Query(-1)).Success && !TraverseMechanicalGraph.Traverse(graph, Query(1, "absent")).Success, "Invalid queries accepted.");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); try { TraverseMechanicalGraph.Traverse(graph, Query(), cancel.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        }
        Console.WriteLine("PASS: immutable graph indexes, parallel/shared edges, induced BFS, multiple starts, suppression, grouping, issues, optional collection, cancellation.");

        Invalid(d => ((JArray)d["objects"]!).Add(d["objects"]![0]!.DeepClone()), "DUPLICATE_COMPONENT_ID");
        Invalid(d => ((JArray)d["mates"]!).Add(d["mates"]![0]!.DeepClone()), "DUPLICATE_MATE_ID");
        Invalid(d => d["mates"]![0]!["componentIds"] = new JArray("A", "absent"), "MISSING_COMPONENT");
        foreach (var ends in new[] { new JArray(), new JArray("A"), new JArray("A", "A"), new JArray("A", "B", "C"), new JArray("A", 2) })
            Invalid(d => d["mates"]![0]!["componentIds"] = ends, "INVALID_ENDPOINTS");
        Invalid(d => d["mates"]![0]!["references"]![0]!["componentId"] = "C", "INVALID_REFERENCE_COMPONENT");
        Invalid(d => d["mates"]![0]!["status"] = "suppressed", "INCONSISTENT_SUPPRESSION");
        Invalid(d => d["mates"] = "not an array", "INVALID_MATES");
        Invalid(d => d["sampleInfo"] = new JObject { ["fixture"] = "true" }, "INVALID_FIXTURE_STATE");
        Require(BuildMechanicalGraph.Build("{\"x\":1,\"x\":2}").State == GraphDataState.Invalid, "Duplicate JSON keys accepted.");
        var empty = Document(); empty["mates"] = new JArray();
        Require(BuildMechanicalGraph.Build(empty.ToString()).State == GraphDataState.Unavailable, "Empty list falsely proves mate coverage.");
        var zero = BuildMechanicalGraph.Build(empty.ToString(), GraphDataState.Available);
        Require(zero.State == GraphDataState.Available && zero.Graph!.MatesById.Count == 0, "Confirmed zero-mate graph rejected.");
        empty.Remove("mates"); Require(BuildMechanicalGraph.Build(empty.ToString()).State == GraphDataState.Unavailable, "Missing mates should be unavailable.");
        Require(BuildMechanicalGraph.Build(empty.ToString(), GraphDataState.Available).State == GraphDataState.Invalid, "Missing array contradicts available state.");
        empty["sampleInfo"] = new JObject { ["fixture"] = true }; empty["mates"] = new JArray();
        Require(BuildMechanicalGraph.Build(empty.ToString(), GraphDataState.Available).State == GraphDataState.Unavailable, "Fixture became valid mate evidence.");
        Console.WriteLine("PASS: atomic duplicate/endpoint/reference validation and Available/Unavailable/Invalid export states.");

        var props = Document(); props["project"]!["units"]!["mass"] = "g";
        props["objects"]![0]!["mass"] = 1000; props["objects"]![1]!["mass"] = 0;
        props["mates"]![0]!["suppressed"] = null;
        var quantities = Traverse(Build(props), Query(0, "A", "B"));
        Require(quantities.Aggregates.Mass!.TotalKnown == 1 && quantities.Aggregates.Mass.Complete && quantities.Aggregates.Mass.KnownCount == 2 && quantities.UnknownSuppressionMateCount == 1, "Unit conversion, zero, or unknown mate state lost.");
        var noneKnown = Traverse(graph, Query(0, "C", "D")); Require(noneKnown.Aggregates.Mass!.TotalKnown == null && !noneKnown.Aggregates.Mass.Complete, "No known masses reported as zero.");
        props["project"]!["units"]!["mass"] = "kg"; props["objects"]![0]!["mass"] = double.MaxValue; props["objects"]![1]!["mass"] = double.MaxValue;
        var overflow = Traverse(Build(props), Query(0, "A", "B")); Require(overflow.Aggregates.Mass!.Overflowed && overflow.Aggregates.Mass.TotalKnown == null && !overflow.Aggregates.Mass.Complete, "Overflow became complete infinity.");
        props["project"]!["units"]!["mass"] = "unknown-unit"; Require(Build(props).NodesById["A"].Mass.State == ValueState.Invalid, "Unsupported unit accepted.");
        Console.WriteLine("PASS: quantity units, missing/invalid counts, zero, overflow, and suppression completeness.");

        // Independent oracle: BFS to determine vertices, then scan all edges for the induced result.
        var random = new Random(417);
        for (int trial = 0; trial < 80; trial++)
        {
            var d = new JObject { ["schemaVersion"] = "1.0", ["project"] = new JObject(), ["objects"] = new JArray(), ["mates"] = new JArray() };
            for (int i = 0; i < 12; i++) ((JArray)d["objects"]!).Add(Node(i.ToString(), null, random.Next(6) == 0));
            for (int i = 0; i < 35; i++) { int a = random.Next(12), b = random.Next(11); if (b >= a) b++; ((JArray)d["mates"]!).Add(Mate("M" + i, a.ToString(), b.ToString(), i % 2 == 0 ? "x" : "y", random.Next(6) == 0)); }
            var g = Build(d); var q = Query(trial % 5, "0", "5"); q.IncludeSuppressedComponents = trial % 3 == 0; q.EdgeFilter.IncludeSuppressed = trial % 4 == 0;
            if (trial % 2 == 0) q.EdgeFilter.AllowedMateTypes = new() { "x" };
            bool Eligible(MateEdge e) => (q.EdgeFilter.IncludeSuppressed || e.Suppressed != true) && (q.EdgeFilter.AllowedMateTypes == null || q.EdgeFilter.AllowedMateTypes.Contains(e.Type!)) &&
                (q.IncludeSuppressedComponents || g.NodesById[e.ObjectAId].Suppressed != true && g.NodesById[e.ObjectBId].Suppressed != true);
            var distance = new Dictionary<string, int>(); var pending = new Queue<string>();
            foreach (string start in q.StartIds) if (q.IncludeSuppressedComponents || g.NodesById[start].Suppressed != true) { distance[start] = 0; pending.Enqueue(start); }
            while (pending.Count > 0)
            {
                string id = pending.Dequeue(); if (distance[id] >= q.MaxDepth) continue;
                foreach (var e in g.MatesById.Values.Where(Eligible).Where(e => e.ObjectAId == id || e.ObjectBId == id))
                { string other = e.ObjectAId == id ? e.ObjectBId : e.ObjectAId; if (!distance.ContainsKey(other)) { distance[other] = distance[id] + 1; pending.Enqueue(other); } }
            }
            var expectedEdges = g.MatesById.Values.Where(Eligible).Where(e => distance.ContainsKey(e.ObjectAId) && distance.ContainsKey(e.ObjectBId)).Select(e => e.Id).ToHashSet();
            var actual = Traverse(g, q);
            Require(actual.Edges!.Select(e => e.Id).ToHashSet().SetEquals(expectedEdges) && actual.DepthByNodeId!.Count == distance.Count && distance.All(p => actual.DepthByNodeId[p.Key] == p.Value), "BFS/oracle mismatch trial " + trial);
        }
        Console.WriteLine("PASS: 80 seeded multigraphs matched an independent induced-subgraph/minimum-depth oracle.");
        string samplePath = Path.Combine(Desktop.Configuration.LocalConfiguration.FindDirectory(), "data", "metadata.json");
        if (File.Exists(samplePath))
        {
            var sampleJson = File.ReadAllText(samplePath);
            if ((bool?)JObject.Parse(sampleJson)["sampleInfo"]?["fixture"] == true)
                Require(BuildMechanicalGraph.Build(sampleJson, GraphDataState.Available).State == GraphDataState.Unavailable, "Local synthetic sample became a complete mechanical graph.");
        }
    }
}
