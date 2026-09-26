using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace CADVision
{
    public sealed class CadReceiveResult
    {
        public bool success;
        public string message;
        public int objectCount;
        public int revision;
    }

    /// <summary>Small HTTP/1.1 endpoint for the local shipper. No OS HTTP URL registration required.</summary>
    public sealed class CadDesignServer : IDisposable
    {
        public const int MaxGlbBytes = 100 * 1024 * 1024;
        public const int MaxMetadataBytes = 8 * 1024 * 1024;
        public const int MaxPackageBytes = MaxGlbBytes + MaxMetadataBytes + 65536;
        private readonly TcpListener listener;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Func<byte[], string, CancellationToken, Task<CadReceiveResult>> import;
        private readonly TimeSpan timeout;
        private int busy;
        private int disposed;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public Task Completion { get; }

        public CadDesignServer(IPAddress address, int port,
            Func<byte[], string, CancellationToken, Task<CadReceiveResult>> import,
            TimeSpan? timeout = null)
        {
            this.import = import ?? throw new ArgumentNullException(nameof(import));
            this.timeout = timeout ?? TimeSpan.FromSeconds(120);
            listener = new TcpListener(address, port);
            listener.Start(4);
            Completion = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                    {
                        await RejectBusy(client).ConfigureAwait(false);
                        continue;
                    }
                    _ = Handle(client); // Handle observes errors and always releases the slot.
                }
            }
            catch (Exception e) when (lifetime.IsCancellationRequested &&
                (e is SocketException || e is ObjectDisposedException)) { }
        }

        private async Task RejectBusy(TcpClient client)
        {
            using (client)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                using var registration = deadline.Token.Register(client.Close);
                try
                {
                    var stream = client.GetStream();
                    // Consume headers before replying so closing a socket with unread request
                    // bytes doesn't replace the HTTP rejection with a TCP reset on Windows.
                    await ReadHeaderBlock(stream, deadline.Token).ConfigureAwait(false);
                    await WriteResponse(stream, 409, new CadReceiveResult
                        { message = "Another design is being received or imported." }).ConfigureAwait(false);
                    client.Client.Shutdown(SocketShutdown.Send);
                    var discard = new byte[8192];
                    while (await stream.ReadAsync(discard, 0, discard.Length, deadline.Token).ConfigureAwait(false) > 0) { }
                }
                catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException || e is OperationCanceledException) { }
            }
        }

        private async Task Handle(TcpClient client)
        {
            using (client)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                deadline.CancelAfter(timeout);
                using var registration = deadline.Token.Register(client.Close);
                try
                {
                    var stream = client.GetStream();
                    byte[] package = await ReadRequest(stream, deadline.Token).ConfigureAwait(false);
                    var pair = DecodePackage(package);
                    deadline.Token.ThrowIfCancellationRequested();
                    var result = await import(pair.glb, pair.json, deadline.Token).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    await WriteResponse(stream, result.success ? 200 : 422, result).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    if (!deadline.IsCancellationRequested)
                    {
                        int status = e is HttpFailure failure ? failure.Status :
                            e is InvalidDataException || e is JsonException || e is DecoderFallbackException ? 422 : 500;
                        if (status == 500) UnityEngine.Debug.LogException(e);
                        var result = new CadReceiveResult { message = status == 500 ? "Design import failed; check the receiver log." : e.Message };
                        try { await WriteResponse(client.GetStream(), status, result).ConfigureAwait(false); }
                        catch (Exception sendError) when (sendError is IOException || sendError is SocketException || sendError is ObjectDisposedException || sendError is InvalidOperationException) { }
                    }
                }
                finally { Interlocked.Exchange(ref busy, 0); }
            }
        }

        private static async Task<string[]> ReadHeaderBlock(Stream stream, CancellationToken token)
        {
            var header = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one, 0, 1, token).ConfigureAwait(false) != 1)
                    throw new HttpFailure(400, "Incomplete HTTP headers.");
                header.Add(one[0]);
                if (header.Count > 8192) throw new HttpFailure(431, "HTTP headers are too large.");
                int n = header.Count;
                if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 && header[n - 2] == 13 && header[n - 1] == 10) break;
            }
            return Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
        }

        private static async Task<byte[]> ReadRequest(Stream stream, CancellationToken token)
        {
            string[] lines = await ReadHeaderBlock(stream, token).ConfigureAwait(false);
            string[] request = lines[0].Split(' ');
            if (request.Length != 3 || (request[2] != "HTTP/1.1" && request[2] != "HTTP/1.0"))
                throw new HttpFailure(400, "Invalid HTTP request line.");
            if (request[1] != "/design") throw new HttpFailure(404, "Use POST /design.");
            if (request[0] != "POST") throw new HttpFailure(405, "Use POST /design.");
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length && lines[i].Length > 0; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) throw new HttpFailure(400, "Malformed HTTP header.");
                string name = lines[i].Substring(0, colon);
                if (fields.ContainsKey(name)) throw new HttpFailure(400, "Duplicate HTTP header.");
                fields.Add(name, lines[i].Substring(colon + 1).Trim());
            }
            if (fields.ContainsKey("Transfer-Encoding")) throw new HttpFailure(400, "Chunked uploads are unsupported; send Content-Length.");
            if (fields.ContainsKey("Expect")) throw new HttpFailure(417, "Send the body without Expect: 100-continue.");
            if (!fields.TryGetValue("Content-Length", out var value) || !long.TryParse(value, out long size) || size <= 0)
                throw new HttpFailure(411, "A positive Content-Length is required.");
            if (size > MaxPackageBytes) throw new HttpFailure(413, "Design package exceeds the upload limit.");
            if (!fields.TryGetValue("Content-Type", out string type) || !type.Equals("application/zip", StringComparison.OrdinalIgnoreCase))
                throw new HttpFailure(415, "Content-Type must be application/zip.");
            var bytes = new byte[(int)size];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes, offset, Math.Min(65536, bytes.Length - offset), token).ConfigureAwait(false);
                if (read == 0) throw new HttpFailure(400, "Upload ended before the complete package arrived.");
                offset += read;
            }
            return bytes;
        }

        public static (byte[] glb, string json) DecodePackage(byte[] bytes)
        {
            if (bytes == null || bytes.Length > MaxPackageBytes) throw new InvalidDataException("Invalid package size.");
            using var input = new MemoryStream(bytes, false);
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            if (zip.Entries.Count != 2) throw new InvalidDataException("Package must contain exactly model.glb and metadata.json.");
            var glb = zip.GetEntry("model.glb");
            var metadata = zip.GetEntry("metadata.json");
            if (glb == null || metadata == null) throw new InvalidDataException("Package must contain model.glb and metadata.json at its root.");
            byte[] modelBytes = ReadEntry(glb, MaxGlbBytes);
            string json = new UTF8Encoding(false, true).GetString(ReadEntry(metadata, MaxMetadataBytes));
            if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
            var parsed = new CadMetadata(json);
            CadGlbPackage.Validate(modelBytes, parsed);
            return (modelBytes, json);
        }

        private static byte[] ReadEntry(ZipArchiveEntry entry, int limit)
        {
            if (entry.Length <= 0 || entry.Length > limit) throw new InvalidDataException($"{entry.FullName} exceeds its size limit or is empty.");
            using var input = entry.Open();
            var bytes = new byte[(int)entry.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = input.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) throw new InvalidDataException("Truncated ZIP entry.");
                offset += read;
            }
            if (input.ReadByte() != -1) throw new InvalidDataException("ZIP entry exceeds its declared length.");
            return bytes;
        }

        private static byte[] Response(int status, CadReceiveResult result)
        {
            byte[] body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(result));
            byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            var bytes = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, bytes, 0, head.Length);
            Buffer.BlockCopy(body, 0, bytes, head.Length, body.Length);
            return bytes;
        }

        private static Task WriteResponse(Stream stream, int status, CadReceiveResult result)
        {
            byte[] bytes = Response(status, result);
            return stream.WriteAsync(bytes, 0, bytes.Length);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            listener.Stop();
        }

        public sealed class HttpFailure : IOException
        {
            public int Status { get; }
            public HttpFailure(int status, string message) : base(message) { Status = status; }
        }
    }
}
