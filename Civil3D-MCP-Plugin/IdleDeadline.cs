using System.Diagnostics;

namespace Civil3DMcpPlugin;

// Stopwatch timestamps keep host-idle expiry independent of wall-clock changes.
internal readonly struct IdleDeadline
{
  private readonly long _expiresAt;

  internal IdleDeadline(TimeSpan timeout, long? startedAt = null)
  {
    _expiresAt = (startedAt ?? Stopwatch.GetTimestamp()) +
      (long)(timeout.TotalSeconds * Stopwatch.Frequency);
  }

  internal bool IsExpired(long? now = null) => (now ?? Stopwatch.GetTimestamp()) >= _expiresAt;
}
