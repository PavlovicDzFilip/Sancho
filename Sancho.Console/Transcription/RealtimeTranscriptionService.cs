using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Sancho.Console.Transcription;

/// <summary>
/// Connects to OpenAI's realtime WebSocket API and transcribes
/// PCM16 24kHz mono audio via gpt-realtime-whisper.
/// </summary>
public sealed class RealtimeTranscriptionService(IOptions<TranscriptionOptions> options)
{
    private const string RealtimeModel = "gpt-realtime-2.1";
    private const string TranscriptionModel = "gpt-realtime-whisper";
    private const int WebSocketBufferSize = 8192;
    private const int HeaderBufferSize = 4096;
    private const string DeltaEventType = "conversation.item.input_audio_transcription.delta";
    private const string CompletedEventType = "conversation.item.input_audio_transcription.completed";
    private const string ErrorEventType = "error";
    private static readonly Uri RealtimeUri =
        new($"wss://api.openai.com/v1/realtime?model={RealtimeModel}");

    private readonly string _apiKey = options.Value.ApiKey;

    /// <summary>
    /// Reads PCM16 chunks from <paramref name="audioInput"/>, sends them to OpenAI,
    /// and yields transcription events as they arrive.
    /// </summary>
    public async IAsyncEnumerable<TranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<byte[]> audioInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");

        string? startupError = null;
        try
        {
            await ws.ConnectAsync(RealtimeUri, ct);
            await ConfigureSessionAsync(ws, ct);
        }
        catch (OperationCanceledException) { yield break; }
        catch (Exception ex)
        {
            startupError = DescribeStartupFailure(ex);
        }

        if (startupError is not null)
        {
            yield return new TranscriptionEvent.Error(startupError);
            yield break;
        }

        var audioDone = new TaskCompletionSource();
        var feedTask = FeedAudioAsync(ws, audioInput, audioDone, ct);

        var buffer = new byte[WebSocketBufferSize];
        using var receiveStream = new MemoryStream();

        Exception? streamError = null;
        try
        {
            while (!ct.IsCancellationRequested && streamError is null)
            {
                receiveStream.SetLength(0);

                TranscriptionEvent? evt = null;
                var shouldBreak = false;
                try
                {
                    WebSocketReceiveResult? result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer, ct);
                        receiveStream.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        shouldBreak = true;
                    }
                    else
                    {
                        receiveStream.Position = 0;
                        var parsed = ParseTranscriptionEvent(receiveStream);
                        evt = parsed.Event;
                        shouldBreak = parsed.ShouldBreak;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    streamError = ex;
                    break;
                }

                if (evt is not null)
                    yield return evt;
                if (shouldBreak)
                    break;
            }
        }
        finally
        {
            audioDone.TrySetResult();
            await feedTask;
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                catch
                {
                    // nothing to do, we are closing the stream already
                }
            }
        }

        if (streamError is not null)
            yield return new TranscriptionEvent.Error(DescribeStreamFailure(streamError));
    }

    /// <summary>
    /// Parses a transcription event from a JSON WebSocket message.
    /// Returns the text to yield (or null if nothing to emit) and a flag
    /// indicating whether the receive loop should break.
    /// </summary>
    private static (TranscriptionEvent? Event, bool ShouldBreak) ParseTranscriptionEvent(MemoryStream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;

        if (type == DeltaEventType && root.TryGetProperty("delta", out var delta))
        {
            var text = delta.GetString();
            return string.IsNullOrWhiteSpace(text)
                ? (null, false)
                : (new TranscriptionEvent.Delta(text), false);
        }

        if (type == CompletedEventType && root.TryGetProperty("transcript", out var transcript))
        {
            var text = transcript.GetString() ?? "";
            return string.IsNullOrWhiteSpace(text)
                ? (null, false)
                : (new TranscriptionEvent.Completed(text), false);
        }

        if (type == ErrorEventType)
        {
            var err = root.TryGetProperty("error", out var e)
                ? ExtractErrorMessage(e) : "(no details)";
            return (new TranscriptionEvent.Error(err), true);
        }

        return (null, false);
    }

    /// <summary>
    /// Extracts the human-readable <c>message</c> from an OpenAI error object,
    /// falling back to the raw JSON when the shape is unexpected.
    /// </summary>
    private static string ExtractErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String)
            return message.GetString() ?? error.GetRawText();
        return error.GetRawText();
    }

    /// <summary>Maps a connect/session-config failure to a user-facing message.</summary>
    private static string DescribeStartupFailure(Exception ex) => ex switch
    {
        WebSocketException => $"Could not connect to the OpenAI transcription service: {ex.Message}",
        InvalidOperationException => $"OpenAI rejected the transcription session: {ex.Message}",
        _ => $"Transcription could not start: {ex.Message}",
    };

    /// <summary>Maps a mid-stream failure to a user-facing message.</summary>
    private static string DescribeStreamFailure(Exception ex) => ex switch
    {
        WebSocketException => $"Transcription stream disconnected: {ex.Message}",
        JsonException => $"Received malformed transcription data: {ex.Message}",
        _ => $"Transcription stream failed: {ex.Message}",
    };

    private static async Task FeedAudioAsync(
        ClientWebSocket ws,
        IAsyncEnumerable<byte[]> input,
        TaskCompletionSource audioDone,
        CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in input.WithCancellation(ct))
            {
                var base64 = Convert.ToBase64String(chunk);
                try
                {
                    await SendJsonAsync(ws, new { type = "input_audio_buffer.append", audio = base64 }, ct);
                }
                catch (Exception) when (ws.State != WebSocketState.Open)
                {
                    // WebSocket closed or aborted during send — stop feeding
                    break;
                }

                if (audioDone.Task.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task ConfigureSessionAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var config = BuildSessionConfig();
        await SendJsonAsync(ws, config, ct);
        await ExpectEventAsync(ws, "session.updated", ct);
    }

    /// <summary>Builds the session configuration object for the OpenAI Realtime API.</summary>
    private static object BuildSessionConfig()
    {
        return new
        {
            type = "session.update",
            session = new
            {
                type = "realtime",
                output_modalities = new[] { "text" },
                audio = new
                {
                    input = new
                    {
                        format = new { type = "audio/pcm", rate = 24000 },
                        turn_detection = new
                        {
                            type = "server_vad",
                            threshold = 0.5,
                            prefix_padding_ms = 300,
                            silence_duration_ms = 2000
                        },
                        transcription = new
                        {
                            model = TranscriptionModel,
                            language = "en"
                        }
                    }
                }
            }
        };
    }

    private static async Task SendJsonAsync(
        ClientWebSocket ws, object obj, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(obj);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task ExpectEventAsync(
        ClientWebSocket ws, string expectedType, CancellationToken ct)
    {
        var buffer = new byte[HeaderBufferSize];
        using var receiveStream = new MemoryStream();

        while (!ct.IsCancellationRequested)
        {
            receiveStream.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, ct);
                receiveStream.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);

            receiveStream.Position = 0;
            using var d = JsonDocument.Parse(receiveStream);
            var type = d.RootElement.TryGetProperty("type", out var tp) ? tp.GetString() : "";

            if (type == expectedType) return;
            if (type == ErrorEventType)
                throw new InvalidOperationException(
                    ExtractErrorMessage(d.RootElement.GetProperty("error")));
        }
    }
}
