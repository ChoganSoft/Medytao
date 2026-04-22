using Microsoft.AspNetCore.SignalR.Client;
using Medytao.Shared.Models;

namespace Medytao.Web.Services;

public class PreviewHubService : IAsyncDisposable
{
    private HubConnection? _hub;
    public event Action<LayerDto>? LayerUpdated;

    public async Task ConnectAsync(string hubUrl, string accessToken)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(hubUrl, opts => opts.AccessTokenProvider = () => Task.FromResult<string?>(accessToken))
            .WithAutomaticReconnect()
            .Build();

        _hub.On<LayerDto>("LayerUpdated", layer => LayerUpdated?.Invoke(layer));

        await _hub.StartAsync();
    }

    public Task JoinMeditationAsync(Guid meditationId) =>
        _hub?.InvokeAsync("JoinMeditation", meditationId.ToString()) ?? Task.CompletedTask;

    public Task LeaveMeditationAsync(Guid meditationId) =>
        _hub?.InvokeAsync("LeaveMeditation", meditationId.ToString()) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
            await _hub.DisposeAsync();
    }
}
