using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace TunerFish.Views;

public partial class AboutWindow : Window
{
    // The XAML previewer needs a parameterless constructor.
    public AboutWindow() : this(null) { }

    /// <param name="mpvVersion">libmpv's version string (e.g. "mpv 0.37.0"), or null if it isn't available.</param>
    public AboutWindow(string? mpvVersion)
    {
        InitializeComponent();

        Title = $"About {AppInfo.Name}";
        NameText.Text = AppInfo.Name;
        VersionText.Text = $"Version {AppInfo.Version}";
        DescriptionText.Text = AppInfo.Description;
        CreditText.Text = AppInfo.Credit;
        CopyrightText.Text = AppInfo.Copyright;
        var avalonia = AppInfo.VersionOf(typeof(AvaloniaObject).Assembly);
        var mpv = mpvVersion is null ? "libmpv (not found)" : $"libmpv ({mpvVersion})";
        CreditsText.Text = $"Built with Avalonia {avalonia}. Playback by {mpv}.";

        CloseButton.Click += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
    }
}
