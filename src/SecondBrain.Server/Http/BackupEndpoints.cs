using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Operations;

namespace SecondBrain.Server.Http;

public static class BackupEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        foreach(var prefix in new[]{"","/v1"})
        {
            endpoints.MapPost(prefix+"/maintenance/backup",CreateAsync).WithMetadata(new EndpointPolicy("maintenance.backup",StepUp:true)).Produces<BackupCreated>(201);
            endpoints.MapGet(prefix+"/maintenance/backups",ListAsync).WithMetadata(new EndpointPolicy("maintenance.backups")).Produces<IReadOnlyList<BackupInfo>>();
            endpoints.MapGet(prefix+"/maintenance/backup/key",KeyAsync).WithMetadata(new EndpointPolicy("maintenance.backup.status")).Produces<BackupKeyStatus>();
            endpoints.MapGet(prefix+"/maintenance/backups/{name}",DownloadAsync).WithMetadata(new EndpointPolicy("maintenance.backup.read",StepUp:true));
            endpoints.MapPost(prefix+"/maintenance/export",ExportAsync).WithMetadata(new EndpointPolicy("maintenance.export",StepUp:true)).Produces(200, contentType:"application/x-tar");
        }
    }
    private static async Task<IResult> ExportAsync(HttpContext context, IExportOperations exports, IAuthRepository auth, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var result = await exports.ExportAsync(await Actor(context, auth, token), token);
            context.Response.Headers["X-SecondBrain-Export-Rows"] = result.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["X-SecondBrain-Export-Files"] = result.Files.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["X-SecondBrain-Export-Partial"] = result.PartialFiles.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Stream(result.Content, "application/x-tar", result.Name);
        }
        catch (AuthorityChangedException) { return Results.Problem(statusCode:403,title:"Session authority changed; sign in again."); }
        catch (BackupException error) { return Problem(error); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
        { return Results.Problem(statusCode:503,title:"Export could not finish. Your documents remain unchanged."); }
    }
    private static async Task<IResult> CreateAsync(HttpContext context,IBackupOperations backups,IAuthRepository auth,CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var created=await backups.CreateAsync(await Actor(context,auth,token),token);
            return Results.Created("/v1/maintenance/backups/"+Uri.EscapeDataString(created.Archive.Name),created);
        }
        catch(AuthorityChangedException){return Results.Problem(statusCode:403,title:"Session authority changed; sign in again.");}
        catch(BackupException error){return Problem(error);}
        catch(Exception error) when(error is IOException or InvalidDataException or InvalidOperationException)
        {return Results.Problem(statusCode:503,title:"Backup could not finish. Your documents remain unchanged; try again after pending work finishes.");}
    }
    private static async Task<IResult> ListAsync(HttpContext context,IBackupOperations backups,IAuthRepository auth,CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        try{return Results.Ok(await backups.ListAsync(await Actor(context,auth,token),token));}
        catch(AuthorityChangedException){return Results.Problem(statusCode:403,title:"Session authority changed; sign in again.");}
        catch(BackupException error){return Problem(error);}
    }
    private static async Task<IResult> KeyAsync(HttpContext context,IBackupOperations backups,IAuthRepository auth,CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { return Results.Ok(await backups.KeyStatusAsync(await Actor(context,auth,token),token)); }
        catch (AuthorityChangedException) { return Results.Problem(statusCode:403,title:"Session authority changed; sign in again."); }
    }
    private static async Task<IResult> DownloadAsync(string name,HttpContext context,IBackupOperations backups,IAuthRepository auth,CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        try{return Results.Stream(await backups.OpenAsync(name,await Actor(context,auth,token),token),"application/octet-stream",name);}
        catch(AuthorityChangedException){return Results.Problem(statusCode:403,title:"Session authority changed; sign in again.");}
        catch(BackupException error){return Problem(error);}
    }
    private static IResult Problem(BackupException error)=>Results.Problem(statusCode:error.Code switch
    {
        "backup_not_found"=>404,"backup_busy"=>409,_=>503
    },title:error.Code switch
    {
        "backup_not_found"=>"Backup not found.","backup_busy"=>"A backup is already running.",
        "backup_capacity"=>"There is not enough free space to create a backup safely.",
        "backup_sources_changed"=>"Sources changed while the backup was starting. Try again.",
        _=>"Backup is unavailable. Check the backup key and source folders on the server."
    },extensions:new Dictionary<string,object?>{{"code",error.Code}});
    private static async Task<IngestionAuthority> Actor(HttpContext context,IAuthRepository auth,CancellationToken token)
    {
        var current=(AuthenticatedCredential)context.Items[typeof(AuthenticatedCredential)]!;
        var credential=await auth.FindAsync(current.Id,token)??throw new AuthorityChangedException();
        return new(current.Id,current.Generation,current.AccountEpoch,credential.Name,current.Kind);
    }
}
