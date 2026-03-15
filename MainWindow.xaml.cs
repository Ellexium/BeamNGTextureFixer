using System.Windows;
using BeamNGTextureFixer.ViewModels;

namespace BeamNGTextureFixer
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }
    }
}