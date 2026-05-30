using SentimentAnalysis.Api.Options;
using Microsoft.Extensions.Options;

namespace SentimentAnalysis.Api.Services;

public sealed class FileStorageService(IOptions<StorageOptions> options, IWebHostEnvironment environment) : IFileStorageService
{
    public async Task<(string OriginalFileName, string StoredPath)> SaveUploadAsync(IFormFile file, Guid jobId, CancellationToken cancellationToken)
    {
        var uploadsPath = ResolveUploadsPath();
        Directory.CreateDirectory(uploadsPath);

        var originalName = Path.GetFileName(file.FileName);
        var storedPath = Path.Combine(uploadsPath, $"{jobId:N}.pdf");
        await using var output = File.Create(storedPath);
        await file.CopyToAsync(output, cancellationToken);
        return (originalName, storedPath);
    }

    private string ResolveUploadsPath()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.UploadsPath))
        {
            return options.Value.UploadsPath;
        }

        if (Directory.Exists("/home/data"))
        {
            return "/home/data/uploads";
        }

        return Path.Combine(environment.ContentRootPath, "App_Data", "uploads");
    }
}
