using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HtmlLinkParser
{
    public class MainForm : Form
    {
        private readonly TextBox txtUrl = new() { Dock = DockStyle.Fill, PlaceholderText = "введите URL, например: wikipedia.org" };
        private readonly Button btnLoad = new() { Text = "Загрузить", AutoSize = true };
        private readonly Button btnCancel = new() { Text = "Отмена", AutoSize = true, Enabled = false };

        private readonly SplitContainer split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 420 };
        private readonly ListBox lbLinks = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
        private readonly RichTextBox rtbHtml = new() { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false };
        private readonly StatusStrip status = new();
        private readonly ToolStripStatusLabel lblStatus = new() { Text = "Готово" };
        private readonly ToolStripProgressBar progress = new() { Style = ProgressBarStyle.Marquee, Visible = false };

        private CancellationTokenSource? _cts;
        private readonly HttpClient _http;

        public MainForm()
        {
            Text = "HTML Link Parser (async)";
            Width = 1000;
            Height = 700;
            StartPosition = FormStartPosition.CenterScreen;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) HtmlLinkParser/1.0");

            var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 40, ColumnCount = 4 };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            top.Controls.Add(txtUrl, 0, 0);
            top.Controls.Add(btnLoad, 1, 0);
            top.Controls.Add(btnCancel, 2, 0);

            split.Panel1.Controls.Add(lbLinks);
            split.Panel1.Padding = new Padding(8, 0, 4, 0);
            split.Panel2.Controls.Add(rtbHtml);
            split.Panel2.Padding = new Padding(4, 0, 8, 0);

            status.Items.Add(lblStatus);
            status.Items.Add(new ToolStripStatusLabel { Spring = true });
            status.Items.Add(progress);

            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(status);

            btnLoad.Click += async (_, __) => await StartLoadAsync();
            btnCancel.Click += (_, __) => _cts?.Cancel();
            txtUrl.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await StartLoadAsync(); } };
            lbLinks.DoubleClick += (_, __) => OpenSelectedLink();
            lbLinks.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenSelectedLink(); } };

            var cm = new ContextMenuStrip();
            cm.Items.Add("Открыть в браузере", null, (_, __) => OpenSelectedLink());
            cm.Items.Add("Копировать", null, (_, __) => CopySelectedLink());
            lbLinks.ContextMenuStrip = cm;
        }

        private async Task StartLoadAsync()
        {
            var raw = txtUrl.Text.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                MessageBox.Show("Введите адрес страницы.", "Нет адреса", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                raw = "https://" + raw;
            }

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            {
                MessageBox.Show("Некорректный адрес.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            try
            {
                SetBusy(true, $"Загружаю {uri} …");
                lbLinks.Items.Clear();
                rtbHtml.Clear();

                string html = await FetchHtmlAsync(uri, _cts.Token);
                rtbHtml.Text = html;

                var links = ExtractLinks(html, uri);
                foreach (var link in links)
                    lbLinks.Items.Add(link);

                lblStatus.Text = $"Готово: найдено ссылок — {lbLinks.Items.Count}";
            }
            catch (OperationCanceledException)
            {
                lblStatus.Text = "Отменено.";
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException se)
            {
                MessageBox.Show($"Сетевая ошибка: {se.Message}", "HTTP ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Ошибка сети.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Ошибка.";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task<string> FetchHtmlAsync(Uri uri, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            resp.EnsureSuccessStatusCode();

            return await resp.Content.ReadAsStringAsync(ct);
        }

        private static IEnumerable<string> ExtractLinks(string html, Uri baseUri)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var rx = new Regex(
                @"<a\b[^>]*\bhref\s*=\s*(?:(['""])(.*?)\1|([^\s>""']+))",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

            foreach (Match m in rx.Matches(html))
            {
                var raw = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                if (string.IsNullOrWhiteSpace(raw)) continue;

                raw = WebUtility.HtmlDecode(raw).Trim();

                if (raw.StartsWith("#")) continue;
                if (raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
                if (raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                if (raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) continue;
                if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                if (Uri.TryCreate(baseUri, raw, out var u) &&
                    (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                {
                    set.Add(u.AbsoluteUri);
                }
            }

            return set;
        }

        private void SetBusy(bool busy, string? msg = null)
        {
            btnLoad.Enabled = !busy;
            btnCancel.Enabled = busy;
            progress.Visible = busy;
            if (msg != null) lblStatus.Text = msg;
        }

        private void OpenSelectedLink()
        {
            if (lbLinks.SelectedItem is string url)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Не удалось открыть ссылку", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void CopySelectedLink()
        {
            if (lbLinks.SelectedItem is string url)
            {
                try { Clipboard.SetText(url); } catch { }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cts?.Cancel();
            base.OnFormClosing(e);
        }
    }
}
//test123
//test1
//test2
//test3
//test4
//test5
//test6
//test7
//test8
//test9
//test10
//test11
//test12
//test13
