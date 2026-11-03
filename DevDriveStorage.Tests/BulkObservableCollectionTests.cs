using System.Collections.Specialized;
using DevDriveStorage;

namespace DevDriveStorage.Tests;

[TestClass]
public sealed class BulkObservableCollectionTests
{
    [TestMethod]
    public void ReplaceAllRaisesASingleResetInsteadOfOneEventPerItem()
    {
        BulkObservableCollection<int> collection = [1, 2, 3];
        List<NotifyCollectionChangedAction> actions = [];
        collection.CollectionChanged += (_, args) => actions.Add(args.Action);

        collection.ReplaceAll(Enumerable.Range(0, 500));

        Assert.HasCount(1, actions);
        Assert.AreEqual(NotifyCollectionChangedAction.Reset, actions[0]);
        Assert.HasCount(500, collection);
        Assert.AreEqual(0, collection[0]);
        Assert.AreEqual(499, collection[499]);
    }

    [TestMethod]
    public void ReplaceAllWithNoItemsEmptiesTheCollection()
    {
        BulkObservableCollection<string> collection = ["a", "b"];

        collection.ReplaceAll([]);

        Assert.IsEmpty(collection);
    }

    [TestMethod]
    public void NormalMutationsStillNotifyIndividually()
    {
        BulkObservableCollection<int> collection = [];
        List<NotifyCollectionChangedAction> actions = [];
        collection.CollectionChanged += (_, args) => actions.Add(args.Action);

        collection.Add(1);
        collection.Remove(1);

        Assert.HasCount(2, actions);
        Assert.AreEqual(NotifyCollectionChangedAction.Add, actions[0]);
        Assert.AreEqual(NotifyCollectionChangedAction.Remove, actions[1]);
    }
}
