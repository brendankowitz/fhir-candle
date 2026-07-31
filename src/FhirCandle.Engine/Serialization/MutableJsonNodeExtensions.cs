using System.Text.Json.Nodes;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Serialization;

/// <summary>
/// Re-exposes the raw backing <see cref="JsonObject"/> of an Ignixa node.
/// </summary>
/// <remarks>
/// Ignixa made <c>BaseJsonNode.MutableNode</c> internal in 0.6.28, steering public callers to the
/// typed facades, and left <see cref="IMutableJsonNode"/> public as the escape hatch for raw access.
/// Candle stores, clones and rewrites resources as JSON rather than through typed facades, so it
/// takes the escape hatch; the extension keeps the property spelling the call sites already use.
/// </remarks>
public static class MutableJsonNodeExtensions
{
    extension(BaseJsonNode node)
    {
        public JsonObject MutableNode => ((IMutableJsonNode)node).MutableNode;
    }
}
