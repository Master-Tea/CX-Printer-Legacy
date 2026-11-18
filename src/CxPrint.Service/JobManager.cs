using System.Text.Json;
using System.Threading.Channels;
using CxPrint.Core;

namespace CxPrint.Service;

public sealed record StoredJob(JobInfo Info,PrintOptions Options,string PrinterName,string Format="application/octet-stream");

public sealed class JobManager : ServiceWorker
{
    private readonly object _gate=new();
    private readonly Dictionary<int,StoredJob> _jobs=[];
    private readonly HashSet<int> _uploading=[];
    private readonly Dictionary<int,long> _documentBytes=[];
    private readonly Dictionary<int,CancellationTokenSource> _cancellations=[];
    private readonly Channel<int> _queue=Channel.CreateBounded<int>(new BoundedChannelOptions(64) { FullMode=BoundedChannelFullMode.Wait,SingleReader=true });
    private readonly IPrintBackend _backend;
    private readonly ILogger<JobManager> _logger;
    private readonly string _root;
    private readonly Func<string,PrinterInfo> _readPrinter;
    private const long SpoolLimit=2L*1024*1024*1024;
    private long _spoolBytes;
    private int _lastId;
    public JobManager(IPrintBackend backend,ILogger<JobManager> logger) : this(backend,logger,Paths.Spool) { }
    public JobManager(IPrintBackend backend,ILogger<JobManager> logger,string spoolDirectory,Func<string,PrinterInfo>? readPrinter=null)
    {
        _backend=backend; _logger=logger; _readPrinter=readPrinter ?? CxPrint.Windows.PrinterCatalog.Read;
        _root=Path.GetFullPath(spoolDirectory); Directory.CreateDirectory(_root);
        var sequence=Path.Combine(_root,"sequence.txt");
        if (File.Exists(sequence) && (!int.TryParse(File.ReadAllText(sequence),out _lastId) || _lastId<0))
            throw new InvalidDataException("作业序号文件损坏，请先备份并检查缓存目录。");
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            if (!int.TryParse(Path.GetFileName(directory),out var id) || id<1 || IsReparse(directory)) continue;
            _lastId=Math.Max(_lastId,id);
            try
            {
                var path=Path.Combine(directory,"job.json");
                if (!File.Exists(path)) { Directory.Delete(directory,true); continue; }
                var job=JsonSerializer.Deserialize<StoredJob>(File.ReadAllText(path),Product.Json);
                if (job is null || job.Info.Id!=id) throw new InvalidDataException("作业元数据与目录不匹配。");
                if (job.Info.State<7) job=job with { Info=job.Info with { State=8,Message="服务重启前作业未完成；为避免重复出纸，请检查打印机后重新提交。" } };
                _jobs[id]=job; CleanDocuments(id); Persist(job);
            }
            catch (Exception ex) { _logger.LogWarning(ex,"恢复作业 {JobId} 的记录失败。",id); }
        }
    }
    public IReadOnlyList<JobInfo> Snapshot() { lock (_gate) return _jobs.Values.Select(j=>j.Info).OrderByDescending(j=>j.Id).Take(50).ToArray(); }
    public StoredJob? Find(int id) { lock (_gate) return _jobs.GetValueOrDefault(id); }
    public StoredJob Create(PrinterInfo printer,PrintOptions options,string name,string user,string format)
    {
        lock (_gate)
        {
            if (_jobs.Values.Count(j=>j.Info.State<7)>=64) throw new InvalidOperationException("待处理作业已达 64 个，请稍后再试。");
            var drive=new DriveInfo(Path.GetPathRoot(_root)!);
            if (drive.AvailableFreeSpace<512L*1024*1024 || _spoolBytes>=SpoolLimit) throw new IOException("打印缓存空间不足。");
            if (_lastId==int.MaxValue) throw new InvalidOperationException("作业编号已耗尽，请备份后重置缓存。");
            var id=++_lastId;
            AtomicWrite(Path.Combine(_root,"sequence.txt"),id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var job=new StoredJob(new(id,printer.Id,Limit(name,200),Limit(user,128),4,"等待文档上传",DateTimeOffset.UtcNow),options,printer.Name,format);
            Directory.CreateDirectory(DirectoryFor(id)); Persist(job); _jobs.Add(id,job); return job;
        }
    }
    public async Task UploadAsync(int id,Stream source,string format,CancellationToken cancellation)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id,out var job) || job.Info.State!=4 || !_uploading.Add(id))
                throw new InvalidOperationException("此作业不再接受文档，禁止重复提交。");
        }
        var incoming=Path.Combine(DirectoryFor(id),"document.incoming");
        try
        {
            await using (var file=new FileStream(incoming,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous))
            {
                var buffer=new byte[65536]; long total=0; int n;
                while ((n=await source.ReadAsync(buffer,cancellation))>0)
                {
                    total+=n;
                    if (total>Product.MaxDocumentBytes) throw new InvalidDataException("文档超过 256 MB 限制。");
                    lock (_gate)
                    {
                        if (_jobs[id].Info.State!=4) throw new OperationCanceledException("作业已取消。");
                        if (_spoolBytes+n>SpoolLimit) throw new IOException("打印缓存已达 2 GB 限制，请稍后重试。");
                        _spoolBytes+=n; _documentBytes[id]=total;
                    }
                    await file.WriteAsync(buffer.AsMemory(0,n),cancellation);
                }
                if (total==0) throw new InvalidDataException("文档为空。");
                await file.FlushAsync(cancellation);
            }
            lock (_gate)
            {
                var job=_jobs[id];
                if (job.Info.State!=4) throw new OperationCanceledException("作业已取消。");
                File.Move(incoming,DocumentFor(id));
                job=job with { Format=format,Info=job.Info with { State=3,Message="等待提交到 Windows 打印队列" } };
                _jobs[id]=job; Persist(job);
                if (!_queue.Writer.TryWrite(id)) throw new InvalidOperationException("作业队列已满。");
            }
        }
        catch
        {
            CleanDocuments(id); Update(id,8,"文档接收失败或连接中断，请重新提交。"); throw;
        }
        finally { lock (_gate) _uploading.Remove(id); }
    }
    public bool Cancel(int id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id,out var job) || job.Info.State>=7) return false;
            _cancellations.GetValueOrDefault(id)?.Cancel();
            _jobs[id]=job with { Info=job.Info with { State=7,Message="作业已取消；已进入 Windows 队列的页面需在 Windows 中确认。" } };
            Persist(_jobs[id]); return true;
        }
    }
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var tick=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); tick.CancelAfter(TimeSpan.FromSeconds(10));
            int id;
            try { id=await _queue.Reader.ReadAsync(tick.Token); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { Expire(); continue; }
            StoredJob job; CancellationTokenSource cancel;
            lock (_gate)
            {
                job=_jobs[id]; if (job.Info.State!=3) { CleanDocuments(id); continue; }
                cancel=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); cancel.CancelAfter(TimeSpan.FromMinutes(20));
                _cancellations[id]=cancel; Update(id,5,"正在渲染并提交到 Windows 打印队列");
            }
            try
            {
                var printer=_readPrinter(job.PrinterName);
                var pages=await _backend.PrintAsync(DocumentFor(id),job.Format,printer,job.Options,job.Info.Name,cancel.Token);
                Update(id,9,printer.IsPdfTest ? $"PDF 测试文件已保存到 {Paths.TestOutput}（按文件时间查找）。" : "已提交到 Windows 打印队列；实际出纸状态请检查打印机。",pages);
            }
            catch (OperationCanceledException)
            { Update(id,stoppingToken.IsCancellationRequested ? 8 : 7,"处理已中止，请检查打印机后再决定是否重打。"); }
            catch (Exception ex)
            { Update(id,8,"打印失败："+ex.Message); _logger.LogError(ex,"作业 {JobId} 打印失败。",id); }
            finally
            {
                lock (_gate) _cancellations.Remove(id); cancel.Dispose();
                try { CleanDocuments(id); } catch (IOException ex) { _logger.LogWarning(ex,"清理打印缓存失败。"); }
                Expire();
            }
        }
    }
    private void Expire()
    {
        lock (_gate)
        {
            foreach (var job in _jobs.Values.Where(j=>j.Info.State==4 && !_uploading.Contains(j.Info.Id) && DateTimeOffset.UtcNow-j.Info.Created>TimeSpan.FromSeconds(120)).ToArray())
            { Update(job.Info.Id,8,"等待文档超时。"); CleanDocuments(job.Info.Id); }
            foreach (var job in _jobs.Values.Where(j=>j.Info.State>=7 && !_uploading.Contains(j.Info.Id) && DateTimeOffset.UtcNow-j.Info.Created>TimeSpan.FromDays(7)).ToArray())
            {
                var directory=DirectoryFor(job.Info.Id);
                if (Directory.Exists(directory) && !IsReparse(directory)) Directory.Delete(directory,true);
                _jobs.Remove(job.Info.Id);
            }
        }
    }
    private void Update(int id,int state,string message,int pages=0)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id,out var job)) return;
            if (job.Info.State==7 && state!=7) return;
            job=job with { Info=job.Info with { State=state,Message=message,Pages=pages } }; _jobs[id]=job; Persist(job);
        }
    }
    private void Persist(StoredJob job)=>AtomicWrite(Path.Combine(DirectoryFor(job.Info.Id),"job.json"),JsonSerializer.Serialize(job,Product.Json));
    private static void AtomicWrite(string path,string text)
    {
        using (var file=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
        using (var writer=new StreamWriter(file)) { writer.Write(text); writer.Flush(); file.Flush(flushToDisk:true); }
        File.Move(path+".tmp",path,true);
    }
    private void CleanDocuments(int id)
    {
        lock (_gate)
        {
            var directory=DirectoryFor(id); if (!Directory.Exists(directory) || IsReparse(directory)) return;
            foreach (var file in Directory.EnumerateFiles(directory).Where(p=>Path.GetFileName(p) is not ("job.json" or "job.json.tmp"))) File.Delete(file);
            var rendered=Path.Combine(directory,"rendered");
            if (Directory.Exists(rendered) && !IsReparse(rendered)) Directory.Delete(rendered,true);
            if (_documentBytes.Remove(id,out var bytes)) _spoolBytes-=bytes;
        }
    }
    private string DirectoryFor(int id)=>Path.Combine(_root,id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private string DocumentFor(int id)=>Path.Combine(DirectoryFor(id),"document.bin");
    private static string Limit(string value,int max)=>new(value.Where(c=>!char.IsControl(c)).Take(max).ToArray());
    private static bool IsReparse(string path)=>(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0;
}
