using System.Windows;
using System.Windows.Controls;
namespace NetCat.UI;
public sealed class EditorDialog : Window
{
    public StackPanel Fields { get; } = new() { Margin = new Thickness(24) };
    public Button Accept { get; } = new() { Content = "Сохранить", MinWidth = 110 };
    public TextBlock Error { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    public Func<Task<bool>>? OnAccept { get; set; }
    public EditorDialog(Window owner, string title, double width = 630)
    {
        Owner = owner; Title = title; Width = width; Height = 650; MinHeight = 300; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel(); var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 12, 24, 20) };
        var cancel = new Button { Content = "Отмена", IsCancel = true }; cancel.Click += (_, _) => Close();
        Accept.SetResourceReference(StyleProperty, "PrimaryButton"); actions.Children.Add(cancel); actions.Children.Add(Accept); DockPanel.SetDock(actions, Dock.Bottom); dock.Children.Add(actions);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Fields }; dock.Children.Add(scroll); Content = dock;
        Error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        Accept.Click += async (_, _) => { try { Accept.IsEnabled = false; if (OnAccept == null || await OnAccept()) DialogResult = true; } catch (Exception e) { Error.Text = NetCat.Engine.ProcessHost.Redact(e.Message); } finally { Accept.IsEnabled = true; } };
        Fields.Children.Add(Error);
        WindowFrame.Apply(this);
    }
    public void Note(string text) { var block = new TextBlock { Text = text }; block.SetResourceReference(StyleProperty, "Caption"); Insert(block); }
    public void Insert(UIElement element) => Fields.Children.Insert(Fields.Children.Count - 1, element);
    public void Advanced(Action addFields, bool expanded = false)
    {
        var start = Fields.Children.Count - 1;
        addFields();
        var content = new StackPanel { Margin = new Thickness(0,12,0,0) };
        while (Fields.Children.Count - 1 > start) { var child=Fields.Children[start]; Fields.Children.RemoveAt(start); content.Children.Add(child); }
        var expander=new Expander { Header="Дополнительно", IsExpanded=expanded, Content=content, Margin=new Thickness(0,18,0,0) };
        expander.SetResourceReference(Control.ForegroundProperty,"TextBrush"); Insert(expander);
    }
    public TextBox Text(string label, string value = "", bool multiline = false)
    {
        Insert(new TextBlock { Text = label }); var box = new TextBox { Text = value, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.NoWrap : TextWrapping.Wrap, MinHeight = multiline ? 140 : 0, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden };
        if (multiline) box.FontFamily = new System.Windows.Media.FontFamily("Consolas"); Insert(box); return box;
    }
    public ComboBox Choice<T>(string label, IEnumerable<T> values, T selected)
    {
        Insert(new TextBlock { Text = label }); var combo = new ComboBox { ItemsSource = values, SelectedItem = selected };
        if (typeof(T).IsEnum)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new RussianLabelConverter() });
            combo.ItemTemplate = new DataTemplate { VisualTree = text };
        }
        Insert(combo); return combo;
    }
    public CheckBox Check(string label, bool value) { var box = new CheckBox { Content = label, IsChecked = value }; Insert(box); return box; }
}
