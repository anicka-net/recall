# Vector Search: C# Walkthrough

How Recall moved from keyword search (FTS5) to semantic search (vector
embeddings). Read the original `csharp-walkthrough.md` first - this builds
on those concepts.

The core idea: instead of matching words in a query against words in diary
entries, we convert both to arrays of 384 numbers (embeddings) and compare
how similar those arrays are. "Feeling completely drained today" matches
"exhaustion and burnout hitting hard" because they point in the same
direction in embedding space, even though they share zero words.

---

## 1. EmbeddingService (`Recall.Storage/EmbeddingService.cs`)

This is the new file. It does three things: loads an ONNX neural network
model, turns text into float arrays, and compares those arrays.

### The Native Library Problem

```csharp
using System.Runtime.InteropServices;

private static void RegisterOnnxNativeResolver()
{
    var onnxAsm = typeof(InferenceSession).Assembly;
    NativeLibrary.SetDllImportResolver(onnxAsm, (name, assembly, searchPath) =>
    {
        if (!name.Contains("onnxruntime"))
            return IntPtr.Zero;

        if (NativeLibrary.TryLoad("libonnxruntime.so", assembly, searchPath, out var handle))
            return handle;

        return IntPtr.Zero;
    });
}
```

**Why this exists** - ONNX Runtime is a C++ library with a thin C# wrapper.
The C# code calls into native C++ via **P/Invoke** (Platform Invoke) - the
.NET mechanism for calling native libraries, like Python's `ctypes` or
`cffi`. Internally, the ONNX Runtime C# wrapper declares something like
`[DllImport("onnxruntime.dll")]` which tells .NET "find and load a native
library called onnxruntime.dll." On Windows this works. On Linux, the actual
file is `libonnxruntime.so`, and .NET 10 doesn't resolve the name
automatically.

**`NativeLibrary.SetDllImportResolver`** - A hook that intercepts native
library loading for a specific assembly. When .NET tries to load
"onnxruntime.dll", our resolver fires, tries `libonnxruntime.so` instead,
and returns the handle. `IntPtr.Zero` means "I can't help, let the default
resolver try."

**`typeof(InferenceSession).Assembly`** - `typeof()` gets the type metadata
(like Python's `type()` but at compile time). `.Assembly` is the DLL that
contains that type. We need this to tell .NET *which* assembly's P/Invoke
calls we're intercepting.

**`NativeLibrary.TryLoad(..., out var handle)`** - The **`out` parameter**
pattern. Instead of returning a tuple `(bool, IntPtr)`, C# methods can
"return" extra values through `out` parameters. `TryLoad` returns `bool`
(success?) and sets `handle` to the loaded library pointer. The `out var`
declares the variable inline. This Try/out pattern is everywhere in .NET:
`int.TryParse(s, out var n)`, `dict.TryGetValue(key, out var val)`, etc.

### Model Loading

```csharp
private InferenceSession? _session;
private Tokenizer? _tokenizer;

private void Initialize()
{
    _tokenizer = BertTokenizer.Create(vocabPath);
    _session = new InferenceSession(modelPath);
    IsAvailable = true;
}
```

**`InferenceSession`** - ONNX Runtime's main class. It loads a `.onnx` model
file (a neural network exported to the ONNX interchange format) and runs
inference on it. Think of it as "the model, ready to use." It's expensive
to create (parses the model, allocates memory) so we create it once and
reuse it.

**`BertTokenizer.Create(vocabPath)`** - From `Microsoft.ML.Tokenizers`.
BERT models don't work with raw text - they need text split into tokens
(subwords) and mapped to integer IDs using a vocabulary file. "unexpected"
might become tokens `[un, ##ex, ##pected]` with IDs `[4895, 4654, 2532]`. The `##`
prefix means "continuation of previous word" (WordPiece tokenization). The
tokenizer also adds special tokens: `[CLS]` at the start and `[SEP]` at the
end - these are how BERT knows where a sentence begins and ends.

**`Tokenizer?`** - The `?` suffix is a **nullable reference type**. It means
this field might be null. Without `?`, the compiler warns if you don't
initialize it. This is C#'s way of preventing NullReferenceException at
compile time (if you have `<Nullable>enable</Nullable>` in the project).
`_session!.Run(...)` later is the **null-forgiving operator** - "I know this
looks nullable, but trust me, it's not null here." Use sparingly.

### Generating Embeddings

```csharp
public float[] Embed(string text)
{
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
```

**`out _, out _`** - **Discards**. `EncodeToIds` has `out` parameters we
don't need (like normalized text). The underscore `_` means "I'm explicitly
ignoring this value." Unlike just not using a variable, discards don't
generate compiler warnings.

**The three input arrays** - BERT models expect three parallel arrays:
- `inputIds`: the token IDs (integers from the vocabulary)
- `attentionMask`: 1 for real tokens, 0 for padding (we have no padding
  since we use the exact token count, so all 1s)
- `tokenTypeIds`: 0 for "first sentence" - BERT can handle two sentences
  (for tasks like "is sentence A related to sentence B?"), but for
  embeddings we only have one, so all 0s

All three must be `long[]` (64-bit integers) because that's what the ONNX
model expects. `int` in C# is 32-bit, `long` is 64-bit.

```csharp
    using var inputIdsOrt = OrtValue.CreateTensorValueFromMemory(
        inputIds, [1, len]);
    using var maskOrt = OrtValue.CreateTensorValueFromMemory(
        attentionMask, [1, len]);
    using var typeOrt = OrtValue.CreateTensorValueFromMemory(
        tokenTypeIds, [1, len]);
```

**`OrtValue.CreateTensorValueFromMemory`** - Wraps a C# array as an ONNX
tensor without copying. The `[1, len]` is the **shape**: batch size 1 (one
text at a time), sequence length `len`. Neural networks operate on tensors
(multi-dimensional arrays) with explicit shapes. `using` ensures the native
ONNX memory is freed.

**`[1, len]`** - This is a **collection expression** (C# 12). Shorthand
for `new long[] { 1, len }`. Works for arrays, lists, spans.

```csharp
    var inputs = new Dictionary<string, OrtValue>
    {
        ["input_ids"] = inputIdsOrt,
        ["attention_mask"] = maskOrt,
        ["token_type_ids"] = typeOrt
    };

    using var runOpts = new RunOptions();
    using var outputs = _session!.Run(runOpts, inputs, _session.OutputNames);
    var output = outputs[0].GetTensorDataAsSpan<float>();
```

**`_session.Run(...)`** - Runs the neural network. Takes named inputs,
returns named outputs. Like calling a function that takes three arrays and
returns one. The ONNX model does millions of matrix multiplications
internally (6 transformer layers, 12 attention heads each) but from C#
it's just one method call.

**`GetTensorDataAsSpan<float>()`** - Returns a **`Span<float>`**. This is
one of C#'s most important performance types. A `Span<T>` is a view into
contiguous memory - like a slice in Go or Rust, or a `memoryview` in Python.
It doesn't copy data. It doesn't allocate. It's stack-only (can't be stored
in a field or captured in a lambda). The output tensor lives in native ONNX
memory, and the span lets us read it directly without copying 384 * seq_len
floats.

### Mean Pooling

```csharp
    // Output shape: [1, seq_len, 384]
    var pooled = new float[EmbeddingDim];
    for (int i = 0; i < len; i++)
        for (int j = 0; j < EmbeddingDim; j++)
            pooled[j] += output[i * EmbeddingDim + j];

    var invLen = 1f / len;
    for (int j = 0; j < EmbeddingDim; j++)
        pooled[j] *= invLen;
```

**What this does** - The BERT model outputs a 384-dimensional vector *for
each token*. "Good morning" with 4 tokens (including [CLS] and [SEP])
gives a [4, 384] matrix. We need a single [384] vector for the whole text.
Mean pooling averages the vectors: for each of the 384 dimensions, sum across
all tokens, divide by token count. The result is a single vector representing
the overall meaning of the text.

**`output[i * EmbeddingDim + j]`** - Flat indexing into a 2D array. The span
is a 1D view of a 2D tensor. Row `i`, column `j` is at position
`i * num_columns + j`. This is **row-major order**, same as C and NumPy's
default.

**`1f / len`** - The `f` suffix makes it a `float` literal. Without it,
`1 / len` would be integer division (floor). Multiplying by the reciprocal
once is faster than dividing 384 times (division is expensive on CPUs).

### L2 Normalization

```csharp
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
```

**Why normalize** - After normalization, each vector has length 1 (lies on
the unit sphere). This makes comparing vectors simple: the dot product of
two unit vectors equals their cosine similarity. Without normalizing, longer
texts would have larger vectors and dominate similarity scores regardless of
meaning.

**`MathF.Sqrt`** - `MathF` operates on `float` (32-bit), `Math` on `double`
(64-bit). Since our arrays are `float[]`, `MathF` avoids float-to-double
conversions.

**`1e-12f`** - Guard against division by zero for degenerate inputs (empty
text, all-zero vectors). A vector with near-zero norm has no meaningful
direction, so we leave it as-is.

### Similarity and Serialization

```csharp
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
```

**`ReadOnlySpan<float>`** - Like `Span<float>` but immutable. The method
accepts both `float[]` and `Span<float>` arguments - C# implicitly converts
arrays to spans. This means we can pass freshly-computed embeddings (arrays)
or views into database blobs without copying.

**`Buffer.BlockCopy`** - Copies raw bytes between arrays. Not elements -
bytes. `float[]` to `byte[]` and back, reinterpreting memory layout. This is
the C# equivalent of Python's `struct.pack`/`unpack` or NumPy's
`.tobytes()`/`np.frombuffer()`. It works because `float` in C# is always
IEEE 754 32-bit, and SQLite stores BLOBs as raw bytes. Each 384-float
embedding becomes exactly 1536 bytes (384 * 4).

**`sizeof(float)`** - Compile-time constant: 4 bytes. Only works for
primitive types (`int`, `float`, `long`, etc.).

---

## 2. Schema Migration (`Recall.Storage/Schema.cs`)

```csharp
public static void Initialize(SqliteConnection connection)
{
    using var cmd = connection.CreateCommand();
    cmd.CommandText = CreateTablesSql;
    cmd.ExecuteNonQuery();

    Migrate(connection);
}

private static void Migrate(SqliteConnection connection)
{
    using var verCmd = connection.CreateCommand();
    verCmd.CommandText = "PRAGMA user_version";
    var version = Convert.ToInt32(verCmd.ExecuteScalar());

    if (version < 1)
    {
        using var alter = connection.CreateCommand();
        alter.CommandText = """
            ALTER TABLE entries ADD COLUMN embedding BLOB;
            PRAGMA user_version = 1;
            """;
        alter.ExecuteNonQuery();
    }
}
```

**`PRAGMA user_version`** - SQLite provides a free integer you can use for
anything. We use it as a schema version counter. On every connection, we
check the version and run any migrations needed. `version < 1` means "this
database was created before vector search was added." The ALTER runs once
and bumps the version to 1. Next connection, `version < 1` is false, so
it's skipped. Add future migrations as `if (version < 2) { ... }`.

**Why not just `ALTER TABLE IF NOT EXISTS`** - SQLite doesn't have
`ADD COLUMN IF NOT EXISTS`. The `user_version` approach is the standard
workaround and scales to any number of schema changes.

**Why keep the FTS5 tables** - They're still in `CreateTablesSql`. New
databases get both FTS5 and the embedding column. The FTS5 tables aren't
used for search anymore, but removing them would break existing databases
that have the FTS5 triggers. Cleaning them up is a future migration.

---

## 3. Vector Search in DiaryDatabase

### Writing with Embeddings

```csharp
public int WriteEntry(string content, string? tags = null, ...)
{
    var textToEmbed = string.IsNullOrEmpty(tags) ? content : $"{content}\n{tags}";
    byte[]? embeddingBlob = null;
    if (_embeddings is { IsAvailable: true })
    {
        try { embeddingBlob = EmbeddingService.Serialize(_embeddings.Embed(textToEmbed)); }
        catch { /* non-fatal: entry saved without embedding */ }
    }

    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO entries (created_at, content, tags, conversation_id, source, embedding)
        VALUES (@now, @content, @tags, @cid, @source, @emb);
        SELECT last_insert_rowid();
        """;
    cmd.Parameters.AddWithValue("@emb", (object?)embeddingBlob ?? DBNull.Value);
    ...
}
```

**`_embeddings is { IsAvailable: true }`** - **Property pattern matching**.
This reads: "is `_embeddings` non-null AND does its `IsAvailable` property
equal `true`?" One expression checks both. In older C# you'd write
`_embeddings != null && _embeddings.IsAvailable`. The `is { }` syntax
extends to deeper checks: `_embeddings is { IsAvailable: true, EmbeddingDim: 384 }`.

**Why combine content and tags** - We embed `"entry content\ntag1,tag2"` so
the vector captures the meaning of both. Searching for "work" will match
entries tagged with "work" even if the content doesn't mention it.

**Graceful degradation** - If embedding fails (model not loaded, OOM,
anything), the entry is still saved without an embedding. It won't appear
in vector search but will still be found by the LIKE fallback. This means
the server never refuses to write because of an embedding problem.

### Searching

```csharp
public List<DiaryEntry> Search(string query, int limit = 10)
{
    if (string.IsNullOrWhiteSpace(query))
        return GetRecent(limit);

    if (_embeddings is { IsAvailable: true })
    {
        try { return VectorSearch(query, limit); }
        catch { /* fall through to LIKE */ }
    }

    return SearchLike(query, limit);
}
```

**The fallback chain**: vector search -> LIKE search -> recent entries. If
the model isn't available, LIKE search (substring matching) takes over. If
the query is blank, just return recent entries. The caller never knows which
path was taken.

```csharp
private List<DiaryEntry> VectorSearch(string query, int limit)
{
    var queryEmbedding = _embeddings!.Embed(query);

    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        SELECT id, created_at, content, tags, conversation_id, embedding
        FROM entries
        WHERE embedding IS NOT NULL
        """;

    var scored = new List<(DiaryEntry Entry, float Score)>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var entry = new DiaryEntry(...);

        var blob = (byte[])reader.GetValue(5);
        var embedding = EmbeddingService.Deserialize(blob);
        var score = EmbeddingService.Similarity(queryEmbedding, embedding);
        scored.Add((entry, score));
    }

    return scored
        .OrderByDescending(x => x.Score)
        .Take(limit)
        .Select(x => x.Entry)
        .ToList();
}
```

**Brute-force search** - Load every embedding, compute similarity against
the query, sort, take top-k. This sounds expensive but isn't:
- 1000 entries * 384 floats * 1 multiply-add = 384,000 operations
- A modern CPU does billions per second
- Total: well under 1 millisecond

You'd need tens of thousands of entries before this becomes noticeable. At
that point, sqlite-vec (SQLite extension for approximate nearest neighbor
search) would be the upgrade path. For a personal diary, brute force is
optimal: zero complexity, exact results, negligible latency.

**`var scored = new List<(DiaryEntry Entry, float Score)>()`** - A list of
**named tuples**. Each element has `.Entry` and `.Score` properties. Like
Python's `list[tuple[DiaryEntry, float]]` but with named fields.

**`scored.OrderByDescending(x => x.Score).Take(limit).Select(x => x.Entry)`**
- LINQ pipeline:
1. Sort by score, highest first
2. Take the top `limit` results
3. Project to just the entry (discard the score)
4. `.ToList()` materializes the result

This is **lazy evaluation** - `OrderByDescending`, `Take`, `Select` don't
execute immediately. They build a pipeline. `.ToList()` pulls values through
the whole chain at once. Like Python generators chained together.

### Backfilling Existing Entries

```csharp
public int BackfillEmbeddings()
{
    if (_embeddings is not { IsAvailable: true }) return 0;

    using var selectCmd = _conn.CreateCommand();
    selectCmd.CommandText = "SELECT id, content, tags FROM entries WHERE embedding IS NULL";

    var toBackfill = new List<(int Id, string Text)>();
    using (var reader = selectCmd.ExecuteReader())
    {
        while (reader.Read()) { ... }
    }

    foreach (var (id, text) in toBackfill)
    {
        var emb = EmbeddingService.Serialize(_embeddings.Embed(text));
        using var updateCmd = _conn.CreateCommand();
        updateCmd.CommandText = "UPDATE entries SET embedding = @emb WHERE id = @id";
        ...
    }
}
```

**`is not { IsAvailable: true }`** - Negated pattern matching. "If
embeddings are null OR not available, return early." The `not` keyword
works with any pattern.

**`var (id, text) in toBackfill`** - **Tuple deconstruction** in a foreach.
Each element of the list is a `(int, string)` tuple, and `var (id, text)`
unpacks it into two variables. Like Python's `for id, text in to_backfill:`.

**Two-pass approach** - First read all entries that need embeddings (closing
the reader), then update them one by one. We can't update while the reader
is open because SQLite only allows one active reader per connection. The
`using (var reader = ...) { }` with explicit braces ensures the reader is
disposed before the update loop starts. (Compare with `using var reader =
...` without braces, which disposes at end of method.)

---

## 4. How It All Flows

```
User searches "feeling overwhelmed"
         |
    DiaryTools.Query()
         |
    DiaryDatabase.Search("feeling overwhelmed")
         |
    EmbeddingService.Embed("feeling overwhelmed")
         |  BertTokenizer: [CLS] feeling overwhelmed [SEP] -> [101, 3110, 12063, 102]
         |  ONNX Runtime: [4 tokens, 384 dims] tensor
         |  Mean pool: [384] vector
         |  L2 normalize: unit vector
         |
    Load all (entry, embedding) from SQLite
    For each: dot product with query vector
    Sort by score, return top-k
         |
    Finds entry about "burnout and too much on my plate"
    (cosine similarity ~0.72 despite zero word overlap)
```

The key insight: the neural network (all-MiniLM-L6-v2) was trained on
hundreds of millions of text pairs. It learned that "feeling overwhelmed"
and "burnout" appear in similar contexts, so their vectors point in similar
directions. This works across languages, synonyms, paraphrases, and even
conceptual relationships that no keyword index could capture.

---

## 5. The Model Files

The system needs two files in `~/.recall/models/all-MiniLM-L6-v2/`:

- **`model.onnx`** (87 MB) - The neural network weights and graph. ONNX
  (Open Neural Network Exchange) is an interchange format - this model was
  originally trained in Python/PyTorch, exported to ONNX, and now runs in
  C# via ONNX Runtime. Same math, different language.

- **`vocab.txt`** (227 KB) - 30,522 WordPiece tokens, one per line. The
  tokenizer maps text to IDs by looking up this vocabulary. Line number =
  token ID.

If these files are missing, everything still works - just without vector
search. The LIKE fallback handles queries until you download the model.
