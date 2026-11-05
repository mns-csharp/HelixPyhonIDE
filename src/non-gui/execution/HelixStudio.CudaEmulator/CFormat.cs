namespace HelixPythonNamespace.CudaEmulator
{
    using System.Globalization;

    /// <summary>
    /// Best-effort approximation of C printf numeric formatting (%g and friends)
    /// used by Value.ToString and the emulated printf. Not bit-exact with libc, but
    /// close enough for emulator output; integer / buffer results (the compute path)
    /// are exact.
    /// </summary>
    internal static class CFormat
    {
        /// <summary>Approximates C's "%g": ~6 significant digits, trailing zeros trimmed, lowercase exponent.</summary>
        public static string G(double d)
        {
            if (double.IsNaN(d)) return "nan";
            if (double.IsPositiveInfinity(d)) return "inf";
            if (double.IsNegativeInfinity(d)) return "-inf";
            string s = d.ToString("G6", CultureInfo.InvariantCulture);
            if (s.IndexOf('E') >= 0) s = s.Replace("E", "e");
            return s;
        }
    }
}
