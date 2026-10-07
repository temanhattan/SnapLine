namespace SnapLine.Services;

internal static class ClipboardWriteSuppression
{
    private static readonly object Gate = new();
    private static uint _sequence;
    private static DateTimeOffset _recordedAt;

    public static void Record(uint sequence)
    {
        lock (Gate)
        {
            _sequence = sequence;
            _recordedAt = DateTimeOffset.UtcNow;
        }
    }

    public static bool Consumes(uint sequence)
    {
        lock (Gate)
        {
            if (_sequence != sequence || DateTimeOffset.UtcNow - _recordedAt > TimeSpan.FromSeconds(5))
                return false;
            _sequence = 0;
            return true;
        }
    }
}
