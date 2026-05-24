using Crm.Models;

namespace Crm.Services;

/// <summary>
/// Runs a heartbeat-poll loop on the currently-viewed record. Posts a
/// heartbeat every 10 s so other users see us; fetches the current
/// viewer list every 4 s so we see them.
/// </summary>
public sealed class PresenceTracker : IAsyncDisposable
{
    private readonly ApiClient _api;
    private CancellationTokenSource? _cts;
    private string? _recordId;
    public PresenceEntry[] Viewers { get; private set; } = Array.Empty<PresenceEntry>();
    public event Action? OnChanged;

    public PresenceTracker(ApiClient api) { _api = api; }

    public void Start(string recordId)
    {
        if (_recordId == recordId && _cts != null) return;
        Stop();
        _recordId = recordId;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _recordId = null;
        Viewers = Array.Empty<PresenceEntry>();
        OnChanged?.Invoke();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var lastHeartbeat = 0L;
        var poll = new PeriodicTimer(TimeSpan.FromSeconds(4));
        while (await poll.WaitForNextTickAsync(ct))
        {
            var rid = _recordId;
            if (rid is null) break;
            // Heartbeat every 10 s. Poll list every tick (4 s).
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - lastHeartbeat > 10_000)
            {
                await _api.HeartbeatAsync(rid);
                lastHeartbeat = now;
            }
            var list = await _api.GetPresenceAsync(rid);
            Viewers = list;
            OnChanged?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await Task.CompletedTask;
    }
}
