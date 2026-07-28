namespace SafeFile.ViewModels;

public sealed class PlaceholderViewModel : ViewModelBase
{
    public string Title { get; }

    public PlaceholderViewModel(string title) => Title = title;
}
