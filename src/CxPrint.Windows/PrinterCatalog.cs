using System.Drawing.Printing;
using System.Runtime.InteropServices;
using CxPrint.Core;

namespace CxPrint.Windows;

public static class PrinterCatalog
{
    public static IReadOnlyList<string> Names()=>PrinterSettings.InstalledPrinters.Cast<string>().ToArray();
    public static IReadOnlyList<PrinterInfo> Enumerate()
    {
        var result=new List<PrinterInfo>();
        foreach (var name in Names())
        {
            try { result.Add(Read(name)); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { result.Add(new(Product.PrinterId(name),name,"","",false,false,[],"",ex.Message)); }
        }
        return result.OrderBy(p=>p.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public static PrinterInfo Read(string name)
    {
        var settings=new PrinterSettings { PrinterName=name };
        if (!settings.IsValid) throw new InvalidOperationException("Windows 打印机不可用。");
        string driver="Windows 打印驱动",port="";
        if (OpenPrinter(name,out var handle,IntPtr.Zero))
        {
            try
            {
                GetPrinter(handle,2,IntPtr.Zero,0,out var needed);
                if (needed is > 0 and < 1_048_576)
                {
                    var buffer=Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        if (GetPrinter(handle,2,buffer,needed,out _))
                        {
                            var info=Marshal.PtrToStructure<PrinterInfo2>(buffer);
                            driver=Marshal.PtrToStringUni(info.DriverName) ?? driver;
                            port=Marshal.PtrToStringUni(info.PortName) ?? "";
                        }
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            finally { ClosePrinter(handle); }
        }
        var interactive=!PrinterInfo.IsMicrosoftPdfDriver(driver) &&
            (port.Equals("PORTPROMPT:",StringComparison.OrdinalIgnoreCase) || port.Equals("FILE:",StringComparison.OrdinalIgnoreCase) ||
            driver.Equals("Microsoft Shared Fax Driver",StringComparison.OrdinalIgnoreCase));
        // Microsoft Print to PDF is the explicit test exception: the backend supplies
        // a fixed output filename. Other interactive drivers cannot run in a service.
        if (interactive) return new(Product.PrinterId(name),name,driver,port,false,false,[],"","此队列需要交互操作，无法在后台服务中共享。");
        var native=ReadMedia(name,port);
        var media=new List<MediaSize>();
        var seen=new HashSet<string>();
        foreach (PaperSize paper in settings.PaperSizes)
        {
            var dimensions=native.GetValueOrDefault(paper.RawKind);
            var width=dimensions.W>0 ? dimensions.W : (int)Math.Round(paper.Width*25.4);
            var height=dimensions.H>0 ? dimensions.H : (int)Math.Round(paper.Height*25.4);
            if (width<=0 || height<=0) continue;
            var keyword=MediaSize.Keyword(width,height);
            // Many drivers expose aliases for one size; query its printable area only once.
            if (!seen.Add(keyword)) continue;
            var margins=(L:635,T:635,R:635,B:635);
            try
            {
                var page=new PageSettings(settings) { PaperSize=paper };
                var area=page.PrintableArea;
                margins=((int)Math.Round(area.Left*25.4),(int)Math.Round(area.Top*25.4),
                    Math.Max(0,width-(int)Math.Round(area.Right*25.4)),Math.Max(0,height-(int)Math.Round(area.Bottom*25.4)));
            }
            catch (Exception ex) when (ex is InvalidPrinterException or System.ComponentModel.Win32Exception) { }
            media.Add(new(keyword,paper.PaperName,width,height,paper.RawKind,
                Math.Clamp(margins.L,0,width/2),Math.Clamp(margins.T,0,height/2),Math.Clamp(margins.R,0,width/2),Math.Clamp(margins.B,0,height/2)));
        }
        media=media.DistinctBy(m=>m.Key).ToList();
        if (media.Count==0) throw new InvalidOperationException("驱动未报告纸张尺寸，已停止广播此打印机，请更新驱动。");
        var defaultPaper=settings.DefaultPageSettings.PaperSize;
        var def=media.FirstOrDefault(m=>m.WindowsPaperId==defaultPaper.RawKind)
            ?? media.FirstOrDefault(m=>Math.Abs(m.Width-defaultPaper.Width*25.4)<60 && Math.Abs(m.Height-defaultPaper.Height*25.4)<60)
            ?? media[0];
        return new(Product.PrinterId(name),name,driver,port,settings.SupportsColor,settings.CanDuplex,media,def.Key);
    }
    private static Dictionary<int,(int W,int H)> ReadMedia(string printer,string port)
    {
        var result=new Dictionary<int,(int,int)>();
        var count=DeviceCapabilities(printer,port,2,IntPtr.Zero,IntPtr.Zero); // DC_PAPERS
        if (count is < 1 or > 2048) return result;
        var ids=Marshal.AllocHGlobal(count*2); var sizes=Marshal.AllocHGlobal(count*8);
        try
        {
            var n1=DeviceCapabilities(printer,port,2,ids,IntPtr.Zero);
            var n2=DeviceCapabilities(printer,port,3,sizes,IntPtr.Zero); // DC_PAPERSIZE: POINT in 0.1 mm
            if (n1!=count || n2!=count) return result;
            for (var i=0;i<count;i++)
                result[(ushort)Marshal.ReadInt16(ids,i*2)]=(checked(Marshal.ReadInt32(sizes,i*8)*10),checked(Marshal.ReadInt32(sizes,i*8+4)*10));
        }
        finally { Marshal.FreeHGlobal(ids); Marshal.FreeHGlobal(sizes); }
        return result;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo2
    {
        public IntPtr ServerName, PrinterName, ShareName, PortName, DriverName, Comment, Location, DevMode,
            SepFile, PrintProcessor, Datatype, Parameters, SecurityDescriptor;
        public uint Attributes,Priority,DefaultPriority,StartTime,UntilTime,Status,Jobs,AveragePpm;
    }
    [DllImport("winspool.drv",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool OpenPrinter(string name,out IntPtr printer,IntPtr defaults);
    [DllImport("winspool.drv",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetPrinter(IntPtr printer,int level,IntPtr info,uint size,out uint needed);
    [DllImport("winspool.drv")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ClosePrinter(IntPtr printer);
    [DllImport("winspool.drv",CharSet=CharSet.Unicode,EntryPoint="DeviceCapabilitiesW")]
    private static extern int DeviceCapabilities(string device,string port,short capability,IntPtr output,IntPtr devMode);
}
