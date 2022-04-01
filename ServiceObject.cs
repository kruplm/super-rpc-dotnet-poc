using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SuperRPC;

public class MySerive
{
    public static int StaticCounter { get; set; }= 0;

    public static async Task<int> Mul(int a, int b) {
        return a * b;
    }

    public int Add(int a, int b) {
        return a + b;
    }

    public int Counter { get; set; } = 0;

    public int Increment() {
        Debug.WriteLine("Increment called");
        return ++Counter;
    }

    public Task<string> GetName() {
        return Task.FromResult("John");
    }

    public void LogMsgLater(Task<string> task) {
        task.ContinueWith(t => Console.WriteLine($"Task completed: {t.Result}"));
    }

    public void CallMeLater(Func<string, Task<string>> d) {
        Task.Delay(3000).ContinueWith(async t => Console.WriteLine("got back " + await d("Helloo")));
    }

    public void TakeAList(List<string> names) {
        Console.WriteLine("names:");
        foreach (var name in names) Console.WriteLine(name);
    }
}
