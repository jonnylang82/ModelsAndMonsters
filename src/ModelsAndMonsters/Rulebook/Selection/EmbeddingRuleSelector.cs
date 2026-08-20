namespace ModelsAndMonsters.Rulebook.Selection;

/// <summary>
/// Turns text into a vector. Deliberately the whole interface: no provider, no model id, no batching policy.
/// </summary>
/// <remarks>
/// v0.8 ships this abstraction and a prototype, and no production implementation, because no embedding
/// provider is configured or testable in this harness. Adding a real dependency purely to claim the box was
/// ticked would put an unmeasured network call on the critical path of every consultation — see
/// <c>reports/rulebook-efficiency.md</c> for the honest finding.
/// </remarks>
public interface IRuleEmbedder
{
    /// <summary>An identifier for the embedding space, so cached vectors from one space are never mixed with another's.</summary>
    string SpaceId { get; }

    /// <summary>True when this embedder can actually produce meaningful vectors. A stub returns false, and the selector falls back.</summary>
    bool IsAvailable { get; }

    Task<IReadOnlyList<float>> EmbedAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// A deterministic, offline, character-n-gram embedder used ONLY to exercise the retrieval path in tests
/// and measurement. It is not a semantic model and must never be configured for a real run.
/// </summary>
/// <remarks>
/// <para>
/// It hashes overlapping character trigrams into a fixed number of buckets and L2-normalises the result.
/// That makes it a lexical-overlap measure, not a semantic one: it will happily rank a card highly because
/// the intent reused its words, and miss a paraphrase that shares none of them. Its measured recall in the
/// evaluation corpus should therefore be read as a FLOOR for what a real embedding model could do, and as
/// evidence that the plumbing — top-K, declared-link expansion, fallback — behaves, not as evidence that
/// embedding retrieval is good enough.
/// </para>
/// <para>
/// It is not a keyword list or a phrase table: it holds no vocabulary of the game, nothing was hand-written
/// per action, and it would work identically on a rulebook about something else. It is simply a very poor
/// embedding model that happens to need no network.
/// </para>
/// </remarks>
public sealed class HashingRuleEmbedder : IRuleEmbedder
{
    private const int Buckets = 256;
    private const int GramSize = 3;

    public string SpaceId => $"hashing-trigram-{Buckets}";

    public bool IsAvailable => true;

    public Task<IReadOnlyList<float>> EmbedAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult(Embed(text));

    public static IReadOnlyList<float> Embed(string? text)
    {
        var vector = new float[Buckets];
        var normalised = (text ?? "").ToLowerInvariant();
        if (normalised.Length < GramSize)
        {
            return vector;
        }

        for (var i = 0; i + GramSize <= normalised.Length; i++)
        {
            var hash = 2166136261u;
            for (var g = 0; g < GramSize; g++)
            {
                hash ^= normalised[i + g];
                hash *= 16777619u;
            }

            vector[hash % Buckets] += 1f;
        }

        var magnitude = MathF.Sqrt(vector.Sum(v => v * v));
        if (magnitude > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= magnitude;
            }
        }

        return vector;
    }

    /// <summary>Cosine similarity of two vectors from the same space. Both are already normalised, so this is a dot product.</summary>
    public static double Similarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count != right.Count)
        {
            return 0;
        }

        double total = 0;
        for (var i = 0; i < left.Count; i++)
        {
            total += left[i] * right[i];
        }

        return total;
    }
}

/// <summary>An embedder that is not configured. Always unavailable, so a selector built on it falls back.</summary>
public sealed class UnavailableRuleEmbedder : IRuleEmbedder
{
    public string SpaceId => "none";

    public bool IsAvailable => false;

    public Task<IReadOnlyList<float>> EmbedAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<float>>([]);
}

/// <summary>
/// Embeds each rule card once, embeds the intent, takes a small top-K by similarity, and expands through
/// declared related-rule links.
/// </summary>
/// <remarks>
/// The card vectors are computed once and cached against the embedder's space and the rulebook version, so
/// the per-consultation cost is one embedding of a short intent and a few hundred multiplications. That
/// cache is deliberately separate from any answer cache: card vectors are static, and an answer is not.
/// A similarity below <see cref="_minimumSimilarity"/> for the best card is treated as "this retrieval has
/// no idea", and the whole bounded rulebook is sent instead.
/// </remarks>
public sealed class EmbeddingRuleSelector : IRuleSelector
{
    private readonly IRuleRepository _repository;
    private readonly IRuleEmbedder _embedder;
    private readonly int _topK;
    private readonly double _minimumSimilarity;
    private Dictionary<string, IReadOnlyList<float>>? _cardVectors;
    private string? _cachedFor;

    public EmbeddingRuleSelector(
        IRuleRepository repository, IRuleEmbedder embedder, int topK = 3, double minimumSimilarity = 0.10)
    {
        _repository = repository;
        _embedder = embedder;
        _topK = topK;
        _minimumSimilarity = minimumSimilarity;
    }

    public RuleSelectionMode Mode => RuleSelectionMode.Embedding;

    public async Task<RuleSelection> SelectAsync(string intent, CancellationToken cancellationToken)
    {
        if (!_embedder.IsAvailable)
        {
            return RuleSelectionSupport.Fallback(_repository, Mode,
                "no embedding provider is configured, so the whole rulebook was sent");
        }

        await EnsureCardVectorsAsync(cancellationToken).ConfigureAwait(false);

        var query = await _embedder.EmbedAsync(intent ?? "", cancellationToken).ConfigureAwait(false);
        if (query.Count == 0)
        {
            return RuleSelectionSupport.Fallback(_repository, Mode,
                "the intent produced no vector, so the whole rulebook was sent");
        }

        var ranked = _cardVectors!
            .Select(pair => (RuleId: pair.Key, Score: HashingRuleEmbedder.Similarity(query, pair.Value)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.RuleId, StringComparer.Ordinal)
            .ToList();

        if (ranked.Count == 0 || ranked[0].Score < _minimumSimilarity)
        {
            return RuleSelectionSupport.Fallback(_repository, Mode,
                $"the best card scored {(ranked.Count == 0 ? 0 : ranked[0].Score):F3}, below the confidence floor " +
                $"of {_minimumSimilarity:F2}, so the whole rulebook was sent");
        }

        var top = ranked.Take(_topK).Where(x => x.Score >= _minimumSimilarity).ToList();
        return RuleSelectionSupport.Build(_repository, Mode,
            [.. top.Select(x => x.RuleId)],
            [.. top.Select(x => $"{x.RuleId} similarity {x.Score:F3} ({_embedder.SpaceId})")]);
    }

    private async Task EnsureCardVectorsAsync(CancellationToken cancellationToken)
    {
        var key = $"{_embedder.SpaceId}|{_repository.RulebookVersion}";
        if (_cardVectors is not null && string.Equals(_cachedFor, key, StringComparison.Ordinal))
        {
            return;
        }

        var vectors = new Dictionary<string, IReadOnlyList<float>>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in _repository.AllCards)
        {
            // The card is embedded from its own text — what it is, and what it explicitly is not. The
            // exclusions matter: half of what distinguishes taking from stealing is each card saying it is
            // not the other.
            var text = $"{card.Summary} {card.Description} {string.Join(" ", card.Exclusions)}";
            vectors[card.RuleId] = await _embedder.EmbedAsync(text, cancellationToken).ConfigureAwait(false);
        }

        _cardVectors = vectors;
        _cachedFor = key;
    }
}
