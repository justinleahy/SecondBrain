using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SecondBrain.Cli;
using SecondBrain.Cli.Commands;
using SecondBrain.Cli.Credentials;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class CliExportTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 4)]
    public async Task ExportDownloadsPrivatelyAndReportsPartialFiles(bool partial, int exit)
    {
        var root = Directory.CreateTempSubdirectory("cli-export-").FullName;
        try
        {
            using var handler = new Handler { Partial = partial };
            var result = await Run(handler, Path.Combine(root, "export.tar"));
            Assert.Equal(exit, result);
            Assert.Equal(handler.Bytes, await File.ReadAllBytesAsync(Path.Combine(root, "export.tar")));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, "export.tar")));
            Assert.Contains("POST /auth/step-up", handler.Requests); Assert.Contains("POST /auth/logout", handler.Requests);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExistingDestinationAndTruncatedDownloadsDoNotOverwriteOrLeavePartialFiles()
    {
        var root = Directory.CreateTempSubdirectory("cli-export-").FullName;
        try
        {
            var path = Path.Combine(root, "export.tar"); await File.WriteAllTextAsync(path, "existing");
            using var handler = new Handler { Truncated = true };
            Assert.Equal(4, await Run(handler, path)); Assert.Empty(handler.Requests);
            Assert.Equal("existing", await File.ReadAllTextAsync(path)); File.Delete(path);
            Assert.NotEqual(0, await Run(handler, path)); Assert.Empty(Directory.EnumerateFiles(root));
            Assert.Contains("POST /auth/logout", handler.Requests);
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task<int> Run(Handler handler, string destination)
    {
        var commands = new HttpDaemonCommands(() => new Credentials(), (_, _) => Task.FromResult("fixture-password"), () => new HttpClient(handler, false));
        commands.Configure(() => throw new InvalidOperationException(), "http://daemon.test", "fixture");
        return await Cli.Program.RunAsync(["maintenance", "export", "--to", destination, "--json"], new CliServices(false) { Knowledge = commands }, new StringWriter(), new StringWriter());
    }
    private sealed class Credentials : ICredentialStore
    {
        public Task StoreAsync(Uri origin, string name, string secret, CancellationToken token = default) => Task.CompletedTask;
        public Task<string?> ReadAsync(Uri origin, string name, CancellationToken token = default) => Task.FromResult<string?>(null);
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal bool Partial, Truncated;
        internal byte[] Bytes { get; } = "synthetic portable archive"u8.ToArray();
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/v1/maintenance/export")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = "export.tar" };
                response.Content.Headers.ContentLength = Bytes.Length + (Truncated ? 1 : 0);
                response.Headers.Add("X-SecondBrain-Export-Partial", Partial ? "1" : "0"); return Task.FromResult(response);
            }
            object body = request.RequestUri.AbsolutePath == "/auth/antiforgery" ? new { headerName = "X-CSRF-TOKEN", token = "fixture" } : new { id = "session" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }
    }
}
