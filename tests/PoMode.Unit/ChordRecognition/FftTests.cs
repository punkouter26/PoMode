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

    [Fact]
    public void A_constant_signal_has_all_its_energy_at_dc()
    {
        var buffer = Enumerable.Repeat(new Complex(1, 0), 256).ToArray();

        Fft.Transform(buffer);

        Assert.Equal(256.0, buffer[0].Magnitude, precision: 3);
        for (var i = 1; i < 256; i++)
        {
            Assert.True(buffer[i].Magnitude < 1e-6, $"bin {i} had {buffer[i].Magnitude}");
        }
    }

    [Fact]
    public void Non_power_of_two_lengths_are_rejected()
        => Assert.Throws<ArgumentException>(() => Fft.Transform(new Complex[100]));
}
