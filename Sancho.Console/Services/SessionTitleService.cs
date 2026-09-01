using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sancho.Console.Transcription;

namespace Sancho.Console.Services;

/// <summary>
/// Figures out a short display name for a session by asking OpenAI's chat
/// completions API (cheap model), reusing the transcription API key. Unlike
/// a <c>claude -p</c> call, this never creates a Claude session file.
/// </summary>
public sealed class SessionTitleService(
    TranscriptionOptions transcriptionOptions,
    HttpClient httpClient,
    ILogger<SessionTitleService> logger)
{
    private const string TitleModel = "gpt-4o-mini";

    private readonly string _apiKey = transcriptionOptions.ApiKey;

    /// <summary>
    /// Returns a cleaned 2-6 word title for the first user message, or
    /// <c>null</c> when there is nothing to title or the request fails or
    /// times out.
    /// </summary>
    public async Task<string?> GenerateTitleAsync(string? userText)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            // No OpenAI key configured — recording is local, and titles are
            // the only remaining OpenAI call, so skip them quietly.
            logger.LogDebug("No OpenAI API key configured — skipping session title generation.");
            return null;
        }

        var prompt = BuildTitlePrompt(userText);
        if (prompt is null)
            return null;

        var requestJson = new JsonObject
        {
            ["model"] = TitleModel,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = prompt
            }),
            ["max_completion_tokens"] = 40
        }.ToJsonString();

        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

        try
        {
            using var response = await httpClient.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "OpenAI title request failed with status {Status}", (int)response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return Clean(text);
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or KeyNotFoundException
            or InvalidOperationException)
        {
            logger.LogDebug("Session title generation failed: {Message}", ex.Message);
            return null;
        }
    }

    private static string? BuildTitlePrompt(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("You name conversations so they can be found again in a session picker.");
        sb.AppendLine("Write a very short title (2-6 words) capturing the topic of this conversation.");
        sb.AppendLine("Reply with the title only — no quotes, no punctuation, no explanation.");
        sb.AppendLine();
        sb.AppendLine("User: " + userText);

        return sb.ToString();
    }

    /// <summary>Normalizes a raw title before it is saved.</summary>
    private static string? Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var cleaned = title.Trim().Trim('"', '\'', '“', '”', '‘', '’', '-', '.');
        cleaned = string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length == 0)
            return null;

        return cleaned.Substring(0, Math.Min(60, cleaned.Length));
    }
}
