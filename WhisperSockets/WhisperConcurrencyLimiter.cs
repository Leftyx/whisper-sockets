namespace WhisperSockets;

public sealed class WhisperConcurrencyLimiter : IDisposable, IAsyncDisposable
{
   private readonly SemaphoreSlim _semaphore;

   public WhisperConcurrencyLimiter(int maxConcurrent)
       => _semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);

   public async ValueTask<Lease> AcquireAsync(CancellationToken ct = default)
   {
      await _semaphore.WaitAsync(ct).ConfigureAwait(false);
      return new Lease(_semaphore);
   }

   public void Dispose() => _semaphore.Dispose();
   public ValueTask DisposeAsync() { _semaphore.Dispose(); return ValueTask.CompletedTask; }

   public readonly struct Lease : IDisposable, IAsyncDisposable
   {
      private readonly SemaphoreSlim? _sem;
      public Lease(SemaphoreSlim sem) => _sem = sem;
      public void Dispose() => _sem?.Release();
      public ValueTask DisposeAsync() { _sem?.Release(); return ValueTask.CompletedTask; }
   }
}
