using Ultiviewer.Core;

namespace Ultiviewer.Core.Tests;

public class AppInfoTests
{
    [Fact]
    public void NameIsUltiviewer() => Assert.Equal("Ultiviewer", AppInfo.Name);
}
