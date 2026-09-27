using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CADVision;
using Core.Tools;
using Core.Tools.View;
using Core.Primitives.DataStructures.Project;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CADEN.Unity
{
    // One instance per published model, with session-scoped receipts. All Unity access is dispatched.
    public sealed class UnityViewHost : IViewHost
    {
        private readonly CADVisionRuntime runtime;
        private readonly CADVisionManipulationService service;
        private readonly ProjectSnapshot snapshot;
        private readonly SynchronizationContext context;
        private readonly int modelRevision;
        private readonly string sessionId = Guid.NewGuid().ToString("N");
        private readonly Dictionary<string, (string Command, JObject Arguments, JObject Result)> receipts = new Dictionary<string, (string, JObject, JObject)>(StringComparer.Ordinal);
        private string fingerprint;
        private long revision;
        private bool invalidated;
        public bool Available { get; }

        // Construct on the main thread after Task 3 has adopted the runtime registry.
        public UnityViewHost(CADVisionRuntime runtime, CADVisionManipulationService service, ProjectSnapshot snapshot)
        {
            this.runtime = runtime; this.service = service; this.snapshot = snapshot;
            context = SynchronizationContext.Current ?? throw new InvalidOperationException("Unity main-thread context is unavailable.");
            modelRevision = runtime.Revision;
            Available = runtime.Metadata?.HasVerifiedNodeMapping == true && service != null && service.HasCadenMapping(runtime.GetAllObjects());
        }
        public void Invalidate() { invalidated = true; }
        private Task<T> Dispatch<T>(Func<T> action, CancellationToken token)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Post(_ =>
            {
                try { token.ThrowIfCancellationRequested(); Guard(); result.TrySetResult(action()); }
                catch (OperationCanceledException) { result.TrySetCanceled(); }
                catch (Exception ex) { result.TrySetException(ex); }
            }, null);
            return result.Task; // No cancellation race after a mutation commits.
        }
        private void Guard()
        {
            if (invalidated || runtime == null || runtime.Metadata == null || runtime.Revision != modelRevision)
                throw new ToolInputException("STALE_SNAPSHOT_REFERENCE", "The Unity model changed; use the new CADEN session.");
        }
        private CADVisionManipulationService.CadenViewBackup Observe()
        {
            var state = service.CaptureCadenView(runtime.GetAllObjects().Keys);
            if (fingerprint != null && fingerprint != state.Fingerprint) revision++;
            fingerprint = state.Fingerprint; return state;
        }
        private JObject State()
        {
            Guard();
            if (Available) Observe();
            var ids = runtime.GetAllObjects();
            var focused = service == null ? Array.Empty<string>() : service.FocusIds.Where(ids.ContainsKey).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var selected = service == null ? Array.Empty<string>() : service.GetSelectedIds().Where(ids.ContainsKey).ToArray();
            var detached = service == null ? Array.Empty<string>() : ids.Keys.Where(service.IsDetached).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            return new JObject { ["viewSessionId"] = sessionId, ["revision"] = revision, ["modelRevision"] = modelRevision,
                ["viewAvailable"] = Available, ["selectedObjectIds"] = new JArray(selected.Take(64)),
                ["selectedObjectCount"] = selected.Length, ["selectionComplete"] = selected.Length <= 64,
                ["focusedObjectIds"] = new JArray(focused.Take(64)), ["focusedObjectCount"] = focused.Length, ["focusComplete"] = focused.Length <= 64,
                ["interactionScopeId"] = service?.CurrentScopeId != null && ids.ContainsKey(service.CurrentScopeId) ? service.CurrentScopeId : null,
                ["multiSelectActive"] = service != null && service.IsMultiSelectActive,
                ["detachedObjectIds"] = new JArray(detached.Take(64)), ["detachedObjectCount"] = detached.Length, ["detachedComplete"] = detached.Length <= 64,
                ["visibility"] = new JObject { ["status"] = "Unavailable", ["reason"] = "Visibility readback is deferred." },
                ["receiptLifetime"] = "loaded_view_session" };
        }
        public Task<JObject> ReadAsync(CancellationToken cancellation) => Dispatch(State, cancellation);
        private JObject Replay(string command, JObject args)
        {
            if ((string)args["viewSessionId"] != sessionId) throw new ToolInputException("REVISION_CONFLICT", "View session changed; refresh get_view_state.");
            if (!receipts.TryGetValue((string)args["operationId"], out var saved)) return null;
            if (saved.Command != command || !JToken.DeepEquals(saved.Arguments, args))
                throw new ToolInputException("IDEMPOTENCY_CONFLICT", "operationId was already used for different arguments.");
            var replay = (JObject)saved.Result.DeepClone(); replay["receipt"]["replayed"] = true; return replay;
        }
        public Task<JObject> RecoverAsync(string command, JObject arguments) => Dispatch(() => Replay(command, arguments), CancellationToken.None);
        private string[] Expand(IEnumerable<string> ids) => ids.SelectMany(id => snapshot.Indexes.Scope(id, CancellationToken.None)).Distinct(StringComparer.Ordinal).ToArray();
        public Task<JObject> ApplyAsync(string command, JObject arguments, CancellationToken cancellation) => Dispatch(() =>
        {
            var replay = Replay(command, arguments); if (replay != null) return replay;
            if (!Available || !service.HasCadenMapping(runtime.GetAllObjects())) throw new ToolInputException("CAPABILITY_UNAVAILABLE", "Verified geometry mapping and the manipulation service are required.");
            var backup = Observe();
            if ((long)arguments["expectedRevision"] != revision) throw new ToolInputException("REVISION_CONFLICT", "The view changed; refresh get_view_state before acting.");
            if (receipts.Count >= 2048) throw new ToolInputException("QUERY_TOO_LARGE", "View receipt limit reached; reload the CADEN view session.");
            var ids = arguments["objectIds"] is JArray array ? array.Values<string>().Distinct(StringComparer.Ordinal).ToArray()
                : arguments["objectId"] != null ? new[] { (string)arguments["objectId"] } : Array.Empty<string>();
            var registry = runtime.GetAllObjects();
            foreach (var id in ids) if (!snapshot.ComponentsById.ContainsKey(id) || !registry.ContainsKey(id))
                throw new ToolInputException("UNKNOWN_OBJECT_ID", "All requested IDs must be present in both metadata and Unity: " + id);
            string mode = (string)arguments["mode"];
            if (service.IsMultiSelectActive && (command == "clear_selection" || command == "select_objects" && (mode == null || mode == "replace") || command == "reset_view"))
                throw new ToolInputException("INTERACTION_BUSY", "Finish headset multi-selection before replacing selection or resetting the whole view.");
            bool usesHierarchy = new[] { "focus_objects", "hide_objects", "show_objects", "detach_for_inspection", "reset_objects", "reset_view" }.Contains(command);
            if (usesHierarchy && snapshot.Capabilities.Hierarchy != CapabilityState.Available)
                throw new ToolInputException("CAPABILITY_UNAVAILABLE", "A valid metadata hierarchy is required for this view action.");
            cancellation.ThrowIfCancellationRequested(); Guard();
            long previousRevision = revision;
            try
            {
                switch (command)
                {
                    case "select_objects":
                        if (mode == null || mode == "replace") service.ClearSelection();
                        foreach (var id in ids) { if (mode == "remove") service.RemoveFromSelection(id); else service.AddToSelection(id); }
                        break;
                    case "clear_selection": service.ClearSelection(); break;
                    case "focus_objects": service.Focus(ids); break;
                    case "clear_focus": service.ClearFocus(); break;
                    case "hide_objects": service.Hide(Expand(ids)); break;
                    case "show_objects": service.ShowCadenObjects(Expand(ids)); break;
                    case "detach_for_inspection":
                        if (!service.IsDetached(ids[0]))
                        {
                            if (Camera.main == null) throw new ToolInputException("CAPABILITY_UNAVAILABLE", "Headset/view camera is required for inspection placement.");
                            service.DetachCadenForInspection(ids[0], Camera.main.transform.right);
                        }
                        break;
                    case "reattach_objects": foreach (var id in ids) if (service.IsDetached(id) && !service.Reattach(id)) throw new InvalidOperationException("Reattach failed: " + id); break;
                    case "reset_objects": service.ResetCadenObjects(mode == "object" ? ids : ParentFirst(Expand(ids))); break;
                    case "reset_view":
                        service.ResetCadenObjects(ParentFirst(registry.Keys)); service.ResetModelTransform();
                        service.ClearFocus(); service.SetCadenIsolation(registry.Keys, registry.Keys); service.EndMultiSelect(true); service.ResetScope(); break;
                    default: throw new ToolInputException("UNKNOWN_TOOL", "Unknown Unity view command.");
                }
                Guard();
                var after = service.CaptureCadenView(registry.Keys);
                bool changed = backup.Fingerprint != after.Fingerprint;
                revision++; fingerprint = after.Fingerprint;
                var result = new JObject { ["changed"] = changed, ["targetObjectIds"] = new JArray(ids), ["view"] = State(),
                    ["receipt"] = new JObject { ["operationId"] = arguments["operationId"].DeepClone(), ["subsystem"] = "view", ["revision"] = revision, ["applied"] = true, ["replayed"] = false } };
                receipts.Add((string)arguments["operationId"], (command, (JObject)arguments.DeepClone(), (JObject)result.DeepClone()));
                return result;
            }
            catch
            {
                // Never restore a backup onto a newly published model.
                Guard(); service.RestoreCadenView(backup); revision = previousRevision; fingerprint = backup.Fingerprint; throw;
            }
        }, cancellation);
        private string[] ParentFirst(IEnumerable<string> ids)
        {
            int Depth(string id) { int depth = 0; for (var parent = snapshot.Indexes.Parent(id); parent != null; parent = snapshot.Indexes.Parent(parent)) depth++; return depth; }
            return ids.Distinct(StringComparer.Ordinal).OrderBy(Depth).ThenBy(id => id, StringComparer.Ordinal).ToArray();
        }
    }
}
