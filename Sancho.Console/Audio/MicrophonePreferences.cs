namespace Sancho.Console.Audio;

/// <summary>Explicit choices promote a device; automatic fallback never changes preference order.</summary>
internal static class MicrophonePreferences
{
    internal static string[] Promote(IReadOnlyList<string>? priority, string identity) =>
        new[] { identity }.Concat(priority ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).ToArray();

    internal static int Choose(IReadOnlyList<string> identities, IReadOnlyList<string>? priority,
        bool forceSelection, bool allowPrompt, Func<int> picker, out bool remember)
    {
        if (identities.Count == 0) throw new InvalidOperationException("No recording devices found. Plug in a microphone and try again.");
        remember = false;
        if (forceSelection && allowPrompt)
        {
            remember = true;
            return Validate(picker(), identities.Count);
        }
        foreach (var preferred in priority ?? [])
            for (var index = 0; index < identities.Count; index++)
                if (identities[index] == preferred) return index;
        if (priority is { Count: > 0 } || !allowPrompt) return 0;
        remember = true;
        return identities.Count == 1 ? 0 : Validate(picker(), identities.Count);
    }

    private static int Validate(int index, int count) => (uint)index < count
        ? index : throw new ArgumentOutOfRangeException(nameof(index));
}
