namespace Sancho.Console.Audio;

/// <summary>A discovered capture device and the factory that opens it.</summary>
public sealed record AudioSourceSelection(string Identity, string Description, Func<IAudioSource> CreateSource);
