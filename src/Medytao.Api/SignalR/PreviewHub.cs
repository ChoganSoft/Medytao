using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Medytao.Api.SignalR;

/// <summary>
/// Clients join a meditation's group to receive live layer/track updates
/// as the composer makes changes — enabling real-time preview sync.
/// </summary>
[Authorize]
public class PreviewHub : Hub
{
    /// <summary>Join a meditation's real-time session.</summary>
    public async Task JoinMeditation(string meditationId) =>
        await Groups.AddToGroupAsync(Context.ConnectionId, meditationId);

    /// <summary>Leave a meditation's real-time session.</summary>
    public async Task LeaveMeditation(string meditationId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, meditationId);

    /// <summary>
    /// Broadcast a layer state update to all connected clients for this meditation.
    /// The API calls this after any layer/track mutation.
    /// </summary>
    public async Task LayerUpdated(string meditationId, object layerState) =>
        await Clients.OthersInGroup(meditationId).SendAsync("LayerUpdated", layerState);
}

/// <summary>
/// Service to push layer updates from API endpoints into the SignalR hub.
/// </summary>
public class PreviewNotifier(IHubContext<PreviewHub> hub)
{
    public Task NotifyLayerUpdated(Guid meditationId, object layerState) =>
        hub.Clients.Group(meditationId.ToString())
            .SendAsync("LayerUpdated", layerState);
}
