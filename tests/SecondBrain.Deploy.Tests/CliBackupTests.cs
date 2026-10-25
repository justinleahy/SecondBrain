using System.Net;
using System.Net.Http.Json;
using SecondBrain.Cli;
using SecondBrain.Cli.Commands;
using SecondBrain.Cli.Credentials;
using SecondBrain.Core.Operations;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class CliBackupTests
{
    private const string Name="backup-20261024T1200000000000Z-0123456789abcdef0123456789abcdef.sbk";
    [Fact]
    public async Task BackupDownloadsPrivatelyThroughASteppedUpSession()
    {
        var folder=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"cli-backup-"+Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using var handler=new Handler();var output=new StringWriter();var commands=Commands(handler);
            Assert.Equal(0,await SecondBrain.Cli.Program.RunAsync(["maintenance","backup","--to",folder,"--json"],new CliServices(false){Knowledge=commands},output,new StringWriter()));
            Assert.Equal(handler.Bytes,await File.ReadAllBytesAsync(Path.Combine(folder,Name)));
            if(!OperatingSystem.IsWindows())Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(Path.Combine(folder,Name)));
            Assert.Contains("POST /auth/step-up",handler.Sent);Assert.Contains("GET /v1/maintenance/backups/"+Name,handler.Sent);Assert.Contains("POST /auth/logout",handler.Sent);
            Assert.DoesNotContain("fixture-password",output.ToString());Assert.Single(Directory.GetFiles(folder));
        }
        finally{Directory.Delete(folder,true);}
    }
    [Fact]
    public async Task IncompleteDownloadLeavesNoPublishedFile()
    {
        var folder=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"cli-backup-"+Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using var handler=new Handler{WrongLength=true};
            Assert.NotEqual(0,await SecondBrain.Cli.Program.RunAsync(["maintenance","backup","--to",folder],new CliServices(false){Knowledge=Commands(handler)},new StringWriter(),new StringWriter()));
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally{Directory.Delete(folder,true);}
    }
    [Fact]
    public async Task ExistingDestinationIsNeverOverwritten()
    {
        var folder=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"cli-backup-"+Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var path=Path.Combine(folder,"saved.sbk");await File.WriteAllTextAsync(path,"keep");using var handler=new Handler();
            Assert.NotEqual(0,await SecondBrain.Cli.Program.RunAsync(["maintenance","backup","--to",path],new CliServices(false){Knowledge=Commands(handler)},new StringWriter(),new StringWriter()));
            Assert.Equal("keep",await File.ReadAllTextAsync(path));
        }
        finally{Directory.Delete(folder,true);}
    }
    private static HttpDaemonCommands Commands(Handler handler)
    {var result=new HttpDaemonCommands(()=>new Credentials(),(_,_)=>Task.FromResult("fixture-password"),()=>new HttpClient(handler,false));result.Configure(()=>throw new InvalidOperationException(),"http://daemon.test","test");return result;}
    private sealed class Credentials:ICredentialStore
    {public Task StoreAsync(Uri origin,string name,string secret,CancellationToken token=default)=>Task.CompletedTask;public Task<string?> ReadAsync(Uri origin,string name,CancellationToken token=default)=>Task.FromResult<string?>(null);}
    private sealed class Handler:HttpMessageHandler
    {
        internal readonly byte[] Bytes="synthetic encrypted archive"u8.ToArray();internal bool WrongLength;internal List<string> Sent{get;}=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var path=request.RequestUri!.PathAndQuery;Sent.Add(request.Method+" "+path);
            if(path=="/v1/maintenance/backups/"+Name)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Bytes)});
            object value=path switch
            {
                "/auth/antiforgery"=>new{headerName="X-CSRF-TOKEN",token="fixture"},
                "/v1/maintenance/backup"=>new BackupCreated(new(Name,Bytes.Length+(WrongLength?1:0),DateTimeOffset.UtcNow,false),2,2,0),
                _=>new{id="session"}
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(value,value.GetType())});
        }
    }
}
