using System.Runtime.InteropServices;
using Whisper.net.Ggml;

namespace WhisperSockets;

public class Program
{
   private const GgmlType _modelType = GgmlType.Medium;
   private const string _modelFileName = $"ggml-{nameof(GgmlType.Medium)}.bin";

   public static async Task Main(string[] args)
   {
      var builder = WebApplication.CreateBuilder(args);

      var modelPath = Path.Combine(AppContext.BaseDirectory, _modelFileName);

      if (!File.Exists(modelPath))
      {
         Console.WriteLine($"Downloading {_modelFileName} ...");
         using var model = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(_modelType);
         using var fs = File.OpenWrite(modelPath);
         await model.CopyToAsync(fs);
      }

      // limit CPU load
      builder.Services.AddSingleton(new WhisperConcurrencyLimiter(maxConcurrent: 2));
      builder.Services.AddSingleton(provider =>
      {
         var limiter = provider.GetRequiredService<WhisperConcurrencyLimiter>();
         return new WhisperFactoryWrapper(modelPath, limiter);
      });
      builder.Services.AddSingleton<TranscriptionWebSocketHandler>();

      var app = builder.Build();

      var webSocketOptions = new WebSocketOptions
      {
         KeepAliveInterval = TimeSpan.FromMinutes(10)
      };

      webSocketOptions.AllowedOrigins.Add("http://localhost:5173");

      app.UseWebSockets();

      app.Map("/transcribe", async (HttpContext ctx, TranscriptionWebSocketHandler handler) => await handler.HandleAsync(ctx));

      Console.WriteLine($"OSDescription: {RuntimeInformation.OSDescription}");
      Console.WriteLine($"OSArchitecture: {RuntimeInformation.OSArchitecture}");
      Console.WriteLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
      Console.WriteLine($"FrameworkDescription: {RuntimeInformation.FrameworkDescription}");

      await app.RunAsync();
   }
}
