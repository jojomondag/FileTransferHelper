namespace FileTransferHelper.Services;

public interface IDialogService
{
    Task<IReadOnlyList<string>> PickFilesAsync();
    Task<string?> PickFolderAsync();
    Task ShowInfoAsync(string title, string message);
    Task ShowWarningAsync(string title, string message);
    Task ShowErrorAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message);
    Task<string?> AskTextAsync(string title, string prompt);
    Task<string?> AskPasswordAsync(string title, string prompt);
}
