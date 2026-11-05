namespace HelixPythonNamespace.ML
{
    using System.Collections.Generic;
    using System.IO;

    /// <summary>
    /// Compact little-endian binary format with magic + version. The model is a
    /// list of named tensors, stored in the order given. Port of the C++
    /// <c>helix::ml::serialize</c>. <see cref="BinaryWriter"/>/<see cref="BinaryReader"/>
    /// are little-endian, matching the original on-disk layout.
    /// </summary>
    public static class TensorSerializer
    {
        private const uint Magic = 0x504F4F4D; // 'POOM' (Helix ML)
        private const uint Version = 1;

        public static bool SaveTensors(Stream stream, IList<Tensor> tensors)
        {
            using (BinaryWriter w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write((uint)tensors.Count);
                foreach (Tensor t in tensors)
                {
                    if (t == null)
                    {
                        return false;
                    }
                    WriteTensor(w, t);
                }
            }
            return true;
        }

        public static bool LoadTensors(Stream stream, IList<Tensor> outTensors)
        {
            using (BinaryReader r = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
            {
                uint magic = r.ReadUInt32();
                if (magic != Magic)
                {
                    return false;
                }
                uint version = r.ReadUInt32();
                if (version != Version)
                {
                    return false;
                }
                uint count = r.ReadUInt32();
                if (count != (uint)outTensors.Count)
                {
                    return false;
                }
                for (int i = 0; i < outTensors.Count; i++)
                {
                    if (outTensors[i] == null)
                    {
                        return false;
                    }
                    if (!ReadTensor(r, outTensors[i]))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public static bool SaveParameters(string path, IList<Parameter> parameters)
        {
            List<Tensor> tensors = new List<Tensor>(parameters.Count);
            foreach (Parameter p in parameters)
            {
                if (p == null)
                {
                    return false;
                }
                tensors.Add(p.Data);
            }
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                return SaveTensors(fs, tensors);
            }
        }

        public static bool LoadParameters(string path, IList<Parameter> parameters)
        {
            List<Tensor> tensors = new List<Tensor>(parameters.Count);
            foreach (Parameter p in parameters)
            {
                if (p == null)
                {
                    return false;
                }
                tensors.Add(p.Data);
            }
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                return LoadTensors(fs, tensors);
            }
        }

        private static void WriteTensor(BinaryWriter w, Tensor t)
        {
            w.Write((uint)t.Rank);
            int[] shape = t.Shape;
            for (int i = 0; i < shape.Length; i++)
            {
                w.Write(shape[i]);
            }
            float[] data = t.Data;
            for (int i = 0; i < data.Length; i++)
            {
                w.Write(data[i]);
            }
        }

        private static bool ReadTensor(BinaryReader r, Tensor outTensor)
        {
            uint rank = r.ReadUInt32();
            if ((int)rank != outTensor.Rank)
            {
                return false;
            }
            for (uint i = 0; i < rank; i++)
            {
                int d = r.ReadInt32();
                if (d != outTensor.Dim((int)i))
                {
                    return false;
                }
            }
            float[] data = outTensor.Data;
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = r.ReadSingle();
            }
            return true;
        }
    }
}
