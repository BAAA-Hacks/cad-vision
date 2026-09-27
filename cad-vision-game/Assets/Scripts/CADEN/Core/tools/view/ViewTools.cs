#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Core.Tools.View
{
    public interface IViewHost
    {
        bool Available { get; }
        Task<JObject> ReadAsync(CancellationToken cancellation);
        Task<JObject> ApplyAsync(string command, JObject arguments, CancellationToken cancellation);
        Task<JObject?> RecoverAsync(string command, JObject arguments);
    }

    public static class ViewTools
    {
        public static readonly IReadOnlyList<string> Names = Array.AsReadOnly(new[] {
            "get_view_state", "select_objects", "clear_selection", "focus_objects", "clear_focus",
            "hide_objects", "show_objects", "detach_for_inspection", "reattach_objects", "reset_objects", "reset_view" });
        public static IEnumerable<ICadenTool> Create(IViewHost host) => Names.Select(name => name == "get_view_state"
            ? (ICadenTool)new ViewRead(host) : new ViewAction(host, name));

        private class ViewRead : IAsyncCadenTool, ICapabilityCadenTool
        {
            protected readonly IViewHost Host;
            public string Name { get; }
            public bool Available => Name == "get_view_state" || Host.Available;
            public ViewRead(IViewHost host, string name = "get_view_state") { Host = host; Name = name; }
            public JObject Declaration
            {
                get
                {
                    JObject Text() => new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 };
                    var properties = new JObject { ["projectId"] = Text(), ["snapshotId"] = Text() };
                    var required = new JArray("projectId", "snapshotId");
                    if (Name != "get_view_state")
                    {
                        properties["operationId"] = Text(); properties["viewSessionId"] = Text();
                        properties["expectedRevision"] = new JObject { ["type"] = "integer", ["minimum"] = 0 };
                        required.Add("operationId"); required.Add("viewSessionId"); required.Add("expectedRevision");
                    }
                    if (new[] { "select_objects", "focus_objects", "hide_objects", "show_objects", "detach_for_inspection", "reattach_objects", "reset_objects" }.Contains(Name))
                    {
                        properties["objectIds"] = new JObject { ["type"] = "array", ["items"] = Text(), ["minItems"] = 1, ["maxItems"] = 64 };
                        required.Add("objectIds");
                    }
                    if (Name == "detach_for_inspection")
                    {
                        properties["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("group", "explode"), ["default"] = "group" };
                        properties["spread"] = new JObject { ["type"] = "number", ["minimum"] = 0.25, ["maximum"] = 3, ["default"] = 1 };
                    }
                    if (Name == "select_objects") properties["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("replace", "add", "remove"), ["default"] = "replace" };
                    if (Name == "reset_objects") properties["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("object", "subtree"), ["default"] = "subtree" };
                    string description = Name switch
                    {
                        "get_view_state" => "Read live selected IDs, interaction scope, detach state, viewSessionId and view revision. Visibility readback is unavailable. Selection does not limit explicit-name queries.",
                        "select_objects" => "Select exact IDs using replace/add/remove. Changes user selection; replace is blocked during multi-select picking.",
                        "clear_selection" => "Clear the user's selection. Blocked during multi-select picking.",
                        "focus_objects" => "Use the Unity menu Focus operation: emphasize targets and logical descendants, ghost/dim other parts without hiding them. Use for focus, isolate, only show, or show just. Preserve visibility, selection, poses and query/interaction scope.",
                        "clear_focus" => "Clear the Unity menu Focus effect, restoring normal appearance. Preserve explicit hides, selection, poses and scope; this does not unhide objects.",
                        "hide_objects" => "Hide exact objects and their logical descendants, including detached members.",
                        "show_objects" => "Unhide exact objects and logical descendants, restoring necessary hidden ancestors. Other hidden branches stay hidden.",
                        "detach_for_inspection" => "Temporarily detach parts or subassemblies. mode group (default; detach one or several): move them together to the viewer's right, clear of their assemblies, keeping their relative layout. mode explode: move each away from the targets' shared barycenter (mean geometry center), its distance scaled by 1 + spread (default 1 doubles it); parts that still overlap another (a small part inside a long one) are pushed further out until clear, up to 2 m; one assembly ID explodes its direct children; needs at least two parts. Listing an object and its descendant moves the descendant with it. Already detached objects are left in place. Does not change CAD mates or engineering metadata.",
                        "reattach_objects" => "Restore detached objects to their original parent and imported local pose. Already attached is a no-op.",
                        "reset_objects" => "Restore target imported poses/parents. Default subtree includes logical descendants even when detached; object mode restores only each specified object. Preserve visibility and selection.",
                        _ => "Explicit whole-view reset: restore all imported poses and model placement, show all, clear selection and interaction scope. Never use for 'reset this part'."
                    };
                    if (Name != "get_view_state") description += " Requires viewSessionId/expectedRevision from current view context and a unique operationId; exact retries reuse original arguments. Receipts last for this loaded Unity view session.";
                    return new JObject { ["name"] = Name, ["description"] = description,
                        ["parameters"] = new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = required } };
                }
            }
            public JObject Execute(JObject arguments) => throw new InvalidOperationException("Unity tools require asynchronous dispatch.");
            public virtual Task<JObject> ExecuteAsync(JObject arguments, CancellationToken cancellation) => Host.ReadAsync(cancellation);
        }
        private sealed class ViewAction : ViewRead, IActionCadenTool
        {
            public ViewAction(IViewHost host, string name) : base(host, name) { }
            public override Task<JObject> ExecuteAsync(JObject arguments, CancellationToken cancellation) => Host.ApplyAsync(Name, arguments, cancellation);
            public Task<JObject?> RecoverCommittedAsync(JObject arguments) => Host.RecoverAsync(Name, arguments);
        }
    }
}
