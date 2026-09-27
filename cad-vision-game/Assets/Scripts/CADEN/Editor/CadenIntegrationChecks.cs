using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CADVision;
using CADEN.Unity;
using Core.Primitives.DataStructures.Memory;
using Core.Primitives.Operations.Project;
using Core.Tools;
using Core.Tools.Query;
using Core.Tools.View;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// Offline native Unity smoke checks: no Gemini, ElevenLabs, exporter, or real project sidecars.
public static class CadenIntegrationChecks
{
    [MenuItem("CADEN/Run offline integration checks")]
    public static async void Run()
    {
        try
        {
            await CheckAsync();
            Debug.Log("CADEN_UNITY_CHECKS_PASSED");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Code(JObject result) => (string)result["errors"]?[0]?["code"];
    private static async Task CheckAsync()
    {
        var runtimeObject = new GameObject("CADEN test runtime");
        var manager = new GameObject("CADEN test service");
        var camera = new GameObject("CADEN test camera"); camera.tag = "MainCamera"; camera.AddComponent<Camera>();
        var root = new GameObject("CADEN test model root");
        var nodes = new Dictionary<int, GameObject>();
        try
        {
            string json = @"{
              'schemaVersion':'2.1','project':{'id':'test-source','name':'Test','rootObjectId':'R'},
              'objects':[
                {'id':'R','name':'Root','type':'assembly','parentId':null,'childIds':['A','O'],'glbNodeIndex':0},
                {'id':'A','name':'Assembly','type':'assembly','parentId':'R','childIds':['P','Q'],'glbNodeIndex':1},
                {'id':'P','name':'Part','type':'part','parentId':'A','childIds':[],'glbNodeIndex':2},
                {'id':'Q','name':'Part','type':'part','parentId':'A','childIds':[],'glbNodeIndex':3},
                {'id':'O','name':'Other','type':'part','parentId':'R','childIds':[],'glbNodeIndex':4}]}";
            nodes[0] = new GameObject("root assembly"); nodes[0].transform.SetParent(root.transform, false);
            nodes[1] = new GameObject("subassembly"); nodes[1].transform.SetParent(nodes[0].transform, false);
            for (int i = 2; i <= 4; i++)
            {
                nodes[i] = GameObject.CreatePrimitive(PrimitiveType.Cube);
                nodes[i].transform.SetParent(nodes[i == 4 ? 0 : 1].transform, false);
                nodes[i].transform.localPosition = new Vector3((i - 2) * 0.2f, 0, 0);
                nodes[i].transform.localScale = Vector3.one * 0.1f;
            }
            var runtime = runtimeObject.AddComponent<CADVisionRuntime>();
            runtime.PublishImportedModel(new CadMetadata(json), root, nodes);
            var service = manager.AddComponent<CADVisionManipulationService>(); service.UseSelectionTint = false;
            service.ReplaceImportedModel(root.transform, runtime.GetAllObjects());
            var loaded = LoadProject.Load(json); Require(loaded.Success, "Canonical fixture failed");
            var snapshot = loaded.Snapshot;
            var view = new UnityViewHost(runtime, service, snapshot);
            var registry = SemanticQueryTools.Create(snapshot, new ProjectAssociation("integration-check"), hostTools: ViewTools.Create(view));
            JObject Identity() => new JObject { ["projectId"] = registry.ProjectId, ["snapshotId"] = registry.SnapshotId };
            async Task<JObject> Arguments(JObject extra = null)
            {
                var state = await registry.ExecuteAsync("get_view_state", Identity());
                Require((bool)state["success"], "View read failed");
                var args = Identity(); args["viewSessionId"] = state["data"]["viewSessionId"].DeepClone();
                args["expectedRevision"] = state["data"]["revision"].DeepClone(); args["operationId"] = Guid.NewGuid().ToString("N");
                if (extra != null) foreach (var p in extra.Properties()) args[p.Name] = p.Value.DeepClone();
                return args;
            }
            async Task<JObject> Apply(string command, JObject extra = null)
            {
                var result = await registry.ExecuteAsync(command, await Arguments(extra));
                Require((bool)result["success"], command + " failed: " + result); return result;
            }
            JObject Ids(params string[] ids) => new JObject { ["objectIds"] = new JArray(ids) };
            Require(registry.Declarations.Any(d => (string)d["name"] == "detach_for_inspection"), "View tools not exposed");
            await Apply("select_objects", Ids("P"));
            var invalid = await registry.ExecuteAsync("select_objects", await Arguments(Ids("Q", "missing")));
            Require(Code(invalid) == "UNKNOWN_OBJECT_ID" && service.GetSelectedIds().SequenceEqual(new[] { "P" }), "Invalid batch partially changed selection");
            var scope = Identity(); scope["objectId"] = "A";
            await registry.ExecuteAsync("set_scope", scope);
            await Apply("select_objects", Ids("O"));
            Require(service.IsSelected("O"), "Query scope improperly blocked explicit view target");
            await Apply("hide_objects", Ids("A")); Require(!nodes[2].activeInHierarchy, "Assembly hide failed");
            await Apply("show_objects", Ids("P"));
            Require(nodes[2].activeInHierarchy && !nodes[3].activeInHierarchy, "Unhide failed to restore ancestor or exposed sibling");
            await Apply("show_objects", Ids("R"));
            Vector3 original = nodes[2].transform.position;
            var detachArgs = await Arguments(Ids("P"));
            var detached = await registry.ExecuteAsync("detach_for_inspection", detachArgs);
            Require((bool)detached["success"] && service.IsDetached("P") && (nodes[2].transform.position - original).magnitude >= 0.1f, "Inspection detach did not move part");
            Vector3 moved = nodes[2].transform.position;
            var replay = await registry.ExecuteAsync("detach_for_inspection", detachArgs);
            Require((bool)replay["receipt"]["replayed"] && nodes[2].transform.position == moved, "Replay moved object twice");
            var conflict = (JObject)detachArgs.DeepClone(); conflict["objectIds"] = new JArray("Q");
            Require(Code(await registry.ExecuteAsync("detach_for_inspection", conflict)) == "IDEMPOTENCY_CONFLICT", "Operation ID accepted different target");
            await Apply("focus_objects", Ids("A"));
            Require(service.IsInFocus("P") && !service.IsInFocus("O") && nodes[4].activeInHierarchy, "Focus failed to include detached descendant or changed visibility");
            await Apply("reset_objects", Ids("A"));
            Require(!service.IsDetached("P") && nodes[2].transform.position == original && service.IsInFocus("P") && nodes[4].activeInHierarchy, "Scoped reset failed or changed focus");
            // Detach multiple: the parts move together and keep their relative layout.
            Vector3 p0 = nodes[2].transform.position, q0 = nodes[3].transform.position;
            await Apply("detach_for_inspection", Ids("P", "Q"));
            Require(service.IsDetached("P") && service.IsDetached("Q") && (nodes[2].transform.position - p0).magnitude >= 0.1f &&
                Vector3.Distance(nodes[3].transform.position - nodes[2].transform.position, q0 - p0) < 1e-4f, "Group detach did not move parts together");
            await Apply("reset_objects", Ids("A"));
            // Explode: one assembly separates its parts away from their shared barycenter.
            var explode = Ids("A"); explode["mode"] = "explode";
            await Apply("detach_for_inspection", explode);
            Vector3 barycenter = (p0 + q0) / 2;
            Require(service.IsDetached("P") && service.IsDetached("Q") &&
                Vector3.Distance(nodes[2].transform.position, barycenter) > Vector3.Distance(p0, barycenter) + 0.05f &&
                Vector3.Distance(nodes[3].transform.position, barycenter) > Vector3.Distance(q0, barycenter) + 0.05f, "Explode did not separate parts from the barycenter");
            var lone = Ids("P"); lone["mode"] = "explode";
            Require(Code(await registry.ExecuteAsync("detach_for_inspection", await Arguments(lone))) == "INVALID_ARGUMENTS", "Explode accepted a single part");
            Require(Code(await registry.ExecuteAsync("detach_for_inspection", await Arguments(Ids("R")))) == "INVALID_ARGUMENTS", "Root assembly detach accepted");
            await Apply("reset_objects", Ids("A"));
            Require(!service.IsDetached("P") && !service.IsDetached("Q") && nodes[2].transform.position == p0, "Reset did not undo explode");
            var staleFocus = await Arguments(Ids("A")); service.Focus("O");
            Require(Code(await registry.ExecuteAsync("focus_objects", staleFocus)) == "REVISION_CONFLICT", "Human focus change ignored");
            await Apply("hide_objects", Ids("O"));
            await Apply("clear_focus");
            Require(!service.IsFocusActive && !nodes[4].activeInHierarchy, "Clear focus changed explicit hides");
            await Apply("focus_objects", Ids("A"));
            var stale = await Arguments(Ids("Q")); service.Select("P");
            Require(Code(await registry.ExecuteAsync("select_objects", stale)) == "REVISION_CONFLICT", "Human selection change ignored");
            service.BeginMultiSelect();
            Require(Code(await registry.ExecuteAsync("clear_selection", await Arguments())) == "INTERACTION_BUSY", "CADEN interrupted multi-pick");
            service.EndMultiSelect();
            using (var cancelled = new CancellationTokenSource())
            {
                var args = await Arguments(Ids("Q")); cancelled.Cancel();
                Require(Code(await registry.ExecuteAsync("select_objects", args, cancelled.Token)) == "CANCELLED" && service.IsSelected("P"), "Cancelled action changed selection");
            }
            // Force a failure after reset has changed poses/visibility; the original view must return.
            service.EnterScope("A"); service.Detach("P"); service.MoveObject("P", Vector3.right * 0.3f);
            var beforeRollback = service.CaptureCadenView(runtime.GetAllObjects().Keys).Fingerprint;
            int throws = 0;
            Action failOnce = () => { if (throws++ == 0) throw new InvalidOperationException("Injected post-mutation failure"); };
            service.ScopeChanged += failOnce;
            try
            {
                var failed = await registry.ExecuteAsync("reset_view", await Arguments());
                Require(Code(failed) == "INTERNAL_ERROR", "Injected action failure did not surface");
                Require(service.CaptureCadenView(runtime.GetAllObjects().Keys).Fingerprint == beforeRollback, "Failed action did not roll back the whole view");
            }
            finally { service.ScopeChanged -= failOnce; }
            // Cancellation during the synchronous commit returns/replays its receipt, not CANCELLED.
            using (var afterCommit = new CancellationTokenSource())
            {
                Action cancel = () => afterCommit.Cancel(); service.ScopeChanged += cancel;
                try
                {
                    var args = await Arguments();
                    var committed = await registry.ExecuteAsync("reset_view", args, afterCommit.Token);
                    Require((bool)committed["success"] && (bool)committed["receipt"]["applied"], "Committed action lost receipt after cancellation");
                    var recovered = await registry.ExecuteAsync("reset_view", args, afterCommit.Token);
                    Require((bool)recovered["receipt"]["replayed"], "Cancelled retry failed to recover committed receipt");
                }
                finally { service.ScopeChanged -= cancel; }
            }
            await Apply("reset_view");
            Require(nodes.Values.All(n => n.activeInHierarchy) && service.GetSelectedIds().Count == 0, "Whole-view reset failed");
            var old = await Arguments(Ids("P"));
            var pending = registry.ExecuteAsync("select_objects", old);
            runtime.Clear();
            Require(Code(await pending) == "STALE_SNAPSHOT_REFERENCE", "Queued action survived model replacement");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(manager); UnityEngine.Object.DestroyImmediate(runtimeObject);
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(camera);
        }
    }
}
