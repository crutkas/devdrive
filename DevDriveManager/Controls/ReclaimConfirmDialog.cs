using DevDriveManager.ViewModels;
using DevDriveReclaim;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Controls;

/// <summary>
/// The last honest moment before anything is deleted.
/// </summary>
/// <remarks>
/// Three things are stated in a fixed order, because the order is the argument: what is about to
/// happen, what of it cannot be undone, and how bad the worst item in the pile is. The amount freed
/// is deliberately not the largest text on the screen — this is a confirmation, and a confirmation
/// that leads with the reward is a nudge.
/// <para>
/// A <see cref="ReclaimRisk.Careful"/> item requires typing DELETE. Anything gated behind a single
/// click gets the same reflexive click as everything else, and the Careful tier exists precisely for
/// the items where that reflex is expensive.
/// </para>
/// </remarks>
internal static class ReclaimConfirmDialog
{
    /// <summary>Guarded because WinUI allows exactly one <see cref="ContentDialog"/> at a time.</summary>
    private static bool s_open;

    public static async Task<bool> ShowAsync(XamlRoot? xamlRoot, ReclaimConfirmationRequest request)
    {
        if (s_open || xamlRoot is null)
        {
            return false;
        }

        var body = new StackPanel { Spacing = 12 };

        body.Children.Add(new TextBlock
        {
            Text = request.RiskText,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });

        var recovery = new TextBlock
        {
            Text = request.RecoveryText,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(recovery, "ReclaimConfirmRecovery");
        body.Children.Add(recovery);

        if (request.VolumeSplitText.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Space comes back: " + request.VolumeSplitText,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });
        }

        TextBox? typed = null;
        if (request.RequiresTypedConfirmation)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Type DELETE to confirm.",
                TextWrapping = TextWrapping.Wrap,
            });

            typed = new TextBox { PlaceholderText = "DELETE" };
            AutomationProperties.SetAutomationId(typed, "ReclaimConfirmTypedInput");
            AutomationProperties.SetName(typed, "Type DELETE to confirm");
            body.Children.Add(typed);
        }

        var dialog = new ContentDialog
        {
            Title = request.Headline,
            Content = body,
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",

            // Cancel is the default so that Enter, and a dialog that appears under a moving cursor,
            // both do the harmless thing.
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };

        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style)
            && style is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }

        AutomationProperties.SetAutomationId(dialog, "ReclaimConfirmDialog");

        if (typed is not null)
        {
            dialog.IsPrimaryButtonEnabled = false;
            typed.TextChanged += (_, _) =>
                dialog.IsPrimaryButtonEnabled =
                    string.Equals(typed.Text.Trim(), "DELETE", StringComparison.Ordinal);
        }

        s_open = true;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            s_open = false;
        }
    }
}
