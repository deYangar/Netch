using System.Windows;
using System.Windows.Controls;
using Netch.Controllers;
using Netch.Utils;
using UserControl = System.Windows.Controls.UserControl;

namespace Netch.Views;

/// <summary>关于页（07§D）：纯绑定；仓库/FAQ 外链与版本直填（静态值）</summary>
public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        versionText.Text = UpdateChecker.Version;
        repoButton.Click += (_, _) => Utils.Utils.Open($"https://github.com/{UpdateChecker.Owner}/{UpdateChecker.Repo}");
        faqButton.Click += (_, _) => Utils.Utils.Open($"https://github.com/{UpdateChecker.Owner}/{UpdateChecker.Repo}/wiki");
    }
}
