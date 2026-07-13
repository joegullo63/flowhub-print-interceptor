using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Xml;

namespace PrintInterceptor
{
    internal static class Program
    {
        private const string MutexName = "Local\\FlowhubPrintInterceptor.Instance";
        private const string StopSignalName = "Local\\FlowhubPrintInterceptor.Stop";

        [STAThread]
        private static int Main(string[] args)
        {
            if (HasArgument(args, "--self-test"))
            {
                return RunSelfTest();
            }

            if (HasArgument(args, "--toggle"))
            {
                return ToggleRunningState();
            }

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(
                        "Print Interceptor is already running in the notification area.",
                        "Print Interceptor",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return 0;
                }

                try
                {
                    var settings = AppSettings.Load();
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var context = new InterceptorContext(settings))
                    using (var stopSignal = new EventWaitHandle(false, EventResetMode.AutoReset, StopSignalName))
                    {
                        RegisteredWaitHandle stopRegistration = ThreadPool.RegisterWaitForSingleObject(
                            stopSignal,
                            delegate { context.RequestExternalStop(); },
                            null,
                            Timeout.Infinite,
                            true);
                        try
                        {
                            Application.Run(context);
                        }
                        finally
                        {
                            stopRegistration.Unregister(null);
                        }
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    Logger.Write("FATAL", ex.ToString());
                    MessageBox.Show(
                        "Print Interceptor could not start.\r\n\r\n" + ex.Message +
                        "\r\n\r\nDetails were written to:\r\n" + Logger.LogPath,
                        "Print Interceptor",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return 1;
                }
            }
        }

        private static int ToggleRunningState()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            EventWaitHandle stopSignal;
            if (TryOpenStopSignal(out stopSignal))
            {
                using (stopSignal)
                {
                    if (MessageBox.Show(
                        "Print Interceptor is currently running. Stop it?\r\n\r\n" +
                        "Receipts will continue to print, but the application will not control the cash drawer while stopped.",
                        "Stop Print Interceptor",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    {
                        return 0;
                    }
                    stopSignal.Set();
                }

                bool stopped = WaitForRunningState(false, 5000);
                MessageBox.Show(
                    stopped ? "Print Interceptor has stopped." : "A stop request was sent, but the application has not stopped yet.",
                    "Print Interceptor",
                    MessageBoxButtons.OK,
                    stopped ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                return stopped ? 0 : 1;
            }

            if (MessageBox.Show(
                "Print Interceptor is currently stopped. Start it now?",
                "Start Print Interceptor",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1) != DialogResult.Yes)
            {
                return 0;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = Assembly.GetExecutingAssembly().Location,
                UseShellExecute = true
            });
            bool started = WaitForRunningState(true, 5000);
            MessageBox.Show(
                started ? "Print Interceptor is running. Look for the shield icon in the notification area." :
                    "Print Interceptor did not report a successful start. Check the error message or audit log for details.",
                "Print Interceptor",
                MessageBoxButtons.OK,
                started ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return started ? 0 : 1;
        }

        private static bool TryOpenStopSignal(out EventWaitHandle signal)
        {
            return TryOpenSignal(StopSignalName, out signal);
        }

        private static bool TryOpenSignal(string signalName, out EventWaitHandle signal)
        {
            try
            {
                signal = EventWaitHandle.OpenExisting(signalName);
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                signal = null;
                return false;
            }
        }

        private static bool WaitForRunningState(bool running, int timeoutMilliseconds)
        {
            return WaitForSignalState(StopSignalName, running, timeoutMilliseconds);
        }

        private static bool WaitForSignalState(string signalName, bool running, int timeoutMilliseconds)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMilliseconds)
            {
                EventWaitHandle signal;
                bool detected = TryOpenSignal(signalName, out signal);
                if (signal != null) signal.Dispose();
                if (detected == running) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        private static bool HasArgument(string[] args, string wanted)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static int RunSelfTest()
        {
            try
            {
                AppSettings settings = AppSettings.Load();
                byte[] command = DrawerCommand.Build(
                    settings.PulseOnMilliseconds,
                    settings.PulseOffMilliseconds);
                byte[] expected = new byte[] { 0x1b, 0x07, 0x14, 0x14, 0x07 };

                if (settings.PulseOnMilliseconds == 200 &&
                    settings.PulseOffMilliseconds == 200 &&
                    !BytesEqual(command, expected))
                {
                    throw new InvalidOperationException("Drawer command generation failed its known-value test.");
                }

                const string eventFixture =
                    "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>" +
                    "<UserData><DocumentPrinted xmlns='http://manifests.microsoft.com/win/2005/08/windows/printing/spooler/core/events'>" +
                    "<Param1>5</Param1><Param2>Print Document</Param2><Param3>operator</Param3>" +
                    "<Param4>computer</Param4><Param5>Example Receipt Printer</Param5><Param6>USB001</Param6>" +
                    "</DocumentPrinted></UserData></Event>";
                Dictionary<string, string> eventData = PrintEventMonitor.ReadEventData(eventFixture);
                string parsedPrinter;
                if (!eventData.TryGetValue("Param5", out parsedPrinter) || parsedPrinter != "Example Receipt Printer")
                {
                    throw new InvalidOperationException("Print event parsing failed its Windows UserData test.");
                }

                byte[] receiptFixture = BuildCompressedPdfFixture("BT 10 10 Td (Example Store)Tj ET");
                string extractedText = PdfTextAnalyzer.ExtractText(receiptFixture);
                if (extractedText.IndexOf("Example Store", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    throw new InvalidOperationException("PDF content extraction failed its compressed-stream test.");
                }

                const string releaseFixture =
                    "{\"tag_name\":\"v1.2.3\",\"assets\":[" +
                    "{\"name\":\"PrintInterceptorSetup.exe\",\"browser_download_url\":\"https://example.invalid/setup.exe\"}," +
                    "{\"name\":\"PrintInterceptorSetup.exe.sha256\",\"browser_download_url\":\"https://example.invalid/setup.exe.sha256\"}]}";
                GitHubRelease parsedRelease = GitHubUpdateService.ParseRelease(releaseFixture);
                if (parsedRelease.Version != new Version(1, 2, 3) ||
                    parsedRelease.InstallerUrl != "https://example.invalid/setup.exe" ||
                    parsedRelease.ChecksumUrl != "https://example.invalid/setup.exe.sha256")
                {
                    throw new InvalidOperationException("GitHub release parsing failed its known-value test.");
                }

                string controlFixtureName = "Local\\FlowhubPrintInterceptor.SelfTest." + Guid.NewGuid().ToString("N");
                using (var controlFixture = new EventWaitHandle(false, EventResetMode.AutoReset, controlFixtureName))
                {
                    if (!WaitForSignalState(controlFixtureName, true, 1000))
                        throw new InvalidOperationException("Desktop control failed to detect a running-state signal.");
                }
                if (!WaitForSignalState(controlFixtureName, false, 1000))
                    throw new InvalidOperationException("Desktop control failed to detect a stopped-state signal.");

                string printerError;
                if (!string.Equals(settings.PrinterName, "CONFIGURED_BY_INSTALLER", StringComparison.OrdinalIgnoreCase) &&
                    !RawPrinter.CanOpen(settings.PrinterName, out printerError))
                {
                    throw new InvalidOperationException(
                        "The configured printer queue cannot be opened: " + printerError);
                }

                Logger.Write("SELFTEST", "Passed. No data was sent to the printer.");
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Write("SELFTEST", "Failed: " + ex);
                return 2;
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }
            return true;
        }

        private static byte[] BuildCompressedPdfFixture(string content)
        {
            byte[] input = Encoding.ASCII.GetBytes(content);
            byte[] deflated;
            using (var compressed = new MemoryStream())
            {
                using (var deflater = new DeflateStream(compressed, CompressionMode.Compress, true))
                {
                    deflater.Write(input, 0, input.Length);
                }
                deflated = compressed.ToArray();
            }

            byte[] prefix = Encoding.ASCII.GetBytes(
                "%PDF-1.4\r\n1 0 obj << /Filter /FlateDecode >>\r\nstream\r\n");
            byte[] suffix = Encoding.ASCII.GetBytes("\r\nendstream\r\nendobj\r\n%%EOF");
            byte[] result = new byte[prefix.Length + 2 + deflated.Length + 4 + suffix.Length];
            int offset = 0;
            Buffer.BlockCopy(prefix, 0, result, offset, prefix.Length);
            offset += prefix.Length;
            result[offset++] = 0x78;
            result[offset++] = 0x01;
            Buffer.BlockCopy(deflated, 0, result, offset, deflated.Length);
            offset += deflated.Length;
            offset += 4; // The extractor intentionally ignores the zlib Adler-32 trailer.
            Buffer.BlockCopy(suffix, 0, result, offset, suffix.Length);
            return result;
        }
    }

    internal sealed class AppSettings
    {
        public string PrinterName { get; private set; }
        public int PromptTimeoutSeconds { get; private set; }
        public int PulseOnMilliseconds { get; private set; }
        public int PulseOffMilliseconds { get; private set; }
        public string FlowhubPrintDirectory { get; private set; }
        public string ReceiptHeader { get; private set; }
        public string FulfillmentHeader { get; private set; }
        public int ClassificationWindowSeconds { get; private set; }
        public string ReleaseRepository { get; private set; }
        public int UpdateCheckHours { get; private set; }

        public static AppSettings Load()
        {
            var settings = new AppSettings();
            settings.PrinterName = Required("PrinterName");
            settings.PromptTimeoutSeconds = Integer("PromptTimeoutSeconds", 30, 5, 300);
            settings.PulseOnMilliseconds = Pulse("PulseOnMilliseconds", 200);
            settings.PulseOffMilliseconds = Pulse("PulseOffMilliseconds", 200);
            settings.FlowhubPrintDirectory = Environment.ExpandEnvironmentVariables(Required("FlowhubPrintDirectory"));
            settings.ReceiptHeader = Required("ReceiptHeader");
            settings.FulfillmentHeader = Required("FulfillmentHeader");
            settings.ClassificationWindowSeconds = Integer("ClassificationWindowSeconds", 120, 10, 600);
            settings.ReleaseRepository = Optional("ReleaseRepository", "joegullo63/flowhub-print-interceptor");
            settings.UpdateCheckHours = Integer("UpdateCheckHours", 6, 1, 168);
            return settings;
        }

        private static string Optional(string key, string defaultValue)
        {
            string value = ConfigurationManager.AppSettings[key];
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
        }

        private static string Required(string key)
        {
            string value = ConfigurationManager.AppSettings[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ConfigurationErrorsException("Missing app setting: " + key);
            }
            return value.Trim();
        }

        private static int Integer(string key, int defaultValue, int minimum, int maximum)
        {
            string text = ConfigurationManager.AppSettings[key];
            int value;
            if (string.IsNullOrWhiteSpace(text)) value = defaultValue;
            else if (!int.TryParse(text, out value))
                throw new ConfigurationErrorsException(key + " must be a whole number.");

            if (value < minimum || value > maximum)
                throw new ConfigurationErrorsException(
                    key + " must be between " + minimum + " and " + maximum + ".");
            return value;
        }

        private static int Pulse(string key, int defaultValue)
        {
            int value = Integer(key, defaultValue, 10, 1270);
            if (value % 10 != 0)
                throw new ConfigurationErrorsException(key + " must be a multiple of 10 ms.");
            return value;
        }
    }

    internal static class Logger
    {
        private static readonly object Gate = new object();
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrintInterceptor",
            "logs");

        public static string LogPath
        {
            get
            {
                return Path.Combine(DirectoryPath, "PrintInterceptor-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            }
        }

        public static void Write(string action, string detail)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(DirectoryPath);
                    string safeDetail = (detail ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
                    File.AppendAllText(
                        LogPath,
                        DateTimeOffset.Now.ToString("o") + "\t" +
                        Environment.UserName + "\t" + action + "\t" + safeDetail + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never cause a drawer action or terminate the watcher.
            }
        }
    }

    internal sealed class PrintJobNotice
    {
        public string DocumentName { get; private set; }
        public string UserName { get; private set; }
        public bool IsManualRequest { get; private set; }
        public long ByteCount { get; private set; }

        public PrintJobNotice(string documentName, string userName, bool isManualRequest)
            : this(documentName, userName, isManualRequest, 0)
        {
        }

        public PrintJobNotice(string documentName, string userName, bool isManualRequest, long byteCount)
        {
            DocumentName = string.IsNullOrWhiteSpace(documentName) ? "(document name unavailable)" : documentName;
            UserName = string.IsNullOrWhiteSpace(userName) ? "(user unavailable)" : userName;
            IsManualRequest = isManualRequest;
            ByteCount = byteCount;
        }
    }

    internal enum FlowhubJobKind
    {
        TransactionReceipt,
        Fulfillment,
        Unknown
    }

    internal sealed class FlowhubPrintCandidate
    {
        public FlowhubJobKind Kind { get; private set; }
        public string FileName { get; private set; }
        public string Evidence { get; private set; }
        public DateTime DetectedUtc { get; private set; }

        public FlowhubPrintCandidate(FlowhubJobKind kind, string fileName, string evidence, DateTime detectedUtc)
        {
            Kind = kind;
            FileName = fileName;
            Evidence = evidence;
            DetectedUtc = detectedUtc;
        }
    }

    internal sealed class FlowhubJobMonitor : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly object _gate = new object();
        private readonly List<FlowhubPrintCandidate> _pending = new List<FlowhubPrintCandidate>();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher _watcher;
        private bool _disposed;

        public event Action<Exception> MonitorFailed;

        public FlowhubJobMonitor(AppSettings settings)
        {
            _settings = settings;
        }

        public void Start()
        {
            if (!Directory.Exists(_settings.FlowhubPrintDirectory))
            {
                throw new DirectoryNotFoundException(
                    "Flowhub print directory was not found: " + _settings.FlowhubPrintDirectory);
            }

            _watcher = new FileSystemWatcher(_settings.FlowhubPrintDirectory, "*.pdf");
            _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite;
            _watcher.Created += OnFileAppeared;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;
            Logger.Write("CLASSIFIER_START", "Watching Flowhub source PDFs.");
        }

        public FlowhubPrintCandidate ConsumeForPrintedJob(DateTime printedUtc)
        {
            DateTime waitUntil = DateTime.UtcNow.AddSeconds(2);
            lock (_gate)
            {
                while (true)
                {
                    PurgeExpired(printedUtc);
                    int bestIndex = -1;
                    DateTime bestTime = DateTime.MaxValue;
                    for (int i = 0; i < _pending.Count; i++)
                    {
                        FlowhubPrintCandidate candidate = _pending[i];
                        if (candidate.DetectedUtc <= printedUtc.AddSeconds(5) && candidate.DetectedUtc < bestTime)
                        {
                            bestIndex = i;
                            bestTime = candidate.DetectedUtc;
                        }
                    }

                    if (bestIndex >= 0)
                    {
                        FlowhubPrintCandidate result = _pending[bestIndex];
                        _pending.RemoveAt(bestIndex);
                        return result;
                    }

                    TimeSpan remaining = waitUntil - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) return null;
                    Monitor.Wait(_gate, remaining);
                }
            }
        }

        private void OnFileAppeared(object sender, FileSystemEventArgs args)
        {
            QueueAnalysis(args.FullPath);
        }

        private void OnFileRenamed(object sender, RenamedEventArgs args)
        {
            QueueAnalysis(args.FullPath);
        }

        private void QueueAnalysis(string path)
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith("test-", StringComparison.OrdinalIgnoreCase)) return;
            if (!Regex.IsMatch(name, "-(receipt|fulfillment)\\.pdf$", RegexOptions.IgnoreCase)) return;

            lock (_gate)
            {
                if (_disposed || !_seen.Add(path)) return;
            }

            DateTime detectedUtc = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem(delegate
            {
                AnalyzeWithRetry(path, detectedUtc);
            });
        }

        private void AnalyzeWithRetry(string path, DateTime detectedUtc)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    byte[] pdf;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (stream.Length <= 0 || stream.Length > 10 * 1024 * 1024)
                            throw new InvalidDataException("Flowhub PDF size is outside the accepted range.");
                        pdf = new byte[(int)stream.Length];
                        int total = 0;
                        while (total < pdf.Length)
                        {
                            int read = stream.Read(pdf, total, pdf.Length - total);
                            if (read == 0) throw new EndOfStreamException("Flowhub PDF was still being written.");
                            total += read;
                        }
                    }

                    FlowhubPrintCandidate candidate = Classify(path, pdf, detectedUtc);
                    lock (_gate)
                    {
                        if (_disposed) return;
                        _pending.Add(candidate);
                        Monitor.PulseAll(_gate);
                    }
                    Logger.Write("CLASSIFIED", "Kind=" + candidate.Kind + "; Evidence=" + candidate.Evidence);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Thread.Sleep(100);
                }
            }

            var unknown = new FlowhubPrintCandidate(
                FlowhubJobKind.Unknown,
                Path.GetFileName(path),
                "PDF could not be inspected",
                detectedUtc);
            lock (_gate)
            {
                if (_disposed) return;
                _pending.Add(unknown);
                Monitor.PulseAll(_gate);
            }
            Logger.Write("CLASSIFICATION_ERROR", (lastError == null ? "Unknown error" : lastError.Message));
        }

        private FlowhubPrintCandidate Classify(string path, byte[] pdf, DateTime detectedUtc)
        {
            string fileName = Path.GetFileName(path);
            bool declaredReceipt = fileName.EndsWith("-receipt.pdf", StringComparison.OrdinalIgnoreCase);
            bool declaredFulfillment = fileName.EndsWith("-fulfillment.pdf", StringComparison.OrdinalIgnoreCase);
            string text = PdfTextAnalyzer.ExtractText(pdf);
            bool hasReceiptHeader = text.IndexOf(_settings.ReceiptHeader, StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasFulfillmentHeader = text.IndexOf(_settings.FulfillmentHeader, StringComparison.OrdinalIgnoreCase) >= 0;

            if (declaredReceipt && hasReceiptHeader && !hasFulfillmentHeader)
            {
                return new FlowhubPrintCandidate(
                    FlowhubJobKind.TransactionReceipt,
                    fileName,
                    "receipt filename and header matched",
                    detectedUtc);
            }
            if (declaredFulfillment && hasFulfillmentHeader && !hasReceiptHeader)
            {
                return new FlowhubPrintCandidate(
                    FlowhubJobKind.Fulfillment,
                    fileName,
                    "fulfillment filename and header matched",
                    detectedUtc);
            }

            return new FlowhubPrintCandidate(
                FlowhubJobKind.Unknown,
                fileName,
                "filename/header mismatch",
                detectedUtc);
        }

        private void PurgeExpired(DateTime printedUtc)
        {
            DateTime cutoff = printedUtc.AddSeconds(-_settings.ClassificationWindowSeconds);
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].DetectedUtc < cutoff)
                {
                    Logger.Write("CLASSIFICATION_EXPIRED", "Kind=" + _pending[i].Kind);
                    _pending.RemoveAt(i);
                }
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs args)
        {
            Action<Exception> handler = MonitorFailed;
            if (handler != null) handler(args.GetException());
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                Monitor.PulseAll(_gate);
            }
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFileAppeared;
                _watcher.Renamed -= OnFileRenamed;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
                _watcher = null;
            }
        }
    }

    internal static class PdfTextAnalyzer
    {
        private const int MaximumExtractedBytes = 5 * 1024 * 1024;

        public static string ExtractText(byte[] pdf)
        {
            if (pdf == null || pdf.Length < 16) throw new InvalidDataException("PDF is empty or truncated.");
            string binary = Encoding.GetEncoding(28591).GetString(pdf);
            var extracted = new StringBuilder();
            int position = 0;
            int decodedStreams = 0;

            while (position < binary.Length)
            {
                int streamMarker = binary.IndexOf("stream", position, StringComparison.Ordinal);
                if (streamMarker < 0) break;
                int dataStart = streamMarker + 6;
                if (dataStart < pdf.Length && pdf[dataStart] == 13) dataStart++;
                if (dataStart < pdf.Length && pdf[dataStart] == 10) dataStart++;
                int endMarker = binary.IndexOf("endstream", dataStart, StringComparison.Ordinal);
                if (endMarker < 0) break;

                int dataLength = endMarker - dataStart;
                while (dataLength > 0 && (pdf[dataStart + dataLength - 1] == 10 || pdf[dataStart + dataLength - 1] == 13))
                    dataLength--;

                int dictionaryStart = Math.Max(0, streamMarker - 512);
                string dictionary = binary.Substring(dictionaryStart, streamMarker - dictionaryStart);
                if (dictionary.IndexOf("/FlateDecode", StringComparison.Ordinal) >= 0 && dataLength > 6)
                {
                    try
                    {
                        using (var input = new MemoryStream(pdf, dataStart + 2, dataLength - 6, false))
                        using (var inflater = new DeflateStream(input, CompressionMode.Decompress))
                        using (var output = new MemoryStream())
                        {
                            CopyWithLimit(inflater, output, MaximumExtractedBytes);
                            extracted.Append(Encoding.GetEncoding(28591).GetString(output.ToArray()));
                            extracted.Append('\n');
                            decodedStreams++;
                        }
                    }
                    catch (InvalidDataException)
                    {
                        // Images and unsupported streams are irrelevant to header matching.
                    }
                    catch (IOException)
                    {
                        // A malformed stream yields an unknown classification, never authorization.
                    }
                }

                position = endMarker + 9;
            }

            if (decodedStreams == 0)
                throw new InvalidDataException("No readable compressed PDF content stream was found.");
            return extracted.ToString();
        }

        private static void CopyWithLimit(Stream input, Stream output, int limit)
        {
            byte[] buffer = new byte[8192];
            int total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > limit) throw new InvalidDataException("Decompressed PDF content exceeded the safety limit.");
                output.Write(buffer, 0, read);
            }
        }
    }

    internal sealed class PrintEventMonitor : IDisposable
    {
        public const string EventLogName = "Microsoft-Windows-PrintService/Operational";
        public const string DrawerDocumentName = "Print Interceptor - Drawer Pulse";

        private readonly string _printerName;
        private EventLogWatcher _watcher;

        public event Action<PrintJobNotice> JobPrinted;
        public event Action<Exception> MonitorFailed;

        public PrintEventMonitor(string printerName)
        {
            _printerName = printerName;
        }

        public void Start()
        {
            var configuration = new EventLogConfiguration(EventLogName);
            try
            {
                if (!configuration.IsEnabled)
                {
                    throw new InvalidOperationException(
                        "The Windows PrintService Operational log is disabled. Run scripts\\Install.ps1 as Administrator.");
                }
            }
            finally
            {
                configuration.Dispose();
            }

            string xpath = "*[System[Provider[@Name='Microsoft-Windows-PrintService'] and EventID=307]]";
            var query = new EventLogQuery(EventLogName, PathType.LogName, xpath);
            _watcher = new EventLogWatcher(query, null, false);
            _watcher.EventRecordWritten += OnEventRecordWritten;
            _watcher.Enabled = true;
        }

        private void OnEventRecordWritten(object sender, EventRecordWrittenEventArgs args)
        {
            if (args.EventException != null)
            {
                Action<Exception> failed = MonitorFailed;
                if (failed != null) failed(args.EventException);
                return;
            }

            if (args.EventRecord == null) return;
            try
            {
                Dictionary<string, string> data = ReadEventData(args.EventRecord.ToXml());
                string eventPrinter = Get(data, "Param5");
                string document = Get(data, "Param2");
                string user = Get(data, "Param3");
                long byteCount;
                if (!long.TryParse(Get(data, "Param7"), out byteCount)) byteCount = 0;

                if (!PrinterMatches(eventPrinter, _printerName)) return;

                Logger.Write("PRINTED", "Printer=" + eventPrinter + "; Document=" + document + "; Bytes=" + byteCount);
                Action<PrintJobNotice> handler = JobPrinted;
                if (handler != null) handler(new PrintJobNotice(document, user, false, byteCount));
            }
            catch (Exception ex)
            {
                Action<Exception> failed = MonitorFailed;
                if (failed != null) failed(ex);
            }
            finally
            {
                args.EventRecord.Dispose();
            }
        }

        internal static Dictionary<string, string> ReadEventData(string xml)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var document = new XmlDocument();
            document.LoadXml(xml);

            // Some Windows builds serialize the event template as EventData/Data
            // elements whose Name attribute is Param1, Param2, and so on.
            XmlNodeList nodes = document.SelectNodes(
                "/*[local-name()='Event']/*[local-name()='EventData']/*[local-name()='Data']");
            foreach (XmlNode node in nodes)
            {
                if (node.Attributes == null) continue;
                XmlAttribute name = node.Attributes["Name"];
                if (name != null) result[name.Value] = node.InnerText ?? string.Empty;
            }


            // Windows 11 currently emits PrintService 307 values under
            // UserData/DocumentPrinted as direct Param1..Param8 elements.
            XmlNodeList userDataNodes = document.SelectNodes(
                "/*[local-name()='Event']/*[local-name()='UserData']//*[starts-with(local-name(), 'Param')]");
            foreach (XmlNode node in userDataNodes)
            {
                result[node.LocalName] = node.InnerText ?? string.Empty;
            }
            return result;
        }

        private static string Get(Dictionary<string, string> data, string key)
        {
            string value;
            return data.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static bool PrinterMatches(string eventPrinter, string configuredPrinter)
        {
            if (string.Equals(eventPrinter, configuredPrinter, StringComparison.OrdinalIgnoreCase)) return true;
            return eventPrinter.EndsWith("\\" + configuredPrinter, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            if (_watcher == null) return;
            _watcher.Enabled = false;
            _watcher.EventRecordWritten -= OnEventRecordWritten;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    internal sealed class InterceptorContext : ApplicationContext, IDisposable
    {
        private readonly AppSettings _settings;
        private readonly Form _dispatcher;
        private readonly NotifyIcon _trayIcon;
        private readonly Queue<PrintJobNotice> _pending = new Queue<PrintJobNotice>();
        private readonly object _pulseGate = new object();
        private PrintEventMonitor _monitor;
        private FlowhubJobMonitor _flowhubMonitor;
        private System.Threading.Timer _updateTimer;
        private int _updateChecking;
        private int _expectedPulseEvents;
        private DateTime _pulseExpectationExpiresUtc;
        private bool _promptActive;
        private bool _disposing;
        private string _status;

        public InterceptorContext(AppSettings settings)
        {
            _settings = settings;
            _status = "Starting";

            _dispatcher = new Form();
            _dispatcher.ShowInTaskbar = false;
            _dispatcher.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            _dispatcher.Opacity = 0;
            _dispatcher.Size = new Size(1, 1);
            IntPtr unused = _dispatcher.Handle;
            MainForm = _dispatcher;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Print Interceptor", null, delegate { ShowStatus(); });
            menu.Items.Add("Request drawer opening...", null, delegate { Enqueue(new PrintJobNotice("Manual request", Environment.UserName, true)); });
            menu.Items.Add("View audit log", null, delegate { OpenLog(); });
            menu.Items.Add("Diagnostics", null, delegate { ShowStatus(); });
            menu.Items.Add("Check for updates...", null, delegate { CheckForUpdates(true); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitApplication(); });

            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = SystemIcons.Shield;
            _trayIcon.Text = "Print Interceptor - starting";
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += delegate { ShowStatus(); };

            _dispatcher.BeginInvoke(new Action(StartMonitor));
            _updateTimer = new System.Threading.Timer(
                delegate { CheckForUpdates(false); },
                null,
                TimeSpan.FromSeconds(20),
                TimeSpan.FromHours(_settings.UpdateCheckHours));
        }

        private void CheckForUpdates(bool interactive)
        {
            if (_disposing) return;
            if (Interlocked.CompareExchange(ref _updateChecking, 1, 0) != 0)
            {
                if (interactive) _trayIcon.ShowBalloonTip(1500, "Print Interceptor", "An update check is already running.", ToolTipIcon.Info);
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    GitHubRelease release = GitHubUpdateService.GetLatest(_settings.ReleaseRepository);
                    Version current = Assembly.GetExecutingAssembly().GetName().Version;
                    if (release.Version <= current)
                    {
                        if (interactive) InvokeOnUi(delegate
                        {
                            MessageBox.Show("Print Interceptor " + current.ToString(3) + " is current.", "No update available", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        });
                        return;
                    }

                    Logger.Write("UPDATE_AVAILABLE", "Version=" + release.Version.ToString(3));
                    InvokeOnUi(delegate
                    {
                        DialogResult answer = MessageBox.Show(
                            "Print Interceptor " + release.Version.ToString(3) + " is available.\r\n\r\n" +
                            "Install it now? This terminal's printer and receipt settings will be preserved. Windows will request administrator approval.",
                            "Print Interceptor update",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Information,
                            MessageBoxDefaultButton.Button1);
                        if (answer == DialogResult.Yes) DownloadAndInstallUpdate(release);
                    });
                }
                catch (Exception ex)
                {
                    Logger.Write("UPDATE_CHECK_FAILED", ex.Message);
                    if (interactive) InvokeOnUi(delegate
                    {
                        MessageBox.Show("The update check failed.\r\n\r\n" + ex.Message, "Print Interceptor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                }
                finally
                {
                    Interlocked.Exchange(ref _updateChecking, 0);
                }
            });
        }

        private void DownloadAndInstallUpdate(GitHubRelease release)
        {
            _trayIcon.ShowBalloonTip(2000, "Print Interceptor", "Downloading verified update...", ToolTipIcon.Info);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string installer = GitHubUpdateService.DownloadAndVerify(release);
                    InvokeOnUi(delegate
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = installer,
                                Arguments = "--update",
                                Verb = "runas",
                                UseShellExecute = true
                            });
                            Logger.Write("UPDATE_LAUNCHED", "Version=" + release.Version.ToString(3));
                            ExitThread();
                        }
                        catch (Exception ex)
                        {
                            Logger.Write("UPDATE_LAUNCH_FAILED", ex.Message);
                            MessageBox.Show("The update was downloaded but could not be launched.\r\n\r\n" + ex.Message, "Print Interceptor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Write("UPDATE_DOWNLOAD_FAILED", ex.Message);
                    InvokeOnUi(delegate
                    {
                        MessageBox.Show("The update download or checksum verification failed.\r\n\r\n" + ex.Message, "Print Interceptor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                }
            });
        }

        private void InvokeOnUi(Action action)
        {
            if (_disposing) return;
            try { _dispatcher.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        private void StartMonitor()
        {
            try
            {
                string printerError;
                if (!RawPrinter.CanOpen(_settings.PrinterName, out printerError))
                    throw new InvalidOperationException("Cannot open printer '" + _settings.PrinterName + "': " + printerError);

                string classifierStatus;
                try
                {
                    _flowhubMonitor = new FlowhubJobMonitor(_settings);
                    _flowhubMonitor.MonitorFailed += OnMonitorFailed;
                    _flowhubMonitor.Start();
                    classifierStatus = "Flowhub classification active";
                }
                catch (Exception classifierError)
                {
                    if (_flowhubMonitor != null)
                    {
                        _flowhubMonitor.Dispose();
                        _flowhubMonitor = null;
                    }
                    classifierStatus = "classification unavailable; unknown-job prompts remain enabled";
                    Logger.Write("CLASSIFIER_ERROR", classifierError.ToString());
                }

                _monitor = new PrintEventMonitor(_settings.PrinterName);
                _monitor.JobPrinted += OnJobPrinted;
                _monitor.MonitorFailed += OnMonitorFailed;
                _monitor.Start();
                _status = "Watching " + _settings.PrinterName + "; " + classifierStatus;
                _trayIcon.Text = Truncate("Print Interceptor - " + _status, 63);
                Logger.Write("START", _status);
                _trayIcon.ShowBalloonTip(3000, "Print Interceptor", _status + ". The drawer remains locked unless you approve a prompt.", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                _status = "NOT WATCHING - " + ex.Message;
                _trayIcon.Text = "Print Interceptor - ERROR";
                Logger.Write("MONITOR_ERROR", ex.ToString());
                MessageBox.Show(
                    _status + "\r\n\r\nThe drawer will not be opened by Print Interceptor.",
                    "Print Interceptor is not active",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void OnJobPrinted(PrintJobNotice notice)
        {
            if (_disposing) return;
            if (ConsumeExpectedPulseEvent(notice))
            {
                Logger.Write("DRAWER_PULSE_EVENT_SUPPRESSED", "Bytes=" + notice.ByteCount);
                return;
            }
            FlowhubPrintCandidate classification = null;
            if (_flowhubMonitor != null)
            {
                classification = _flowhubMonitor.ConsumeForPrintedJob(DateTime.UtcNow);
            }
            try
            {
                FlowhubPrintCandidate captured = classification;
                _dispatcher.BeginInvoke(new Action(delegate
                {
                    HandlePrintedJob(notice, captured);
                }));
            }
            catch (InvalidOperationException)
            {
                // The application is shutting down.
            }
        }

        private void HandlePrintedJob(PrintJobNotice notice, FlowhubPrintCandidate classification)
        {
            if (classification == null)
            {
                Logger.Write("CLASSIFICATION_MISSING", "No Flowhub source PDF matched the Windows print event.");
                Enqueue(notice);
                return;
            }

            if (classification.Kind == FlowhubJobKind.Fulfillment)
            {
                Logger.Write("DRAWER_SUPPRESSED_FULFILLMENT", classification.Evidence);
                _trayIcon.ShowBalloonTip(
                    2000,
                    "Fulfillment ticket",
                    "Fulfillment ticket detected; drawer kept locked.",
                    ToolTipIcon.Info);
                return;
            }

            if (classification.Kind == FlowhubJobKind.TransactionReceipt)
            {
                OpenDrawer("Automatic transaction receipt", true);
                return;
            }

            Logger.Write("CLASSIFICATION_UNKNOWN", classification.Evidence);
            Enqueue(new PrintJobNotice(
                "Unrecognized Flowhub print (" + classification.FileName + ")",
                notice.UserName,
                false));
        }

        private void OnMonitorFailed(Exception ex)
        {
            _status = "Monitor error - " + ex.Message;
            Logger.Write("MONITOR_ERROR", ex.ToString());
            if (_disposing) return;
            try
            {
                _dispatcher.BeginInvoke(new Action(delegate
                {
                    _trayIcon.Text = "Print Interceptor - ERROR";
                    _trayIcon.ShowBalloonTip(5000, "Print Interceptor monitor error", ex.Message, ToolTipIcon.Error);
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void Enqueue(PrintJobNotice notice)
        {
            _pending.Enqueue(notice);
            ShowNextPrompt();
        }

        private void ShowNextPrompt()
        {
            if (_promptActive || _pending.Count == 0 || _disposing) return;
            _promptActive = true;
            PrintJobNotice notice = _pending.Dequeue();

            using (var prompt = new AuthorizationForm(_settings.PromptTimeoutSeconds, notice))
            {
                DialogResult result = prompt.ShowDialog(_dispatcher);
                if (result == DialogResult.Yes)
                {
                    OpenDrawer("Operator approval; Document=" + notice.DocumentName, false);
                }
                else
                {
                    Logger.Write(prompt.TimedOut ? "DRAWER_DENIED_TIMEOUT" : "DRAWER_DENIED", "Document=" + notice.DocumentName);
                }
            }

            _promptActive = false;
            if (_pending.Count > 0) _dispatcher.BeginInvoke(new Action(ShowNextPrompt));
        }

        private void OpenDrawer(string reason, bool automatic)
        {
            RegisterExpectedPulseEvent();
            try
            {
                byte[] command = DrawerCommand.Build(
                    _settings.PulseOnMilliseconds,
                    _settings.PulseOffMilliseconds);
                RawPrinter.Send(_settings.PrinterName, PrintEventMonitor.DrawerDocumentName, command);
                Logger.Write(automatic ? "DRAWER_OPEN_AUTOMATIC" : "DRAWER_OPEN_APPROVED", reason);
            }
            catch (Exception ex)
            {
                CancelExpectedPulseEvent();
                Logger.Write("DRAWER_OPEN_FAILED", ex.ToString());
                MessageBox.Show(
                    "The drawer command failed. The drawer was not intentionally opened.\r\n\r\n" + ex.Message,
                    "Print Interceptor",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void RegisterExpectedPulseEvent()
        {
            lock (_pulseGate)
            {
                _expectedPulseEvents++;
                _pulseExpectationExpiresUtc = DateTime.UtcNow.AddSeconds(15);
            }
        }

        private void CancelExpectedPulseEvent()
        {
            lock (_pulseGate)
            {
                if (_expectedPulseEvents > 0) _expectedPulseEvents--;
            }
        }

        private bool ConsumeExpectedPulseEvent(PrintJobNotice notice)
        {
            lock (_pulseGate)
            {
                if (_expectedPulseEvents == 0) return false;
                if (DateTime.UtcNow > _pulseExpectationExpiresUtc)
                {
                    _expectedPulseEvents = 0;
                    return false;
                }

                // Star's driver rewrites the document title, so pair our expectation
                // with the exact five-byte StarPRNT command size instead.
                if (notice.ByteCount != 5) return false;
                _expectedPulseEvents--;
                return true;
            }
        }

        private void ShowStatus()
        {
            MessageBox.Show(
                "Status: " + _status + "\r\n\r\n" +
                "Printer: " + _settings.PrinterName + "\r\n" +
                "Unanswered prompts lock after: " + _settings.PromptTimeoutSeconds + " seconds\r\n\r\n" +
                "Important: both Star driver Peripheral Unit Timing settings must be None.",
                "Print Interceptor diagnostics",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void OpenLog()
        {
            Logger.Write("LOG_VIEWED", string.Empty);
            string directory = Path.GetDirectoryName(Logger.LogPath);
            Directory.CreateDirectory(directory);
            Process.Start("explorer.exe", "/select,\"" + Logger.LogPath + "\"");
        }

        private void ExitApplication()
        {
            if (MessageBox.Show(
                "Exit Print Interceptor? Receipts will still print, but authorization prompts will stop and this application will not open the drawer.",
                "Print Interceptor",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }
            Logger.Write("EXIT", "Operator exited application.");
            ExitThread();
        }

        public void RequestExternalStop()
        {
            Logger.Write("STOP_REQUESTED", "Desktop start/stop shortcut.");
            InvokeOnUi(delegate { ExitThread(); });
        }

        protected override void ExitThreadCore()
        {
            Dispose();
            base.ExitThreadCore();
        }

        public new void Dispose()
        {
            if (_disposing) return;
            _disposing = true;
            if (_updateTimer != null)
            {
                _updateTimer.Dispose();
                _updateTimer = null;
            }
            if (_monitor != null)
            {
                _monitor.JobPrinted -= OnJobPrinted;
                _monitor.MonitorFailed -= OnMonitorFailed;
                _monitor.Dispose();
                _monitor = null;
            }
            if (_flowhubMonitor != null)
            {
                _flowhubMonitor.MonitorFailed -= OnMonitorFailed;
                _flowhubMonitor.Dispose();
                _flowhubMonitor = null;
            }
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _dispatcher.Dispose();
        }

        private static string Truncate(string value, int maximum)
        {
            return value.Length <= maximum ? value : value.Substring(0, maximum);
        }
    }

    internal sealed class AuthorizationForm : Form
    {
        private readonly System.Windows.Forms.Timer _timer;
        private int _secondsRemaining;
        private readonly Label _countdown;

        public bool TimedOut { get; private set; }

        public AuthorizationForm(int timeoutSeconds, PrintJobNotice notice)
        {
            Text = "Cash Drawer Authorization";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            ClientSize = new Size(500, 245);
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

            var title = new Label();
            title.Text = notice.IsManualRequest ? "Open the cash drawer?" : "A receipt has finished printing.";
            title.Font = new Font(Font, FontStyle.Bold);
            title.AutoSize = false;
            title.TextAlign = ContentAlignment.MiddleCenter;
            title.SetBounds(20, 20, 460, 32);

            var question = new Label();
            question.Text = "Would you like to open the cash drawer?";
            question.AutoSize = false;
            question.TextAlign = ContentAlignment.MiddleCenter;
            question.SetBounds(20, 58, 460, 28);

            var detail = new Label();
            detail.Text = notice.IsManualRequest ? "Manual operator request" : "Document: " + SafeDisplay(notice.DocumentName);
            detail.AutoEllipsis = true;
            detail.AutoSize = false;
            detail.TextAlign = ContentAlignment.MiddleCenter;
            detail.ForeColor = Color.DimGray;
            detail.SetBounds(30, 92, 440, 25);

            _countdown = new Label();
            _countdown.AutoSize = false;
            _countdown.TextAlign = ContentAlignment.MiddleCenter;
            _countdown.ForeColor = Color.DimGray;
            _countdown.SetBounds(20, 122, 460, 24);

            var keepLocked = new Button();
            keepLocked.Text = "Keep Locked";
            keepLocked.DialogResult = DialogResult.No;
            keepLocked.SetBounds(95, 174, 140, 42);

            var openDrawer = new Button();
            openDrawer.Text = "Open Drawer";
            openDrawer.DialogResult = DialogResult.Yes;
            openDrawer.SetBounds(265, 174, 140, 42);

            Controls.Add(title);
            Controls.Add(question);
            Controls.Add(detail);
            Controls.Add(_countdown);
            Controls.Add(keepLocked);
            Controls.Add(openDrawer);

            AcceptButton = keepLocked;
            CancelButton = keepLocked;
            ActiveControl = keepLocked;

            _secondsRemaining = timeoutSeconds;
            UpdateCountdown();
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 1000;
            _timer.Tick += OnTick;
            _timer.Start();

            Shown += delegate
            {
                Activate();
                BringToFront();
                NativeMethods.SetForegroundWindow(Handle);
            };
        }

        private void OnTick(object sender, EventArgs e)
        {
            _secondsRemaining--;
            if (_secondsRemaining <= 0)
            {
                TimedOut = true;
                DialogResult = DialogResult.No;
                Close();
                return;
            }
            UpdateCountdown();
        }

        private void UpdateCountdown()
        {
            _countdown.Text = "Defaults to Keep Locked in " + _secondsRemaining + " seconds";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.None) DialogResult = DialogResult.No;
            _timer.Stop();
            base.OnFormClosing(e);
        }

        private static string SafeDisplay(string value)
        {
            string safe = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            return safe.Length <= 80 ? safe : safe.Substring(0, 77) + "...";
        }
    }

    internal static class DrawerCommand
    {
        public static byte[] Build(int onMilliseconds, int offMilliseconds)
        {
            if (onMilliseconds < 10 || onMilliseconds > 1270 || onMilliseconds % 10 != 0)
                throw new ArgumentOutOfRangeException("onMilliseconds");
            if (offMilliseconds < 10 || offMilliseconds > 1270 || offMilliseconds % 10 != 0)
                throw new ArgumentOutOfRangeException("offMilliseconds");

            // StarPRNT: ESC BEL n1 n2 sets peripheral-unit-1 pulse timing;
            // BEL then executes that pulse. Units are 10 milliseconds.
            return new byte[]
            {
                0x1b, 0x07,
                (byte)(onMilliseconds / 10),
                (byte)(offMilliseconds / 10),
                0x07
            };
        }
    }

    internal sealed class GitHubRelease
    {
        public Version Version;
        public string InstallerUrl;
        public string ChecksumUrl;
    }

    internal static class GitHubUpdateService
    {
        private const string InstallerAsset = "PrintInterceptorSetup.exe";
        private const string ChecksumAsset = "PrintInterceptorSetup.exe.sha256";

        public static GitHubRelease GetLatest(string repository)
        {
            if (!Regex.IsMatch(repository ?? string.Empty, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
                throw new ConfigurationErrorsException("ReleaseRepository must be in owner/repository form.");

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string json;
            using (WebClient client = Client())
                json = client.DownloadString("https://api.github.com/repos/" + repository + "/releases/latest");

            return ParseRelease(json);
        }

        internal static GitHubRelease ParseRelease(string json)
        {
            var serializer = new JavaScriptSerializer();
            var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("GitHub returned an invalid release response.");
            object tagObject;
            if (!root.TryGetValue("tag_name", out tagObject))
                throw new InvalidDataException("The latest GitHub release has no tag.");
            Version version = ParseVersion(Convert.ToString(tagObject));

            string installerUrl = null;
            string checksumUrl = null;
            object assetsObject;
            object[] assets = root.TryGetValue("assets", out assetsObject) ? assetsObject as object[] : null;
            if (assets != null)
            {
                foreach (object item in assets)
                {
                    var asset = item as Dictionary<string, object>;
                    if (asset == null) continue;
                    string name = Value(asset, "name");
                    string url = Value(asset, "browser_download_url");
                    if (string.Equals(name, InstallerAsset, StringComparison.OrdinalIgnoreCase)) installerUrl = url;
                    if (string.Equals(name, ChecksumAsset, StringComparison.OrdinalIgnoreCase)) checksumUrl = url;
                }
            }

            if (string.IsNullOrWhiteSpace(installerUrl) || string.IsNullOrWhiteSpace(checksumUrl))
                throw new InvalidDataException("The latest release is missing the installer or checksum asset.");
            return new GitHubRelease { Version = version, InstallerUrl = installerUrl, ChecksumUrl = checksumUrl };
        }

        public static string DownloadAndVerify(GitHubRelease release)
        {
            string directory = Path.Combine(Path.GetTempPath(), "PrintInterceptorUpdates");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "PrintInterceptorSetup-" + release.Version.ToString(3) + ".exe");
            string checksumText;
            using (WebClient client = Client())
            {
                client.DownloadFile(release.InstallerUrl, path);
                checksumText = client.DownloadString(release.ChecksumUrl);
            }

            Match expectedMatch = Regex.Match(checksumText ?? string.Empty, "(?i)\\b[0-9a-f]{64}\\b");
            if (!expectedMatch.Success) throw new InvalidDataException("Release checksum file is invalid.");
            string expected = expectedMatch.Value.ToUpperInvariant();
            string actual;
            using (var stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(path); } catch { }
                throw new InvalidDataException("Downloaded installer checksum does not match the GitHub release.");
            }
            return path;
        }

        private static WebClient Client()
        {
            var client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] = "Flowhub-Print-Interceptor";
            client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            client.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            return client;
        }

        private static Version ParseVersion(string tag)
        {
            string value = (tag ?? string.Empty).Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
            Version version;
            if (!Version.TryParse(value, out version))
                throw new InvalidDataException("Release tag is not a supported version: " + tag);
            return version;
        }

        private static string Value(Dictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) ? Convert.ToString(value) : string.Empty;
        }
    }

    internal static class RawPrinter
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private class DocInfo1
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string DocumentName;
            [MarshalAs(UnmanagedType.LPWStr)] public string OutputFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool OpenPrinter(string printerName, out IntPtr printer, IntPtr defaults);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr printer);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int StartDocPrinter(IntPtr printer, int level, [In] DocInfo1 docInfo);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr printer);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr printer);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr printer);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr printer, byte[] bytes, int count, out int written);

        public static bool CanOpen(string printerName, out string error)
        {
            IntPtr printer;
            if (!OpenPrinter(printerName, out printer, IntPtr.Zero))
            {
                error = LastError();
                return false;
            }
            ClosePrinter(printer);
            error = string.Empty;
            return true;
        }

        public static void Send(string printerName, string documentName, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new ArgumentException("No printer data was supplied.", "bytes");

            IntPtr printer;
            if (!OpenPrinter(printerName, out printer, IntPtr.Zero))
                throw new InvalidOperationException("OpenPrinter failed: " + LastError());

            bool documentStarted = false;
            bool pageStarted = false;
            try
            {
                var info = new DocInfo1();
                info.DocumentName = documentName;
                info.DataType = "RAW";
                if (StartDocPrinter(printer, 1, info) == 0)
                    throw new InvalidOperationException("StartDocPrinter failed: " + LastError());
                documentStarted = true;

                if (!StartPagePrinter(printer))
                    throw new InvalidOperationException("StartPagePrinter failed: " + LastError());
                pageStarted = true;

                int written;
                if (!WritePrinter(printer, bytes, bytes.Length, out written))
                    throw new InvalidOperationException("WritePrinter failed: " + LastError());
                if (written != bytes.Length)
                    throw new IOException("Only " + written + " of " + bytes.Length + " command bytes reached the spooler.");
            }
            finally
            {
                if (pageStarted) EndPagePrinter(printer);
                if (documentStarted) EndDocPrinter(printer);
                ClosePrinter(printer);
            }
        }

        private static string LastError()
        {
            return new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
    }

    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr window);
    }
}
