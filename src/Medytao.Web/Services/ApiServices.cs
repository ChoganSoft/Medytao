using System.Net.Http.Headers;
using System.Net.Http.Json;
using Blazored.LocalStorage;
using Medytao.Shared.Models;

namespace Medytao.Web.Services;

public class AuthService(HttpClient http, ILocalStorageService storage)
{
    private const string TokenKey = "auth_token";
    private bool _initialized;

    public async Task<bool> LoginAsync(string email, string password)
    {
        var response = await http.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        if (!response.IsSuccessStatusCode) return false;
        var token = await response.Content.ReadFromJsonAsync<AuthTokenDto>();
        if (token is null) return false;
        await storage.SetItemAsync(TokenKey, token);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        _initialized = true;
        return true;
    }

    public async Task<bool> RegisterAsync(string email, string displayName, string password)
    {
        var response = await http.PostAsJsonAsync("/api/v1/auth/register", new { email, displayName, password });
        if (!response.IsSuccessStatusCode) return false;
        var token = await response.Content.ReadFromJsonAsync<AuthTokenDto>();
        if (token is null) return false;
        await storage.SetItemAsync(TokenKey, token);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        _initialized = true;
        return true;
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        var token = await storage.GetItemAsync<AuthTokenDto>(TokenKey);
        if (token is not null && token.ExpiresAt > DateTimeOffset.UtcNow)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        _initialized = true;
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        var token = await storage.GetItemAsync<AuthTokenDto>(TokenKey);
        return token is not null && token.ExpiresAt > DateTimeOffset.UtcNow;
    }

    public async Task LogoutAsync()
    {
        await storage.RemoveItemAsync(TokenKey);
        http.DefaultRequestHeaders.Authorization = null;
        _initialized = false;
    }
}

public class MeditationService(HttpClient http)
{
    public Task<List<MeditationSummaryDto>?> GetAllAsync() =>
        http.GetFromJsonAsync<List<MeditationSummaryDto>>("/api/v1/meditations");

    public Task<MeditationDetailDto?> GetByIdAsync(Guid id) =>
        http.GetFromJsonAsync<MeditationDetailDto>($"/api/v1/meditations/{id}");

    // ProgramId jest teraz wymagane — backend rzuca 404, gdyby program nie
    // istniał. UI tworzy medytacje tylko z poziomu widoku konkretnego programu,
    // więc id zawsze ma sensowną wartość.
    public async Task<MeditationSummaryDto?> CreateAsync(Guid programId, string title, string? description = null)
    {
        var response = await http.PostAsJsonAsync("/api/v1/meditations", new { programId, title, description });
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<MeditationSummaryDto>() : null;
    }

    public Task PublishAsync(Guid id) =>
        http.PostAsync($"/api/v1/meditations/{id}/publish", null);

    public Task<HttpResponseMessage> DeleteAsync(Guid id) =>
        http.DeleteAsync($"/api/v1/meditations/{id}");

    public async Task<LayerDto?> UpdateLayerAsync(Guid layerId, float volume, bool muted)
    {
        var response = await http.PutAsJsonAsync($"/api/v1/layers/{layerId}", new { volume, muted });
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<LayerDto>() : null;
    }

    public async Task<TrackDto?> AddTrackAsync(Guid layerId, Guid assetId)
    {
        var response = await http.PostAsJsonAsync($"/api/v1/layers/{layerId}/tracks", new { assetId });
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<TrackDto>() : null;
    }

    public async Task<TrackDto?> UpdateTrackAsync(Guid trackId, Guid layerId,
        float volume, int loopCount, int fadeInMs, int fadeOutMs, int startOffsetMs, int crossfadeMs)
    {
        var response = await http.PutAsJsonAsync($"/api/v1/layers/{layerId}/tracks/{trackId}",
            new { volume, loopCount, fadeInMs, fadeOutMs, startOffsetMs, crossfadeMs });
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<TrackDto>() : null;
    }

    public Task RemoveTrackAsync(Guid layerId, Guid trackId) =>
        http.DeleteAsync($"/api/v1/layers/{layerId}/tracks/{trackId}");

    public Task ReorderTracksAsync(Guid layerId, IEnumerable<Guid> orderedIds) =>
        http.PutAsJsonAsync($"/api/v1/layers/{layerId}/tracks/reorder", new { orderedTrackIds = orderedIds });
}

public class ProgramService(HttpClient http)
{
    public Task<List<ProgramSummaryDto>?> GetAllAsync() =>
        http.GetFromJsonAsync<List<ProgramSummaryDto>>("/api/v1/programs");

    public Task<ProgramDetailDto?> GetByIdAsync(Guid id) =>
        http.GetFromJsonAsync<ProgramDetailDto>($"/api/v1/programs/{id}");

    public async Task<ProgramSummaryDto?> CreateAsync(string name, string? description = null)
    {
        var response = await http.PostAsJsonAsync("/api/v1/programs", new { name, description });
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ProgramSummaryDto>() : null;
    }

    public async Task<ProgramSummaryDto?> UpdateAsync(Guid id, string name, string? description)
    {
        var response = await http.PutAsJsonAsync($"/api/v1/programs/{id}", new { name, description });
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ProgramSummaryDto>() : null;
    }

    public Task<HttpResponseMessage> DeleteAsync(Guid id) =>
        http.DeleteAsync($"/api/v1/programs/{id}");
}

public class AssetService(HttpClient http)
{
    public Task<List<AssetDto>?> GetAllAsync() =>
        http.GetFromJsonAsync<List<AssetDto>>("/api/v1/assets");

    public async Task<AssetDto?> UploadAsync(Stream stream, string fileName, string contentType, string type, int? durationMs = null)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StreamContent(stream), "file", fileName);
        content.Add(new StringContent(type), "type");
        if (durationMs.HasValue) content.Add(new StringContent(durationMs.Value.ToString()), "durationMs");
        var response = await http.PostAsync("/api/v1/assets/upload", content);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<AssetDto>() : null;
    }

    public Task DeleteAsync(Guid id) => http.DeleteAsync($"/api/v1/assets/{id}");
}