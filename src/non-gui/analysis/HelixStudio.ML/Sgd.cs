namespace HelixPythonNamespace.ML
{
    using System.Collections.Generic;

    /// <summary>
    /// Plain SGD with optional weight decay. Operates on a list of parameters;
    /// the model owns the parameter storage. Port of the C++ <c>helix::ml::SGD</c>.
    /// </summary>
    public sealed class Sgd
    {
        private readonly List<Parameter> _params;
        private readonly float _lr;
        private readonly float _weightDecay;

        public Sgd(IEnumerable<Parameter> parameters, float lr, float weightDecay = 0.0f)
        {
            _params = new List<Parameter>(parameters);
            _lr = lr;
            _weightDecay = weightDecay;
        }

        public void Step()
        {
            foreach (Parameter p in _params)
            {
                if (p == null)
                {
                    continue;
                }
                Tensor w = p.Data;
                Tensor g = p.Grad;
                int n = w.NumElements();
                for (int i = 0; i < n; i++)
                {
                    float decay = _weightDecay != 0.0f ? _weightDecay * w[i] : 0.0f;
                    w[i] -= _lr * (g[i] + decay);
                }
            }
        }

        public void ZeroGrad()
        {
            foreach (Parameter p in _params)
            {
                if (p != null)
                {
                    p.ZeroGrad();
                }
            }
        }
    }
}
