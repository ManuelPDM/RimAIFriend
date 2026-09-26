using System;

namespace AIPawnControl
{
    /// <summary>
    /// Model-specific embedding details (PHASE3.md §5), picked by the model name, plus vector helpers. Vectors are
    /// stored unit-length, so cosine similarity is a dot product, and packed into a base64 string for the save.
    /// </summary>
    public static class Embedding
    {
        public const int CutDims = 256;

        private static bool IsGemma(string model) => model.IndexOf("gemma", StringComparison.OrdinalIgnoreCase) >= 0;
        private static bool IsNomic(string model) => model.IndexOf("nomic", StringComparison.OrdinalIgnoreCase) >= 0;

        public static string Prefix(string model, bool query)
        {
            if (IsGemma(model))
                return query ? "task: search result | query: " : "title: none | text: ";
            if (IsNomic(model))
                return query ? "search_query: " : "search_document: ";
            return "";
        }

        /// <summary>
        /// Gemma and nomic are Matryoshka models: keep the first 256 values (nomic after a layer norm over all of
        /// them, as its model card says). Any other model keeps its full size; cutting it would ruin the vector.
        /// </summary>
        public static float[] Shape(string model, float[] raw)
        {
            float[] v = raw;
            if (IsNomic(model) && raw.Length > CutDims)
            {
                double mean = 0, variance = 0;
                foreach (float x in raw)
                    mean += x;
                mean /= raw.Length;
                foreach (float x in raw)
                    variance += (x - mean) * (x - mean);
                double scale = 1.0 / Math.Sqrt(variance / raw.Length + 1e-5);
                v = new float[CutDims];
                for (int i = 0; i < CutDims; i++)
                    v[i] = (float)((raw[i] - mean) * scale);
            }
            else if (IsGemma(model) && raw.Length > CutDims)
            {
                v = new float[CutDims];
                Array.Copy(raw, v, CutDims);
            }
            else
                v = (float[])raw.Clone();
            Normalize(v);
            return v;
        }

        /// <summary>
        /// Cosine → 0..1 relevance. Models differ a lot: at 256 dims gemma scores unrelated texts about 0.2 and a good
        /// match about 0.6, while nomic's scores bunch up (0.55 unrelated, 0.75 a good match). Measured in step 3.
        /// </summary>
        public static float Relevance(string model, float cosine)
        {
            float floor = IsNomic(model) ? 0.55f : IsGemma(model) ? 0.2f : 0.3f;
            float good = IsNomic(model) ? 0.8f : 0.65f;
            return Math.Max(0f, Math.Min(1f, (cosine - floor) / (good - floor)));
        }

        /// <summary>What a vector was made with. Only vectors with the same tag are ever compared.</summary>
        public static string Tag(string model, int dims) => model + "|" + dims;

        public static float Cosine(float[] a, float[] b)
        {
            float dot = 0f;
            for (int i = 0; i < a.Length; i++)
                dot += a[i] * b[i];
            return dot;
        }

        public static string Pack(float[] v)
        {
            var bytes = new byte[v.Length * sizeof(float)];
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }

        public static float[] Unpack(string packed)
        {
            if (string.IsNullOrEmpty(packed))
                return null;
            var bytes = Convert.FromBase64String(packed);
            var v = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
            return v;
        }

        private static void Normalize(float[] v)
        {
            double sum = 0;
            foreach (float x in v)
                sum += x * x;
            float length = (float)Math.Sqrt(sum);
            if (length > 0f)
                for (int i = 0; i < v.Length; i++)
                    v[i] /= length;
        }
    }
}
