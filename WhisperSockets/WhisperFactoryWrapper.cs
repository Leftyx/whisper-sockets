using System.Text;
using Whisper.net;

namespace WhisperSockets;

public sealed class WhisperFactoryWrapper : IAsyncDisposable
{
   private readonly WhisperFactory _factory;
   private readonly WhisperConcurrencyLimiter _limiter;

   public WhisperFactoryWrapper(string modelPath, WhisperConcurrencyLimiter limiter)
   {
      _factory = WhisperFactory.FromPath(modelPath);
      _limiter = limiter;
   }

   public async Task<string> TranscribeWavAsync(Stream wavStream, string language, CancellationToken cancellationToken)
   {
      await using var lease = await _limiter.AcquireAsync(cancellationToken);

      try
      {
         return await DoTranscribeWavAsync(wavStream, language, cancellationToken);
      }
      catch (OperationCanceledException)
      {
         // normal cancellation — just rethrow so outer token logic can stop cleanly
         throw;
      }
      catch (Exception ex)
      {
         // log, sanitize, and return a safe placeholder instead of blowing up the session
         Console.WriteLine($"[Whisper] Error: {ex.Message}");
         return string.Empty; // caller treats empty text as “skip”
      }
   }

   private async Task<string> DoTranscribeWavAsync(Stream wavStream, string language, CancellationToken cancellationToken)
   {
      using var processor = _factory.CreateBuilder()
                                    .WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language)
                                    .Build();

      var textBuilder = new StringBuilder();

      await foreach (var segment in processor.ProcessAsync(wavStream, cancellationToken))
      {
         textBuilder.Append(segment.Text);
      }

      return textBuilder.ToString().Trim();
   }

   public ValueTask DisposeAsync()
   {
      _factory.Dispose();

      return ValueTask.CompletedTask;
   }
}
