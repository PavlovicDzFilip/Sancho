using Microsoft.Extensions.Logging;
using Sancho.Console.Config;

namespace Sancho.Console.Transcription;

/// <summary>
/// Downloads the local speech-to-text model files — the selected sherpa-onnx
/// whisper .en int8 size (see <see cref="WhisperModels"/>) plus the shared
/// silero VAD — into <c>~/.sancho/models/&lt;size-dir&gt;/</c>. The download
/// runs at startup (Program.cs, right after the size is resolved), before
/// capture or transcription begin.
/// Files already on disk are reused; downloads land in <c>.part</c> files and
/// are moved into place only when complete, so an interrupted download never
/// leaves a half-written model behind. Each size keeps its own directory, so
/// switching sizes only costs the new download.
/// </summary>
public sealed class LocalSttModels(ILogger<LocalSttModels> logger, WhisperModelSpec spec)
{
    /// <summary>The selected whisper size; the recognizer reads its file names.</summary>
    public WhisperModelSpec Spec { get; } = spec;

    /// <summary>The VAD model is shared by every whisper size.</summary>
    public const string SileroVadFileName = "silero_vad.onnx";

    private const string VadUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx";

    // The zipformer model predates the whisper engine (ADR-0003); its files
    // are no longer read, so a completed download cleans the stale directory.
    private const string SupersededModelDirName = "sherpa-onnx-zipformer-en-2023-06-26";

    // Dedicated client: the encoders can exceed the app-wide 45 s HttpClient
    // timeout on slow connections, so downloads get their own.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private (string File, string Url)[] Files =>
    [
        (Spec.EncoderFile, Spec.EncoderUrl),
        (Spec.DecoderFile, Spec.DecoderUrl),
        (Spec.TokensFile, Spec.TokensUrl),
        (SileroVadFileName, VadUrl),
    ];

    /// <summary>
    /// Returns the directory holding the model files, downloading them first
    /// if needed, or <c>null</c> when a download fails.
    /// </summary>
    public async Task<string?> EnsureDownloadedAsync(CancellationToken ct)
    {
        var dir = Path.Combine(SanchoPaths.ModelsDir, Spec.ModelDirName);
        Directory.CreateDirectory(dir);

        var files = Files;
        for (var i = 0; i < files.Length; i++)
        {
            var (file, url) = files[i];
            var path = Path.Combine(dir, file);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                continue;

            logger.LogInformation(
                "Downloading speech model {Index}/{Count}: {File}…", i + 1, files.Length, file);
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

        TryDeleteSupersededModel();
        return dir;
    }

    /// <summary>Removes the superseded streaming-model directory, if present.</summary>
    private static void TryDeleteSupersededModel()
    {
        try
        {
            var old = Path.Combine(SanchoPaths.ModelsDir, SupersededModelDirName);
            if (Directory.Exists(old))
                Directory.Delete(old, recursive: true);
        }
        catch (IOException)
        {
            // best effort — a stale directory is harmless, just disk space
        }
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
