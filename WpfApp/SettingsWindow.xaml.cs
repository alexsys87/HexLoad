using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;

namespace HexLoad
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            LoadAvailablePorts();
            LoadSavedSettings();
        }

        private void LoadAvailablePorts()
        {
            string[] ports = SerialPort.GetPortNames();
            ComPortComboBox.ItemsSource = ports;
        }

        private void LoadSavedSettings()
        {
            string savedPort = Properties.Settings.Default.ComPort;
            int savedBaudRate = Properties.Settings.Default.BaudRate;

            if (!string.IsNullOrEmpty(savedPort) && ComPortComboBox.Items.Contains(savedPort))
                ComPortComboBox.SelectedItem = savedPort;
            else if (ComPortComboBox.Items.Count > 0)
                ComPortComboBox.SelectedIndex = 0;

            foreach (ComboBoxItem item in BaudRateComboBox.Items)
            {
                if (item.Tag != null && item.Tag.ToString() == savedBaudRate.ToString())
                {
                    BaudRateComboBox.SelectedItem = item;
                    break;
                }
            }
            if (BaudRateComboBox.SelectedItem == null && BaudRateComboBox.Items.Count > 0)
                BaudRateComboBox.SelectedIndex = 0;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            string? selectedPort = ComPortComboBox.SelectedItem?.ToString();
            int selectedBaudRate = 9600;

            if (BaudRateComboBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag.ToString(), out int baud))
                selectedBaudRate = baud;

            Properties.Settings.Default.ComPort = selectedPort;
            Properties.Settings.Default.BaudRate = selectedBaudRate;
            Properties.Settings.Default.Save();

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}