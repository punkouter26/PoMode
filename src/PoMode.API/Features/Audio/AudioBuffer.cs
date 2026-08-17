namespace PoMode.API.Features.Audio;

/// <summary>Interleaved, normalised (-1..1) PCM.</summary>
public sealed record AudioBuffer(float[] Samples, int SampleRate, int Channels)
{
    public double DurationSeconds => Samples.Length / (double)(SampleRate * Math.Max(Channels, 1));
}
