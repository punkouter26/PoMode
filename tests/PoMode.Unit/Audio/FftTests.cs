using System.Numerics;
using PoMode.API.Audio;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public class FftTests
{
    /// <summary>A non-power-of-two length would corrupt the transform rather than fail, so the guard is checked beside the transform itself.</summary>
    [Fact]
    public void A_pure_tone_peaks_in_its_bin_and_non_power_of_two_lengths_are_refused()
    {
        // A pure tone peaks in the expected bin
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

        // Non power of two lengths are rejected
        {
            Assert.Throws<ArgumentException>(() => Fft.Transform(new Complex[100]));
        }
    }
}
