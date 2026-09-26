using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CADVision
{
    public static class CadGlbPackage
    {
        /// <summary>Validate the binary envelope and mapping before allocating Unity resources.</summary>
        public static void Validate(byte[] bytes, CadMetadata metadata)
        {
            if (bytes == null || bytes.Length < 20) throw new InvalidDataException("GLB is missing or truncated.");
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != 0x46546C67 || reader.ReadUInt32() != 2)
                throw new InvalidDataException("Expected a glTF 2 binary file.");
            if (reader.ReadUInt32() != bytes.Length) throw new InvalidDataException("GLB length does not match its header.");
            uint length = reader.ReadUInt32();
            if (reader.ReadUInt32() != 0x4E4F534A || length > bytes.Length - 20 || length % 4 != 0)
                throw new InvalidDataException("Invalid GLB JSON chunk.");
            JObject gltf;
            try { gltf = JObject.Parse(new UTF8Encoding(false, true).GetString(reader.ReadBytes((int)length))); }
            catch (Exception e) when (e is JsonException || e is DecoderFallbackException)
            { throw new InvalidDataException("Invalid GLB JSON.", e); }
            while (stream.Position < stream.Length)
            {
                if (stream.Length - stream.Position < 8) throw new InvalidDataException("Truncated GLB chunk header.");
                uint chunkLength = reader.ReadUInt32();
                reader.ReadUInt32();
                if (chunkLength % 4 != 0 || chunkLength > stream.Length - stream.Position)
                    throw new InvalidDataException("Truncated GLB chunk.");
                stream.Position += chunkLength;
            }
            // The two-file contract must be self-contained; do not fetch exporter-supplied URLs.
            foreach (string collection in new[] { "buffers", "images" })
                if (gltf[collection] is JArray resources)
                    foreach (var resource in resources)
                    {
                        var uri = (string)resource["uri"];
                        if (uri != null && !uri.StartsWith("data:", StringComparison.Ordinal))
                            throw new InvalidDataException("The GLB must embed all buffers and images.");
                    }
            if (!(gltf["nodes"] is JArray nodes)) throw new InvalidDataException("GLB has no nodes.");
            foreach (var pair in metadata.NodeIndices)
                if (pair.Value >= nodes.Count) throw new InvalidDataException($"'{pair.Key}' references missing GLB node {pair.Value}.");
        }
    }
}
