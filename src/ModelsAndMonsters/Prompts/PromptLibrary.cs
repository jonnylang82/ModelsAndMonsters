using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace ModelsAndMonsters.Prompts;

/// <summary>
/// Loads prompt templates from files so prompts can be edited and versioned independently of code.
/// Prompt comparison is expected to become part of the experiment, so every template is hashed and
/// the hashes are recorded in run.json.
/// </summary>
public sealed class PromptLibrary
{
    private readonly ImmutableDictionary<string, PromptTemplate> _templates;

    private PromptLibrary(ImmutableDictionary<string, PromptTemplate> templates)
    {
        _templates = templates;
    }

    /// <summary>Template name (file name without extension) to content hash.</summary>
    public IReadOnlyDictionary<string, string> Versions =>
        _templates.ToDictionary(kv => kv.Key, kv => kv.Value.Version);

    public static PromptLibrary LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Prompt template directory not found: {directory}");
        }

        var builder = ImmutableDictionary.CreateBuilder<string, PromptTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            builder[name] = new PromptTemplate(name, File.ReadAllText(file));
        }

        if (builder.Count == 0)
        {
            throw new InvalidOperationException($"No prompt templates (*.md) found in {directory}.");
        }

        return new PromptLibrary(builder.ToImmutable());
    }

    public PromptTemplate Get(string name) =>
        _templates.TryGetValue(name, out var template)
            ? template
            : throw new KeyNotFoundException(
                $"Prompt template '{name}' not found. Available: {string.Join(", ", _templates.Keys.Order())}.");

    /// <summary>Renders a template by name, substituting <c>{{placeholder}}</c> tokens.</summary>
    public string Render(string name, IReadOnlyDictionary<string, string?>? values = null) =>
        Get(name).Render(values);
}

/// <summary>
/// A single prompt file. Substitution is intentionally trivial (<c>{{name}}</c>) — a templating engine
/// would be scope the experiment does not need.
/// </summary>
public sealed class PromptTemplate
{
    public PromptTemplate(string name, string content)
    {
        Name = name;
        Content = content.Replace("\r\n", "\n");
        Version = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Content)))[..16];
    }

    public string Name { get; }

    public string Content { get; }

    /// <summary>Short content hash used to identify the exact prompt text a run used.</summary>
    public string Version { get; }

    public string Render(IReadOnlyDictionary<string, string?>? values = null)
    {
        if (values is null || values.Count == 0)
        {
            return Content;
        }

        var builder = new StringBuilder(Content);
        foreach (var (key, value) in values)
        {
            builder.Replace($"{{{{{key}}}}}", value ?? "");
        }

        return builder.ToString();
    }
}
