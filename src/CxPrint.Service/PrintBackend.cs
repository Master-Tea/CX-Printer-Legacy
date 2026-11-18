using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using CxPrint.Core;
using CxPrint.Windows;
using PDFiumCore;

namespace CxPrint.Service;

public interface IPrintBackend
{
    Task<int> PrintAsync(string filename,string format,PrinterInfo printer,PrintOptions options,string jobName,CancellationToken cancellation);
}

public sealed class WindowsPrintBackend : IPrintBackend
{
    public async Task<int> PrintAsync(string filename,string format,PrinterInfo printer,PrintOptions options,string jobName,CancellationToken cancellation)
        =>await PrintAsync(filename,format,printer,options,jobName,cancellation,Paths.TestOutput);
    internal async Task<int> PrintAsync(string filename,string format,PrinterInfo printer,PrintOptions options,string jobName,
        CancellationToken cancellation,string testOutputDirectory)
    {
        var pages=new List<string>();
        string? output=null;
        var work=Path.Combine(Path.GetDirectoryName(filename)!,"rendered"); Directory.CreateDirectory(work);
        try
        {
            await Prepare(filename,format,work,pages,cancellation);
            if (pages.Count==0) throw new InvalidDataException("文档没有可打印页面。");
            cancellation.ThrowIfCancellationRequested();
            // Capabilities are read again before spooling to avoid stale driver IDs after a driver update.
            var fresh=PrinterCatalog.Read(printer.Name);
            if (!fresh.Usable) throw new InvalidOperationException(fresh.Error ?? "打印队列不可用。");
            var media=fresh.Media.FirstOrDefault(m=>m.Key==options.Media)
                ?? throw new InvalidOperationException("驱动纸张能力已变化，请重新选择纸张。");
            if (fresh.IsPdfTest) output=CreateTestOutputPath(testOutputDirectory);
            await Task.Run(()=>PrintPages(pages,fresh,media,options,jobName,cancellation,output),cancellation);
            if (output is not null) await WaitForTestOutputAsync(output,cancellation);
            return pages.Count;
        }
        catch
        {
            if (output is not null) { try { File.Delete(output); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            throw;
        }
        finally
        {
            foreach (var page in pages) if (File.Exists(page)) File.Delete(page);
            if (Directory.Exists(work)) Directory.Delete(work,true);
        }
    }
    internal static async Task Prepare(string filename,string format,string work,List<string> pages,CancellationToken cancellation)
    {
        using var input=File.OpenRead(filename);
        var signature=new byte[8]; var got=await input.ReadAsync(signature,cancellation); input.Position=0;
        if (got<4) throw new InvalidDataException("文档不完整。");
        var detected=signature.AsSpan(0,4).SequenceEqual("%PDF"u8) ? "application/pdf" :
            signature.AsSpan(0,4).SequenceEqual("UNIR"u8) ? "image/urf" :
            signature.AsSpan(0,4).SequenceEqual("RaS2"u8) ? "image/pwg-raster" :
            signature[0]==0xff && signature[1]==0xd8 ? "image/jpeg" :
            signature.AsSpan(0,4).SequenceEqual(new byte[]{137,80,78,71}) ? "image/png" : "unknown";
        if (detected=="unknown" || format!="application/octet-stream" && format!=detected)
            throw new InvalidDataException("文档内容与声明的格式不一致。");
        if (detected=="application/pdf")
            RenderPdf(filename,work,pages,cancellation);
        else if (detected is "image/urf" or "image/pwg-raster")
        {
            var reader=new RasterReader(input); RasterPage? page;
            while ((page=reader.NextPage()) is not null)
            {
                cancellation.ThrowIfCancellationRequested();
                using var bitmap=new Bitmap(page.Width,page.Height,PixelFormat.Format24bppRgb);
                bitmap.SetResolution(page.Dpi,page.Dpi);
                var bits=bitmap.LockBits(new Rectangle(0,0,page.Width,page.Height),ImageLockMode.WriteOnly,PixelFormat.Format24bppRgb);
                try
                {
                    var bgr=new byte[page.Width*3];
                    page.DecodeRows((y,row)=>
                    {
                        for (var x=0;x<page.Width;x++)
                        {
                            if (page.BytesPerPixel==1) bgr[x*3]=bgr[x*3+1]=bgr[x*3+2]=row[x];
                            else { bgr[x*3]=row[x*3+2]; bgr[x*3+1]=row[x*3+1]; bgr[x*3+2]=row[x*3]; }
                        }
                        Marshal.Copy(bgr,0,IntPtr.Add(bits.Scan0,y*bits.Stride),bgr.Length);
                    },cancellation);
                }
                finally { bitmap.UnlockBits(bits); }
                var path=Path.Combine(work,$"{pages.Count:D4}.png"); bitmap.Save(path,ImageFormat.Png); pages.Add(path);
                EnforceRenderQuota(work);
            }
        }
        else
        {
            var path=Path.Combine(work,"0000.png");
            input.Position=0;
            using var image=Image.FromStream(input);
            if ((long)image.Width*image.Height>64_000_000) throw new InvalidDataException("图片像素过大。");
            image.Save(path,ImageFormat.Png); pages.Add(path); EnforceRenderQuota(work);
        }
    }

    private static readonly object PdfiumGate=new();
    private static bool _pdfiumInitialized;
    private static void RenderPdf(string filename,string work,List<string> pages,CancellationToken cancellation)
    {
        lock (PdfiumGate)
        {
            if (!_pdfiumInitialized) { fpdfview.FPDF_InitLibrary(); _pdfiumInitialized=true; }
            var document=fpdfview.FPDF_LoadDocument(filename,string.Empty);
            if (document is null) throw new InvalidDataException($"无法打开 PDF 文件（PDFium 错误 {fpdfview.FPDF_GetLastError()}）。");
            try
            {
                var pageCount=fpdfview.FPDF_GetPageCount(document);
                if (pageCount is <1 or >1000) throw new InvalidDataException("PDF 页数应为 1–1000 页。");
                for (var index=0;index<pageCount;index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var page=fpdfview.FPDF_LoadPage(document,index);
                    if (page is null) throw new InvalidDataException("PDF 页面无法读取。");
                    try
                    {
                        var pointsWidth=fpdfview.FPDF_GetPageWidthF(page);
                        var pointsHeight=fpdfview.FPDF_GetPageHeightF(page);
                        if (!float.IsFinite(pointsWidth) || !float.IsFinite(pointsHeight) || pointsWidth<=0 || pointsHeight<=0)
                            throw new InvalidDataException("PDF 页面尺寸无效。");
                        var width=(int)Math.Ceiling(pointsWidth*300/72.0);
                        var height=(int)Math.Ceiling(pointsHeight*300/72.0);
                        if (width<1 || height<1 || (long)width*height>64_000_000)
                            throw new InvalidDataException("PDF 页面过大。");
                        var bitmap=fpdfview.FPDFBitmapCreate(width,height,1);
                        if (bitmap is null) throw new InvalidDataException("无法创建 PDF 页面图像。");
                        try
                        {
                            fpdfview.FPDFBitmapFillRect(bitmap,0,0,width,height,0xFFFFFFFF);
                            fpdfview.FPDF_RenderPageBitmap(bitmap,page,0,0,width,height,0,0);
                            var stride=fpdfview.FPDFBitmapGetStride(bitmap);
                            var buffer=fpdfview.FPDFBitmapGetBuffer(bitmap);
                            if (buffer==IntPtr.Zero || stride<width*4) throw new InvalidDataException("PDF 页面图像数据无效。");
                            var path=Path.Combine(work,$"{index:D4}.png");
                            using (var image=new Bitmap(width,height,stride,PixelFormat.Format32bppArgb,buffer))
                            {
                                image.SetResolution(300,300);
                                image.Save(path,ImageFormat.Png);
                            }
                            pages.Add(path); EnforceRenderQuota(work);
                        }
                        finally { fpdfview.FPDFBitmapDestroy(bitmap); }
                    }
                    finally { fpdfview.FPDF_ClosePage(page); }
                }
            }
            finally { fpdfview.FPDF_CloseDocument(document); }
        }
    }

    private static void EnforceRenderQuota(string work)
    {
        if (Directory.EnumerateFiles(work).Sum(p=>new FileInfo(p).Length)>1_073_741_824)
            throw new IOException("作业渲染缓存超过 1 GB 限制。");
    }
    internal static string CreateTestOutputPath(string directory)
    {
        directory=Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0) throw new IOException("PDF 测试输出目录不能是链接。");
        var files=Directory.EnumerateFiles(directory,"CX-Test-*.pdf").Select(p=>new FileInfo(p)).ToArray();
        if (files.Length>=100 || files.Sum(f=>f.Length)>=1_073_741_824)
            throw new IOException($"PDF 测试输出已达 100 个文件或 1 GB，请清理 {directory} 后重试。");
        return Path.Combine(directory,$"CX-Test-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.pdf");
    }
    private static async Task WaitForTestOutputAsync(string output,CancellationToken cancellation)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                try
                {
                    using var file=new FileStream(output,FileMode.Open,FileAccess.Read,FileShare.Read);
                    var signature=new byte[5];
                    if (file.Read(signature)==5 && signature.AsSpan().SequenceEqual("%PDF-"u8)) return;
                }
                catch (IOException) { }
                await Task.Delay(200,timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("Microsoft Print to PDF 未生成测试文件，请检查 Windows 打印队列。"); }
    }
    private static void PrintPages(List<string> pages,PrinterInfo printer,MediaSize media,PrintOptions options,string jobName,
        CancellationToken cancellation,string? output=null)
    {
        using var document=new PrintDocument();
        document.PrinterSettings.PrinterName=printer.Name;
        if (!document.PrinterSettings.IsValid) throw new InvalidOperationException("打印队列不可用。");
        if (output is not null)
        {
            document.PrinterSettings.PrintToFile=true;
            document.PrinterSettings.PrintFileName=output;
        }
        var paper=document.PrinterSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault(p=>p.RawKind==media.WindowsPaperId)
            ?? throw new InvalidOperationException("驱动未找到对应的纸张 ID；已阻止回退成 Letter。");
        document.DocumentName=jobName;
        document.PrintController=new StandardPrintController();
        document.PrinterSettings.Copies=(short)options.Copies;
        document.PrinterSettings.Collate=true;
        document.PrinterSettings.Duplex=options.Sides switch { "two-sided-long-edge"=>Duplex.Vertical,"two-sided-short-edge"=>Duplex.Horizontal,_=>Duplex.Simplex };
        document.DefaultPageSettings.PaperSize=paper;
        document.DefaultPageSettings.Color=options.Color && printer.Color;
        document.DefaultPageSettings.Landscape=options.Orientation==4;
        document.DefaultPageSettings.Margins=new Margins(0,0,0,0);
        var index=0;
        document.QueryPageSettings+=(_,e)=>
        {
            e.PageSettings.PaperSize=paper;
            e.PageSettings.Landscape=options.Orientation==4;
            e.PageSettings.Color=options.Color && printer.Color;
            e.Cancel=cancellation.IsCancellationRequested;
        };
        document.PrintPage+=(_,e)=>
        {
            if (cancellation.IsCancellationRequested) { e.Cancel=true; return; }
            using var image=Image.FromFile(pages[index++]);
            var graphics=e.Graphics ?? throw new InvalidOperationException("打印驱动没有提供绘图表面。");
            // PrintDocument's origin is the printable area. Translate to physical page coordinates,
            // preserving the paper size selected in IPP and clipping to the hardware printable area.
            graphics.TranslateTransform(-e.PageSettings.HardMarginX,-e.PageSettings.HardMarginY);
            var area=e.PageSettings.PrintableArea;
            var ratio=Math.Min(area.Width/image.Width,area.Height/image.Height);
            var width=image.Width*ratio; var height=image.Height*ratio;
            graphics.DrawImage(image,area.Left+(area.Width-width)/2,area.Top+(area.Height-height)/2,width,height);
            e.HasMorePages=index<pages.Count;
        };
        document.Print(); cancellation.ThrowIfCancellationRequested();
    }
}
