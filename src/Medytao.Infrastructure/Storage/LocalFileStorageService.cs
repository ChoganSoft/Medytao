using Medytao.Domain.Interfaces;

namespace Medytao.Infrastructure.Storage;

/// <summary>
/// Przechowuje pliki w lokalnym folderze na dysku i udostępnia je przez endpoint API.
/// W środowisku developerskim wszystko działa bez zewnętrznych zależności.
/// </summary>
public class LocalFileStorageService(string rootPath, string publicUrlPrefix) : IStorageService
{
    public async Task<string> UploadAsync(Stream stream, string fileName, string contentType, CancellationToken ct = default)
    {
        Directory.CreateDirectory(rootPath);

        var subfolder = Guid.NewGuid().ToString("N");
        var fullDir = Path.Combine(rootPath, subfolder);
        Directory.CreateDirectory(fullDir);

        // Zabezpieczenie przed path traversal - bierzemy tylko nazwę pliku
        var safeName = Path.GetFileName(fileName);
        var fullPath = Path.Combine(fullDir, safeName);

        await using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
        await stream.CopyToAsync(fileStream, ct);

        // blobKey to relatywna ścieżka używana potem do download/URL
        return $"{subfolder}/{safeName}";
    }

    public Task<Stream> DownloadAsync(string blobKey, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(blobKey);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Asset not found: {blobKey}");

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string blobKey, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(blobKey);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);

            // Usuń pusty folder (każdy plik leży w swoim GUID-owym folderze)
            var dir = Path.GetDirectoryName(fullPath);
            if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        return Task.CompletedTask;
    }

    public string GetPublicUrl(string blobKey) =>
        $"{publicUrlPrefix.TrimEnd('/')}/{blobKey}";

    private string ResolvePath(string blobKey)
    {
        // Zabezpieczenie przed "../etc/passwd"
        var combined = Path.GetFullPath(Path.Combine(rootPath, blobKey));
        var rootFull = Path.GetFullPath(rootPath);
        if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Invalid asset path.");
        return combined;
    }
}
