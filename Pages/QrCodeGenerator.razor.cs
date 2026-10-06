using Microsoft.AspNetCore.Components;
using ToolsServer.Shared;
using static ToolsServer.Enums;
using Microsoft.JSInterop;
using QRCoder;
using System.Text;
using FilesClient;
using Microsoft.AspNetCore.Components.Forms;
using MimeKit;
using SixLabors.ImageSharp;

namespace ToolsServer.Pages
{
    public partial class QrCodeGenerator
    {
        [CascadingParameter]
        public MainLayout Layout { get; set; }
        [Inject]
        public FilesClientHandler FilesClientHandler { get; set; }
        [Inject]
        public IJSRuntime JSRuntime { get; set; }
        [Inject]
        public IConfiguration Configuration { get; set; }
        private string customText1 = string.Empty;
        private string customText2 = string.Empty;
        private string customText3 = string.Empty;
        private string customText4 = string.Empty;
        private string customText5 = string.Empty;
        private string customText6 = string.Empty;
        private string customText7 = string.Empty;
        private string customText8 = string.Empty;
        private string customText9 = string.Empty;
        private string customText10 = string.Empty;
        private bool isLoading;
        private bool customCheck1;
        private byte[]? data;
        private QrCodeType qrCodeType;
        private WifiEncryption wifiEncryption = WifiEncryption.WPA;
        private string errorMessage;
        private long maxAllowedSize = 20*1024*1024;

        protected override void OnInitialized()
        {
            base.OnInitialized();
            Layout.Title ="QR Code Generator";
        }

        private async Task GenerateButtonClicked()
        {
            isLoading = true;
            if (qrCodeType == QrCodeType.LINK || qrCodeType == QrCodeType.TEXT)
            {
                await GenerateQRCode(customText1);
            }
            else if (qrCodeType == QrCodeType.WIFI)
            {
                var wifiData = $"WIFI:S:{customText1};T:{wifiEncryption};P:{customText2};H:{customCheck1};;";
                await GenerateQRCode(wifiData);
            }
            else if (qrCodeType == QrCodeType.FILE)
            {
                if (!string.IsNullOrEmpty(customText1))
                {
                    await GenerateQRCode(customText1);
                }
            }
            else if (qrCodeType == QrCodeType.PROFILE)
            {
                if (string.IsNullOrEmpty(customText1))
                {
                    errorMessage = "Name is Required";
                    isLoading = false;
                    return;
                }
                var htmlTemplate = $"<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"UTF-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{customText1}'s Profile</title><link rel=\"stylesheet\" href=\"https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.0.0-beta3/css/all.min.css\"><style>body{{font-family:Arial,sans-serif;background-color:#f4f4f4;margin:0;padding:0}}.header{{background-color:#333;color:#fff;text-align:center;padding:20px}}.profile-container{{max-width:600px;margin:20px auto;background-color:#fff;border-radius:5px;box-shadow:0 0 10px rgba(0,0,0,.2);padding:20px;display:grid;gap:20px}}.section{{font-weight:700;font-size:1.2em;border-bottom:1px solid #ccc;padding-bottom:10px}}.profile-field{{display:grid;grid-template-columns:1fr 2fr;align-items:center;gap:10px}}.profile-label{{color:#555}}.profile-value{{color:#333}}</style></head><body><div class=\"header\"><h1>{customText1}'s Profile</h1></div><div class=\"profile-container\"><div class=\"section\">Basic Information</div><div class=\"profile-field\"><span class=\"profile-label\"><i class=\"fas fa-user\"></i> Name:</span><span class=\"profile-value\">{customText1}</span></div><div class=\"profile-field\"><span class=\"profile-label\"><i class=\"fas fa-phone\"></i> Phone:</span><span class=\"profile-value\">{customText2}</span></div><div class=\"profile-field\"><span class=\"profile-label\"><i class=\"fas fa-envelope\"></i> Email:</span><span class=\"profile-value\"><a href=\"mailto:{customText3}\">{customText3}</a></span></div><div class=\"section\">Address Information</div><div class=\"profile-field\"><span class=\"profile-label\"><i class=\"fas fa-home\"></i> Address:</span><span class=\"profile-value\">{customText4}</span></div><div class=\"profile-field\"><span class=\"profile-label\"><i class=\"fas fa-city\"></i> City:</span><span class=\"profile-value\">{customText5}</span></div><div class=\"profile-field\"><span class=\"profile-label\"><i class=\"fas fa-mail-bulk\"></i> Post Code:</span><span class=\"profile-value\">{customText6}</span></div><div class=\"profile-field\"><span class=\"profile-label\"><i class=\"fas fa-globe-americas\"></i> Country:</span><span class=\"profile-value\">{customText7}</span></div><div class=\"section\">Professional Information</div><div class=\"profile-field\"><span class=\"profile-label\"><i class=\"fas fa-building\"></i> Company:</span><span class=\"profile-value\">{customText8}</span></div><div class=\"profile-field\"><span class=\"profile-label\"><i class=\"fas fa-briefcase\"></i> Job Title:</span><span class=\"profile-value\">{customText9}</span></div><div class=\"profile-field\"><span class=\"profile-label\"><i class=\"fas fa-globe\"></i> Website:</span><span class=\"profile-value\"><a href=\"{customText10}\">{customText10}</a></span></div></div></body></html>";
                var bytes = Encoding.UTF8.GetBytes(htmlTemplate);
                using var stream = new MemoryStream(bytes);
                var uploadResult = await FilesClientHandler.UploadFileAsync(stream, customText1.Trim() + ".html", "text/html", true);
                if (uploadResult.Item1)
                {
                    if (!string.IsNullOrEmpty(uploadResult.Item2))
                    {
                        await GenerateQRCode(uploadResult.Item2);
                    }
                }
                else
                {
                    isLoading = false;
                    errorMessage = uploadResult.Item2;
                }
            }
            else if (qrCodeType == QrCodeType.VCARD)
            {
                customText5 = $"BEGIN:VCARD\nVERSION:3.0\nFN:{customText1}\nTEL:{customText2}\nEMAIL:{customText3}\nORG:{customText4}\nEND:VCARD";
                await GenerateQRCode(customText5);
            }
            isLoading = false;
        }

        //private async Task HandleFileUpload()
        //{
        //    isLoading = true;
        //    await JSRuntime.InvokeVoidAsync("UploadFileFormWithPromise", Configuration["FileService:Token"], Configuration["Host"], maxAllowedSize);
        //}

        private async Task HandleFileUpload(InputFileChangeEventArgs e)
        {
            try
            {
                isLoading = true;
                var userFileName = e.File.Name;
                using var stream = e.File.OpenReadStream(maxAllowedSize);
                var contentType = MimeTypes.GetMimeType(Path.GetExtension(userFileName));
                var uploadResult = await FilesClientHandler.UploadFileAsync(stream, userFileName, contentType, true);
                if (uploadResult.Item1)
                {
                    if (!string.IsNullOrEmpty(uploadResult.Item2))
                    {
                        await GenerateQRCode(uploadResult.Item2);
                    }
                }
                else
                {
                    isLoading = false;
                    errorMessage = uploadResult.Item2;
                }
            }
            catch (Exception ex)
            {
                isLoading = false;
                errorMessage = $"An error occurred: {ex.Message}";
            }
        }

        private async Task DownloadButtonClicked()
        {
            await JSRuntime.InvokeVoidAsync("BlazorDownloadFileBlob", CancellationToken.None, data, "image/png", "QrCode.png");
        }

        private void ResetFields()
        {
            customText1 = string.Empty;
            customText2 = string.Empty;
            customText3 = string.Empty;
            customText4 = string.Empty;
            customText5 = string.Empty;
            customText6 = string.Empty;
            customText7 = string.Empty;
            customText8 = string.Empty;
            customText9 = string.Empty;
            customText10 = string.Empty;
            customCheck1 = false;
            data = null;
        }

        private async Task GenerateQRCode(string content)
        {
            try
            {
                isLoading = true;
                errorMessage = string.Empty;
                data = null;
                using var qrGenerator = new QRCodeGenerator();
                using var qrCodeData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.L, requestedVersion:-1);
                using var qrCode = new QRCode(qrCodeData);
                using var qrCodeImage = qrCode.GetGraphic(100);
                using var stream = new MemoryStream();
                await qrCodeImage.SaveAsPngAsync(stream);
                data = stream.ToArray();
                isLoading = false;
            }
            catch (Exception ex)
            {
                errorMessage = $"An error occurred: {ex.Message}";
                isLoading = false;
            }
        }
    }
}
