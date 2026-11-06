using System.Xml.Linq;
using Xunit;

namespace SentinelCore.Tests;

public sealed class U1NavigationContractTests
{
    [Fact]
    public void EveryNavigationEntryMatchesItsPageAndDashboardTarget()
    {
        var document = XDocument.Load(TestRepository.Combine("src", "SentinelCore.App", "MainWindow.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var navigation = document.Descendants().Single(e => (string?)e.Attribute(xaml + "Name") == "NavList");
        var pages = document.Descendants().Single(e => (string?)e.Attribute(xaml + "Name") == "Pages");
        var labels = navigation.Elements().Select(e => (string?)e.Attribute("Content")).ToArray();
        Assert.Equal(13, labels.Length);
        Assert.Equal(labels, pages.Elements().Select(e => (string?)e.Attribute("Header")));
        foreach (var button in document.Descendants().Where(e => (string?)e.Attribute("Click") == "OnNavigate"))
        {
            var target = int.Parse(button.Attribute("Tag")!.Value);
            Assert.InRange(target, 0, labels.Length - 1);
            Assert.Equal("Abrir " + labels[target], (string?)button.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml/presentation}AutomationProperties.Name")
                ?? (string?)button.Attribute("AutomationProperties.Name"));
        }
    }
}
