using System.Numerics;
using PoMode.API.Features.ChordRecognition;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public class FftTests
{
    [Fact]
    public void A_pure_tone_peaks_in_the_expected_bin()
    {
        const int size = 1024;
        const int sampleRate = 8000;
        const double frequency = 1000.0; // bin = 1000 / (8000/1024) = 128
        var buffer = new Complex[size];
        for (var i = 0; i < size; i++)
        {
            buffer[i] = new Complex(Math.Sin(2 * Math.PI * frequency * i / sampleRate), 0);
        }

        Fft.Transform(buffer);

        var peak = 0;
        for (var i = 1; i < size / 2; i++)
        {
            if (buffer[i].Magnitude > buffer[peak].Magnitude) peak = i;
        }
        Assert.InRange(peak, 126, 130);
    }

    /// <summary>A silent guard: a non-power-of-two length would corrupt the transform rather
    /// than fail, and every chroma frame downstream would be quietly wrong.</summary>
    [Fact]
    public void Non_power_of_two_lengths_are_rejected()
        => Assert.Throws<ArgumentException>(() => Fft.Transform(new Complex[100]));
}
