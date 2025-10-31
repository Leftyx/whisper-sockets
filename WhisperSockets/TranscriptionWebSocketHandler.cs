using WhisperSockets;

public sealed class TranscriptionWebSocketHandler
{
    private readonly WhisperFactoryWrapper _whisper;

    public TranscriptionWebSocketHandler(WhisperFactoryWrapper whisper)
    {
        _whisper = whisper;
    }

    public async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("Expected WebSocket");
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

        var session = new TranscriptionSession(ws, _whisper);

        await session.RunAsync(ctx.RequestAborted);
    }
}

