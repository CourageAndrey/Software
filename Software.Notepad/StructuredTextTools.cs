using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.XPath;
using Json.Path;

namespace Software.Notepad
{
    public sealed record StructureNode(string Label, string Path, StructureNode[] Children, int Line = 0, int Column = 0);

    public static class StructuredTextTools
    {
        public const int MaximumCharacters = 2 * 1024 * 1024;
        public const int MaximumDepth = 128;
        public const int MaximumTreeNodes = 10_000;

        public static string FormatJson(string text, bool compact = false, bool sortKeys = false)
        {
            using var document = ParseJson(text);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = !compact }))
                WriteJson(document.RootElement, writer, sortKeys);
            return Encoding.UTF8.GetString(output.ToArray());
        }

        public static void ValidateJson(string text)
        {
            using var document = ParseJson(text);
        }

        public static string QueryJson(string text, string expression)
        {
            using var document = ParseJson(text);
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = MaximumDepth });
            var results = JsonPath.Parse(expression).Evaluate(node);
            var matches = results.Matches.Take(MaximumTreeNodes + 1).ToArray();
            if (matches.Length > MaximumTreeNodes)
                throw new InvalidOperationException("The query returned too many results.");
            return JoinResults(matches.Select(match => $"{match.Location}\n{match.Value?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null"}"));
        }

        public static StructureNode JsonTree(string text)
        {
            using var document = ParseJson(text);
            int count = 0;
            return Build(document.RootElement, "$", "$");

            StructureNode Build(JsonElement element, string label, string path)
            {
                if (++count > MaximumTreeNodes)
                    throw new InvalidOperationException("The tree exceeds the 10,000-node limit.");
                var children = element.ValueKind switch
                {
                    JsonValueKind.Object => element.EnumerateObject().Select(property =>
                        Build(property.Value, property.Name, path + "[" + JsonSerializer.Serialize(property.Name) + "]")).ToArray(),
                    JsonValueKind.Array => element.EnumerateArray().Select((value, index) => Build(value, $"[{index}]", $"{path}[{index}]")).ToArray(),
                    _ => []
                };
                string summary = element.ValueKind switch
                {
                    JsonValueKind.Object => $"{{{children.Length} properties}}",
                    JsonValueKind.Array => $"[{children.Length} items]",
                    _ => Shorten(element.GetRawText())
                };
                return new StructureNode($"{label}: {summary}", path, children);
            }
        }

        public static string FormatXml(string text, bool compact = false)
        {
            var document = ParseXml(text);
            foreach (var element in document.Descendants())
            {
                string? space = element.AncestorsAndSelf().Select(parent => (string?)parent.Attribute(XNamespace.Xml + "space"))
                    .FirstOrDefault(value => value != null);
                if (space != "preserve" && element.Elements().Any() && !element.Nodes().OfType<XText>().Any(node => !string.IsNullOrWhiteSpace(node.Value)))
                {
                    foreach (var whitespace in element.Nodes().OfType<XText>().Where(node => node is not XCData && string.IsNullOrWhiteSpace(node.Value)).ToArray())
                        whitespace.Remove();
                }
            }
            var output = new StringBuilder();
            using (var writer = XmlWriter.Create(output, new XmlWriterSettings
            {
                Indent = !compact, IndentChars = "  ", OmitXmlDeclaration = true, NewLineHandling = NewLineHandling.None
            }))
                document.WriteTo(writer);
            string declaration = document.Declaration?.ToString() ?? "";
            return declaration + (declaration.Length > 0 && !compact ? Environment.NewLine : "") + output;
        }

        public static void ValidateXml(string text) => ParseXml(text);

        public static string[] ValidateXmlSchema(string text, string schemaText)
        {
            var document = ParseXml(text);
            CheckSize(schemaText);
            var messages = new List<string>();
            using var reader = XmlReader.Create(new StringReader(schemaText), ReaderSettings());
            var schemas = new XmlSchemaSet { XmlResolver = null };
            schemas.ValidationEventHandler += (_, eventArgs) => messages.Add(eventArgs.Message);
            schemas.Add(null, reader);
            schemas.Compile();
            if (messages.Count == 0)
                document.Validate(schemas, (_, eventArgs) => messages.Add(eventArgs.Message));
            return messages.ToArray();
        }

        public static string QueryXml(string text, string expression)
        {
            var document = ParseXml(text);
            var navigator = document.CreateNavigator();
            var namespaces = new XmlNamespaceManager(navigator.NameTable);
            foreach (var attribute in document.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                namespaces.AddNamespace(attribute.Name.LocalName == "xmlns" ? "d" : attribute.Name.LocalName, attribute.Value);
            object result = navigator.Evaluate(expression, namespaces);
            if (result is not XPathNodeIterator nodes)
                return Convert.ToString(result, CultureInfo.InvariantCulture) ?? "";
            return JoinResults(Values());

            IEnumerable<string> Values()
            {
                int count = 0;
                while (nodes.MoveNext())
                {
                    if (++count > MaximumTreeNodes)
                        throw new InvalidOperationException("The query returned too many results.");
                    yield return nodes.Current!.NodeType is XPathNodeType.Element or XPathNodeType.Root ? nodes.Current.OuterXml : nodes.Current.Value;
                }
            }
        }

        public static StructureNode XmlTree(string text)
        {
            var document = ParseXml(text);
            int count = 0;
            return Build(document.Root!, "");

            StructureNode Build(XElement element, string parent)
            {
                if (++count > MaximumTreeNodes)
                    throw new InvalidOperationException("The tree exceeds the 10,000-node limit.");
                int position = element.ElementsBeforeSelf().Count(sibling => sibling.Name == element.Name) + 1;
                string path = parent + $"/*[local-name()={XPathLiteral(element.Name.LocalName)} and namespace-uri()={XPathLiteral(element.Name.NamespaceName)}][{position}]";
                var children = element.Elements().Select(child => Build(child, path)).ToList();
                foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
                {
                    if (++count > MaximumTreeNodes)
                        throw new InvalidOperationException("The tree exceeds the 10,000-node limit.");
                    children.Insert(0, new StructureNode($"@{attribute.Name.LocalName}: {Shorten(attribute.Value)}",
                        path + $"/@*[local-name()={XPathLiteral(attribute.Name.LocalName)} and namespace-uri()={XPathLiteral(attribute.Name.NamespaceName)}]", []));
                }
                var info = (IXmlLineInfo)element;
                return new StructureNode(element.Name.LocalName + (element.HasElements ? "" : ": " + Shorten(element.Value)),
                    path, children.ToArray(), info.LineNumber, info.LinePosition);
            }
        }

        public static XDocument ParseXml(string text)
        {
            CheckSize(text);
            using (var check = XmlReader.Create(new StringReader(text), ReaderSettings()))
            {
                while (check.Read())
                {
                    if (check.Depth > MaximumDepth)
                        throw new XmlException("XML nesting exceeds the 128-level limit.");
                }
            }
            using var reader = XmlReader.Create(new StringReader(text), ReaderSettings());
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }

        private static XmlReaderSettings ReaderSettings() => new()
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumCharacters
        };

        private static JsonDocument ParseJson(string text)
        {
            CheckSize(text);
            var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            try { RejectDuplicateKeys(document.RootElement); }
            catch { document.Dispose(); throw; }
            return document;
        }

        private static void RejectDuplicateKeys(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new JsonException($"Duplicate JSON property: {property.Name}");
                    RejectDuplicateKeys(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    RejectDuplicateKeys(item);
            }
        }

        private static void WriteJson(JsonElement element, Utf8JsonWriter writer, bool sortKeys)
        {
            if (element.ValueKind == JsonValueKind.Object && sortKeys)
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteJson(property.Value, writer, sortKeys);
                }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array && sortKeys)
            {
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteJson(item, writer, sortKeys);
                writer.WriteEndArray();
            }
            else
                element.WriteTo(writer);
        }

        private static string JoinResults(IEnumerable<string> values)
        {
            var result = new StringBuilder();
            foreach (string value in values)
            {
                if (result.Length + (long)value.Length + 4 > MaximumCharacters)
                    throw new InvalidOperationException("Query output exceeds the 2 Mi-character limit.");
                if (result.Length > 0)
                    result.AppendLine().AppendLine();
                result.Append(value);
            }
            return result.ToString();
        }

        private static void CheckSize(string text)
        {
            if (text.Length > MaximumCharacters)
                throw new InvalidOperationException("XML/JSON tools support at most 2 Mi characters per document.");
        }

        private static string Shorten(string value) => value.Length <= 100 ? value : value[..100] + "...";

        private static string XPathLiteral(string value)
        {
            if (!value.Contains('\''))
                return "'" + value + "'";
            if (!value.Contains('"'))
                return "\"" + value + "\"";
            return "concat(" + string.Join(",\"'\",", value.Split('\'').Select(part => "'" + part + "'")) + ")";
        }
    }
}