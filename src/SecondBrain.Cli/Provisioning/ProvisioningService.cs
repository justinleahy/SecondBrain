using SecondBrain.Cli.Commands;
using YamlDotNet.RepresentationModel;

namespace SecondBrain.Cli.Provisioning;

/// <summary>Root-run provisioning only; it never initializes stores or issues application credentials.</summary>
public sealed class ProvisioningService(IProvisionPlatform? platform = null)
{
    private readonly IProvisionPlatform _platform = platform ?? new LinuxProvisionPlatform();
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode GroupReadableDirectory = PrivateDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public ProvisionResult Provision(ProvisionOptions options)
    {
        Validate(options);
        if (!options.DryRun && !_platform.IsRoot) throw new CliPreconditionException("brain init --provision must run as root; use --dry-run to inspect the plan.");
        if (!options.DryRun && OperatingSystem.IsWindows()) throw new CliPreconditionException("Provisioning requires a Unix host.");
        var actions = new List<ProvisionAction>();
        void Action(string kind, string target, string detail, Action execute)
        {
            actions.Add(new(kind, target, detail));
            if (!options.DryRun) execute();
        }
        if (!options.SkipUsers)
        {
            Action("group", options.DaemonUser, $"gid={options.DaemonGid}", () => _platform.EnsureGroup(options.DaemonUser, options.DaemonGid));
            foreach (var (name, uid) in new[] { (options.DaemonUser, options.DaemonUid), (options.ExtractorUser, options.ExtractorUid), (options.SyncUser, options.SyncUid) })
                Action("user", name, $"uid={uid} group={options.DaemonUser}", () => _platform.EnsureUser(name, uid, options.DaemonUser));
        }
        void DirectoryAction(string path, UnixFileMode mode, uint uid, uint gid)
        {
            Action("directory", path, $"mode={Convert.ToString((int)mode, 8)} uid={uid} gid={gid}", () =>
            {
                RejectSymlink(path);
                Directory.CreateDirectory(path, mode);
                File.SetUnixFileMode(path, mode);
                _platform.SetOwner(path, uid, gid);
            });
        }
        DirectoryAction(options.DataRoot, PrivateDirectory, options.DaemonUid, options.DaemonGid);
        DirectoryAction(Path.Combine(options.DataRoot, "keyring"), PrivateDirectory, options.DaemonUid, options.DaemonGid);
        DirectoryAction(options.IncomingRoot, GroupReadableDirectory, options.SyncUid, options.DaemonGid);
        DirectoryAction(options.ConfigDirectory, GroupReadableDirectory, 0, options.DaemonGid);
        // Directories require execute for traversal; the 0600 secrets requirement applies to files.
        DirectoryAction(Path.Combine(options.ConfigDirectory, "secrets"), PrivateDirectory, options.DaemonUid, options.DaemonGid);
        DirectoryAction(options.RuntimeDirectory, GroupReadableDirectory | UnixFileMode.GroupWrite | UnixFileMode.SetGroup, options.DaemonUid, options.DaemonGid);
        var templateRoot = FindTemplateRoot(options.TemplatesDirectory);
        var configTemplate = Path.Combine(templateRoot, "provision", options.Deployment == "compose" ? "config.compose.yaml" : "config.yaml");
        if (!File.Exists(configTemplate)) configTemplate = Path.Combine(templateRoot, "provision", "config.yaml");
        var config = File.Exists(configTemplate) ? Rewrite(File.ReadAllText(configTemplate), options) : DefaultConfiguration(options);
        var configPath = Path.Combine(options.ConfigDirectory, "config.yaml");
        Action("file", configPath, "create if absent; mode=600; daemon-owned", () =>
        {
            WriteIfAbsent(configPath, config, PrivateFile);
            File.SetUnixFileMode(configPath, PrivateFile);
            _platform.SetOwner(configPath, options.DaemonUid, options.DaemonGid);
        });
        if (!options.DryRun)
        {
            foreach (var secret in Directory.EnumerateFiles(Path.Combine(options.ConfigDirectory, "secrets")))
            {
                RejectSymlink(secret);
                File.SetUnixFileMode(secret, PrivateFile);
                _platform.SetOwner(secret, options.DaemonUid, options.DaemonGid);
            }
        }
        if (options.Deployment == "systemd")
        {
            foreach (var name in new[] { "secondbrain.service", "secondbrain-extractor.socket", "secondbrain-extractor.service" })
                InstallTemplate(Path.Combine(templateRoot, "systemd", name), Path.Combine(options.UnitsDirectory, name), options, Action);
            InstallTemplate(Path.Combine(templateRoot, "provision", "secondbrain.tmpfiles.conf"), Path.Combine(options.TmpFilesDirectory, "secondbrain.conf"), options, Action);
        }
        else if (options.Deployment == "compose")
        {
            var dockerDirectory = Path.Combine(templateRoot, "docker");
            var compose = Path.Combine(dockerDirectory, "compose.yaml");
            if (!File.Exists(compose)) compose = Path.Combine(templateRoot, "compose.yaml");
            InstallTemplate(compose, Path.Combine(options.ComposeDirectory, "compose.yaml"), options, Action);
            foreach (var (sourceName, installedName) in new[] { ("compose.env.example", ".env.example"), ("cloudflared.yaml", "cloudflared.yaml") })
            {
                var source = Path.Combine(templateRoot, "provision", sourceName);
                if (File.Exists(source)) InstallTemplate(source, Path.Combine(options.ComposeDirectory, installedName), options, Action);
            }
            if (Directory.Exists(dockerDirectory))
                foreach (var source in Directory.EnumerateFiles(dockerDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    if (source != compose && Path.GetFileName(source) != "Dockerfile")
                        InstallTemplate(source, Path.Combine(options.ComposeDirectory, Path.GetRelativePath(dockerDirectory, source)), options, Action);
        }
        return new(options.DryRun, actions);
    }

    private void InstallTemplate(string source, string destination, ProvisionOptions options, Action<string, string, string, Action> action)
    {
        if (!File.Exists(source)) throw new CliPreconditionException($"Missing deployment template {source}; provide --templates pointing to the installed deploy tree.");
        var text = Rewrite(File.ReadAllText(source), options);
        if (options.Deployment == "compose" && Path.GetFileName(destination) == "compose.yaml") text = RuntimeCompose(text);
        action("template", destination, "create if absent; mode=644; root-owned", () =>
        {
            RejectSymlink(Path.GetDirectoryName(destination)!);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            WriteIfAbsent(destination, text, PrivateFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            _platform.SetOwner(destination, 0, 0);
        });
    }

    private static void WriteIfAbsent(string path, string content, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix provisioning permissions are unavailable.");
        RejectSymlink(path);
        if (File.Exists(path)) return;
        using var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = mode });
        using var writer = new StreamWriter(stream);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static string FindTemplateRoot(string root) => Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) == "provision" ? Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))! : root;
    private static string RuntimeCompose(string text)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root ||
            !root.Children.TryGetValue(new YamlScalarNode("services"), out var value) || value is not YamlMappingNode services)
            throw new CliPreconditionException("Compose template must define one services mapping.");
        foreach (var service in services.Children.Values.OfType<YamlMappingNode>()) service.Children.Remove(new YamlScalarNode("build"));
        var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        yaml.Save(writer, assignAnchors: false);
        return writer.ToString();
    }
    private static string Rewrite(string text, ProvisionOptions options) => text
        .Replace("/srv/secondbrain-incoming", options.IncomingRoot, StringComparison.Ordinal)
        .Replace("/srv/secondbrain", options.DataRoot, StringComparison.Ordinal)
        .Replace("/etc/secondbrain", options.ConfigDirectory, StringComparison.Ordinal)
        .Replace("/run/secondbrain", options.RuntimeDirectory, StringComparison.Ordinal);

    private static string DefaultConfiguration(ProvisionOptions options)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        return $"data_root: {Quote(options.DataRoot)}\nserver:\n  listeners:\n    - {{ scheme: http, bind: 127.0.0.1, port: 7171 }}\n  hosts: [localhost, 127.0.0.1]\n  origins: [http://localhost:7171, http://127.0.0.1:7171]\nproviders: {{}}\nmodels: {{}}\nprivacy:\n  local_only: true\nsources:\n  incoming_root: {Quote(options.IncomingRoot)}\n  allowed_roots: [{Quote(options.IncomingRoot)}]\n";
    }

    private static void Validate(ProvisionOptions options)
    {
        foreach (var path in new[] { options.DataRoot, options.IncomingRoot, options.ConfigDirectory, options.RuntimeDirectory, options.UnitsDirectory, options.TmpFilesDirectory, options.ComposeDirectory, options.TemplatesDirectory })
            if (!Path.IsPathFullyQualified(path) || path.Contains('\n') || path.Contains('\r') || path.Contains('\0')) throw new CliUsageException("Provisioning paths must be absolute and contain no control characters.");
        if (options.Deployment is not ("systemd" or "compose" or "none")) throw new CliUsageException("--deployment must be systemd, compose, or none.");
        var data = Path.GetFullPath(options.DataRoot).TrimEnd(Path.DirectorySeparatorChar);
        var incoming = Path.GetFullPath(options.IncomingRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (data == incoming || incoming.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.Ordinal) || data.StartsWith(incoming + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new CliUsageException("The data and incoming roots must be separate trees.");
        if (options.DaemonUid == 0 || options.ExtractorUid == 0 || options.SyncUid == 0 || options.DaemonGid == 0 || options.DaemonUid == options.ExtractorUid || options.DaemonUid == options.SyncUid || options.ExtractorUid == options.SyncUid)
            throw new CliUsageException("Daemon, extractor, and sync identities must use separate non-root UIDs and a non-root daemon GID.");
    }

    private static void RejectSymlink(string path)
    {
        FileSystemInfo item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (item.LinkTarget is not null) throw new CliPreconditionException($"Refusing to provision a symbolic link: {path}.");
    }
}
