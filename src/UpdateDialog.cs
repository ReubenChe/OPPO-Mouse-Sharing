using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Xml;

internal static class UpdateDialog
{
    internal static Window Create(string title, string message, bool confirm)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var main = new XmlDocument();
        var dialog = new XmlDocument();
        using (var stream = assembly.GetManifestResourceStream("update.main.xaml")) main.Load(stream);
        using (var stream = assembly.GetManifestResourceStream("update.dialog.xaml")) dialog.Load(stream);
        const string ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = dialog.CreateElement("Window.Resources", ns);
        foreach (XmlNode style in main.DocumentElement.SelectNodes("*[local-name()='Window.Resources']/*"))
            resources.AppendChild(dialog.ImportNode(style, true));
        dialog.DocumentElement.PrependChild(resources);
        var window = (Window)XamlReader.Load(new XmlNodeReader(dialog));
        window.Title = title;
        ((TextBlock)window.FindName("DialogTitle")).Text = title;
        ((TextBlock)window.FindName("DialogMessage")).Text = message;
        ((TextBlock)window.FindName("DialogHint")).Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        var cancel = (Button)window.FindName("CancelChoice");
        var accept = (Button)window.FindName("ConfirmChoice");
        cancel.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        accept.Content = confirm ? "下载并安装" : "确定";
        if (!confirm) Grid.SetColumnSpan(accept, 3);
        if (!confirm) Grid.SetColumn(accept, 0);
        cancel.Click += (s, e) => { window.DialogResult = false; };
        ((Button)window.FindName("CloseX")).Click += (s, e) => { window.DialogResult = false; };
        accept.Click += (s, e) => { window.DialogResult = true; };
        window.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) window.DragMove(); };
        using (var stream = assembly.GetManifestResourceStream("update.icon")) {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            ((Image)window.FindName("DialogBrandMark")).Source = decoder.Frames[decoder.Frames.Count - 1];
        }
        return window;
    }
    internal static bool Show(string title, string message, bool confirm)
    {
        return Create(title, message, confirm).ShowDialog() == true;
    }
}
