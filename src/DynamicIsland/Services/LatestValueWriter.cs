using System.Diagnostics;

namespace DynamicIsland.Services;

/// <summary>
/// Applies only the most recent value on the thread pool, one write at a time, so a slider being dragged leaves no backlog
/// of slow writes (WMI) and never applies an older value after a newer one.
/// </summary>
internal sealed class LatestValueWriter<T>(Action<T> write, string name) where T : struct
{
    private readonly object _gate = new();
    private T? _pending;
    private bool _running;

    public void Post(T value)
    {
        lock (_gate)
        {
            _pending = value;
            if (_running)
                return;
            _running = true;
        }
        Task.Run(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            T value;
            lock (_gate)
            {
                if (_pending is not { } next)
                {
                    _running = false;
                    return;
                }
                value = next;
                _pending = null;
            }

            try
            {
                write(value);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{name}] write failed: {ex.Message}");
            }
        }
    }
}
