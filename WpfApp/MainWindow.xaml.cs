using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private byte[]? _currentFileData = null;
        private int _currentMode = 1;
        private int _startOffset = 0;

        private Bootloader? _bootloader;
        private CancellationTokenSource? _connectCts;
        private CancellationTokenSource? _programCts;

        public MainWindow()
        {
            InitializeComponent();
            UpdatePortDisplay();
            this.Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _bootloader?.Dispose();
            _connectCts?.Cancel();
            _programCts?.Cancel();
        }

        // ==== Вспомогательные методы ====
        private void Log(string message)
        {
            if (Dispatcher.CheckAccess())
            {
                LogTextBox.AppendText(message + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            }
            else
            {
                Dispatcher.InvokeAsync(() => Log(message));
            }
        }

        private void UpdatePortDisplay()
        {
            string port = Settings.Default.ComPort;
            int baud = Settings.Default.BaudRate;
            ComPortTextBlock.Text = $"COM Port: {port}";
            BaudRateTextBlock.Text = $"Baudrate: {baud}";
        }

        // ==== Класс для отображения HEX ====
        public class HexRow
        {
            public string Address { get; set; } = string.Empty;
            public string B0 { get; set; } = string.Empty; public string B1 { get; set; } = string.Empty;
            public string B2 { get; set; } = string.Empty; public string B3 { get; set; } = string.Empty;
            public string B4 { get; set; } = string.Empty; public string B5 { get; set; } = string.Empty;
            public string B6 { get; set; } = string.Empty; public string B7 { get; set; } = string.Empty;
            public string B8 { get; set; } = string.Empty; public string B9 { get; set; } = string.Empty;
            public string BA { get; set; } = string.Empty; public string BB { get; set; } = string.Empty;
            public string BC { get; set; } = string.Empty; public string BD { get; set; } = string.Empty;
            public string BE { get; set; } = string.Empty; public string BF { get; set; } = string.Empty;
            public string Ascii { get; set; } = string.Empty;
        }

        private void RefreshHexView()
        {
            if (_currentFileData == null) return;

            var rows = new List<HexRow>();
            int activeCols = 16 / _currentMode;

            for (int c = 1; c <= 16; c++)
            {
                var column = HexGrid.Columns[c];
                if (c <= activeCols)
                {
                    column.Visibility = Visibility.Visible;
                    column.Header = ((c - 1) * _currentMode).ToString("X");
                    column.Width = _currentMode == 4 ? 90 : (_currentMode == 2 ? 60 : 35);
                }
                else column.Visibility = Visibility.Collapsed;
            }

            for (int i = _startOffset; i < _currentFileData.Length; i += 16)
            {
                var row = new HexRow { Address = i.ToString("X4") };

                for (int j = 0; j < activeCols; j++)
                {
                    int offset = i + (j * _currentMode);
                    string hexVal = "";

                    if (offset < _currentFileData.Length)
                    {
                        if (_currentMode == 4 && offset + 3 < _currentFileData.Length)
                            hexVal = BitConverter.ToUInt32(_currentFileData, offset).ToString("X8");
                        else if (_currentMode == 2 && offset + 1 < _currentFileData.Length)
                            hexVal = BitConverter.ToUInt16(_currentFileData, offset).ToString("X4");
                        else
                            hexVal = _currentFileData[offset].ToString("X2");
                    }
                    else hexVal = "--";

                    string propName = j < 10 ? $"B{j}" : $"B{(char)('A' + j - 10)}";
                    typeof(HexRow).GetProperty(propName)?.SetValue(row, hexVal);
                }

                StringBuilder sb = new StringBuilder();
                for (int k = 0; k < 16; k++)
                {
                    int offset = i + k;
                    if (offset < _currentFileData.Length)
                    {
                        char c = (char)_currentFileData[offset];
                        sb.Append((c < 32 || c > 126) ? "." : c.ToString());
                    }
                    else sb.Append(" ");
                }
                row.Ascii = sb.ToString();
                rows.Add(row);
            }
            HexGrid.ItemsSource = rows;
        }

        private byte[] ParseIntelHex(string[] lines)
        {
            byte[] tempBuffer = new byte[1024 * 1024];
            int maxAddress = 0;

            foreach (string line in lines)
            {
                if (!line.StartsWith(":") || line.Length < 11) continue;

                int byteCount = Convert.ToInt32(line.Substring(1, 2), 16);
                int address = Convert.ToInt32(line.Substring(3, 4), 16);
                int recordType = Convert.ToInt32(line.Substring(7, 2), 16);

                if (recordType == 00)
                {
                    for (int i = 0; i < byteCount; i++)
                    {
                        byte b = (byte)Convert.ToInt32(line.Substring(9 + (i * 2), 2), 16);
                        tempBuffer[address + i] = b;
                        if (address + i > maxAddress) maxAddress = address + i;
                    }
                }
                else if (recordType == 01) break;
            }

            byte[] result = new byte[maxAddress + 1];
            Array.Copy(tempBuffer, result, maxAddress + 1);
            return result;
        }

        private byte[] ParseMotorolaSRecord(string[] lines)
        {
            // Определим минимальный и максимальный адреса
            uint minAddr = uint.MaxValue;
            uint maxAddr = 0;
            // Сначала проходим по строкам, чтобы найти границы адресов
            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line) || line[0] != 'S') continue;
                if (line.Length < 4) continue;

                char recordType = line[1];
                // Интересуют только типы с данными: S1, S2, S3
                if (recordType != '1' && recordType != '2' && recordType != '3') continue;

                // Длина записи (байт данных + адрес + контрольная сумма) в байтах
                int byteCount = Convert.ToInt32(line.Substring(2, 2), 16);
                if (line.Length < 4 + byteCount * 2) continue; // недостаточно символов

                int addrBytes = 0;
                if (recordType == '1') addrBytes = 2; // 16-битный адрес
                else if (recordType == '2') addrBytes = 3; // 24-битный
                else if (recordType == '3') addrBytes = 4; // 32-битный

                // Извлекаем адрес
                uint address = 0;
                for (int i = 0; i < addrBytes; i++)
                {
                    byte b = (byte)Convert.ToInt32(line.Substring(4 + i * 2, 2), 16);
                    address = (address << 8) | b;
                }

                // Данные (байты после адреса, до последнего байта перед контрольной суммой)
                int dataBytes = byteCount - addrBytes - 1; // минус контрольная сумма
                if (dataBytes < 0) continue;

                // Обновляем границы
                if (address < minAddr) minAddr = address;
                uint endAddr = address + (uint)dataBytes - 1;
                if (endAddr > maxAddr) maxAddr = endAddr;
            }

            if (minAddr == uint.MaxValue)
                throw new Exception("No valid S-record data found");

            // Создаём буфер, заполняем 0xFF (стёртое состояние)
            uint bufferSize = maxAddr - minAddr + 1;
            byte[] buffer = new byte[bufferSize];
            for (int i = 0; i < bufferSize; i++) buffer[i] = 0xFF;

            // Второй проход: записываем данные
            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line) || line[0] != 'S') continue;
                if (line.Length < 4) continue;

                char recordType = line[1];
                if (recordType != '1' && recordType != '2' && recordType != '3') continue;

                int byteCount = Convert.ToInt32(line.Substring(2, 2), 16);
                if (line.Length < 4 + byteCount * 2) continue;

                int addrBytes = (recordType == '1') ? 2 : (recordType == '2') ? 3 : 4;

                uint address = 0;
                for (int i = 0; i < addrBytes; i++)
                {
                    byte b = (byte)Convert.ToInt32(line.Substring(4 + i * 2, 2), 16);
                    address = (address << 8) | b;
                }

                int dataBytes = byteCount - addrBytes - 1;
                if (dataBytes <= 0) continue;

                // Вычисляем контрольную сумму (опционально)
                byte checksum = 0;
                for (int i = 0; i < byteCount - 1; i++) // суммируем все байты кроме контрольной суммы
                {
                    string byteStr = line.Substring(2 + i * 2, 2);
                    checksum += (byte)Convert.ToInt32(byteStr, 16);
                }
                byte expectedChecksum = (byte)Convert.ToInt32(line.Substring(2 + (byteCount - 1) * 2, 2), 16);
                if ((checksum + expectedChecksum) != 0xFF)
                {
                    // Можно проигнорировать или выбросить исключение
                    // Для надёжности проигнорируем
                }

                // Копируем данные в буфер со смещением
                uint offset = address - minAddr;
                for (int i = 0; i < dataBytes; i++)
                {
                    byte b = (byte)Convert.ToInt32(line.Substring(4 + addrBytes * 2 + i * 2, 2), 16);
                    buffer[offset + i] = b;
                }
            }

            return buffer;
        }

        private byte[] ParseTiTxt(string[] lines)
        {
            // Сначала соберём все пары (адрес, байт) в список
            var dataList = new List<(uint addr, byte value)>();
            uint currentAddr = 0;
            bool inData = false;
            uint minAddr = uint.MaxValue;
            uint maxAddr = 0;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // Проверка на конец файла 'q'
                if (line == "q" || line == "Q")
                    break;

                if (line[0] == '@')
                {
                    // Строка с адресом
                    string addrStr = line.Substring(1).Trim();
                    if (!uint.TryParse(addrStr, System.Globalization.NumberStyles.HexNumber, null, out currentAddr))
                        throw new Exception($"Invalid address: {addrStr}");
                    inData = true;
                    continue;
                }

                if (!inData) continue; // пропускаем строки до первого адреса

                // Разбиваем строку на части по пробелам
                string[] parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string part in parts)
                {
                    if (part.Length == 0) continue;
                    // Иногда встречается 'q' в середине строки? По стандарту нет, но на всякий случай
                    if (part == "q" || part == "Q")
                        break;

                    byte b = Convert.ToByte(part, 16);
                    dataList.Add((currentAddr, b));
                    if (currentAddr < minAddr) minAddr = currentAddr;
                    if (currentAddr > maxAddr) maxAddr = currentAddr;
                    currentAddr++;
                }
            }

            if (dataList.Count == 0)
                throw new Exception("No valid TI-TXT data found");

            // Создаём массив от minAddr до maxAddr
            uint size = maxAddr - minAddr + 1;
            byte[] buffer = new byte[size];
            // Заполняем 0xFF (стёртое состояние) – опционально, если нужно
            for (int i = 0; i < size; i++) buffer[i] = 0xFF;

            foreach (var (addr, value) in dataList)
            {
                uint offset = addr - minAddr;
                buffer[offset] = value;
            }

            return buffer;
        }

        // ==== Обработчики меню ====
        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog();
            dialog.Filter = "All Supported|*.bin;*.hex;*.s19;*.mot;*.srec;*.txt;*.ti-txt|Binary files (*.bin)|*.bin|Intel HEX (*.hex)|*.hex|Motorola S-record (*.s19;*.mot;*.srec)|*.s19;*.mot;*.srec|TI-TXT (*.txt;*.ti-txt)|*.txt;*.ti-txt";

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string ext = Path.GetExtension(dialog.FileName).ToLower();
                    // Читаем файл один раз в любом случае
                    string[] lines = File.ReadAllLines(dialog.FileName);
                    bool parsed = false;

                    if (ext == ".hex")
                    {
                        _currentFileData = ParseIntelHex(lines);
                        parsed = true;
                    }
                    else if (ext == ".s19" || ext == ".mot" || ext == ".srec")
                    {
                        _currentFileData = ParseMotorolaSRecord(lines);
                        parsed = true;
                    }
                    else if (ext == ".txt" || ext == ".ti-txt")
                    {
                        // Проверяем, похоже ли на TI-TXT (первая строка начинается с '@')
                        if (lines.Length > 0 && lines[0].TrimStart().StartsWith("@"))
                        {
                            _currentFileData = ParseTiTxt(lines);
                            parsed = true;
                        }
                    }

                    if (!parsed)
                    {
                        // Если ни один парсер не сработал, читаем как бинарный
                        // Но мы уже прочитали файл как текст, поэтому нужно перечитать как бинарный
                        _currentFileData = File.ReadAllBytes(dialog.FileName);
                    }

                    _startOffset = 0;
                    FileNameText.Text = Path.GetFileName(dialog.FileName);
                    Log($"File: {dialog.FileName}\nType: {(parsed ? "Parsed" : "Binary")}\nSize: {_currentFileData!.Length} bytes");
                    RefreshHexView();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error: " + ex.Message);
                }
            }
        }

        private void SaveAsBinary_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFileData == null) { Log("No data to save."); return; }
            var dialog = new SaveFileDialog { Filter = "Binary files (*.bin)|*.bin", DefaultExt = "bin" };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllBytes(dialog.FileName, _currentFileData);
                    Log($"Saved as binary: {dialog.FileName}");
                }
                catch (Exception ex) { Log($"Save error: {ex.Message}"); }
            }
        }

        private void SaveAsHex_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFileData == null) { Log("No data to save."); return; }
            var dialog = new SaveFileDialog { Filter = "Intel HEX files (*.hex)|*.hex", DefaultExt = "hex" };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string hex = GenerateIntelHex(_currentFileData);
                    File.WriteAllText(dialog.FileName, hex, Encoding.ASCII);
                    Log($"Saved as Intel HEX: {dialog.FileName}");
                }
                catch (Exception ex) { Log($"Save error: {ex.Message}"); }
            }
        }

        private void SaveAsSrec_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFileData == null) { Log("No data to save."); return; }
            var dialog = new SaveFileDialog { Filter = "Motorola S-record (*.s19;*.srec)|*.s19;*.srec", DefaultExt = "s19" };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string srec = GenerateMotorolaSRecord(_currentFileData);
                    File.WriteAllText(dialog.FileName, srec, Encoding.ASCII);
                    Log($"Saved as S-record: {dialog.FileName}");
                }
                catch (Exception ex) { Log($"Save error: {ex.Message}"); }
            }
        }

        private void SaveAsTiTxt_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFileData == null) { Log("No data to save."); return; }
            var dialog = new SaveFileDialog { Filter = "TI-TXT files (*.txt)|*.txt", DefaultExt = "txt" };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string titxt = GenerateTiTxt(_currentFileData);
                    File.WriteAllText(dialog.FileName, titxt, Encoding.ASCII);
                    Log($"Saved as TI-TXT: {dialog.FileName}");
                }
                catch (Exception ex) { Log($"Save error: {ex.Message}"); }
            }
        }

        // ==== Генерация Intel HEX ====
        private string GenerateIntelHex(byte[] data)
        {
            const int bytesPerLine = 16;
            var sb = new StringBuilder();
            int address = 0;
            while (address < data.Length)
            {
                int chunk = Math.Min(bytesPerLine, data.Length - address);
                int checksum = chunk; // длина
                checksum += (address >> 8) & 0xFF; // старший байт адреса
                checksum += address & 0xFF;         // младший байт адреса
                checksum += 0; // тип записи 00

                sb.Append(':');
                sb.Append(chunk.ToString("X2"));
                sb.Append((address >> 8).ToString("X2"));
                sb.Append((address & 0xFF).ToString("X2"));
                sb.Append("00"); // тип

                for (int i = 0; i < chunk; i++)
                {
                    byte b = data[address + i];
                    sb.Append(b.ToString("X2"));
                    checksum += b;
                }

                checksum = (-checksum) & 0xFF;
                sb.AppendLine(checksum.ToString("X2"));

                address += chunk;
            }
            // Конец файла
            sb.AppendLine(":00000001FF");
            return sb.ToString();
        }

        // ==== Генерация Motorola S-record (S3, 32-битный адрес) ====
        private string GenerateMotorolaSRecord(byte[] data)
        {
            const int bytesPerLine = 16; // макс 16 байт данных в S3 (тип 3)
            var sb = new StringBuilder();
            int address = 0;
            while (address < data.Length)
            {
                int dataBytes = Math.Min(bytesPerLine, data.Length - address);
                int totalBytes = dataBytes + 5; // адрес 4 байта + контрольная сумма 1 байт
                if (totalBytes > 0xFF) totalBytes = 0xFF; // ограничение длины, но у нас не превысит

                sb.Append('S');
                sb.Append('3'); // тип S3
                sb.Append(totalBytes.ToString("X2"));

                // адрес 32 бита
                sb.Append(((address >> 24) & 0xFF).ToString("X2"));
                sb.Append(((address >> 16) & 0xFF).ToString("X2"));
                sb.Append(((address >> 8) & 0xFF).ToString("X2"));
                sb.Append((address & 0xFF).ToString("X2"));

                byte checksum = (byte)(totalBytes +
                                       ((address >> 24) & 0xFF) +
                                       ((address >> 16) & 0xFF) +
                                       ((address >> 8) & 0xFF) +
                                       (address & 0xFF));

                for (int i = 0; i < dataBytes; i++)
                {
                    byte b = data[address + i];
                    sb.Append(b.ToString("X2"));
                    checksum += b;
                }

                checksum = (byte)(~checksum); // контрольная сумма = 0xFF - сумма всех байтов (кроме префикса S и типа)
                sb.AppendLine(checksum.ToString("X2"));

                address += dataBytes;
            }
            // Завершающая запись S9 (адрес 0)
            sb.AppendLine("S9030000FC");
            return sb.ToString();
        }

        // ==== Генерация TI-TXT ====
        private string GenerateTiTxt(byte[] data)
        {
            const int bytesPerLine = 16;
            var sb = new StringBuilder();
            sb.AppendLine("@0"); // начинаем с адреса 0
            int address = 0;
            while (address < data.Length)
            {
                int chunk = Math.Min(bytesPerLine, data.Length - address);
                for (int i = 0; i < chunk; i++)
                {
                    sb.Append(data[address + i].ToString("X2"));
                    if (i < chunk - 1) sb.Append(' ');
                }
                sb.AppendLine();
                address += chunk;
            }
            sb.AppendLine("q");
            return sb.ToString();
        }

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int mode))
            {
                _currentMode = mode;
                RefreshHexView();
            }
        }

        private void AddressInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                if (_currentFileData == null) return;

                try
                {
                    string input = AddressInput.Text.Trim();
                    if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        _startOffset = Convert.ToInt32(input.Substring(2), 16);
                    else
                        _startOffset = Convert.ToInt32(input, 16);

                    if (_startOffset >= _currentFileData.Length) _startOffset = _currentFileData.Length - 1;
                    if (_startOffset < 0) _startOffset = 0;

                    RefreshHexView();
                    StatusText.Text = $"Started view from offset: 0x{_startOffset:X4}";
                }
                catch
                {
                    MessageBox.Show("Invalid address! Use HEX format (e.g. 0x100 or 100)");
                }
            }
        }

        private void Options_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow();
            settingsWindow.Owner = this;
            if (settingsWindow.ShowDialog() == true)
            {
                UpdatePortDisplay();
            }
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            var about = new AboutWindow();
            about.Owner = this;
            about.ShowDialog();
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        // ==== Подключение с повторными попытками и корректным управлением ресурсами ====
        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            // Если уже подключены — отключаемся
            if (_bootloader != null)
            {
                _bootloader.Dispose();
                _bootloader = null;
                TargetStatus.Text = "Not connected";
                TargetStatus.Foreground = Brushes.Red;
                Log("Disconnected.");
                return;
            }

            // Если идёт процесс подключения — отменяем
            if (_connectCts != null)
            {
                _connectCts.Cancel();
                _connectCts = null;
                Log("Connection attempt cancelled.");
                return;
            }

            // Начинаем новое подключение
            string port = Settings.Default.ComPort;
            int baud = Settings.Default.BaudRate;

            if (string.IsNullOrEmpty(port))
            {
                Log("COM port not configured. Go to Options.");
                return;
            }

            _connectCts = new CancellationTokenSource();
            var token = _connectCts.Token;

            try
            {
                int attempt = 1;
                while (!token.IsCancellationRequested)
                {
                    Log($"Connection attempt {attempt} to {port} at {baud}...");
                    var boot = new Bootloader(port, baud);
                    try
                    {
                        bool connected = await boot.ConnectAsync();
                        if (connected)
                        {
                            var info = await boot.GetInfoAsync(token);
                            _bootloader = boot; // сохраняем успешное подключение
                            UpdateTargetInfo(info);
                            Log("Connected successfully.");
                            return;
                        }
                        else
                        {
                            boot.Dispose(); // неудачная попытка – освобождаем
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        boot.Dispose();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        boot.Dispose();
                        Log($"Attempt {attempt} failed: {ex.Message}");
                    }

                    attempt++;
                    try
                    {
                        await Task.Delay(1000, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log("Connection cancelled.");
            }
            finally
            {
                _connectCts?.Dispose();
                _connectCts = null;
            }
        }

        private void UpdateTargetInfo(BootInfo info)
        {
            uint v = info.Version;
            string versionStr = $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";
            TargetStatus.Text = $"Connected: v{versionStr}, Product 0x{info.Product:X8}, Pages {info.Pages + 2}, PageSize {info.PageSize}";
            TargetStatus.Foreground = Brushes.Blue;
        }

        private async void Erase_Click(object sender, RoutedEventArgs e)
        {
            if (_bootloader == null)
            {
                Log("Not connected.");
                return;
            }

            var result = MessageBox.Show(
                "Are you sure you want to erase the entire application flash?",
                "Confirm Erase",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                Log("Erasing flash...");
                await _bootloader.EraseAsync();
                Log("Flash erased successfully.");
            }
            catch (Exception ex)
            {
                Log($"Erase failed: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Erase Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (_bootloader == null)
            {
                Log("Not connected.");
                return;
            }

            try
            {
                Log("Resetting target...");
                await _bootloader.ResetAsync();
                Log("Reset command sent.");

                // После сброса соединение обычно теряется, освобождаем ресурсы
                _bootloader.Dispose();
                _bootloader = null;
                TargetStatus.Text = "Not connected";
                TargetStatus.Foreground = Brushes.Red;

                // Скрываем детальную информацию (если она была)
                TargetVersion.Visibility = Visibility.Collapsed;
                TargetProduct.Visibility = Visibility.Collapsed;
                TargetPages.Visibility = Visibility.Collapsed;
                TargetPageSize.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Log($"Reset failed: {ex.Message}");
            }
        }

        // ==== Прошивка ====
        private async void ProgramButton_Click(object sender, RoutedEventArgs e)
        {
            if (_bootloader == null)
            {
                Log("Not connected.");
                return;
            }

            if (_currentFileData == null)
            {
                Log("No firmware loaded. Open a file first.");
                return;
            }

            // Проверка соединения (обновляем таймаут загрузчика)
            try
            {
                Log("Checking connection...");
                bool connected = await _bootloader.ConnectAsync();
                if (!connected)
                {
                    Log("Connection lost. Please reconnect.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log($"Connection check failed: {ex.Message}. Please reconnect.");
                return;
            }

            byte[] firmware = _currentFileData.Skip(_startOffset).ToArray();
            uint flashSize = _bootloader.DeviceInfo.Pages * _bootloader.DeviceInfo.PageSize;
            if (firmware.Length > flashSize)
            {
                Log($"Firmware size ({firmware.Length} bytes) exceeds flash ({flashSize} bytes).");
                return;
            }

            _programCts = new CancellationTokenSource();
            var progressWindow = new ProgressWindow();
            progressWindow.Owner = this;
            progressWindow.CancelRequested += (s, args) => _programCts?.Cancel();

            var programTask = ProgramFirmwareAsync(progressWindow, firmware, _programCts.Token);
            progressWindow.ShowDialog();

            try
            {
                await programTask;
                Log("Programming completed successfully.");

                // После сброса соединение обычно теряется, освобождаем ресурсы
                _bootloader.Dispose();
                _bootloader = null;
                TargetStatus.Text = "Not connected";
                TargetStatus.Foreground = Brushes.Red;

                // Скрываем детальную информацию (если она была)
                TargetVersion.Visibility = Visibility.Collapsed;
                TargetProduct.Visibility = Visibility.Collapsed;
                TargetPages.Visibility = Visibility.Collapsed;
                TargetPageSize.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException)
            {
                Log("Programming cancelled.");
            }
            catch (Exception ex)
            {
                Log($"Programming failed: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Programming Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _programCts?.Dispose();
                _programCts = null;
            }
        }

        private async Task ProgramFirmwareAsync(ProgressWindow progressWindow, byte[] firmware, CancellationToken token)
        {
            try
            {
                await _bootloader!.EraseAsync(token);
                var progress = new Progress<int>(value =>
                {
                    progressWindow.UpdateProgress(value, $"Programming... {value}%");
                });
                await _bootloader.ProgramAsync(firmware, progress, token);
                await _bootloader.ResetAsync(token);
                progressWindow.Dispatcher.Invoke(() => progressWindow.Close());
            }
            catch
            {
                progressWindow.Dispatcher.Invoke(() => progressWindow.Close());
                throw;
            }
        }

        private void SaveAsCArray_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFileData == null) { Log("No data to save."); return; }
            var dialog = new SaveFileDialog { Filter = "C source files (*.c;*.h)|*.c;*.h", DefaultExt = "h" };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string arrayName = Path.GetFileNameWithoutExtension(dialog.FileName) ?? "firmware";
                    string content = GenerateCArray(_currentFileData, arrayName);
                    File.WriteAllText(dialog.FileName, content, Encoding.ASCII);
                    Log($"Saved as C array: {dialog.FileName}");
                }
                catch (Exception ex) { Log($"Save error: {ex.Message}"); }
            }
        }

        private string GenerateCArray(byte[] data, string arrayName)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"const unsigned char {arrayName}[{data.Length}] = {{");
            const int bytesPerLine = 16;
            for (int i = 0; i < data.Length; i += bytesPerLine)
            {
                sb.Append("  ");
                for (int j = 0; j < bytesPerLine && i + j < data.Length; j++)
                {
                    sb.Append($"0x{data[i + j]:X2}");
                    if (i + j < data.Length - 1)
                        sb.Append(", ");
                }
                sb.AppendLine();
            }
            sb.AppendLine("};");
            return sb.ToString();
        }
    }
}