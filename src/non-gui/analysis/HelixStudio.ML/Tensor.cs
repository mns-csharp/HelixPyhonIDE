namespace HelixPythonNamespace.ML
{
    using System;

    /// <summary>
    /// Single-precision contiguous CPU tensor. Row-major. Minimal API — just what
    /// the completion ranker model needs. No broadcasting, no GPU, no lazy graph.
    /// Port of the C++ <c>helix::ml::Tensor</c>.
    /// </summary>
    public sealed class Tensor
    {
        private readonly int[] _shape;
        private readonly float[] _data;

        public Tensor()
        {
            _shape = new int[0];
            _data = new float[0];
        }

        public Tensor(int[] shape)
        {
            _shape = (int[])shape.Clone();
            _data = new float[Product(_shape)];
        }

        public int[] Shape
        {
            get { return _shape; }
        }

        public int NumElements()
        {
            return Product(_shape);
        }

        public int Rank
        {
            get { return _shape.Length; }
        }

        public int Dim(int i)
        {
            return _shape[i];
        }

        /// <summary>Backing storage, row-major. Read and write directly.</summary>
        public float[] Data
        {
            get { return _data; }
        }

        public float this[int i]
        {
            get { return _data[i]; }
            set { _data[i] = value; }
        }

        public float At(int i)
        {
            return _data[i];
        }

        public void Fill(float v)
        {
            for (int i = 0; i < _data.Length; i++)
            {
                _data[i] = v;
            }
        }

        public void ZeroFill()
        {
            Array.Clear(_data, 0, _data.Length);
        }

        public static Tensor Zeros(int[] shape)
        {
            return new Tensor(shape);
        }

        public static Tensor Random(int[] shape, float stddev, Random rng)
        {
            Tensor t = new Tensor(shape);
            for (int i = 0; i < t._data.Length; i++)
            {
                t._data[i] = (float)(NextGaussian(rng) * stddev);
            }
            return t;
        }

        private static int Product(int[] shape)
        {
            int n = 1;
            for (int j = 0; j < shape.Length; j++)
            {
                if (shape[j] < 0)
                {
                    throw new ArgumentException("negative dimension");
                }
                n *= shape[j];
            }
            return n;
        }

        // Standard-normal sample via Box–Muller. (System.Random has no Gaussian;
        // the C++ original used std::normal_distribution — exact values differ but
        // the distribution matches, which is all the model relies on.)
        private static double NextGaussian(Random rng)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
