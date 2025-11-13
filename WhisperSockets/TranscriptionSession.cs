namespace WhisperSockets;

using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

internal sealed class TranscriptionSession : IAsyncDisposable
{
   // Cache static JSON serializer options to avoid allocation on every serialize call
   private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
   {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      WriteIndented = false
   };

   private readonly WebSocket _socket;
   private readonly WhisperFactoryWrapper _whisper;
   private readonly byte[] _buffer = new byte[64 * 1024];
   private readonly Channel<MemoryStream> _audioChannel = Channel.CreateBounded<MemoryStream>(new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
   private string _language = "auto";
   private bool _endRequested;
   private bool _disposed;

   public TranscriptionSession(WebSocket socket, WhisperFactoryWrapper whisper)
   {
      _socket = socket;
      _whisper = whisper;
   }

   public async Task RunAsync(CancellationToken cancellationToken)
   {
      var transcriptionTask = BackgroundTranscribeAsync(cancellationToken);

      Exception? caught = null;
      try
      {
         while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
         {
            var result = await _socket.ReceiveAsync(_buffer, cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
               break;
            }

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
               await HandleBinaryMessageAsync(result, cancellationToken).ConfigureAwait(false);
            }
         }
      }
      catch (Exception ex)
      {
         // single catch to simplify flow; handle known cases specially
         caught = ex;
         if (ex is OperationCanceledException)
         {
            Console.WriteLine("[Session] Cancelled.");
         }
         else if (ex is WebSocketException wsex)
         {
            Console.WriteLine($"[Session] WebSocket error: {wsex.Message}");
         }
         else
         {
            Console.WriteLine($"[Session] Unexpected error: {ex}");
            try { await SendErrorAsync(ex.Message, cancellationToken).ConfigureAwait(false); } catch { }
         }
      }

      // cleanup: signal no more writes and wait for background task to finish
      _audioChannel.Writer.TryComplete();

      // Wait for the background task to finish but do not rethrow its exceptions here.
      await Task.WhenAny(transcriptionTask).ConfigureAwait(false);
      if (transcriptionTask.IsFaulted)
      {
         // observe exception to avoid unobserved exception telemetry
         Console.WriteLine($"[Session] Transcription task failed: {transcriptionTask.Exception?.Flatten().InnerException}");
      }

      // Only attempt CloseAsync when the socket is in a valid state for closing.
      if (_socket.State is WebSocketState.Open or
          WebSocketState.CloseReceived or
          WebSocketState.CloseSent)
      {
         await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "session end", CancellationToken.None).ConfigureAwait(false);
      }
   }

   private async Task SendErrorAsync(string message, CancellationToken cancellationToken)
   {
      if (_socket.State != WebSocketState.Open)
      {
         return;
      }

      var err = JsonSerializer.Serialize(new { type = "error", message }, JsonOptions);
      byte[] bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(err));
      try
      {
         int bytesWritten = Encoding.UTF8.GetBytes(err, bytes);
         await _socket.SendAsync(
             new ArraySegment<byte>(bytes, 0, bytesWritten),
             WebSocketMessageType.Text,
             true,
             cancellationToken).ConfigureAwait(false);
      }
      finally
      {
         ArrayPool<byte>.Shared.Return(bytes);
      }
   }

   private void HandleTextMessage(int count)
   {
      try
      {
         // Parse JSON directly from the UTF-8 buffer to avoid an intermediate string allocation.
         using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(_buffer, 0, count));
         var json = doc.RootElement;

         if (json.TryGetProperty("language", out var lang))
         {
            _language = lang.GetString() ?? _language;
         }

         if (json.TryGetProperty("type", out var type) &&
             type.GetString()?.Equals("end", StringComparison.OrdinalIgnoreCase) == true)
         {
            _endRequested = true;
            _audioChannel.Writer.TryComplete();
         }
      }
      catch (Exception)
      {
         // ignore malformed JSON
      }
   }

   private async Task HandleBinaryMessageAsync(WebSocketReceiveResult first, CancellationToken cancellationToken)
   {
      // Allocate a MemoryStream and write the incoming binary frames into it. Ownership is passed to the channel.
      var ms = new MemoryStream();
      ms.Write(_buffer, 0, first.Count);

      var result = first;

      while (!result.EndOfMessage)
      {
         result = await _socket.ReceiveAsync(_buffer, cancellationToken).ConfigureAwait(false);
         ms.Write(_buffer, 0, result.Count);
      }

      // rewind before sending to consumer
      ms.Position = 0;

      // enqueue full WAV stream for transcription (no ToArray copy)
      await _audioChannel.Writer.WriteAsync(ms, cancellationToken).ConfigureAwait(false);
      // ownership transferred to channel consumer on success
   }

   private async Task BackgroundTranscribeAsync(CancellationToken cancellationToken)
   {
      // enumerate the channel without passing the request's cancellation token so that
      // the reader completes when the writer completes instead of throwing.
      await foreach (var wavStream in _audioChannel.Reader.ReadAllAsync().ConfigureAwait(false))
      {
         if (cancellationToken.IsCancellationRequested)
         {
            wavStream.Dispose();
            break;
         }

         try
         {
            using (wavStream)
            {
               var transcript = await _whisper.TranscribeWavAsync(wavStream, _language, cancellationToken).ConfigureAwait(false);

               if (string.IsNullOrWhiteSpace(transcript))
               {
                  continue;
               }

               Console.Write("Transcribing: {0}", transcript);

               var message = JsonSerializer.Serialize(new { type = "transcript", text = transcript }, JsonOptions);

               byte[] bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(message));
               try
               {
                  int bytesWritten = Encoding.UTF8.GetBytes(message, bytes);

                  if (_socket.State == WebSocketState.Open)
                  {
                     await _socket.SendAsync(
                         new ArraySegment<byte>(bytes, 0, bytesWritten),
                         WebSocketMessageType.Text,
                         true,
                         cancellationToken).ConfigureAwait(false);
                  }
               }
               finally
               {
                  ArrayPool<byte>.Shared.Return(bytes);
               }
            }
         }
         catch (OperationCanceledException)
         {
            // stop immediately on cancellation
            break;
         }
         catch (Exception ex)
         {
            // log and attempt to notify client
            Console.WriteLine($"[Transcribe] Error: {ex}");
            if (_socket.State == WebSocketState.Open)
            {
               try
               {
                  await SendErrorAsync("Transcription error", cancellationToken).ConfigureAwait(false);
               }
               catch { }
            }
         }
      }
   }

   public async ValueTask DisposeAsync()
   {
      if (_disposed)
      {
         return;
      }

      _disposed = true;

      // Complete the channel writer to signal no more data will be written
      _audioChannel.Writer.TryComplete();

      // Drain any remaining items in the channel and dispose them
      await foreach (var item in _audioChannel.Reader.ReadAllAsync().ConfigureAwait(false))
      {
         item?.Dispose();
      }

      _socket?.Dispose();
   }
}
