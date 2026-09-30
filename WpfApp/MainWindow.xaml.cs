using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HexLoad.Properties;

namespace HexLoad
{
    public partial class MainWindow : Window
    {
        /// <summary>Maximum number of rows built for the hex view at once (1 MB of data).</summary>
        private const int MaxViewRows = 65536;

        /// <summary>Log length at which the older half of the log is dropped, characters.</summary>
        private const int MaxLogLength = 200_000;

        private FirmwareImage? _image;
        private int _currentMode = 1;      // 1 / 2 / 4 bytes per cell
        private int _viewOffset;           // hex view scroll offset (view only, never affects programming)
        private string _lastFileName = "firmware";

        private Bootloader? _bootloader;
        private CancellationTokenSource? _connectCts;
        private bool _busy;                // guards against concurrent port operations

        /// <summary>Cached "00".."FF" strings - avoids allocations when building the hex view.</summary>
        private static readonly string[] ByteText = CreateByteText();

        private static string[] CreateByteText()
        {
            var t = new string[256];
            for (int i = 0; i < 256; i++) t[i] = i.ToString("X2", CultureInfo.InvariantCulture);
            return t;
        }

        public MainWindow()
        {
            InitializeComponent();
            UpdatePortDisplay();
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Cancel first, then dispose - otherwise a running operation may touch a closed port
            _connectCts?.Cancel();
            _bootloader?.Dispose();
            _bootloader = null;
        }

        // ==================== Helpers ====================

        private void Log(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(() => Log(message));
                return;
            }

            if (LogTextBox.Text.Length > MaxLogLength)
            {
                // Drop the older half so the TextBox does not grow without bound
                string text = LogTextBox.Text;
                int cut = text.IndexOf('\n', text.Length / 2);
                LogTextBox.Text = cut >= 0 ? text.Substring(cut + 1) : string.Empty;
            }

            LogTextBox.AppendText(message + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        }

        private void UpdatePortDisplay()
        {
            string port = Settings.Default.ComPort;
            int baud = Settings.Default.BaudRate;
            ComPortTextBlock.Text = "COM Port: " + (string.IsNullOrEmpty(port) ? "(not set)" : port);
            BaudRateTextBlock.Text = $"Baudrate: {baud}";
        }

        /// <summary>Disables menu items while the port is in use (no concurrent commands).</summary>
        private void SetBusy(bool busy)
        {
            _busy = busy;
            MenuErase.IsEnabled = !busy;
            MenuReset.IsEnabled = !busy;
            MenuProgram.IsEnabled = !busy;
            MenuOpen.IsEnabled = !busy;
            MenuOptions.IsEnabled = !busy;
            System.Windows.Input.Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null;
        }

        private bool RequireConnection()
        {
            if (_bootloader == null)
            {
                Log("Not connected. Use Target -> Connect first.");
                return false;
            }
            return true;
        }

        // ==================== Hex view row model ====================

        public sealed class HexRow
        {
            private readonly string[] _cells;

            public HexRow(string address, string[] cells, string ascii)
            {
                Address = address;
                _cells = cells;
                Ascii = ascii;
            }

            public string Address { get; }
            public string Ascii { get; }

            // XAML bindings; indexed access instead of reflection
            public string B0 => _cells[0];
            public string B1 => _cells[1];
            public string B2 => _cells[2];
            public string B3 => _cells[3];
            public string B4 => _cells[4];
            public string B5 => _cells[5];
            public string B6 => _cells[6];
            public string B7 => _cells[7];
            public string B8 => _cells[8];
            public string B9 => _cells[9];
            public string BA => _cells[10];
            public string BB => _cells[11];
            public string BC => _cells[12];
            public string BD => _cells[13];
            public string BE => _cells[14];
            public string BF => _cells[15];
        }

        private void RefreshHexView()
        {
            if (_image == null || _image.Data.Length == 0)
            {
                HexGrid.ItemsSource = null;
                return;
            }

            byte[] data = _image.Data;
            uint baseAddr = _image.BaseAddress;
            int activeCols = 16 / _currentMode;

            for (int c = 1; c <= 16; c++)
            {
                var column = HexGrid.Columns[c];
                if (c <= activeCols)
                {
                    column.Visibility = Visibility.Visible;
                    column.Header = ((c - 1) * _currentMode).ToString("X", CultureInfo.InvariantCulture);
                    column.Width = _currentMode == 4 ? 90 : _currentMode == 2 ? 60 : 35;
                }
                else
                {
                    column.Visibility = Visibility.Collapsed;
                }
            }

            int start = _viewOffset & ~15;                       // align view start to 16 bytes
            int totalRows = (data.Length - start + 15) / 16;
            int rowCount = Math.Min(totalRows, MaxViewRows);

            var rows = new List<HexRow>(rowCount);
            var ascii = new char[16];

            for (int r = 0; r < rowCount; r++)
            {
                int i = start + r * 16;
                var cells = new string[16];

                for (int j = 0; j < 16; j++)
                {
                    if (j >= activeCols) { cells[j] = string.Empty; continue; }

                    int offset = i + j * _currentMode;
                    if (offset >= data.Length) { cells[j] = "--"; continue; }

                    if (_currentMode == 4)
                        cells[j] = offset + 3 < data.Length
                            ? BitConverter.ToUInt32(data, offset).ToString("X8", CultureInfo.InvariantCulture)
                            : ByteText[data[offset]];
                    else if (_currentMode == 2)
                        cells[j] = offset + 1 < data.Length
                            ? BitConverter.ToUInt16(data, offset).ToString("X4", CultureInfo.InvariantCulture)
                            : ByteText[data[offset]];
                    else
                        cells[j] = ByteText[data[offset]];
                }

                for (int k = 0; k < 16; k++)
                {
                    int offset = i + k;
                    if (offset < data.Length)
                    {
                        byte b = data[offset];
                        ascii[k] = b < 32 || b > 126 ? '.' : (char)b;
                    }
                    else ascii[k] = ' ';
                }

                rows.Add(new HexRow(
                    (baseAddr + (uint)i).ToString("X8", CultureInfo.InvariantCulture),
                    cells,
                    new string(ascii)));
            }

            HexGrid.ItemsSource = rows;

            StatusText.Text = rowCount < totalRows
                ? $"Showing 0x{baseAddr + (uint)start:X8}..0x{baseAddr + (uint)(start + rowCount * 16 - 1):X8} " +
                  $"(at most {MaxViewRows * 16} bytes are shown - enter an address to go further)"
                : $"View offset: 0x{start:X}";
        }

        // ==================== Open file ====================

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "All Supported|*.bin;*.hex;*.ihx;*.s19;*.s28;*.s37;*.mot;*.srec;*.txt;*.ti-txt" +
                         "|Binary files (*.bin)|*.bin" +
                         "|Intel HEX (*.hex;*.ihx)|*.hex;*.ihx" +
                         "|Motorola S-record (*.s19;*.s28;*.s37;*.mot;*.srec)|*.s19;*.s28;*.s37;*.mot;*.srec" +
                         "|TI-TXT (*.txt;*.ti-txt)|*.txt;*.ti-txt" +
                         "|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                string path = dialog.FileName;

                // Detect the format by extension, or by the file head if ambiguous.
                // Text formats are read line by line, binary directly as bytes (no double read).
                byte[] head = ReadHead(path, 512);
                var format = HexFile.DetectFormat(path, head);

                FirmwareImage image;
                if (format == HexFile.FileFormat.Binary)
                {
                    // A raw binary has no address - it is placed at the application address
                    image = new FirmwareImage(File.ReadAllBytes(path), SettingsWindow.GetAppAddress(), hasAddress: false);
                }
                else
                {
                    string[] lines = File.ReadAllLines(path);
                    image = format switch
                    {
                        HexFile.FileFormat.IntelHex => HexFile.ParseIntelHex(lines),
                        HexFile.FileFormat.SRecord => HexFile.ParseSRecord(lines),
                        _ => HexFile.ParseTiTxt(lines),
                    };
                }

                _image = image;
                _viewOffset = 0;
                _lastFileName = Path.GetFileNameWithoutExtension(path);
                AddressInput.Text = "0x0";

                FileNameText.Text = Path.GetFileName(path);
                FileInfoTextBlock.Text =
                    $"{format}, {image.Data.Length} bytes, 0x{image.BaseAddress:X8}..0x{image.EndAddress:X8}" +
                    (image.HasAddress ? "" : " (assumed)");

                Log($"Opened: {path}");
                Log($"  format: {format}, size: {image.Data.Length} bytes, " +
                    $"address 0x{image.BaseAddress:X8}..0x{image.EndAddress:X8}" +
                    (image.HasAddress ? "" : " (binary file, application address assumed)"));

                foreach (string w in image.Warnings)
                    Log("  warning: " + w);

                RefreshHexView();
            }
            catch (Exception ex)
            {
                Log($"Failed to open file: {ex.Message}");
                MessageBox.Show(ex.Message, "Open file", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static byte[] ReadHead(string path, int count)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[(int)Math.Min(count, fs.Length)];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = fs.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
        }

        // ==================== Save ====================

        private void SaveWith(string filter, string defaultExt, Func<FirmwareImage, string, string> generator)
        {
            if (_image == null) { Log("No data to save."); return; }

            var dialog = new SaveFileDialog
            {
                Filter = filter,
                DefaultExt = defaultExt,
                FileName = _lastFileName + "." + defaultExt
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                string content = generator(_image, dialog.FileName);
                File.WriteAllText(dialog.FileName, content, Encoding.ASCII);
                Log($"Saved: {dialog.FileName}");
            }
            catch (Exception ex)
            {
                Log($"Failed to save file: {ex.Message}");
                MessageBox.Show(ex.Message, "Save file", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveAsBinary_Click(object sender, RoutedEventArgs e)
        {
            if (_image == null) { Log("No data to save."); return; }

            var dialog = new SaveFileDialog
            {
                Filter = "Binary files (*.bin)|*.bin",
                DefaultExt = "bin",
                FileName = _lastFileName + ".bin"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                File.WriteAllBytes(dialog.FileName, _image.Data);
                Log($"Saved (bin): {dialog.FileName}");
            }
            catch (Exception ex)
            {
                Log($"Failed to save file: {ex.Message}");
                MessageBox.Show(ex.Message, "Save file", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveAsHex_Click(object sender, RoutedEventArgs e) =>
            SaveWith("Intel HEX files (*.hex)|*.hex", "hex",
                     (img, _) => HexFile.GenerateIntelHex(img.Data, img.BaseAddress));

        private void SaveAsSrec_Click(object sender, RoutedEventArgs e) =>
            SaveWith("Motorola S-record (*.s19;*.srec)|*.s19;*.srec", "s19",
                     (img, _) => HexFile.GenerateSRecord(img.Data, img.BaseAddress));

        private void SaveAsTiTxt_Click(object sender, RoutedEventArgs e) =>
            SaveWith("TI-TXT files (*.txt)|*.txt", "txt",
                     (img, _) => HexFile.GenerateTiTxt(img.Data, img.BaseAddress));

        private void SaveAsCArray_Click(object sender, RoutedEventArgs e) =>
            SaveWith("C source files (*.c;*.h)|*.c;*.h", "h",
                     (img, path) => HexFile.GenerateCArray(img.Data, img.BaseAddress,
                                                           Path.GetFileNameWithoutExtension(path)));

        // ==================== View ====================

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int mode) &&
                (mode == 1 || mode == 2 || mode == 4))
            {
                _currentMode = mode;
                RefreshHexView();
            }
        }

        private void AddressInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            if (_image == null) return;

            if (!SettingsWindow.TryParseAddress(AddressInput.Text, out uint value))
            {
                MessageBox.Show("Invalid address. Use hex, e.g. 0x100 or 100.",
                                "Address", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Accept both an absolute address and an offset from the start of the image
            uint offset = value >= _image.BaseAddress ? value - _image.BaseAddress : value;
            if (offset >= (uint)_image.Data.Length) offset = (uint)Math.Max(0, _image.Data.Length - 1);

            _viewOffset = (int)offset;
            RefreshHexView();
        }

        // ==================== Settings / About ====================

        private void Options_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow { Owner = this };
            if (settingsWindow.ShowDialog() == true)
                UpdatePortDisplay();
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            var about = new AboutWindow { Owner = this };
            about.ShowDialog();
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        // ==================== Connect ====================

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            // Connected -> disconnect
            if (_bootloader != null)
            {
                _bootloader.Dispose();
                _bootloader = null;
                ClearTargetInfo();
                Log("Disconnected.");
                return;
            }

            // Connection attempt in progress -> cancel it
            if (_connectCts != null)
            {
                _connectCts.Cancel();
                Log("Cancelling connection...");
                return;
            }

            string port = Settings.Default.ComPort;
            int baud = Settings.Default.BaudRate;

            if (string.IsNullOrWhiteSpace(port))
            {
                Log("COM port is not set. Open Options...");
                MessageBox.Show("COM port is not set. Open Options...", "Connect",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (baud <= 0)
            {
                Log("Invalid baud rate. Open Options...");
                MessageBox.Show("Invalid baud rate. Open Options...", "Connect",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var cts = new CancellationTokenSource();
            _connectCts = cts;
            var token = cts.Token;

            MenuConnect.Header = "Cancel connect";
            SetBusy(true);

            try
            {
                Log($"Connecting to {port} @ {baud}... (retrying until cancelled; reset the board to enter the bootloader)");
                int attempt = 0;
                string? lastError = null;

                while (!token.IsCancellationRequested)
                {
                    attempt++;
                    Bootloader? boot = null;
                    try
                    {
                        boot = new Bootloader(port, baud);
                        if (await boot.ConnectAsync(token).ConfigureAwait(true))
                        {
                            var info = await boot.GetInfoAsync(token).ConfigureAwait(true);
                            _bootloader = boot;
                            boot = null;                    // ownership moved to the field
                            UpdateTargetInfo(info);
                            Log($"Connected (attempt {attempt}).");
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Do not repeat the same error in the log on every attempt
                        if (ex.Message != lastError)
                        {
                            lastError = ex.Message;
                            Log($"  attempt {attempt}: {ex.Message}");
                        }
                    }
                    finally
                    {
                        boot?.Dispose();
                    }

                    try { await Task.Delay(500, token).ConfigureAwait(true); }
                    catch (OperationCanceledException) { break; }
                }

                Log("Connection cancelled.");
            }
            finally
            {
                _connectCts = null;
                cts.Dispose();
                MenuConnect.Header = _bootloader != null ? "Disconnect" : "Connect";
                SetBusy(false);
            }
        }

        private void UpdateTargetInfo(BootInfo info)
        {
            TargetStatus.Text = "Connected";
            TargetStatus.Foreground = Brushes.Blue;

            TargetVersion.Text = $"Version:   {info.VersionString}";
            TargetProduct.Text = $"Product:   0x{info.Product:X8}";
            TargetPages.Text = $"Pages:     {info.Pages} (application area)";
            TargetPageSize.Text = $"Page size: {info.PageSize} B, app flash {info.FlashSize / 1024} KB";

            TargetVersion.Visibility = Visibility.Visible;
            TargetProduct.Visibility = Visibility.Visible;
            TargetPages.Visibility = Visibility.Visible;
            TargetPageSize.Visibility = Visibility.Visible;

            Log($"Device: bootloader v{info.VersionString}, product 0x{info.Product:X8}, " +
                $"{info.Pages} pages x {info.PageSize} B = {info.FlashSize} B for the application");
        }

        private void ClearTargetInfo()
        {
            TargetStatus.Text = "Not connected";
            TargetStatus.Foreground = Brushes.Red;
            TargetVersion.Visibility = Visibility.Collapsed;
            TargetProduct.Visibility = Visibility.Collapsed;
            TargetPages.Visibility = Visibility.Collapsed;
            TargetPageSize.Visibility = Visibility.Collapsed;
            MenuConnect.Header = "Connect";
        }

        // ==================== Erase / reset ====================

        private async void Erase_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || !RequireConnection()) return;

            var result = MessageBox.Show(
                "Erase the whole application area of the flash?",
                "Confirm erase",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            SetBusy(true);
            try
            {
                Log("Erasing flash...");
                await _bootloader!.EraseAsync().ConfigureAwait(true);
                Log("Flash erased.");
            }
            catch (Exception ex)
            {
                Log($"Erase failed: {ex.Message}");
                MessageBox.Show(ex.Message, "Erase", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || !RequireConnection()) return;

            SetBusy(true);
            try
            {
                Log("Resetting device...");
                await _bootloader!.ResetAsync().ConfigureAwait(true);
                Log("Reset command sent.");
            }
            catch (Exception ex)
            {
                Log($"Reset failed: {ex.Message}");
            }
            finally
            {
                // The device restarts - release the port
                _bootloader?.Dispose();
                _bootloader = null;
                ClearTargetInfo();
                SetBusy(false);
            }
        }

        // ==================== Program ====================

        /// <summary>
        /// The bootloader writes page N at APPLICATION_ADDRESS + offset, so the image must start
        /// at the application address. Checks images that carry addresses (HEX/S-record/TI-TXT).
        /// Returns the image to program, or null if the user cancelled.
        /// </summary>
        private FirmwareImage? CheckLoadAddress(FirmwareImage image)
        {
            if (!image.HasAddress) return image;

            uint app = SettingsWindow.GetAppAddress();
            if (image.BaseAddress == app) return image;

            if (image.BaseAddress < app && image.EndAddress >= app)
            {
                uint skipped = app - image.BaseAddress;
                var answer = MessageBox.Show(
                    $"The image starts at 0x{image.BaseAddress:X8}, below the application address 0x{app:X8}.\n" +
                    $"The first {skipped} bytes belong to the bootloader area and cannot be written.\n\n" +
                    $"Program only the part starting at 0x{app:X8}?",
                    "Load address", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (answer != MessageBoxResult.Yes) return null;
                Log($"Skipping {skipped} bytes below 0x{app:X8}.");
                return image.SliceFrom(app);
            }

            var answer2 = MessageBox.Show(
                $"The image is linked for 0x{image.BaseAddress:X8}, but the bootloader places the " +
                $"application at 0x{app:X8}.\nThe firmware will most likely not run.\n\nProgram anyway?",
                "Load address", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            return answer2 == MessageBoxResult.Yes ? image : null;
        }

        private async void ProgramButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || !RequireConnection()) return;

            if (_image == null || _image.Data.Length == 0)
            {
                Log("No firmware loaded - open a file first.");
                return;
            }

            var boot = _bootloader!;
            if (!boot.HasDeviceInfo)
            {
                Log("No device info - reconnect.");
                return;
            }

            var image = CheckLoadAddress(_image);
            if (image == null)
            {
                Log("Programming cancelled (load address).");
                return;
            }

            byte[] firmware = image.Data;
            uint flashSize = boot.DeviceInfo.FlashSize;
            if ((uint)firmware.Length > flashSize)
            {
                string msg = $"Firmware size ({firmware.Length} B) exceeds the application area ({flashSize} B).";
                Log(msg);
                MessageBox.Show(msg, "Program", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SetBusy(true);
            using var cts = new CancellationTokenSource();
            var progressWindow = new ProgressWindow { Owner = this };
            progressWindow.CancelRequested += (_, _) => cts.Cancel();

            Task? work = null;

            // Start the task only after the window is shown: otherwise a fast run could close
            // the window before ShowDialog() is called, leaving the dialog open forever.
            progressWindow.Loaded += (_, _) => work = RunProgrammingAsync(boot, firmware, progressWindow, cts.Token);
            progressWindow.ShowDialog();

            try
            {
                if (work != null) await work.ConfigureAwait(true);
                Log($"Programming completed: {firmware.Length} bytes.");

                // The device restarts after CMD_RESET - release the port
                _bootloader?.Dispose();
                _bootloader = null;
                ClearTargetInfo();
            }
            catch (OperationCanceledException)
            {
                Log("Programming cancelled. Flash content is undefined - program again.");
            }
            catch (Exception ex)
            {
                Log($"Programming failed: {ex.Message}");
                MessageBox.Show(ex.Message, "Program", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task RunProgrammingAsync(Bootloader boot, byte[] firmware,
                                               ProgressWindow window, CancellationToken token)
        {
            try
            {
                window.UpdateProgress(0, "Erasing flash...");
                await boot.EraseAsync(token).ConfigureAwait(true);

                var progress = new Progress<int>(value => window.UpdateProgress(value, $"Writing... {value}%"));
                await boot.ProgramAsync(firmware, progress, token).ConfigureAwait(true);

                window.UpdateProgress(100, "Resetting device...");
                await boot.ResetAsync(token).ConfigureAwait(true);
            }
            finally
            {
                window.CloseFromCode();
            }
        }
    }
}
