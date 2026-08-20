using System.Net.Http.Json;
using System.Text.Json;

namespace ModelsAndMonsters.Rulebook.Selection;

/// <summary>
/// An <see cref="IRuleEmbedder"/> backed by an Ollama embedding model.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a direct call to <c>/api/embed</c> rather than another abstraction layer: the whole surface
/// is one request with one string and one vector back, and the harness already talks to this endpoint for
/// chat. It is the only real embedding provider available to this project, which is why the investigation
/// could measure approach 2 honestly instead of shipping a stub and calling it evaluated.
/// </para>
/// <para>
/// It reports itself unavailable when it cannot reach the endpoint or the model, so the selector built on it
/// falls back to the whole bounded rulebook rather than failing a consultation. Availability is probed once
/// and remembered: a selection is on the critical path of a turn and must not pay for a health check.
/// </para>
/// </remarks>
public sealed class OllamaRuleEmbedder : IRuleEmbedder, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly bool _ownsClient;
    private bool? _available;

    public OllamaRuleEmbedder(string endpoint, string model, HttpClient? http = null)
    {
        _model = model;
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.BaseAddress ??= new Uri(endpoint.TrimEnd('/') + "/");
    }

    public string SpaceId => $"ollama:{_model}";

    /// <summary>
    /// True until a call proves otherwise. The first embedding attempt is what establishes availability —
    /// probing eagerly in the constructor would put a network round trip in the path of building a run.
    /// </summary>
    public bool IsAvailable => _available ?? true;

    public async Task<IReadOnlyList<float>> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        if (_available == false)
        {
            return [];
        }

        try
        {
            using var response = await _http
                .PostAsJsonAsync("api/embed", new { model = _model, input = text ?? "" }, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _available = false;
                return [];
            }

            using var document = await JsonDocument
                .ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), default, cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("embeddings", out var embeddings)
                || embeddings.ValueKind != JsonValueKind.Array
                || embeddings.GetArrayLength() == 0)
            {
                _available = false;
                return [];
            }

            var vector = embeddings[0].EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();
            var magnitude = MathF.Sqrt(vector.Sum(v => v * v));
            if (magnitude > 0)
            {
                for (var i = 0; i < vector.Length; i++)
                {
                    vector[i] /= magnitude;
                }
            }

            _available = true;
            return vector;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _available = false;
            return [];
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
