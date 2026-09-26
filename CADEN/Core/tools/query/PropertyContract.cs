using System.Linq;
using Core.Primitives.Operations.Project;
using Newtonsoft.Json.Linq;

namespace Core.Tools.Query
{
    // Compatibility adapter while the higher-level query tools migrate to ProjectSnapshot.
    public static class PropertyContract
    {
        public static string[] Fields => MetadataPropertyRules.Fields.ToArray();
        internal static JObject Read(MetadataStore store, string id, string field)
        {
            if (!Fields.Contains(field)) throw new ToolInputException("INVALID_ARGUMENTS", "Unknown property field: " + field);
            return MetadataPropertyRules.Read(store.Get(id), (JObject)store.Document["project"]!, store.IsFixture, store.Source(id, field), field);
        }
    }
}
