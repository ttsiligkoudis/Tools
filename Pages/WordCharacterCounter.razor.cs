using Microsoft.AspNetCore.Components;
using ToolsServer.Shared;

namespace ToolsServer.Pages
{
    public partial class WordCharacterCounter
    {
        [CascadingParameter]
        public MainLayout Layout { get; set; }

        private string inputText = "";
        private int WordCount => inputText.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        private int CharacterCount => inputText.Length;

        protected override void OnInitialized()
        {
            base.OnInitialized();
            Layout.Title = "Word/Character Counter";
        }
    }
}
