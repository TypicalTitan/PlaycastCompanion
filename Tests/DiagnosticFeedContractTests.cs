using System.IO.Pipes;
using System.Diagnostics;
using System.ComponentModel;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace PlaycastCompanion.Tests;

public static class DiagnosticFeedContractTests
{
    public static async Task RunAsync()
    {
        AcceptsVersionOneAndRejectsInvalidFrames();
        await ReadsSplitMultipleAndBoundedFrames();
        await ReceivesAuthenticatedPipeEventsAndReconnects();
        await ReceivesNativeNodePipeClient();
    }

    private static async Task ReceivesNativeNodePipeClient()
    {
        var pipe = "PlaycastCompanion.NodeContract." + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new SessionDiagnosticsListener(Environment.UserName, _ => received.TrySetResult(), status =>
        {
            if (status.StartsWith("Waiting for Playcast diagnostic feed", StringComparison.Ordinal)) ready.TrySetResult();
        }, pipe);
        listener.Start();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("""
            const net = require('node:net');
            const client = net.connect('\\\\.\\pipe\\' + process.argv[1]);
            client.on('connect', () => client.end(JSON.stringify({version:1,category:'diagnostics',payload:{event:'native_node_client'}}) + '\n'));
            client.on('error', error => { console.error(error.message); process.exitCode = 1; });
            """);
        start.ArgumentList.Add(pipe);
        Process? client;
        try { client = Process.Start(start); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2)
        {
            Console.WriteLine("SKIP: native Node pipe client contract; Node.js is not installed.");
            return;
        }
        using (client)
        {
            Check(client != null, "Native Node client must start.");
            try
            {
                await client!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var error = await client.StandardError.ReadToEndAsync();
                Check(client.ExitCode == 0, "Native Node client failed: " + error);
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                if (client != null && !client.HasExited)
                {
                    try { client.Kill(); }
                    catch (InvalidOperationException) { }
                    await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }
        await listener.StopAsync();
    }

    private static void AcceptsVersionOneAndRejectsInvalidFrames()
    {
        Check(Parse("""{"version":1,"category":"realtime","payload":{"type":"hello"}}"""), "Version-one events should be accepted.");
        Check(!Parse("""{"version":2,"category":"realtime","payload":{}}"""), "Unsupported versions must be rejected.");
        Check(!Parse("""{"version":1,"category":"../../escape","payload":{}}"""), "Unrecognized categories must be rejected.");
        Check(!Parse("{invalid"), "Malformed JSON must be rejected.");
        Check(!SessionDiagnosticsListener.TryParseFrame(new byte[SessionDiagnosticsListener.MaxFrameBytes + 1], out _, out _),
            "Oversize frames must be rejected before parsing.");
    }

    private static async Task ReadsSplitMultipleAndBoundedFrames()
    {
        var wire = Encoding.UTF8.GetBytes("first\nsecond\r\n" + new string('x', 32000) + "\n");
        var frames = new List<byte[]>();
        await foreach (var frame in new DiagnosticFrameReader().ReadAsync(new MemoryStream(wire))) frames.Add(frame);
        Check(frames.Count == 3 && Encoding.UTF8.GetString(frames[1]) == "second\r" && frames[2].Length == 32000,
            "Reader must preserve split frames and multiple frames per read.");
        var oversize = new MemoryStream(new byte[SessionDiagnosticsListener.MaxFrameBytes + 1]);
        await ExpectInvalidFrame(oversize, "Oversize streams must never allocate an unbounded line.");
        await ExpectInvalidFrame(new MemoryStream(Encoding.UTF8.GetBytes("unfinished")), "Partial disconnects need an explicit error.");
    }

    private static async Task ReceivesAuthenticatedPipeEventsAndReconnects()
    {
        var pipe = "PlaycastCompanion.ContractTests." + Guid.NewGuid().ToString("N");
        var events = new List<JsonObject>();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var status = new List<string>();
        using var listener = new SessionDiagnosticsListener(Environment.UserName, envelope =>
        {
            lock (events)
            {
                events.Add(envelope);
                if (events.Count == 2) second.TrySetResult();
                if (events.Count == 3) third.TrySetResult();
            }
        }, message => { lock (status) status.Add(message); }, pipe);
        listener.Start();
        using (var client = Client(pipe))
        {
            await client.ConnectAsync(5000);
            await Send(client, """{"version":1,"category":"realtime","payload":{"sequence":1}}""");
            await Send(client, """{"version":1,"category":"ipc","payload":{"sequence":2}}""");
            await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using (var client = Client(pipe))
        {
            await client.ConnectAsync(5000);
            await Send(client, """{"version":99,"category":"realtime","payload":{}}""");
            await Send(client, """{"version":1,"category":"state","payload":{"sequence":3}}""");
            await third.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await listener.StopAsync();
        Check(events.Count == 3, "Authenticated feed must read continuously and reconnect without accepting invalid events.");
        lock (status)
            Check(status.Any(message => message.Contains("frame rejected")) && status.Any(message => message.Contains("connected")),
                "Connection coverage and rejected frames must be observable.");
    }

    private static NamedPipeClientStream Client(string pipe) => new(".", pipe, PipeDirection.Out,
        PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
    private static async Task Send(Stream stream, string message) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(message + "\n"));
    private static bool Parse(string value) => SessionDiagnosticsListener.TryParseFrame(Encoding.UTF8.GetBytes(value), out _, out _);
    private static void Check(bool condition, string message) => SessionLogContractTests.Check(condition, message);

    private static async Task ExpectInvalidFrame(Stream stream, string message)
    {
        try
        {
            await foreach (var unused in new DiagnosticFrameReader().ReadAsync(stream)) { }
        }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }
}
