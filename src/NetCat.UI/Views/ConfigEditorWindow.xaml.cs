using System;
using System.Text.Json;
using System.Windows;
using ICSharpCode.AvalonEdit.Highlighting;

namespace NetCat.UI.Views
{
    public partial class ConfigEditorWindow : Window
    {
        public string ResultJson { get; private set; } = string.Empty;

        public ConfigEditorWindow(string initialJson)
        {
            InitializeComponent();
            JsonEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("JavaScript");
            JsonEditor.Text = initialJson;
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            string raw = JsonEditor.Text;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                ResultJson = raw;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                StatusMessage.Text = $"Ошибка JSON: {ex.Message}";
                StatusMessage.Foreground = System.Windows.Media.Brushes.IndianRed;
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
