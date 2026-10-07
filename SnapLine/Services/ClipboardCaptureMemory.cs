namespace SnapLine.Services;

internal static class ClipboardCaptureMemory
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTimeOffset> Hashes = new(StringComparer.Ordinal);

    public static bool Contains(string hash, DateTimeOffset now)
    {
        lock (Gate)
        {
            RemoveExpired(now);
            return Hashes.ContainsKey(hash);
        }
    }

    public static void Remember(string hash, DateTimeOffset now)
    {
        lock (Gate)
        {
            RemoveExpired(now);
            Hashes[hash] = now;
        }
    }

    private static void RemoveExpired(DateTimeOffset now)
    {
        foreach (var key in Hashes.Where(pair => now - pair.Value > TimeSpan.FromMinutes(30))
                     .Select(pair => pair.Key).ToArray())
            Hashes.Remove(key);
    }
}
