using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ToolsServer.Shared;

namespace ToolsServer.Pages
{
    public partial class VideoDownloader
    {
        [CascadingParameter]
        public MainLayout Layout { get; set; }
        private string videoUrl = "";
        private string downloadStatus = "";
        private bool isDownloading = false;
        private int downloadProgress = 0;
        private string downloadProgressString => $"{downloadProgress}%";
        [Inject]
        public IJSRuntime JSRuntime { get; set; }

        protected override void OnInitialized()
        {
            base.OnInitialized();
            Layout.Title = "Video Downloader";
        }

        private async Task DownloadVideo()
        {
            if (!string.IsNullOrWhiteSpace(videoUrl))
            {
                isDownloading = true;

                try
                {
                    using (var httpClient = new HttpClient())
                    {
                        using (var response = await httpClient.GetAsync(videoUrl, HttpCompletionOption.ResponseHeadersRead))
                        {
                            var contentLength = response.Content.Headers.ContentLength ?? -1;
                            var stream = await response.Content.ReadAsStreamAsync();
                            var contentType = response.Content.Headers.ContentType.MediaType;

                            // Use the ContentDisposition header if available, otherwise use a timestamp-based name
                            var fileName = response.Content.Headers.ContentDisposition?.FileName ?? $"{DateTime.Now:yyyyMMddHHmmss}.mp4";

                            // Convert the stream to a byte array
                            using (var memoryStream = new MemoryStream())
                            {
                                var buffer = new byte[65536];
                                int bytesRead;
                                long totalBytesRead = 0;

                                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    await memoryStream.WriteAsync(buffer, 0, bytesRead);
                                    totalBytesRead += bytesRead;

                                    if (contentLength > 0)
                                    {
                                        downloadProgress = (int)((totalBytesRead * 100) / contentLength);
                                        StateHasChanged(); // Trigger UI update
                                    }
                                }

                                // Send the byte array to the JavaScript function
                                await JSRuntime.InvokeVoidAsync("BlazorDownloadFileBlob", memoryStream.ToArray(), contentType, fileName);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    downloadStatus = "Failed to download the video: " + ex.Message;
                }
                finally
                {
                    isDownloading = false;
                    downloadProgress = 0;
                }
            }
            else
            {
                downloadStatus = "Please enter a valid video URL.";
            }
        }
    }
}
