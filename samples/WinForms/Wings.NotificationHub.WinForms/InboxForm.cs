namespace Wings.NotificationHub.WinForms;

/// <summary>A resizable window hosting an <see cref="InboxPanel"/> (an alternative to the bell dropdown, e.g. for a "Notifications" menu item).</summary>
public sealed class InboxForm : Form
{
    public InboxForm(InboxClient client, Uri? webBaseAddress = null)
    {
        Text = "Notifications";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(440, 640);
        MinimumSize = new Size(320, 360);
        ShowIcon = false;
        Panel = new InboxPanel { Dock = DockStyle.Fill, Client = client, WebBaseAddress = webBaseAddress };
        Controls.Add(Panel);
        Shown += async (_, _) =>
        {
            await Panel.ReloadAsync();
            try { Text = (await client.GetBrandingAsync()).InboxTitle ?? Text; } catch (InboxClientException) { /* keep default title */ }
        };
    }

    public InboxPanel Panel { get; }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Panel.Client = null; // detach event handlers from the shared client
        base.OnFormClosed(e);
    }
}
