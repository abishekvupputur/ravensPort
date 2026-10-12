using Avalonia.Automation;
using Avalonia.Controls;
using RavensPort.Helpers;
using Button = Avalonia.Controls.Button;
using ToolTip = Avalonia.Controls.ToolTip;

namespace RavensPort.Views;

public partial class ProjectLinksView : UserControl
{
    public ProjectLinksView()
    {
        InitializeComponent();

        foreach (var (label, uri) in ProjectLinks.All)
        {
            var button = new Button { Content = label, Margin = new(0, 0, 8, 6) };
            button.Classes.Add("ghost");
            ToolTip.SetTip(button, uri);
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => ProjectLinks.Open(TopLevel.GetTopLevel(this), uri);
            Links.Children.Add(button);
        }
    }
}
