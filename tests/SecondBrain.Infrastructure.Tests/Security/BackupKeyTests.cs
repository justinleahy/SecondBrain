using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Operations;
using SecondBrain.Infrastructure.Security;
using Xunit;

namespace SecondBrain.Infrastructure.Tests.Security;

public sealed class BackupKeyTests
{
    [Fact]
    public async Task ReadsAPrivateExternalKeyAndClearsReturnedBytesAtDisposal()
    {
        var root=Path.Combine(Path.GetTempPath(),"sb-key-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var data=Directory.CreateDirectory(Path.Combine(root,"data")).FullName;var path=Path.Combine(root,"backup.key");
            var bytes=RandomNumberGenerator.GetBytes(32);await File.WriteAllBytesAsync(path,bytes);
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            var provider=new FileBackupKeyProvider(new Monitor(new SecondBrainOptions{DataRoot=data,Backup=new(){KeyFile=path}}),_=>null);
            var key=await provider.ReadAsync();var held=key.Bytes;Assert.Equal(bytes,held.ToArray());key.Dispose();
            Assert.All(held.ToArray(),b=>Assert.Equal(0,b));Assert.Throws<ObjectDisposedException>(()=>key.Bytes);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact]
    public async Task RejectsLinksPublicModesAndKeysInsideDataRoot()
    {
        if (OperatingSystem.IsWindows()) return;
        var root=Path.Combine(Path.GetTempPath(),"sb-key-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var data=Directory.CreateDirectory(Path.Combine(root,"data")).FullName;var path=Path.Combine(root,"backup.key");await File.WriteAllBytesAsync(path,new byte[32]);
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.OtherRead);
            var options=new SecondBrainOptions{DataRoot=data,Backup=new(){KeyFile=path}};var provider=new FileBackupKeyProvider(new Monitor(options),_=>null);
            Assert.Equal("backup_key_permissions_or_size",(await Assert.ThrowsAsync<BackupException>(async()=>await provider.ReadAsync())).Code);
            File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            var link=Path.Combine(root,"link.key");File.CreateSymbolicLink(link,path);options.Backup.KeyFile=link;
            Assert.Equal("backup_key_location",(await Assert.ThrowsAsync<BackupException>(async()=>await provider.ReadAsync())).Code);
            options.Backup.KeyFile=Path.Combine(data,"backup.key");await File.WriteAllBytesAsync(options.Backup.KeyFile,new byte[32]);
            Assert.Equal("backup_key_location",(await Assert.ThrowsAsync<BackupException>(async()=>await provider.ReadAsync())).Code);
        }
        finally{Directory.Delete(root,true);}
    }
    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public async Task EnvironmentKeysMustDecodeToExactly32Bytes(int length)
    {
        var provider=new FileBackupKeyProvider(new Monitor(new()),_=>Convert.ToBase64String(new byte[length]));
        Assert.Equal("backup_key_invalid",(await Assert.ThrowsAsync<BackupException>(async()=>await provider.ReadAsync())).Code);
    }
    private sealed class Monitor(SecondBrainOptions options):IOptionsMonitor<SecondBrainOptions>
    {public SecondBrainOptions CurrentValue=>options;public SecondBrainOptions Get(string? name)=>options;public IDisposable? OnChange(Action<SecondBrainOptions,string?> listener)=>null;}
}
