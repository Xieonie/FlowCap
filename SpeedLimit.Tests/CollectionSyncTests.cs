using System.Collections.ObjectModel;
using SpeedLimit;

public class CollectionSyncTests
{
    [Theory]
    [InlineData("abcd", "dcba")]
    [InlineData("abcd", "bd")]
    [InlineData("ab", "xaby")]
    [InlineData("", "abc")]
    [InlineData("abc", "")]
    [InlineData("abcde", "eaxc")]
    public void SyncTo_ProducesDesiredOrder(string start, string desired)
    {
        var target = new ObservableCollection<char>(start);
        CollectionSync.SyncTo(target, desired.ToList());
        Assert.Equal(desired, new string(target.ToArray()));
    }

    [Fact]
    public void SyncTo_UnchangedListRaisesNoEvents()
    {
        var target = new ObservableCollection<char>("abc");
        var events = 0;
        target.CollectionChanged += (_, _) => events++;
        CollectionSync.SyncTo(target, "abc".ToList());
        Assert.Equal(0, events);
    }
}
