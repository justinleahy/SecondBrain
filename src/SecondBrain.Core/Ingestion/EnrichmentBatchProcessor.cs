using System.Text.Json;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Providers;

namespace SecondBrain.Core.Ingestion;

/// <summary>Persist each external intent before sending; reconcile ambiguous submissions without blind retries.</summary>
public sealed class EnrichmentBatchProcessor(IEnrichmentRepository enrichment,IEnrichmentBatchRepository batches,
    IProviderRegistry providers,IOptionsMonitor<SecondBrainOptions> options,TimeProvider clock)
{
    private static string Identity(SecondBrainOptions config,string provider,string model)=>ContentTypeSelection.Hash(
        JsonSerializer.Serialize(new {Provider=config.Providers[provider],Model=model}));
    public async Task<bool> ProcessAsync(CancellationToken token=default)
    {
        var batch=await batches.ReadPendingAsync(token);
        if(batch is null) return await Prepare(token);
        if(batch.State.NextAttempt>clock.GetUtcNow()) return false;
        var state=batch.State;
        IBatchProvider? provider=null;
        try {
            var config=options.CurrentValue;
            if(config.Providers.ContainsKey(state.Provider) && Identity(config,state.Provider,state.Model)==state.BindingIdentity)
                provider=providers.GetProvider(state.Provider).GetBatchLifecycleProvider(state.Model);
        } catch(InvalidOperationException) { }
        if(provider is null) {
            // Do not send an old remote id to a newly configured endpoint. Retain it as a cleanup audit.
            await batches.FinishAsync(batch with {State=state with {Invalidated=true,Error="provider_changed_remote_cleanup_pending"}},token);
            return true;
        }
        try {
            var valid=!state.Invalidated && options.CurrentValue.Enrichment.Enabled && options.CurrentValue.Enrichment.BatchBackfill
                && EnrichmentPolicy.Fingerprint(options.CurrentValue)==state.Fingerprint && batch.Items is not null;
            if(valid) foreach(var (item,index) in batch.Items!.Select((item,index)=>(item,index)))
                if(!(state.CompletedItems??[]).Contains(index) && !await enrichment.CheckAsync(item.Work,token) && !await enrichment.WasCompletedAsync(item.Work,token)) {valid=false;break;}
            if(!valid && !state.Invalidated)
                batch=await Save(batch,state with {Invalidated=true,Error="superseded"},token);
            state=batch.State;
            if(!state.Invalidated && state.Phase is "prepared" or "staged")
                provider=providers.GetRole(ModelRole.Enrich).Provider.GetBatchProvider(state.Model) ?? throw new InvalidOperationException("Batch submission is unavailable.");
            if(state.Invalidated && state.Receipt is null && state.Phase is "prepared" or "staged") {
                if(state.StagedFileId is { } file) await provider.DeleteFilesAsync(new(new("",file),"cancelled",true),token);
                await batches.FinishAsync(batch,token); return true;
            }
            switch(state.Phase) {
                case "prepared":
                    batch=await Save(batch,state with {Phase="staging"},token);
                    var staged=await provider.StageAsync(state.Id,batch.Items!.Select(x=>x.Request).ToArray(),token);
                    await Save(batch,batch.State with {Phase="staged",StagedFileId=staged},token);break;
                case "staging":
                    var recovered=await provider.FindStagedAsync(state.Id,token);
                    if(recovered is null) {
                        if(clock.GetUtcNow()-state.CreatedAt>TimeSpan.FromHours(1))
                            await batches.FinishAsync(batch with {State=state with {Invalidated=true,Error="input_upload_unconfirmed"}},token);
                        else await Delay(batch,"input_upload_unconfirmed",token);
                    } else await Save(batch,state with {Phase="staged",StagedFileId=recovered},token);
                    break;
                case "staged":
                    batch=await Save(batch,state with {Phase="submitting"},token);
                    var receipt=await provider.SubmitAsync(state.Id,state.StagedFileId,batch.Items!.Select(x=>x.Request).ToArray(),token);
                    await Save(batch,batch.State with {Phase="submitted",Receipt=receipt},token);break;
                case "submitting":
                    var found=await provider.FindAsync(state.Id,token);
                    if(found is null) {
                        if(clock.GetUtcNow()-state.CreatedAt>TimeSpan.FromHours(48))
                            await batches.FinishAsync(batch with {State=state with {Invalidated=true,Error="submission_unconfirmed"}},token);
                        else await Delay(batch,"submission_unconfirmed",token);
                    } else await Save(batch,state with {Phase="submitted",Receipt=found},token);
                    break;
                case "submitted":
                    if(state.Invalidated) await provider.CancelAsync(state.Receipt!,token);
                    var snapshot=await provider.PollAsync(state.Receipt!,token);
                    await Save(batch,state with {Phase=snapshot.Terminal?"collecting":"submitted",Snapshot=snapshot,NextAttempt=clock.GetUtcNow().AddSeconds(snapshot.Terminal?0:30)},token);
                    break;
                case "collecting":
                    if(state.Invalidated || batch.Items is null) {
                        await Save(batch,state with {Phase="cleanup"},token);break;
                    }
                    var results=await provider.CollectAsync(state.Snapshot!,batch.Items.Select(x=>x.Request.Id).ToArray(),token);
                    if(results.Count!=batch.Items.Length || results.Select(x=>x.Id).Distinct().Count()!=results.Count ||
                        results.Any(result=>!batch.Items.Any(item=>item.Request.Id==result.Id))) throw new InvalidDataException("Invalid batch result identities.");
                    await batches.SaveAsync(batch with {State=state with {Phase="publishing",NextAttempt=clock.GetUtcNow()},Results=results.ToArray()},token);break;
                case "publishing":
                    if(state.Invalidated || batch.Items is null || batch.Results is null) {
                        await Save(batch,state with {Phase="cleanup",Invalidated=true},token);break;
                    }
                    var completed=(state.CompletedItems??[]).ToHashSet();
                    foreach(var (item,index) in batch.Items.Select((item,index)=>(item,index))) {
                        if(completed.Contains(index)) continue;
                        var result=batch.Results!.Single(x=>x.Id==item.Request.Id);
                        try {
                            if(result.FailureCode is null && result.Text is not null)
                                await enrichment.PublishAsync(item.Work,EnrichmentProcessor.Parse(result.Text,state.Fingerprint),token);
                            else await enrichment.FailAsync(item.Work,result.FailureCode??"invalid_response",false,token);
                        } catch(Exception error) when(error is JsonException or ArgumentException or InvalidDataException or ManagedFileConflictException) {
                            await enrichment.FailAsync(item.Work,"invalid_response",false,token);
                        }
                        await batches.CompleteItemAsync(item,result.Usage,token);
                        completed.Add(index);
                        batch=await Save(batch,batch.State with {CompletedItems=completed.Order().ToArray()},token);
                    }
                    await Save(batch,batch.State with {Phase="cleanup"},token);break;
                case "cleanup":
                    await provider.DeleteFilesAsync(state.Snapshot??new(state.Receipt??new("",state.StagedFileId),"cancelled",true),token);
                    await batches.FinishAsync(batch,token);break;
                default: throw new InvalidOperationException("Unknown batch phase.");
            }
        }
        catch(OperationCanceledException) when(!token.IsCancellationRequested) {await DelayLatest("provider_timeout",token);}
        catch(ProviderRequestException error) when(!error.Retryable) {await RejectLatest("provider_rejected",token);}
        catch(InvalidDataException) {await RejectLatest("invalid_response",token);}
        catch(HttpRequestException) {await DelayLatest("provider_unavailable",token);}
        catch(SecondBrain.Core.Privacy.PrivacyPolicyException) {await DelayLatest("privacy_policy",token);}
        return true;
    }
    private async Task<bool> Prepare(CancellationToken token)
    {
        var config=options.CurrentValue;
        if(!config.Enrichment.Enabled || !config.Enrichment.BatchBackfill || !providers.IsCurrentConfiguration(config)) return false;
        var role=providers.GetRole(ModelRole.Enrich);
        if(!role.Model.Capabilities.Batch || role.Provider.GetBatchProvider(role.Model.Model) is null) return false;
        var fingerprint=EnrichmentPolicy.Fingerprint(config);var items=new List<EnrichmentBatchItem>();
        try {
            for(var i=0;i<16;i++) {
                var work=await enrichment.ClaimBackfillAsync(fingerprint,token);if(work is null) break;
                if(work.CachedResult is { } cached) {await enrichment.PublishAsync(work,cached,token);continue;}
                var prepared=EnrichmentPrompt.Create(work,role);items.Add(new(work,prepared.Request,prepared.ReservedTokens));
            }
            if(items.Count==0) return false;
            var state=new EnrichmentBatchState(Guid.NewGuid().ToString("N"),role.Provider.ProviderName,role.Model.Model,
                Identity(config,role.Provider.ProviderName,role.Model.Model),fingerprint,"prepared",clock.GetUtcNow(),CreatedAt:clock.GetUtcNow());
            if(await batches.PrepareAsync(state,items.ToArray(),token) is not null) return true;
            foreach(var item in items) await enrichment.FailAsync(item.Work,"batch_deferred",true,token);
        } catch(ModelBudgetException) {foreach(var item in items) await enrichment.FailAsync(item.Work,"budget",true,token);}
        return items.Count>0;
    }
    private Task<EnrichmentBatch> Save(EnrichmentBatch batch,EnrichmentBatchState state,CancellationToken token) =>
        batches.SaveAsync(batch with {State=state},token);
    private async Task Delay(EnrichmentBatch batch,string error,CancellationToken token) =>
        _=await Save(batch,batch.State with {Error=error,NextAttempt=clock.GetUtcNow().AddMinutes(1)},token);
    private async Task DelayLatest(string error,CancellationToken token) {
        var latest=await batches.ReadPendingAsync(token);if(latest is not null) await Delay(latest,error,token);
    }
    private async Task RejectLatest(string error,CancellationToken token) {
        var latest=await batches.ReadPendingAsync(token);
        if(latest is null) return;
        if(latest.State.Phase=="cleanup") {
            await batches.FinishAsync(latest with {State=latest.State with {Invalidated=true,Error="remote_cleanup_pending"}},token);return;
        }
        await Save(latest,latest.State with {Phase="cleanup",Invalidated=true,Error=error,NextAttempt=clock.GetUtcNow()},token);
    }
}
