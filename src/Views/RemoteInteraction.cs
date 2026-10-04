using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SourceGit.Contracts;

namespace SourceGit.Views
{
    public sealed class RemoteInteraction : Window
    {
        public RemoteInteraction(InteractionRequest request)
        {
            var editor = request.Kind is "editor" or "sequenceEditor";
            Title = editor ? "Remote Git editor" : "Remote Git authentication";
            Width = editor ? 760 : 520; Height = editor ? 500 : 230;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var input = new TextBox { Text = request.Content ?? "", AcceptsReturn = editor, MinHeight = editor ? 300 : 32, PasswordChar = editor ? '\0' : '●' };
            var accept = new Button { Content = "Continue" };
            var cancel = new Button { Content = "Cancel" };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            buttons.Children.Add(cancel); buttons.Children.Add(accept);
            var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = request.Prompt, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            panel.Children.Add(input); panel.Children.Add(buttons); Content = panel;
            accept.Click += (_, _) => { var value = input.Text ?? ""; input.Text = ""; Close(new InteractionReply(request.Id, true, value)); };
            cancel.Click += (_, _) => { input.Text = ""; Close(new InteractionReply(request.Id, false, "")); };
        }
    }
}
