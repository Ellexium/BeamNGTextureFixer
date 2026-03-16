using System.ComponentModel;
using System.Windows;
using BeamNGTextureFixer.ViewModels;
using System.Windows.Input;


namespace BeamNGTextureFixer
{
    public partial class MainWindow : Window
    {
        private void SelectedModsTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is MainViewModel vm && vm.BrowseModsCommand.CanExecute(null))
            {
                vm.BrowseModsCommand.Execute(null);
                e.Handled = true;
            }
        }

        private bool _closingAfterAbort;

        public MainWindow()
        {
            InitializeComponent();

            var vm = new MainViewModel();
            vm.RequestCloseRequested += OnRequestCloseRequested;
            DataContext = vm;

            Closing += MainWindow_Closing;
        }

        private void OnRequestCloseRequested()
        {
            _closingAfterAbort = true;
            Close();
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_closingAfterAbort)
                return;

            if (DataContext is MainViewModel vm && vm.IsBusy)
            {
                var result = MessageBox.Show(
                    "A scan or build is currently in progress.\n\nAbort and close?",
                    "Abort and Close",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    vm.AbortAndClose();
                }
                else
                {
                    e.Cancel = true;
                }
            }
        }
    }
}