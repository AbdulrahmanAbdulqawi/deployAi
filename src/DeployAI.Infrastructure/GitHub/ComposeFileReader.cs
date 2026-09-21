using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace DeployAI.Infrastructure.GitHub;

/// <summary>A named volume a compose service mounts.</summary>
public sealed record ComposeVolume(string Name, string MountPath);

/// <summary>
/// One service as the repository declares it. Facts only — whether a built service with no port
/// and a command of its own is a "worker" is a conclusion, and conclusions belong to the caller.
/// </summary>
public sealed record ComposeService(
    string Name,
    /// <summary>Build context, from either `build: ./x` or `build: { context: ./x }`. Null for a pulled image.</summary>
    string? BuildContext,
    /// <summary>Context-relative Dockerfile, when the mapping form names one.</summary>
    string? DockerfilePath,
    /// <summary>Registry image for a service the repository does not build. Null when built.</summary>
    string? Image,
    IReadOnlyList<int> ExposedPorts,
    /// <summary>Raw `ports:` entries. Their presence is what Traefik cannot share; kept verbatim so the offender can be named.</summary>
    IReadOnlyList<string> HostPortMappings,
    IReadOnlyList<ComposeVolume> Volumes,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> EnvironmentKeys,
    bool HasHealthcheck,
    string? Command);

/// <summary>
/// A parsed compose file. <see cref="IsInconclusive"/> is the distinction every scan here keeps:
/// a file that could not be parsed must never be reported as a file declaring nothing.
/// </summary>
public sealed record ComposeFile(
    IReadOnlyList<ComposeService> Services,
    /// <summary>Every `${VAR}` the file expects the deploy to supply, in first-seen order.</summary>
    IReadOnlyList<string> ReferencedVariables,
    bool IsInconclusive,
    string? ParseError)
{
    public static ComposeFile Inconclusive(string error) => new([], [], true, error);
}

/// <summary>
/// Reads a docker-compose file into its services.
/// </summary>
/// <remarks>
/// <para>
/// Compose was previously read by regex over the whole file — which is how
/// <c>PublishesHostPorts</c> can report that "the compose file publishes host ports" without
/// being able to say which service does, and how a <c>postgres</c> image anywhere in the file
/// becomes "this app needs a database provisioned on the provider". Neither question can be
/// answered without knowing which lines belong to which service, so this parses properly.
/// </para>
/// <para>
/// YamlDotNet rather than a hand-rolled subset: the files this has to read already use flow
/// sequences (<c>test: [CMD-SHELL, ...]</c>), quoted interpolation and both the list and mapping
/// spellings of <c>environment</c>. A parser that got any of those wrong would fail the way the
/// regexes do — quietly, with a plausible answer.
/// </para>
/// </remarks>
public static partial class ComposeFileReader
{
    public static ComposeFile Read(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return ComposeFile.Inconclusive("The compose file was empty.");
        }

        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(content));
            if (stream.Documents.Count == 0)
            {
                return ComposeFile.Inconclusive("The compose file contained no YAML document.");
            }

            if (stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                return ComposeFile.Inconclusive("The compose file's root is not a mapping.");
            }

            root = mapping;
        }
        catch (Exception ex)
        {
            return ComposeFile.Inconclusive(ex.Message);
        }

        var services = new List<ComposeService>();
        if (TryGet(root, "services") is YamlMappingNode serviceMap)
        {
            foreach (var (key, value) in serviceMap.Children)
            {
                if (key is YamlScalarNode { Value: { } name } && value is YamlMappingNode definition)
                {
                    services.Add(ReadService(name, definition));
                }
            }
        }

        return new ComposeFile(services, ReadReferencedVariables(content), IsInconclusive: false, ParseError: null);
    }

    private static ComposeService ReadService(string name, YamlMappingNode definition)
    {
        string? buildContext = null;
        string? dockerfile = null;

        switch (TryGet(definition, "build"))
        {
            // `build: ./server` — the shorthand, and the more common of the two.
            case YamlScalarNode { Value: { } shorthand } when !string.IsNullOrWhiteSpace(shorthand):
                buildContext = shorthand;
                break;
            case YamlMappingNode buildMap:
                buildContext = Scalar(TryGet(buildMap, "context"));
                dockerfile = Scalar(TryGet(buildMap, "dockerfile"));
                break;
        }

        return new ComposeService(
            name,
            buildContext,
            dockerfile,
            Image: Scalar(TryGet(definition, "image")),
            ExposedPorts: ReadSequence(definition, "expose")
                .Select(entry => int.TryParse(entry.Split('/')[0], out var port) ? port : 0)
                .Where(port => port > 0)
                .ToList(),
            HostPortMappings: ReadSequence(definition, "ports"),
            Volumes: ReadVolumes(definition),
            DependsOn: ReadDependsOn(definition),
            EnvironmentKeys: ReadEnvironmentKeys(definition),
            HasHealthcheck: TryGet(definition, "healthcheck") is not null,
            Command: Scalar(TryGet(definition, "command")));
    }

    /// <summary>
    /// Named volumes only. A bind mount (<c>./src:/app</c>) is a development convenience that does
    /// not survive a deploy, so reporting it as storage would be misleading.
    /// </summary>
    private static IReadOnlyList<ComposeVolume> ReadVolumes(YamlMappingNode definition) =>
        ReadSequence(definition, "volumes")
            .Select(entry => entry.Split(':'))
            .Where(parts => parts.Length >= 2 && !parts[0].StartsWith('.') && !parts[0].StartsWith('/'))
            .Select(parts => new ComposeVolume(parts[0], parts[1]))
            .ToList();

    /// <summary>
    /// `depends_on` is a list in the short form and a mapping in the long form
    /// (<c>db: { condition: service_healthy }</c>). Both name the same dependency.
    /// </summary>
    private static IReadOnlyList<string> ReadDependsOn(YamlMappingNode definition) =>
        TryGet(definition, "depends_on") switch
        {
            YamlSequenceNode sequence => sequence.Children.Select(Scalar).OfType<string>().ToList(),
            YamlMappingNode mapping => mapping.Children.Keys.Select(Scalar).OfType<string>().ToList(),
            _ => []
        };

    /// <summary>
    /// `environment` is either a mapping (<c>KEY: value</c>) or a list (<c>- KEY=value</c>).
    /// reel-hub's compose uses both, in the same file.
    /// </summary>
    private static IReadOnlyList<string> ReadEnvironmentKeys(YamlMappingNode definition) =>
        TryGet(definition, "environment") switch
        {
            YamlMappingNode mapping => mapping.Children.Keys.Select(Scalar).OfType<string>().ToList(),
            YamlSequenceNode sequence => sequence.Children
                .Select(Scalar)
                .OfType<string>()
                .Select(entry => entry.Split('=', 2)[0].Trim())
                .Where(key => key.Length > 0)
                .ToList(),
            _ => []
        };

    /// <summary>
    /// Read from the raw text rather than the parsed tree: a variable can appear in any value —
    /// a command, a healthcheck test, an image tag — and enumerating every place it might hide is
    /// how one gets missed.
    /// </summary>
    private static IReadOnlyList<string> ReadReferencedVariables(string content) =>
        VariableReference().Matches(content)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<string> ReadSequence(YamlMappingNode definition, string key) =>
        TryGet(definition, key) switch
        {
            YamlSequenceNode sequence => sequence.Children.Select(Scalar).OfType<string>().ToList(),
            YamlScalarNode { Value: { } single } when !string.IsNullOrWhiteSpace(single) => [single],
            _ => []
        };

    private static YamlNode? TryGet(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static string? Scalar(YamlNode? node) =>
        node is YamlScalarNode { Value: { } value } && !string.IsNullOrWhiteSpace(value) ? value : null;

    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex VariableReference();
}
