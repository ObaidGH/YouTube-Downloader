﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YouTube_Downloader_DLL.Classes;

namespace YouTube_Downloader_DLL
{
    public partial class UpdateDownloader : Form
    {
        public const string ChangelogUrl = "https://raw.githubusercontent.com/AlexxEG/YouTube-Downloader/master/CHANGELOG.md";
        public const string GetReleasesAPIUrl = "https://api.github.com/repos/AlexxEG/YouTube-Downloader/releases";
        public const string UserAgent = "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2;)";

        string _latestDownloadUrl = string.Empty;
        WebClient _webClient;

        public new IWin32Window Owner { get; private set; }

        public UpdateDownloader()
        {
            InitializeComponent();
        }

        private void UpdateDownloader_Load(object sender, EventArgs e)
        {
            lLocalVersion.Text = Common.VersionString;

            this.GetJsonResponseAsync(GetJsonResponse_Callback);

            this.GetChangelogAsync();
        }

        private void btnDownload_Click(object sender, EventArgs e)
        {
            if (btnDownload.Text == "Download")
            {
                _webClient = new WebClient();
                _webClient.DownloadProgressChanged += webClient_DownloadProgressChanged;
                _webClient.DownloadFileCompleted += webClient_DownloadFileCompleted;

                btnDownload.Enabled = false;
                btnCancel.Enabled = true;
                btnClose.Enabled = false;

                _webClient.DownloadFileAsync(new Uri(_latestDownloadUrl), Path.GetFileName(_latestDownloadUrl));
            }
            else if (btnDownload.Text == "Install")
            {
                if (!File.Exists(Path.GetFileName(_latestDownloadUrl)))
                {
                    MessageBox.Show(this, "Couldn't find downloaded installer.");
                    return;
                }

                try
                {
                    Process.Start(Path.GetFileName(_latestDownloadUrl));
                }
                catch (Exception ex)
                {
                    Common.SaveException(ex);
                    MessageBox.Show(this, "Couldn't open installer. Exception message: " + ex.Message);
                    return;
                }

                this.Close();

                if (this.Owner is Form) // WinForms
                {
                    Application.Exit();
                }
                else if (this.Owner is System.Windows.Window) // WPF
                {
                    System.Windows.Application.Current.Shutdown();
                }
            }
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            _webClient.CancelAsync();

            Helper.DeleteFiles(Path.GetFileName(_latestDownloadUrl));

            progressBar1.Value = 0;
            btnDownload.Enabled = true;
            btnCancel.Enabled = false;
            btnClose.Enabled = true;
        }

        private void btnClose_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void webClient_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            progressBar1.Value = e.ProgressPercentage;

            lStatus.Text = string.Format("{0}\n{1}% - {2}/{3}",
                Path.GetFileName(_latestDownloadUrl),
                e.ProgressPercentage,
                Helper.FormatFileSize(e.BytesReceived),
                Helper.FormatFileSize(e.TotalBytesToReceive));
        }

        private void webClient_DownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
        {
            progressBar1.Value = progressBar1.Maximum;
            btnDownload.Text = "Install";
            btnDownload.Enabled = true;
            btnClose.Enabled = true;

            _webClient.Dispose();
            _webClient = null;
        }

        private void webBrowser1_Navigating(object sender, WebBrowserNavigatingEventArgs e)
        {
            if (e.Url.LocalPath == "blank")
                return;

            e.Cancel = true;

            if (e.Url.AbsoluteUri.StartsWith("about"))
                return;

            Process.Start(e.Url.ToString());
        }

        private void GetJsonResponse_Callback(JArray response)
        {
            // Store download url instead of requesting it twice
            _latestDownloadUrl = response[0]["assets"][0]["browser_download_url"].ToString();

            var v = new Version(response[0]["tag_name"].ToString().TrimStart('v'));

            lOnlineVersion.Text = v.ToString();

            // Re-align download button
            btnDownload.Left = lOnlineVersion.Right + 6;

            if (Common.Version == v)
            {
                // Local and online version is the same
                lLocalVersion.ForeColor = lOnlineVersion.ForeColor = System.Drawing.SystemColors.ControlText;
            }
            else if (Common.Version < v)
            {
                // Local version is outdated
                lLocalVersion.ForeColor = System.Drawing.Color.Red;
                lOnlineVersion.ForeColor = System.Drawing.Color.Green;

                btnDownload.Enabled = true;
            }
            else
            {
                // Online version is outdated. This should only happen on development machines,
                // so just gonna treat it like's up-to-date.
                lLocalVersion.ForeColor = lOnlineVersion.ForeColor = System.Drawing.SystemColors.ControlText;
            }
        }

        [Obsolete]
        private new void Show()
        {
            throw new NotImplementedException();
        }

        [Obsolete]
        private new void Show(IWin32Window owner)
        {
            throw new NotImplementedException();
        }

        [Obsolete]
        private new void ShowDialog()
        {
            throw new NotImplementedException();
        }

        public new void ShowDialog(IWin32Window owner)
        {
            this.Owner = owner;
            base.ShowDialog(owner);
        }

        private async void GetChangelogAsync()
        {
            // Here we're using .html files stored in resources to render the pages. These .html files contains
            // the styles needed. This code will insert the data using string.Format.
            try
            {
                var wc = new WebClient();
                var task = await wc.DownloadStringTaskAsync(ChangelogUrl);
                var html = CommonMark.CommonMarkConverter.Convert(task);

                // Insert changelog into html template
                html = string.Format(Properties.Resources.Changelog, html);

                webBrowser1.DocumentText = html;
            }
            catch (Exception ex)
            {
                // Insert exception info into html template
                var html = string.Format(Properties.Resources.Exception, ex.ToString());

                webBrowser1.DocumentText = html;

                // Make the button toggle detailed exception info
                webBrowser1.DocumentCompleted += delegate
                {
                    webBrowser1.Document.GetElementById("toggle").Click += delegate
                    {
                        var content = webBrowser1.Document.GetElementById("content");

                        if (content.Style.Contains("none"))
                            content.Style = "display: block;";
                        else
                            content.Style = "display: none;";
                    };
                };
            }
        }

        private async void GetJsonResponseAsync(Action<JArray> callback)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(GetReleasesAPIUrl);
            request.KeepAlive = false;
            request.UserAgent = UserAgent;

            string json = string.Empty;

            await Task.Run(delegate
            {
                HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                json = new StreamReader(response.GetResponseStream()).ReadToEnd();
            });

            callback.Invoke(JsonConvert.DeserializeObject<JArray>(json));
        }
    }
}
