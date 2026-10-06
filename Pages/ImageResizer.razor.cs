using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using ToolsServer.Shared;
using static ToolsServer.Enums;

namespace ToolsServer.Pages
{
    public partial class ImageResizer
    {
        [CascadingParameter]
        public MainLayout Layout { get; set; }
        private ResizeModel Model { get; set; } = new();
        [Inject]
        public IJSRuntime JSRuntime { get; set; }

        protected override void OnInitialized()
        {
            base.OnInitialized();
            Layout.Title = "Image Resizer";
        }

        private async Task HandleFileSelected(InputFileChangeEventArgs e)
        {
            try
            {
                var imageFile = e.File;
                Model.FileName = Path.GetFileNameWithoutExtension(imageFile.Name);
                Model.Extension = Path.GetExtension(imageFile.Name).TrimStart('.');
                Model.ContentType = imageFile.ContentType;
                using var stream = imageFile.OpenReadStream(Model.MaxAllowedSize);
                Model.Image = await Image.LoadAsync(stream);
                Model.Width = Model.Image.Width;
                Model.Height = Model.Image.Height;
                Model.Ratio = (double)Model.Image.Width / Model.Image.Height;
            }
            catch (Exception)
            {

            }
        }

        private async Task ResizeImage()
        {
            try
            {
                var newWidth = Model.Type == ResizeType.Percentage ? (int)(Model.Image.Width * Model.Percentage / 100.0) : Model.Width ?? 0;
                var newHeight = Model.Type == ResizeType.Percentage ? (int)(Model.Image.Height * Model.Percentage / 100.0) : Model.Height ?? 0;

                Model.Image.Mutate(x => x.Resize(newWidth, newHeight));

                using var resizedImageStream = new MemoryStream();
                Model.Image.Save(resizedImageStream, PngFormat.Instance);
                var resizedImageBytes = resizedImageStream.ToArray();

                await JSRuntime.InvokeVoidAsync("BlazorDownloadFileBlob", resizedImageBytes, Model.ContentType, $"{Model.FileName}_{newWidth}x{newHeight}.{Model.Extension}");
            }
            catch (Exception)
            {

            }          
        }

        private async Task WidthOnInput(ChangeEventArgs e)
        {
            if (int.TryParse(e.Value.ToString(), out var tmpNumber))
            {
                if (Model.KeepAspectRatio)
                {
                    Model.Height = (int)(tmpNumber / Model.Ratio);
                }
                Model.Width = tmpNumber;
            }
        }

        private async Task HeightOnInput(ChangeEventArgs e)
        {
            if (int.TryParse(e.Value.ToString(), out var tmpNumber))
            {
                if (Model.KeepAspectRatio)
                {
                    Model.Width = (int)(tmpNumber * Model.Ratio);
                }
                Model.Height = tmpNumber;
            }
        }

        internal class ResizeModel
        {
            public ResizeType Type { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public bool KeepAspectRatio { get; set; } = true;
            public int Percentage { get; set; } = 50;
            public string FileName { get; set; }
            public string Extension { get; set; }
            public string ContentType { get; set; }
            public long MaxAllowedSize { get; set; } = 50*1024*1024;
            public Image? Image { get; set; }
            public double Ratio { get; set; }
        }
    }
}
