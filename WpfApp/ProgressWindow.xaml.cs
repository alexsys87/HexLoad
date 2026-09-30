using System;
using System.Windows;

namespace HexLoad
{
    public partial class ProgressWindow : Window
    {
        private bool _closedFromCode;

        /// <summary>Raised when Cancel is pressed or the user tries to close the window.</summary>
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
            if (status != null) StatusText.Text = status;
        }

        /// <summary>Closes the window when the operation has finished (not treated as a cancel).</summary>
        public void CloseFromCode()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(CloseFromCode);
                return;
            }

            _closedFromCode = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CancelButton.IsEnabled = false;
            StatusText.Text = "Cancelling...";
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Closing the window by the user (X button / Alt+F4) means cancel;
            // otherwise the operation would keep running with no progress indication.
            if (!_closedFromCode)
                CancelRequested?.Invoke(this, EventArgs.Empty);

            base.OnClosing(e);
        }
    }
}
