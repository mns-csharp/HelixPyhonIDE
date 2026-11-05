namespace HelixPythonNamespace.ML
{
    /// <summary>
    /// A trainable tensor plus its gradient buffer. The optimizer reads
    /// <see cref="Grad"/> and writes <see cref="Data"/>; the model's forward pass
    /// reads <see cref="Data"/> and the backward pass accumulates into
    /// <see cref="Grad"/>. Port of the C++ <c>helix::ml::Parameter</c>.
    /// </summary>
    public sealed class Parameter
    {
        private readonly Tensor _data;
        private readonly Tensor _grad;

        public Parameter()
        {
            _data = new Tensor();
            _grad = new Tensor();
        }

        public Parameter(Tensor data)
        {
            _data = data;
            _grad = Tensor.Zeros(data.Shape);
        }

        public Tensor Data
        {
            get { return _data; }
        }

        public Tensor Grad
        {
            get { return _grad; }
        }

        public void ZeroGrad()
        {
            _grad.ZeroFill();
        }
    }
}
