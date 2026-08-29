using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Sancho.Console.Transcription;

/// <summary>
/// Connects to OpenAI's realtime WebSocket API and transcribes
/// PCM16 24kHz mono audio via gpt-realtime-whisper. When the
/// connection drops it reconnects with exponential backoff.
/// </summary>
public sealed class RealtimeTranscriptionService(IOptions<TranscriptionOptions> options)
    : ITranscriptionService
{
    private const string RealtimeModel = "gpt-realtime-2.1";
    private const string TranscriptionModel = "gpt-realtime-whisper";
    private const int WebSocketBufferSize = 8192;
    private const int HeaderBufferSize = 4096;
    private const string DeltaEventType = "conversation.item.input_audio_transcription.delta";
    private const string CompletedEventType = "conversation.item.input_audio_transcription.completed";
    private const string ErrorEventType = "error";
    private const int MaxReconnectAttempts = 5;
    private static readonly TimeSpan[] BackoffDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
    ];
    private static readonly Uri RealtimeUri =
        new($"wss://api.openai.com/v1/realtime?model={RealtimeModel}");

    private readonly string _apiKey = options.Value.ApiKey;

    /// <summary>
    /// Reads PCM16 chunks from <paramref name="audioInput"/>, sends them to OpenAI,
    /// and yields transcription events as they arrive.
    /// </summary>
    /// <remarks>
    /// When the connection drops (or the initial connect fails), a
    /// <see cref="TranscriptionEvent.Reconnecting"/> event is emitted and the
    /// connection is retried with exponential backoff — audio produced during the
    /// outage keeps accumulating in <paramref name="audioInput"/> and is sent once
    /// the connection is back. A successful connection resets the retry policy.
    /// After <see cref="MaxReconnectAttempts"/> consecutive failures a single
    /// <see cref="TranscriptionEvent.Failed"/> event is emitted and the stream ends.
    /// A <see cref="TranscriptionEvent.Connected"/> event is emitted after each
    /// successful connection so the UI can show the live state.
    /// </remarks>
    public async IAsyncEnumerable<TranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<byte[]> audioInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var failures = 0;

        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");

            string? dropReason;
            try
            {
                await ws.ConnectAsync(RealtimeUri, ct);
                await ConfigureSessionAsync(ws, ct);
                dropReason = null;
            }
            catch (OperationCanceledException) { yield break; }
            catch (Exception ex)
            {
                dropReason = DescribeStartupFailure(ex);
            }

            if (dropReason is null)
            {
                failures = 0; // connected — restart the retry policy from zero
                yield return new TranscriptionEvent.Connected();

                var audioDone = new TaskCompletionSource();
                var feedTask = FeedAudioAsync(ws, audioInput, audioDone, ct);

                var buffer = new byte[WebSocketBufferSize];
                using var receiveStream = new MemoryStream();

                try
                {
                    while (!ct.IsCancellationRequested && dropReason is null)
                    {
                        receiveStream.SetLength(0);

                        TranscriptionEvent? evt = null;
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
                                dropReason = "the server closed the connection";
                                break;
                            }

                            receiveStream.Position = 0;
                            var parsed = ParseTranscriptionEvent(receiveStream);
                            evt = parsed.Event;
                            if (parsed.EndReason is not null)
                            {
                                dropReason = parsed.EndReason;
                                break;
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            dropReason = DescribeStreamFailure(ex);
                            break;
                        }

                        if (evt is not null)
                            yield return evt;
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
            }

            if (dropReason is null)
                yield break; // cancelled — stop without retrying

            // ── Connection lost — retry or give up ──
            failures++;
            if (failures > MaxReconnectAttempts)
            {
                yield return new TranscriptionEvent.Failed(
                    $"Transcription failed after {MaxReconnectAttempts} reconnect attempts — last error: {dropReason}");
                yield break;
            }

            yield return new TranscriptionEvent.Reconnecting(
                $"transcription disconnected ({dropReason}) — reconnecting in {BackoffDelays[failures - 1].TotalSeconds:0}s… attempt {failures}/{MaxReconnectAttempts}");

            try
            {
                await Task.Delay(BackoffDelays[failures - 1], ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Parses a transcription event from a JSON WebSocket message.
    /// Returns the text event to yield (or null if nothing to emit) and an
    /// end reason that is non-null when the receive loop should break.
    /// </summary>
    private static (TranscriptionEvent? Event, string? EndReason) ParseTranscriptionEvent(MemoryStream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;

        if (type == DeltaEventType && root.TryGetProperty("delta", out var delta))
        {
            var text = delta.GetString();
            return string.IsNullOrWhiteSpace(text)
                ? (null, null)
                : (new TranscriptionEvent.Delta(text), null);
        }

        if (type == CompletedEventType && root.TryGetProperty("transcript", out var transcript))
        {
            var text = transcript.GetString() ?? "";
            return string.IsNullOrWhiteSpace(text)
                ? (null, null)
                : (new TranscriptionEvent.Completed(text), null);
        }

        if (type == ErrorEventType)
        {
            var err = root.TryGetProperty("error", out var e)
                ? ExtractErrorMessage(e) : "(no details)";
            return (null, $"the server reported an error: {err}");
        }

        return (null, null);
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

    /// <summary>Maps a connect/session-config failure to a user-facing reason.</summary>
    private static string DescribeStartupFailure(Exception ex) => ex switch
    {
        WebSocketException => $"could not connect to the OpenAI transcription service: {ex.Message}",
        InvalidOperationException => $"OpenAI rejected the transcription session: {ex.Message}",
        _ => $"transcription could not start: {ex.Message}",
    };

    /// <summary>Maps a mid-stream failure to a user-facing reason.</summary>
    private static string DescribeStreamFailure(Exception ex) => ex switch
    {
        WebSocketException => $"the stream dropped: {ex.Message}",
        JsonException => $"malformed transcription data: {ex.Message}",
        _ => $"the stream failed: {ex.Message}",
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
