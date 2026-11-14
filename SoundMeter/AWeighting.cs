using System.Numerics;

namespace SoundMeter;

/// <summary>
/// A-weighting filter (IEC 61672), the curve used by sound level meters for dB(A).
/// The analog curve has poles at 20.6 Hz (x2), 107.7 Hz, 737.9 Hz and 12194 Hz (x2) and four
/// zeros at 0 Hz. Each pole becomes a first-order digital section (bilinear transform with
/// pre-warping), and the whole chain is normalised to 0 dB at 1 kHz.
/// </summary>
sealed class AWeighting
{
    readonly Section[] _sections;
    readonly double _gain;

    public AWeighting(int sampleRate)
    {
        _sections =
        [
            new(sampleRate, 20.598997, highPass: true),
            new(sampleRate, 20.598997, highPass: true),
            new(sampleRate, 107.65265, highPass: true),
            new(sampleRate, 737.86223, highPass: true),
            new(sampleRate, 12194.217, highPass: false),
            new(sampleRate, 12194.217, highPass: false),
        ];

        // Normalise so that a 1 kHz tone passes unchanged.
        var z = Complex.FromPolarCoordinates(1, 2 * Math.PI * 1000 / sampleRate);
        double mag = 1;
        foreach (var s in _sections) mag *= s.Magnitude(z);
        _gain = 1 / mag;
    }

    public double Process(double x)
    {
        foreach (var s in _sections) x = s.Process(x);
        return x * _gain;
    }

    public void Reset()
    {
        foreach (var s in _sections) s.Reset();
    }

    sealed class Section
    {
        readonly double _b0, _b1, _a1;
        double _x1, _y1;

        public Section(int fs, double f, bool highPass)
        {
            double k = 2.0 * fs;
            double w = k * Math.Tan(Math.PI * f / fs); // pre-warped analog pole
            double norm = 1 / (k + w);
            _a1 = (w - k) * norm;
            if (highPass) { _b0 = k * norm; _b1 = -_b0; } // s / (s + w)
            else { _b0 = w * norm; _b1 = _b0; }           // w / (s + w)
        }

        public double Process(double x)
        {
            double y = _b0 * x + _b1 * _x1 - _a1 * _y1;
            _x1 = x;
            _y1 = y;
            return y;
        }

        public double Magnitude(Complex z)
        {
            var zi = 1 / z;
            return Complex.Abs((_b0 + _b1 * zi) / (1 + _a1 * zi));
        }

        public void Reset() => _x1 = _y1 = 0;
    }
}
