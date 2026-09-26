using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CADVision;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class CadReceiverTests
{
    private static IEnumerator RunNetworkTest(Func<Task> test)
    {
        var work = Task.Run(test);
        double deadline = UnityEditor.EditorApplication.timeSinceStartup + 15;
        while (!work.IsCompleted)
        {
            Assert.That(UnityEditor.EditorApplication.timeSinceStartup, Is.LessThan(deadline), "Network test timed out.");
            yield return null;
        }
        if (work.IsFaulted) throw work.Exception;
    }

    private static byte[] Package(bool missingMetadata = false, bool nestedNames = false)
    {
        using var glb = new MemoryStream();
        using (var writer = new BinaryWriter(glb, Encoding.UTF8, true))
        {
            string json = "{\"nodes\":[{}]}";
            byte[] chunk = Encoding.UTF8.GetBytes(json.PadRight((json.Length + 3) / 4 * 4));
            writer.Write(0x46546C67u); writer.Write(2u); writer.Write((uint)(20 + chunk.Length));
            writer.Write((uint)chunk.Length); writer.Write(0x4E4F534Au); writer.Write(chunk);
        }
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var entry = zip.CreateEntry(nestedNames ? "../model.glb" : "model.glb").Open())
                glb.WriteTo(entry);
            if (!missingMetadata)
                using (var writer = new StreamWriter(zip.CreateEntry("metadata.json").Open()))
                    writer.Write("{\"schemaVersion\":\"1.0\",\"project\":{\"rootObjectId\":\"A\"},\"objects\":[{\"id\":\"A\",\"glbNodeIndex\":0}]}");
        }
        return output.ToArray();
    }

    private static async Task<string> Send(int port, byte[] body, string method = "POST", int? declaredLength = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        var header = Encoding.ASCII.GetBytes($"{method} /design HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/zip\r\nContent-Length: {declaredLength ?? body.Length}\r\n\r\n");
        await stream.WriteAsync(header, 0, header.Length);
        if (body.Length > 0) await stream.WriteAsync(body, 0, body.Length);
        if (declaredLength > body.Length) client.Client.Shutdown(SocketShutdown.Send);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void RejectsIncompleteOrNestedPackages(bool missing, bool nested)
        => Assert.Throws<InvalidDataException>(() => CadDesignServer.DecodePackage(Package(missing, nested)));

    [UnityTest]
    public IEnumerator AcknowledgesOnlyAfterImportCompletesAndRejectsConcurrentUpload() => RunNetworkTest(AcknowledgementTest);

    private static async Task AcknowledgementTest()
    {
        var entered = new TaskCompletionSource<bool>();
        var finish = new TaskCompletionSource<CadReceiveResult>();
        using var server = new CadDesignServer(IPAddress.Loopback, 0, (glb, json, token) =>
        { entered.SetResult(true); return finish.Task; });
        var first = Send(server.Port, Package());
        Assert.That(await Task.WhenAny(entered.Task, Task.Delay(5000)), Is.SameAs(entered.Task));
        Assert.That(first.IsCompleted, Is.False);
        // Send only headers on the rejected connection; receiver closes without consuming a body.
        var rejected = await Send(server.Port, Array.Empty<byte>());
        Assert.That(rejected, Does.StartWith("HTTP/1.1 409"));
        finish.SetResult(new CadReceiveResult { success = true, objectCount = 1, revision = 1 });
        Assert.That(await first, Does.Contain("\"success\":true"));
    }

    [UnityTest]
    public IEnumerator IncompleteUploadDoesNotInvokeImporterAndNextUploadSucceeds() => RunNetworkTest(IncompleteUploadTest);

    private static async Task IncompleteUploadTest()
    {
        int calls = 0;
        using var server = new CadDesignServer(IPAddress.Loopback, 0, (glb, json, token) =>
        { calls++; return Task.FromResult(new CadReceiveResult { success = true }); });
        var response = await Send(server.Port, new byte[2], declaredLength: 100);
        Assert.That(response, Does.StartWith("HTTP/1.1 400"));
        Assert.That(calls, Is.Zero);
        Assert.That(await Send(server.Port, Package()), Does.StartWith("HTTP/1.1 200"));
        Assert.That(calls, Is.EqualTo(1));
    }

    [UnityTest]
    public IEnumerator RejectsOversizedAndWrongMethodRequestsWithoutImport() => RunNetworkTest(RequestValidationTest);

    private static async Task RequestValidationTest()
    {
        using var server = new CadDesignServer(IPAddress.Loopback, 0,
            (glb, json, token) => throw new Exception("Importer should not run"));
        Assert.That(await Send(server.Port, Array.Empty<byte>(), declaredLength: CadDesignServer.MaxPackageBytes + 1), Does.StartWith("HTTP/1.1 413"));
        Assert.That(await Send(server.Port, Array.Empty<byte>(), "GET"), Does.StartWith("HTTP/1.1 405"));
    }

    [UnityTest]
    public IEnumerator ShutdownCancelsPendingImport() => RunNetworkTest(ShutdownTest);

    private static async Task ShutdownTest()
    {
        var entered = new TaskCompletionSource<bool>();
        var canceled = new TaskCompletionSource<bool>();
        var server = new CadDesignServer(IPAddress.Loopback, 0, async (glb, json, token) =>
        {
            entered.SetResult(true);
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { canceled.SetResult(true); throw; }
            return null;
        });
        var request = Send(server.Port, Package());
        try
        {
            Assert.That(await Task.WhenAny(entered.Task, Task.Delay(5000)), Is.SameAs(entered.Task));
            server.Dispose();
            Assert.That(await Task.WhenAny(canceled.Task, Task.Delay(5000)), Is.SameAs(canceled.Task));
            try { await request; } catch (IOException) { }
        }
        finally { server.Dispose(); }
    }

    [UnityTest]
    public IEnumerator TestAsmTransfersIntoRealRuntimeAndAcknowledgesThreeObjects()
    {
        string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
        string glb = Path.Combine(repo, "TestASM.glb");
        string metadata = Path.Combine(repo, "TestASM_metadata_sample.json");
        if (!File.Exists(glb) || !File.Exists(metadata)) Assert.Ignore("Local TestASM fixture is absent.");
        using var package = new MemoryStream();
        using (var zip = new ZipArchive(package, ZipArchiveMode.Create, true))
        {
            using (var entry = zip.CreateEntry("model.glb").Open())
            { var bytes = File.ReadAllBytes(glb); entry.Write(bytes, 0, bytes.Length); }
            using (var entry = zip.CreateEntry("metadata.json").Open())
            { var bytes = File.ReadAllBytes(metadata); entry.Write(bytes, 0, bytes.Length); }
        }
        var host = new GameObject("receiver test");
        var receiver = host.AddComponent<CadDesignReceiver>();
        receiver.AllowLan = false;
        receiver.Port = 18085;
        try
        {
            receiver.StartReceiver();
            Assert.That(receiver.IsListening, Is.True);
            var sending = Send(receiver.Port, package.ToArray());
            while (!sending.IsCompleted) yield return null;
            if (sending.IsFaulted) throw sending.Exception;
            Assert.That(sending.Result, Does.StartWith("HTTP/1.1 200"));
            Assert.That(sending.Result, Does.Contain("\"objectCount\":3"));
            Assert.That(host.GetComponent<CADVisionRuntime>().GetAllObjects().Count, Is.EqualTo(3));
        }
        finally
        {
            receiver.StopReceiver();
            host.GetComponent<CADVisionRuntime>().Clear();
            UnityEngine.Object.DestroyImmediate(host);
        }
    }
}
