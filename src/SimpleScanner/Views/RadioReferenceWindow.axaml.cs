using Avalonia.Controls;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Views;

/// <summary>The RadioReference browser's window. Everything it does is in <see cref="RadioReferenceViewModel"/>.</summary>
public partial class RadioReferenceWindow : Window
{
    public RadioReferenceWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is RadioReferenceViewModel vm) await vm.OpenAsync();
        };
    }
}
