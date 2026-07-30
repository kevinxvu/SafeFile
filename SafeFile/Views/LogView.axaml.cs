using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using SafeFile.ViewModels;

namespace SafeFile.Views;

public partial class LogView : UserControl
{
    private LogViewModel? _viewModel;

    public LogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.FilteredEntries.CollectionChanged -= OnEntriesChanged;

        _viewModel = DataContext as LogViewModel;
        if (_viewModel is not null)
            _viewModel.FilteredEntries.CollectionChanged += OnEntriesChanged;
    }

    private void OnEntriesChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel?.AutoScroll != true)
            return;

        Dispatcher.UIThread.Post(
            LogScrollViewer.ScrollToEnd,
            DispatcherPriority.Background);
    }
}
