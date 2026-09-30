using System;
using System.Windows;

namespace HexLoad
{
    public partial class ProgressWindow : Window
    {
        public event EventHandler? CancelRequested;

        public ProgressWindow()
        {
            InitializeComponent();
        }

        public void UpdateProgress(int value, string? status = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateProgress(value, status));
                return;
            }
            ProgressBar.Value = value;
            if (status != null)
                StatusText.Text = status;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}