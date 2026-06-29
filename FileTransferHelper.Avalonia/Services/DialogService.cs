using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace FileTransferHelper.Services;

public sealed class DialogService(Window owner) : IDialogService
{
    private const double DialogContentWidth = 400;

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var result = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Välj filer",
            AllowMultiple = true
        });

        return result
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
    }

    public async Task<string?> PickFolderAsync()
    {
        var result = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Välj mapp",
            AllowMultiple = false
        });

        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    public Task ShowInfoAsync(string title, string message) =>
        ShowMessageAsync(title, message, "OK");

    public Task ShowWarningAsync(string title, string message) =>
        ShowMessageAsync(title, message, "OK");

    public Task ShowErrorAsync(string title, string message) =>
        ShowMessageAsync(title, message, "OK");

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var window = CreateDialogShell(title, out var body, out var footer);
        body.Children.Add(CreateMessageBlock(message));

        var result = false;
        footer.Children.Add(CreateButton("Nej", () =>
        {
            result = false;
            window.Close();
        }));
        footer.Children.Add(CreatePrimaryButton("Ja", () =>
        {
            result = true;
            window.Close();
        }));

        await window.ShowDialog(owner);
        return result;
    }

    public async Task<string?> AskTextAsync(string title, string prompt)
    {
        var window = CreateDialogShell(title, out var body, out var footer);
        body.Children.Add(CreatePromptBlock(prompt));

        var textBox = CreateInputTextBox();
        body.Children.Add(textBox);

        string? result = null;
        void Submit()
        {
            var value = textBox.Text?.Trim() ?? "";
            result = string.IsNullOrWhiteSpace(value) ? null : value;
            window.Close();
        }

        footer.Children.Add(CreateButton("Avbryt", () =>
        {
            result = null;
            window.Close();
        }));
        footer.Children.Add(CreatePrimaryButton("OK", Submit));

        textBox.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                Submit();
            }
            else if (args.Key == Key.Escape)
            {
                result = null;
                window.Close();
            }
        };

        window.Opened += (_, _) => textBox.Focus();
        await window.ShowDialog(owner);
        return result;
    }

    public async Task<string?> AskPasswordAsync(string title, string prompt)
    {
        var window = CreateDialogShell(title, out var body, out var footer);
        body.Children.Add(CreatePromptBlock(prompt));

        var textBox = CreateInputTextBox(password: true);
        body.Children.Add(textBox);

        string? result = null;
        void Submit()
        {
            result = textBox.Text;
            window.Close();
        }

        footer.Children.Add(CreateButton("Avbryt", () =>
        {
            result = null;
            window.Close();
        }));
        footer.Children.Add(CreatePrimaryButton("Anslut", Submit));

        textBox.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                Submit();
            }
            else if (args.Key == Key.Escape)
            {
                result = null;
                window.Close();
            }
        };

        window.Opened += (_, _) => textBox.Focus();
        await window.ShowDialog(owner);
        return result;
    }

    private async Task ShowMessageAsync(string title, string message, string buttonText)
    {
        var window = CreateDialogShell(title, out var body, out var footer);
        body.Children.Add(CreateMessageBlock(message));
        footer.Children.Add(CreatePrimaryButton(buttonText, () => window.Close()));
        await window.ShowDialog(owner);
    }

    private static Window CreateDialogShell(string title, out StackPanel body, out StackPanel footer)
    {
        body = new StackPanel
        {
            Spacing = 10
        };

        footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var window = new Window
        {
            Title = title,
            Width = DialogContentWidth + 32,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(16, 14, 16, 12),
                Child = new Grid
                {
                    RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto)],
                    RowSpacing = 14,
                    Children =
                    {
                        body,
                        footer
                    }
                }
            }
        };

        Grid.SetRow(body, 0);
        Grid.SetRow(footer, 1);

        return window;
    }

    private static TextBlock CreateMessageBlock(string message) => new()
    {
        Text = message,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = DialogContentWidth,
        LineHeight = 20
    };

    private static TextBlock CreatePromptBlock(string prompt) => new()
    {
        Text = prompt,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = DialogContentWidth,
        Opacity = 0.85,
        FontSize = 12
    };

    private static TextBox CreateInputTextBox(bool password = false)
    {
        var textBox = new TextBox
        {
            MinWidth = DialogContentWidth,
            MaxWidth = DialogContentWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        if (password)
        {
            textBox.PasswordChar = '*';
        }

        return textBox;
    }

    private static Button CreateButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 84,
            Padding = new Thickness(12, 6)
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Button CreatePrimaryButton(string text, Action onClick)
    {
        var button = CreateButton(text, onClick);
        button.Classes.Add("accent");
        return button;
    }
}
