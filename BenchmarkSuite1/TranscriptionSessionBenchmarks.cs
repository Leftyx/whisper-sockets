using BenchmarkDotNet.Attributes;
using System;
using System.Text;
using System.Text.Json;

namespace BenchmarkSuiteWhisper;

[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class TranscriptionSessionBenchmarks
{
   private const string TestTranscript = "The quick brown fox jumps over the lazy dog. This is a test transcription message.";
   private const string TestLanguage = "en";
   private const string TestErrorMessage = "An error occurred during transcription";

   [Benchmark(Description = "JSON Serialize Transcript Message (Current)")]
   public void SerializeTranscriptMessage()
   {
      var message = JsonSerializer.Serialize(new { type = "transcript", text = TestTranscript });
      var bytes = Encoding.UTF8.GetBytes(message);
   }

   [Benchmark(Description = "JSON Serialize Error Message (Current)")]
   public void SerializeErrorMessage()
   {
      var message = JsonSerializer.Serialize(new { type = "error", message = TestErrorMessage });
      var bytes = Encoding.UTF8.GetBytes(message);
   }

   [Benchmark(Description = "Multiple Serialize/Encode Operations (Throughput)")]
   public void MultipleSerializeOperations()
   {
      for (int i = 0; i < 100; i++)
      {
         var message = JsonSerializer.Serialize(new { type = "transcript", text = TestTranscript });
         var bytes = Encoding.UTF8.GetBytes(message);
      }
   }

   [Benchmark(Description = "Parse JSON from Buffer (Current)")]
   public void ParseJsonFromBuffer()
   {
      var buffer = Encoding.UTF8.GetBytes("{\"type\": \"end\", \"language\": \"en\"}");
      using var doc = JsonDocument.Parse(buffer.AsMemory());
      var json = doc.RootElement;

      if (json.TryGetProperty("type", out var type))
      {
         var typeStr = type.GetString();
      }
   }
}
