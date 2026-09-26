using System.Diagnostics;
using Core.Primitives.DataStructures.Memory;
using Core.Primitives.Operations.Project;
using Core.Primitives.Operations.Memory;
using Core.Tools.Issues;
using Core.Tools.Memory;
using Core.Tools.Query;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class PerformanceBenchmarks
{
    private sealed class Storage : IMemoryPersistence
    {
        string? text;
        public string? Read() => text;
        public void Commit(string? expected, string next) { if (text != expected) throw new Exception("Conflict"); text = next; }
    }
    internal static async Task Run(string metadataPath, string outputPath)
    {
        var loadTimer = Stopwatch.StartNew();
        var snapshot = LoadProject.Load(File.ReadAllText(metadataPath)).Snapshot ?? throw new Exception("Load failed");
        loadTimer.Stop();
        var association = new ProjectAssociation("offline-benchmark");
        var issues = await IssueAccess.OpenAsync(snapshot, association, new Storage());
        var memory = MemoryAccess.Open(snapshot, association, new Storage());
        var registry = SemanticQueryTools.Create(snapshot, association, issues: issues, memory: memory);
        string root = (string)snapshot.CopyProjectMetadata()["rootObjectId"]!;
        JObject Args(JObject extra) { var a = (JObject)extra.DeepClone(); a["projectId"] = association.ProjectId; a["snapshotId"] = snapshot.SnapshotId; return a; }
        var cases = new List<(string Name, JObject Args)> {
            ("get_model_summary", new JObject()),
            ("get_object_details", new JObject { ["objectIds"] = new JArray(root), ["fields"] = new JArray("mass", "material") }),
            ("find_objects", new JObject { ["query"] = snapshot.Name == "ScopeRig" ? "Bolt" : "redstart" }),
            ("find_connections", new JObject { ["query"] = snapshot.Name == "ScopeRig" ? "Drivetrain" : "redstart", ["otherQuery"] = "drone base" }),
            ("get_issue_summary", new JObject()),
            ("set_scope", new JObject { ["objectId"] = root }),
            ("get_mates", new JObject { ["objectIds"] = new JArray(snapshot.MatesById.Values.FirstOrDefault()?.ObjectAId ?? root) })
        };
        var scope = snapshot.MechanicalScopes.FirstOrDefault(s => s.Graph != null && s.Graph.NodesById.Count >= 2);
        if (scope != null)
        {
            var ids = scope.Graph!.NodesById.Keys.Take(2).ToArray();
            cases.Insert(0, ("find_mechanical_path", new JObject { ["scopeAssemblyId"] = scope.ScopeAssemblyId, ["configuration"] = scope.Configuration,
                ["startObjectId"] = ids[0], ["endObjectId"] = ids[1] }));
        }
        var measurements = new JArray();
        foreach (var c in cases)
        {
            await registry.ExecuteAsync("clear_scope", Args(new JObject()));
            for (int i = 0; i < 10; i++) await registry.ExecuteAsync(c.Name, c.Name == "get_model_summary" ? new JObject() : Args(c.Args));
            var elapsed = new List<double>(); long bytes = 0; long allocatedBefore = GC.GetTotalAllocatedBytes(true);
            for (int i = 0; i < 100; i++)
            {
                var timer = Stopwatch.StartNew();
                var result = await registry.ExecuteAsync(c.Name, c.Name == "get_model_summary" ? new JObject() : Args(c.Args));
                bytes = System.Text.Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)); timer.Stop();
                if ((bool?)result["success"] != true) throw new Exception("Benchmark failure: " + result);
                elapsed.Add(timer.Elapsed.TotalMilliseconds);
            }
            long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            elapsed.Sort();
            measurements.Add(new JObject { ["tool"] = c.Name, ["meanMs"] = elapsed.Average(), ["p95Ms"] = elapsed[94],
                ["allocatedBytesPerCall"] = allocated / 100, ["responseBytes"] = bytes });
        }
        var report = new JObject { ["metadata"] = Path.GetFileName(metadataPath), ["objects"] = snapshot.ComponentsById.Count,
            ["loadMs"] = loadTimer.Elapsed.TotalMilliseconds, ["iterations"] = 100, ["warmup"] = 10,
            ["scope"] = "warm local dispatch plus response serialization; no Gemini; process allocation estimate; Debug build",
            ["measurements"] = measurements };
        File.WriteAllText(outputPath, report.ToString(Formatting.Indented));
        Console.WriteLine(report.ToString(Formatting.Indented));
    }
}
