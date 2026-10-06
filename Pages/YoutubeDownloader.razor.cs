using Microsoft.JSInterop;
using System.Text.RegularExpressions;
using YoutubeExplode.Videos.Streams;
using YoutubeExplode;
using static ToolsServer.Enums;
using YoutubeExplode.Converter;
using Microsoft.AspNetCore.Components;
using YoutubeExplode.Videos;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using NAudio.Lame;
using NAudio.Wave;
using YoutubeExplode.Common;
using ToolsServer.Shared;

namespace ToolsServer.Pages
{
    public partial class YoutubeDownloader
    {
        [CascadingParameter]
        public MainLayout Layout { get; set; }
        [Inject]
        public IJSRuntime JSRuntime { get; set; }
        [Inject]
        public IHubContext<FileHub> FileHubContext { get; set; }
        [Inject]
        public YoutubeClient Youtube { get; set; }
        private string url;
        private string thumbnailUrl;
        private string errorMessage;
        private string title;
        private bool isLoading;
        private bool showLoader;
        private bool isPlaylist;
        private int downloadProgress;
        private readonly string downloadsPath = "wwwroot/downloads/";
        private Dictionary<VideoId, string> videos = new();
        private VideoType selectedType = VideoType.Audio;

        protected override void OnInitialized()
        {
            base.OnInitialized();
            Layout.Title = "Youtube Downloader";
        }

        private async Task UrlOnInput(ChangeEventArgs e)
        {
            url = e.Value.ToString();
            thumbnailUrl = string.Empty;
            title = string.Empty;
            await GetVideoData();
            StateHasChanged();
        }

        private async Task GetVideoData(bool initVideoDictionary = false)
        {
            try
            {
                errorMessage = string.Empty;

                if (initVideoDictionary)
                    videos = new();

                if (isPlaylist)
                {
                    var playlist = await Youtube.Playlists.GetAsync(url);
                    thumbnailUrl = playlist.Thumbnails.GetWithHighestResolution().Url;
                    title = $"{Regex.Replace(playlist.Title, @"[\\/:*?""<>|]", "_")}";

                    if (initVideoDictionary)
                    {
                        await foreach (var batch in Youtube.Playlists.GetVideoBatchesAsync(url))
                        {
                            foreach (var playlistVideo in batch.Items)
                            {
                                videos.Add(playlistVideo.Id, playlistVideo.Title);
                            }
                        }
                    }
                }
                else
                {
                    var video = await Youtube.Videos.GetAsync(url);
                    title = video.Title;
                    thumbnailUrl = video.Thumbnails.GetWithHighestResolution().Url;
                    if (initVideoDictionary)
                        videos.Add(video.Id, video.Title);
                }
            }
            catch (Exception ex)
            {
                thumbnailUrl = string.Empty;
                title = string.Empty;
                errorMessage = $"An error occurred: {ex.Message}";
            }
        }

        private async Task PlaylistSwitchOnChange(ChangeEventArgs e)
        {
            _ = bool.TryParse(e.Value.ToString(), out isPlaylist);
            if (!string.IsNullOrEmpty(url))
                await GetVideoData();
            if (isPlaylist && selectedType == VideoType.VideoAndAudioHighestQuality)
                selectedType = VideoType.VideoAndAudioQuick;
        }

        private async Task DownloadButtonClicked()
        {
            try
            {
                errorMessage = string.Empty;

                await GetVideoData(true);

                showLoader = videos.Count == 1;
                isLoading = true;
                StateHasChanged();

                await SaveVideo();

                isLoading = false;
                downloadProgress = 0;
            }
            catch (Exception ex)
            {
                errorMessage = $"An error occurred: {ex.Message}";
            }
            finally
            {
                isLoading = false;
                showLoader = false;
            }
        }

        private async Task SaveVideo()
        {
            var count = 1;
            var connectionId = videos.Count > 1 ? await JSRuntime.InvokeAsync<string>("getConnectionId") : null;
            var taskList = new List<Task>();

            var numberOfBatches = (int)Math.Ceiling((double)videos.Count / 20);
            for (int i = 0; i < numberOfBatches; i++)
            {
                var filteredVideos = videos.Skip(i * 20).Take(20).ToList();

                foreach (var video in filteredVideos)
                {
                    var fileName = string.Empty;
                    byte[] fileBytes = null;
                    var ext = string.Empty;
                    var failureCount = 0;
                    var success = false;
                    Exception lastException = null;

                    while (failureCount < 3 && !success)
                    {
                        try
                        {
                            (fileName, fileBytes, ext) = await GetVideoStreamData(count, video);
                            success = true;
                        }
                        catch (Exception ex)
                        {
                            lastException = ex;
                            failureCount++;
                        }
                    }

                    if (fileBytes == null)
                    {
                        if (videos.Count == 1)
                            errorMessage = $"Failed to download '{video.Value}': {lastException?.Message}";
                        continue;
                    }

                    if (videos.Count > 1)
                    {
                        taskList.Add(FileHubContext.Clients.Client(connectionId).SendAsync("ReceiveFile", fileName, Convert.ToBase64String(fileBytes), CancellationToken.None));
                    }
                    else
                    {
                        showLoader = true;
                        StateHasChanged();
                        var contentType = selectedType == VideoType.Audio || selectedType == VideoType.AudioMp3 ? "audio/" : "video/" + ext;
                        await JSRuntime.InvokeVoidAsync("BlazorDownloadFileBlob", CancellationToken.None, fileBytes, contentType, fileName);
                        showLoader = false;
                    }

                    downloadProgress = count * 100 / videos.Count;
                    StateHasChanged();

                    count++;
                }

                await Task.WhenAll(taskList);

                if (videos.Count > 1)
                {
                    showLoader = true;
                    StateHasChanged();
                    await JSRuntime.InvokeVoidAsync("BlazorGenerateAndDownloadZip", CancellationToken.None, title + (numberOfBatches > 1 ? $" - part {i + 1}" : ""));
                    showLoader = false;
                }
            }
        }

        private async Task<(string, byte[], string)> GetVideoStreamData(int count, KeyValuePair<VideoId, string> video)
        {
            IStreamInfo streamInfo = null;
            byte[] fileBytes = null;

            var streamManifest = await Youtube.Videos.Streams.GetManifestAsync(video.Key);

            if (selectedType == VideoType.Audio || selectedType == VideoType.AudioMp3)
                streamInfo = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();
            else if (selectedType == VideoType.Video)
                streamInfo = streamManifest.GetVideoOnlyStreams().Where(s => s.Container == Container.Mp4).GetWithHighestVideoQuality();
            else if (selectedType == VideoType.VideoAndAudioQuick || selectedType == VideoType.VideoAndAudioHighestQuality)
            {
                var muxedStreams = streamManifest.GetMuxedStreams();
                if (muxedStreams.Any())
                    streamInfo = muxedStreams.GetWithHighestVideoQuality();
            }

            var fileName = $"{(videos.Count > 1 ? count + " - " : "")}{Regex.Replace(video.Value, @"[\\/:*?""<>|]", "_")}";

            // Most videos no longer expose a combined video+audio (muxed) stream, so quick download
            // falls back to the same ffmpeg merge path as "Highest Quality" when none is available.
            var needsMerge = streamInfo == null &&
                (selectedType == VideoType.VideoAndAudioQuick || (selectedType == VideoType.VideoAndAudioHighestQuality && !isPlaylist));

            string ext;
            if (needsMerge)
            {
                ext = "mp4";
                var filePath = $"{downloadsPath}{fileName}.{ext}";
                if (File.Exists(filePath)) File.Delete(filePath);
                await Youtube.Videos.DownloadAsync(video.Key, filePath);
                fileBytes = await File.ReadAllBytesAsync(filePath);
                File.Delete(filePath);
            }
            else
            {
                ext = streamInfo.Container.Name;
                using var stream = await Youtube.Videos.Streams.GetAsync(streamInfo);
                using var memoryStream = new MemoryStream();
                await stream.CopyToAsync(memoryStream);
                fileBytes = memoryStream.ToArray();
                if (selectedType == VideoType.AudioMp3 && ext != "mp3")
                {
                    fileBytes = await ConvertToMp3(fileName, ext, fileBytes);
                    ext = "mp3";
                }
            }

            fileName = $"{fileName}.{ext}";

            return (fileName, fileBytes, ext);
        }

        private static async Task<byte[]> ConvertToMp3(string fileName, string ext, byte[] fileBytes)
        {
            string tempWebmPath = Path.Combine(Path.GetTempPath(), $"{fileName}.{ext}");
            File.WriteAllBytes(tempWebmPath, fileBytes);
            using (var reader = new MediaFoundationReader(tempWebmPath))
            {
                using (var mp3Stream = new MemoryStream())
                {
                    using (var mp3Writer = new LameMP3FileWriter(mp3Stream, reader.WaveFormat, LAMEPreset.VBR_90))
                    {
                        reader.CopyTo(mp3Writer);
                    }
                    fileBytes = mp3Stream.ToArray();
                }
            }
            File.Delete(tempWebmPath);
            return fileBytes;
        }
    }
}
