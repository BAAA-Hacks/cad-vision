#nullable enable
using System;
namespace Core.Tools
{
    public sealed class ToolLimits
    {
        public int MaxObjectIds { get; }
        public int MaxScopeIds { get; }
        public int MaxResults { get; }
        public int MaxDepth { get; }
        public int MaxResponseCharacters { get; }
        public ToolLimits(int maxObjectIds = 32, int maxScopeIds = 256, int maxResults = 50, int maxDepth = 32, int maxResponseCharacters = 64000)
        {
            if (maxObjectIds < 1 || maxObjectIds > 256 || maxScopeIds < 1 || maxScopeIds > 10000 || maxResults < 1 || maxResults > 1000 || maxDepth < 1 || maxDepth > 128 || maxResponseCharacters < 4096 || maxResponseCharacters > 1000000)
                throw new ArgumentException("Tool limits must stay inside supported safety bounds.");
            MaxObjectIds = maxObjectIds; MaxScopeIds = maxScopeIds; MaxResults = maxResults; MaxDepth = maxDepth; MaxResponseCharacters = maxResponseCharacters;
        }
    }
}
