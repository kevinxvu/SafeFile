namespace SafeFile.ViewModels;

public sealed class PlaceholderViewModel : ViewModelBase
{
    private readonly string _titleKey;
    public string Title => Services.LocalizationService.Instance.Get(_titleKey);

    public PlaceholderViewModel(string titleKey) => _titleKey = titleKey;

    public void RefreshLocalization() => OnPropertyChanged(nameof(Title));
}
