using System.Net;
using Windows.Foundation;

namespace PlaycastCompanion.Tests;

public static class LightingContractTests
{
    public static async Task RunAsync()
    {
        await PendingRetriesAreBoundedAndRecover();
        await CancellationStopsWaitingAndPreventsNewWork();
        await ChromaCleanupReportsActualOutcome(HttpStatusCode.OK, false);
        await ChromaCleanupReportsActualOutcome(HttpStatusCode.InternalServerError, false);
        await ChromaCleanupReportsActualOutcome(HttpStatusCode.OK, true);
    }

    private static async Task PendingRetriesAreBoundedAndRecover()
    {
        var gate = new WinRtOperationGate();
        var stuck = new ControlledOperation();
        int starts = 0;
        Func<IAsyncOperation<int>> create = () => { starts++; return stuck; };
        await ExpectFailure(() => gate.InvokeAsync(create, TimeSpan.FromMilliseconds(5), CancellationToken.None));
        for (int attempt = 0; attempt < 200; attempt++)
            await ExpectFailure(() => gate.InvokeAsync(create, TimeSpan.FromMilliseconds(5), CancellationToken.None));
        Check(starts == 1 && stuck.Cancels == 1 && stuck.Closes == 0, "Pending retries accumulated operations.");
        stuck.State = AsyncStatus.Canceled;
        var complete = new ControlledOperation { State = AsyncStatus.Completed };
        Check(await gate.InvokeAsync(() => complete, TimeSpan.FromMilliseconds(5), CancellationToken.None) == 42,
            "Completed cancellation prevented recovery.");
        Check(stuck.Closes == 1 && complete.Closes == 1, "Terminal operations were not closed.");
    }

    private static async Task CancellationStopsWaitingAndPreventsNewWork()
    {
        var gate = new WinRtOperationGate();
        var operation = new ControlledOperation();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await ExpectFailure(() => gate.InvokeAsync(() => operation, TimeSpan.FromSeconds(10), cancellation.Token));
        Check(clock.Elapsed < TimeSpan.FromSeconds(1) && operation.Cancels == 1, "Shutdown cancellation remained unbounded.");
        int starts = 0;
        await ExpectFailure(() => gate.InvokeAsync(() => { starts++; return operation; },
            TimeSpan.FromMilliseconds(5), cancellation.Token));
        Check(starts == 0, "Cancelled caller started new work.");
        var complete = new ControlledOperation { State = AsyncStatus.Completed };
        using var race = new CancellationTokenSource();
        await ExpectFailure(() => new WinRtOperationGate().InvokeAsync(() =>
        {
            race.Cancel();
            return complete;
        }, TimeSpan.FromSeconds(1), race.Token));
        Check(complete.Results == 0 && complete.Closes == 1, "Cancellation at completion still released a result for painting.");
    }

    private static async Task ChromaCleanupReportsActualOutcome(HttpStatusCode status, bool transportFailure)
    {
        using var handler = new ChromaFixtureHandler(status, transportFailure);
        using var http = new HttpClient(handler);
        var controller = new ChromaController(new AppConfig(), http);
        controller.StartBlackout();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (controller.StatusText != "Holding blackout" && DateTime.UtcNow < deadline) await Task.Delay(10);
        Check(controller.StatusText == "Holding blackout", "Synthetic Chroma did not engage.");
        await controller.StopBlackoutAsync();
        bool success = !transportFailure && (int)status is >= 200 and < 300;
        Check(handler.Deletes == 1 && !controller.IsHolding, "Stop must release once and stop reassertion.");
        Check(controller.StatusText == (success ? "Hold ended; Chroma session released"
            : "Hold ended; Chroma release unverified"), "Cleanup claimed an unobserved result.");
    }

    private static async Task ExpectFailure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected bounded cancellation or pending-operation rejection.");
    }
    private static void Check(bool condition, string message) => SessionLogContractTests.Check(condition, message);

    private sealed class ControlledOperation : IAsyncOperation<int>
    {
        public AsyncStatus State = AsyncStatus.Started;
        public int Cancels, Closes, Results;
        public uint Id => 1;
        public AsyncStatus Status => State;
        public Exception ErrorCode => new InvalidOperationException("Fixture");
        public AsyncOperationCompletedHandler<int>? Completed { get; set; }
        public void Cancel() => Cancels++;
        public void Close() { Check(State != AsyncStatus.Started, "Pending operation was closed."); Closes++; }
        public int GetResults() { Check(State == AsyncStatus.Completed, "Results read before completion."); Results++; return 42; }
    }

    private sealed class ChromaFixtureHandler(HttpStatusCode status, bool transportFailure) : HttpMessageHandler
    {
        public int Deletes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete)
            {
                Deletes++;
                if (transportFailure) throw new HttpRequestException("Synthetic SDK endpoint unavailable");
                return Task.FromResult(new HttpResponseMessage(status));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(request.Method == HttpMethod.Post ? "{\"uri\":\"http://fixture.invalid/session\"}" : "{}") });
        }
    }
}
