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
        ++Counter;
        CounterChanged?.Invoke(this, Counter);
        return Counter;
    }

    public Task<string> GetName() {
        return Task.FromResult("John");
    }

    public void LogMsgLater(Task<string> task) {
        task.ContinueWith(t => Console.WriteLine($"Task completed: {t.Result}"));
    }

    public void CallMeLater(Action<string> callback) {
        Task.Delay(3000).ContinueWith(t => {
            callback("Helloo");
        });
    }

    public void TakeAList(List<string> names) {
        Console.WriteLine("names:");
        foreach (var name in names) Console.WriteLine(name);
    }

    public void TakeArray(int[] nums) {
        Console.WriteLine("names:");
        foreach (var num in nums) Console.WriteLine(num);
    }

    public void TakeADictionary(Dictionary<string, string> dict) {
        foreach (var (name, value) in dict) Console.WriteLine(name + " -> " + value);
    }

    public event EventHandler<int> CounterChanged;
}
