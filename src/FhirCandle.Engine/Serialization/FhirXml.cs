using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Ignixa.Abstractions;
using Ignixa.Serialization;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Serialization;

public static class FhirXml
{
    private static readonly XNamespace F = "http://hl7.org/fhir";
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    public static string Serialize(ResourceJsonNode resource, IFhirSchemaProvider schema, bool pretty = false)
    {
        XElement root = WriteResource(resource.MutableNode, schema);
        return root.ToString(pretty ? SaveOptions.None : SaveOptions.DisableFormatting);
    }

    public static ResourceJsonNode Parse(string xml, IFhirSchemaProvider schema)
    {
        XElement root = XElement.Parse(xml);
        JsonObject obj = ReadResource(root, schema);
        return JsonSourceNodeFactory.Parse((JsonNode)obj);
    }

    private static XElement WriteResource(JsonObject obj, IFhirSchemaProvider schema)
    {
        string resourceType = obj["resourceType"]!.GetValue<string>();
        IType type = schema.GetTypeDefinition(resourceType)
            ?? throw new NotSupportedException($"Unknown resource type '{resourceType}'");
        var el = new XElement(F + resourceType);
        WriteChildren(el, obj, type, schema);
        return el;
    }

    private static void WriteChildren(XElement parent, JsonObject obj, IType type, IFhirSchemaProvider schema)
    {
        foreach (IType child in type.Children.OrderBy(c => c.Order))
        {
            string name = child.Info.Name;
            string actualName = name;
            JsonNode? value;
            JsonObject? shadow;

            if (child.Info.IsChoiceElement)
            {
                KeyValuePair<string, JsonNode?> match = obj.FirstOrDefault(kv =>
                    kv.Value is not null
                    && kv.Key.Length > name.Length
                    && kv.Key.StartsWith(name, StringComparison.Ordinal)
                    && !kv.Key.StartsWith('_'));
                if (match.Key is null) continue;
                actualName = match.Key;
                value = match.Value;
                shadow = obj.TryGetPropertyValue("_" + actualName, out JsonNode? s) ? s as JsonObject : null;
            }
            else
            {
                if (!obj.TryGetPropertyValue(name, out value) || value is null) continue;
                shadow = obj.TryGetPropertyValue("_" + name, out JsonNode? s) ? s as JsonObject : null;
            }

            if (name == "contained" && value is JsonArray containedArr)
            {
                foreach (JsonNode? c in containedArr)
                    parent.Add(new XElement(F + "contained", WriteResource(c!.AsObject(), schema)));
                continue;
            }
            if (name == "div")
            {
                parent.Add(XElement.Parse(value!.GetValue<string>()));
                continue;
            }

            IType effectiveType = ResolveEffectiveType(child, actualName, schema);

            IEnumerable<(JsonNode? Val, JsonNode? Shadow)> items = value is JsonArray arr
                ? arr.Select((v, i) => (v, (shadow as JsonNode as JsonArray)?[i]))
                : [(value, (JsonNode?)shadow)];

            foreach ((JsonNode? item, JsonNode? itemShadow) in items)
            {
                var el = new XElement(F + actualName);
                if (effectiveType.Info.IsPrimitive)
                {
                    if (item is not null) el.SetAttributeValue("value", JsonScalarToString(item));
                    if (itemShadow is JsonObject shObj) WriteChildren(el, shObj, ExtensionCarrier(schema), schema);
                }
                else if (item is JsonObject complex)
                {
                    IType childType = (complex["resourceType"]?.GetValue<string>() is { } rt
                        ? schema.GetTypeDefinition(rt) : null) ?? effectiveType;
                    WriteChildren(el, complex, childType, schema);
                }
                parent.Add(el);
            }
        }
    }

    private static JsonObject ReadResource(XElement el, IFhirSchemaProvider schema)
    {
        string resourceType = el.Name.LocalName;
        IType type = schema.GetTypeDefinition(resourceType)
            ?? throw new NotSupportedException($"Unknown resource type '{resourceType}'");
        var obj = new JsonObject { ["resourceType"] = resourceType };
        ReadChildren(el, obj, type, schema);
        return obj;
    }

    private static void ReadChildren(XElement parent, JsonObject obj, IType type, IFhirSchemaProvider schema)
    {
        foreach (IType child in type.Children.OrderBy(c => c.Order))
        {
            string name = child.Info.Name;

            if (name == "contained")
            {
                List<XElement> containedEls = parent.Elements(F + "contained").ToList();
                if (containedEls.Count == 0) continue;
                var containedArr = new JsonArray();
                foreach (XElement wrapper in containedEls)
                {
                    XElement? nested = wrapper.Elements().FirstOrDefault();
                    if (nested is not null) containedArr.Add(ReadResource(nested, schema));
                }
                obj["contained"] = containedArr;
                continue;
            }

            if (name == "div")
            {
                XElement? divEl = parent.Elements(Xhtml + "div").FirstOrDefault();
                if (divEl is not null) obj["div"] = divEl.ToString(SaveOptions.DisableFormatting);
                continue;
            }

            List<(XElement El, string ActualName)> matches = child.Info.IsChoiceElement
                ? parent.Elements()
                    .Where(e => e.Name.Namespace == F
                        && e.Name.LocalName.Length > name.Length
                        && e.Name.LocalName.StartsWith(name, StringComparison.Ordinal))
                    .Select(e => (e, e.Name.LocalName))
                    .ToList()
                : parent.Elements(F + name).Select(e => (e, name)).ToList();

            if (matches.Count == 0) continue;

            if (child.IsCollection)
            {
                var valueArr = new JsonArray();
                var shadowArr = new JsonArray();
                bool anyShadow = false;
                foreach ((XElement elMatch, string actualName) in matches)
                {
                    (JsonNode? val, JsonObject? shadow) = ReadChild(elMatch, child, actualName, schema);
                    valueArr.Add(val);
                    shadowArr.Add(shadow);
                    if (shadow is not null) anyShadow = true;
                }
                obj[name] = valueArr;
                if (anyShadow) obj["_" + name] = shadowArr;
            }
            else
            {
                (XElement elMatch, string actualName) = matches[0];
                (JsonNode? val, JsonObject? shadow) = ReadChild(elMatch, child, actualName, schema);
                obj[actualName] = val;
                if (shadow is not null) obj["_" + actualName] = shadow;
            }
        }
    }

    private static (JsonNode? Value, JsonObject? Shadow) ReadChild(
        XElement el, IType schemaChild, string actualName, IFhirSchemaProvider schema)
    {
        IType effectiveType = ResolveEffectiveType(schemaChild, actualName, schema);

        if (effectiveType.Info.IsPrimitive)
        {
            JsonNode? value = el.Attribute("value") is { } attr
                ? PrimitiveStringToJson(attr.Value, effectiveType.Info.Primitive)
                : null;

            JsonObject? shadow = null;
            if (el.Elements().Any())
            {
                var shadowObj = new JsonObject();
                ReadChildren(el, shadowObj, ExtensionCarrier(schema), schema);
                if (shadowObj.Count > 0) shadow = shadowObj;
            }
            return (value, shadow);
        }

        var complexObj = new JsonObject();
        ReadChildren(el, complexObj, effectiveType, schema);
        return (complexObj, null);
    }

    // ExtensionCarrier(schema) returns schema.GetTypeDefinition("Element") (id + extension children).
    private static IType ExtensionCarrier(IFhirSchemaProvider schema) =>
        schema.GetTypeDefinition("Element")
            ?? throw new NotSupportedException("Schema is missing the 'Element' type definition");

    // An element's own IType only carries its element name and declared FhirPrimitive/Children
    // when it IS a primitive (e.g. "id"); for complex-typed elements (e.g. Patient.name),
    // childrenFactory is null and the real structure lives on the referenced complex type
    // (e.g. "HumanName"), reachable only via ITypeExtended.Types/DefaultTypeName. Choice
    // elements (value[x], registered stripped of "[x]") additionally carry multiple candidate
    // Types entries; the one actually present is identified by matching its FHIR type code
    // against the suffix of the concrete element/property name (e.g. "valueQuantity" -> "Quantity").
    private static IType ResolveEffectiveType(IType child, string actualName, IFhirSchemaProvider schema)
    {
        if (child is not ITypeExtended extended) return child;

        string? typeName;
        if (child.Info.IsChoiceElement && actualName.Length > child.Info.Name.Length)
        {
            string suffix = actualName[child.Info.Name.Length..];
            typeName = extended.Types
                .Select(t => t.Code)
                .FirstOrDefault(code => string.Equals(code, suffix, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            typeName = extended.DefaultTypeName ?? extended.Types.FirstOrDefault()?.Code;
        }

        return (typeName is not null ? schema.GetTypeDefinition(typeName) : null) ?? child;
    }

    private static JsonNode PrimitiveStringToJson(string raw, FhirPrimitive kind) => kind switch
    {
        FhirPrimitive.Boolean => JsonValue.Create(bool.Parse(raw)),
        FhirPrimitive.Integer or FhirPrimitive.UnsignedInt or FhirPrimitive.PositiveInt
            or FhirPrimitive.Integer64 or FhirPrimitive.Decimal
            => JsonValue.Create(decimal.Parse(raw, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(raw),
    };

    private static string JsonScalarToString(JsonNode n) =>
        n is JsonValue v && v.TryGetValue(out bool b) ? (b ? "true" : "false")
        : n is JsonValue v2 && v2.TryGetValue(out decimal d) ? d.ToString(CultureInfo.InvariantCulture)
        : n.GetValue<string>();
}
