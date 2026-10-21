using Windows.Storage.Streams;

namespace DynamicIsland.Services;

/// <summary>Reads a WinRT stream reference (artwork, app logo) into a byte array with a size cap.</summary>
internal static class StreamBytes
{
    public static async Task<byte[]?> ReadAsync(IRandomAccessStreamReference? reference, ulong maxBytes)
    {
        if (reference is null)
            return null;

        using var stream = await reference.OpenReadAsync();
        if (stream.Size == 0 || stream.Size > maxBytes)
            return null;

        using var reader = new DataReader(stream);
        var loaded = await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[loaded];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
