using System.Collections.Concurrent;
using Darp.CodeEditor.CSharp.Execution.TestApi;
using Shouldly;
using static Darp.CodeEditor.CSharp.Execution.Tests.TestRunners;

namespace Darp.CodeEditor.CSharp.Execution.Tests;

public sealed class ScriptBatcherTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Items_go_to_the_script_in_batches_and_each_gets_its_result()
    {
        await using CSharpScriptRunner runner = await StartAsync();
        using ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(Handler("return 0;"), Cancellation);
        var batchSizes = new ConcurrentQueue<int>();
        await using var batcher = new ScriptBatcher<Sample, int>(
            samples =>
            {
                batchSizes.Enqueue(samples.Length);
                return handler.HandleBatchAsync(samples, Cancellation);
            },
            new ScriptBatcherOptions { MaxBatchSize = 64 }
        );

        int[] results = await Task.WhenAll(
            Enumerable.Range(0, 1000).Select(i => batcher.SubmitAsync(new Sample(i, []), Cancellation))
        );

        results.ShouldBe(Enumerable.Range(0, 1000).Select(i => i * 2));
        batchSizes.Sum().ShouldBe(1000);
        batchSizes.Max().ShouldBeLessThanOrEqualTo(64);
        // Items queue while a batch is in flight, so far fewer calls than items cross to the runner.
        batchSizes.Count.ShouldBeLessThan(100);
    }

    [Fact]
    public async Task Failed_batch_fails_its_items_and_later_batches_still_go_out()
    {
        var handling = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        await using var batcher = new ScriptBatcher<int, int>(async items =>
        {
            if (items.Contains(0))
            {
                handling.TrySetResult();
                await release.Task;
                throw new InvalidOperationException("batch failed");
            }
            return [.. items.Select(i => i * 10)];
        });

        Task<int>[] failing = [batcher.SubmitAsync(0, Cancellation)];
        await handling.Task.WaitAsync(Cancellation);
        Task<int>[] next = [batcher.SubmitAsync(1, Cancellation), batcher.SubmitAsync(2, Cancellation)];
        release.SetResult();

        (await Should.ThrowAsync<InvalidOperationException>(() => failing[0])).Message.ShouldBe("batch failed");
        (await Task.WhenAll(next)).ShouldBe([10, 20]);
    }

    [Fact]
    public async Task Submitting_waits_for_room_when_the_queue_is_full()
    {
        var handling = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        await using var batcher = new ScriptBatcher<int>(
            async _ =>
            {
                handling.TrySetResult();
                await release.Task;
            },
            new ScriptBatcherOptions { Capacity = 2 }
        );
        Task inFlight = batcher.SubmitAsync(0, Cancellation);
        await handling.Task.WaitAsync(Cancellation);
        Task[] queued = [batcher.SubmitAsync(1, Cancellation), batcher.SubmitAsync(2, Cancellation)];

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Should.ThrowAsync<OperationCanceledException>(() => batcher.SubmitAsync(3, timeout.Token));

        release.SetResult();
        await Task.WhenAll([inFlight, .. queued]);
    }
}
