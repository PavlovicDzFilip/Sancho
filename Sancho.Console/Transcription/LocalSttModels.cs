using Microsoft.Extensions.Logging;
using Sancho.Console.Config;

namespace Sancho.Console.Transcription;

/// <summary>
/// Downloads the local speech-to-text model files (sherpa-onnx streaming
/// zipformer, English, int8) into <c>~/.sancho/models/</c> on first use.
/// Files already on disk are reused; downloads land in <c>.part</c> files
/// and are moved into place only when complete, so an interrupted download
/// never leaves a half-written model behind.
/// </summary>
public sealed class LocalSttModels(ILogger<LocalSttModels> logger)
{
    public const string ModelDirName = "sherpa-onnx-streaming-zipformer-en-2023-06-26";

    private const string BaseUrl =
        "https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-en-2023-06-26/resolve/main/";

    // Dedicated client: the 67 MB encoder can exceed the app-wide 45 s
    // HttpClient timeout on slow connections, so downloads get their own.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private static readonly (string File, string Url)[] Files =
    [
        ("encoder-epoch-99-avg-1-chunk-16-left-128.int8.onnx",
         BaseUrl + "encoder-epoch-99-avg-1-chunk-16-left-128.int8.onnx"),
        ("decoder-epoch-99-avg-1-chunk-16-left-128.int8.onnx",
         BaseUrl + "decoder-epoch-99-avg-1-chunk-16-left-128.int8.onnx"),
        ("joiner-epoch-99-avg-1-chunk-16-left-128.int8.onnx",
         BaseUrl + "joiner-epoch-99-avg-1-chunk-16-left-128.int8.onnx"),
        ("tokens.txt", BaseUrl + "tokens.txt"),
    ];

    /// <summary>
    /// Returns the directory holding the model files, downloading them first
    /// if needed, or <c>null</c> when a download fails.
    /// </summary>
    public async Task<string?> EnsureDownloadedAsync(CancellationToken ct)
    {
        var dir = Path.Combine(SanchoPaths.ModelsDir, ModelDirName);
        Directory.CreateDirectory(dir);

        for (var i = 0; i < Files.Length; i++)
        {
            var (file, url) = Files[i];
            var path = Path.Combine(dir, file);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                continue;

            logger.LogInformation(
                "Downloading speech model {Index}/{Count}: {File}…", i + 1, Files.Length, file);
            var partPath = path + ".part";
            try
            {
                await using (var source = await Http.GetStreamAsync(url, ct))
                await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write))
                    await source.CopyToAsync(target, ct);
                File.Move(partPath, path, overwrite: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                TryDelete(partPath);
                if (ct.IsCancellationRequested)
                    throw;
                logger.LogError("Could not download {File}: {Message}", file, ex.Message);
                return null;
            }
        }

        return dir;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best effort — a stale .part file is harmless, it gets overwritten next run
        }
    }
}
