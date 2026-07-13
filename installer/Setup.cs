using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

namespace PrintInterceptorSetup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (HasArgument(args, "--resource-test"))
                return Payload.Exists() ? 0 : 2;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (HasArgument(args, "--update"))
            {
                try
                {
                    Installer.UpdateExisting();
                    MessageBox.Show(
                        "Print Interceptor was updated. This terminal's existing configuration was preserved.",
                        "Update complete",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString(), "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
            Application.Run(new SetupForm());
            return 0;
        }

        private static bool HasArgument(string[] args, string wanted)
        {
            foreach (string arg in args)
                if (string.Equals(arg, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    internal sealed class PrinterChoice
    {
        public string Name;
        public string Driver;
        public string Port;

        public override string ToString()
        {
            return Name;
        }
    }

    internal sealed class FlowhubConfiguration
    {
        public string Path;
        public string ReceiptPrinter;
        public string FulfillmentPrinter;
    }

    internal static class SetupDiscovery
    {
        public static List<PrinterChoice> Printers()
        {
            var result = new List<PrinterChoice>();
            var details = new Dictionary<string, PrinterChoice>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DriverName, PortName FROM Win32_Printer"))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        string name = Convert.ToString(item["Name"]);
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        details[name] = new PrinterChoice
                        {
                            Name = name,
                            Driver = Convert.ToString(item["DriverName"]),
                            Port = Convert.ToString(item["PortName"])
                        };
                    }
                }
            }
            catch { }

            foreach (string name in PrinterSettings.InstalledPrinters)
            {
                PrinterChoice choice;
                if (!details.TryGetValue(name, out choice))
                    choice = new PrinterChoice { Name = name, Driver = "Unknown", Port = "Unknown" };
                result.Add(choice);
            }
            result.Sort(delegate(PrinterChoice a, PrinterChoice b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        public static FlowhubConfiguration FindFlowhubConfiguration()
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidates = new List<string>();
            try
            {
                foreach (string directory in Directory.GetDirectories(roaming))
                {
                    string path = Path.Combine(directory, "appData.json");
                    if (File.Exists(path)) candidates.Add(path);
                }
            }
            catch { }

            foreach (string path in candidates)
            {
                try
                {
                    string json = File.ReadAllText(path);
                    string receipt = JsonString(json, "receipt");
                    string fulfillment = JsonString(json, "fulfillment");
                    if (!string.IsNullOrWhiteSpace(receipt) || !string.IsNullOrWhiteSpace(fulfillment))
                    {
                        return new FlowhubConfiguration
                        {
                            Path = path,
                            ReceiptPrinter = receipt,
                            FulfillmentPrinter = fulfillment
                        };
                    }
                }
                catch { }
            }
            return null;
        }

        public static string FindFlowhubPrintDirectory()
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidates = new List<string>();
            candidates.Add(Path.Combine(roaming, "FlowhubMaui", "print-util", "printFiles"));
            try
            {
                foreach (string directory in Directory.GetDirectories(roaming))
                    candidates.Add(Path.Combine(directory, "print-util", "printFiles"));
            }
            catch { }

            foreach (string candidate in candidates)
            {
                try
                {
                    if (Directory.Exists(candidate) &&
                        (File.Exists(Path.Combine(candidate, "test-receipt.pdf")) ||
                         Directory.GetFiles(candidate, "*-receipt.pdf").Length > 0))
                        return candidate;
                }
                catch { }
            }
            return Path.Combine(roaming, "FlowhubMaui", "print-util", "printFiles");
        }

        public static string FindFlowhubExecutable()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string stub = Path.Combine(local, "FlowhubMaui", "FlowhubMaui.exe");
            return File.Exists(stub) ? stub : null;
        }

        public static Dictionary<string, string> ExistingInterceptorSettings()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string config = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PrintInterceptor", "PrintInterceptor.exe.config");
            if (!File.Exists(config)) return result;
            try
            {
                var document = new XmlDocument();
                document.Load(config);
                XmlNodeList nodes = document.SelectNodes("/configuration/appSettings/add");
                foreach (XmlNode node in nodes)
                {
                    if (node.Attributes == null) continue;
                    XmlAttribute key = node.Attributes["key"];
                    XmlAttribute value = node.Attributes["value"];
                    if (key != null && value != null) result[key.Value] = value.Value;
                }
            }
            catch { }
            return result;
        }

        private static string JsonString(string json, string key)
        {
            Match match = Regex.Match(
                json,
                "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"",
                RegexOptions.IgnoreCase);
            if (!match.Success) return string.Empty;
            return Regex.Unescape(match.Groups["value"].Value);
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly ComboBox _printer = new ComboBox();
        private readonly Label _printerDetails = new Label();
        private readonly Label _flowhubStatus = new Label();
        private readonly CheckBox _flowhubConfirmed = new CheckBox();
        private readonly CheckBox _timingOne = new CheckBox();
        private readonly CheckBox _timingTwo = new CheckBox();
        private readonly TextBox _receiptHeader = new TextBox();
        private readonly TextBox _fulfillmentHeader = new TextBox();
        private readonly TextBox _flowhubDirectory = new TextBox();
        private readonly CheckBox _startWithWindows = new CheckBox();
        private readonly CheckBox _desktopShortcut = new CheckBox();
        private readonly Button _install = new Button();
        private readonly Label _status = new Label();
        private FlowhubConfiguration _flowhubConfiguration;

        public SetupForm()
        {
            Text = "Print Interceptor Setup";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 850);
            MinimumSize = new Size(776, 889);
            Font = new Font("Segoe UI", 9.5F);
            Icon = SystemIcons.Shield;

            var title = new Label
            {
                Text = "Print Interceptor Setup",
                Font = new Font("Segoe UI", 18F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(24, 18)
            };
            var intro = new Label
            {
                Text = "Configure safe, automatic cash-drawer control for this Windows 11 Flowhub station.",
                AutoSize = true,
                Location = new Point(28, 56)
            };
            Controls.Add(title);
            Controls.Add(intro);

            BuildPrinterGroup();
            BuildFlowhubGroup();
            BuildDrawerGroup();
            BuildClassificationGroup();
            BuildStartupGroup();

            _install.Text = "Install and Start";
            _install.Font = new Font(Font, FontStyle.Bold);
            _install.SetBounds(555, 789, 170, 42);
            _install.Click += InstallClicked;
            Controls.Add(_install);

            _status.SetBounds(28, 792, 510, 38);
            _status.Text = "Complete all five sections, then install.";
            Controls.Add(_status);

            Load += delegate { RefreshEverything(); };
        }

        private void BuildPrinterGroup()
        {
            GroupBox group = Group("1. Select the Windows receipt printer", 24, 88, 712, 112);
            var label = new Label { Text = "Receipt printer:", AutoSize = true, Location = new Point(18, 31) };
            _printer.DropDownStyle = ComboBoxStyle.DropDownList;
            _printer.SetBounds(130, 27, 550, 30);
            _printer.SelectedIndexChanged += delegate { PrinterChanged(); };
            _printerDetails.SetBounds(130, 65, 550, 25);
            _printerDetails.ForeColor = Color.DimGray;
            group.Controls.Add(label);
            group.Controls.Add(_printer);
            group.Controls.Add(_printerDetails);
        }

        private void BuildFlowhubGroup()
        {
            GroupBox group = Group("2. Verify Flowhub printer routing", 24, 210, 712, 154);
            _flowhubStatus.SetBounds(18, 28, 665, 48);
            var open = new Button { Text = "Open Flowhub", Location = new Point(18, 82), Size = new Size(120, 30) };
            open.Click += delegate
            {
                string path = SetupDiscovery.FindFlowhubExecutable();
                if (path == null) MessageBox.Show("Flowhub Maui was not found in the expected Local AppData location.");
                else Process.Start(path);
            };
            var refresh = new Button { Text = "Refresh detection", Location = new Point(148, 82), Size = new Size(135, 30) };
            refresh.Click += delegate { RefreshFlowhub(); };
            _flowhubConfirmed.Text = "I verified Flowhub Receipts and Fulfillment both use the selected printer.";
            _flowhubConfirmed.SetBounds(18, 119, 650, 24);
            group.Controls.Add(_flowhubStatus);
            group.Controls.Add(open);
            group.Controls.Add(refresh);
            group.Controls.Add(_flowhubConfirmed);
        }

        private void BuildDrawerGroup()
        {
            GroupBox group = Group("3. Disable automatic drawer timing in the Star driver", 24, 374, 712, 126);
            var open = new Button { Text = "Open Printer Properties", Location = new Point(18, 28), Size = new Size(180, 30) };
            open.Click += delegate { OpenPrinterProperties(); };
            var note = new Label
            {
                Text = "On Device Settings, set both timing values to None, then Apply.",
                AutoSize = true,
                Location = new Point(214, 35)
            };
            _timingOne.Text = "Peripheral Unit 1 - Timing is None";
            _timingOne.SetBounds(18, 70, 310, 24);
            _timingTwo.Text = "Peripheral Unit 2 - Timing is None";
            _timingTwo.SetBounds(350, 70, 310, 24);
            group.Controls.Add(open);
            group.Controls.Add(note);
            group.Controls.Add(_timingOne);
            group.Controls.Add(_timingTwo);
        }

        private void BuildClassificationGroup()
        {
            GroupBox group = Group("4. Confirm Flowhub receipt markers", 24, 510, 712, 150);
            group.Controls.Add(new Label { Text = "Transaction header:", AutoSize = true, Location = new Point(18, 31) });
            _receiptHeader.SetBounds(155, 27, 205, 28);
            group.Controls.Add(_receiptHeader);
            group.Controls.Add(new Label { Text = "Fulfillment header:", AutoSize = true, Location = new Point(375, 31) });
            _fulfillmentHeader.SetBounds(505, 27, 180, 28);
            group.Controls.Add(_fulfillmentHeader);
            group.Controls.Add(new Label { Text = "Flowhub PDF folder:", AutoSize = true, Location = new Point(18, 73) });
            _flowhubDirectory.SetBounds(155, 69, 452, 28);
            var browse = new Button { Text = "Browse...", Location = new Point(615, 67), Size = new Size(70, 30) };
            browse.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.SelectedPath = _flowhubDirectory.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) _flowhubDirectory.Text = dialog.SelectedPath;
                }
            };
            group.Controls.Add(browse);
            var note = new Label
            {
                Text = "Both Flowhub's internal filename type and the matching PDF header are required for automatic action.",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Location = new Point(18, 112)
            };
            group.Controls.Add(note);
        }

        private void BuildStartupGroup()
        {
            GroupBox group = Group("5. Choose startup and desktop controls", 24, 670, 712, 105);
            _startWithWindows.Text = "Start Print Interceptor automatically when this Windows user signs in (recommended).";
            _startWithWindows.Checked = true;
            _startWithWindows.SetBounds(18, 27, 665, 24);
            _desktopShortcut.Text = "Add a desktop shortcut that asks before starting or stopping Print Interceptor.";
            _desktopShortcut.Checked = true;
            _desktopShortcut.SetBounds(18, 55, 665, 24);
            var note = new Label
            {
                Text = "The shortcut never opens the drawer; it only turns receipt monitoring on or off.",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Location = new Point(37, 80)
            };
            group.Controls.Add(_startWithWindows);
            group.Controls.Add(_desktopShortcut);
            group.Controls.Add(note);
        }

        private GroupBox Group(string text, int x, int y, int width, int height)
        {
            var group = new GroupBox { Text = text };
            group.SetBounds(x, y, width, height);
            Controls.Add(group);
            return group;
        }

        private void RefreshEverything()
        {
            Dictionary<string, string> existing = SetupDiscovery.ExistingInterceptorSettings();
            string oldPrinter;
            existing.TryGetValue("PrinterName", out oldPrinter);

            _printer.Items.Clear();
            List<PrinterChoice> printers = SetupDiscovery.Printers();
            int preferred = -1;
            for (int i = 0; i < printers.Count; i++)
            {
                _printer.Items.Add(printers[i]);
                if (!string.IsNullOrWhiteSpace(oldPrinter) &&
                    string.Equals(printers[i].Name, oldPrinter, StringComparison.OrdinalIgnoreCase)) preferred = i;
                else if (preferred < 0 && printers[i].Driver.IndexOf("Star", StringComparison.OrdinalIgnoreCase) >= 0) preferred = i;
            }
            if (_printer.Items.Count > 0) _printer.SelectedIndex = preferred >= 0 ? preferred : 0;

            string value;
            _receiptHeader.Text = existing.TryGetValue("ReceiptHeader", out value) ? value : string.Empty;
            _fulfillmentHeader.Text = existing.TryGetValue("FulfillmentHeader", out value) ? value : "In-Store-Fulfillment";
            _flowhubDirectory.Text = existing.TryGetValue("FlowhubPrintDirectory", out value)
                ? Environment.ExpandEnvironmentVariables(value)
                : SetupDiscovery.FindFlowhubPrintDirectory();
            if (existing.Count > 0)
            {
                _startWithWindows.Checked = Installer.BooleanSetting(existing, "StartWithWindows", Installer.StartupEnabled());
                _desktopShortcut.Checked = Installer.BooleanSetting(existing, "DesktopShortcut", true);
            }
            RefreshFlowhub();
        }

        private void PrinterChanged()
        {
            PrinterChoice selected = _printer.SelectedItem as PrinterChoice;
            _printerDetails.Text = selected == null ? string.Empty :
                "Driver: " + selected.Driver + "     Port: " + selected.Port;
            RefreshFlowhubStatus();
        }

        private void RefreshFlowhub()
        {
            _flowhubConfiguration = SetupDiscovery.FindFlowhubConfiguration();
            RefreshFlowhubStatus();
        }

        private void RefreshFlowhubStatus()
        {
            PrinterChoice selected = _printer.SelectedItem as PrinterChoice;
            if (_flowhubConfiguration == null)
            {
                _flowhubStatus.Text = "Flowhub printer configuration was not detected. In Flowhub, press Alt > Printers and select the printer above for Receipts and Fulfillment.";
                _flowhubStatus.ForeColor = Color.DarkOrange;
                return;
            }

            bool matches = selected != null &&
                string.Equals(_flowhubConfiguration.ReceiptPrinter, selected.Name, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(_flowhubConfiguration.FulfillmentPrinter, selected.Name, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(_flowhubConfiguration.FulfillmentPrinter, "Use Receipt Printer", StringComparison.OrdinalIgnoreCase) ||
                 string.IsNullOrWhiteSpace(_flowhubConfiguration.FulfillmentPrinter));
            _flowhubStatus.Text = "Detected: Receipts = " + Empty(_flowhubConfiguration.ReceiptPrinter) +
                " | Fulfillment = " + Empty(_flowhubConfiguration.FulfillmentPrinter) +
                "\r\nConfig: " + _flowhubConfiguration.Path;
            _flowhubStatus.ForeColor = matches ? Color.DarkGreen : Color.DarkOrange;
            if (matches) _flowhubConfirmed.Checked = true;
        }

        private void OpenPrinterProperties()
        {
            PrinterChoice selected = _printer.SelectedItem as PrinterChoice;
            if (selected == null) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = "printui.dll,PrintUIEntry /p /n \"" + selected.Name.Replace("\"", "") + "\"",
                UseShellExecute = true
            });
        }

        private void InstallClicked(object sender, EventArgs e)
        {
            PrinterChoice selected = _printer.SelectedItem as PrinterChoice;
            if (selected == null) { Fail("Select a receipt printer."); return; }
            if (!_flowhubConfirmed.Checked) { Fail("Verify Flowhub's receipt and fulfillment printer selections."); return; }
            if (!_timingOne.Checked || !_timingTwo.Checked) { Fail("Confirm that both Star peripheral timing values are None."); return; }
            if (string.IsNullOrWhiteSpace(_receiptHeader.Text) || string.IsNullOrWhiteSpace(_fulfillmentHeader.Text))
            { Fail("Both classification headers are required."); return; }
            if (!Directory.Exists(_flowhubDirectory.Text)) { Fail("The Flowhub PDF folder does not exist."); return; }

            _install.Enabled = false;
            UseWaitCursor = true;
            _status.ForeColor = Color.Black;
            _status.Text = "Installing...";
            Application.DoEvents();
            try
            {
                Installer.Install(
                    selected.Name,
                    _flowhubDirectory.Text,
                    _receiptHeader.Text.Trim(),
                    _fulfillmentHeader.Text.Trim(),
                    _startWithWindows.Checked,
                    _desktopShortcut.Checked);
                _status.ForeColor = Color.DarkGreen;
                _status.Text = "Installed and running. Look for the shield icon in the notification area.";
                MessageBox.Show(
                    this,
                    "Print Interceptor is installed and running.\r\n\r\n" +
                    (_startWithWindows.Checked ? "It will start automatically when this Windows user signs in.\r\n" : "Automatic startup is disabled.\r\n") +
                    (_desktopShortcut.Checked ? "A Start or Stop shortcut was added to the desktop.\r\n\r\n" : "No desktop shortcut was created.\r\n\r\n") +
                    "Test a real fulfillment ticket first: it should print while the drawer stays locked. " +
                    "Then test a transaction receipt: the drawer should open once without a prompt.",
                    "Installation complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _status.ForeColor = Color.DarkRed;
                _status.Text = "Installation failed: " + ex.Message;
                MessageBox.Show(this, ex.ToString(), "Installation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _install.Enabled = true;
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        private void Fail(string message)
        {
            _status.ForeColor = Color.DarkRed;
            _status.Text = message;
            MessageBox.Show(this, message, "Setup needs attention", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static string Empty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(uses receipt fallback)" : value;
        }
    }

    internal static class Payload
    {
        private const string ResourceName = "PrintInterceptor.Payload.exe";

        public static bool Exists()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                return stream != null && stream.Length > 0;
        }

        public static void WriteTo(string path)
        {
            using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (input == null) throw new InvalidOperationException("The embedded interceptor payload is missing.");
                using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
            }
        }
    }

    internal static class Installer
    {
        public static void UpdateExisting()
        {
            EnsureAdministrator();
            string installDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PrintInterceptor");
            string executable = Path.Combine(installDirectory, "PrintInterceptor.exe");
            string configuration = executable + ".config";
            if (!File.Exists(configuration))
                throw new FileNotFoundException("The installed terminal configuration was not found.", configuration);

            Dictionary<string, string> settings = SetupDiscovery.ExistingInterceptorSettings();
            bool startWithWindows = BooleanSetting(settings, "StartWithWindows", StartupEnabled());
            bool desktopShortcut;
            if (!TryBooleanSetting(settings, "DesktopShortcut", out desktopShortcut))
            {
                desktopShortcut = MessageBox.Show(
                    "Add a desktop shortcut that asks before starting or stopping Print Interceptor?",
                    "Print Interceptor desktop control",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1) == DialogResult.Yes;
            }

            StopExisting();
            Directory.CreateDirectory(installDirectory);
            Payload.WriteTo(executable);
            ConfigureStartup(executable, startWithWindows);
            ConfigureDesktopShortcut(executable, desktopShortcut);
            SetConfigurationBoolean(configuration, "StartWithWindows", startWithWindows);
            SetConfigurationBoolean(configuration, "DesktopShortcut", desktopShortcut);
            StartLimited(executable);
        }

        public static bool StartupEnabled()
        {
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
                return run != null && run.GetValue("PrintInterceptor") != null;
        }

        public static bool DesktopShortcutExists()
        {
            return File.Exists(DesktopShortcutPath());
        }

        public static bool BooleanSetting(Dictionary<string, string> settings, string key, bool fallback)
        {
            bool value;
            return TryBooleanSetting(settings, key, out value) ? value : fallback;
        }

        private static bool TryBooleanSetting(Dictionary<string, string> settings, string key, out bool value)
        {
            value = false;
            string text;
            return settings.TryGetValue(key, out text) && bool.TryParse(text, out value);
        }

        public static void Install(
            string printerName,
            string flowhubDirectory,
            string receiptHeader,
            string fulfillmentHeader,
            bool startWithWindows,
            bool desktopShortcut)
        {
            EnsureAdministrator();
            StopExisting();

            string installDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PrintInterceptor");
            Directory.CreateDirectory(installDirectory);
            string executable = Path.Combine(installDirectory, "PrintInterceptor.exe");
            Payload.WriteTo(executable);
            File.WriteAllText(executable + ".config", Configuration(
                printerName, flowhubDirectory, receiptHeader, fulfillmentHeader,
                startWithWindows, desktopShortcut), Encoding.UTF8);

            RunAndCheck(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wevtutil.exe"),
                "sl Microsoft-Windows-PrintService/Operational /e:true");

            ConfigureStartup(executable, startWithWindows);
            ConfigureDesktopShortcut(executable, desktopShortcut);

            StartLimited(executable);
        }

        private static void ConfigureStartup(string executable, bool enabled)
        {
            using (RegistryKey run = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
            {
                if (enabled)
                    run.SetValue("PrintInterceptor", "\"" + executable + "\"", RegistryValueKind.String);
                else
                    run.DeleteValue("PrintInterceptor", false);
            }
        }

        private static string DesktopShortcutPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "Print Interceptor - Start or Stop.lnk");
        }

        private static void ConfigureDesktopShortcut(string executable, bool enabled)
        {
            string shortcutPath = DesktopShortcutPath();
            if (!enabled)
            {
                if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
                return;
            }

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                throw new InvalidOperationException("Windows Script Host is unavailable, so the desktop shortcut could not be created.");

            object shell = null;
            object shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { shortcutPath });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { executable });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { "--toggle" });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(executable) });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Start or stop Print Interceptor" });
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { executable + ",0" });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }

        private static string Configuration(
            string printer,
            string directory,
            string receipt,
            string fulfillment,
            bool startWithWindows,
            bool desktopShortcut)
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\" ?>\r\n" +
                "<configuration>\r\n" +
                "  <startup useLegacyV2RuntimeActivationPolicy=\"true\"><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.8\" /></startup>\r\n" +
                "  <appSettings>\r\n" +
                Add("PrinterName", printer) +
                Add("PromptTimeoutSeconds", "30") +
                Add("PulseOnMilliseconds", "200") +
                Add("PulseOffMilliseconds", "200") +
                Add("FlowhubPrintDirectory", directory) +
                Add("ReceiptHeader", receipt) +
                Add("FulfillmentHeader", fulfillment) +
                Add("ClassificationWindowSeconds", "120") +
                Add("ReleaseRepository", "joegullo63/flowhub-print-interceptor") +
                Add("UpdateCheckHours", "6") +
                Add("StartWithWindows", startWithWindows.ToString()) +
                Add("DesktopShortcut", desktopShortcut.ToString()) +
                "  </appSettings>\r\n</configuration>\r\n";
        }

        private static void SetConfigurationBoolean(string path, string key, bool value)
        {
            var document = new XmlDocument();
            document.PreserveWhitespace = true;
            document.Load(path);
            XmlElement appSettings = document.SelectSingleNode("/configuration/appSettings") as XmlElement;
            if (appSettings == null) throw new InvalidDataException("The installed configuration has no appSettings section.");
            XmlElement setting = appSettings.SelectSingleNode("add[@key='" + key + "']") as XmlElement;
            if (setting == null)
            {
                setting = document.CreateElement("add");
                setting.SetAttribute("key", key);
                appSettings.AppendChild(setting);
            }
            setting.SetAttribute("value", value.ToString());
            document.Save(path);
        }

        private static string Add(string key, string value)
        {
            return "    <add key=\"" + SecurityElement.Escape(key) + "\" value=\"" +
                SecurityElement.Escape(value) + "\" />\r\n";
        }

        private static void EnsureAdministrator()
        {
            var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Setup must run as Administrator.");
        }

        private static void StopExisting()
        {
            foreach (Process process in Process.GetProcessesByName("PrintInterceptor"))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch { }
            }
        }

        private static void StartLimited(string executable)
        {
            string identity = WindowsIdentity.GetCurrent().Name.Replace("'", "''");
            string exe = executable.Replace("'", "''");
            string task = "PrintInterceptor-Start-" + Guid.NewGuid().ToString("N");
            string script =
                "$a=New-ScheduledTaskAction -Execute '" + exe + "';" +
                "$p=New-ScheduledTaskPrincipal -UserId '" + identity + "' -LogonType Interactive -RunLevel Limited;" +
                "Register-ScheduledTask -TaskName '" + task + "' -Action $a -Principal $p -Force|Out-Null;" +
                "try{Start-ScheduledTask -TaskName '" + task + "';Start-Sleep -Seconds 1}" +
                "finally{Unregister-ScheduledTask -TaskName '" + task + "' -Confirm:$false -ErrorAction SilentlyContinue}";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            RunAndCheck("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded);
        }

        private static void RunAndCheck(string file, string arguments)
        {
            using (Process process = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }))
            {
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(Path.GetFileName(file) + " failed with exit code " + process.ExitCode + ".");
            }
        }
    }
}
