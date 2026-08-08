using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using SafeFile.Core.Crypto;
using SafeFile.Core.IO;
using SafeFile.Core.Services;
using SafeFile.Services;
using SafeFile.ViewModels;

namespace SafeFile.Core.Tests;

public sealed class DecryptViewModelTests
{
    private static readonly Argon2Parameters FastKdf = new(16_384, 1, 1);

    [Fact]
    public async Task ExcludedFolder_FiltersQueue_AndClearRestoresVaults()
    {
        using var temp = new TempDirectory();
        var sourceFiles = Directory.CreateDirectory(Path.Combine(temp.Path, "plain")).FullName;
        var vaultRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "vaults")).FullName;
        var excludedVaultRoot = Directory.CreateDirectory(Path.Combine(vaultRoot, "excluded")).FullName;
        var firstSource = Path.Combine(sourceFiles, "first.txt");
        var secondSource = Path.Combine(sourceFiles, "second.txt");
        await File.WriteAllTextAsync(firstSource, "first");
        await File.WriteAllTextAsync(secondSource, "second");
        var password = "decrypt-queue-password"u8.ToArray();
        var encryptor = new FileEncryptor(consumerThreads: 2);
        await encryptor.EncryptFileAsync(
            firstSource, Path.Combine(vaultRoot, "first.safe"),
            password, 1_048_576, FastKdf);
        await encryptor.EncryptFileAsync(
            secondSource, Path.Combine(excludedVaultRoot, "second.safe"),
            password, 1_048_576, FastKdf);

        var picker = new StubFilePicker { Folders = [excludedVaultRoot] };
        var errors = new RecordingErrorDialog();
        var viewModel = new DecryptViewModel(
            picker,
            errors,
            new SettingsService(),
            NullLogger<FileEncryptor>.Instance);

        await viewModel.AddDroppedSourcesAsync([vaultRoot]);
        Assert.Equal(2, viewModel.Items.Count);

        await viewModel.BrowseExcludedFoldersCommand.ExecuteAsync(null);
        Assert.Single(viewModel.Items);
        Assert.DoesNotContain(
            viewModel.Items,
            item => item.SourcePath.StartsWith(excludedVaultRoot, StringComparison.Ordinal));
        Assert.Empty(errors.Messages);

        viewModel.ClearExcludedFoldersCommand.Execute(null);
        Assert.Equal(2, viewModel.Items.Count);
    }

    [Fact]
    public async Task FolderNames_AfterEncrypt_ReverifiesManifestAndEnablesActions()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "folder-names")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "album"));
        var errors = new RecordingErrorDialog();
        var service = new FolderNameProtectionService(
            new TextCryptoService(),
            NullLogger<FolderNameProtectionService>.Instance);
        var viewModel = new FolderNamesViewModel(
            new StubFilePicker(),
            errors,
            new SettingsService(),
            service);
        const string password = "folder-names-action-password";

        await viewModel.SelectFolderAsync(root);
        viewModel.Password = password;
        viewModel.ConfirmPassword = password;
        await viewModel.EncryptCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasManifest);
        Assert.True(viewModel.IsManifestVerified);
        Assert.True(viewModel.CanEncrypt);
        Assert.True(viewModel.CanDecrypt);
        Assert.Empty(errors.Messages);
    }

    private sealed class StubFilePicker : IFilePickerService
    {
        public IReadOnlyList<string> Folders { get; init; } = [];
        public Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerFileType>? filters = null) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> PickFilesAsync(string title, IReadOnlyList<FilePickerFileType>? filters = null) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> PickFoldersAsync(string title) => Task.FromResult(Folders);
        public Task<string?> PickSaveFileAsync(string title, string suggestedFileName, IReadOnlyList<FilePickerFileType>? filters = null) => Task.FromResult<string?>(null);
        public void OpenFolder(string path) { }
    }

    private sealed class RecordingErrorDialog : IErrorDialogService
    {
        public List<string> Messages { get; } = [];
        public Task ShowErrorAsync(string message, string? title = null)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
