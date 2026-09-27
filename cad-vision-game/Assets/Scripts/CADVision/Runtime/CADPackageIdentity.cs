using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CADVision
{
    /// <summary>Identity of the exact bundled CAD pair used by a same-room session.</summary>
    public static class CADPackageIdentity
    {
        public const int Protocol = 2;
        public static string Current { get; private set; }
        public static byte[] ModelBytes { get; private set; }
        public static byte[] MetadataBytes { get; private set; }

        public static string Compute(byte[] glb, byte[] metadata)
        {
            if (glb == null || metadata == null) throw new ArgumentNullException();
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Protocol);
                writer.Write(glb.Length);
                writer.Write(glb);
                writer.Write(metadata.Length);
                writer.Write(metadata);
            }
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
        }

        public static void SetLoaded(byte[] glb, string metadata)
        {
            ModelBytes = glb;
            MetadataBytes = Encoding.UTF8.GetBytes(metadata);
            Current = Compute(ModelBytes, MetadataBytes);
        }

        public static void Clear()
        {
            Current = null;
            ModelBytes = null;
            MetadataBytes = null;
        }
    }
}
