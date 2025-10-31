namespace WhisperSockets;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

internal sealed class TranscriptionSession
{
    private readonly WebSocket _socket;
    private readonly WhisperFactoryWrapper _whisper;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private readonly Channel<byte[]> _audioChannel = Channel.CreateUnbounded<byte[]>();
    private string _language = "auto";
    private bool _endRequested;

    public TranscriptionSession(WebSocket socket, WhisperFactoryWrapper whisper)
    {
        _socket = socket;
        _whisper = whisper;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var transcriptionTask = Task.Run(() => BackgroundTranscribeAsync(cancellationToken), cancellationToken);

        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(_buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleTextMessage(result.Count);
                    if (_endRequested)
                    {
                        break;
                    }
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    await HandleBinaryMessageAsync(result, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal: user pressed stop, token cancelled, or socket dropped
            Console.WriteLine("[Session] Cancelled.");
        }
        catch (WebSocketException wsex)
        {
            // network reset, browser tab closed, etc.
            Console.WriteLine($"[Session] WebSocket error: {wsex.Message}");
        }
        catch (Exception ex)
        {
            // unexpected bug
            Console.WriteLine($"[Session] Unexpected error: {ex}");
            await SendErrorAsync(ex.Message);
        }
        finally
        {
            _audioChannel.Writer.TryComplete();
            await transcriptionTask;
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "session end", CancellationToken.None);
        }
    }

    private async Task SendErrorAsync(string message)
    {
        if (_socket.State != WebSocketState.Open) return;
        var err = JsonSerializer.Serialize(new { type = "error", message });
        var bytes = Encoding.UTF8.GetBytes(err);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private void HandleTextMessage(int count)
    {
        try
        {
            var text = Encoding.UTF8.GetString(_buffer, 0, count);
            var json = JsonDocument.Parse(text).RootElement;

            if (json.TryGetProperty("language", out var lang))
                _language = lang.GetString() ?? _language;

            if (json.TryGetProperty("type", out var type) &&
                type.GetString()?.Equals("end", StringComparison.OrdinalIgnoreCase) == true)
            {
                _endRequested = true;
                _audioChannel.Writer.TryComplete();
            }
        }
        catch (Exception exception)
        {
            // ignore malformed JSON
        }
    }

    private async Task HandleBinaryMessageAsync(WebSocketReceiveResult first, CancellationToken cancellationToken)
    {
        await using var ms = new MemoryStream();
        ms.Write(_buffer, 0, first.Count);

        var result = first;

        while (!result.EndOfMessage)
        {
            result = await _socket.ReceiveAsync(_buffer, cancellationToken);
            ms.Write(_buffer, 0, result.Count);
        }

        // enqueue full WAV file for transcription
        await _audioChannel.Writer.WriteAsync(ms.ToArray(), cancellationToken);
    }

    private async Task BackgroundTranscribeAsync(CancellationToken cancellationToken)
    {
        await foreach (var chunk in _audioChannel.Reader.ReadAllAsync(cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await using var wavStream = new MemoryStream(chunk);

            var transcript = await _whisper.TranscribeWavAsync(wavStream, _language, cancellationToken);

            if (string.IsNullOrWhiteSpace(transcript))
            {
                continue;
            }

            var message = JsonSerializer.Serialize(new { type = "transcript", text = transcript });

            await _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken);
        }
    }
}
