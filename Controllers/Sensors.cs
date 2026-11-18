using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("sensors")]
public sealed class Sensors(HomeAssistantState state) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpGet("state")]
    public HomeAssistantSnapshot GetState()
    {
        Response.Headers.CacheControl = "no-store";
        return state.Snapshot;
    }

    [HttpGet("events")]
    public async Task Events(CancellationToken cancellation)
    {
        var subscription = state.Subscribe();
        if (subscription is null)
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        try
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-store";
            Response.Headers["X-Accel-Buffering"] = "no";
            await Write("retry: 5000\n\n", cancellation);
            using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
            var ready = subscription.Reader.WaitToReadAsync(cancellation).AsTask();
            var tick = heartbeat.WaitForNextTickAsync(cancellation).AsTask();
            while (!cancellation.IsCancellationRequested)
            {
                await Task.WhenAny(ready, tick);
                if (ready.IsCompleted)
                {
                    if (!await ready) break;
                    while (subscription.Reader.TryRead(out var snapshot))
                    {
                        await Write($"data: {JsonSerializer.Serialize(snapshot, JsonOptions)}\n\n", cancellation);
                        if (!snapshot.Enabled && snapshot.Status != "starting") return;
                    }
                    ready = subscription.Reader.WaitToReadAsync(cancellation).AsTask();
                }
                if (tick.IsCompleted)
                {
                    if (!await tick) break;
                    await Write(": keepalive\n\n", cancellation);
                    tick = heartbeat.WaitForNextTickAsync(cancellation).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { state.Unsubscribe(subscription); }
    }

    private async Task Write(string data, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await Response.WriteAsync(data, deadline.Token);
        await Response.Body.FlushAsync(deadline.Token);
    }
}
