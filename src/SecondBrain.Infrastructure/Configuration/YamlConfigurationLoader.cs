using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.Security.Cryptography;
using System.Text;
using SecondBrain.Core.Configuration;
using SecondBrain.Infrastructure.FileSystem;

namespace SecondBrain.Infrastructure.Configuration;

/// <summary>Loads the M0 subset of Appendix B, retaining secret references in options.</summary>
public sealed class YamlConfigurationLoader
{
    private static readonly HashSet<string> DeferredSections = new(StringComparer.Ordinal)
    { "types", "retrieval", "sandbox" };
    // Appendix B sections of which only some keys are modeled; the remaining keys stay deferred.
    private static readonly Dictionary<string, HashSet<string>> PartialSections = new(StringComparer.Ordinal)
    { ["ingest"] = new(StringComparer.Ordinal) { "sync_embed_deadline_ms", "deep_rescan_at" } };
    private static readonly HashSet<string> SecretFields = new(StringComparer.Ordinal)
    { "api_key", "password", "bootstrap_password", "client_secret", "access_client_secret", "secret", "token", "backup_key" };
    private readonly SecretResolver resolver;
    private readonly Func<string, string?> environment;

    public YamlConfigurationLoader(string? secretsDirectory = null, Func<string, string?>? environment = null)
    {
        this.environment = environment ?? Environment.GetEnvironmentVariable;
        resolver = new SecretResolver(secretsDirectory, this.environment);
    }

    public ISecretResolver Secrets => resolver;

    public SecondBrainOptions Load(string path) => LoadSnapshot(path).Options;

    public ConfigurationSnapshot LoadSnapshot(string path)
    {
        try { return LoadTextSnapshot(File.ReadAllText(path)); }
        catch (IOException) { throw new ConfigurationException("Configuration file cannot be read."); }
        catch (UnauthorizedAccessException) { throw new ConfigurationException("Configuration file cannot be read."); }
    }

    public SecondBrainOptions LoadText(string yaml) => LoadTextSnapshot(yaml).Options;

    public ConfigurationSnapshot LoadTextSnapshot(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
                throw new ConfigurationException("Configuration must contain one YAML mapping document.");
            var secretReferences = new Dictionary<YamlScalarNode, string>(ReferenceEqualityComparer.Instance);
            Visit(root, new HashSet<YamlNode>(ReferenceEqualityComparer.Instance), secretReferences, 0);
            if (secretReferences.Any(pair => pair.Key.Value != pair.Value))
                throw new ConfigurationException("Secret references must not be reused through YAML aliases in other fields.");
            if (Get(root, "providers") is YamlMappingNode providers)
            {
                foreach (var item in providers.Children.Values.OfType<YamlMappingNode>())
                {
                    RejectAliasConflict(item, "kind", "adapter");
                    RejectAliasConflict(item, "endpoint", "base_url");
                }
            }
            var sandboxMetadata = string.Empty;
            if (Get(root, "sandbox") is { } sandbox)
            {
                using var metadataWriter = new StringWriter();
                new YamlStream(new YamlDocument(sandbox)).Save(metadataWriter, false);
                sandboxMetadata = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(metadataWriter.ToString())));
            }
            foreach (var section in DeferredSections) root.Children.Remove(new YamlScalarNode(section));
            foreach (var (section, modeled) in PartialSections)
            {
                if (Get(root, section) is not YamlMappingNode partial) { root.Children.Remove(new YamlScalarNode(section)); continue; }
                foreach (var key in partial.Children.Keys.Where(key => key is not YamlScalarNode { Value: { } name } || !modeled.Contains(name)).ToArray())
                    partial.Children.Remove(key);
            }
            if (Get(root, "sources") is YamlMappingNode sources) sources.Children.Remove(new YamlScalarNode("folders"));
            using var writer = new StringWriter();
            stream.Save(writer, false);
            var options = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .WithDuplicateKeyChecking()
                .Build().Deserialize<SecondBrainOptions>(writer.ToString());
            if (environment("SECONDBRAIN_DATA_ROOT") is { } rootOverride) options.DataRoot = rootOverride;
            if (options.Extractor is null) throw new ConfigurationException("extractor must not be null.");
            if (environment("SECONDBRAIN_EXTRACTOR_SOCKET") is { } socketOverride) options.Extractor.SocketPath = socketOverride;
            ConfigurationValidator.Validate(options, UnixHostPathInspector.Instance);
            foreach (var calendar in options.Sources.CalendarSubscriptions)
            {
                try
                {
                    var link = SecondBrain.Core.Sources.CalendarLink.Parse(resolver.Resolve(calendar.UrlReference));
                    if (!options.Sources.CalendarAllowedOrigins.Any(origin => SecondBrain.Core.Sources.CalendarLink.ValidateOrigin(origin) == link.Origin))
                        throw new ConfigurationException("Configured calendar origin must appear in sources.calendar_allowed_origins.");
                }
                catch (SecondBrain.Core.Sources.CalendarSubscriptionException) { throw new ConfigurationException("Configured calendar URL is invalid."); }
            }
            return new ConfigurationSnapshot(options, sandboxMetadata);
        }
        catch (YamlException) { throw new ConfigurationException("Configuration YAML or schema is invalid; inspect field names and types."); }
        catch (ArgumentException) { throw new ConfigurationException("Configuration contains an invalid value."); }
        catch (InvalidOperationException) { throw new ConfigurationException("Configuration contains an invalid structure."); }
    }

    private void Visit(YamlNode node, HashSet<YamlNode> ancestors, Dictionary<YamlScalarNode, string> secretReferences, int depth, bool calendarEntry = false)
    {
        if (depth > 64 || !ancestors.Add(node))
            throw new ConfigurationException("Configuration YAML aliases are recursive or nesting exceeds 64 levels.");
        try
        {
            if (node is YamlMappingNode mapping)
            {
                foreach (var pair in mapping.Children)
                {
                    if (pair.Key is not YamlScalarNode key || key.Value is null)
                        throw new ConfigurationException("Configuration mapping keys must be text.");
                    if (SecretFields.Contains(key.Value) || calendarEntry && key.Value == "url")
                    {
                        if (pair.Value is not YamlScalarNode scalar || scalar.Value is null || !SecretResolver.ReferencePattern().IsMatch(scalar.Value))
                            throw new ConfigurationException($"Field {key.Value} must contain a ${{NAME}} reference, never an inline secret.");
                        _ = resolver.Resolve(scalar.Value);
                        secretReferences[scalar] = scalar.Value;
                        continue;
                    }
                    if (key.Value == "calendar_subscriptions")
                    {
                        if (pair.Value is not YamlSequenceNode calendars) throw new ConfigurationException("calendar_subscriptions must be a list.");
                        foreach (var calendar in calendars.Children)
                        {
                            if (calendar is not YamlMappingNode) throw new ConfigurationException("Calendar subscription entries must be mappings.");
                            Visit(calendar, ancestors, secretReferences, depth + 1, calendarEntry: true);
                        }
                        continue;
                    }
                    Visit(pair.Value, ancestors, secretReferences, depth + 1);
                }
            }
            else if (node is YamlSequenceNode sequence)
            {
                foreach (var child in sequence.Children) Visit(child, ancestors, secretReferences, depth + 1);
            }
            else if (node is YamlScalarNode scalar && scalar.Value is not null)
            {
                scalar.Value = resolver.Expand(scalar.Value);
                if (scalar.Value.Contains("${", StringComparison.Ordinal))
                    throw new ConfigurationException("Configuration contains a malformed reference.");
                if (Uri.TryCreate(scalar.Value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
                    throw new ConfigurationException("Endpoint URLs must not contain credentials.");
            }
        }
        finally { ancestors.Remove(node); }
    }

    private static YamlNode? Get(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static void RejectAliasConflict(YamlMappingNode node, string first, string second)
    {
        if (Get(node, first) is YamlScalarNode a && Get(node, second) is YamlScalarNode b && !string.Equals(a.Value, b.Value, StringComparison.Ordinal))
            throw new ConfigurationException($"Provider aliases {first} and {second} conflict.");
    }
}

/// <summary>Validated options and opaque restart-only sandbox metadata; never contains resolved secrets.</summary>
public sealed record ConfigurationSnapshot(SecondBrainOptions Options, string RestartMetadata);
