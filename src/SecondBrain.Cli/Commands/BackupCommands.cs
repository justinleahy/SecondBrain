using System.Net.Http.Json;
using SecondBrain.Core.Operations;

namespace SecondBrain.Cli.Commands;

public sealed partial class HttpDaemonCommands
{
    public async Task<CliResult> BackupAsync(string? destination,CancellationToken token)
    {
        using var client=await OpenAsync(apiKey:false,token);client.Timeout=TimeSpan.FromHours(1);
        var password=await readPassword("backup",token);await LoginAsync(client,password,token);
        try
        {
            await StepUpAsync(client,password,token);
            using var response=await SendAsync(client,HttpMethod.Post,"/v1/maintenance/backup",new{},token);
            var created=await response.Content.ReadFromJsonAsync<BackupCreated>(Json,token)??throw new CliPreconditionException("The daemon returned no backup information.");
            string? path=null;
            if(destination is not null)
            {
                var name=created.Archive.Name;
                if(name!=Path.GetFileName(name)||!name.EndsWith(".sbk",StringComparison.Ordinal)||name.Contains('/')||name.Contains('\\'))
                    throw new CliPreconditionException("The daemon returned an invalid archive name.");
                path=Path.GetFullPath(Directory.Exists(destination)?Path.Combine(destination,name):destination);
                if(File.Exists(path)||Directory.Exists(path))throw new CliPreconditionException("The backup destination already exists.");
                var parent=Path.GetDirectoryName(path)!;if(!Directory.Exists(parent))throw new CliPreconditionException("The backup destination directory does not exist.");
                var temporary=Path.Combine(parent,".backup-"+Guid.NewGuid().ToString("N")+".tmp");
                try
                {
                    using var download=await client.GetAsync("/v1/maintenance/backups/"+Uri.EscapeDataString(name),HttpCompletionOption.ResponseHeadersRead,token);
                    if(!download.IsSuccessStatusCode)throw new DaemonCommandException(new(CliExitCode.PreconditionFailed,"daemon-rejected","The daemon rejected the backup download."));
                    var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,Options=FileOptions.Asynchronous};
                    if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
                    await using(var output=new FileStream(temporary,options))
                    {
                        await using var input=await download.Content.ReadAsStreamAsync(token);var buffer=new byte[65536];long total=0;
                        while(true){var read=await input.ReadAsync(buffer,token);if(read==0)break;total+=read;if(total>created.Archive.Bytes)throw new IOException("Backup download size changed.");await output.WriteAsync(buffer.AsMemory(0,read),token);}
                        if(total!=created.Archive.Bytes)throw new IOException("Backup download was incomplete.");output.Flush(true);
                    }
                    File.Move(temporary,path,false);
                }
                finally{File.Delete(temporary);}
            }
            return new(created.Archive.Partial?CliExitCode.PreconditionFailed:CliExitCode.Ok,created.Archive.Partial?"backup-partial":"ok",
                created.Archive.Partial?"An incomplete backup was created. Review unavailable source files before relying on it.":"Encrypted backup created.",new{backup=created,localPath=path});
        }
        finally{await LogoutAsync(client,token);}
    }
}
