using System.Runtime.CompilerServices;
using System;
using Moq;
using Xunit;

namespace Super.RPC.Tests;

public class ProxyObjectRegistryTests
{
    ProxyObjectRegistry registry = new ProxyObjectRegistry();
    object? testObj1 = new object();
    object? testObj2 = new object();

    [Fact]
    void GetId_ReturnsTheId() {
        registry.Register("id1", testObj1);
        registry.Register("id2", testObj2);
        Assert.Equal("id1", registry.GetId(testObj1));
        Assert.Equal("id2", registry.GetId(testObj2));
    }

    [Fact]
    void GetId_ReturnsNullIfNotFound() {
        registry.Register("id1", testObj1);
        Assert.Equal(null, registry.GetId(new object()));
    }

    [Fact]
    void Get_ReturnsTheObject() {
        registry.Register("id1", testObj1);
        registry.Register("id2", testObj2);
        Assert.Equal(testObj1, registry.Get("id1"));
        Assert.Equal(testObj2, registry.Get("id2"));
    }

    [Fact]
    void Get_ReturnsNullIfNotFound() {
        registry.Register("id1", testObj1);
        Assert.Equal(null, registry.Get("id2"));
    }

    [Fact]
    void Register_DisposeGetsCalled()
    {
        var mockDispose = new Mock<Action>();
        mockDispose.Setup(_ => _());

        RegisterNewObject("id1", mockDispose);

        GC.Collect();
        GC.WaitForPendingFinalizers();

        mockDispose.Verify(_ => _(), Times.Once);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RegisterNewObject(string id, Mock<Action> mockDispose)
    {
        var testObj = new object();
        registry.Register(id, testObj, mockDispose.Object);
    }
}