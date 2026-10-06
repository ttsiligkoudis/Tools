using FilesClient;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using System.IO.Compression;
using ToolsServer.Shared;

namespace ToolsServer.Pages
{
    public partial class FilesTransfer
    {
        [CascadingParameter]
        public MainLayout Layout { get; set; }
        [Inject]
        public FilesClientHandler FilesClientHandler { get; set; }
        [Inject]
        public IJSRuntime JSRuntime { get; set; }
        [Inject]
        public IConfiguration Configuration { get; set; }
        private string errorMessage;
        private string email;
        private string title;
        private string message = string.Empty;
        private List<IBrowserFile> files = new();
        private bool isLoading;
        private bool success;
        private readonly long maxAllowedSizePerRequest = 95 * 1024 * 1024;
        private readonly long maxAllowedSize = (long)2 * 1000 * 1024 * 1024;

        protected override async void OnInitialized()
        {
            base.OnInitialized();
            Layout.Title = "File Transfer";
        }

        private void HandleFileSelection(InputFileChangeEventArgs e)
        {
            files.AddRange(e.GetMultipleFiles()?.Where(w => !files.Any(w2 => w2.Name == w.Name)).ToList() ?? new());
            StateHasChanged();
        }

        private async Task OpenInputFile()
        {
            await JSRuntime.InvokeVoidAsync("clickById", "inputFile");
        }

        private async void SubmitForm()
        {
            try
            {
                isLoading = true;
                success = false;
                errorMessage = string.Empty;

                //await JSRuntime.InvokeVoidAsync("UploadFileForm", Configuration["FileService:Token"], Configuration["Host"]);

                using var stream = new MemoryStream();

                if (files.Count == 1)
                {
                    await files.First().OpenReadStream(maxAllowedSize).CopyToAsync(stream);
                }
                else
                {
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Create, true);
                    foreach (var file in files)
                    {
                        var entry = archive.CreateEntry(file.Name);
                        using var entryStream = entry.Open();
                        await file.OpenReadStream(maxAllowedSize).CopyToAsync(entryStream);
                    }
                }

                stream.Position = 0;
                var fileName = files.Count == 1 ? files.First().Name : title.Trim() + ".zip";
                var contentType = files.Count == 1 ? files.First().ContentType : "application/zip";
                var filePath = $"C:\\github\\FileService\\Files\\{fileName}";

                if (!File.Exists(filePath))
                {
                    using FileStream fileStream = new(filePath, FileMode.Create);
                    await stream.CopyToAsync(fileStream);
                }

                FilesClientHandler.UploadAndSendFileAsync(filePath, contentType, email, title, message);

                isLoading = false;
                success = true;
                StateHasChanged();
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                isLoading = false;
                StateHasChanged();
            }

        }
    }
}
