namespace Ail.Simulator;

/// <summary>Seeded sampling helpers. Not bit-compatible with NumPy; statistically equivalent.</summary>
public sealed class Rng
{
    private readonly Random _r;
    private double? _spare;

    public Rng(int seed) => _r = new Random(seed);

    public double NextDouble() => _r.NextDouble();

    public double Gaussian()
    {
        if (_spare is { } s) { _spare = null; return s; }
        double u, v, q;
        do
        {
            u = 2 * _r.NextDouble() - 1;
            v = 2 * _r.NextDouble() - 1;
            q = u * u + v * v;
        } while (q is >= 1 or 0);
        var f = Math.Sqrt(-2 * Math.Log(q) / q);
        _spare = v * f;
        return u * f;
    }

    public int Binomial(int n, double p)
    {
        if (n <= 0 || p <= 0) return 0;
        if (p >= 1) return n;
        var flip = p > 0.5;
        if (flip) p = 1 - p;
        int k;
        if (n < 40)
        {
            k = 0;
            for (var i = 0; i < n; i++) if (_r.NextDouble() < p) k++;
        }
        else if (n * p < 30)
        {
            // BINV inversion: exact for small means
            var q = 1 - p;
            var s = p / q;
            var a = (n + 1) * s;
            var r = Math.Pow(q, n);
            var u = _r.NextDouble();
            k = 0;
            while (u > r && k < n)
            {
                u -= r;
                k++;
                r *= a / k - s;
            }
        }
        else
        {
            var q = 1 - p;
            k = (int)Math.Round(n * p + Math.Sqrt(n * p * q) * Gaussian());
            k = Math.Clamp(k, 0, n);
        }
        return flip ? n - k : k;
    }

    public double ChiSquare(double dof)
    {
        if (dof <= 30)
        {
            double sum = 0;
            for (var i = 0; i < (int)dof; i++) { var g = Gaussian(); sum += g * g; }
            return sum;
        }
        // Wilson-Hilferty cube-root approximation
        var t = 1 - 2 / (9 * dof) + Gaussian() * Math.Sqrt(2 / (9 * dof));
        return dof * t * t * t;
    }

    public int Choice(double[] cumulative)
    {
        var u = _r.NextDouble();
        for (var i = 0; i < cumulative.Length; i++) if (u < cumulative[i]) return i;
        return cumulative.Length - 1;
    }

    public double[] LognormalFactors(int size, double spread)
    {
        var f = new double[size];
        for (var i = 0; i < size; i++) f[i] = spread <= 0 ? 1.0 : Math.Exp(Gaussian() * spread - spread * spread / 2);
        return f;
    }
}
