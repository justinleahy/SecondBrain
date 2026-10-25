using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Providers;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Usage;

namespace SecondBrain.Storage.Ingestion;

public sealed partial class SqliteEnrichmentRepository
{
    public Task<EnrichmentBatch?> ReadPendingAsync(CancellationToken token = default) => state.QueueWriteAsync(async (c,t,ct) => {
        var row=await c.QuerySingleOrDefaultAsync<BatchRow>(Sql("""
            SELECT b.metadata_json Metadata,p.bytes Items,r.bytes Results
            FROM batch_jobs b LEFT JOIN payloads p ON p.id=b.request_payload_id AND p.redacted_at IS NULL
            LEFT JOIN payloads r ON r.id=b.result_payload_id AND r.redacted_at IS NULL
            WHERE b.kind='enrichment' AND b.status NOT IN ('done','failed','cancelled') ORDER BY b.created_at,b.id LIMIT 1
            """,null,t,ct));
        return row is null?null:new EnrichmentBatch(JsonSerializer.Deserialize<EnrichmentBatchState>(row.Metadata,Json)!,
            row.Items is null?null:JsonSerializer.Deserialize<EnrichmentBatchItem[]>(row.Items,Json),
            row.Results is null?null:JsonSerializer.Deserialize<BatchChatResult[]>(row.Results,Json));
    },token).AsTask();

    public Task<EnrichmentBatch?> PrepareAsync(EnrichmentBatchState batch, EnrichmentBatchItem[] items, CancellationToken token = default) => state.QueueWriteAsync(async (c,t,ct) => {
        if(!options.CurrentValue.Enrichment.BatchBackfill || !PolicyCurrent(batch.Fingerprint) || items.Length is <1 or >16) return null;
        if(await c.ExecuteScalarAsync<int>(Sql("SELECT COUNT(*) FROM batch_jobs WHERE kind='enrichment' AND status NOT IN ('done','failed','cancelled')",null,t,ct))!=0) return null;
        foreach(var item in items)
            if(item.Work.CachedResult is not null || item.Work.Fingerprint!=batch.Fingerprint || !await Check(c,t,item.Work,ct)) return null;
        var reserved=new List<EnrichmentBatchItem>();
        foreach(var item in items) {
            var actor=item.Work.Actor;
            var usageId=await SqliteModelUsage.ReserveInTransactionAsync(c,t,"native-batch/"+batch.Id+"/"+item.Work.JobId,
                batch.Provider,batch.Model,"enrich",item.ReservedTokens,actor?.CredentialId,actor?.CredentialGeneration,actor?.AccountEpoch,
                item.Work.JobId,null,clock.GetUtcNow(),options.CurrentValue.Budgets.DailyTokens,options.CurrentValue.Limits.PerCredential.ModelTokensPerHour,ct);
            reserved.Add(item with {UsageId=usageId});
            await c.ExecuteAsync(Sql("UPDATE jobs SET payload_json=json_set(payload_json,'$.batchId',@batch),lease_until='9999-12-31T23:59:59.0000000+00:00' WHERE id=@id",
                new {batch=batch.Id,id=item.Work.JobId},t,ct));
        }
        var owned=reserved.ToArray(); var bytes=JsonSerializer.SerializeToUtf8Bytes(owned,Json);
        if(bytes.Length>4*1024*1024) throw new InvalidOperationException("Batch input exceeds its local bound.");
        var payload=Ulid.NewUlid().ToString(); var now=AuthTime.Format(clock.GetUtcNow());
        await c.ExecuteAsync(Sql("""
            INSERT INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES(@payload,@hash,@bytes,@refs,@now);
            INSERT INTO payload_refs(payload_id,referrer_kind,referrer_id) VALUES(@payload,'batch_job',@id);
            INSERT INTO batch_jobs(id,provider,remote_id,kind,status,request_payload_id,metadata_json,created_at,updated_at)
            VALUES(@id,@provider,'','enrichment','prepared',@payload,@metadata,@now,@now);
            """,new {payload,hash=Convert.ToHexStringLower(SHA256.HashData(bytes)),bytes,refs=JsonSerializer.Serialize(owned.Select(x=>x.Work.DocumentId).Distinct()),
                now,id=batch.Id,provider=batch.Provider,metadata=JsonSerializer.Serialize(batch,Json)},t,ct));
        return new EnrichmentBatch(batch,owned);
    },token).AsTask();

    public Task<EnrichmentBatch> SaveAsync(EnrichmentBatch batch,CancellationToken token=default) => state.QueueWriteAsync(async(c,t,ct)=> {
        var before=await c.QuerySingleAsync<BatchStored>(Sql("SELECT metadata_json Metadata,result_payload_id ResultPayload FROM batch_jobs WHERE id=@id",new {id=batch.State.Id},t,ct));
        var saved=JsonSerializer.Deserialize<EnrichmentBatchState>(before.Metadata,Json)!;
        if(saved.Version!=batch.State.Version || saved.Provider!=batch.State.Provider || saved.Model!=batch.State.Model || saved.BindingIdentity!=batch.State.BindingIdentity)
            throw new InvalidOperationException("Batch state changed.");
        var next=batch.State with {Version=batch.State.Version+1}; var resultPayload=before.ResultPayload;
        if(batch.Results is not null && resultPayload is null) {
            var bytes=JsonSerializer.SerializeToUtf8Bytes(batch.Results,Json);
            if(bytes.Length>4*1024*1024) throw new InvalidOperationException("Batch output exceeds its local bound.");
            resultPayload=Ulid.NewUlid().ToString();
            await c.ExecuteAsync(Sql("""
                INSERT INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES(@payload,@hash,@bytes,@refs,@now);
                INSERT INTO payload_refs(payload_id,referrer_kind,referrer_id) VALUES(@payload,'batch_job',@id);
                """,new {payload=resultPayload,hash=Convert.ToHexStringLower(SHA256.HashData(bytes)),bytes,
                    refs=JsonSerializer.Serialize(batch.Items?.Select(x=>x.Work.DocumentId).Distinct()??[]),now=AuthTime.Format(clock.GetUtcNow()),id=next.Id},t,ct));
        }
        await c.ExecuteAsync(Sql("UPDATE batch_jobs SET status=@phase,remote_id=@remote,metadata_json=@metadata,result_payload_id=@result,updated_at=@now WHERE id=@id",
            new {phase=next.Phase,remote=next.Receipt?.Id??"",metadata=JsonSerializer.Serialize(next,Json),result=resultPayload,now=AuthTime.Format(clock.GetUtcNow()),id=next.Id},t,ct));
        return batch with {State=next};
    },token).AsTask();

    public Task CompleteItemAsync(EnrichmentBatchItem item,ModelUsageTotals? totals,CancellationToken token=default) => state.QueueWriteAsync(async(c,t,ct)=> {
        await SqliteModelUsage.SettleInTransactionAsync(c,t,item.UsageId,totals,ct);return 0;
    },token).AsTask();

    public Task FinishAsync(EnrichmentBatch batch,CancellationToken token=default) => state.QueueWriteAsync(async(c,t,ct)=> {
        var id=batch.State.Id;
        var failed=batch.State.Error is "input_upload_unconfirmed" or "submission_unconfirmed" or "invalid_response" or "provider_rejected" or "remote_cleanup_pending";
        var reservations=await c.QueryAsync<long>(Sql("SELECT u.id FROM usage u JOIN usage_calls uc ON uc.usage_id=u.id WHERE uc.call_id LIKE @prefix AND u.settled=0",new {prefix="native-batch/"+id+"/%"},t,ct));
        foreach(var reservation in reservations) await SqliteModelUsage.SettleInTransactionAsync(c,t,reservation,null,ct);
        await c.ExecuteAsync(Sql("""
            UPDATE jobs SET status=@jobStatus,lease_until=NULL,error=@reason WHERE status='running' AND json_extract(payload_json,'$.batchId')=@id;
            UPDATE batch_jobs SET status=@phase,metadata_json=@metadata,request_payload_id=NULL,result_payload_id=NULL,updated_at=@now WHERE id=@id;
            DELETE FROM payload_refs WHERE referrer_kind='batch_job' AND referrer_id=@id;
            """,new {id,reason=batch.State.Error??"batch_incomplete",jobStatus=failed?"failed":"cancelled",phase=failed?"failed":batch.State.Invalidated?"cancelled":"done",metadata=JsonSerializer.Serialize(batch.State,Json),now=AuthTime.Format(clock.GetUtcNow())},t,ct));
        return 0;
    },token).AsTask();

    private sealed class BatchRow { public string Metadata {get;set;}="";public byte[]? Items {get;set;} public byte[]? Results {get;set;} }
    private sealed class BatchStored {public string Metadata {get;set;}="";public string? ResultPayload {get;set;} }
}
