﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using YouTube_Downloader_DLL.Classes;
using YouTube_Downloader_DLL.FFmpeg;
using YouTube_Downloader_DLL.FileDownloading;
using YouTube_Downloader_DLL.Helpers;

namespace YouTube_Downloader_DLL.Operations
{
    public class PlaylistOperation : Operation
    {
        class ArgKeys
        {
            public const int Max = 6;
            public const int Min = 4;
            public const string Input = "input";
            public const string Output = "output";
            public const string DASH = "dash";
            public const string PreferredQuality = "preferred_quality";
            public const string PlaylistName = "playlist_name";
            public const string Videos = "videos";
        }

        public const int EventFileDownloadComplete = 1000;
        public const int UpdateProperties = -1;

        int _downloads = 0;
        int _failures = 0;
        int _preferredQuality;
        bool _combining, _processing, _useDash;
        bool? _downloaderSuccessful;

        Exception _operationException;
        FileDownloader downloader;

        public string PlaylistName { get; private set; }
        public List<string> DownloadedFiles { get; set; } = new List<string>();
        public List<VideoInfo> Videos { get; set; } = new List<VideoInfo>();

        /// <summary>
        /// Occurs when a single file download from the playlist is complete.
        /// </summary>
        public event EventHandler<string> FileDownloadComplete;

        public PlaylistOperation()
        {
        }

        private void downloader_Canceled(object sender, EventArgs e)
        {
            _downloaderSuccessful = false;
        }

        private void downloader_Completed(object sender, EventArgs e)
        {
            // If the download didn't fail & wasn't canceled it was most likely successful.
            if (_downloaderSuccessful == null) _downloaderSuccessful = true;
        }

        private void downloader_FileDownloadFailed(object sender, FileDownloadFailedEventArgs e)
        {
            // If one or more files fail, whole operation failed. Might handle it more
            // elegantly in the future.
            _downloaderSuccessful = false;

            e.Exception.Data.Add("FileDownload", e.FileDownload);

            Common.SaveException(e.Exception);
        }

        private void downloader_CalculatedTotalFileSize(object sender, EventArgs e)
        {
            this.FileSize = downloader.TotalSize;
        }

        private void downloader_ProgressChanged(object sender, EventArgs e)
        {
            if (_processing)
                return;

            try
            {
                _processing = true;

                string speed = string.Format(new FileSizeFormatProvider(), "{0:s}", downloader.Speed);
                long longETA = Helper.GetETA(downloader.Speed, downloader.TotalSize, downloader.TotalProgress);
                string eta = longETA == 0 ? "" : "  [ " + FormatLeftTime.Format((longETA) * 1000) + " ]";

                this.ETA = eta;
                this.Speed = speed;
                this.Progress = downloader.TotalProgress;
                this.ReportProgress((int)downloader.TotalPercentage(), null);
            }
            catch { }
            finally
            {
                _processing = false;
            }
        }

        #region Operation members

        public override void Dispose()
        {
            base.Dispose();

            // Free managed resources
            if (downloader != null)
            {
                downloader.Dispose();
                downloader = null;
            }
        }

        public override bool CanPause()
        {
            // Can only pause if currently downloading
            return !_combining && downloader != null && downloader.CanPause;
        }

        public override bool CanResume()
        {
            // Can only resume downloader
            return !_combining && downloader != null && downloader.CanResume;
        }

        public override bool CanStop()
        {
            return this.Status == OperationStatus.Working;
        }

        public override bool OpenContainingFolder()
        {
            try
            {
                Process.Start(Path.GetDirectoryName(this.Output));
            }
            catch
            {
                return false;
            }
            return true;
        }

        public override void Pause()
        {
            // Only the downloader can be paused.
            if (downloader.CanPause)
            {
                downloader.Pause();
                this.Status = OperationStatus.Paused;
            }
        }

        public override void Resume()
        {
            // Only the downloader can be resumed.
            if (downloader.CanResume)
            {
                downloader.Resume();
                this.Status = OperationStatus.Working;
            }
        }

        public override bool Stop(bool cleanup)
        {
            if (this.IsBusy)
                this.CancelAsync();

            this.Status = OperationStatus.Canceled;

            return true;
        }

        #endregion

        protected override void WorkerCompleted(RunWorkerCompletedEventArgs e)
        {
            switch ((OperationStatus)e.Result)
            {
                case OperationStatus.Canceled:
                    // Tell user how many videos was downloaded before being canceled, if any
                    if (this.Videos.Count == 0)
                        this.Title = $"Playlist canceled";
                    else
                        this.Title = $"\"{PlaylistName}\" canceled. {_downloads} of {Videos.Count} videos downloaded";
                    return;
                case OperationStatus.Failed:
                    // Tell user about known exceptions. Otherwise just a simple failed message
                    if (_operationException is TimeoutException)
                        this.Title = $"Timeout. Couldn't get playlist information";
                    else
                    {
                        if (string.IsNullOrEmpty(PlaylistName))
                            this.Title = $"Couldn't download playlist";
                        else
                            this.Title = $"Couldn't download \"{PlaylistName}\"";
                    }
                    return;
            }

            // If code reaches here, it means operation was successful
            if (_failures == 0)
            {
                // All videos downloaded successfully
                this.Title = string.Format("Downloaded \"{0}\" playlist. {1} videos",
                    this.PlaylistName, this.Videos.Count);
            }
            else
            {
                // Some or all videos failed. Tell user how many
                this.Title = string.Format("Downloaded \"{0}\" playlist. {1} of {2} videos, {3} failed",
                    this.PlaylistName, _downloads, this.Videos.Count, _failures);
            }
        }

        protected override void WorkerDoWork(DoWorkEventArgs e)
        {
            try
            {
                // Retrieve playlist name and videos
                if (this.Videos.Count == 0)
                    this.GetPlaylistInfo();
            }
            catch (TimeoutException ex)
            {
                e.Result = OperationStatus.Failed;
                _operationException = ex;
                return;
            }

            try
            {
                int count = 0;

                foreach (VideoInfo video in this.Videos)
                {
                    if (this.CancellationPending)
                        break;

                    // Reset variable(s)
                    _downloaderSuccessful = null;
                    downloader.Files.Clear();

                    count++;

                    VideoFormat videoFormat = Helper.GetPreferredFormat(video, _useDash, _preferredQuality);

                    // Update properties for new video
                    this.ReportProgress(UpdateProperties, new Dictionary<string, object>()
                    {
                        { nameof(Title), $"({count}/{this.Videos.Count}) {video.Title}" },
                        { nameof(Duration), video.Duration },
                        { nameof(FileSize), videoFormat.FileSize }
                    });

                    string finalFile = Path.Combine(this.Output,
                                                    $"{Helper.FormatTitle(videoFormat.VideoInfo.Title)}.{videoFormat.Extension}");

                    this.DownloadedFiles.Add(finalFile);

                    if (!_useDash)
                    {
                        downloader.Files.Add(new FileDownload(finalFile, videoFormat.DownloadUrl));
                    }
                    else
                    {
                        VideoFormat audioFormat = Helper.GetAudioFormat(videoFormat);
                        // Add '_audio' & '_video' to end of filename. Only get filename, not full path.
                        string audioFile = Regex.Replace(finalFile, @"^(.*)(\..*)$", "$1_audio$2");
                        string videoFile = Regex.Replace(finalFile, @"^(.*)(\..*)$", "$1_video$2");

                        // Download audio and video, since DASH has them separated
                        downloader.Files.Add(new FileDownload(audioFile, audioFormat.DownloadUrl));
                        downloader.Files.Add(new FileDownload(videoFile, videoFormat.DownloadUrl));
                    }

                    downloader.Start();

                    // Wait for downloader to finish
                    while (downloader.IsBusy || downloader.IsPaused)
                    {
                        if (this.CancellationPending)
                        {
                            downloader.Stop(false);
                            break;
                        }

                        Thread.Sleep(200);
                    }

                    // Download successful. Combine video & audio if download is a DASH video
                    if (_downloaderSuccessful == true)
                    {
                        if (_useDash)
                        {
                            this.ReportProgress(UpdateProperties, new Dictionary<string, object>()
                            {
                                { nameof(Text), "Combining..." },
                                { nameof(ReportsProgress), false }
                            });

                            if (!this.Combine())
                                _failures++;

                            this.ReportProgress(UpdateProperties, new Dictionary<string, object>()
                            {
                                { nameof(Text), string.Empty },
                                { nameof(ReportsProgress), true }
                            });
                        }

                        _downloads++;
                        this.ReportProgress(EventFileDownloadComplete, finalFile);
                    }
                    // Download failed, cleanup and continue
                    else if (_downloaderSuccessful == false)
                    {
                        _failures++;
                        // Delete all related files. Helper method will check if it exists, throwing no errors
                        Helper.DeleteFiles(downloader.Files.Select(x => x.Path).ToArray());
                    }

                    // Reset before starting new download.
                    this.ReportProgress(ProgressMin, null);
                }

                e.Result = this.CancellationPending ? OperationStatus.Canceled : OperationStatus.Success;
            }
            catch (Exception ex)
            {
                Common.SaveException(ex);
                e.Result = OperationStatus.Failed;
                _operationException = ex;
            }
        }

        protected override void WorkerProgressChanged(ProgressChangedEventArgs e)
        {
            if (e.UserState == null)
                return;

            // Used to set multiple properties
            if (e.UserState is Dictionary<string, object>)
            {
                foreach (KeyValuePair<string, object> pair in (e.UserState as Dictionary<string, object>))
                {
                    this.GetType().GetProperty(pair.Key).SetValue(this, pair.Value);
                }
            }
            else if (e.ProgressPercentage == EventFileDownloadComplete) // FileDownloadComplete
            {
                OnFileDownloadComplete(e.UserState as string);
            }
        }

        protected override void WorkerStart(Dictionary<string, object> args)
        {
            if (!(args.Count.Any(ArgKeys.Min, ArgKeys.Max)))
                throw new ArgumentException();

            // Temporary title.
            this.Title = "Getting playlist info...";
            this.ReportsProgress = true;

            this.Input = (string)args[ArgKeys.Input];
            this.Output = (string)args[ArgKeys.Output];
            this.Link = this.Input;

            _useDash = (bool)args[ArgKeys.DASH];
            _preferredQuality = (int)args[ArgKeys.PreferredQuality];

            if (args.Count == ArgKeys.Max)
            {
                this.PlaylistName = (string)args[ArgKeys.PlaylistName];

                if (args[ArgKeys.Videos] != null)
                    this.Videos.AddRange((IEnumerable<VideoInfo>)args[ArgKeys.Videos]);
            }

            downloader = new FileDownloader();

            // Attach downloader events
            downloader.Canceled += downloader_Canceled;
            downloader.Completed += downloader_Completed;
            downloader.FileDownloadFailed += downloader_FileDownloadFailed;
            downloader.CalculatedTotalFileSize += downloader_CalculatedTotalFileSize;
            downloader.ProgressChanged += downloader_ProgressChanged;
        }

        private bool Combine()
        {
            string audio = downloader.Files[0].Path;
            string video = downloader.Files[1].Path;
            // Remove '_video' from video file to get a final filename.
            string output = video.Replace("_video", string.Empty);
            FFmpegResult<bool> result = null;

            _combining = true;

            try
            {
                result = FFmpegHelper.CombineDash(video, audio, output);

                // Save errors if combining failed
                if (!result.Value)
                {
                    var sb = new StringBuilder();

                    sb.AppendLine(this.Title);

                    foreach (string error in result.Errors)
                        sb.AppendLine($" - {error}");

                    this.ErrorsInternal.Add(sb.ToString());
                }

                // Cleanup the separate audio and video files
                Helper.DeleteFiles(audio, video);
            }
            catch (Exception ex)
            {
                Common.SaveException(ex);
                return false;
            }
            finally
            {
                _combining = false;
            }

            return result.Value;
        }

        private void OnFileDownloadComplete(string file)
        {
            this.FileDownloadComplete?.Invoke(this, file);
        }

        private void GetPlaylistInfo()
        {
            var reader = new PlaylistReader(this.Input);
            VideoInfo video;

            this.PlaylistName = reader.WaitForPlaylist().Name;

            while ((video = reader.Next()) != null)
            {
                if (this.CancellationPending)
                {
                    reader.Stop();
                    break;
                }

                this.Videos.Add(video);
            }
        }

        public Dictionary<string, object> Args(string url,
                                               string output,
                                               bool dash,
                                               int preferredQuality)
        {
            return new Dictionary<string, object>()
            {
                { ArgKeys.Input, url },
                { ArgKeys.Output, output },
                { ArgKeys.DASH, dash },
                { ArgKeys.PreferredQuality, preferredQuality }
            };
        }

        public Dictionary<string, object> Args(string url,
                                               string output,
                                               bool dash,
                                               int preferredQuality,
                                               string playlistName,
                                               ICollection<VideoInfo> videos)
        {
            return new Dictionary<string, object>()
            {
                { ArgKeys.Input, url },
                { ArgKeys.Output, output },
                { ArgKeys.DASH, dash },
                { ArgKeys.PreferredQuality, preferredQuality },
                { ArgKeys.PlaylistName, playlistName },
                { ArgKeys.Videos, videos }
            };
        }
    }
}
