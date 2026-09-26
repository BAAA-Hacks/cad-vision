using Core.Primitives.DataStructures.MechanicalGraph;
using Core.Primitives.DataStructures.Memory;
using Core.Primitives.DataStructures.Project;
using Core.Primitives.DataStructures.Issues;
using Core.Primitives.Operations.MechanicalGraph;
using Core.Primitives.Operations.Project;
using Core.Primitives.Operations.Issues;
using Core.Primitives.Operations.Issues.Checkers;
using Core.Tools.Query;
using Newtonsoft.Json.Linq;

internal static class MechanicalQueryChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static JObject Document()
    {
        var ids = new[] { "A", "B", "C", "D", "X", "Y" };
        JObject Mate(string id, string a, string b) => new() { ["id"] = id, ["componentIds"] = new JArray(a, b), ["suppressed"] = false, ["status"] = "dangling" };
        var mates = new JArray(Mate("AB2", "A", "B"), Mate("AB1", "A", "B"), Mate("AC", "A", "C"), Mate("BD", "B", "D"), Mate("CD", "C", "D"), Mate("BC", "B", "C"));
        return new JObject { ["schemaVersion"] = "1.0", ["project"] = new JObject { ["id"] = "export", ["name"] = "Mechanical", ["rootObjectId"] = "R" },
            ["objects"] = new JArray(new[] { new JObject { ["id"] = "R", ["name"] = "Root", ["type"] = "assembly", ["parentId"] = null, ["childIds"] = new JArray(ids), ["suppressed"] = false } }
                .Concat(ids.Select(id => new JObject { ["id"] = id, ["name"] = id, ["type"] = "part", ["parentId"] = "R", ["childIds"] = new JArray(), ["suppressed"] = false, ["fixed"] = false }))),
            ["mates"] = mates,
            ["mechanicalScopes"] = new JArray(new JObject { ["scopeAssemblyId"] = "R", ["configuration"] = "Default", ["source"] = "synthetic-contract-test",
                ["membershipCoverage"] = "Complete", ["mateCoverage"] = "Complete", ["occurrenceIds"] = new JArray(ids), ["mateIds"] = new JArray(mates.Select(m => (string)m["id"]!)) }) };
    }
    private static ProjectSnapshot Load(JObject doc)
    {
        var result = LoadProject.Load(doc.ToString()); Require(result.Success, "Non-graph project metadata should load."); return result.Snapshot!;
    }
    private static MechanicalScope Scope(JObject doc) => Load(doc).MechanicalScopes[0];
    public static async Task RunAsync()
    {
        var doc = Document(); var snapshot = Load(doc); var scope = snapshot.MechanicalScopes[0];
        Require(scope.Graph?.SnapshotId == snapshot.SnapshotId, "Scoped graph lost canonical snapshot identity.");
        Require(scope.State == GraphDataState.Available && scope.Graph!.NodesById.Count == 6 && !scope.Graph.NodesById.ContainsKey("R"), "Hierarchy assembly became an isolated vertex.");
        var neighborhood = MechanicalQueries.Neighborhood(scope, new[] { "A" }, 1);
        Require(neighborhood.Depths.Keys.ToHashSet().SetEquals(new[] { "A", "B", "C" }) && neighborhood.Mates.Count == 4 && neighborhood.DepthBoundReached, "Induced neighborhood lost parallel/cross edges or bound.");
        var zero = MechanicalQueries.Neighborhood(scope, new[] { "A", "B", "A" }, 0);
        Require(zero.Depths.Count == 2 && zero.Depths.Values.All(d => d == 0) && zero.Mates.Count == 2, "Multiple depth-zero roots incorrect.");
        var path = MechanicalQueries.Path(scope, "A", "D");
        Require(path.Status == MechanicalPathStatus.Found && path.ObjectIds.SequenceEqual(new[] { "A", "B", "D" }) && path.Mates[0].Id == "AB1" && path.Mates[0].Status == "dangling" && path.ShortestPathComplete, "Canonical shortest/parallel path or mate status lost.");
        var shuffled = Document(); shuffled["objects"] = new JArray(((JArray)shuffled["objects"]!).Reverse()); shuffled["mates"] = new JArray(((JArray)shuffled["mates"]!).Reverse());
        Require(MechanicalQueries.Path(Scope(shuffled), "A", "D").Mates.Select(m => m.Id).SequenceEqual(path.Mates.Select(m => m.Id)), "Path depends on export order.");
        Require(MechanicalQueries.Path(scope, "A", "A", 0).Mates.Count == 0 && MechanicalQueries.Path(scope, "A", "A", 0).Status == MechanicalPathStatus.Found, "Zero-hop identity failed.");
        Require(MechanicalQueries.Path(scope, "A", "D", 1).Status == MechanicalPathStatus.NotEstablished, "Bounded search falsely disproved path.");
        Require(MechanicalQueries.Path(scope, "A", "X", 8).Status == MechanicalPathStatus.ConfirmedDisconnected, "Finite but exhaustive search failed disconnection.");
        Require(MechanicalQueries.Path(scope, "X", "Y", 0).Status == MechanicalPathStatus.ConfirmedDisconnected, "Exhausted singleton incorrectly treated as interrupted search.");
        doc["mechanicalScopes"]![0]!["mateCoverage"] = "Partial";
        var partial = Scope(doc);
        Require(MechanicalQueries.Path(partial, "A", "D").Status == MechanicalPathStatus.Found && !MechanicalQueries.Path(partial, "A", "D").ShortestPathComplete && MechanicalQueries.Path(partial, "X", "Y").Status == MechanicalPathStatus.NotEstablished, "Partial evidence became complete claim.");
        doc["mechanicalScopes"]![0]!["mateCoverage"] = "Complete";
        doc["mechanicalScopes"]![0]!["membershipCoverage"] = "Partial";
        Require(MechanicalQueries.Path(Scope(doc), "X", "Y").Status == MechanicalPathStatus.NotEstablished, "Partial membership proved isolation.");
        doc = Document(); doc["mates"]![0]!["suppressed"] = null;
        var unknownEdge = Scope(doc);
        Require(!MechanicalQueries.Neighborhood(unknownEdge, new[] { "A" }, 1).Mates.Any(m => m.Id == "AB2") && MechanicalQueries.Path(unknownEdge, "X", "Y").Status == MechanicalPathStatus.NotEstablished, "Unknown mate suppression treated as active/complete.");
        doc = Document(); doc["objects"]![2]!["suppressed"] = null;
        var unknownNode = Scope(doc);
        Require(!MechanicalQueries.Neighborhood(unknownNode, new[] { "A" }, 2).Depths.ContainsKey("B") && MechanicalQueries.Path(unknownNode, "B", "B").Status == MechanicalPathStatus.NotEstablished, "Unknown endpoint admitted.");
        doc["objects"]![2]!["suppressed"] = true;
        Require(MechanicalQueries.Path(Scope(doc), "B", "D").Status == MechanicalPathStatus.NotEstablished, "Suppressed endpoint returned disconnected.");
        var islands = MechanicalQueries.Islands(scope);
        Require(islands.Count == 3 && islands[0].SequenceEqual(new[] { "A", "B", "C", "D" }) && islands[1][0] == "X", "Island main/tie/singleton semantics wrong.");
        var association = new ProjectAssociation("caden"); var registry = SemanticQueryTools.Create(snapshot, association);
        JObject Args(string a = "A", string b = "D") => new() { ["projectId"] = association.ProjectId, ["snapshotId"] = snapshot.SnapshotId, ["scopeAssemblyId"] = "R", ["configuration"] = "Default", ["startObjectId"] = a, ["endObjectId"] = b };
        Require(registry.Declarations.Count == 9, "Mechanical capabilities not advertised.");
        var wrongScope = Args(); wrongScope["configuration"] = "NotExported";
        var scopedFailure = await registry.ExecuteAsync("find_mechanical_path", wrongScope);
        Require((string?)scopedFailure["errors"]![0]!["details"]!["reasonCode"] == "MECHANICAL_SCOPE_MISSING" && !(bool)scopedFailure["errors"]![0]!["details"]!["retryable"]!, "Per-request scope failure lacks shared recovery contract.");
        var response = await registry.ExecuteAsync("find_mechanical_path", Args());
        Require((bool)response["success"]! && (string?)response["coverage"]!["countUnit"] == "objects" && (string?)response["coverage"]!["mates"]!["countUnit"] == "mates" && ((JArray)response["data"]!["parallelMates"]![0]!["eligibleMateIds"]!).Count == 2, "Tool envelope/parallel evidence broken.");
        var invalidArgs = Args("R"); Require((string?)(await registry.ExecuteAsync("find_mechanical_path", invalidArgs))["errors"]![0]!["code"] == "OBJECT_OUTSIDE_SCOPE", "Out-of-scope object accepted.");
        invalidArgs = Args("missing"); Require((string?)(await registry.ExecuteAsync("find_mechanical_path", invalidArgs))["errors"]![0]!["code"] == "UNKNOWN_OBJECT_ID", "Unknown object accepted.");
        invalidArgs = Args(); invalidArgs["snapshotId"] = "old"; Require((string?)(await registry.ExecuteAsync("find_mechanical_path", invalidArgs))["errors"]![0]!["code"] == "STALE_SNAPSHOT_REFERENCE", "Stale graph query accepted.");
        invalidArgs = Args(); invalidArgs["maxHops"] = 33; Require((string?)(await registry.ExecuteAsync("find_mechanical_path", invalidArgs))["errors"]![0]!["code"] == "MAX_DEPTH_EXCEEDED", "Hop bound not enforced.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Require((string?)(await registry.ExecuteAsync("find_mechanical_path", Args(), cancel.Token))["errors"]![0]!["code"] == "CANCELLED", "Mechanical cancellation not propagated.");
        foreach (string state in new[] { "Unavailable", "Invalid" })
        {
            doc = Document(); doc["mechanicalScopes"]![0]!["mateCoverage"] = state;
            var unavailable = Load(doc); var tools = SemanticQueryTools.Create(unavailable, association); var args = Args(); args["snapshotId"] = unavailable.SnapshotId;
            Require(!tools.Declarations.Any(t => (string?)t["name"] == "find_mechanical_path") && (string?)(await tools.ExecuteAsync("find_mechanical_path", args))["errors"]![0]!["code"] == "CAPABILITY_UNAVAILABLE", "Unavailable/invalid scope exposed or returned success.");
        }
        doc = Document(); ((JArray)doc["mechanicalScopes"]![0]!["occurrenceIds"]!).RemoveAt(0);
        Require(Load(doc).Capabilities.MechanicalGraph == CapabilityState.Invalid, "Mate endpoint outside membership did not invalidate scope.");
        doc = Document(); ((JArray)doc["mechanicalScopes"]!).Add(doc["mechanicalScopes"]![0]!.DeepClone());
        Require(Load(doc).MechanicalScopes.All(s => s.State == GraphDataState.Invalid), "Duplicate scope key remained usable.");
        doc = Document(); doc.Remove("mechanicalScopes"); Require(Load(doc).MechanicalScopes.Count == 0 && !SemanticQueryTools.Create(Load(doc), association).Declarations.Any(t => (string?)t["name"] == "find_mechanical_path"), "Unscoped export advertised connectivity.");
        doc = Document(); doc["sampleInfo"] = new JObject { ["fixture"] = true }; Require(Scope(doc).State == GraphDataState.Unavailable, "Fixture became mechanical evidence.");
        var unmated = new MechanicalIssueChecker("R", "Default", false); var islandChecker = new MechanicalIssueChecker("R", "Default", true);
        var subject = new IssueSubject(IssueSubjectKind.Object, "X");
        Require((await unmated.EvaluateAsync(snapshot, subject, default)).Findings.Count == 1 && (await islandChecker.EvaluateAsync(snapshot, subject, default)).Findings.Count == 1, "Confirmed isolated part not detected.");
        doc = Document(); doc["objects"]![5]!["fixed"] = null;
        Require((await unmated.EvaluateAsync(Load(doc), subject, default)).Status == SubjectEvaluationStatus.UnableToEvaluate, "Unknown fixed became unmated finding.");
        doc = Document(); doc["mates"]![0]!["suppressed"] = null;
        var sUnknown = Load(doc);
        Require((await unmated.EvaluateAsync(sUnknown, new IssueSubject(IssueSubjectKind.Object, "A"), default)).Status == SubjectEvaluationStatus.UnableToEvaluate && (await unmated.EvaluateAsync(sUnknown, subject, default)).Findings.Count == 1 && (await islandChecker.EvaluateAsync(sUnknown, subject, default)).Status == SubjectEvaluationStatus.UnableToEvaluate, "Subject-local uncertainty/island coverage incorrect.");
        var engine = InitialIssueCheckers.CreateEngine(snapshot); var scan = await engine.ScanAsync(new IssueStore(snapshot)); Require(scan.Success, "Mechanical checker scan did not commit.");
        doc = Document(); doc.Remove("mates"); doc["mechanicalScopes"]![0]!["mateCoverage"] = "Unavailable"; doc["mechanicalScopes"]![0]!["mateIds"] = new JArray();
        Require(Scope(doc).State == GraphDataState.Unavailable, "Missing unavailable mate export misclassified as invalid.");
        doc = Document(); doc["mechanicalScopes"]![0]!["mateCoverage"] = new JObject(); Require(Scope(doc).State == GraphDataState.Invalid, "Malformed coverage failed graceful degradation.");
        doc = Document(); doc["mechanicalScopes"]![0]!["occurrenceIds"] = new JArray(); doc["mechanicalScopes"]![0]!["mateIds"] = new JArray();
        Require(Scope(doc).State == GraphDataState.Available && MechanicalQueries.Islands(Scope(doc)).Count == 0, "Explicit empty scope was rejected.");
        // Independent distance-to-target oracle, followed by lexicographic greedy reconstruction.
        var random = new Random(718);
        for (int trial = 0; trial < 40; trial++)
        {
            doc = Document(); var ids = new[] { "A", "B", "C", "D", "X", "Y" }; var edges = new JArray();
            for (int i = 0; i < 16; i++)
            {
                int a = random.Next(ids.Length), b = random.Next(ids.Length - 1); if (b >= a) b++;
                edges.Add(new JObject { ["id"] = "E" + i, ["componentIds"] = new JArray(ids[a], ids[b]), ["suppressed"] = random.Next(4) == 0 ? JValue.CreateNull() : new JValue(random.Next(5) == 0) });
            }
            doc["mates"] = edges; doc["mechanicalScopes"]![0]!["mateIds"] = new JArray(edges.Select(e => (string)e["id"]!));
            var s = Scope(doc); var g = s.Graph!;
            foreach (string end in ids)
            {
                var distances = new Dictionary<string, int> { [end] = 0 }; var pending = new Queue<string>(); pending.Enqueue(end);
                var eligible = g.MatesById.Values.Where(e => e.Suppressed == false).ToArray();
                while (pending.Count > 0)
                {
                    var current = pending.Dequeue();
                    foreach (var edge in eligible.Where(e => e.ObjectAId == current || e.ObjectBId == current))
                    { string other = edge.GetOtherEndpoint(current); if (distances.TryAdd(other, distances[current] + 1)) pending.Enqueue(other); }
                }
                foreach (string start in ids)
                {
                    var actual = MechanicalQueries.Path(s, start, end);
                    Require((actual.Status == MechanicalPathStatus.Found) == distances.ContainsKey(start), "Path existence differs from independent oracle.");
                    if (!distances.ContainsKey(start)) continue;
                    var expectedMates = new List<string>(); string current = start;
                    while (current != end)
                    {
                        var candidates = eligible.Where(e => e.ObjectAId == current || e.ObjectBId == current)
                            .Where(e => distances.TryGetValue(e.GetOtherEndpoint(current), out int d) && d == distances[current] - 1)
                            .OrderBy(e => e.GetOtherEndpoint(current), StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal);
                        var edge = candidates.First(); expectedMates.Add(edge.Id); current = edge.GetOtherEndpoint(current);
                    }
                    Require(actual.Mates.Select(e => e.Id).SequenceEqual(expectedMates), "Canonical shortest path differs from independent oracle.");
                }
            }
        }
        Console.WriteLine("PASS: scoped mechanical export validation, confirmed-active neighborhoods, deterministic paths/parallel mates, partial evidence, exhaustive/bounded disconnection, coverage, capability gating and connectivity checkers.");
    }
}
