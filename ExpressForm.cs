using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MuseDesktop;

/// <summary>
/// A bridge-owned Adobe Express window. Express has no desktop API, but
/// its menus/panels are web DOM, so hosting it in our own WebView2 lets
/// the bridge run page JavaScript (clicks, text, file inputs) through
/// /express/eval instead of only pushing pixels around.
/// </summary>
public sealed class ExpressForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private bool _ready;

    public ExpressForm(string dataDir)
    {
        Text = "Adobe Express — Muse Bridge";
        Width = 1280;
        Height = 860;
        MinimumSize = new Size(640, 480);
        Controls.Add(_web);
        Load += async (_, _) =>
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: Path.Combine(dataDir, "WebView2-Express"));
                await _web.EnsureCoreWebView2Async(env);
                _web.CoreWebView2.NewWindowRequested += (_, args) =>
                {
                    args.Handled = true;
                    if (!string.IsNullOrEmpty(args.Uri))
                    {
                        _web.CoreWebView2.Navigate(args.Uri);
                    }
                };
                _ready = true;
                _web.Source = new Uri("https://new.express.adobe.com");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open Adobe Express: " + ex.Message,
                    "Muse Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        FormClosing += (_, e) =>
        {
            // Keep the signed-in session alive; hide instead of closing.
            e.Cancel = true;
            Hide();
        };
    }

    public async Task<string> EvalAsync(string script)
    {
        if (!_ready || _web.CoreWebView2 is null)
        {
            return "{\"error\":\"express window still loading\"}";
        }
        // ExecuteScriptAsync returns the result JSON-encoded.
        return await _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    public object Info()
    {
        return new
        {
            ready = _ready,
            url = _ready ? _web.CoreWebView2?.Source : null,
            title = _ready ? _web.CoreWebView2?.DocumentTitle : null,
            visible = Visible,
        };
    }
}
