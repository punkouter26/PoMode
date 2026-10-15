using PoMode.API.Features.Separation;
using Xunit;

namespace PoMode.Unit.Audio;

/// <summary>
/// The server's MDX-Net demix, without the model: the mixed-radix FFT against a plain DFT, and the
/// STFT and ISTFT together returning a tone they were given, which is what a model that passes its
/// input through would hear back. A bin error or a window mismatch shows up as a tone that comes back
/// changed.
/// </summary>
public class MdxDemixTests
{
    [Fact]
    public void The_fft_matches_a_plain_dft_and_the_stft_pair_returns_a_tone_unchanged()
    {
        // 60 = 4 x 3 x 5: every radix the 7680-point transform uses.
        const int n = 60;
        var re = new double[n];
        var im = new double[n];
        var random = new Random(3);
        for (var i = 0; i < n; i++)
        {
            (re[i], im[i]) = (random.NextDouble() - 0.5, random.NextDouble() - 0.5);
        }
        var (inRe, inIm) = ((double[])re.Clone(), (double[])im.Clone());
        new MdxDemix.MixedRadixFft(n).Forward(re, im);
        for (var k = 0; k < n; k++)
        {
            double sumRe = 0, sumIm = 0;
            for (var t = 0; t < n; t++)
            {
                var angle = -2 * Math.PI * k * t / n;
                sumRe += (inRe[t] * Math.Cos(angle)) - (inIm[t] * Math.Sin(angle));
                sumIm += (inRe[t] * Math.Sin(angle)) + (inIm[t] * Math.Cos(angle));
            }
            Assert.Equal(sumRe, re[k], 9);
            Assert.Equal(sumIm, im[k], 9);
        }

        // A 1 kHz tone, left and right at different levels, through one chunk's STFT and back.
        var length = MdxDemix.Hop * (MdxDemix.DimT - 1);
        var left = new float[length];
        var right = new float[length];
        for (var i = 0; i < length; i++)
        {
            left[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 1000 * i / MdxDemix.SampleRate);
            right[i] = 0.25f * (float)Math.Sin(2 * Math.PI * 1000 * i / MdxDemix.SampleRate);
        }
        var stft = new MdxDemix.Stft();
        var (backLeft, backRight) = stft.Inverse(stft.Forward(left, right));
        for (var i = MdxDemix.NFft; i < length - MdxDemix.NFft; i += 97)
        {
            Assert.Equal(left[i], backLeft[i], 3);
            Assert.Equal(right[i], backRight[i], 3);
        }
    }
}
