using System.Numerics;

namespace PoMode.API.Features.ChordRecognition;

/// <summary>In-place iterative radix-2 Cooley-Tukey FFT.</summary>
public static class Fft
{
    /// <summary>Transforms <paramref name="buffer"/> in place. Length must be a power of two.</summary>
    public static void Transform(Complex[] buffer)
    {
        var n = buffer.Length;
        if (n == 0 || (n & (n - 1)) != 0)
        {
            throw new ArgumentException("FFT length must be a power of two.", nameof(buffer));
        }

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j &= ~bit;
            }
            j |= bit;
            if (i < j)
            {
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
            }
        }

        // Iterative butterflies.
        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var wLength = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < n; start += length)
            {
                var w = Complex.One;
                var half = length / 2;
                for (var k = 0; k < half; k++)
                {
                    var u = buffer[start + k];
                    var v = buffer[start + k + half] * w;
                    buffer[start + k] = u + v;
                    buffer[start + k + half] = u - v;
                    w *= wLength;
                }
            }
        }
    }
}
