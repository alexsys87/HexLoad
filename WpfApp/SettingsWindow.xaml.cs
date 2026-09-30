using System;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace HexLoad
{
    public partial class SettingsWindow : Window
    {
        public const int DefaultBaudRate = 115200;
        public const uint DefaultAppAddress = 0x08000800;

        public SettingsWindow()
        {
            InitializeComponent();
            LoadAvailablePorts();
            LoadSavedSettings();
        }

        /// <summary>Parses "0x08000800" / "08000800" style hex addresses.</summary>
        public static bool TryParseAddress(string? text, out uint address)
        {
            address = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string s = text.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
        }

        /// <summary>Application start address from the settings, or the default if it is not valid.</summary>
        public static uint GetAppAddress()
        {
            return TryParseAddress(Properties.Settings.Default.AppAddress, out uint a) ? a : DefaultAppAddress;
        }

        private void LoadAvailablePorts()
        {
            string[] ports;
            try
            {
                ports = SerialPort.GetPortNames();
            }
            catch (Exception)
            {
                ports = Array.Empty<string>();
            }

            // Numeric-aware order, otherwise COM10 comes before COM2
            ComPortComboBox.ItemsSource = ports
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p.Length)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            HintText.Text = ports.Length == 0
                ? "No COM ports found. Connect the USB-UART adapter and press Refresh."
                : "The bootloader firmware runs at 115200 baud.";
        }

        private void LoadSavedSettings()
        {
            string savedPort = Properties.Settings.Default.ComPort;
            int savedBaudRate = Properties.Settings.Default.BaudRate;
            if (savedBaudRate <= 0) savedBaudRate = DefaultBaudRate;

            if (!string.IsNullOrEmpty(savedPort) && ComPortComboBox.Items.Contains(savedPort))
                ComPortComboBox.SelectedItem = savedPort;
            else if (ComPortComboBox.Items.Count > 0)
                ComPortComboBox.SelectedIndex = 0;

            SelectBaudRate(savedBaudRate);

            AppAddressTextBox.Text = $"0x{GetAppAddress():X8}";
        }

        private void SelectBaudRate(int baudRate)
        {
            foreach (ComboBoxItem item in BaudRateComboBox.Items)
            {
                if (TryGetTagValue(item, out int value) && value == baudRate)
                {
                    BaudRateComboBox.SelectedItem = item;
                    return;
                }
            }

            // Saved value not in the list - fall back to the default
            foreach (ComboBoxItem item in BaudRateComboBox.Items)
            {
                if (TryGetTagValue(item, out int value) && value == DefaultBaudRate)
                {
                    BaudRateComboBox.SelectedItem = item;
                    return;
                }
            }

            if (BaudRateComboBox.Items.Count > 0) BaudRateComboBox.SelectedIndex = 0;
        }

        private static bool TryGetTagValue(ComboBoxItem item, out int value)
        {
            value = 0;
            string? tag = item.Tag?.ToString();
            return tag != null && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private void RefreshPorts_Click(object sender, RoutedEventArgs e)
        {
            string? current = ComPortComboBox.SelectedItem?.ToString();
            LoadAvailablePorts();
            if (current != null && ComPortComboBox.Items.Contains(current))
                ComPortComboBox.SelectedItem = current;
            else if (ComPortComboBox.Items.Count > 0)
                ComPortComboBox.SelectedIndex = 0;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            string? selectedPort = ComPortComboBox.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(selectedPort))
            {
                MessageBox.Show("Select a COM port.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryParseAddress(AppAddressTextBox.Text, out uint appAddress) || (appAddress & 3) != 0)
            {
                MessageBox.Show("Enter a word-aligned hex application address, e.g. 0x08000800.", "Settings",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int selectedBaudRate = DefaultBaudRate;
            if (BaudRateComboBox.SelectedItem is ComboBoxItem item && TryGetTagValue(item, out int baud))
                selectedBaudRate = baud;

            Properties.Settings.Default.ComPort = selectedPort;
            Properties.Settings.Default.BaudRate = selectedBaudRate;
            Properties.Settings.Default.AppAddress = $"0x{appAddress:X8}";
            Properties.Settings.Default.Save();

            DialogResult = true;   // closes the modal window
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;  // closes the modal window
        }
    }
}
