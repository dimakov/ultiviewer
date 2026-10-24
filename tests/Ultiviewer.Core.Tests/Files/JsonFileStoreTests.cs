using Ultiviewer.Core.Settings;

namespace Ultiviewer.Core.Tests.Files;

public class JsonFileStoreTests
{
    [Fact]
    public void SessionWithUnsetWindowPositionAndSplitRoundTrips()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("session.json");
        var session = new SessionState { IsSplit = true, SplitVertical = false, SplitRatio = 0.3, FocusedGroup = 1 };
        session.SplitTabs.Add(new OpenTabState { Path = "C:\\nb\\Page.md", ViewMode = ViewMode.Live });

        JsonFileStore.Save(path, session); // Window.Left/Top are NaN until the window saves its placement
        var loaded = JsonFileStore.Load<SessionState>(path);

        Assert.True(double.IsNaN(loaded.Window.Left));
        Assert.Equal((true, false, 0.3, 1), (loaded.IsSplit, loaded.SplitVertical, loaded.SplitRatio, loaded.FocusedGroup));
        Assert.Equal(ViewMode.Live, Assert.Single(loaded.SplitTabs).ViewMode);
    }
}
