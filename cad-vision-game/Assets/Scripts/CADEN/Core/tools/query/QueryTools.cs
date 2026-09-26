#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Core.Tools.Query
{
    public static class QueryTools
    {
        public static ToolRegistry Create(MetadataStore? store) => new ToolRegistry(new ICadenTool[]
        {
            new ModelSummaryTool(store), new SearchObjectsTool(store), new GetObjectTool(store), new GetHierarchyTool(store)
        });
    }

    public abstract class QueryTool : ICadenTool
    {
        private readonly MetadataStore? store;
        protected MetadataStore Store => store ?? throw new ToolInputException("MODEL_NOT_LOADED", "No metadata is loaded. Load a metadata JSON in the host application first.");
        protected QueryTool(MetadataStore? store) { this.store = store; }
        public abstract string Name { get; }
        public abstract JObject Declaration { get; }
        public abstract JObject Execute(JObject args);
        protected JObject Definition(string description, JObject properties, params string[] required) => new JObject
        {
            ["name"] = Name, ["description"] = description,
            ["parameters"] = new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JArray(required) }
        };
        protected static JObject Arg(string type, string description) => new JObject { ["type"] = type, ["description"] = description };
        protected static int Bounded(JObject args, string field, int fallback, int min, int max)
        {
            int n = (int?)args[field] ?? fallback;
            if (n < min || n > max) throw new ToolInputException("INVALID_ARGUMENTS", $"{field} must be between {min} and {max}.");
            return n;
        }
        protected JObject Page(IEnumerable<JObject> source, JObject args)
        {
            int limit = Bounded(args, "limit", 20, 1, 50), offset = Bounded(args, "offset", 0, 0, 10000);
            int total = 0; var page = new List<JObject>();
            foreach (var row in source) { if (total >= offset && page.Count < limit) page.Add(row); total++; }
            return new JObject { ["provenance"] = Store.Provenance, ["items"] = new JArray(page), ["total"] = total,
                ["offset"] = offset, ["limit"] = limit, ["truncated"] = offset + page.Count < total,
                ["nextOffset"] = offset + page.Count < total ? new JValue(offset + page.Count) : JValue.CreateNull() };
        }
    }

    public sealed class ModelSummaryTool : QueryTool
    {
        public ModelSummaryTool(MetadataStore? store) : base(store) { }
        public override string Name => "get_model_summary";
        public override JObject Declaration => Definition("Describe the loaded model, root, object counts, provenance and property availability. Query this before model-specific claims. Counts are exported occurrences, not necessarily physical BOM quantities.", new JObject());
        public override JObject Execute(JObject args)
        {
            var availability = new JObject();
            foreach (string field in PropertyContract.Fields)
            {
                var states = Store.Objects.Select(o => (string)PropertyContract.Read(Store, (string)o["id"]!, field)["status"]!).ToList();
                availability[field] = new JObject { ["available"] = states.Count(s => s == "available"), ["missing"] = states.Count(s => s == "missing"), ["invalid"] = states.Count(s => s == "invalid") };
            }
            return new JObject { ["name"] = Store.Name, ["root"] = Store.Context(Store.RootId), ["provenance"] = Store.Provenance,
                ["objectCount"] = Store.Count, ["partCount"] = Store.Objects.Count(o => (string?)o["type"] == "part"),
                ["assemblyCount"] = Store.Objects.Count(o => (string?)o["type"] == "assembly"), ["propertyAvailability"] = availability,
                ["relationships"] = "Mate/interference querying is not implemented; empty lists do not prove absence or successful checks." };
        }
    }

    public sealed class SearchObjectsTool : QueryTool
    {
        public SearchObjectsTool(MetadataStore? store) : base(store) { }
        public override string Name => "search_objects";
        public override JObject Declaration => Definition("Case-insensitive substring search over object ID and display name. All words must match. Returns distinct instances with hierarchy context; never silently resolves repeated names. parent_id restricts to direct children. Paginate with offset.",
            new JObject { ["query"] = Arg("string", "Name words or an exact ID; not a semantic geometry search."),
                ["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray("part", "assembly") },
                ["parent_id"] = Arg("string", "Optional exact parent ID; direct children only."), ["limit"] = Arg("integer", "1–50, default 20."), ["offset"] = Arg("integer", "Zero-based result offset, default 0.") }, "query");
        public override JObject Execute(JObject args)
        {
            string query = ((string)args["query"]!).Trim();
            if (query.Length == 0) throw new ToolInputException("INVALID_ARGUMENTS", "query must not be blank.");
            string? parent = (string?)args["parent_id"]; if (parent != null) Store.Get(parent);
            string[] words = query.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var results = Store.Objects.Where(o => ((string?)args["type"] == null || (string?)o["type"] == (string?)args["type"]) &&
                (parent == null || (string?)o["parentId"] == parent) && words.All(w => (((string)o["id"]!) + " " + (string)o["name"]!).IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(o => string.Equals((string?)o["id"], query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(o => (string)o["name"]!, StringComparer.OrdinalIgnoreCase).ThenBy(o => (string)o["id"]!, StringComparer.Ordinal)
                .Select(o => Store.Context((string)o["id"]!));
            return Page(results, args);
        }
    }

    public sealed class GetObjectTool : QueryTool
    {
        public GetObjectTool(MetadataStore? store) : base(store) { }
        public override string Name => "get_object";
        public override JObject Declaration => Definition("Retrieve an exact object ID returned by search or hierarchy. Returns contextual identity and property status/value/reason/sourceField. Missing or invalid values are unavailable evidence. Optional fields selects known properties.",
            new JObject { ["id"] = Arg("string", "Exact object ID; names are not unique."), ["fields"] = new JObject { ["type"] = "array", ["description"] = "Optional property names: " + string.Join(", ", PropertyContract.Fields), ["items"] = new JObject { ["type"] = "string" } } }, "id");
        public override JObject Execute(JObject args)
        {
            string id = (string)args["id"]!; var context = Store.Context(id);
            var fields = args["fields"] is JArray requested ? requested.Select(v => (string)v!).Distinct().ToArray() : PropertyContract.Fields;
            var properties = new JObject(); foreach (string field in fields) properties[field] = PropertyContract.Read(Store, id, field);
            return new JObject { ["object"] = context, ["properties"] = properties, ["provenance"] = Store.Provenance };
        }
    }

    public sealed class GetHierarchyTool : QueryTool
    {
        public GetHierarchyTool(MetadataStore? store) : base(store) { }
        public override string Name => "get_hierarchy";
        public override JObject Declaration => Definition("Explore the root or an exact assembly ID. Returns a paginated flat breadth-first list with depth and parent IDs. depth=1 returns direct children; bounded to 5. Root context is separate. Child counts show where to explore further.",
            new JObject { ["id"] = Arg("string", "Optional object ID; defaults to model root."), ["depth"] = Arg("integer", "1–5, default 1."), ["limit"] = Arg("integer", "1–50, default 20."), ["offset"] = Arg("integer", "Zero-based result offset, default 0.") });
        public override JObject Execute(JObject args)
        {
            string id = (string?)args["id"] ?? Store.RootId;
            var root = Store.Context(id); int depth = Bounded(args, "depth", 1, 1, 5);
            bool deeper = false;
            IEnumerable<JObject> Rows()
            {
                var queue = new Queue<(string id, int depth)>(); queue.Enqueue((id, 0));
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    if (item.depth > 0)
                    {
                        var row = Store.Context(item.id); row["depth"] = item.depth;
                        if (item.depth == depth && (int)row["childCount"]! > 0) deeper = true;
                        yield return row;
                    }
                    if (item.depth < depth)
                        foreach (string child in ((JArray)Store.Get(item.id)["childIds"]!).Select(v => (string)v!)) queue.Enqueue((child, item.depth + 1));
                }
            }
            var result = Page(Rows(), args); result["root"] = root; result["requestedDepth"] = depth;
            result["deeperLevelsOmitted"] = deeper;
            return result;
        }
    }
}
