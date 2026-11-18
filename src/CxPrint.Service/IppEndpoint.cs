using System.Collections.Concurrent;
using CxPrint.Core;
using CxPrint.Windows;
using Microsoft.AspNetCore.Http.Features;
using static CxPrint.Core.IppCodec;

namespace CxPrint.Service;

public sealed class IppEndpoint(StateStore state,PrinterMonitor printers,JobManager jobs,DiscoveryWorker discovery,
    DigestAuthentication digest,ILogger<IppEndpoint> logger)
{
    private readonly SemaphoreSlim _requests=new(16,16);
    private readonly ConcurrentDictionary<string,(int Count,DateTimeOffset Until)> _failures=new();
    private readonly DateTimeOffset _started=DateTimeOffset.UtcNow;
    private readonly ConcurrentQueue<string> _recentRequests=new();
    public IReadOnlyList<string> RecentRequests=>_recentRequests.ToArray();
    private sealed class RequestTrace
    {
        public IppMessage? Request;
        public IppMessage? Response;
        public bool Failed;
    }
    public async Task HandleAsync(HttpContext context)
    {
        if (!await _requests.WaitAsync(0,context.RequestAborted)) { context.Response.StatusCode=503; context.Response.Headers.RetryAfter="5"; return; }
        var trace=new RequestTrace();
        try { await Process(context,trace); }
        catch { trace.Failed=true; throw; }
        finally
        {
            _requests.Release();
            var operation=trace.Request is {} request ? $"0x{request.Code:x4} id={request.RequestId}" : "unparsed";
            var result=trace.Response is {} response ? $"0x{response.Code:x4}" : "none";
            var detail=(trace.Response?.Text("status-message") ?? "").Replace('\r',' ').Replace('\n',' ');
            if (detail.Length>200) detail=detail[..200];
            var entry=$"{DateTimeOffset.Now:O} {context.Connection.RemoteIpAddress} op={operation} HTTP={(trace.Failed ? "exception" : context.Response.StatusCode.ToString())} IPP={result} {detail}".TrimEnd();
            _recentRequests.Enqueue(entry);
            while (_recentRequests.Count>32) _recentRequests.TryDequeue(out _);
            logger.LogInformation("IPP {RequestResult}",entry);
        }
    }
    private async Task Process(HttpContext context,RequestTrace trace)
    {
        if (!string.Equals(context.Request.ContentType?.Split(';',2)[0].Trim(),"application/ipp",StringComparison.OrdinalIgnoreCase))
        { context.Response.StatusCode=415; return; }
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        IppMessage request;
        try { trace.Request=request=await IppCodec.ReadAsync(context.Request.Body,timeout.Token); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException)
        { context.Response.StatusCode=400; return; }
        IppMessage Error(ushort status,string message)=>Response(request,status,message:message);
        async Task Send(IppMessage response)
        {
            trace.Response=response;
            var bytes=Write(response); context.Response.ContentType="application/ipp"; context.Response.ContentLength=bytes.Length;
            await context.Response.Body.WriteAsync(bytes,timeout.Token);
        }
        if (!(request.Major==1 && request.Minor==1 || request.Major==2 && request.Minor==0))
        { await Send(Error(0x0503,"Supported IPP versions: 1.1, 2.0")); return; }
        if (request.Attributes.Count<2 || request.Attributes[0] is not { Group:IppTag.Operation,Name:"attributes-charset",Tag:IppTag.Charset } ||
            request.Attributes[1] is not { Group:IppTag.Operation,Name:"attributes-natural-language",Tag:IppTag.Language })
        { await Send(Error(0x0400,"Required operation attributes must come first")); return; }
        if (!string.Equals(request.Text("attributes-charset"),"utf-8",StringComparison.OrdinalIgnoreCase))
        { await Send(Error(0x040d,"UTF-8 required")); return; }
        if (!PrinterAttributes.Operations.Contains(request.Code))
        { await Send(Error(0x0501,"Operation not supported")); return; }
        var settings=state.Settings;
        if (!settings.Enabled) { await Send(Error(0x0506,"Printer sharing is paused")); return; }
        var path=context.Request.Path.Value ?? "";
        var jobId=request.Number("job-id");
        if (request.Text("job-uri") is { } jobUri && Uri.TryCreate(jobUri,UriKind.Absolute,out var parsedJob))
            int.TryParse(parsedJob.AbsolutePath.Split('/').LastOrDefault(),out jobId);
        if (path.StartsWith("/jobs/",StringComparison.Ordinal)) int.TryParse(path.Split('/').LastOrDefault(),out jobId);
        var job=jobId>0 ? jobs.Find(jobId) : null;
        var printerId=path.StartsWith("/printers/",StringComparison.Ordinal) ? path[10..].TrimEnd('/') : job?.Info.PrinterId;
        var shared=settings.Printers.FirstOrDefault(p=>p.Id==printerId);
        var printer=printers.Printers.FirstOrDefault(p=>p.Id==printerId && p.Usable);
        if (shared is null || printer is null) { await Send(Error(0x0406,"Printer not found or driver unavailable")); return; }
        if (request.Text("printer-uri") is { } target && (!Uri.TryCreate(target,UriKind.Absolute,out var targetUri) || targetUri.AbsolutePath.TrimEnd('/')!="/printers/"+printerId))
        { await Send(Error(0x0400,"Printer URI does not match target")); return; }
        if (job is not null && job.Info.PrinterId!=printerId) { await Send(Error(0x0406,"Job not found")); return; }
        var user=request.Text("requesting-user-name") ?? "anonymous";
        if (settings.Authentication.Enabled && request.Code!=0x000b)
        {
            var remote=context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var now=DateTimeOffset.UtcNow;
            foreach (var entry in _failures) if (entry.Value.Until<now) _failures.TryRemove(entry.Key,out _);
            if (_failures.TryGetValue(remote,out var failure) && failure.Count>=20 && failure.Until>now)
            { context.Response.StatusCode=429; context.Response.Headers.RetryAfter="60"; return; }
            var rawTarget=context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path+context.Request.QueryString;
            var secret=WindowsSecurity.UnprotectSecret(settings.Authentication.ProtectedHa1!);
            if (!digest.Verify(context.Request.Headers.Authorization,context.Request.Method,rawTarget,settings.Authentication.Username,secret))
            {
                // The first challenge without Authorization is normal protocol negotiation.
                if (context.Request.Headers.ContainsKey("Authorization") && _failures.Count<4096)
                    _failures.AddOrUpdate(remote,(1,now.AddMinutes(1)),(_,old)=>(old.Count+1,old.Until));
                context.Response.StatusCode=401;
                context.Response.Headers.Append("WWW-Authenticate",digest.Challenge());
                context.Response.Headers.Append("WWW-Authenticate",digest.Challenge("MD5"));
                return;
            }
            _failures.TryRemove(remote,out _); user=settings.Authentication.Username;
        }
        var address=context.Connection.LocalIpAddress?.ToString().Split('%')[0] ?? "127.0.0.1";
        if (address.Contains(':')) address="["+address+"]";
        var baseUri=$"ipp://{address}:{settings.Port}"; var uri=baseUri+"/printers/"+printerId;
        try
        {
            switch (request.Code)
            {
                case 0x000b: // Get-Printer-Attributes is intentionally available before authentication.
                    var requested=request.Find("requested-attributes")?.Values.Select(v=>v.Text).ToHashSet(StringComparer.Ordinal);
                    await Send(Response(request,0,PrinterAttributes.Build(printer,shared,uri,settings.Authentication.Enabled,
                        (int)(DateTimeOffset.UtcNow-_started).TotalSeconds,jobs.Snapshot().Count(j=>j.PrinterId==printer.Id && j.State<7),discovery.ConfigChangeTime,requested)));
                    break;
                case 0x0002: case 0x0004: case 0x0005:
                    var options=PrintOptions.From(request,printer,shared);
                    if (request.Code==0x0004) { await Send(Response(request,0)); break; }
                    var created=jobs.Create(printer,options,request.Text("job-name") ?? "AirPrint 文档",user,request.Text("document-format") ?? "application/octet-stream");
                    if (request.Code==0x0002) await jobs.UploadAsync(created.Info.Id,context.Request.Body,created.Format,timeout.Token);
                    await Send(Response(request,0,JobAttributes(jobs.Find(created.Info.Id)!,baseUri)));
                    break;
                case 0x0006:
                    if (job is null || !Owned(job,user,settings)) { await Send(Error(0x0406,"Job not found")); break; }
                    if (!request.Flag("last-document")) { await Send(Error(0x040b,"Only single-document jobs are supported")); break; }
                    var format=request.Text("document-format") ?? job.Format;
                    if (!PrinterAttributes.Formats.Contains(format)) { await Send(Error(0x040a,"Document format not supported")); break; }
                    await jobs.UploadAsync(job.Info.Id,context.Request.Body,format,timeout.Token);
                    await Send(Response(request,0,JobAttributes(jobs.Find(job.Info.Id)!,baseUri)));
                    break;
                case 0x0008:
                    if (job is null || !Owned(job,user,settings)) { await Send(Error(0x0406,"Job not found")); break; }
                    await Send(jobs.Cancel(job.Info.Id) ? Response(request,0) : Error(0x0404,"Job is already terminal; inspect Windows queue"));
                    break;
                case 0x0009:
                    await Send(job is null || !Owned(job,user,settings) ? Error(0x0406,"Job not found") : Response(request,0,JobAttributes(job,baseUri)));
                    break;
                case 0x000a:
                    var which=request.Text("which-jobs") ?? "not-completed";
                    if (which is not ("all" or "completed" or "not-completed")) { await Send(Error(0x040b,"Unsupported which-jobs")); break; }
                    var limit=Math.Clamp(request.Number("limit",50),1,50);
                    var output=new List<IppAttribute>();
                    foreach (var info in jobs.Snapshot().Where(j=>j.PrinterId==printerId &&
                        (which=="all" || (which=="completed" ? j.State>=7 : j.State<7)) &&
                        (!settings.Authentication.Enabled || j.User==user) && (!request.Flag("my-jobs") || j.User==user)).Take(limit))
                    {
                        output.Add(new(IppTag.Job,0,"",[])); // explicit delimiter between job groups
                        output.AddRange(JobAttributes(jobs.Find(info.Id)!,baseUri));
                    }
                    await Send(Response(request,0,output));
                    break;
            }
        }
        catch (UnsupportedOptionException ex)
        {
            var supplied=request.Find(ex.Attribute);
            var attributes=supplied is null ? null : new[] { supplied with { Group=IppTag.Unsupported } };
            await Send(Response(request,ex.Attribute=="document-format" ? (ushort)0x040a : (ushort)0x040b,attributes,"Unsupported "+ex.Attribute));
        }
        catch (InvalidDataException ex) { await Send(Error(0x0400,ex.Message)); }
        catch (InvalidOperationException ex) { await Send(Error(0x0404,ex.Message)); }
        catch (IOException ex) { logger.LogWarning(ex,"接收打印作业失败。"); await Send(Error(0x0507,"Spool storage or connection unavailable")); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogError(ex,"IPP 操作 {Operation} 失败。",request.Code); await Send(Error(0x0500,"Internal printer error; see local diagnostics")); }
    }
    private static bool Owned(StoredJob job,string user,BridgeSettings settings)=>!settings.Authentication.Enabled || job.Info.User==user;
    private static IEnumerable<IppAttribute> JobAttributes(StoredJob job,string baseUri)
    {
        const byte g=IppTag.Job;
        return [Int(g,"job-id",job.Info.Id),Text(g,IppTag.Uri,"job-uri",$"{baseUri}/jobs/{job.Info.Id}"),
            Text(g,IppTag.Uri,"job-printer-uri",$"{baseUri}/printers/{job.Info.PrinterId}"),
            Text(g,IppTag.Name,"job-name",job.Info.Name),Text(g,IppTag.Name,"job-originating-user-name",job.Info.User),
            Attr(g,"job-state",IppValue.Int(job.Info.State,IppTag.Enum)),
            Text(g,IppTag.Keyword,"job-state-reasons",job.Info.State switch { 3=>"job-queued",4=>"job-incoming",5=>"job-printing",7=>"job-canceled-by-user",8=>"aborted-by-system",9=>"job-completed-successfully",_=>"none" }),
            Text(g,IppTag.Text,"job-state-message",job.Info.Message), Int(g,"job-impressions-completed",job.Info.Pages),
            Int(g,"time-at-creation",(int)Math.Clamp((job.Info.Created-Product.StartedAt).TotalSeconds,0,int.MaxValue)),Int(g,"job-printer-up-time",Product.UptimeSeconds),
            Text(g,IppTag.Keyword,"media",job.Options.Media),Int(g,"copies",job.Options.Copies)];
    }
}
