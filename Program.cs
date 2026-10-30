// Scanner button monitor (Windows tray app, WIA) -> multi-page PDF
//
// Press the scanner button -> page is scanned. When no new page arrives for
// IdleSeconds, all pages from that session are saved as one PDF in
// Documents\Scans. No NuGet packages needed (the PDF is written by hand).
//
// Needs the COM reference "Microsoft Windows Image Acquisition Library v2.0".

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using WIA;

namespace ScanButtonMonitor;

// Usage:
//   ScanButtonMonitor.exe --register     (once; asks for admin) adds this app to the
//                                        scanner's Events tab "Start this program" list
//   ScanButtonMonitor.exe --unregister   removes it again
// Windows launches the exe with /StiDevice:... /StiEvent:... when the button is pressed.
internal static class Program
{
    private const string MutexName = @"Local\ScanButtonMonitor.Instance";
    internal const string TriggerName = @"Local\ScanButtonMonitor.Trigger";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--register", StringComparer.OrdinalIgnoreCase))
        {
            HandlerRegistration.Run(register: true);
            return;
        }
        if (args.Contains("--unregister", StringComparer.OrdinalIgnoreCase))
        {
            HandlerRegistration.Run(register: false);
            return;
        }
        if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
        {
            using var probeMutex = new Mutex(true, MutexName, out bool free);
            if (!free)
            {
                MessageBox.Show("Exit the scan monitor from the tray first, then run --probe again.", "Probe");
                return;
            }
            ApplicationConfiguration.Initialize();
            Application.Run(new ProbeContext());
            return;
        }

        // Launched by Windows for a button press?
        string? stiArg = args.FirstOrDefault(a => a.StartsWith("/StiDevice:", StringComparison.OrdinalIgnoreCase));
        bool launchedByButton = stiArg is not null;
        string? deviceId = stiArg?["/StiDevice:".Length..].Trim('"');

        using var mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Already running: tell it a button was pressed, then quit.
            MonitorContext.Log($"second instance started, args: {string.Join(' ', args)}");
            if (launchedByButton)
            {
                try
                {
                    EventWaitHandle.OpenExisting(TriggerName).Set();
                    MonitorContext.Log("second instance: trigger signal sent");
                }
                catch (Exception ex)
                {
                    MonitorContext.Log("second instance: could not signal running copy: " + ex.Message);
                }
            }
            else
            {
                MonitorContext.Log("second instance: no /StiDevice argument, nothing to do");
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MonitorContext(launchedByButton, deviceId));
    }
}

internal static class HandlerRegistration
{
    private const string HandlerName = "Scan Button Monitor";
    private const string HandlerDescription = "Scan pages and save them as one PDF";

    public static void Run(bool register)
    {
        if (!IsAdmin())
        {
            // Relaunch elevated with the same switch.
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!,
                    register ? "--register" : "--unregister")
                { UseShellExecute = true, Verb = "runas" });
            }
            catch
            {
                MessageBox.Show("Administrator rights are required.", HandlerName);
            }
            return;
        }

        string exe = Environment.ProcessPath!;
        string command = $"\"{exe}\" /StiDevice:%1 /StiEvent:%2";
        var report = new StringBuilder();

        try
        {
            var mgr = new DeviceManager();
            foreach (DeviceInfo info in mgr.DeviceInfos)
            {
                Device dev = info.Connect();
                foreach (DeviceEvent ev in dev.Events)
                {
                    if (((int)ev.Type & 2) == 0) continue;      // buttons only
                    try
                    {
                        if (register)
                            mgr.RegisterPersistentEvent(command, HandlerName, HandlerDescription,
                                exe + ",0", ev.EventID, info.DeviceID);
                        else
                            mgr.UnregisterPersistentEvent(command, HandlerName, HandlerDescription,
                                exe + ",0", ev.EventID, info.DeviceID);
                        report.AppendLine($"{ev.Name}: OK");
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"{ev.Name}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            report.AppendLine(ex.Message);
        }

        if (report.Length == 0) report.AppendLine("No scanner button events found.");
        MessageBox.Show(report.ToString(), register ? "Registered" : "Unregistered");
    }

    private static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

// ScanButtonMonitor.exe --probe
// Watches the scanner for 90 seconds and logs every WIA property (device and item)
// and every WIA event, only when something CHANGES. Insert a page, remove it,
// press the button, and see which line changes in Documents\Scans\probe.log.
// Candidate to look for: "Document Handling Status" (ID 3087), where 1 = FEED_READY.
internal sealed class ProbeContext : ApplicationContext
{
    private const int Seconds = 90;
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Scans", "probe.log");

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly NotifyIcon _tray;
    private readonly DeviceManager _mgr = new();
    private readonly Dictionary<string, string> _last = new();
    private DateTime _end = DateTime.Now.AddSeconds(Seconds);
    private Device? _dev;
    private Item? _item;

    public ProbeContext()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, "");
        _tray = new NotifyIcon { Icon = SystemIcons.Information, Text = "Scanner probe", Visible = true };

        _timer.Tick += (_, _) =>
        {
            if (DateTime.Now > _end) { Finish(); return; }
            if (_dev is null || _item is null) return;
            try
            {
                Dump("dev", _dev.Properties);
                Dump("item", _item.Properties);
            }
            catch (Exception ex) { Write("sample failed: " + ex.Message); }
        };
        _timer.Start();

        try
        {
            _mgr.OnEvent += (eventId, deviceId, itemId) =>
                Write($"EVENT {eventId} device={deviceId} item={itemId}");

            DeviceInfo? info = null;
            foreach (DeviceInfo di in _mgr.DeviceInfos) { info = di; break; }
            if (info is null) { Write("No scanner found."); _end = DateTime.MinValue; return; }

            _dev = info.Connect();
            _item = _dev.Items[1];

            // listen to ALL events this scanner has, notification ones too
            foreach (DeviceEvent ev in _dev.Events)
            {
                try { _mgr.RegisterEvent(ev.EventID, info.DeviceID); Write($"listening: {ev.Name} {ev.EventID}"); }
                catch (Exception ex) { Write($"could not listen to {ev.Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Write("Setup failed: " + ex);
            _end = DateTime.MinValue;                      // ends on the next timer tick
            return;
        }

        _tray.ShowBalloonTip(5000, "Scanner probe",
            $"Running {Seconds}s: insert a page, remove it, press the button.", ToolTipIcon.Info);
    }

    // First sample writes every property; after that only changes are written.
    private void Dump(string scope, dynamic props)
    {
        foreach (dynamic p in props)
        {
            string key = $"{scope} {p.PropertyID} {p.Name}";
            string val;
            try { val = Convert.ToString(p.Value) ?? ""; }
            catch { val = "<unreadable>"; }

            if (!_last.TryGetValue(key, out string? old))
            {
                _last[key] = val;
                Write($"{key} = {val}");
            }
            else if (old != val)
            {
                _last[key] = val;
                Write($"CHANGED {key}: {old} -> {val}");
            }
        }
    }

    private static void Write(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch { }
    }

    private void Finish()
    {
        _timer.Stop();
        foreach (object? o in new object?[] { _item, _dev })
            if (o is not null && System.Runtime.InteropServices.Marshal.IsComObject(o))
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(o);
        _item = null;
        _dev = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        _tray.Visible = false;
        MessageBox.Show($"Probe finished.\n\nResults: {LogPath}", "Scanner probe");
        ExitThread();
    }
}

internal record ScannedPage(byte[] Jpeg, int Width, int Height);

internal sealed class MonitorContext : ApplicationContext
{
    // ---------------- settings ----------------
    private const int IdleSeconds = 8;      // no new page for this long -> save PDF
    private const int Dpi = 300;
    private const int ColorIntent = 1;      // 1 = color, 2 = grayscale, 4 = black & white
    private const string FormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";
    private const string DisconnectEventId = "{143E4E83-6497-11D2-A231-00C04FA31809}";   // WIA "Device disconnected"
    private const int WatchdogSeconds = 5;  // backup check that the scanner is still listed

    // Auto de-skew + auto-crop. The scanner returns a full-size canvas; anything
    // near-white (blank padding) or the bluish-gray backing visible beside the
    // paper is treated as background. The backing color below was measured on
    // DS-640 scans (about RGB 172,177,183); adjust it if yours differs.
    private const bool AutoDeskew = true;
    private const bool AutoCrop = true;
    private const int BackingR = 172, BackingG = 177, BackingB = 183;
    private const int BackingTol = 22;      // max color distance from the backing color
    private const int WhiteMin = 250;       // R, G and B all >= this -> white padding
    private const int CropStep = 6;         // analyze every 6th pixel (fast)
    private const double LooseEdge = 0.02;  // pass 1: row/column with 2% paper = inside the sheet
    private const double TightEdge = 0.90;  // pass 2: edge row/column must be 90% paper (trims wedges)
    private const double MinSkewDegrees = 0.3;   // smaller skews are left alone (avoids blur)
    private const double MaxSkewDegrees = 15;    // larger "skews" are treated as detection errors

    private static readonly string OutputDir = Path.Combine(@"D:\Documents\Scans");
    //private static readonly string OutputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Scans");
    private static readonly string LogPath = Path.Combine(OutputDir, "scan-monitor.log");
    // ------------------------------------------

    private readonly Icon _icon;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _idle;
    private readonly Control _marshal;          // used to hop back onto the UI thread
    private readonly DeviceManager _mgr;        // keep alive or events stop
    private readonly List<ScannedPage> _pages = new();
    private readonly HashSet<string> _knownDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _watchdog;
    private bool _exiting;
    private bool _busy;
    private DateTime _lastScan = DateTime.MinValue;

    private readonly EventWaitHandle _trigger;
    private RegisteredWaitHandle? _triggerWait;

    public MonitorContext(bool scanNow = false, string? deviceId = null)
    {
        Directory.CreateDirectory(OutputDir);

        _marshal = new Control();
        _ = _marshal.Handle;                    // force handle creation on this thread

        var menu = new ContextMenuStrip();
        menu.Items.Add("Save PDF now", null, (_, _) => SavePdf());
        menu.Items.Add("Open scans folder", null, (_, _) => Process.Start("explorer.exe", OutputDir));
        menu.Items.Add("Open log", null, (_, _) => Process.Start("notepad.exe", LogPath));
        menu.Items.Add("Exit", null, (_, _) => { SavePdf(); ExitThread(); });

        var assembly = Assembly.GetExecutingAssembly();
        string resourceName = "ScanButtonMonitor.Icon.ico";
        using (Stream stream = assembly.GetManifestResourceStream(resourceName)!)
            _icon = (stream == null ? SystemIcons.Application : new Icon(stream));
        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "Scan button monitor",
            Visible = true,
            ContextMenuStrip = menu
        };

        _idle = new System.Windows.Forms.Timer { Interval = IdleSeconds * 1000 };
        _idle.Tick += (_, _) => SavePdf();

        _watchdog = new System.Windows.Forms.Timer { Interval = WatchdogSeconds * 1000 };
        _watchdog.Tick += (_, _) =>
        {
            if (!_busy && !ScannerStillConnected()) ScannerGone("scanner no longer listed");
        };

        _mgr = new DeviceManager();
        _mgr.OnEvent += OnWiaEvent;
        RegisterButtonEvents();
        _watchdog.Start();

        // A second instance (started by Windows on a button press) sets this
        // event and exits; we then scan as if the button was pressed.
        _trigger = new EventWaitHandle(false, EventResetMode.AutoReset, Program.TriggerName);
        _triggerWait = ThreadPool.RegisterWaitForSingleObject(_trigger,
            (_, _) =>
            {
                Log("trigger signal received");
                _marshal.BeginInvoke(new Action(() => ScanPage(null)));
            },
            null, Timeout.Infinite, executeOnlyOnce: false);

        // We were started by the button press itself: scan right away.
        if (scanNow)
            _marshal.BeginInvoke(new Action(() => ScanPage(deviceId)));
    }

    // Ask every connected scanner which action events (buttons) it has,
    // and register for all of them. This catches vendor-specific events
    // such as "Scan/toPC Button", not just the standard WIA ones.
    private void RegisterButtonEvents()
    {
        int registered = 0;
        try
        {
            foreach (DeviceInfo info in _mgr.DeviceInfos)
            {
                dynamic d = info;
                string name = d.Properties["Name"].Value;
                _knownDevices.Add(info.DeviceID);
                Device dev = info.Connect();
                foreach (DeviceEvent ev in dev.Events)
                {
                    bool isAction = ((int)ev.Type & 2) != 0;   // 2 = ActionEvent
                    bool isDisconnect = string.Equals(ev.EventID, DisconnectEventId,
                        StringComparison.OrdinalIgnoreCase);
                    Log($"{name}: '{ev.Name}' {ev.EventID} type={ev.Type}" +
                        (isAction || isDisconnect ? " -> registered" : ""));
                    if (isAction || isDisconnect)
                    {
                        _mgr.RegisterEvent(ev.EventID, info.DeviceID);
                        if (isAction) registered++;
                    }
                }
                ReleaseCom(dev);    // don't keep the scanner connected between presses
            }
        }
        catch (Exception ex)
        {
            Log("Register failed: " + ex);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Notify(registered > 0
            ? $"Listening for scanner button ({registered} event(s))."
            : "No scanner buttons found. See scan-monitor.log.",
            registered > 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    // The scanner was unplugged or switched off: save what we have and quit.
    // Windows starts the program again on the next button press once it is back.
    private void ScannerGone(string reason)
    {
        if (_exiting) return;
        _exiting = true;
        _watchdog.Stop();
        Log($"scanner disconnected ({reason}); exiting");
        SavePdf();
        ExitThread();
    }

    // Backup for the disconnect event: a fresh DeviceManager sees the current
    // device list. Never reports "gone" because of a failed check.
    private bool ScannerStillConnected()
    {
        if (_knownDevices.Count == 0) return true;     // nothing to watch
        DeviceManager? fresh = null;
        try
        {
            fresh = new DeviceManager();
            foreach (DeviceInfo info in fresh.DeviceInfos)
                if (_knownDevices.Contains(info.DeviceID)) return true;
            return false;
        }
        catch (Exception ex)
        {
            Log("watchdog check failed: " + ex.Message);
            return true;
        }
        finally
        {
            ReleaseCom(fresh);
        }
    }

    private void OnWiaEvent(string eventId, string deviceId, string itemId)
    {
        Log($"event {eventId} from {deviceId}");
        // registered per device, so a disconnect here is always our scanner
        if (string.Equals(eventId, DisconnectEventId, StringComparison.OrdinalIgnoreCase))
        {
            _marshal.BeginInvoke(new Action(() => ScannerGone("disconnect event")));
            return;
        }
        // ignore events while scanning and duplicates from the same press
        if (_busy || (DateTime.UtcNow - _lastScan).TotalSeconds < 1.5) return;
        _marshal.BeginInvoke(new Action(() => ScanPage(deviceId)));
    }

    private void ScanPage(string? deviceId)
    {
        // same press can arrive twice (COM callback + launched instance)
        if (_busy || (DateTime.UtcNow - _lastScan).TotalSeconds < 1.5)
        {
            Log($"scan request ignored (busy={_busy})");
            return;
        }
        _busy = true;
        _idle.Stop();
        string bmpPath = Path.Combine(Path.GetTempPath(), $"scan_{Guid.NewGuid():N}.bmp");
        try
        {
            Log("scan started");
            ScanToFile(deviceId, bmpPath);
            Log("transfer done, device released");
            _pages.Add(EncodeJpeg(bmpPath));

            Notify($"Page {_pages.Count} scanned. Saving PDF in {IdleSeconds}s if no more pages.");
        }
        catch (Exception ex)
        {
            Log("Scan failed: " + ex);
            Notify("Scan failed: " + ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            try { File.Delete(bmpPath); } catch { }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            _busy = false;
            _lastScan = DateTime.UtcNow;
            if (_pages.Count > 0) _idle.Start();
        }
    }

    // All WIA objects live only inside this method and are released explicitly.
    // If they are left for the garbage collector, the driver keeps the scanner
    // "in use" (light blinking, next button press delivered late).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ScanToFile(string? deviceId, string bmpPath)
    {
        Device? dev = null;
        Item? item = null;
        ImageFile? img = null;
        try
        {
            dev = ConnectTo(deviceId);
            item = dev.Items[1];
            SetProp(item, 6146, ColorIntent);   // current intent
            SetProp(item, 6147, Dpi);           // horizontal resolution
            SetProp(item, 6148, Dpi);           // vertical resolution

            img = (ImageFile)item.Transfer(FormatBmp);
            img.SaveFile(bmpPath);
        }
        finally
        {
            ReleaseCom(img);
            ReleaseCom(item);
            ReleaseCom(dev);
        }
    }

    private static void ReleaseCom(object? o)
    {
        if (o is not null && System.Runtime.InteropServices.Marshal.IsComObject(o))
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(o);
    }

    // Connect to the scanner that fired the event; if the id is missing or
    // doesn't match (e.g. Windows passed a placeholder), use the first scanner.
    private Device ConnectTo(string? deviceId)
    {
        DeviceInfo? first = null;
        foreach (DeviceInfo info in _mgr.DeviceInfos)
        {
            first ??= info;
            if (deviceId is not null &&
                string.Equals(info.DeviceID, deviceId, StringComparison.OrdinalIgnoreCase))
                return info.Connect();
        }
        if (first is null) throw new InvalidOperationException("No scanner found.");
        return first.Connect();
    }

    private static void SetProp(Item item, int propertyId, int value)
    {
        try
        {
            foreach (dynamic p in (dynamic)item.Properties)
            {
                if (p.PropertyID == propertyId) { p.Value = value; return; }
            }
        }
        catch { /* driver doesn't allow it; use its default */ }
    }

    // BMP from the scanner -> 24-bit RGB JPEG (quality 90), ready to embed in the PDF.
    private static ScannedPage EncodeJpeg(string bmpPath)
    {
        using var src = new Bitmap(bmpPath);
        var work = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        work.SetResolution(Dpi, Dpi);
        using (var g = Graphics.FromImage(work))
            g.DrawImage(src, 0, 0, src.Width, src.Height);

        try
        {
            int w0 = work.Width, h0 = work.Height;
            var paper = BuildPaperMask(work, out bool[,] backing, out int cw, out int ch);

            double angle = AutoDeskew ? EstimateSkew(backing, cw, ch) : 0;
            if (angle != 0)
            {
                var rotated = Rotate(work, angle);
                work.Dispose();
                work = rotated;
                paper = BuildPaperMask(work, out backing, out cw, out ch);   // re-measure after rotating
            }

            Rectangle box = AutoCrop
                ? FindPaperBox(paper, cw, ch, work.Width, work.Height)
                : new Rectangle(0, 0, work.Width, work.Height);
            Log($"deskew {angle:0.00} deg, crop {w0}x{h0} -> {box.Width}x{box.Height}");

            using var cropped = work.Clone(box, PixelFormat.Format24bppRgb);
            var codec = ImageCodecInfo.GetImageEncoders()
                .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
            using var ms = new MemoryStream();
            cropped.Save(ms, codec, ep);
            return new ScannedPage(ms.ToArray(), cropped.Width, cropped.Height);
        }
        finally
        {
            work.Dispose();
        }
    }

    // Finds the paper area of a 24-bit scan by color. Paper is assumed to start
    // at the top-left corner (the DS-640 feeds it against that edge), so only the
    // right and bottom edges are located:
    //   right  = where the gray backing strip begins
    //   bottom = last row containing anything other than white / backing gray
    // If nothing sensible is found, the whole image is kept.
    // Coarse grid (one cell per CropStep pixels). paper = true where the cell is
    // paper; backing = true where it is the gray backing; white padding is neither.
    private static bool[,] BuildPaperMask(Bitmap bmp, out bool[,] backing, out int cw, out int ch)
    {
        int w = bmp.Width, h = bmp.Height;
        cw = (w + CropStep - 1) / CropStep;
        ch = (h + CropStep - 1) / CropStep;
        var paper = new bool[ch, cw];
        backing = new bool[ch, cw];

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            for (int cy = 0; cy < ch; cy++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + cy * CropStep * data.Stride, row, 0, row.Length);
                for (int cx = 0; cx < cw; cx++)
                {
                    int i = cx * CropStep * 3;              // pixels are B, G, R
                    int b = row[i], g = row[i + 1], r = row[i + 2];
                    bool white = Math.Min(r, Math.Min(g, b)) >= WhiteMin;
                    int dr = r - BackingR, dg = g - BackingG, db = b - BackingB;
                    bool isBacking = dr * dr + dg * dg + db * db <= BackingTol * BackingTol;
                    backing[cy, cx] = isBacking;
                    paper[cy, cx] = !(white || isBacking);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return paper;
    }

    // Skew angle in degrees from the sheet's LEFT edge, measured only where the
    // gray backing is visible left of the sheet (a backing run starting at the
    // canvas edge, then the sheet). Without visible backing there is no real edge
    // to measure: a page scanned straight, or white paper whose content merely
    // starts at some margin, must NOT be rotated. The result is the angle to pass
    // to RotateTransform to straighten the page, or 0.
    private static double EstimateSkew(bool[,] backing, int cw, int ch)
    {
        var ys = new List<double>();
        var xs = new List<double>();
        for (int y = 0; y < ch; y++)
        {
            int x = 0;
            while (x < cw && backing[y, x]) x++;           // leading backing run
            if (x < 3 || x > cw - 4) continue;             // no real backing, or a row that is all backing
            if (backing[y, x + 1] || backing[y, x + 2]) continue;   // glitch, not a clean edge
            ys.Add(y);
            xs.Add(x);                                     // first cell of the sheet
        }
        int n = ys.Count;
        if (n < Math.Max(20, ch * 0.15)) return 0;         // too little evidence
        int lo = n / 10, hi = n - n / 10;                  // ignore ragged corners
        if (!FitLine(ys.GetRange(lo, hi - lo), xs.GetRange(lo, hi - lo), out double slope)) return 0;

        double deg = Math.Atan(slope) * 180.0 / Math.PI;
        return Math.Abs(deg) >= MinSkewDegrees && Math.Abs(deg) <= MaxSkewDegrees ? deg : 0;
    }

    // Least-squares fit x = slope*y + b, repeated a few times with outliers dropped.
    private static bool FitLine(List<double> ys, List<double> xs, out double slope)
    {
        slope = 0;
        int n = ys.Count;
        var keep = new bool[n];
        Array.Fill(keep, true);
        for (int iter = 0; iter < 4; iter++)
        {
            double sy = 0, sx = 0, syy = 0, sxy = 0;
            int m = 0;
            for (int i = 0; i < n; i++)
            {
                if (!keep[i]) continue;
                sy += ys[i]; sx += xs[i]; syy += ys[i] * ys[i]; sxy += ys[i] * xs[i]; m++;
            }
            if (m < 10) return false;
            double den = m * syy - sy * sy;
            if (Math.Abs(den) < 1e-9) return false;
            double a = (m * sxy - sy * sx) / den;
            double b = (sx - a * sy) / m;
            slope = a;

            var res = new List<double>();
            for (int i = 0; i < n; i++)
                if (keep[i]) res.Add(Math.Abs(xs[i] - (a * ys[i] + b)));
            res.Sort();
            double limit = Math.Max(1.5, 2.5 * res[res.Count / 2]);
            for (int i = 0; i < n; i++)
                keep[i] = Math.Abs(xs[i] - (a * ys[i] + b)) <= limit;
        }
        return true;
    }

    // Rotates about the center onto a larger white canvas so no corner is cut off.
    private static Bitmap Rotate(Bitmap src, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        double c = Math.Abs(Math.Cos(rad)), s = Math.Abs(Math.Sin(rad));
        int w = src.Width, h = src.Height;
        int nw = (int)Math.Ceiling(w * c + h * s);
        int nh = (int)Math.Ceiling(w * s + h * c);

        var dst = new Bitmap(nw, nh, PixelFormat.Format24bppRgb);
        dst.SetResolution(Dpi, Dpi);
        using var g = Graphics.FromImage(dst);
        g.Clear(Color.White);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.TranslateTransform(nw / 2f, nh / 2f);
        g.RotateTransform((float)degrees);
        g.TranslateTransform(-w / 2f, -h / 2f);
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);   // no halo at the edges
        g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, attrs);
        return dst;
    }

    // Two passes over the paper mask:
    //   1. loose box: any row/column with a little paper in it
    //   2. tight box: edge rows/columns must be mostly paper, which trims the
    //      background wedges left by sheets that aren't perfect rectangles
    // If nothing sensible is found, the whole image is kept.
    private static Rectangle FindPaperBox(bool[,] paper, int cw, int ch, int w, int h)
    {
        var whole = new Rectangle(0, 0, w, h);
        var colCount = new int[cw];
        var rowCount = new int[ch];
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
                if (paper[y, x]) { colCount[x]++; rowCount[y]++; }

        int l0 = -1, r0 = -1, t0 = -1, b0 = -1;
        for (int x = 0; x < cw; x++)
            if (colCount[x] >= LooseEdge * ch) { if (l0 < 0) l0 = x; r0 = x + 1; }
        for (int y = 0; y < ch; y++)
            if (rowCount[y] >= LooseEdge * cw) { if (t0 < 0) t0 = y; b0 = y + 1; }
        if (l0 < 0 || t0 < 0) return whole;

        int bw = r0 - l0, bh = b0 - t0;
        int l = -1, r = -1, t = -1, b = -1;
        for (int x = l0; x < r0; x++)
        {
            int count = 0;
            for (int y = t0; y < b0; y++) if (paper[y, x]) count++;
            if (count >= TightEdge * bh) { if (l < 0) l = x; r = x + 1; }
        }
        for (int y = t0; y < b0; y++)
        {
            int count = 0;
            for (int x = l0; x < r0; x++) if (paper[y, x]) count++;
            if (count >= TightEdge * bw) { if (t < 0) t = y; b = y + 1; }
        }
        if (l < 0 || t < 0) { l = l0; r = r0; t = t0; b = b0; }

        int left = l * CropStep, top = t * CropStep;
        int right = Math.Min(w, r * CropStep), bottom = Math.Min(h, b * CropStep);
        if (right - left < w * 0.1 || bottom - top < h * 0.1) return whole;   // implausibly small
        return new Rectangle(left, top, right - left, bottom - top);
    }

    private void SavePdf()
    {
        _idle.Stop();
        if (_pages.Count == 0) return;
        string path = Path.Combine(OutputDir, $"Scan_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
        try
        {
            WritePdf(path, _pages);
            Notify($"Saved {_pages.Count} page(s): {Path.GetFileName(path)}");
            _pages.Clear();
        }
        catch (Exception ex)
        {
            Log("Save failed: " + ex);
            Notify("Could not save PDF: " + ex.Message, ToolTipIcon.Error);
        }
    }

    // Minimal PDF writer: one JPEG per page, page size = pixels / DPI.
    private static void WritePdf(string path, List<ScannedPage> pages)
    {
        int n = pages.Count;
        int objCount = 2 + 3 * n;               // catalog, pages, then page/content/image per page
        var offsets = new long[objCount + 1];
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);

        void W(string s) { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); }
        void Begin(int id) { offsets[id] = fs.Position; W($"{id} 0 obj\n"); }
        void End() => W("endobj\n");
        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        W("%PDF-1.4\n");

        Begin(1);
        W("<< /Type /Catalog /Pages 2 0 R >>\n");
        End();

        Begin(2);
        string kids = string.Join(" ", Enumerable.Range(0, n).Select(i => $"{3 + 3 * i} 0 R"));
        W($"<< /Type /Pages /Count {n} /Kids [{kids}] >>\n");
        End();

        for (int i = 0; i < n; i++)
        {
            var p = pages[i];
            int pageId = 3 + 3 * i, contentId = pageId + 1, imageId = pageId + 2;
            string w = F(p.Width * 72.0 / Dpi), h = F(p.Height * 72.0 / Dpi);

            Begin(pageId);
            W($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] " +
              $"/Resources << /XObject << /Im0 {imageId} 0 R >> >> /Contents {contentId} 0 R >>\n");
            End();

            string content = $"q {w} 0 0 {h} 0 0 cm /Im0 Do Q\n";
            Begin(contentId);
            W($"<< /Length {content.Length} >>\nstream\n{content}endstream\n");
            End();

            Begin(imageId);
            W($"<< /Type /XObject /Subtype /Image /Width {p.Width} /Height {p.Height} " +
              $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {p.Jpeg.Length} >>\nstream\n");
            fs.Write(p.Jpeg, 0, p.Jpeg.Length);
            W("\nendstream\n");
            End();
        }

        long xref = fs.Position;
        W($"xref\n0 {objCount + 1}\n0000000000 65535 f \n");
        for (int id = 1; id <= objCount; id++)
            W($"{offsets[id]:D10} 00000 n \n");
        W($"trailer\n<< /Size {objCount + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    private void Notify(string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        Log(message);
        _tray.ShowBalloonTip(3000, "Scan button monitor", message, icon);
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _triggerWait?.Unregister(null);
            _trigger.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _idle.Dispose();
            _watchdog.Dispose();
        }
        base.Dispose(disposing);
    }
}