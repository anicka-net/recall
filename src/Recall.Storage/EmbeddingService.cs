using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace Recall.Storage;

public class EmbeddingService : IDisposable
{
    private const int MaxSeqLength = 256;
    public const int EmbeddingDim = 384;

    private InferenceSession? _session;
    private Tokenizer? _tokenizer;
    private readonly string _modelDir;
    private bool _disposed;

    public bool IsAvailable { get; private set; }

    public EmbeddingService(string modelDir)
    {
        _modelDir = modelDir;
        RegisterOnnxNativeResolver();
        Initialize();
    }

    /// <summary>
    /// ONNX Runtime P/Invokes "onnxruntime.dll" but on Linux the file is libonnxruntime.so.
    /// .NET 10 doesn't auto-resolve this, so we help it along.
    /// </summary>
    private static void RegisterOnnxNativeResolver()
    {
        var onnxAsm = typeof(InferenceSession).Assembly;
        NativeLibrary.SetDllImportResolver(onnxAsm, (name, assembly, searchPath) =>
        {
            // Let the default resolver try first for non-onnxruntime libraries
            if (!name.Contains("onnxruntime"))
                return IntPtr.Zero;

            // Try the standard name on Linux
            if (NativeLibrary.TryLoad("libonnxruntime.so", assembly, searchPath, out var handle))
                return handle;

            return IntPtr.Zero;
        });
    }

    private void Initialize()
    {
        var modelPath = Path.Combine(_modelDir, "model.onnx");
        var vocabPath = Path.Combine(_modelDir, "vocab.txt");

        if (!File.Exists(modelPath) || !File.Exists(vocabPath))
        {
            Console.Error.WriteLine($"Embedding model not found at {_modelDir}");
            Console.Error.WriteLine("Vector search disabled, falling back to text search.");
            return;
        }

        try
        {
            _tokenizer = BertTokenizer.Create(vocabPath);
            _session = new InferenceSession(modelPath);
            IsAvailable = true;
            Console.Error.WriteLine($"Embedding model loaded from {_modelDir}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load embedding model: {ex.Message}");
        }
    }

    public float[] Embed(string text)
    {
        if (!IsAvailable)
            throw new InvalidOperationException(
                $"Embedding model not available. Place model.onnx and vocab.txt in {_modelDir}");

        // Tokenize (BertTokenizer adds [CLS] and [SEP] automatically)
        var ids = _tokenizer!.EncodeToIds(text, MaxSeqLength, out _, out _);
        int len = ids.Count;

        var inputIds = new long[len];
        var attentionMask = new long[len];
        var tokenTypeIds = new long[len];

        for (int i = 0; i < len; i++)
        {
            inputIds[i] = ids[i];
            attentionMask[i] = 1;
        }

        // Run ONNX inference
        using var inputIdsOrt = OrtValue.CreateTensorValueFromMemory(
            inputIds, [1, len]);
        using var maskOrt = OrtValue.CreateTensorValueFromMemory(
            attentionMask, [1, len]);
        using var typeOrt = OrtValue.CreateTensorValueFromMemory(
            tokenTypeIds, [1, len]);

        var inputs = new Dictionary<string, OrtValue>
        {
            ["input_ids"] = inputIdsOrt,
            ["attention_mask"] = maskOrt,
            ["token_type_ids"] = typeOrt
        };

        using var runOpts = new RunOptions();
        using var outputs = _session!.Run(runOpts, inputs, _session.OutputNames);

        // Output shape: [1, seq_len, 384]
        var output = outputs[0].GetTensorDataAsSpan<float>();

        // Mean pooling over sequence positions
        var pooled = new float[EmbeddingDim];
        for (int i = 0; i < len; i++)
            for (int j = 0; j < EmbeddingDim; j++)
                pooled[j] += output[i * EmbeddingDim + j];

        var invLen = 1f / len;
        for (int j = 0; j < EmbeddingDim; j++)
            pooled[j] *= invLen;

        // L2 normalize
        float norm = 0;
        for (int j = 0; j < EmbeddingDim; j++)
            norm += pooled[j] * pooled[j];
        norm = MathF.Sqrt(norm);

        if (norm > 1e-12f)
        {
            var invNorm = 1f / norm;
            for (int j = 0; j < EmbeddingDim; j++)
                pooled[j] *= invNorm;
        }

        return pooled;
    }

    /// <summary>
    /// Cosine similarity of two L2-normalized embeddings = their dot product.
    /// </summary>
    public static float Similarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float dot = 0;
        for (int i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot;
    }

    public static byte[] Serialize(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] Deserialize(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _session?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
