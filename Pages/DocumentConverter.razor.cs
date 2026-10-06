using Aspose.Words;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using ToolsServer.Shared;
using static ToolsServer.Enums;

namespace ToolsServer.Pages
{
    public partial class DocumentConverter
    {
        [CascadingParameter]
        public MainLayout Layout { get; set; }
        [Inject]
        public IJSRuntime JSRuntime { get; set; }
        public DocumentType targetDocumentType { get; set; }
        public long maxAllowedSize { get; set; } = 50 * 1024 * 1024;
        public bool isLoading { get; set; }
        public IBrowserFile? file { get; set; }
        public string errorMessage { get; set; }

        private readonly string downloadsPath = "wwwroot/downloads/";

        protected override async Task OnInitializedAsync()
        {
            base.OnInitialized();
            Layout.Title = "Document Converter";
        }

        private async Task HandleFileSelected(InputFileChangeEventArgs e)
        {
            file = e.File;
        }

        private async Task ConvertButtonClicked()
        {
            try
            {
                errorMessage = string.Empty;
                using var stream = file.OpenReadStream(maxAllowedSize);
                using var memoryStream = new MemoryStream();
                await stream.CopyToAsync(memoryStream);
                var doc = new Document(memoryStream);
                var newFileName = Path.GetFileNameWithoutExtension(file.Name) + GetFileExtension();
                var filePath = downloadsPath + newFileName;
                doc.Save(filePath);
                var fileBytes = await File.ReadAllBytesAsync(filePath);
                await JSRuntime.InvokeVoidAsync("BlazorDownloadFileBlob", CancellationToken.None, fileBytes, file.ContentType, newFileName);
                File.Delete(filePath);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
            }
        }


        private string GetFileExtension()
        {
            return targetDocumentType switch
            {
                DocumentType.PDF => ".pdf",
                DocumentType.Word => ".docx",
                DocumentType.Excel => ".xlsx",
                _ => throw new NotImplementedException(),
            };
        }
    }
}
