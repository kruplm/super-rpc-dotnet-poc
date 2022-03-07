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
}
