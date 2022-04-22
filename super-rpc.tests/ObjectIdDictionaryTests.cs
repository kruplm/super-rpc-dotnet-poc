using Xunit;

namespace Super.RPC.Tests;

public class ObjectIdDictionaryTests 
{
    ObjectIdDictionary<string, object, string> dictionary = new ObjectIdDictionary<string, object, string>();
    object keyObj = new object();

    [Fact]
    void Add_AddsToBothDictionaries() {
        dictionary.Add("id1", keyObj, "value1");
        Assert.NotNull(dictionary.ById["id1"]);
        Assert.NotNull(dictionary.ByObj[keyObj]);
    }

    [Fact]
    void Add_EntryContainsTheCorrectValues() {
        dictionary.Add("id1", keyObj, "value1");
        var entry = dictionary.ById["id1"];

        Assert.Equal("id1", entry.id);
        Assert.Equal(keyObj, entry.obj);
        Assert.Equal("value1", entry.value);
    }

    [Fact]
    void Add_ValuesCanBeOverridden() {
        dictionary.Add("id1", keyObj, "value1");
        dictionary.Add("id1", keyObj, "value2");
        var entry = dictionary.ByObj[keyObj];

        Assert.Equal("id1", entry.id);
        Assert.Equal(keyObj, entry.obj);
        Assert.Equal("value2", entry.value);
    }

    [Fact]
    void RemoveById_RemovesFromBothDictionaries() {
        dictionary.Add("id1", keyObj, "value1");

        dictionary.RemoveById("id1");

        Assert.False(dictionary.ById.TryGetValue("id1", out var entry));
        Assert.False(dictionary.ByObj.TryGetValue(keyObj, out var entry2));
    }
}